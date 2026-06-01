using System.Net;
using System.Net.Http.Json;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class QueueStatusTests
{
    private static Func<ResolvedConfig, CodeyBoxClient> MakeFactory(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(handler))
            { BaseAddress = new Uri(config.ApiBaseUrl) });
    }

    [Fact]
    public async Task Status_HumanReadable_ParsesAndPrintsFields()
    {
        const string responseBody = @"{
  ""paused"": true,
  ""pausedReason"": ""maintenance"",
  ""itemCount"": 42,
  ""nextItemId"": ""abc-123""
}";

        HttpRequestMessage? captured = null;
        var factory = MakeFactory(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json"),
            };
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "status"], factory);

            Assert.Equal(0, code);
            Assert.NotNull(captured);
            Assert.Equal(HttpMethod.Get, captured.Method);
            Assert.Contains("/queue/status", captured.RequestUri!.ToString());
            var stdout = output.Out.ToString();
            Assert.Contains("Paused:", stdout);
            Assert.Contains("True", stdout);
            Assert.Contains("maintenance", stdout);
            Assert.Contains("Items:", stdout);
            Assert.Contains("42", stdout);
            Assert.Contains("Next:", stdout);
            Assert.Contains("abc-123", stdout);
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Status_Json_PrintsRawResponse()
    {
        const string responseBody = "{\"paused\":false,\"itemCount\":7}";

        var factory = MakeFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json"),
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "status", "--json"], factory);

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
    public async Task Status_MinimalResponse_PrintsAvailableFields()
    {
        const string responseBody = "{\"paused\":false}";

        var factory = MakeFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json"),
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "status"], factory);

            Assert.Equal(0, code);
            var stdout = output.Out.ToString();
            Assert.Contains("Paused:", stdout);
            Assert.DoesNotContain("Reason:", stdout);
            Assert.DoesNotContain("Items:", stdout);
            Assert.DoesNotContain("Next:", stdout);
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Status_Unauthorized_WritesToStderrNonZeroExit()
    {
        var factory = MakeFactory(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("Unauthorized"),
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "status"], factory);

            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("401", output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Status_NetworkFailure_WritesToStderrNonZeroExit()
    {
        Func<ResolvedConfig, CodeyBoxClient> factory = config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(_ =>
                throw new HttpRequestException("Connection refused")))
            { BaseAddress = new Uri(config.ApiBaseUrl) });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "status"], factory);

            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("Connection", output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }
}
