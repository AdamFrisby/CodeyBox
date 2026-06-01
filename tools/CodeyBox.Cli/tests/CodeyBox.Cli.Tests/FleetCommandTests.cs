using System.Net;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class FleetCommandTests
{
    private static Func<ResolvedConfig, CodeyBoxClient> MakeFactory(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(handler))
            { BaseAddress = new Uri(config.ApiBaseUrl) });
    }

    [Fact]
    public async Task Fleet_HumanReadable_GetsSummaryAndPrintsTable()
    {
        const string responseBody = """
[
  {
    "projectId": "proj-alpha",
    "displayName": "Alpha Project",
    "queuedCount": 2,
    "inFlightCount": 1,
    "currentPhase": "Working",
    "recentOutcomes": ["Done", "Failed"],
    "isPaused": false,
    "hasRecentFailures": true,
    "pausedReason": null,
    "monthlySpendUsd": 12.34,
    "monthlyBudgetUsd": null,
    "budgetThresholdState": "ok"
  }
]
""";

        HttpRequestMessage? captured = null;
        var factory = MakeFactory(req =>
        {
            captured = req;
            return JsonResponse(responseBody);
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["fleet"], factory);

            Assert.Equal(0, code);
            Assert.NotNull(captured);
            Assert.Equal(HttpMethod.Get, captured.Method);
            Assert.EndsWith("/fleet/summary", captured.RequestUri!.ToString());
            var stdout = output.Out.ToString();
            Assert.Contains("PROJECT", stdout);
            Assert.Contains("proj-alpha", stdout);
            Assert.Contains("Alpha Project", stdout);
            Assert.Contains("Working", stdout);
            Assert.Contains("false", stdout);
            Assert.Contains("true", stdout);
            Assert.Contains("12.34", stdout);
            Assert.Contains("BUDGET_STATE", stdout);
            Assert.Contains("ok", stdout);
            Assert.Contains("Done,Failed", stdout);
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Fleet_Json_PrintsRawResponse()
    {
        const string responseBody = """[{"projectId":"proj-alpha"}]""";
        HttpRequestMessage? captured = null;
        var factory = MakeFactory(req =>
        {
            captured = req;
            return JsonResponse(responseBody);
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["fleet", "--json"], factory);

            Assert.Equal(0, code);
            Assert.NotNull(captured);
            Assert.Equal(HttpMethod.Get, captured.Method);
            Assert.EndsWith("/fleet/summary", captured.RequestUri!.ToString());
            Assert.Equal(responseBody, output.Out.ToString().Trim());
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Fleet_HumanReadable_NonArrayResponse_WritesParsingError()
    {
        var factory = MakeFactory(_ => JsonResponse("""{"projectId":"proj-alpha"}"""));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["fleet"], factory);

            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("Error parsing response", output.Error.ToString());
            Assert.Contains("Expected top-level JSON array", output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };
}
