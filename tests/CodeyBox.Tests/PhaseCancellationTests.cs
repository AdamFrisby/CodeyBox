using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="PhaseCancellation"/> source attribution and the
/// resulting <see cref="PhaseCancellationException"/> wrapping. Covers the
/// catch-routing inputs the pipeline depends on without spinning a full
/// PipelineRunner harness.
/// </summary>
public sealed class PhaseCancellationTests
{
    [Fact]
    public async Task PhaseTimeout_AttributesAsTimeoutPhase()
    {
        using var phase = new PhaseCancellation("work", CancellationToken.None);
        phase.SetPhaseTimeout(TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<PhaseCancellationException>(() =>
            phase.RunAsync(ct => Task.Delay(Timeout.InfiniteTimeSpan, ct)));

        Assert.Equal("work", ex.Phase);
        Assert.Equal(CancellationSources.PhaseTimeout("work"), ex.Source);
        Assert.True(CancellationSources.IsPhaseTimeout(ex.Source));
        Assert.False(CancellationSources.IsTransient(ex.Source));
    }

    [Fact]
    public async Task OperatorParent_AttributesAsOperator()
    {
        using var operatorCts = new CancellationTokenSource();
        using var phase = new PhaseCancellation("work", operatorCts.Token);

        var task = phase.RunAsync(ct => Task.Delay(Timeout.InfiniteTimeSpan, ct));
        await operatorCts.CancelAsync();

        var ex = await Assert.ThrowsAsync<PhaseCancellationException>(() => task);
        Assert.Equal(CancellationSources.Operator, ex.Source);
    }

    [Fact]
    public async Task HostShutdown_AttributesAsHostShutdown()
    {
        using var hostCts = new CancellationTokenSource();
        using var phase = new PhaseCancellation("rework", CancellationToken.None);
        phase.HookHostShutdown(hostCts.Token, grace: TimeSpan.FromMilliseconds(50));

        var task = phase.RunAsync(ct => Task.Delay(Timeout.InfiniteTimeSpan, ct));
        await hostCts.CancelAsync();

        var ex = await Assert.ThrowsAsync<PhaseCancellationException>(() => task);
        Assert.True(ex.Source is CancellationSources.HostShutdown or CancellationSources.HostShutdownDeadline,
            $"expected host-shutdown source, got '{ex.Source}'");
        Assert.Equal("rework", ex.Phase);
    }

    [Fact]
    public async Task StuckProbe_AttributesAsStuckProbe()
    {
        using var phase = new PhaseCancellation("merge", CancellationToken.None);
        phase.RecordStuckProbe();
        await phase.Cts.CancelAsync();

        var ex = await Assert.ThrowsAsync<PhaseCancellationException>(() =>
            phase.RunAsync(ct => Task.Delay(Timeout.InfiniteTimeSpan, ct)));

        Assert.Equal(CancellationSources.StuckProbe, ex.Source);
    }

    [Fact]
    public async Task UnattributedCancel_ResolvesToUnknown()
    {
        using var phase = new PhaseCancellation("audit", CancellationToken.None);
        // Cancel the inner CTS directly without going through any of our
        // registered hooks. Mirrors the smoking-gun pattern from prod where
        // an external/leaked supervisor token cancels the linked CTS.
        await phase.Cts.CancelAsync();

        var ex = await Assert.ThrowsAsync<PhaseCancellationException>(() =>
            phase.RunAsync(ct => Task.Delay(Timeout.InfiniteTimeSpan, ct)));

        Assert.Equal(CancellationSources.Unknown, ex.Source);
        Assert.True(CancellationSources.IsTransient(ex.Source));
    }

    [Fact]
    public async Task FirstSourceWins_OnRace()
    {
        // Two contributors arming the same phase: the first to fire records
        // attribution; later ones are no-ops. The CompareExchange in
        // TryRecordSource provides the linearisation point.
        using var operatorCts = new CancellationTokenSource();
        using var hostCts = new CancellationTokenSource();
        using var phase = new PhaseCancellation("work", operatorCts.Token);
        phase.HookHostShutdown(hostCts.Token, grace: TimeSpan.FromMilliseconds(50));

        await operatorCts.CancelAsync();
        // Race the host shutdown after the operator cancel was already recorded.
        await hostCts.CancelAsync();

        var ex = await Assert.ThrowsAsync<PhaseCancellationException>(() =>
            phase.RunAsync(ct => Task.Delay(Timeout.InfiniteTimeSpan, ct)));

        // Operator fired first → host shutdown should not steal the slot.
        Assert.Equal(CancellationSources.Operator, ex.Source);
    }

    [Fact]
    public async Task Wrap_PreservesInnerMessageForLogAggregation()
    {
        using var phase = new PhaseCancellation("work", CancellationToken.None);
        var inner = new TaskCanceledException(); // default message: "A task was canceled."

        var ex = phase.Wrap(inner);

        Assert.Contains("A task was canceled.", ex.Message);
        Assert.Contains("work", ex.Message);
        Assert.Contains("source=", ex.Message);
        Assert.Same(inner, ex.InnerException);
        await Task.CompletedTask;
    }

    [Fact]
    public void Wrap_DoubleWrapIsIdempotent()
    {
        using var phase = new PhaseCancellation("rework", CancellationToken.None);
        var first = phase.Wrap(new TaskCanceledException());
        var second = phase.Wrap(first);

        Assert.Same(first, second);
    }

    [Fact]
    public void IsTransient_ClassifiesSourcesCorrectly()
    {
        Assert.True(CancellationSources.IsTransient(CancellationSources.Unknown));
        Assert.True(CancellationSources.IsTransient(CancellationSources.HostShutdownDeadline));
        Assert.False(CancellationSources.IsTransient(CancellationSources.Operator));
        Assert.False(CancellationSources.IsTransient(CancellationSources.HostShutdown));
        Assert.False(CancellationSources.IsTransient(CancellationSources.StuckProbe));
        Assert.False(CancellationSources.IsTransient(CancellationSources.PhaseTimeout("work")));
        Assert.False(CancellationSources.IsTransient(null));
    }
}
