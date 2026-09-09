using System.Net;
using System.Net.Http.Json;
using CodeyBox.Cli.Models;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class QueueDepsTests
{
    private const string Id = "aabbccdd-0000-0000-0000-000000000000";

    private static Func<ResolvedConfig, CodeyBoxClient> MakeFactory(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(handler))
            { BaseAddress = new Uri(config.ApiBaseUrl) });
    }

    private static HttpResponseMessage ListResponse(IEnumerable<WorkItemDto> items) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(items.ToList(), CliJsonContext.Default.ListWorkItemDto),
        };

    [Fact]
    public async Task Deps_HumanReadable_GetsDependentsAndPrintsTable()
    {
        var dependent = SampleData.WorkItem("Queued");
        dependent.Id = "ffffffff-0000-0000-0000-000000000000";
        dependent.Title = "Downstream item";
        HttpRequestMessage? captured = null;
        var factory = MakeFactory(req => { captured = req; return ListResponse([dependent]); });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "deps", Id], factory);

            Assert.Equal(0, code);
            Assert.Equal(HttpMethod.Get, captured!.Method);
            Assert.EndsWith($"/workitems/{Id}/dependents", captured.RequestUri!.ToString());
            var stdout = output.Out.ToString();
            // Table renders a truncated short id (first 8 chars, then ellipsis).
            Assert.Contains("ffffff", stdout);
            Assert.Contains("Downstream item", stdout);
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Deps_NoDependents_PrintsPlaceholder()
    {
        var factory = MakeFactory(_ => ListResponse([]));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "deps", Id], factory);

            Assert.Equal(0, code);
            Assert.Contains("No dependents.", output.Out.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Deps_Quiet_PrintsOnlyIds()
    {
        var a = SampleData.WorkItem();
        a.Id = "11111111-0000-0000-0000-000000000000";
        var b = SampleData.WorkItem();
        b.Id = "22222222-0000-0000-0000-000000000000";
        var factory = MakeFactory(_ => ListResponse([a, b]));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "deps", Id, "--quiet"], factory);

            Assert.Equal(0, code);
            var lines = output.Out.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            Assert.Equal(a.Id, lines[0].Trim());
            Assert.Equal(b.Id, lines[1].Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Deps_Json_PrintsSerializedArray()
    {
        var dependent = SampleData.WorkItem("Working");
        var factory = MakeFactory(_ => ListResponse([dependent]));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "deps", Id, "--json"], factory);

            Assert.Equal(0, code);
            var stdout = output.Out.ToString();
            using var doc = System.Text.Json.JsonDocument.Parse(stdout);
            Assert.Equal(1, doc.RootElement.GetArrayLength());
            Assert.Equal(dependent.Id, doc.RootElement[0].GetProperty("id").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }
}
