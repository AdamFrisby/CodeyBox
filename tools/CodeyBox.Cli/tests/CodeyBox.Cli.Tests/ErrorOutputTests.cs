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
            AssertFullApiBaseUrlPrecedence(error, tempDir);
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
            AssertFullApiBaseUrlPrecedence(error, tempDir);
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
    [InlineData("http://:5036", "value is not an absolute URI")]
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
            AssertFullApiBaseUrlPrecedence(error, tempDir);
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
    public async Task Error_EmptyApiBaseUrlFlag_IsReportedAsMalformedWithoutFallingThrough()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        WriteConfigFile(tempDir, "http://127.0.0.1:5998", "test-key");
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", tempDir);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_URL", "http://127.0.0.1:5999");
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");

        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["--api-url", "", "queue", "ls"],
                MakeNetworkForbiddenFactory());

            var error = output.Error.ToString();
            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("malformed API base URL '<empty>'.", error);
            Assert.Contains("Source: --api-url flag.", error);
            Assert.Contains("Cause: value is empty", error);
            Assert.DoesNotContain("5998", error);
            Assert.DoesNotContain("5999", error);
            Assert.DoesNotContain("network should not be attempted", error);
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
    public async Task Error_EmptyApiBaseUrlEnvironmentVariable_IsReportedAsMalformedWithoutFallingThrough()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        WriteConfigFile(tempDir, "http://127.0.0.1:5998", "test-key");
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", tempDir);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_URL", "");
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");

        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "ls"], MakeNetworkForbiddenFactory());

            var error = output.Error.ToString();
            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("malformed API base URL '<empty>'.", error);
            Assert.Contains("Source: CODEYBOX_CLI_API_URL environment variable.", error);
            Assert.Contains("Cause: value is empty", error);
            Assert.DoesNotContain("5998", error);
            Assert.DoesNotContain("network should not be attempted", error);
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
    public async Task Error_EmptyApiBaseUrlConfigFileValue_IsReportedAsMalformedWithoutFallingThrough()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        WriteConfigFile(tempDir, "", "test-key");
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", tempDir);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_URL", null);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);

        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "ls"], MakeNetworkForbiddenFactory());

            var error = output.Error.ToString();
            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("malformed API base URL '<empty>'.", error);
            Assert.Contains($"Source: {Path.Combine(tempDir, "config.json")} config file.", error);
            Assert.Contains("Cause: value is empty", error);
            Assert.DoesNotContain("Source: built-in default", error);
            Assert.DoesNotContain("network should not be attempted", error);
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
        yield return new object[] { new[] { "queue", "show", "some-id" } };
        yield return new object[] { new[] { "queue", "add", "--project", "proj", "--title", "Title", "--prompt", "Prompt" } };
        yield return new object[] { new[] { "queue", "template", "checks", "--project", "proj" } };
        yield return new object[] { new[] { "queue", "retry", "some-id" } };
        yield return new object[] { new[] { "queue", "abandon", "some-id" } };
        yield return new object[] { new[] { "queue", "pause", "--reason", "maintenance" } };
        yield return new object[] { new[] { "queue", "resume" } };
        yield return new object[] { new[] { "queue", "cancel", "some-id" } };
        yield return new object[] { new[] { "queue", "reorder", "id-1", "id-2" } };
        yield return new object[] { new[] { "agents", "pause", "claude", "--reason", "maintenance" } };
        yield return new object[] { new[] { "agents", "resume", "claude" } };
        yield return new object[] { new[] { "agents", "paused" } };
    }

    public static IEnumerable<object[]> ConnectionFailureCommandPathAndCauseCases()
    {
        foreach (var row in ConnectionFailureCommandPaths())
        {
            var args = (string[])row[0];
            yield return new object[] { args, SocketFailure(SocketError.ConnectionRefused), "connection refused" };
            yield return new object[] { args, SocketFailure(SocketError.HostNotFound), "invalid host or DNS lookup failed" };
            yield return new object[] { args, SocketFailure(SocketError.TimedOut), "timeout" };
            yield return new object[]
            {
                args,
                new OperationCanceledException(
                    "request was canceled",
                    new TimeoutException("connect timed out")),
                "timeout",
            };
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Error_WatchConnectionFailure_PrintsResolvedUrlCauseRemedyAndSource(bool forcePoll)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var url = $"http://127.0.0.1:{GetUnusedTcpPort()}";
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", tempDir);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_URL", null);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");

        var args = forcePoll
            ? new[] { "--api-url", url, "queue", "watch", "aabbccdd-0000-0000-0000-000000000000", "--poll" }
            : new[] { "--api-url", url, "queue", "watch", "aabbccdd-0000-0000-0000-000000000000" };

        using var output = new TestOutput();
        try
        {
            var sw = Stopwatch.StartNew();
            var code = await CliApp.InvokeAsync(args);
            sw.Stop();

            var error = output.Error.ToString();
            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("Connection error: Could not connect to the CodeyBox API.", error);
            Assert.Contains($"Resolved API base URL: {url}", error);
            Assert.Contains("Source: --api-url flag.", error);
            Assert.Contains("Cause: connection refused", error);
            AssertFullApiBaseUrlPrecedence(error, tempDir);
            Assert.Contains("Run codeybox configure to set the API base URL and key, or pass --api-url.", error);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"CLI took {sw.Elapsed} to report watch connection failure.");
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
    public async Task Error_CallerCancelledRequest_IsNotReportedAsConnectionFailure()
    {
        var config = new ResolvedConfig
        {
            ApiBaseUrl = "http://127.0.0.1:5036",
            ApiKey = "test-key",
        };
        using var client = new HttpClient(new CancelledHttpMessageHandler())
        {
            BaseAddress = new Uri(config.ApiBaseUrl),
        };
        var codeyBoxClient = new CodeyBoxClient(client, config);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => codeyBoxClient.GetWorkItemsAsync(ct: cts.Token));

        Assert.IsNotType<CodeyBoxConnectionException>(ex);
    }

    [Fact]
    public async Task Error_MalformedApiBaseUrl_RedactsUrlLikeSecretsWhenUriCannotBeParsed()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        const string url = "http://user:secret@example.com:notaport/api?token=secret#fragment";
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", tempDir);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_URL", null);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");

        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["--api-url", url, "queue", "ls"], MakeNetworkForbiddenFactory());

            var error = output.Error.ToString();
            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("malformed API base URL 'http://redacted@example.com:notaport/api?redacted'.", error);
            Assert.Contains("Cause: value is not an absolute URI", error);
            Assert.DoesNotContain("secret", error);
            Assert.DoesNotContain("token=", error);
            Assert.DoesNotContain("fragment", error);
            Assert.DoesNotContain("network should not be attempted", error);
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
    [MemberData(nameof(ConnectionFailureCommandPathAndCauseCases))]
    public async Task Error_ConnectionFailure_IsWrappedAcrossHttpCommandPaths(
        string[] args,
        Exception exception,
        string expectedCause)
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
                MakeThrowingDiagnosticFactory(exception));

            var error = output.Error.ToString();
            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("Could not connect to the CodeyBox API.", error);
            Assert.Contains("Resolved API base URL: http://localhost:5036", error);
            Assert.Contains("Source: built-in default http://localhost:5036.", error);
            Assert.Contains($"Cause: {expectedCause}", error);
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
    public void Warning_PlaintextHttpNonLoopback_RedactsUrlSecrets()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var url = "http://user:secret@example.com:5036/api?token=secret#fragment";
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", tempDir);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_URL", null);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);

        using var output = new TestOutput();
        try
        {
            var config = ConfigResolver.Resolve(url, null);

            var error = output.Error.ToString();
            Assert.Equal(url, config.ApiBaseUrl);
            Assert.Contains(
                "Warning: API base URL 'http://redacted@example.com:5036/api?redacted' uses plaintext HTTP on a non-loopback address",
                error);
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

    private static Func<ResolvedConfig, CodeyBoxClient> MakeNetworkForbiddenFactory() =>
        config =>
        {
            using var _ = CodeyBoxHttpFactory.CreateClient(config, TimeSpan.FromSeconds(30));
            throw new InvalidOperationException("network should not be attempted");
        };

    private static HttpRequestException SocketFailure(SocketError socketError) =>
        new("socket failure", new SocketException((int)socketError));

    private static void AssertFullApiBaseUrlPrecedence(string error, string tempDir)
    {
        var configPath = Path.Combine(tempDir, "config.json");
        Assert.Contains(
            $"Precedence: --api-url flag > CODEYBOX_CLI_API_URL environment variable > {configPath} config file > built-in default http://localhost:5036.",
            error);
    }

    private static void WriteConfigFile(string tempDir, string url, string key)
    {
        Directory.CreateDirectory(tempDir);
        var config = new CliConfig { ApiBaseUrl = url, ApiKey = key };
        var json = JsonSerializer.Serialize(config, CliJsonContext.Default.CliConfig);
        File.WriteAllText(Path.Combine(tempDir, "config.json"), json);
    }

    private sealed class CancelledHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromCanceled<HttpResponseMessage>(cancellationToken);
    }
}
