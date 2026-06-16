using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CodeyBox.Sandbox.Multipass;

namespace CodeyBox.Tests;

/// <summary>
/// Round-trip tests for <see cref="MultipassSandboxProvider.BuildEnvironmentFileContent"/>
/// (each value survives a /bin/sh dot-source) plus a smoke test for the
/// exec wrapper's exit-126 diagnostic: a malformed env file must surface
/// the underlying shell error to wrapper stderr, not vanish into a bare
/// "exit 126".
/// </summary>
public sealed class MultipassExecWrapperDiagnosticsTests
{
    [Fact]
    public void BuildEnvironmentFileContent_RejectsNulInValue()
    {
        var env = new Dictionary<string, string> { ["X"] = "ok\0bad" };
        var ex = Assert.Throws<ArgumentException>(
            () => MultipassSandboxProvider.BuildEnvironmentFileContent(env));
        Assert.Contains("NUL", ex.Message);
        Assert.Contains("X", ex.Message);
    }

    [Fact]
    public void BuildEnvironmentFileContent_RejectsNulInKey()
    {
        var env = new Dictionary<string, string> { ["B\0AD"] = "value" };
        Assert.Throws<ArgumentException>(
            () => MultipassSandboxProvider.BuildEnvironmentFileContent(env));
    }

