using System.Globalization;
using System.Text;
using System.Collections.Concurrent;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox.Incus;

namespace CodeyBox.Tests;

public sealed class DefaultProcessRunnerCancellationTests
{
    [Fact]
    public void Constructor_RejectsInvalidCleanupTimingPolicy()
    {
        var invalid = new[]
        {
            new DefaultProcessRunnerOptions { CleanupTimeout = TimeSpan.Zero },
            new DefaultProcessRunnerOptions { CleanupTimeout = TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(1) },
            new DefaultProcessRunnerOptions { ProcessGroupExitPollInterval = TimeSpan.Zero },
            new DefaultProcessRunnerOptions { ProcessGroupExitPollInterval = TimeSpan.FromSeconds(1) + TimeSpan.FromTicks(1) },
            new DefaultProcessRunnerOptions
            {
                CleanupTimeout = TimeSpan.FromMilliseconds(5),
                ProcessGroupExitPollInterval = TimeSpan.FromMilliseconds(6),
            },
        };

        foreach (var options in invalid)
            Assert.Throws<ArgumentOutOfRangeException>(() => new DefaultProcessRunner(options));
    }

    [Fact]
    public async Task CancellationCleanupSchedulesConfiguredDeadlineOnInjectedClock()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var time = new RecordingTimeProvider();
        var transcript = new PidTranscript();
        using var cancellation = new CancellationTokenSource();
        var runner = new DefaultProcessRunner(
            new DefaultProcessRunnerOptions
            {
                IsolateLinuxProcessGroup = true,
                CleanupTimeout = TimeSpan.FromSeconds(17),
                ProcessGroupExitPollInterval = TimeSpan.FromMilliseconds(25),
            },
            time);
        var run = runner.RunAsync(
            ["/bin/sh", "-c", "printf 'root=%s\\n' $$; sleep 30"],
            stdin: null,
            cancellation.Token,
            stdoutChunkCallback: transcript.Append,
            maxStdoutBytes: 4096,
            maxStderrBytes: 4096);
        _ = await transcript.RootPid.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            run.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains(TimeSpan.FromSeconds(17), time.TimerDueTimes);
    }

    [Fact]
    public async Task DefaultRunner_DoesNotCreateNewLinuxSession()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var runner = new DefaultProcessRunner();

        var result = await runner.RunAsync(
            [
                "/bin/sh", "-c",
                "printf '%s\\n' \"$$\"; read -r pid comm state ppid pgrp sid rest < \"/proc/$$/stat\"; printf '%s\\n' \"$sid\"",
            ],
            stdin: null,
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        var identifiers = result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture))
            .ToArray();
        Assert.Equal(2, identifiers.Length);
        Assert.NotEqual(identifiers[0], identifiers[1]);
    }

    [Fact]
    public async Task CancellationWhileChildDoesNotReadLargeStdin_KillsRootAndDescendantPromptly()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var transcript = new PidTranscript();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runner = NewIsolatedRunner();
        var run = runner.RunAsync(
            [
                "/bin/sh", "-c",
                "printf 'root=%s\\n' $$; sleep 30 </dev/null & child=$!; printf 'child=%s\\n' \"$child\"; wait",
            ],
            new string('x', 4 * 1024 * 1024),
            cancellation.Token,
            stdoutChunkCallback: transcript.Append,
            maxStdoutBytes: 4096,
            maxStderrBytes: 4096);
        var rootPid = await transcript.RootPid.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var descendantPid = await transcript.ChildPid.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => run.WaitAsync(IsolatedRunCompletionBudget));

        await AssertProcessesGoneAsync(rootPid, descendantPid);
    }

    [Fact]
    public async Task RootExitsDuringLargeStdinWrite_KillsOrphanedDescendantAndDoesNotHang()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var transcript = new PidTranscript();
        var runner = new IncusCliProcessRunner();
        var run = runner.RunAsync(
            [
                "/bin/sh", "-c",
                "printf 'root=%s\\n' $$; sleep 30 </dev/null & child=$!; printf 'child=%s\\n' \"$child\"; exit 0",
            ],
            new string('x', 4 * 1024 * 1024),
            CancellationToken.None,
            stdoutChunkCallback: transcript.Append,
            maxStdoutBytes: 4096,
            maxStderrBytes: 4096);
        var rootPid = await transcript.RootPid.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var descendantPid = await transcript.ChildPid.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<IOException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));

        await AssertProcessesGoneAsync(rootPid, descendantPid);
    }

    [Fact]
    public async Task OutputLimitAfterRootExits_KillsOrphanedWriterProcess()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var transcript = new PidTranscript();
        var runner = NewIsolatedRunner();
        var result = await runner.RunAsync(
            [
                "/bin/sh", "-c",
                "sh -c 'printf \"child=%s\\n\" $$ >&2; while :; do printf 0123456789; done' & exit 0",
            ],
            stdin: null,
            CancellationToken.None,
            stderrChunkCallback: transcript.Append,
            maxStdoutBytes: 1024,
            maxStderrBytes: 4096).WaitAsync(IsolatedRunCompletionBudget);
        var descendantPid = await transcript.ChildPid.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.StdoutLimitExceeded);
        await AssertProcessesGoneAsync(descendantPid);
    }

    [Fact]
    public async Task CancellationWhileReaderWaitsOnOrphanedPipe_KillsDescendant()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var transcript = new PidTranscript();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runner = NewIsolatedRunner();
        var run = runner.RunAsync(
            [
                "/bin/sh", "-c",
                "sh -c 'printf \"child=%s\\n\" $$ >&2; sleep 30' & exit 0",
            ],
            stdin: null,
            cancellation.Token,
            stderrChunkCallback: transcript.Append,
            maxStdoutBytes: 4096,
            maxStderrBytes: 4096);
        var descendantPid = await transcript.ChildPid.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(IsolatedRunCompletionBudget));

        await AssertProcessesGoneAsync(descendantPid);
    }

    [Fact]
    public async Task MissingExplicitOutputLimit_PreservesUnboundedSharedRunnerBehavior()
    {
        if (!OperatingSystem.IsLinux())
            return;
        const int outputBytes = (16 * 1024 * 1024) + 1;
        var runner = new DefaultProcessRunner();

        var result = await runner.RunAsync(
            [
                "/bin/sh", "-c",
                $"yes x | head -c {outputBytes.ToString(CultureInfo.InvariantCulture)}",
            ],
            stdin: null,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.StdoutLimitExceeded);
        Assert.False(result.StderrLimitExceeded);
        Assert.Equal(outputBytes, Encoding.UTF8.GetByteCount(result.Stdout));
    }

    // These teardown tests verify that orphaned writers are killed and their
    // pipes drained — not how fast. On a saturated host the real kill+drain can
    // exceed the 5 s default cleanup budget, so give it generous headroom; the
    // assertions (process gone, limit exceeded) are unchanged.
    private static readonly TimeSpan IsolatedRunnerCleanupTimeout = TimeSpan.FromSeconds(60);

    // Anti-hang guard for awaiting an isolated run to complete. It must exceed
    // IsolatedRunnerCleanupTimeout so a slow-but-correct teardown under load
    // reports its real outcome instead of tripping the guard first.
    private static readonly TimeSpan IsolatedRunCompletionBudget = TimeSpan.FromSeconds(90);

    private static DefaultProcessRunner NewIsolatedRunner() => new(
        new DefaultProcessRunnerOptions
        {
            IsolateLinuxProcessGroup = true,
            CleanupTimeout = IsolatedRunnerCleanupTimeout,
        });

    private static async Task AssertProcessesGoneAsync(params int[] processIds)
    {
        foreach (var processId in processIds)
        {
            var processPath = $"/proc/{processId.ToString(CultureInfo.InvariantCulture)}";
            for (var attempt = 0; attempt < 80 && Directory.Exists(processPath); attempt++)
                await Task.Delay(25);
            Assert.False(Directory.Exists(processPath), $"process {processId} survived runner teardown");
        }
    }

    private sealed class PidTranscript
    {
        private readonly Lock _gate = new();
        private readonly StringBuilder _pending = new();

        internal TaskCompletionSource<int> RootPid { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<int> ChildPid { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Append(string chunk)
        {
            lock (_gate)
            {
                _pending.Append(chunk);
                while (true)
                {
                    var newline = IndexOfNewline(_pending);
                    if (newline < 0)
                        return;
                    var line = _pending.ToString(0, newline);
                    _pending.Remove(0, newline + 1);
                    ParseLine(line);
                }
            }
        }

        private void ParseLine(string line)
        {
            const string rootPrefix = "root=";
            const string childPrefix = "child=";
            if (line.StartsWith(rootPrefix, StringComparison.Ordinal)
                && int.TryParse(line.AsSpan(rootPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var root))
            {
                RootPid.TrySetResult(root);
            }
            else if (line.StartsWith(childPrefix, StringComparison.Ordinal)
                && int.TryParse(line.AsSpan(childPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var child))
            {
                ChildPid.TrySetResult(child);
            }
        }

        private static int IndexOfNewline(StringBuilder value)
        {
            for (var index = 0; index < value.Length; index++)
            {
                if (value[index] == '\n')
                    return index;
            }
            return -1;
        }
    }

    private sealed class RecordingTimeProvider : TimeProvider
    {
        internal ConcurrentQueue<TimeSpan> TimerDueTimes { get; } = new();

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            TimerDueTimes.Enqueue(dueTime);
            return TimeProvider.System.CreateTimer(callback, state, dueTime, period);
        }
    }
}
