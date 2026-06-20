using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="ResizableConcurrencyGate"/> covering the resize
/// directions called out in the WorkerPool hot-reload contract: grow admits
/// queued waiters immediately, shrink never aborts in-flight permits, and
/// invalid sizes are rejected without leaving the gate in a corrupt state.
/// </summary>
public sealed class ResizableConcurrencyGateTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public void InitialTarget_BelowOne_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResizableConcurrencyGate(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResizableConcurrencyGate(-5));
    }

    [Fact]
    public void TryEnter_RespectsInitialTarget()
    {
        using var gate = new ResizableConcurrencyGate(2);
        Assert.True(gate.TryEnter());
        Assert.True(gate.TryEnter());
        Assert.False(gate.TryEnter());
        Assert.Equal(2, gate.CurrentInFlight);
        Assert.Equal(2, gate.CurrentTarget);
    }

    [Fact]
    public void Release_FreesSlotForNextTryEnter()
    {
        using var gate = new ResizableConcurrencyGate(1);
        Assert.True(gate.TryEnter());
        Assert.False(gate.TryEnter());
        gate.Release();
        Assert.True(gate.TryEnter());
    }

    [Fact]
    public async Task WaitAsync_BlocksUntilSlotReleased()
    {
        using var gate = new ResizableConcurrencyGate(1);
        Assert.True(gate.TryEnter());

        var waitTask = gate.WaitAsync(CancellationToken.None);
        Assert.False(waitTask.IsCompleted);

        gate.Release();
        await waitTask.WaitAsync(WaitTimeout);
        Assert.Equal(1, gate.CurrentInFlight);
    }

    [Fact]
    public async Task Resize_Grow_AdmitsQueuedWaitersImmediately()
    {
        using var gate = new ResizableConcurrencyGate(2);
        Assert.True(gate.TryEnter());
        Assert.True(gate.TryEnter());

        var w1 = gate.WaitAsync(CancellationToken.None);
        var w2 = gate.WaitAsync(CancellationToken.None);
        Assert.False(w1.IsCompleted);
        Assert.False(w2.IsCompleted);

        var result = gate.Resize(4);

        Assert.Equal(2, result.OldTarget);
        Assert.Equal(4, result.NewTarget);
        await Task.WhenAll(w1, w2).WaitAsync(WaitTimeout);
        Assert.Equal(4, gate.CurrentInFlight);
        Assert.Equal(4, gate.CurrentTarget);
    }

    [Fact]
    public void Resize_Shrink_DoesNotAbortInFlightPermits()
    {
        using var gate = new ResizableConcurrencyGate(3);
        Assert.True(gate.TryEnter());
        Assert.True(gate.TryEnter());
        Assert.True(gate.TryEnter());

        gate.Resize(1);

        // None of the three reservations are kicked; the gate just refuses
        // to admit more until the count converges down to the new target.
        Assert.Equal(3, gate.CurrentInFlight);
        Assert.Equal(1, gate.CurrentTarget);
        Assert.False(gate.TryEnter());

        // Release the first — still above new target (2 > 1) — no new admission.
        gate.Release();
        Assert.False(gate.TryEnter());
        // Drop to the new target — still no admission until the count falls
        // strictly below it.
        gate.Release();
        Assert.False(gate.TryEnter());
        // Now at the new target floor; the next release frees a slot.
        gate.Release();
        Assert.True(gate.TryEnter());
    }

    [Fact]
    public async Task Resize_ShrinkThenGrow_RestoresAdmissionWithoutDoubleCount()
    {
        using var gate = new ResizableConcurrencyGate(3);
        Assert.True(gate.TryEnter());
        Assert.True(gate.TryEnter());
        Assert.True(gate.TryEnter());

        gate.Resize(1);
        Assert.Equal(3, gate.CurrentInFlight);

        // Queue a waiter while we're shrunk and above target.
        var waiter = gate.WaitAsync(CancellationToken.None);
        Assert.False(waiter.IsCompleted);

        // Grow back to 4: target now > in-flight (3), one slot available for
        // the queued waiter. The earlier "shrink-grow" cannot leak permits.
        var growResult = gate.Resize(4);
        Assert.Equal(1, growResult.OldTarget);
        Assert.Equal(4, growResult.NewTarget);

        await waiter.WaitAsync(WaitTimeout);
        Assert.Equal(4, gate.CurrentInFlight);
    }

    [Fact]
    public void Resize_BelowOne_Throws_AndPriorTargetUnchanged()
    {
        using var gate = new ResizableConcurrencyGate(3);
        Assert.True(gate.TryEnter());

        Assert.Throws<ArgumentOutOfRangeException>(() => gate.Resize(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => gate.Resize(-1));

        Assert.Equal(3, gate.CurrentTarget);
        Assert.Equal(1, gate.CurrentInFlight);
        // The gate still admits up to the original target.
        Assert.True(gate.TryEnter());
        Assert.True(gate.TryEnter());
    }

    [Fact]
    public void Resize_SameValue_IsNoOp()
    {
        using var gate = new ResizableConcurrencyGate(2);
        var result = gate.Resize(2);
        Assert.Equal(2, result.OldTarget);
        Assert.Equal(2, result.NewTarget);
        Assert.Equal(0, result.InFlight);
    }

    [Fact]
    public async Task WaitAsync_RespectsCancellation()
    {
        using var gate = new ResizableConcurrencyGate(1);
        Assert.True(gate.TryEnter());

        using var cts = new CancellationTokenSource();
        var waitTask = gate.WaitAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => waitTask.WaitAsync(WaitTimeout));
    }

    [Fact]
    public async Task Release_SkipsCancelledWaitersAndWakesNextLive()
    {
        using var gate = new ResizableConcurrencyGate(1);
        Assert.True(gate.TryEnter());

        using var cts = new CancellationTokenSource();
        var cancelledWait = gate.WaitAsync(cts.Token);
        var liveWait = gate.WaitAsync(CancellationToken.None);
        cts.Cancel();

        // The cancelled waiter never grabs the released slot — the live
        // waiter does.
        gate.Release();
        await liveWait.WaitAsync(WaitTimeout);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cancelledWait.WaitAsync(WaitTimeout));
        Assert.Equal(1, gate.CurrentInFlight);
    }

    [Fact]
    public void Release_AfterDispose_DoesNotThrow()
    {
        var gate = new ResizableConcurrencyGate(1);
        Assert.True(gate.TryEnter());
        gate.Dispose();
        gate.Release();
    }

    [Fact]
    public async Task Dispose_CancelsQueuedWaiters()
    {
        var gate = new ResizableConcurrencyGate(1);
        Assert.True(gate.TryEnter());
        var waiter = gate.WaitAsync(CancellationToken.None);

        gate.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => waiter.WaitAsync(WaitTimeout));
    }
}
