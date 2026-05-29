using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox.Multipass;
using Serilog;
using Serilog.Events;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="MultipassDaemonRetry"/>: classifier branches,
/// retry/probe loop control flow, cancellation, and audit emission.
///
/// These tests pin the contract documented in the bug report — the
/// classifier must recognise each transient-signal family, the retry
/// loop must back off and probe between attempts, non-retryable
/// commands must fail fast, and a cancelled CT must abort the loop
/// instead of running the full backoff schedule.
/// </summary>
public sealed class MultipassDaemonRetryTests
{
    private static IReadOnlyList<string> Argv(string command, params string[] rest) =>
        ["/usr/bin/multipass", command, .. rest];

    private static Task<MultipassDaemonHealthProbeResult> Healthy(CancellationToken _) =>
        Task.FromResult(MultipassDaemonHealthProbeResult.Healthy());

    private static MultipassDaemonRetryPolicy InstantPolicy() => new()
    {
        Delay = (_, _) => Task.CompletedTask,
    };

    // ────────────────────────────────────────────────────────────────────
    // ClassifyTransient: each branch and its negative
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void ClassifyTransient_ReturnsNull_OnSuccessfulExit()
    {
        Assert.Null(MultipassDaemonRetry.ClassifyTransient(
            Argv("launch"),
            new ProcessRunResult(0, "", "cannot connect to the multipass socket")));
    }

    [Fact]
    public void ClassifyTransient_ReturnsNull_ForNonRetryableCommand_EvenWithSocketStderr()
    {
        // 'stop', 'delete', 'transfer', 'list' must fail fast even when the
        // stderr looks transient — they're outside RetryableCommands and
        // running them again may have side effects.
        foreach (var nonRetryable in new[] { "stop", "delete", "transfer", "list", "purge" })
        {
            var classification = MultipassDaemonRetry.ClassifyTransient(
                Argv(nonRetryable, "codeybox-x"),
                new ProcessRunResult(1, "", "cannot connect to the multipass socket"));
            Assert.Null(classification);
        }
    }

    [Fact]
    public void ClassifyTransient_DetectsQemuProcessCrashed()
    {
        var classification = MultipassDaemonRetry.ClassifyTransient(
            Argv("start", "codeybox-x"),
            new ProcessRunResult(1, "", "multipassd: qemu-system-x86_64; error: Process crashed"));
        Assert.Equal("qemu-process-crashed", classification);
    }

    [Fact]
    public void ClassifyTransient_DetectsMultipassSocketUnreachable()
    {
        var classification = MultipassDaemonRetry.ClassifyTransient(
            Argv("launch", "--name", "codeybox-x"),
            new ProcessRunResult(1, "", "launch failed: cannot connect to the multipass socket"));
        Assert.Equal("multipass-socket-unreachable", classification);
    }

    [Fact]
    public void ClassifyTransient_DetectsMultipassDaemonUnreachable_OnLaunchOrStart()
    {
        // 'cannot connect to' without the 'multipass socket' suffix — the
        // launch/start-only branch that catches generic daemon disconnects.
        foreach (var command in new[] { "launch", "start" })
        {
            var classification = MultipassDaemonRetry.ClassifyTransient(
                Argv(command, "codeybox-x"),
                new ProcessRunResult(1, "", "cannot connect to multipassd at /run/multipass_socket.sock"));
            Assert.Equal("multipass-daemon-unreachable", classification);
        }
    }

    [Fact]
    public void ClassifyTransient_DoesNotMatchDaemonUnreachable_OnExecOrInfo()
    {
        // The "cannot connect to" (no 'socket' substring) branch is gated
        // to launch/start — exec/info/clone/mount must NOT match it.
        foreach (var command in new[] { "exec", "info", "clone", "mount" })
        {
            var classification = MultipassDaemonRetry.ClassifyTransient(
                Argv(command, "codeybox-x"),
                new ProcessRunResult(1, "", "cannot connect to api.example"));
            Assert.Null(classification);
        }
    }

    [Fact]
    public void ClassifyTransient_FallsBackToSocketErrorClass()
    {
        // Hits the generic-'socket' final branch — stderr mentions socket
        // but doesn't match the more specific multipass-socket-unreachable
        // wording.
        var classification = MultipassDaemonRetry.ClassifyTransient(
            Argv("exec", "codeybox-x"),
            new ProcessRunResult(1, "", "exec failed: snap.multipass socket disappeared"));
        Assert.Equal("multipass-socket-error", classification);
    }

