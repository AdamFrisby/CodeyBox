using System.Net;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class JsonEndpointCommandTests
{
    private static Func<ResolvedConfig, CodeyBoxClient> MakeFactory(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(handler))
            { BaseAddress = new Uri(config.ApiBaseUrl) });
    }

    [Fact]
    public async Task SharedRunner_MissingApiKey_WritesHintAndNonZeroExit()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", tempDir);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        Func<ResolvedConfig, CodeyBoxClient> factory = _ =>
            throw new InvalidOperationException("Factory should not be called without an API key.");

        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["workers"], factory);

            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("codeybox configure", output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", null);
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SharedRunner_InvalidJson_WritesParsingErrorAndNonZeroExit()
    {
        var factory = MakeFactory(_ => TextResponse(HttpStatusCode.OK, "not valid json"));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["workers"], factory);

            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("Error parsing response", output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task SharedRunner_ApiError_WritesStatusAndNonZeroExit()
    {
        var factory = MakeFactory(_ => TextResponse(HttpStatusCode.ServiceUnavailable, "unavailable"));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["workers"], factory);

            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("Error (503)", output.Error.ToString());
            Assert.Contains("unavailable", output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task SharedRunner_HttpRequestException_WritesConnectionErrorAndNonZeroExit()
    {
        var factory = MakeFactory(_ => throw new HttpRequestException("connection refused"));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["workers"], factory);

            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("Connection error", output.Error.ToString());
            Assert.Contains("connection refused", output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    private static HttpResponseMessage TextResponse(HttpStatusCode statusCode, string body)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body),
        };
    }
}
