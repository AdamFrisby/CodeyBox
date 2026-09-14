using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="QueueDrain.WaitForQuiescenceAsync"/> — the
/// pause-and-wait primitive behind <c>POST /queue/drain</c>. All waits use
/// scripted counts and millisecond-scale intervals so the tests stay
/// deterministic under full-suite load.
/// </summary>
public sealed class QueueDrainTests
{
    [Fact]
    public async Task WaitForQuiescence_ReturnsTrueImmediatelyWhenAlreadyIdle()
    {
        var calls = 0;
        var drained = await QueueDrain.WaitForQuiescenceAsync(
            _ =>
            {
                calls++;
                return Task.FromResult(0);
            },
            TimeSpan.FromSeconds(5),
            CancellationToken.None,
            TimeSpan.FromMilliseconds(5));

        Assert.True(drained);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task WaitForQuiescence_WaitsUntilRunningCountReachesZero()
    {
        var remaining = new Queue<int>([2, 1, 0]);
        var calls = 0;
        var drained = await QueueDrain.WaitForQuiescenceAsync(
            _ =>
            {
                calls++;
                return Task.FromResult(remaining.Dequeue());
            },
            TimeSpan.FromSeconds(5),
            CancellationToken.None,
            TimeSpan.FromMilliseconds(5));

        Assert.True(drained);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task WaitForQuiescence_ReturnsFalseOnTimeout()
    {
        var drained = await QueueDrain.WaitForQuiescenceAsync(
            _ => Task.FromResult(1),
            TimeSpan.FromMilliseconds(60),
            CancellationToken.None,
            TimeSpan.FromMilliseconds(5));

        Assert.False(drained);
    }

    [Fact]
    public async Task WaitForQuiescence_NonPositiveTimeoutChecksExactlyOnce()
    {
        var calls = 0;
        var drained = await QueueDrain.WaitForQuiescenceAsync(
            _ =>
            {
                calls++;
                return Task.FromResult(1);
            },
            TimeSpan.Zero,
            CancellationToken.None,
            TimeSpan.FromMilliseconds(5));

        Assert.False(drained);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task WaitForQuiescence_PropagatesCallerCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            QueueDrain.WaitForQuiescenceAsync(
                _ => Task.FromResult(1),
                TimeSpan.FromSeconds(5),
                cts.Token,
                TimeSpan.FromMilliseconds(5)));
    }
}
