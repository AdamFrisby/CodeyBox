using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class ErrorOutputTests
{
    private static Func<ResolvedConfig, CodeyBoxClient> MakeFactory(HttpStatusCode status, string body = "Unauthorized")
    {
        return config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(_ =>
                new HttpResponseMessage(status) { Content = new StringContent(body) }))
            { BaseAddress = new Uri(config.ApiBaseUrl) });
    }

    [Fact]
    public async Task Error_Unauthorized_WritesToStderrNonZeroExit()
    {
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "ls"], MakeFactory(HttpStatusCode.Unauthorized));

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
    public async Task Error_NotFound_WritesToStderrNonZeroExit()
    {
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            Func<ResolvedConfig, CodeyBoxClient> factory = config => new CodeyBoxClient(
                new HttpClient(new FakeHttpMessageHandler(_ =>
                    new HttpResponseMessage(HttpStatusCode.NotFound)))
                { BaseAddress = new Uri(config.ApiBaseUrl) });

            var code = await CliApp.InvokeAsync(["queue", "show", "nonexistent-id"], factory);

            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("not found", output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Error_InternalServerError_WritesToStderrNonZeroExit()
    {
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "ls"],
                MakeFactory(HttpStatusCode.InternalServerError, "Internal error details"));

            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("500", output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Error_ConnectionRefused_PrintsResolvedUrlCauseRemedyAndReturnsPromptly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var url = $"http://127.0.0.1:{GetUnusedTcpPort()}";
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", tempDir);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_URL", null);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");

        using var output = new TestOutput();
        try
        {
            var sw = Stopwatch.StartNew();
            var code = await CliApp.InvokeAsync(["--api-url", url, "queue", "ls"]);
            sw.Stop();

            var error = output.Error.ToString();
            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains($"Resolved API base URL: {url}", error);
            Assert.Contains("Source: --api-url flag.", error);
            Assert.Contains("Cause: connection refused", error);
            Assert.Contains("Run codeybox configure to set the API base URL and key, or pass --api-url.", error);
            Assert.Contains("--api-url flag > CODEYBOX_CLI_API_URL environment variable", error);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"CLI took {sw.Elapsed} to report connection refused.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", null);
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_URL", null);
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Error_MalformedApiBaseUrl_PrintsSourcePrecedenceAndSkipsNetwork()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", tempDir);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_URL", null);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");

        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["--api-url", "not a url", "queue", "ls"]);

            var error = output.Error.ToString();
            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("malformed API base URL 'not a url'", error);
            Assert.Contains("Source: --api-url flag.", error);
            Assert.Contains("Cause: value is not an absolute URI", error);
            Assert.Contains("--api-url flag > CODEYBOX_CLI_API_URL environment variable", error);
            Assert.Contains("Run codeybox configure to set the API base URL and key, or pass --api-url.", error);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", null);
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_URL", null);
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Error_MissingApiKey_PrintsHintAndNonZeroExit()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", tempDir);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);

        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "ls"]);

            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("codeybox configure", output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", null);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Error_MissingPrompt_WritesToStderr()
    {
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "add", "--project", "foo", "--title", "bar"],
                MakeFactory(HttpStatusCode.OK));

            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("--prompt", output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    private static int GetUnusedTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
