using System.Net;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class WorkersCommandTests
{
    private static Func<ResolvedConfig, CodeyBoxClient> MakeFactory(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(handler))
            { BaseAddress = new Uri(config.ApiBaseUrl) });
    }

    [Fact]
    public async Task Workers_HumanReadable_GetsWorkersAndPrintsTable()
    {
        const string responseBody = """
[
  {
    "workerId": "worker-0001",
    "hostName": "host-a",
    "processId": 123,
    "startedAt": "2026-01-01T00:00:00Z",
    "lastHeartbeatAt": "2026-01-01T00:01:00Z",
    "currentWorkItemId": "item-0001"
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
            var code = await CliApp.InvokeAsync(["workers"], factory);

            Assert.Equal(0, code);
            Assert.NotNull(captured);
            Assert.Equal(HttpMethod.Get, captured.Method);
            Assert.EndsWith("/workers", captured.RequestUri!.ToString());
            var stdout = output.Out.ToString();
            Assert.Contains("WORKER", stdout);
            Assert.Contains("worker-0001", stdout);
            Assert.Contains("host-a", stdout);
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Workers_Json_PrintsRawResponse()
    {
        const string responseBody = """[{"workerId":"worker-0001"}]""";
        var factory = MakeFactory(_ => JsonResponse(responseBody));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["workers", "--json"], factory);

            Assert.Equal(0, code);
            Assert.Equal(responseBody, output.Out.ToString().Trim());
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task WorkersStatus_HumanReadable_GetsStatusAndPrintsTable()
    {
        const string responseBody = """
{
  "maxConcurrent": 4,
  "currentlyRunning": 2,
  "queuedCount": 7,
  "lastSpawnAt": "2026-01-01T00:02:00Z"
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
            var code = await CliApp.InvokeAsync(["workers", "status"], factory);

            Assert.Equal(0, code);
            Assert.NotNull(captured);
            Assert.Equal(HttpMethod.Get, captured.Method);
            Assert.EndsWith("/workers/status", captured.RequestUri!.ToString());
            var stdout = output.Out.ToString();
            Assert.Contains("Max concurrent", stdout);
            Assert.Contains("Currently running", stdout);
            Assert.Contains("7", stdout);
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task WorkersStatus_Json_PrintsRawResponse()
    {
        const string responseBody = """{"maxConcurrent":4,"currentlyRunning":2}""";
        var factory = MakeFactory(_ => JsonResponse(responseBody));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["workers", "status", "--json"], factory);

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
