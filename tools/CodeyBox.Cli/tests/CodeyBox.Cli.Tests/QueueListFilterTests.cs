using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class QueueListFilterTests
{
    private static Func<ResolvedConfig, CodeyBoxClient> MakeFactory(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(handler))
            { BaseAddress = new Uri(config.ApiBaseUrl) });
    }

    [Fact]
    public async Task List_StateFilter_PassesQueryString()
    {
        string? requestUrl = null;
        var factory = MakeFactory(req =>
        {
            requestUrl = req.RequestUri!.ToString();
            return SampleData.WorkItemListResponse([]);
        });

        using var output = new TestOutput();
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "ls", "--state", "Working,Auditing"], factory);

            Assert.Equal(0, code);
            Assert.NotNull(requestUrl);
            Assert.Contains("state=Working%2CAuditing", requestUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task List_ProjectFilter_PassesQueryString()
    {
        string? requestUrl = null;
        var factory = MakeFactory(req =>
        {
            requestUrl = req.RequestUri!.ToString();
            return SampleData.WorkItemListResponse([]);
        });

        using var output = new TestOutput();
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "ls", "--project", "myapp"], factory);

            Assert.Equal(0, code);
            Assert.Contains("project=myapp", requestUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task List_Quiet_OutputsIdsOnly()
    {
        var item1 = SampleData.WorkItem("Working");
        item1.Id = "aaaa-0000";
        var item2 = SampleData.WorkItem("Queued");
        item2.Id = "bbbb-1111";
        var items = new[] { item1, item2 };
        var factory = MakeFactory(_ => SampleData.WorkItemListResponse(items));

        using var output = new TestOutput();
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "ls", "--quiet"], factory);

            Assert.Equal(0, code);
            var lines = output.Out.ToString().Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            Assert.Equal("aaaa-0000", lines[0].Trim());
            Assert.Equal("bbbb-1111", lines[1].Trim());
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task List_Json_OutputsRawJson()
    {
        var factory = MakeFactory(_ => SampleData.WorkItemListResponse([]));

        using var output = new TestOutput();
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "ls", "--json"], factory);

            Assert.Equal(0, code);
            var stdout = output.Out.ToString().Trim();
            Assert.StartsWith("[", stdout);
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task List_LimitFilter_PassesQueryString()
    {
        string? requestUrl = null;
        var factory = MakeFactory(req =>
        {
            requestUrl = req.RequestUri!.ToString();
            return SampleData.WorkItemListResponse([]);
        });

        using var output = new TestOutput();
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "ls", "--limit", "5"], factory);

            Assert.Equal(0, code);
            Assert.Contains("limit=5", requestUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }
}