    [Fact]
    public void ClassifyTransient_ReturnsNull_ForNonTransientFailure()
    {
        // Image-not-found, auth, etc. — these MUST fail fast.
        var classification = MultipassDaemonRetry.ClassifyTransient(
            Argv("launch", "--name", "codeybox-x"),
            new ProcessRunResult(2, "", "image 'bogus' not found in remote 'release'"));
        Assert.Null(classification);
    }

    [Fact]
    public void ClassifyTransient_DetectsInstanceLockContention()
    {
        var classification = MultipassDaemonRetry.ClassifyTransient(
            Argv("clone", "baseline-xxx", "--name", "codeybox-y"),
            new ProcessRunResult(1, "", "clone failed: Could not acquire lock for '/var/snap/multipass/common/data/multipassd/multipassd-vm-instances.json'"));
        Assert.Equal("multipass-instance-lock-contention", classification);
    }

    [Fact]
    public void ClassifyTransient_DetectsInstanceLockContention_OnLaunch()
    {
        var classification = MultipassDaemonRetry.ClassifyTransient(
            Argv("launch", "--name", "codeybox-x"),
            new ProcessRunResult(1, "", "launch failed: Could not acquire lock for '/var/snap/multipass/common/data/multipassd/multipassd-vm-instances.json'"));
        Assert.Equal("multipass-instance-lock-contention", classification);
    }

    [Fact]
    public void ClassifyTransient_LockContention_RequiresVmInstancesPath()
    {
        var classification = MultipassDaemonRetry.ClassifyTransient(
            Argv("clone", "baseline-xxx", "--name", "codeybox-y"),
            new ProcessRunResult(1, "", "Could not acquire lock for '/tmp/other.lock'"));
        Assert.Null(classification);
    }

    // ────────────────────────────────────────────────────────────────────
    // RunWithRetryAsync: retry & audit & log behaviour
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunWithRetry_ReturnsImmediately_OnNonTransientFailure()
    {
        // image-not-found is non-transient. The retry layer must NOT loop;
        // a single attempt should be made even though we're calling 'launch'.
        var attempts = 0;
        var argv = Argv("launch", "--name", "codeybox-x");
        var result = await MultipassDaemonRetry.RunWithRetryAsync(
            argv,
            _ =>
            {
                attempts++;
                return Task.FromResult(new ProcessRunResult(2, "", "image not found"));
            },
            Healthy,
            NullLogger.Instance,
            WorkItemId.New(),
            CancellationToken.None,
            InstantPolicy());

        Assert.Equal(1, attempts);
        Assert.Equal("image not found", result.Stderr);
    }

    [Fact]
    public async Task RunWithRetry_ExhaustsRetries_WhenAllAttemptsAreQemuCrashes()
    {
        // Covers the qemu-process-crashed branch end-to-end and confirms
        // the exhausted-retries final message includes the error class.
        var attempts = 0;
        var argv = Argv("start", "codeybox-x");
        var result = await MultipassDaemonRetry.RunWithRetryAsync(
            argv,
            _ =>
            {
                attempts++;
                return Task.FromResult(new ProcessRunResult(
                    1, "", "qemu-system-x86_64; error: Process crashed"));
            },
            ct => Task.FromResult(MultipassDaemonHealthProbeResult.Unhealthy("daemon down")),
            NullLogger.Instance,
            WorkItemId.New(),
            CancellationToken.None,
            InstantPolicy());

        Assert.Equal(3, attempts);
        Assert.Contains("multipass daemon unreachable after 2 retries", result.Stderr);
        Assert.Contains("qemu-process-crashed", result.Stderr);
    }

    [Fact]
    public async Task RunWithRetry_FinalMessage_DifferentiatesHealthyDaemonFromUnreachable()
    {
        // When the daemon recovers between retries but the transient signal
        // keeps reappearing, the final message must use the "transient
        // daemon error" wording, not "daemon unreachable".
        var argv = Argv("launch", "--name", "codeybox-x");
        var result = await MultipassDaemonRetry.RunWithRetryAsync(
            argv,
            _ => Task.FromResult(new ProcessRunResult(1, "", "cannot connect to the multipass socket")),
            Healthy,
            NullLogger.Instance,
            WorkItemId.New(),
            CancellationToken.None,
            InstantPolicy());

        Assert.Contains("multipass transient daemon error after 2 retries", result.Stderr);
        Assert.DoesNotContain("daemon unreachable", result.Stderr);
    }

