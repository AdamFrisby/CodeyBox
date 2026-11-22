using CodeyBox.Core;
using ControllableTimeProvider = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="HostResourceBlockTracker"/>: disk-block
/// record/revalidate/expiry semantics, provisioning-block visibility without
/// dispatch gating, and the dispatch-blocking query the orchestrator's pickup
/// loop consults.
/// </summary>
public sealed class HostResourceBlockTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RecordDiskBlock_ProducesActiveBlockingStatus()
    {
        var tracker = new HostResourceBlockTracker(new ControllableTimeProvider(T0));

        tracker.RecordDiskBlock("/var/lib/codeybox", freeBytes: 100, thresholdBytes: 10_000, TimeSpan.FromMinutes(5));

        var block = Assert.Single(tracker.GetActiveHostBlocks());
        Assert.Equal(HostResourceBlockKinds.DiskSpace, block.Kind);
        Assert.Equal("/var/lib/codeybox", block.MountPath);
        Assert.Equal(100, block.FreeBytes);
        Assert.Equal(10_000, block.ThresholdBytes);
        Assert.Equal(T0, block.BlockedSince);
        Assert.True(block.DispatchBlocking);
        Assert.Contains("only 100 bytes free", block.Reason, StringComparison.Ordinal);
        Assert.Equal(block, tracker.GetBlockingBlock());
    }

    [Fact]
    public void RepeatedDeferral_KeepsOriginalBlockedSince_AndExtendsDeadline()
    {
        var time = new ControllableTimeProvider(T0);
        var tracker = new HostResourceBlockTracker(time);

        tracker.RecordDiskBlock("/data", 1, 10, TimeSpan.FromMinutes(5));
        time.Advance(TimeSpan.FromMinutes(2));
        tracker.RecordDiskBlock("/data", 2, 10, TimeSpan.FromMinutes(5));

        var block = Assert.Single(tracker.GetActiveHostBlocks());
        Assert.Equal(T0, block.BlockedSince);
        Assert.Equal(T0.AddMinutes(7), block.BlockedUntil);
        Assert.Equal(T0.AddMinutes(2), block.LastObservedAt);
        Assert.Equal(2, block.FreeBytes);
    }

    [Fact]
    public void DiskBlock_ReleasesEarly_WhenSamplerShowsMountRecovered()
    {
        var time = new ControllableTimeProvider(T0);
        var freeBytes = 100L;
        var tracker = new HostResourceBlockTracker(
            time,
            diskSampler: () => [new DiskGuardSample("/data", freeBytes, ThresholdBytes: 1_000)]);

        tracker.RecordDiskBlock("/data", freeBytes: 100, thresholdBytes: 1_000, TimeSpan.FromHours(1));
        Assert.NotNull(tracker.GetBlockingBlock());

        // Space returns: the next status read revalidates against the live
        // sample and releases the hold long before RecheckIn.
        freeBytes = 5_000;
        Assert.Empty(tracker.GetActiveHostBlocks());
        Assert.Null(tracker.GetBlockingBlock());
    }

    [Fact]
    public void DiskBlock_Persists_WhenSamplerStillBelowThreshold()
    {
        var tracker = new HostResourceBlockTracker(
            new ControllableTimeProvider(T0),
            diskSampler: () => [new DiskGuardSample("/data", FreeBytes: 50, ThresholdBytes: 1_000)]);

        tracker.RecordDiskBlock("/data", freeBytes: 100, thresholdBytes: 1_000, TimeSpan.FromHours(1));

        var block = Assert.Single(tracker.GetActiveHostBlocks());
        Assert.Equal("disk:/data", block.Key);
        Assert.NotNull(tracker.GetBlockingBlock());
    }

    [Fact]
    public void DiskBlock_InconclusiveSample_KeepsTimedBlock()
    {
        // A null FreeBytes sample is inconclusive — only positive recovery
        // evidence may clear a block early.
        var tracker = new HostResourceBlockTracker(
            new ControllableTimeProvider(T0),
            diskSampler: () => [new DiskGuardSample("/data", FreeBytes: null, ThresholdBytes: 1_000)]);

        tracker.RecordDiskBlock("/data", freeBytes: 100, thresholdBytes: 1_000, TimeSpan.FromHours(1));

        Assert.Single(tracker.GetActiveHostBlocks());
    }

    [Fact]
    public void DiskBlock_LapsesAtRecheckIn_WithoutSampler()
    {
        var time = new ControllableTimeProvider(T0);
        var tracker = new HostResourceBlockTracker(time);

        tracker.RecordDiskBlock("/data", 1, 10, TimeSpan.FromMinutes(5));
        Assert.NotNull(tracker.GetBlockingBlock());

        time.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromTicks(1)));
        Assert.Empty(tracker.GetActiveHostBlocks());
        Assert.Null(tracker.GetBlockingBlock());
    }

    [Fact]
    public void ProvisioningBlock_IsVisible_ButNeverDispatchBlocking()
    {
        var tracker = new HostResourceBlockTracker(new ControllableTimeProvider(T0));

        tracker.RecordProvisioningBlock(
            "incus", "create", "incus-db-transaction-deadline", "detail text", TimeSpan.FromMinutes(1));

        var block = Assert.Single(tracker.GetActiveHostBlocks());
        Assert.Equal(HostResourceBlockKinds.SandboxProvisioning, block.Kind);
        Assert.Equal("incus", block.Provider);
        Assert.Equal("create", block.Operation);
        Assert.Equal("incus-db-transaction-deadline", block.ErrorClass);
        Assert.Equal("detail text", block.Detail);
        Assert.False(block.DispatchBlocking);
        // Item/project-scoped deferrals must not hold the whole queue.
        Assert.Null(tracker.GetBlockingBlock());
    }

    [Fact]
    public void ProvisioningBlock_DedupesOnProviderOperationErrorClass()
    {
        var time = new ControllableTimeProvider(T0);
        var tracker = new HostResourceBlockTracker(time);

        tracker.RecordProvisioningBlock("p", "op", "cls", null, TimeSpan.FromMinutes(1));
        time.Advance(TimeSpan.FromSeconds(30));
        tracker.RecordProvisioningBlock("p", "op", "cls", null, TimeSpan.FromMinutes(1));
        tracker.RecordProvisioningBlock("p", "op", "other", null, TimeSpan.FromMinutes(1));

        Assert.Equal(2, tracker.GetActiveHostBlocks().Count);
    }

    [Fact]
    public void DistinctMounts_TrackedIndependently()
    {
        var tracker = new HostResourceBlockTracker(new ControllableTimeProvider(T0));

        tracker.RecordDiskBlock("/a", 1, 10, TimeSpan.FromMinutes(5));
        tracker.RecordDiskBlock("/b", 2, 10, TimeSpan.FromMinutes(5));

        var blocks = tracker.GetActiveHostBlocks();
        Assert.Equal(2, blocks.Count);
        Assert.Contains(blocks, b => b.MountPath == "/a");
        Assert.Contains(blocks, b => b.MountPath == "/b");
    }

    [Fact]
    public void SamplerFault_NeverBreaksStatusRead()
    {
        var tracker = new HostResourceBlockTracker(
            new ControllableTimeProvider(T0),
            diskSampler: () => throw new InvalidOperationException("probe exploded"));

        tracker.RecordDiskBlock("/data", 1, 10, TimeSpan.FromHours(1));

        // The timed block still stands; the fault is swallowed.
        Assert.Single(tracker.GetActiveHostBlocks());
    }
}
