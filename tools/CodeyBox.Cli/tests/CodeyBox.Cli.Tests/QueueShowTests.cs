using System.Net;
using System.Text.Json;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class QueueShowTests
{
    private static Func<ResolvedConfig, CodeyBoxClient> MakeFactory(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(handler))
            { BaseAddress = new Uri(config.ApiBaseUrl) });
    }

    [Fact]
    public async Task Show_HumanReadable_PrintsAllFields()
    {
        var item = SampleData.WorkItem("Working");
        item.LastError = "previous error";
        var factory = MakeFactory(_ => SampleData.WorkItemResponse(item));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "show", item.Id],
                factory);

            Assert.Equal(0, code);
            var stdout = output.Out.ToString();
            Assert.Contains(item.Id, stdout);
            Assert.Contains("Working", stdout);
            Assert.Contains(item.ProjectId, stdout);
            Assert.Contains(item.Title, stdout);
            Assert.Contains(item.Agent, stdout);
            Assert.Contains("previous error", stdout);
            Assert.Contains(item.Prompt, stdout);
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Show_Json_PrintsRawJson()
    {
        var item = SampleData.WorkItem("Done");
        var factory = MakeFactory(_ => SampleData.WorkItemResponse(item));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "show", item.Id, "--json"],
                factory);

            Assert.Equal(0, code);
            var stdout = output.Out.ToString().Trim();
            Assert.StartsWith("{", stdout);
            var parsed = JsonSerializer.Deserialize(stdout, CliJsonContext.Default.WorkItemDto);
            Assert.NotNull(parsed);
            Assert.Equal(item.Id, parsed.Id);
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Show_NotFound_WritesToStderrNonZeroExit()
    {
        var factory = MakeFactory(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "show", "nonexistent-id"],
                factory);

            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("not found", output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Show_Unauthorized_WritesToStderrNonZeroExit()
    {
        var factory = MakeFactory(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("Unauthorized"),
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "show", "some-id"],
                factory);

            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("401", output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }
}