    [Fact]
    public async Task RunWithRetry_AppliesConfiguredBackoffsInOrder()
    {
        var argv = Argv("launch", "--name", "codeybox-x");
        var delays = new List<TimeSpan>();
        var policy = new MultipassDaemonRetryPolicy
        {
            MaxAttempts = 3,
            Backoffs = [TimeSpan.FromMilliseconds(7), TimeSpan.FromMilliseconds(13)],
            Delay = (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            },
        };

        await MultipassDaemonRetry.RunWithRetryAsync(
            argv,
            _ => Task.FromResult(new ProcessRunResult(1, "", "cannot connect to the multipass socket")),
            Healthy,
            NullLogger.Instance,
            WorkItemId.New(),
            CancellationToken.None,
            policy);

        Assert.Equal(
            [TimeSpan.FromMilliseconds(7), TimeSpan.FromMilliseconds(13)],
            delays);
    }

    [Fact]
    public async Task RunWithRetry_CloneRetriesLockContention_ThenSucceeds()
    {
        var argv = Argv("clone", "baseline-xxx", "--name", "codeybox-y");
        var attempts = 0;

        var result = await MultipassDaemonRetry.RunWithRetryAsync(
            argv,
            _ =>
            {
                attempts++;
                if (attempts == 1)
                    return Task.FromResult(new ProcessRunResult(
                        1, "", "clone failed: Could not acquire lock for '/var/snap/multipass/common/data/multipassd/multipassd-vm-instances.json'"));
                return Task.FromResult(new ProcessRunResult(0, "done", ""));
            },
            Healthy,
            NullLogger.Instance,
            WorkItemId.New(),
            CancellationToken.None,
            InstantPolicy());

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("done", result.Stdout);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task RunWithRetry_LaunchRetriesLockContention_ExhaustsThenSurfaces()
    {
        var argv = Argv("launch", "--name", "codeybox-x");
        var stderr = "launch failed: Could not acquire lock for '/var/snap/multipass/common/data/multipassd/multipassd-vm-instances.json'";

        var result = await MultipassDaemonRetry.RunWithRetryAsync(
            argv,
            _ => Task.FromResult(new ProcessRunResult(1, "", stderr)),
            ct => Task.FromResult(MultipassDaemonHealthProbeResult.Unhealthy("daemon stalled")),
            NullLogger.Instance,
            WorkItemId.New(),
            CancellationToken.None,
            InstantPolicy());

        Assert.Contains("multipass daemon unreachable after 2 retries", result.Stderr);
        Assert.Contains("multipass-instance-lock-contention", result.Stderr);
    }

    [Fact]
    public async Task RunWithRetry_LogsInfoThenWarning_AcrossRetries()
    {
        var logger = new RecordingLogger();
        var argv = Argv("launch", "--name", "codeybox-x");

        await MultipassDaemonRetry.RunWithRetryAsync(
            argv,
            _ => Task.FromResult(new ProcessRunResult(1, "", "cannot connect to the multipass socket")),
            Healthy,
            logger,
            WorkItemId.New(),
            CancellationToken.None,
            InstantPolicy());

        // First retry → INF; second retry → WRN; exhaustion → ERR.
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Information);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task RunWithRetry_HonoursCancellation_BeforeBackoffElapses()
    {
        // Spec: 'honour the CT — if shutdown is in progress, abort the
        // retry loop and propagate cancellation cleanly.' We trip the
        // token inside policy.Delay; the next ThrowIfCancellationRequested
        // at the top of the loop should rethrow OCE.
        using var cts = new CancellationTokenSource();
        var attempts = 0;
        var argv = Argv("launch", "--name", "codeybox-x");
        var policy = new MultipassDaemonRetryPolicy
        {
            Delay = (_, _) =>
            {
                cts.Cancel();
                return Task.CompletedTask;
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MultipassDaemonRetry.RunWithRetryAsync(
                argv,
                _ =>
                {
                    attempts++;
                    return Task.FromResult(new ProcessRunResult(
                        1, "", "cannot connect to the multipass socket"));
                },
                Healthy,
                NullLogger.Instance,
                WorkItemId.New(),
                cts.Token,
                policy));

        // Exactly one attempt should have run before cancellation aborted
        // the loop — never the full 3.
        Assert.Equal(1, attempts);
    }

    // ────────────────────────────────────────────────────────────────────
    // RunWithRetryAsync: guard clauses
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunWithRetry_RejectsZeroMaxAttempts()
    {
        var policy = new MultipassDaemonRetryPolicy { MaxAttempts = 0 };
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            MultipassDaemonRetry.RunWithRetryAsync(
                Argv("launch"),
                _ => Task.FromResult(new ProcessRunResult(0, "", "")),
                Healthy,
                NullLogger.Instance,
                WorkItemId.New(),
                CancellationToken.None,
                policy));
    }

    [Fact]
    public async Task RunWithRetry_RejectsBackoffsShorterThanRetryCount()
    {
        // MaxAttempts=3 requires Backoffs.Count >= 2.
        var policy = new MultipassDaemonRetryPolicy
        {
            MaxAttempts = 3,
            Backoffs = [TimeSpan.FromMilliseconds(1)],
        };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            MultipassDaemonRetry.RunWithRetryAsync(
                Argv("launch"),
                _ => Task.FromResult(new ProcessRunResult(0, "", "")),
                Healthy,
                NullLogger.Instance,
                WorkItemId.New(),
                CancellationToken.None,
                policy));
    }

