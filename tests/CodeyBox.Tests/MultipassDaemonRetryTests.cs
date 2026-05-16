using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Sandbox.Multipass;

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
            new RunResult(0, "", "cannot connect to the multipass socket")));
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
                new RunResult(1, "", "cannot connect to the multipass socket"));
            Assert.Null(classification);
        }
    }

    [Fact]
    public void ClassifyTransient_DetectsQemuProcessCrashed()
    {
        var classification = MultipassDaemonRetry.ClassifyTransient(
            Argv("start", "codeybox-x"),
            new RunResult(1, "", "multipassd: qemu-system-x86_64; error: Process crashed"));
        Assert.Equal("qemu-process-crashed", classification);
    }

    [Fact]
    public void ClassifyTransient_DetectsMultipassSocketUnreachable()
    {
        var classification = MultipassDaemonRetry.ClassifyTransient(
            Argv("launch", "--name", "codeybox-x"),
            new RunResult(1, "", "launch failed: cannot connect to the multipass socket"));
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
                new RunResult(1, "", "cannot connect to multipassd at /run/multipass_socket.sock"));
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
                new RunResult(1, "", "cannot connect to api.example"));
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
            new RunResult(1, "", "exec failed: snap.multipass socket disappeared"));
        Assert.Equal("multipass-socket-error", classification);
    }

    [Fact]
    public void ClassifyTransient_ReturnsNull_ForNonTransientFailure()
    {
        // Image-not-found, auth, etc. — these MUST fail fast.
        var classification = MultipassDaemonRetry.ClassifyTransient(
            Argv("launch", "--name", "codeybox-x"),
            new RunResult(2, "", "image 'bogus' not found in remote 'release'"));
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
                return Task.FromResult(new RunResult(2, "", "image not found"));
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
                return Task.FromResult(new RunResult(
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
            _ => Task.FromResult(new RunResult(1, "", "cannot connect to the multipass socket")),
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
            _ => Task.FromResult(new RunResult(1, "", "cannot connect to the multipass socket")),
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
    public async Task RunWithRetry_LogsInfoThenWarning_AcrossRetries()
    {
        var logger = new RecordingLogger();
        var argv = Argv("launch", "--name", "codeybox-x");

        await MultipassDaemonRetry.RunWithRetryAsync(
            argv,
            _ => Task.FromResult(new RunResult(1, "", "cannot connect to the multipass socket")),
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
                    return Task.FromResult(new RunResult(
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

    private sealed class RecordingLogger : ILogger
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
