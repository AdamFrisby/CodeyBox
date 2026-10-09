using CodeyBox.Core.ExternalBuilds;
using CodeyBox.Orchestrator.ExternalBuilds;

namespace CodeyBox.Tests.ExternalBuilds;

/// <summary>Guards added in rework: host-resolved artifact reads, fencing
/// ownership, CAS conflicts, and durable restart delivery/parks.</summary>
public sealed class ExternalBuildDurabilityTests
{
    [Fact]
    public async Task ArtifactRead_HostResolved_ValidatesMembershipAndDigest()
    {
        var clock = new ControllableClock(DateTimeOffset.UtcNow);
        var opts = ExternalBuildTestKit.Options();
        var store = new InMemoryExternalBuildStore();
        var provider = new FakeSnapshotBuildProvider();
        var service = new ExternalBuildService(store, [provider], () => opts, clock);
        var tools = new ExternalBuildSandboxTools(service, store, () => opts, clock, [provider]);
        var cap = tools.IssueCapability("proj", "w1", "work", 1, 1);
        var started = await tools.StartAsync(cap.Handle, "fake-target", ExternalBuildTestKit.Source(), "k1");

        var refs = await tools.ListArtifactsAsync(cap.Handle, started.Id);
        Assert.Single(refs);
        var payload = await tools.ReadArtifactAsync(cap.Handle, started.Id, "package.zip");
        Assert.Equal(payload.ContentDigestSha256, ExternalBuildProvenance.DigestBytes(payload.Content));
    }

    [Fact]
    public async Task ArtifactRead_TraversalOrUnlistedName_RejectedBeforeFetch()
    {
        var clock = new ControllableClock(DateTimeOffset.UtcNow);
        var opts = ExternalBuildTestKit.Options();
        var store = new InMemoryExternalBuildStore();
        var provider = new FakeSnapshotBuildProvider();
        var service = new ExternalBuildService(store, [provider], () => opts, clock);
        var tools = new ExternalBuildSandboxTools(service, store, () => opts, clock, [provider]);
        var cap = tools.IssueCapability("proj", "w1", "work", 1, 1);
        var started = await tools.StartAsync(cap.Handle, "fake-target", ExternalBuildTestKit.Source(), "k1");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            tools.ReadArtifactAsync(cap.Handle, started.Id, "../evil.zip"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tools.ReadArtifactAsync(cap.Handle, started.Id, "no-such.zip"));
    }

    [Fact]
    public async Task ArtifactRead_ForeignProviderInstance_Rejected()
    {
        var clock = new ControllableClock(DateTimeOffset.UtcNow);
        var opts = ExternalBuildTestKit.Options();
        var store = new InMemoryExternalBuildStore();
        var provider = new FakeSnapshotBuildProvider();
        var foreign = new FakeGitBuildProvider();
        var service = new ExternalBuildService(store, [provider], () => opts, clock);
        var tools = new ExternalBuildSandboxTools(service, store, () => opts, clock, [provider]);
        var cap = tools.IssueCapability("proj", "w1", "work", 1, 1);
        var started = await tools.StartAsync(cap.Handle, "fake-target", ExternalBuildTestKit.Source(), "k1");

        await Assert.ThrowsAsync<ExternalBuildTargetNotApprovedException>(() =>
            tools.ReadArtifactAsync(cap.Handle, started.Id, "package.zip", foreign));
    }

    [Fact]
    public async Task Service_SetsFenceWhileOwned_AndClearsOnTerminal()
    {
        var provider = new FakeSnapshotBuildProvider { PollsToTerminal = 1 };
        var (service, store, _, _) = ExternalBuildTestKit.BuildService(provider);
        var queued = await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());
        Assert.False(string.IsNullOrEmpty(queued.FenceOwner));

