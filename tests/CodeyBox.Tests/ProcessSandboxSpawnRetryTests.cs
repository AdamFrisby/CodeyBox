using System.ComponentModel;
using CodeyBox.Sandbox.Process;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="ProcessSandbox.StartWithTransientRetryAsync"/> — the
/// bounded retry that absorbs momentary <c>posix_spawn</c> resource-exhaustion
/// failures (EAGAIN/EMFILE/…) seen under the full-suite fork/fd storm. Drives the
/// pure retry core directly with injected <c>start</c> and <c>delay</c> callbacks
/// so no real subprocess is spawned.
/// </summary>
public sealed class ProcessSandboxSpawnRetryTests
{
    private const int Eagain = 11;
    private const int Emfile = 24;
    private const int Enoent = 2;

    private static Win32Exception SpawnError(int errno) => new(errno);

    [Fact]
    public async Task SucceedsOnFirstAttempt_DoesNotDelay()
    {
        var starts = 0;
        var delays = 0;

        await ProcessSandbox.StartWithTransientRetryAsync(
            start: () => { starts++; return true; },
            delay: (_, _) => { delays++; return Task.CompletedTask; },
            maxAttempts: ProcessSandbox.SpawnMaxAttempts,
            ct: default);

        Assert.Equal(1, starts);
        Assert.Equal(0, delays);
    }

    [Fact]
    public async Task RetriesTransientFailure_ThenSucceeds()
    {
        var starts = 0;
        var delayAttempts = new List<int>();

        await ProcessSandbox.StartWithTransientRetryAsync(
            start: () =>
            {
                starts++;
                if (starts < 3) throw SpawnError(starts == 1 ? Eagain : Emfile);
                return true;
            },
            delay: (attempt, _) => { delayAttempts.Add(attempt); return Task.CompletedTask; },
            maxAttempts: ProcessSandbox.SpawnMaxAttempts,
            ct: default);

        Assert.Equal(3, starts);
        // One delay before each of the two retries, indexed by the just-failed attempt.
        Assert.Equal(new[] { 1, 2 }, delayAttempts);
    }

    [Fact]
    public async Task ExhaustsAttempts_RethrowsLastTransientFailure()
    {
        var starts = 0;

        var ex = await Assert.ThrowsAsync<Win32Exception>(() =>
            ProcessSandbox.StartWithTransientRetryAsync(
                start: () => { starts++; throw SpawnError(Emfile); },
                delay: (_, _) => Task.CompletedTask,
                maxAttempts: ProcessSandbox.SpawnMaxAttempts,
                ct: default));

        Assert.Equal(Emfile, ex.NativeErrorCode);
        Assert.Equal(ProcessSandbox.SpawnMaxAttempts, starts);
    }

    [Fact]
    public async Task NonTransientFailure_RethrowsImmediatelyWithoutRetry()
    {
        var starts = 0;
        var delays = 0;

        var ex = await Assert.ThrowsAsync<Win32Exception>(() =>
            ProcessSandbox.StartWithTransientRetryAsync(
                start: () => { starts++; throw SpawnError(Enoent); },
                delay: (_, _) => { delays++; return Task.CompletedTask; },
                maxAttempts: ProcessSandbox.SpawnMaxAttempts,
                ct: default));

        Assert.Equal(Enoent, ex.NativeErrorCode);
        Assert.Equal(1, starts);
        Assert.Equal(0, delays);
    }

    [Fact]
    public async Task CancellationRequested_ThrowsBeforeStarting()
    {
        var starts = 0;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProcessSandbox.StartWithTransientRetryAsync(
                start: () => { starts++; return true; },
                delay: (_, _) => Task.CompletedTask,
                maxAttempts: ProcessSandbox.SpawnMaxAttempts,
                ct: cts.Token));

        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task RejectsNonPositiveMaxAttempts()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            ProcessSandbox.StartWithTransientRetryAsync(
                start: () => true,
                delay: (_, _) => Task.CompletedTask,
                maxAttempts: 0,
                ct: default));
    }
}
