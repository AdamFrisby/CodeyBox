using CodeyBox.HostProcess;
using CodeyBox.Sandbox.Incus;
using ControllableTimeProvider = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests;

public sealed class IncusCliRunnerTests
{
    private static readonly IncusSandboxOptions Options = new()
    {
        OperationTimeout = TimeSpan.FromSeconds(1),
        MaxConcurrentOperations = 1,
    };

    [Fact]
    public async Task RunCheckedAsync_ReturnsSuccessfulProcessResult()
    {
        var processResult = new ProcessRunResult(0, "ready\n", "");
        var runner = new IncusCliRunner(new StubProcessRunner((_, _, _) =>
            Task.FromResult(processResult)));

        var actual = await runner.RunCheckedAsync(
            "probe",
            Options,
            ["incus", "version"],
            stdin: null,
            timeout: null,
            CancellationToken.None);

        Assert.Equal(processResult, actual);
    }

    [Fact]
    public async Task RunCheckedAsync_SuppliesConfiguredOutputBoundsToProcessBoundary()
    {
        var process = new CapturingProcessRunner();
        var options = Options with
        {
            MaxCliStdoutBytes = 12_345,
            MaxCliStderrBytes = 6_789,
        };
        var runner = new IncusCliRunner(process);

        await runner.RunCheckedAsync(
            "probe",
            options,
            ["incus", "version"],
            stdin: null,
            timeout: null,
            CancellationToken.None);

        Assert.Equal(options.MaxCliStdoutBytes, process.MaxStdoutBytes);
        Assert.Equal(options.MaxCliStderrBytes, process.MaxStderrBytes);
        Assert.True(process.KillOnOutputLimit);
    }

    [Theory]
    [InlineData(null, 1024)]
    [InlineData(1024, null)]
    public async Task IncusProcessBoundary_RejectsMissingOutputBound(
        int? maxStdoutBytes,
        int? maxStderrBytes)
    {
        var runner = new IncusCliProcessRunner();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(
            ["/bin/true"],
            stdin: null,
            CancellationToken.None,
            maxStdoutBytes: maxStdoutBytes,
            maxStderrBytes: maxStderrBytes));

