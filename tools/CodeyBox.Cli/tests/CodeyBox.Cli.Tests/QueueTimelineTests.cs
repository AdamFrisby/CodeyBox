using System.Net;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class QueueTimelineTests
{
    private const string Id = "aabbccdd-0000-0000-0000-000000000000";
    private const char Esc = '\u001b';

    private static Func<ResolvedConfig, CodeyBoxClient> MakeFactory(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(handler))
            { BaseAddress = new Uri(config.ApiBaseUrl) });
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task Timeline_HumanReadable_GetsEndpointAndPrintsTable()
    {
        const string body = """
{
  "workItemId": "aabbccdd-0000-0000-0000-000000000000",
  "entries": [
    { "occurredAt": "2026-01-01T00:00:00Z", "kind": "state_change", "summary": "Queued -> Working", "details": {} },
    { "occurredAt": "2026-01-01T00:05:00Z", "kind": "auditor_run", "summary": "security passed", "details": { "iteration": 1 } }
  ]
}
""";
        HttpRequestMessage? captured = null;
        var factory = MakeFactory(req => { captured = req; return JsonResponse(body); });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "timeline", Id], factory);

            Assert.Equal(0, code);
            Assert.Equal(HttpMethod.Get, captured!.Method);
            Assert.EndsWith($"/workitems/{Id}/timeline", captured.RequestUri!.ToString());
            var stdout = output.Out.ToString();
            Assert.Contains($"Timeline for {Id}", stdout);
            Assert.Contains("state_change", stdout);
            Assert.Contains("Queued -> Working", stdout);
            Assert.Contains("auditor_run", stdout);
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Timeline_EmptyEntries_PrintsPlaceholder()
    {
        const string body = """{ "workItemId": "aabbccdd-0000-0000-0000-000000000000", "entries": [] }""";
        var factory = MakeFactory(_ => JsonResponse(body));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "timeline", Id], factory);

            Assert.Equal(0, code);
            Assert.Contains("(no timeline entries)", output.Out.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Timeline_Json_PrintsRawResponse()
    {
        const string body = """{"workItemId":"x","entries":[]}""";
        var factory = MakeFactory(_ => JsonResponse(body));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "timeline", Id, "--json"], factory);

            Assert.Equal(0, code);
            Assert.Equal(body, output.Out.ToString().Trim());
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Timeline_ControlCharsInSummary_AreStripped()
    {
        // Untrusted summary carrying an ANSI escape (JSON \u001b) must not reach the terminal.
        const string body =
            "{\"workItemId\":\"x\",\"entries\":[" +
            "{\"occurredAt\":\"t\",\"kind\":\"note\",\"summary\":\"hi\\u001b[31mRED\",\"details\":{}}]}";
        var factory = MakeFactory(_ => JsonResponse(body));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "timeline", Id], factory);

            Assert.Equal(0, code);
            var stdout = output.Out.ToString();
            Assert.DoesNotContain(Esc, stdout);
            Assert.Contains("hi[31mRED", stdout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }
}
