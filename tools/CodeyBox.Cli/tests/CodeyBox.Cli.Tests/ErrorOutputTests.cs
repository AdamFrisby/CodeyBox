using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
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
    public async Task Error_DnsFailure_PrintsResolvedUrlCauseRemedyAndSource()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var url = "http://does-not-resolve.invalid:5036";
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", tempDir);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_URL", null);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");

        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["--api-url", url, "queue", "ls"],
                MakeThrowingDiagnosticFactory(SocketFailure(SocketError.HostNotFound)));

            var error = output.Error.ToString();
            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains($"Resolved API base URL: {url}", error);
            Assert.Contains("Source: --api-url flag.", error);
            Assert.Contains("Cause: invalid host or DNS lookup failed", error);
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
    public async Task Error_OperationCanceledTimeout_PrintsTimeoutDiagnostics()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var url = "http://127.0.0.1:5036";
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", tempDir);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_URL", null);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");

        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["--api-url", url, "queue", "ls"],
                MakeThrowingDiagnosticFactory(
                    new OperationCanceledException(
                        "request was canceled",
                        new TimeoutException("connect timed out"))));

            var error = output.Error.ToString();
            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains($"Resolved API base URL: {url}", error);
            Assert.Contains("Source: --api-url flag.", error);
            Assert.Contains("Cause: timeout", error);
            Assert.Contains("Underlying error: connect timed out", error);
            Assert.Contains("Run codeybox configure to set the API base URL and key, or pass --api-url.", error);
            Assert.DoesNotContain("Unexpected error", error);
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
    public void CodeyBoxHttpFactory_UsesShortConnectTimeout()
    {
        var config = new ResolvedConfig
        {
            ApiBaseUrl = "http://127.0.0.1:5036",
            ApiKey = "test-key",
        };

        using var client = CodeyBoxHttpFactory.CreateClient(config, TimeSpan.FromSeconds(30));
        var handlerField = typeof(HttpMessageInvoker).GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(handlerField);
        var handler = Assert.IsType<SocketsHttpHandler>(handlerField.GetValue(client));
        Assert.Equal(CliConnectionDiagnostics.ConnectTimeout, handler.ConnectTimeout);
        Assert.True(handler.ConnectTimeout <= TimeSpan.FromSeconds(2));
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

    [Theory]
    [InlineData("   ", "value is empty")]
    [InlineData("ftp://127.0.0.1:5036", "scheme must be http or https")]
    public async Task Error_MalformedApiBaseUrl_PrintsActionableMessageForValidationBranches(
        string apiBaseUrl,
        string expectedCause)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", tempDir);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_URL", null);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");

        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["--api-url", apiBaseUrl, "queue", "ls"]);

            var error = output.Error.ToString();
            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("malformed API base URL", error);
            Assert.Contains("Source: --api-url flag.", error);
            Assert.Contains($"Cause: {expectedCause}", error);
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
    public async Task Error_ConnectionFailure_PrintsEnvVarSource()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var url = "http://127.0.0.1:5036";
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", tempDir);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_URL", url);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");

        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "ls"],
                MakeThrowingDiagnosticFactory(SocketFailure(SocketError.ConnectionRefused)));

            var error = output.Error.ToString();
            Assert.NotEqual(0, code);
            Assert.Contains($"Resolved API base URL: {url}", error);
            Assert.Contains("Source: CODEYBOX_CLI_API_URL environment variable.", error);
            Assert.Contains("Cause: connection refused", error);
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
    public async Task Error_ConnectionFailure_PrintsConfigFileSource()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var url = "http://127.0.0.1:5036";
        WriteConfigFile(tempDir, url, "test-key");
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", tempDir);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_URL", null);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);

        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "ls"],
                MakeThrowingDiagnosticFactory(SocketFailure(SocketError.ConnectionRefused)));

            var error = output.Error.ToString();
            Assert.NotEqual(0, code);
            Assert.Contains($"Resolved API base URL: {url}", error);
            Assert.Contains($"Source: {Path.Combine(tempDir, "config.json")} config file.", error);
            Assert.Contains("Cause: connection refused", error);
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
    public async Task Error_ConnectionFailure_PrintsBuiltInDefaultSource()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", tempDir);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_URL", null);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");

        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "ls"],
                MakeThrowingDiagnosticFactory(SocketFailure(SocketError.ConnectionRefused)));

            var error = output.Error.ToString();
            Assert.NotEqual(0, code);
            Assert.Contains("Resolved API base URL: http://localhost:5036", error);
            Assert.Contains("Source: built-in default http://localhost:5036.", error);
            Assert.Contains("Cause: connection refused", error);
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
    public async Task Error_ConnectionFailure_RedactsUrlSecrets()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var url = "http://user:secret@127.0.0.1:5036/api?token=secret#fragment";
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", tempDir);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_URL", null);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");

        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["--api-url", url, "queue", "ls"],
                MakeThrowingDiagnosticFactory(SocketFailure(SocketError.ConnectionRefused)));

            var error = output.Error.ToString();
            Assert.NotEqual(0, code);
            Assert.Contains("Resolved API base URL: http://redacted@127.0.0.1:5036/api?redacted", error);
            Assert.DoesNotContain("secret", error);
            Assert.DoesNotContain("token=", error);
            Assert.DoesNotContain("fragment", error);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", null);
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_URL", null);
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    public static IEnumerable<object[]> ConnectionFailureCommandPaths()
    {
        yield return new object[] { new[] { "workers" } };
        yield return new object[] { new[] { "queue", "pause", "--reason", "maintenance" } };
        yield return new object[] { new[] { "queue", "resume" } };
        yield return new object[] { new[] { "queue", "cancel", "some-id" } };
    }

    [Theory]
    [MemberData(nameof(ConnectionFailureCommandPaths))]
    public async Task Error_ConnectionFailure_IsWrappedAcrossHttpCommandPaths(string[] args)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", tempDir);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_URL", null);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");

        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                args,
                MakeThrowingDiagnosticFactory(SocketFailure(SocketError.ConnectionRefused)));

            var error = output.Error.ToString();
            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("Could not connect to the CodeyBox API.", error);
            Assert.Contains("Cause: connection refused", error);
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

    private static Func<ResolvedConfig, CodeyBoxClient> MakeThrowingDiagnosticFactory(Exception exception)
    {
        return config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(_ => throw exception))
            {
                BaseAddress = new Uri(config.ApiBaseUrl),
            },
            config);
    }

    private static HttpRequestException SocketFailure(SocketError socketError) =>
        new("socket failure", new SocketException((int)socketError));

    private static void WriteConfigFile(string tempDir, string url, string key)
    {
        Directory.CreateDirectory(tempDir);
        var config = new CliConfig { ApiBaseUrl = url, ApiKey = key };
        var json = JsonSerializer.Serialize(config, CliJsonContext.Default.CliConfig);
        File.WriteAllText(Path.Combine(tempDir, "config.json"), json);
    }
}