    // ────────────────────────────────────────────────────────────────────
    // ProbeDaemonAsync: each branch
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProbeDaemon_ReturnsHealthy_OnZeroExit()
    {
        var runner = new StubProcessRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, "multipass 1.15.0", "")));
        var result = await MultipassDaemonRetry.ProbeDaemonAsync(
            runner, "multipass", TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(result.IsHealthy);
    }

    [Fact]
    public async Task ProbeDaemon_ReturnsUnhealthy_OnNonZeroExit()
    {
        var runner = new StubProcessRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(1, "", "boom")));
        var result = await MultipassDaemonRetry.ProbeDaemonAsync(
            runner, "multipass", TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.False(result.IsHealthy);
        Assert.Contains("multipass version failed (exit 1)", result.Error);
        Assert.Contains("boom", result.Error);
    }

    [Fact]
    public async Task ProbeDaemon_ReturnsTimeoutUnhealthy_WhenRunnerExceedsDeadline()
    {
        // Runner cooperates with the probe's linked CT — when CancelAfter
        // fires, Task.Delay throws OCE with the timeout token (not the
        // caller's ct), which the probe must classify as "timed out".
        var runner = new StubProcessRunner(async (_, _, runnerCt) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), runnerCt);
            return new ProcessRunResult(0, "", "");
        });
        var result = await MultipassDaemonRetry.ProbeDaemonAsync(
            runner, "multipass", TimeSpan.FromMilliseconds(5), CancellationToken.None);
        Assert.False(result.IsHealthy);
        Assert.Contains("timed out", result.Error);
    }

    [Fact]
    public async Task ProbeDaemon_PropagatesCallerCancellation()
    {
        // The caller's ct is already cancelled when the probe starts: the
        // OCE-when-ct-cancelled branch must rethrow instead of returning
        // an Unhealthy "timed out" result.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var runner = new StubProcessRunner(async (_, _, runnerCt) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), runnerCt);
            return new ProcessRunResult(0, "", "");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MultipassDaemonRetry.ProbeDaemonAsync(
                runner, "multipass", TimeSpan.FromSeconds(5), cts.Token));
    }

    [Fact]
    public async Task ProbeDaemon_CapturesGenericException()
    {
        // Runner throws something that isn't OperationCanceledException —
        // the fallback catch must surface the exception type and message
        // in the Unhealthy error so an operator can diagnose it.
        var runner = new StubProcessRunner((_, _, _) =>
            throw new InvalidOperationException("multipass binary missing"));
        var result = await MultipassDaemonRetry.ProbeDaemonAsync(
            runner, "multipass", TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.False(result.IsHealthy);
        Assert.Contains("InvalidOperationException", result.Error);
        Assert.Contains("multipass binary missing", result.Error);
    }

    private sealed class StubProcessRunner : IProcessRunner
    {
        private readonly Func<IReadOnlyList<string>, string?, CancellationToken, Task<ProcessRunResult>> _handler;

        public StubProcessRunner(
            Func<IReadOnlyList<string>, string?, CancellationToken, Task<ProcessRunResult>> handler)
        {
            _handler = handler;
        }

        public Task<ProcessRunResult> RunAsync(
            IReadOnlyList<string> argv,
            string? stdin,
            CancellationToken ct,
            Action<string>? stdoutChunkCallback = null,
            Action<string>? stderrChunkCallback = null,
            int? maxStdoutBytes = null,
            int? maxStderrBytes = null,
            IReadOnlyDictionary<string, string>? environment = null) =>
            _handler(argv, stdin, ct);
    }

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();
            public void Dispose() { }
        }
    }
}