        var terminal = await service.ReconcileAsync(queued.Id);
        Assert.Equal(ExternalBuildState.Succeeded, terminal.State);
        Assert.Null(terminal.FenceOwner);
        Assert.Null((await store.GetAsync(queued.Id))!.FenceOwner);
    }

    [Fact]
    public async Task Store_StaleFenceClaim_FailsInsteadOfOverwriting()
    {
        var provider = new FakeSnapshotBuildProvider();
        var (service, store, _, _) = ExternalBuildTestKit.BuildService(provider);
        var queued = await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());

        var stale = (await store.GetAsync(queued.Id))!;
        var fresh = await service.ReconcileAsync(queued.Id);
        Assert.NotEqual(stale.FenceEpoch, fresh.FenceEpoch);

        var hijack = stale with { State = ExternalBuildState.Cancelled };
        Assert.False(await store.TryClaimAsync(stale.Id, stale.State, stale.FenceOwner, hijack));
        Assert.NotEqual(ExternalBuildState.Cancelled, (await store.GetAsync(queued.Id))!.State);
    }

    [Fact]
    public async Task Delivery_AfterRestart_EmitsUndeliveredTerminalOnce()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xb-deliver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "state.db");
            var clock = new ControllableClock(DateTimeOffset.UtcNow);
            var opts = ExternalBuildTestKit.Options();
            var provider = new FakeSnapshotBuildProvider { PollsToTerminal = 1 };
            string buildId;
            using (var store = new SqliteExternalBuildStore(path))
            {
                var service = new ExternalBuildService(store, [provider], () => opts, clock);
                var record = await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());
                buildId = record.Id;
                var terminal = await service.ReconcileAsync(record.Id);
                Assert.Equal(ExternalBuildState.Succeeded, terminal.State);
            }
            using (var reopened = new SqliteExternalBuildStore(path))
            {
                var service2 = new ExternalBuildService(reopened, [provider], () => opts, clock);
                var history = new InMemoryExternalBuildHistory();
                var coordinator2 = new ExternalBuildParkCoordinator(service2, reopened, history, () => opts, clock);
                var resumes = new List<string>();
                var first = await coordinator2.DeliverCompletionsAsync((r, kind, ct) =>
                {
                    resumes.Add(r.Id + ":" + kind);
                    return Task.CompletedTask;
                });
                Assert.Single(first);
                Assert.Equal(buildId, first[0]);

                var coordinator3 = new ExternalBuildParkCoordinator(
                    service2, reopened, new InMemoryExternalBuildHistory(), () => opts, clock);
                var second = await coordinator3.DeliverCompletionsAsync((r, kind, ct) => Task.CompletedTask);
                Assert.Empty(second);
                Assert.Single(resumes);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Park_SurvivesRestart_AsKnownWait()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xb-park-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "state.db");
            var clock = new ControllableClock(DateTimeOffset.UtcNow);
            var opts = ExternalBuildTestKit.Options();
            var provider = new FakeSnapshotBuildProvider { PollsToTerminal = 100 };
            string buildId;
            using (var store = new SqliteExternalBuildStore(path))
            {
                var service = new ExternalBuildService(store, [provider], () => opts, clock);
                var history = new InMemoryExternalBuildHistory();
                var coordinator = new ExternalBuildParkCoordinator(service, store, history, () => opts, clock);
                var record = await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());
                buildId = record.Id;
                var stored = (await store.GetAsync(buildId))!;
                for (var i = 0; i < 5; i++)
                    await store.RecordSampleAsync(new ExternalBuildDurationSample(
                        stored.Target, TimeSpan.FromMinutes(25), true,
                        clock.GetUtcNow().AddHours(-i - 1), ColdCache: false));
                var parked = await coordinator.MaybeParkAsync(buildId, TimeSpan.FromMinutes(1), coldCache: false);
                Assert.NotNull(parked);
            }
            using (var reopened = new SqliteExternalBuildStore(path))
            {
                var service2 = new ExternalBuildService(reopened, [provider], () => opts, clock);
                var coordinator2 = new ExternalBuildParkCoordinator(
                    service2, reopened, new InMemoryExternalBuildHistory(), () => opts, clock);
                Assert.True(await coordinator2.IsParkedKnownWaitAsync(buildId));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
