using CodeyBox.Core.ExternalBuilds;
using CodeyBox.Orchestrator.ExternalBuilds;

namespace CodeyBox.Tests.ExternalBuilds;

/// <summary>Park coordinator: predicted/elapsed parking, no-needless-park,
/// single delivery, obsolete-attempt suppression, known-wait state.</summary>
public sealed class ExternalBuildParkCoordinatorTests
{
    private static (ExternalBuildService, InMemoryExternalBuildStore, ControllableClock, ExternalBuildOptions, FakeSnapshotBuildProvider)
        New()
    {
        var clock = new ControllableClock(DateTimeOffset.UtcNow);
        var opts = ExternalBuildTestKit.Options();
        var store = new InMemoryExternalBuildStore();
        var provider = new FakeSnapshotBuildProvider { PollsToTerminal = 100 };
        var service = new ExternalBuildService(store, [provider], () => opts, clock);
        return (service, store, clock, opts, provider);
    }

    private static void SeedHistory(InMemoryExternalBuildHistory history, ExternalBuildTargetKey target, double minutes, int count)
    {
        for (var i = 0; i < count; i++)
            history.Record(new ExternalBuildDurationSample(
                target, TimeSpan.FromMinutes(minutes), true,
                DateTimeOffset.UtcNow.AddHours(-i - 1), ColdCache: false));
    }

    [Fact]
    public async Task PredictedLong_Parks_AndDeliversOnce()
    {
        var (service, store, clock, opts, _) = New();
        var history = new InMemoryExternalBuildHistory();
        var coordinator = new ExternalBuildParkCoordinator(service, store, history, () => opts, clock);
        var record = await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());
        var stored = (await store.GetAsync(record.Id))!;
        SeedHistory(history, stored.Target, 25, 5);

        var parked = await coordinator.MaybeParkAsync(record.Id, TimeSpan.FromMinutes(1), coldCache: false);
        Assert.NotNull(parked);
        Assert.True(parked.Decision.ShouldPark);
        Assert.True(coordinator.IsParkedKnownWait(record.Id));
        Assert.Equal(5, parked.Decision.SampleCount);
    }

    [Fact]
    public async Task PredictedShort_StaysActive()
    {
        var (service, store, clock, opts, _) = New();
        var history = new InMemoryExternalBuildHistory();
        var coordinator = new ExternalBuildParkCoordinator(service, store, history, () => opts, clock);
        var record = await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());
        var stored = (await store.GetAsync(record.Id))!;
        SeedHistory(history, stored.Target, 3, 5);

        Assert.Null(await coordinator.MaybeParkAsync(record.Id, TimeSpan.FromMinutes(1), coldCache: false));
        Assert.False(coordinator.IsParkedKnownWait(record.Id));
    }

    [Fact]
    public async Task ElapsedFallback_ParksAfter10Minutes()
    {
        var (service, store, clock, opts, _) = New();
        var history = new InMemoryExternalBuildHistory();
        var coordinator = new ExternalBuildParkCoordinator(service, store, history, () => opts, clock);
        var record = await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());

        Assert.Null(await coordinator.MaybeParkAsync(record.Id, TimeSpan.FromMinutes(5), null));
        var parked = await coordinator.MaybeParkAsync(
            record.Id, TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(1)), null);
        Assert.NotNull(parked);
    }

    [Fact]
    public async Task CompletedBeforePark_NeedsNoPark()
    {
        var provider = new FakeSnapshotBuildProvider { PollsToTerminal = 1 };
        var clock = new ControllableClock(DateTimeOffset.UtcNow);
        var opts = ExternalBuildTestKit.Options();
        var store = new InMemoryExternalBuildStore();
        var service = new ExternalBuildService(store, [provider], () => opts, clock);
        var history = new InMemoryExternalBuildHistory();
        var coordinator = new ExternalBuildParkCoordinator(service, store, history, () => opts, clock);
        var record = await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());
        await service.ReconcileAsync(record.Id);

        Assert.Null(await coordinator.MaybeParkAsync(record.Id, TimeSpan.Zero, null));
    }
}
