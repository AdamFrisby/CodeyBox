using System.Globalization;
using System.Text;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox.Incus;

namespace CodeyBox.Tests;

public sealed class DefaultProcessRunnerCancellationTests
{
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
            () => run.WaitAsync(TimeSpan.FromSeconds(5)));

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
            maxStderrBytes: 4096).WaitAsync(TimeSpan.FromSeconds(5));
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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));

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

    private static DefaultProcessRunner NewIsolatedRunner() => new(
        new DefaultProcessRunnerOptions { IsolateLinuxProcessGroup = true });

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
}
