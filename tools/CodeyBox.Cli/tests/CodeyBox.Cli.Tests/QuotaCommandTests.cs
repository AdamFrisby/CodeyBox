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
            Assert.Matches(
                @"(?m)^claude\s+60%\s+2026-01-01T06:00:00Z\s+true\s+true\s+1\s+1\s+ok",
                stdout);
            Assert.Contains("opencode", stdout);
            Assert.Contains("opencode-go/deepseek-v4-pro", stdout);
            Assert.Contains("Rolling", stdout);
            Assert.Contains("92%", stdout);
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
            var code = await CliApp.InvokeAsync(["quota", "--json"], factory);

            Assert.Equal(0, code);
            Assert.NotNull(captured);
            Assert.Equal(HttpMethod.Get, captured.Method);
            Assert.EndsWith("/quota", captured.RequestUri!.ToString());
            Assert.Equal(responseBody, output.Out.ToString().Trim());
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Quota_HumanReadable_NonObjectResponse_WritesParsingError()
    {
        var factory = MakeFactory(_ => JsonResponse("""[]"""));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["quota"], factory);

            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("Error parsing response", output.Error.ToString());
            Assert.Contains("Expected top-level JSON object", output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Quota_HumanReadable_MissingObservedFailureWindow_DoesNotPrintOrphanSuffix()
    {
        const string responseBody = """
{
  "generatedAt": "2026-01-01T00:00:00Z",
  "minQuotaPct": 10,
  "unknownPolicy": "UseObservedFailures",
  "probes": [],
  "budgets": [],
  "budgetsError": false
}
""";
        var factory = MakeFactory(_ => JsonResponse(responseBody));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["quota"], factory);

            Assert.Equal(0, code);
            var observedLine = output.Out.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Single(line => line.StartsWith("Observed failure window", StringComparison.Ordinal));
            Assert.Equal("Observed failure window", observedLine.TrimEnd());
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
