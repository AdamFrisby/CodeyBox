using System.Net;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class AuditSoundnessCommandTests
{
    private static Func<ResolvedConfig, CodeyBoxClient> MakeFactory(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(handler))
            { BaseAddress = new Uri(config.ApiBaseUrl) });
    }

    private const string ResponseBody = """
{
  "windowSize": 100,
  "evaluatedCount": 2,
  "selectorFilter": null,
  "unsafeSkipCount": 1,
  "safeCount": 1,
  "fullSuiteCount": 0,
  "unverifiableCount": 0,
  "totalTestsSaved": 3,
  "estimatedSavedMs": 50000,
  "bySelector": [
    { "selector": "coverage", "runs": 2, "unsafeSkipCount": 1, "safeCount": 1, "testsSaved": 3, "estimatedSavedMs": 50000 }
  ],
  "gate": {
    "maxAllowedUnsafeSkips": 0,
    "calibrationWindowSize": 100,
    "assessableCount": 2,
    "readyForEnforcement": false,
    "reason": "1 unsafe skip(s) observed in the window"
  }
}
""";

    [Fact]
    public async Task Soundness_HumanReadable_RendersCountsAndGate()
    {
        HttpRequestMessage? captured = null;
        var factory = MakeFactory(req =>
        {
            captured = req;
            return JsonResponse(ResponseBody);
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["audit", "test-selection-soundness", "--limit", "100"], factory);

            Assert.Equal(0, code);
            Assert.NotNull(captured);
            Assert.Equal(HttpMethod.Get, captured.Method);
            Assert.Contains("/audit/test-selection/soundness", captured.RequestUri!.ToString());
            Assert.Contains("limit=100", captured.RequestUri!.ToString());
            var stdout = output.Out.ToString();
            Assert.Contains("Unsafe skips", stdout);
            Assert.Contains("coverage", stdout);
            Assert.Contains("Ready", stdout);
            Assert.Contains("false", stdout);
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Soundness_SelectorOption_IsSentAsQueryParam()
    {
        HttpRequestMessage? captured = null;
        var factory = MakeFactory(req =>
        {
            captured = req;
            return JsonResponse(ResponseBody);
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["audit", "test-selection-soundness", "--selector", "coverage"], factory);

            Assert.Equal(0, code);
            Assert.NotNull(captured);
            Assert.Contains("selector=coverage", captured.RequestUri!.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Soundness_Json_PrintsRawResponse()
    {
        var factory = MakeFactory(_ => JsonResponse(ResponseBody));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["audit", "test-selection-soundness", "--json"], factory);

            Assert.Equal(0, code);
            Assert.Equal(ResponseBody, output.Out.ToString().Trim());
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