        Assert.Contains("requires explicit stdout and stderr limits", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncusProcessBoundary_RereadsAndValidatesCleanupPolicyPerInvocation()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var options = new IncusSandboxOptions();
        var runner = new IncusCliProcessRunner(() => options);

        var first = await runner.RunAsync(
            ["/bin/true"],
            stdin: null,
            CancellationToken.None,
            maxStdoutBytes: 1024,
            maxStderrBytes: 1024);
        options = options with { CliProcessCleanupTimeout = TimeSpan.Zero };

        Assert.Equal(0, first.ExitCode);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await runner.RunAsync(
                ["/bin/true"],
                stdin: null,
                CancellationToken.None,
                maxStdoutBytes: 1024,
                maxStderrBytes: 1024));
    }

    [Fact]
    public async Task RunCheckedAsync_MapsNonZeroExitToContextualBoundedError()
    {
        var longError = "daemon failed\r\n" + new string('x', 5000);
        var runner = new IncusCliRunner(new StubProcessRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(17, "ignored", longError))));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunCheckedAsync(
                "copy baseline",
                Options,
                ["incus", "copy", "source", "target"],
                stdin: null,
                timeout: null,
                CancellationToken.None));

        Assert.StartsWith(
            "Incus copy baseline failed with exit code 17: daemon failed  ",
            exception.Message,
            StringComparison.Ordinal);
        Assert.EndsWith("...", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', exception.Message);
        Assert.DoesNotContain('\n', exception.Message);
        Assert.True(exception.Message.Length < 4200, "diagnostic must remain bounded");
    }

    [Fact]
    public async Task RunCheckedAsync_MapsDeadlineCancellationToTimeout()
    {
        var time = new ControllableTimeProvider();
        var processStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new IncusCliRunner(new StubProcessRunner(async (_, _, ct) =>
        {
            processStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new ProcessRunResult(0, "", "");
        }), time);

        var operation = runner.RunCheckedAsync(
            "start",
            Options,
            ["incus", "start", "codeybox-test"],
            stdin: null,
            timeout: TimeSpan.FromSeconds(30),
            CancellationToken.None);
        await processStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        time.Advance(TimeSpan.FromSeconds(30));
        var exception = await Assert.ThrowsAsync<IncusTransientTimeoutException>(() =>
            operation.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal("start", exception.Operation);
        Assert.Contains("Incus start exceeded its", exception.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
    }

    [Fact]
    public async Task RunAllowFailureAsync_DeadlineThrowsTransientTimeoutNamingSubcommand()
    {
        var time = new ControllableTimeProvider();
        var processStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new IncusCliRunner(new StubProcessRunner(async (_, _, ct) =>
        {
            processStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new ProcessRunResult(0, "", "");
        }), time);

        var operation = runner.RunAllowFailureAsync(
            Options,
            ["incus", "--project", "codeybox", "exec", "codeybox-test", "--", "/bin/true"],
            stdin: null,
            timeout: TimeSpan.FromSeconds(30),
            CancellationToken.None);
        await processStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        time.Advance(TimeSpan.FromSeconds(30));
        var exception = await Assert.ThrowsAsync<IncusTransientTimeoutException>(() =>
            operation.WaitAsync(TimeSpan.FromSeconds(5)));

        // The derived label names the hung subcommand without leaking argv.
        Assert.Equal("exec", exception.Operation);
        Assert.Contains("[exec] exceeded its", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("codeybox-test", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    // Direct argv (no global prefix).
    [InlineData(new[] { "incus", "start", "codeybox-test" }, "start")]
    [InlineData(new[] { "incus", "file", "push", "/host/x", "codeybox-test/x" }, "file push")]
    [InlineData(new[] { "incus", "config", "device", "add", "codeybox-test" }, "config device")]
    // Real argv from IncusCommandBuilder.Prefix: the global `--project <name>`
    // prefix must be skipped, never mistaken for the subcommand.
    [InlineData(new[] { "incus", "--project", "codeybox", "exec", "codeybox-test", "--", "/bin/true" }, "exec")]
    [InlineData(new[] { "incus", "--project", "codeybox", "start", "codeybox-test" }, "start")]
    [InlineData(new[] { "incus", "--project", "codeybox", "file", "push", "/host/x", "vm/x" }, "file push")]
    // Degenerate / untrusted-looking argv falls back without leaking values.
    [InlineData(new[] { "incus" }, "operation")]
    [InlineData(new[] { "incus", "/absolute/path" }, "operation")]
    [InlineData(new[] { "incus", "Start" }, "operation")]
    [InlineData(new[] { "incus", "--project", "codeybox", "/absolute/path" }, "operation")]
    public void DescribeSubcommand_EmitsOnlyLeadingVerbTokens(string[] argv, string expected)
    {
        Assert.Equal(expected, IncusCliRunner.DescribeSubcommand(argv));
    }

    [Fact]
    public async Task RunCheckedAsync_PreservesCallerCancellation()
    {
        var runner = new IncusCliRunner(new StubProcessRunner((_, _, ct) =>
            Task.FromCanceled<ProcessRunResult>(ct)));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        var exception = await Record.ExceptionAsync(() =>
            runner.RunCheckedAsync(
                "list",
                Options,
                ["incus", "list"],
                stdin: null,
                timeout: TimeSpan.FromMinutes(1),
                canceled.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.IsNotType<TimeoutException>(exception);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RunCheckedAsync_RejectsOutputLimitOverflow(
        bool stdoutLimitExceeded,
        bool stderrLimitExceeded)
    {
        var result = new ProcessRunResult(
            0,
            "bounded stdout",
            "bounded stderr",
            stdoutLimitExceeded,
            stderrLimitExceeded);
        var runner = new IncusCliRunner(new StubProcessRunner((_, _, _) => Task.FromResult(result)));

        var exception = await Assert.ThrowsAsync<IncusOutputBoundException>(() =>
            runner.RunCheckedAsync(
                "list",
                Options,
                ["incus", "list"],
                stdin: null,
                timeout: null,
                CancellationToken.None));

        Assert.Contains("exceeded its configured output bound", exception.Message, StringComparison.Ordinal);
        Assert.Contains("output-bound", exception.Message, StringComparison.Ordinal);
        Assert.Equal("list", exception.Operation);
        Assert.IsAssignableFrom<InvalidOperationException>(exception);
    }

    [Fact]
    public async Task RunCheckedAsync_PreservesUnexpectedRunnerFailureAsCause()
    {
        var cause = new IOException("process table unavailable");
        var runner = new IncusCliRunner(new StubProcessRunner((_, _, _) =>
            Task.FromException<ProcessRunResult>(cause)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunCheckedAsync(
                "version probe",
                Options,
                ["incus", "version"],
                stdin: null,
                timeout: null,
                CancellationToken.None));

        Assert.Same(cause, exception.InnerException);
        Assert.Equal("Incus version probe could not be executed.", exception.Message);
    }

    [Fact]
    public async Task RunAllowFailureAsync_PreservesUnexpectedRunnerFailureAsCause()
    {
        var cause = new IOException("process table unavailable");
        var runner = new IncusCliRunner(new StubProcessRunner((_, _, _) =>
            Task.FromException<ProcessRunResult>(cause)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAllowFailureAsync(
                Options,
                ["incus", "list"],
                stdin: null,
                timeout: null,
                CancellationToken.None));

        Assert.Same(cause, exception.InnerException);
        Assert.Equal("Incus CLI operation could not be executed.", exception.Message);
    }

    [Fact]
    public async Task RunCheckedAsync_DaemonConnectionLoss_WaitsForProbeThenRetries()
    {
        var options = Options with
        {
            DaemonReadyPollInterval = TimeSpan.FromMilliseconds(1),
            DaemonReadyTimeout = TimeSpan.FromSeconds(5),
            DaemonReadyProbeTimeout = TimeSpan.FromSeconds(5),
        };
        var invocations = new List<IReadOnlyList<string>>();
        var runner = new IncusCliRunner(new StubProcessRunner((argv, _, _) =>
        {
            invocations.Add(argv);
            if (argv.Contains("copy", StringComparer.Ordinal) && invocations.Count == 1)
            {
                return Task.FromResult(new ProcessRunResult(
                    1,
                    string.Empty,
                    "Error: Lost connection to the event listener and failed to reconnect: " +
                    "websocket: close 1006 (abnormal closure): unexpected EOF"));
            }
            return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
        }));

        var result = await runner.RunCheckedAsync(
            "copy baseline",
            options,
            ["incus", "copy", "source/ready", "target"],
            stdin: null,
            timeout: null,
            CancellationToken.None,
            allowDaemonConnectivityRetry: true);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(3, invocations.Count);
        Assert.Equal(new[] { "incus", "query", "/1.0" }, invocations[1]);
        Assert.Contains("copy", invocations[2], StringComparer.Ordinal);
    }

    [Fact]
    public async Task RunCheckedAsync_DaemonConnectionLoss_WithoutOptIn_IsNotRetried()
    {
        var invocations = 0;
        var runner = new IncusCliRunner(new StubProcessRunner((_, _, _) =>
        {
            invocations++;
            return Task.FromResult(new ProcessRunResult(
                1,
                string.Empty,
                "Error: Lost connection to the event listener and failed to reconnect: " +
                "websocket: close 1006 (abnormal closure): unexpected EOF"));
        }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunCheckedAsync(
                "copy baseline",
                Options,
                ["incus", "copy", "source/ready", "target"],
                stdin: null,
                timeout: null,
                CancellationToken.None));

        Assert.Contains("Lost connection to the event listener", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, invocations);
    }

    [Fact]
    public async Task RunCheckedAsync_ExecArgv_DaemonConnectionLoss_IsNotRetried()
    {
        var invocations = 0;
        var runner = new IncusCliRunner(new StubProcessRunner((_, _, _) =>
        {
            invocations++;
            return Task.FromResult(new ProcessRunResult(
                1,
                string.Empty,
                "Error: Lost connection to the event listener: websocket: close 1006 (abnormal closure): unexpected EOF"));
        }));

        // Even with the retry flag set, exec argv is never eligible: the
        // guest command may have partially run and guest stderr is merged
        // into the captured stream. The failure is marked exec-scoped and
        // omits guest output so it cannot forge a transient deferral.
        await Assert.ThrowsAsync<IncusExecCommandException>(() =>
            runner.RunCheckedAsync(
                "run provisioning commands",
                Options,
                ["incus", "--project", "codeybox", "exec", "codeybox-test", "--", "/bin/sh", "-s"],
                stdin: null,
                timeout: null,
                CancellationToken.None,
                allowDaemonConnectivityRetry: true));

        Assert.Equal(1, invocations);
    }

    [Fact]
    public async Task RunCheckedAsync_ExecArgv_FailureDerivesFromInvalidOperation()
    {
        var runner = new IncusCliRunner(new StubProcessRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(1, string.Empty, "guest boom"))));

        var exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            runner.RunCheckedAsync(
                "run provisioning commands",
                Options,
                ["incus", "exec", "codeybox-test", "--", "/bin/false"],
                stdin: null,
                timeout: null,
                CancellationToken.None));

        Assert.IsType<IncusExecCommandException>(exception);
    }

    [Fact]
    public async Task RunCheckedAsync_ExecArgv_WithholdsGuestStderrFromMessage()
    {
        // The CLI merges guest-process stderr into the captured stream, so a
        // guest printing a daemon-loss signature must not reach the message
        // classifier through the checked-exec failure prose.
        const string guestForgery =
            "Error: Lost connection to the event listener: websocket: close 1006 (abnormal closure): unexpected EOF";
        var runner = new IncusCliRunner(new StubProcessRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(1, string.Empty, "guest output\n" + guestForgery))));

        var exception = await Assert.ThrowsAsync<IncusExecCommandException>(() =>
            runner.RunCheckedAsync(
                "run provisioning commands",
                Options,
                ["incus", "--project", "codeybox", "exec", "codeybox-test", "--", "/bin/sh", "-s"],
                stdin: null,
                timeout: null,
                CancellationToken.None,
                allowDaemonConnectivityRetry: true));

        Assert.DoesNotContain("Lost connection", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("guest output", exception.Message, StringComparison.Ordinal);
        Assert.Contains("exit code 1", exception.Message, StringComparison.Ordinal);
        Assert.Null(IncusTransientInfrastructure.TryClassify(
            exception,
            IncusTransientInfrastructure.DefaultSignatures));
    }

    [Fact]
    public async Task RunCheckedAsync_DaemonRetry_PrepareRunsBeforeRetry()
    {
        var options = Options with
        {
            DaemonReadyPollInterval = TimeSpan.FromMilliseconds(1),
            DaemonReadyTimeout = TimeSpan.FromSeconds(5),
            DaemonReadyProbeTimeout = TimeSpan.FromSeconds(5),
        };
        var events = new List<string>();
        var runner = new IncusCliRunner(new StubProcessRunner((argv, _, _) =>
        {
            events.Add(argv.Contains("copy", StringComparer.Ordinal) ? "copy" : "probe");
            if (events.Count == 1)
            {
                return Task.FromResult(new ProcessRunResult(
                    1,
                    string.Empty,
                    "Error: Lost connection to the event listener and failed to reconnect: " +
                    "websocket: close 1006 (abnormal closure): unexpected EOF"));
            }
            return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
        }));

        var result = await runner.RunCheckedAsync(
            "copy baseline",
            options,
            ["incus", "copy", "source/ready", "target"],
            stdin: null,
            timeout: null,
            CancellationToken.None,
            allowDaemonConnectivityRetry: true,
            prepareForDaemonRetryAsync: _ =>
            {
                events.Add("prepare");
                return Task.FromResult(true);
            });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["copy", "probe", "prepare", "copy"], events);
    }

    [Fact]
    public async Task RunCheckedAsync_DaemonRetry_PrepareCannotProveAbsence_SurfacesOriginalFailure()
    {
        var options = Options with
        {
            DaemonReadyPollInterval = TimeSpan.FromMilliseconds(1),
            DaemonReadyTimeout = TimeSpan.FromSeconds(5),
            DaemonReadyProbeTimeout = TimeSpan.FromSeconds(5),
        };
        var copyAttempts = 0;
        var runner = new IncusCliRunner(new StubProcessRunner((argv, _, _) =>
        {
            if (argv.Contains("copy", StringComparer.Ordinal))
            {
                copyAttempts++;
                return Task.FromResult(new ProcessRunResult(
                    1,
                    string.Empty,
                    "Error: Lost connection to the event listener and failed to reconnect: " +
                    "websocket: close 1006 (abnormal closure): unexpected EOF"));
            }
            return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
        }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunCheckedAsync(
                "copy baseline",
                options,
                ["incus", "copy", "source/ready", "target"],
                stdin: null,
                timeout: null,
                CancellationToken.None,
                allowDaemonConnectivityRetry: true,
                prepareForDaemonRetryAsync: _ => Task.FromResult(false)));

        // The original transient failure surfaces — never a retry into a
        // non-transient "already exists".
        Assert.Contains("Lost connection to the event listener", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, copyAttempts);
    }

    [Fact]
    public async Task RunCheckedAsync_DaemonNeverReady_SurfacesOriginalFailure()
    {
        var time = new ControllableTimeProvider();
        var options = Options with
        {
            DaemonUnavailableRetryAttempts = 1,
            DaemonReadyTimeout = TimeSpan.FromMilliseconds(40),
            DaemonReadyPollInterval = TimeSpan.FromMilliseconds(5),
            DaemonReadyProbeTimeout = TimeSpan.FromMilliseconds(10),
        };
        var copyAttempts = 0;
        var probeAttempts = 0;
        var runner = new IncusCliRunner(new StubProcessRunner((argv, _, _) =>
        {
            if (argv.Contains("copy", StringComparer.Ordinal))
            {
                copyAttempts++;
                return Task.FromResult(new ProcessRunResult(
                    1,
                    string.Empty,
                    "Error: Lost connection to the event listener and failed to reconnect: " +
                    "websocket: close 1006 (abnormal closure): unexpected EOF"));
            }
            probeAttempts++;
            return Task.FromResult(new ProcessRunResult(1, string.Empty, "connection refused"));
        }), time);

        var operation = runner.RunCheckedAsync(
            "copy baseline",
            options,
            ["incus", "copy", "source/ready", "target"],
            stdin: null,
            timeout: null,
            CancellationToken.None,
            allowDaemonConnectivityRetry: true);

        // Drive the fake clock until the bounded readiness wait expires;
        // wall-clock scheduling can never starve the poll-count assertion.
        var pumpDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!operation.IsCompleted)
        {
            Assert.True(DateTime.UtcNow < pumpDeadline, "the bounded readiness wait did not complete");
            time.Advance(TimeSpan.FromMilliseconds(10));
            await Task.Delay(1);
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
        Assert.Contains("Lost connection to the event listener", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, copyAttempts);
        Assert.True(probeAttempts >= 2, "the bounded readiness wait must poll more than once");
    }

    [Fact]
    public async Task RunCheckedAsync_NonDaemonFailure_IsNotRetried()
    {
        var invocations = 0;
        var runner = new IncusCliRunner(new StubProcessRunner((_, _, _) =>
        {
            invocations++;
            return Task.FromResult(new ProcessRunResult(1, string.Empty, "Error: instance already exists"));
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunCheckedAsync(
                "copy baseline",
                Options,
                ["incus", "copy", "source/ready", "target"],
                stdin: null,
                timeout: null,
                CancellationToken.None));

        Assert.Equal(1, invocations);
    }

    [Fact]
    public async Task RunAllowFailureAsync_DaemonConnectionLoss_ReportsExecutionUnavailable()
    {
        var runner = new IncusCliRunner(new StubProcessRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(
                1,
                string.Empty,
                "Error: Lost connection to the event listener: websocket: close 1006 (abnormal closure): unexpected EOF"))));

        var result = await runner.RunAllowFailureAsync(
            Options,
            ["incus", "file", "pull", "codeybox-test/x", "/host/x"],
            stdin: null,
            timeout: null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.ExecutionUnavailable);
    }

    [Fact]
    public async Task RunAllowFailureAsync_ExecArgv_DaemonSignatureInGuestStderr_StaysDefinitive()
    {
        // `incus exec` merges guest-process stderr into the captured stream,
        // so a guest printing a daemon-loss signature must never forge
        // transport loss.
        var runner = new IncusCliRunner(new StubProcessRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(
                1,
                string.Empty,
                "agent output\nwebsocket: close 1006 (abnormal closure): unexpected EOF"))));

        var result = await runner.RunAllowFailureAsync(
            Options,
            ["incus", "--project", "codeybox", "exec", "codeybox-test", "--", "/bin/false"],
            stdin: null,
            timeout: null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.ExecutionUnavailable);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task RunAllowFailureAsync_ExecArgv_GenuineCliDaemonLoss_AlsoStaysDefinitive()
    {
        // The CLI's own daemon-loss diagnostic is indistinguishable from
        // guest output on the merged stderr stream, so exec argv never gets
        // the ExecutionUnavailable rewrite; the guest completion sentinel
        // remains the ambiguity oracle for exec outcomes.
        var runner = new IncusCliRunner(new StubProcessRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(
                1,
                string.Empty,
                "Error: Lost connection to the event listener: websocket: close 1006 (abnormal closure): unexpected EOF"))));

        var result = await runner.RunAllowFailureAsync(
            Options,
            ["incus", "exec", "codeybox-test", "--", "/bin/true"],
            stdin: null,
            timeout: null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.ExecutionUnavailable);
    }

    [Fact]
    public async Task RunAllowFailureAsync_NonDaemonFailure_KeepsDefinitiveResult()
    {
        var runner = new IncusCliRunner(new StubProcessRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(3, string.Empty, "Error: command exited 3"))));

        var result = await runner.RunAllowFailureAsync(
            Options,
            ["incus", "exec", "codeybox-test", "--", "/bin/false"],
            stdin: null,
            timeout: null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.ExecutionUnavailable);
        Assert.Equal(3, result.ExitCode);
    }

    private sealed class StubProcessRunner(
        Func<IReadOnlyList<string>, string?, CancellationToken, Task<ProcessRunResult>> handler)
        : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(
            IReadOnlyList<string> argv,
            string? stdin,
            CancellationToken ct,
            Action<string>? stdoutChunkCallback = null,
            Action<string>? stderrChunkCallback = null,
            int? maxStdoutBytes = null,
            int? maxStderrBytes = null,
            IReadOnlyDictionary<string, string>? environment = null,
            bool killOnOutputLimit = true,
            string? workingDirectory = null)
            => handler(argv, stdin, ct);
    }

    private sealed class CapturingProcessRunner : IProcessRunner
    {
        internal int? MaxStdoutBytes { get; private set; }
        internal int? MaxStderrBytes { get; private set; }
        internal bool KillOnOutputLimit { get; private set; }

        public Task<ProcessRunResult> RunAsync(
            IReadOnlyList<string> argv,
            string? stdin,
            CancellationToken ct,
            Action<string>? stdoutChunkCallback = null,
            Action<string>? stderrChunkCallback = null,
            int? maxStdoutBytes = null,
            int? maxStderrBytes = null,
            IReadOnlyDictionary<string, string>? environment = null,
            bool killOnOutputLimit = true,
            string? workingDirectory = null)
        {
            MaxStdoutBytes = maxStdoutBytes;
            MaxStderrBytes = maxStderrBytes;
            KillOnOutputLimit = killOnOutputLimit;
            return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
        }
    }
}
