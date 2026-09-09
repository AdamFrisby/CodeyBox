using System.Net;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class QueueCostsTests
{
    private const string Id = "aabbccdd-0000-0000-0000-000000000000";

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

    private const string SampleBody = """
{
  "workItemId": "aabbccdd-0000-0000-0000-000000000000",
  "totals": {
    "inputTokens": 1500,
    "cachedInputTokens": 200,
    "outputTokens": 400,
    "estimatedUsd": 0.42,
    "elapsedMs": 12000,
    "invocationCount": 3
  },
  "byPhase": {
    "Work": { "inputTokens": 1000, "outputTokens": 300, "estimatedUsd": 0.3, "invocationCount": 2, "byIteration": [] },
    "Audit": { "inputTokens": 500, "outputTokens": 100, "estimatedUsd": 0.12, "invocationCount": 1, "byIteration": [] }
  },
  "byAgent": [
    { "agent": "claude", "modelId": "claude-opus", "inputTokens": 1500, "outputTokens": 400, "estimatedUsd": 0.42, "invocationCount": 3 }
  ]
}
""";

    [Fact]
    public async Task Costs_HumanReadable_GetsEndpointAndPrintsBreakdown()
    {
        HttpRequestMessage? captured = null;
        var factory = MakeFactory(req => { captured = req; return JsonResponse(SampleBody); });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "costs", Id], factory);

            Assert.Equal(0, code);
            Assert.Equal(HttpMethod.Get, captured!.Method);
            Assert.EndsWith($"/workitems/{Id}/costs", captured.RequestUri!.ToString());
            var stdout = output.Out.ToString();
            Assert.Contains($"Costs for {Id}", stdout);
            Assert.Contains("Input tokens", stdout);
            Assert.Contains("1500", stdout);
            Assert.Contains("By phase", stdout);
            Assert.Contains("Work", stdout);
            Assert.Contains("Audit", stdout);
            Assert.Contains("By agent", stdout);
            Assert.Contains("claude-opus", stdout);
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Costs_Json_PrintsRawResponse()
    {
        const string body = """{"workItemId":"x","totals":{},"byPhase":{},"byAgent":[]}""";
        var factory = MakeFactory(_ => JsonResponse(body));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "costs", Id, "--json"], factory);

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
    public async Task Costs_NonObjectResponse_WritesParsingError()
    {
        var factory = MakeFactory(_ => JsonResponse("[]"));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "costs", Id], factory);

            Assert.NotEqual(0, code);
            Assert.Contains("Error parsing response", output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }
}
