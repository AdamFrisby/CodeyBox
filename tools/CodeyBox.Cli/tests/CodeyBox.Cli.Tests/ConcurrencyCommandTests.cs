using System.Net;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class ConcurrencyCommandTests
{
    private static Func<ResolvedConfig, CodeyBoxClient> MakeFactory(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(handler))
            { BaseAddress = new Uri(config.ApiBaseUrl) });
    }

    [Fact]
    public async Task Concurrency_HumanReadable_GetsConcurrencyAndPrintsTables()
    {
        const string responseBody = """
{
  "globalMaxConcurrent": 4,
  "currentlyRunningTotal": 1,
  "perAgentCaps": { "codex": 1, "claude": 2 },
  "currentlyRunningPerAgent": { "codex": 1 },
  "burnEstimates": [
    { "agent": "codex", "avgBurnPctPerItem": 90, "sampleCount": 10 }
  ],
  "memberFits": [
    {
      "classId": "default",
      "agent": "codex",
      "modelId": "gpt-5",
      "availablePct": 80,
      "avgBurnPctPerItem": 10,
      "fitInWindow": 8,
      "runningOnAgent": 1
    }
  ],
  "agentAvailability": [
    {
      "agent": "claude",
      "excluded": true,
      "reason": "smoke failed",
      "consecutiveFastFails": 3
    }
  ]
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
            var code = await CliApp.InvokeAsync(["concurrency"], factory);

            Assert.Equal(0, code);
            Assert.NotNull(captured);
            Assert.Equal(HttpMethod.Get, captured.Method);
            Assert.EndsWith("/concurrency", captured.RequestUri!.ToString());
            var stdout = output.Out.ToString();
            Assert.Contains("Global max concurrent", stdout);
            Assert.Contains("codex", stdout);
            Assert.Contains("default", stdout);
            Assert.Contains("smoke failed", stdout);
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Concurrency_Json_PrintsRawResponse()
    {
        const string responseBody = """{"globalMaxConcurrent":4,"currentlyRunningTotal":1}""";
        var factory = MakeFactory(_ => JsonResponse(responseBody));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["concurrency", "--json"], factory);

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
