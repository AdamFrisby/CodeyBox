using System.Net;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class QuotaCommandTests
{
    private static Func<ResolvedConfig, CodeyBoxClient> MakeFactory(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(handler))
            { BaseAddress = new Uri(config.ApiBaseUrl) });
    }

    [Fact]
    public async Task Quota_HumanReadable_GetsQuotaAndPrintsTables()
    {
        const string responseBody = """
{
  "generatedAt": "2026-01-01T00:00:00Z",
  "minQuotaPct": 10,
  "unknownPolicy": "UseObservedFailures",
  "observedFailureWindowMinutes": 60,
  "probes": [
    {
      "agent": "claude",
      "latestSnapshot": {
        "availablePct": 60,
        "resetAt": "2026-01-01T06:00:00Z",
        "notes": "ok",
        "perModel": {
          "claude-opus": { "availablePct": 0, "window": "weekly" }
        }
      },
      "observedFailuresLast60m": [
        { "projectId": "proj", "modelId": "claude-opus", "failureKind": "LimitReached", "count": 1 }
      ],
      "wouldAllow": true,
      "defaultModelWouldAllow": true,
      "perModelWouldAllow": { "claude-opus": false }
    }
  ],
  "budgets": [
    {
      "agent": "opencode",
      "model": "opencode-go/deepseek-v4-pro",
      "windows": [
        { "kind": "Rolling", "hours": 5, "percentRemaining": 92, "resetAt": null }
      ]
    }
  ],
  "budgetsError": false,
  "observedFailuresLast60m": []
}
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
            var code = await CliApp.InvokeAsync(["quota"], factory);

            Assert.Equal(0, code);
            Assert.NotNull(captured);
            Assert.Equal(HttpMethod.Get, captured.Method);
            Assert.EndsWith("/quota", captured.RequestUri!.ToString());
            var stdout = output.Out.ToString();
            Assert.Contains("Min quota", stdout);
            Assert.Contains("claude", stdout);
            Assert.Contains("60%", stdout);
            Assert.Contains("opencode", stdout);
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Quota_Json_PrintsRawResponse()
    {
        const string responseBody = """{"probes":[],"budgets":[],"budgetsError":false}""";
        var factory = MakeFactory(_ => JsonResponse(responseBody));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["quota", "--json"], factory);

            Assert.Equal(0, code);
            Assert.Equal(responseBody, output.Out.ToString().Trim());
            Assert.Empty(output.Error.ToString());
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