    [Theory]
    [InlineData("EMBED_NEWLINE", "line1\nline2\nline3")]
    [InlineData("EMBED_SQUOTE", "it's a 'quote' party")]
    [InlineData("EMBED_BACKSLASH", "a\\b\\c\\")]
    [InlineData("EMBED_BACKTICK", "value with `command` chars")]
    [InlineData("EMBED_DOLLAR", "$HOME $(date) ${PATH}")]
    [InlineData("EMBED_MIXED", "mix '\"$`\\\n end")]
    public async Task BuildEnvironmentFileContent_RoundTripsThroughShellDotSource(string key, string value)
    {
        if (OperatingSystem.IsWindows()) return;

        var content = MultipassSandboxProvider.BuildEnvironmentFileContent(
            new Dictionary<string, string> { [key] = value });

        var path = Path.Combine(Path.GetTempPath(), $"codeybox-env-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(path, content);
        try
        {
            var (exit, stdout, stderr) = await RunShellAsync(
                $". \"$1\"; printf %s \"${key}\"", path);

            Assert.Equal(0, exit);
            Assert.Equal("", stderr);
            Assert.Equal(value, stdout);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExecWrapper_FailedEnvFileSource_EmitsUnderlyingShellError()
    {
        if (OperatingSystem.IsWindows()) return;

        var workDir = Path.Combine(Path.GetTempPath(), $"codeybox-wrap-work-{Guid.NewGuid():N}");
        var wrapperPath = Path.Combine(Path.GetTempPath(), $"codeybox-wrap-{Guid.NewGuid():N}.sh");
        var badEnvPath = Path.Combine(Path.GetTempPath(), $"codeybox-bad-env-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        // Simulate the production failure mode: dot-sourcing returns
        // non-zero with diagnostic output on stderr. (We avoid syntax
        // errors here because dash treats those as fatal to the parent
        // shell, which short-circuits the wrapper before exit 126.)
        const string sentinel = "synthetic-underlying-error-detail";
        await File.WriteAllTextAsync(badEnvPath, $"echo '{sentinel}' >&2\nfalse\n");
        await File.WriteAllTextAsync(wrapperPath, MultipassSandboxProvider.ExecWrapperScript);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(wrapperPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            var (exit, _, stderr) = await RunProcessAsync(
                "/bin/bash", [wrapperPath, workDir, "--env-file", badEnvPath, "true"]);

            Assert.Equal(126, exit);
            Assert.Contains("failed to source env file", stderr, StringComparison.Ordinal);
            Assert.Contains(badEnvPath, stderr, StringComparison.Ordinal);
            Assert.Contains(sentinel, stderr, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(wrapperPath);
            File.Delete(badEnvPath);
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecWrapper_FailedCd_EmitsUnderlyingShellError()
    {
        if (OperatingSystem.IsWindows()) return;

        var wrapperPath = Path.Combine(Path.GetTempPath(), $"codeybox-wrap-{Guid.NewGuid():N}.sh");
        var missingDir = Path.Combine(Path.GetTempPath(), $"codeybox-nope-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(wrapperPath, MultipassSandboxProvider.ExecWrapperScript);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(wrapperPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            var (exit, _, stderr) = await RunProcessAsync(
                "/bin/bash", [wrapperPath, missingDir, "true"]);

            Assert.Equal(127, exit);
            Assert.Contains("failed to cd to", stderr, StringComparison.Ordinal);
            Assert.Contains(missingDir, stderr, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(wrapperPath);
        }
    }

    [Fact]
    public async Task ExecWrapper_ValidEnvFile_AppliesValuesAndExecsCommand()
    {
        if (OperatingSystem.IsWindows()) return;

        var workDir = Path.Combine(Path.GetTempPath(), $"codeybox-wrap-work-{Guid.NewGuid():N}");
        var wrapperPath = Path.Combine(Path.GetTempPath(), $"codeybox-wrap-{Guid.NewGuid():N}.sh");
        var envPath = Path.Combine(Path.GetTempPath(), $"codeybox-env-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var envContent = MultipassSandboxProvider.BuildEnvironmentFileContent(
            new Dictionary<string, string> { ["CODEYBOX_TEST_VALUE"] = "hello\nworld" });
        await File.WriteAllTextAsync(envPath, envContent);
        await File.WriteAllTextAsync(wrapperPath, MultipassSandboxProvider.ExecWrapperScript);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(wrapperPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            var (exit, stdout, stderr) = await RunProcessAsync(
                "/bin/bash",
                [wrapperPath, workDir, "--env-file", envPath, "sh", "-c", "printf %s \"$CODEYBOX_TEST_VALUE\""]);

            Assert.Equal(0, exit);
            Assert.Equal("", stderr);
            Assert.Equal("hello\nworld", stdout);
        }
        finally
        {
            File.Delete(wrapperPath);
            if (File.Exists(envPath))
                File.Delete(envPath);
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecWrapper_StdinFileFeedsChildCommandStdin()
    {
        if (OperatingSystem.IsWindows()) return;

        var workDir = Path.Combine(Path.GetTempPath(), $"codeybox-wrap-work-{Guid.NewGuid():N}");
        var wrapperPath = await CreateExecutableWrapperAsync();
        var stdinPath = Path.Combine(Path.GetTempPath(), $"codeybox-stdin-{Guid.NewGuid():N}");
        const string prompt = "prompt line 1\nprompt line 2 with $dollars and 'quotes'\n";
        Directory.CreateDirectory(workDir);
        await File.WriteAllTextAsync(stdinPath, prompt);

        try
        {
            var (exit, stdout, stderr) = await RunProcessAsync(
                "/bin/bash",
                [wrapperPath, "--stdin-file", stdinPath, workDir, "sh", "-c", "cat"]);

            Assert.Equal(0, exit);
            Assert.Equal(prompt, stdout);
            Assert.Equal("", stderr);
        }
        finally
        {
            File.Delete(wrapperPath);
            File.Delete(stdinPath);
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecWrapper_StdinFileMissingPathEmitsDiagnostic()
    {
        if (OperatingSystem.IsWindows()) return;

        var wrapperPath = await CreateExecutableWrapperAsync();
        try
        {
            var (exit, _, stderr) = await RunProcessAsync(
                "/bin/bash",
                [wrapperPath, "--stdin-file"]);

            Assert.Equal(127, exit);
            Assert.Contains("--stdin-file requires a path argument", stderr, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(wrapperPath);
        }
    }

    [Fact]
    public async Task ExecWrapper_HttpOutputTransportStreamsLineBatchesAndRetriesSameSequence()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await CommandAvailableAsync("python3")) return;

        var stdoutZeroAttempts = 0;
        await using var server = StubHttpIngestServer.Start(request =>
        {
            if (request.Stream == "stdout" && request.Seq == 0 && ++stdoutZeroAttempts == 1)
                return 500;
            return request.Stream == "ready" ? 204 : 200;
        });
        var workDir = Path.Combine(Path.GetTempPath(), $"codeybox-wrap-work-{Guid.NewGuid():N}");
        var wrapperPath = await CreateExecutableWrapperAsync();
        Directory.CreateDirectory(workDir);
        const string token = "test-token-not-for-agent";
        const string exitToken = "test-exit-token-not-for-agent";
        const string runId = "run-wrapper-ok";
        var env = new Dictionary<string, string?>
        {
            [MultipassAgentOutputHttpIngestSession.UrlEnvironmentVariable] = server.BaseUrl,
            [MultipassAgentOutputHttpIngestSession.TokenEnvironmentVariable] = token,
            [MultipassAgentOutputHttpIngestSession.ExitTokenEnvironmentVariable] = exitToken,
            [MultipassAgentOutputHttpIngestSession.RunIdEnvironmentVariable] = runId,
            ["CODEYBOX_AGENT_LOG_FILE"] = "",
        };

        try
        {
            var agentScript = """
                if [ -n "${CODEYBOX_AGENT_OUTPUT_TOKEN:-}" ]; then printf 'token leaked\n'; fi
                if [ -n "${CODEYBOX_AGENT_OUTPUT_EXIT_TOKEN:-}" ]; then printf 'exit token leaked\n'; fi
                printf 'out-1\n'
                printf 'out-2\n'
                printf 'err-1\n' >&2
                """;

            var (exit, stdout, stderr) = await RunProcessAsync(
                "/bin/bash",
                [wrapperPath, workDir, "sh", "-c", agentScript],
                env);

            Assert.Equal(0, exit);
            Assert.Equal("", stdout);
            Assert.Equal("", stderr);
            Assert.Equal(2, stdoutZeroAttempts);

            var requests = server.Requests.ToArray();
            Assert.Contains(requests, r => r.Stream == "ready" && r.RunId == runId && r.Seq == 0);

            var stdoutRequests = requests.Where(r => r.Stream == "stdout").ToArray();
            Assert.Equal([0, 0, 1], stdoutRequests.Select(r => r.Seq).ToArray());
            Assert.Equal("out-1\n", stdoutRequests[0].BodyText);
            Assert.Equal("out-1\n", stdoutRequests[1].BodyText);
            Assert.Equal("out-2\n", stdoutRequests[2].BodyText);

            var stderrRequest = Assert.Single(requests, r => r.Stream == "stderr");
            Assert.Equal(0, stderrRequest.Seq);
            Assert.Equal("err-1\n", stderrRequest.BodyText);
            var exitRequest = Assert.Single(requests, r => r.Stream == "exit");
            Assert.Equal(0, exitRequest.Seq);
            Assert.Equal("0\n", exitRequest.BodyText);
            Assert.DoesNotContain(requests, r => r.BodyText.Contains(token, StringComparison.Ordinal));
            Assert.DoesNotContain(requests, r => r.BodyText.Contains(exitToken, StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(wrapperPath);
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecWrapper_HttpOutputTransportWithLogFileTeesOutputAndPostsExit()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await CommandAvailableAsync("python3")) return;

        await using var server = StubHttpIngestServer.Start(request =>
            request.Stream == "ready" ? 204 : 200);
        var workDir = Path.Combine(Path.GetTempPath(), $"codeybox-wrap-work-{Guid.NewGuid():N}");
        var wrapperPath = await CreateExecutableWrapperAsync();
        Directory.CreateDirectory(workDir);
        var logPath = Path.Combine(workDir, "agent.log");
        var env = new Dictionary<string, string?>
        {
            [MultipassAgentOutputHttpIngestSession.UrlEnvironmentVariable] = server.BaseUrl,
            [MultipassAgentOutputHttpIngestSession.TokenEnvironmentVariable] = "test-token",
            [MultipassAgentOutputHttpIngestSession.ExitTokenEnvironmentVariable] = "test-exit-token",
            [MultipassAgentOutputHttpIngestSession.RunIdEnvironmentVariable] = "run-wrapper-log",
            ["CODEYBOX_AGENT_LOG_FILE"] = logPath,
        };

        try
        {
            var agentScript = """
                printf 'out-log\n'
                printf 'err-log\n' >&2
                exit 5
                """;

            var (exit, stdout, stderr) = await RunProcessAsync(
                "/bin/bash",
                [wrapperPath, workDir, "sh", "-c", agentScript],
                env);

            Assert.Equal(5, exit);
            Assert.Equal("", stdout);
            Assert.Equal("", stderr);
            Assert.Equal("out-log\nerr-log\n", await File.ReadAllTextAsync(logPath));
            Assert.Equal("5\n", await File.ReadAllTextAsync(logPath + ".exit"));

            var requests = server.Requests.ToArray();
            Assert.Contains(requests, r => r.Stream == "ready" && r.RunId == "run-wrapper-log" && r.Seq == 0);
            Assert.DoesNotContain(requests, r => r.Stream == "stderr");
            Assert.Equal("out-log\nerr-log\n", string.Concat(
                requests.Where(r => r.Stream == "stdout").OrderBy(r => r.Seq).Select(r => r.BodyText)));

            var exitRequest = Assert.Single(requests, r => r.Stream == "exit");
            Assert.Equal(0, exitRequest.Seq);
            Assert.Equal("5\n", exitRequest.BodyText);
        }
        finally
        {
            File.Delete(wrapperPath);
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecWrapper_HttpOutputTransportTerminalStreamStatusFailsRun()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await CommandAvailableAsync("python3")) return;

        await using var server = StubHttpIngestServer.Start(request =>
            request.Stream == "ready" ? 204 : 409);
        var workDir = Path.Combine(Path.GetTempPath(), $"codeybox-wrap-work-{Guid.NewGuid():N}");
        var wrapperPath = await CreateExecutableWrapperAsync();
        Directory.CreateDirectory(workDir);
        var env = new Dictionary<string, string?>
        {
            [MultipassAgentOutputHttpIngestSession.UrlEnvironmentVariable] = server.BaseUrl,
            [MultipassAgentOutputHttpIngestSession.TokenEnvironmentVariable] = "test-token",
            [MultipassAgentOutputHttpIngestSession.ExitTokenEnvironmentVariable] = "test-exit-token",
            [MultipassAgentOutputHttpIngestSession.RunIdEnvironmentVariable] = "run-wrapper-conflict",
            ["CODEYBOX_AGENT_LOG_FILE"] = "",
        };

        try
        {
            var (exit, stdout, stderr) = await RunProcessAsync(
                "/bin/bash",
                [wrapperPath, workDir, "sh", "-c", "printf 'out-1\\n'"],
                env);

            Assert.Equal(87, exit);
            Assert.Equal("", stdout);
            Assert.Contains("agent output HTTP ingest failed during run", stderr, StringComparison.Ordinal);

            var stdoutRequest = Assert.Single(server.Requests, r => r.Stream == "stdout");
            Assert.Equal(0, stdoutRequest.Seq);
            Assert.Equal("out-1\n", stdoutRequest.BodyText);
        }
        finally
        {
            File.Delete(wrapperPath);
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecWrapper_HttpOutputTransportTerminalExitStatusFailsRunAfterStreamsSucceed()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await CommandAvailableAsync("python3")) return;

        await using var server = StubHttpIngestServer.Start(request => request.Stream switch
        {
            "ready" => 204,
            "exit" => 409,
            _ => 200,
        });
        var workDir = Path.Combine(Path.GetTempPath(), $"codeybox-wrap-work-{Guid.NewGuid():N}");
        var wrapperPath = await CreateExecutableWrapperAsync();
        Directory.CreateDirectory(workDir);
        var env = new Dictionary<string, string?>
        {
            [MultipassAgentOutputHttpIngestSession.UrlEnvironmentVariable] = server.BaseUrl,
            [MultipassAgentOutputHttpIngestSession.TokenEnvironmentVariable] = "test-token",
            [MultipassAgentOutputHttpIngestSession.ExitTokenEnvironmentVariable] = "test-exit-token",
            [MultipassAgentOutputHttpIngestSession.RunIdEnvironmentVariable] = "run-wrapper-exit-conflict",
            ["CODEYBOX_AGENT_LOG_FILE"] = "",
        };

        try
        {
            var agentScript = """
                printf 'out-before-exit\n'
                printf 'err-before-exit\n' >&2
                exit 4
                """;

            var (exit, stdout, stderr) = await RunProcessAsync(
                "/bin/bash",
                [wrapperPath, workDir, "sh", "-c", agentScript],
                env);

            Assert.Equal(87, exit);
            Assert.Equal("", stdout);
            Assert.Contains("agent output HTTP ingest failed during run", stderr, StringComparison.Ordinal);

            var requests = server.Requests.ToArray();
            Assert.Contains(requests, r => r.Stream == "stdout" && r.BodyText == "out-before-exit\n");
            Assert.Contains(requests, r => r.Stream == "stderr" && r.BodyText == "err-before-exit\n");
            var exitRequest = Assert.Single(requests, r => r.Stream == "exit");
            Assert.Equal(0, exitRequest.Seq);
            Assert.Equal("4\n", exitRequest.BodyText);
        }
        finally
        {
            File.Delete(wrapperPath);
            Directory.Delete(workDir, recursive: true);
        }
    }

    private static Task<(int Exit, string Stdout, string Stderr)> RunShellAsync(string script, string arg)
        => RunProcessAsync("/bin/sh", ["-c", script, "codeybox-test", arg]);

    private static async Task<string> CreateExecutableWrapperAsync()
    {
        var wrapperPath = Path.Combine(Path.GetTempPath(), $"codeybox-wrap-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(wrapperPath, MultipassSandboxProvider.ExecWrapperScript);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(wrapperPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return wrapperPath;
    }

    private static async Task<bool> CommandAvailableAsync(string command)
    {
        var (exit, _, _) = await RunProcessAsync("/usr/bin/env", [command, "--version"]);
        return exit == 0;
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        ClearInheritedCodeyBoxTransportEnvironment(psi);
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
                psi.Environment[key] = value ?? "";
        }

        using var process = Process.Start(psi)!;
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutTask = ReadAllAsync(process.StandardOutput, stdout);
        var stderrTask = ReadAllAsync(process.StandardError, stderr);
        await process.WaitForExitAsync();
        await Task.WhenAll(stdoutTask, stderrTask);
        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static void ClearInheritedCodeyBoxTransportEnvironment(ProcessStartInfo psi)
    {
        psi.Environment.Remove("CODEYBOX_AGENT_LOG_FILE");
        psi.Environment.Remove(MultipassAgentOutputHttpIngestSession.UrlEnvironmentVariable);
        psi.Environment.Remove(MultipassAgentOutputHttpIngestSession.TokenEnvironmentVariable);
        psi.Environment.Remove(MultipassAgentOutputHttpIngestSession.RunIdEnvironmentVariable);
    }

    private static async Task ReadAllAsync(System.IO.StreamReader reader, StringBuilder sink)
    {
        var buffer = new char[4096];
        int n;
        while ((n = await reader.ReadAsync(buffer.AsMemory())) > 0)
            sink.Append(buffer, 0, n);
    }

    private sealed class StubHttpIngestServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Func<StubHttpRequest, int> _statusSelector;
        private readonly Task _listenTask;

        private StubHttpIngestServer(HttpListener listener, string baseUrl, Func<StubHttpRequest, int> statusSelector)
        {
            _listener = listener;
            BaseUrl = baseUrl;
            _statusSelector = statusSelector;
            _listenTask = Task.Run(ListenAsync);
        }

        public string BaseUrl { get; }
        public List<StubHttpRequest> Requests { get; } = [];

        public static StubHttpIngestServer Start(Func<StubHttpRequest, int> statusSelector)
        {
            const int maxAttempts = 20;
            HttpListenerException? lastBindFailure = null;

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                var port = GetFreeTcpPort();
                var prefix = $"http://127.0.0.1:{port}/";
                var listener = new HttpListener();
                listener.Prefixes.Add(prefix);
                try
                {
                    listener.Start();
                    return new StubHttpIngestServer(
                        listener,
                        prefix.TrimEnd('/') + "/codeybox-agent-output",
                        statusSelector);
                }
                catch (HttpListenerException ex) when (IsAddressAlreadyInUse(ex))
                {
                    lastBindFailure = ex;
                    listener.Close();
                }
            }

            throw new InvalidOperationException("Could not bind test HTTP listener to a free loopback port.", lastBindFailure);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            try { await _listenTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            _cts.Dispose();
        }

        private async Task ListenAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (HttpListenerException) when (!_listener.IsListening) { break; }
                catch (ObjectDisposedException) { break; }

                await HandleAsync(context).ConfigureAwait(false);
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            using var memory = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(memory, _cts.Token).ConfigureAwait(false);
            var request = StubHttpRequest.From(context.Request.Url?.AbsolutePath ?? "", memory.ToArray());
            lock (Requests)
            {
                Requests.Add(request);
            }
            context.Response.StatusCode = _statusSelector(request);
            context.Response.Close();
        }

        private static int GetFreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        private static bool IsAddressAlreadyInUse(HttpListenerException ex)
            => ex.ErrorCode is 98 or 183 or 10048
               || ex.Message.Contains("Address already in use", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record StubHttpRequest(string RunId, string Stream, long Seq, string BodyText)
    {
        public static StubHttpRequest From(string path, byte[] body)
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var runId = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
            var stream = parts.Length > 2 ? Uri.UnescapeDataString(parts[2]) : "";
            var seq = parts.Length > 3 && long.TryParse(parts[3], out var parsed) ? parsed : -1;
            return new StubHttpRequest(runId, stream, seq, Encoding.UTF8.GetString(body));
        }
    }
}