/// <summary>
/// Pins the AuditLog.SandboxLaunchTransientRetry emission on retry to a real
/// Serilog sink. The unit-only tests above use the Microsoft.Extensions.Logging
/// ILogger which is independent of Serilog's static <c>Log.Logger</c>, so the
/// surface contract — the audit pipeline ACTUALLY fires for each retry with
/// the correct workItemId/attempt/errorClass — is only verified here.
///
/// Wired into the GlobalSerilog collection because it mutates the static
/// Serilog logger that other tests also touch.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class MultipassDaemonRetryAuditTests : IDisposable
{
    private readonly TestSink _sink = new();

    public MultipassDaemonRetryAuditTests()
    {
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .Enrich.With<SensitiveDataRedactionEnricher>()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    public void Dispose() => Log.CloseAndFlush();

    private static IReadOnlyList<string> Argv(string command, params string[] rest) =>
        ["/usr/bin/multipass", command, .. rest];

    private static Task<MultipassDaemonHealthProbeResult> Healthy(CancellationToken _) =>
        Task.FromResult(MultipassDaemonHealthProbeResult.Healthy());

    private static MultipassDaemonRetryPolicy InstantPolicy() => new()
    {
        Delay = (_, _) => Task.CompletedTask,
    };

    [Fact]
    public async Task SandboxLaunchTransientRetry_FiresForEachRetry_WithAttemptAndErrorClass()
    {
        var workItemId = WorkItemId.New();
        await MultipassDaemonRetry.RunWithRetryAsync(
            Argv("launch", "--name", "codeybox-x"),
            _ => Task.FromResult(new ProcessRunResult(1, "", "cannot connect to the multipass socket")),
            Healthy,
            NullLogger.Instance,
            workItemId,
            CancellationToken.None,
            InstantPolicy());

        var retryEvents = _sink.Events
            .Where(e => GetScalar<string>(e, "EventName") == "sandbox.launch_transient_retry")
            .ToList();

        // Two retries (between attempts 1→2 and 2→3); none after the final
        // failed attempt because the retry loop breaks before auditing.
        Assert.Equal(2, retryEvents.Count);

        Assert.Equal(workItemId.ToString(), GetScalar<string>(retryEvents[0], "WorkItemId"));
        Assert.Equal(1, GetScalar<int>(retryEvents[0], "Attempt"));
        Assert.Equal("multipass-socket-unreachable", GetScalar<string>(retryEvents[0], "ErrorClass"));

        Assert.Equal(workItemId.ToString(), GetScalar<string>(retryEvents[1], "WorkItemId"));
        Assert.Equal(2, GetScalar<int>(retryEvents[1], "Attempt"));
        Assert.Equal("multipass-socket-unreachable", GetScalar<string>(retryEvents[1], "ErrorClass"));
    }

    [Fact]
    public async Task SandboxLaunchTransientRetry_DoesNotFire_OnFirstAttemptSuccess()
    {
        // No transient classification → return path bypasses the audit
        // emission entirely. Guards against an accidental call before the
        // null-check on errorClass.
        await MultipassDaemonRetry.RunWithRetryAsync(
            Argv("launch", "--name", "codeybox-x"),
            _ => Task.FromResult(new ProcessRunResult(0, "ok", "")),
            Healthy,
            NullLogger.Instance,
            WorkItemId.New(),
            CancellationToken.None,
            InstantPolicy());

        Assert.DoesNotContain(_sink.Events, e =>
            GetScalar<string>(e, "EventName") == "sandbox.launch_transient_retry");
    }

    [Fact]
    public async Task SandboxLaunchTransientRetry_DoesNotFire_WhenWorkItemIdIsNull()
    {
        // Internal/maintenance callers (e.g. leak reaper) pass workItemId=null
        // so the audit emission is suppressed but the retry still runs.
        await MultipassDaemonRetry.RunWithRetryAsync(
            Argv("launch", "--name", "codeybox-x"),
            _ => Task.FromResult(new ProcessRunResult(1, "", "cannot connect to the multipass socket")),
            Healthy,
            NullLogger.Instance,
            workItemId: null,
            CancellationToken.None,
            InstantPolicy());

        Assert.DoesNotContain(_sink.Events, e =>
            GetScalar<string>(e, "EventName") == "sandbox.launch_transient_retry");
    }

    private static T? GetScalar<T>(LogEvent evt, string key)
    {
        if (!evt.Properties.TryGetValue(key, out var prop) || prop is not ScalarValue sv)
            return default;
        if (sv.Value is T t)
            return t;
        if (typeof(T) == typeof(int) && sv.Value is long l)
            return (T)(object)(int)l;
        return default;
    }
}
