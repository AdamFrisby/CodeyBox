using CodeyBox.Core.ExternalBuilds;
using CodeyBox.Orchestrator.ExternalBuilds;

namespace CodeyBox.Tests.ExternalBuilds;

/// <summary>Lifecycle: intent-before-dispatch, idempotent replay, uncertain
/// reconciliation without duplicate paid runs, crash windows, and completion
/// delivery races.</summary>
public sealed class ExternalBuildLifecycleTests
{
    [Fact]
    public async Task Start_PersistsIntentBeforeDispatch_ThenQueuesWithRunId()
    {
        var provider = new FakeSnapshotBuildProvider();
        var (service, store, _, _) = ExternalBuildTestKit.BuildService(provider);

        var record = await service.StartAsync(
            ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());

        Assert.Equal(ExternalBuildState.Queued, record.State);
        Assert.NotNull(record.ProviderRunId);
        Assert.NotEmpty(record.RequestId);
        var stored = await store.GetAsync(record.Id);
        Assert.NotNull(stored);
        Assert.Equal(record.ProviderRunId, stored.ProviderRunId);
    }

    [Fact]
    public async Task Start_IsIdempotent_OnSameKeyAndBody()
    {
        var provider = new FakeSnapshotBuildProvider();
        var (service, _, _, _) = ExternalBuildTestKit.BuildService(provider);

        var first = await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());
        var second = await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, provider.SubmitCalls);
    }

    [Fact]
    public async Task UncertainSubmit_ReconcilesWithoutDuplicatePaidRun()
    {
        var provider = new FakeSnapshotBuildProvider { FailNextSubmitUncertain = true };
        var (service, _, _, _) = ExternalBuildTestKit.BuildService(provider);

        var uncertain = await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());
        Assert.Equal(ExternalBuildState.SubmitUncertain, uncertain.State);
        Assert.Null(uncertain.ProviderRunId);

        var reconciled = await service.ReconcileAsync(uncertain.Id);
        Assert.Equal(ExternalBuildState.Queued, reconciled.State);
        Assert.NotNull(reconciled.ProviderRunId);

        // Only two submits happened (uncertain + one retry), and the retry
        // produced exactly one provider run: no duplicate paid run.
        Assert.Equal(2, provider.SubmitCalls);

        // A repeated reconcile-driven retry still yields the same run via
        // request-identity dedup, never a second run.
        var again = await service.ReconcileAsync(reconciled.Id);
        Assert.Equal(reconciled.ProviderRunId, again.ProviderRunId);
    }

    [Fact]
    public async Task UncertainSubmit_RetryBound_YieldsReconciliationBlocked_NotSilentPass()
    {
        var provider = new FakeSnapshotBuildProvider { FailNextSubmitUncertain = true };
        var (service, _, _, _) = ExternalBuildTestKit.BuildService(
            provider, o => o.MaxDispatchAttempts = 1);

        var uncertain = await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());
        var blocked = await service.ReconcileAsync(uncertain.Id);

        Assert.Equal(ExternalBuildState.ReconciliationBlocked, blocked.State);
        var gate = ExternalBuildEvidenceGate.Check(blocked, new string('a', 64), new string('b', 64));
        Assert.False(gate.Passed);
    }

    [Fact]
    public async Task Polling_DrivesQueuedToSucceeded_WithAuthoritativeEvidence()
    {
        var provider = new FakeSnapshotBuildProvider { PollsToTerminal = 2 };
        var (service, _, _, _) = ExternalBuildTestKit.BuildService(provider);

        var record = await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());
        record = await service.ReconcileAsync(record.Id);
        record = await service.ReconcileAsync(record.Id);

        Assert.Equal(ExternalBuildState.Succeeded, record.State);
        Assert.NotNull(record.Evidence);
        Assert.True(record.Evidence.Authoritative);
        Assert.Equal(ExternalBuildTerminalCause.ProviderSucceeded, record.TerminalCause);
    }

    [Fact]
    public async Task CrashBetweenIntentAndAcceptance_RecoversViaRestartedService()
    {
        var provider = new FakeSnapshotBuildProvider { FailNextSubmitUncertain = true };
        var clock = new ControllableClock(DateTimeOffset.UtcNow);
        var opts = ExternalBuildTestKit.Options();
        var store = new InMemoryExternalBuildStore();
        var before = new ExternalBuildService(store, [provider], () => opts, clock);
        var uncertain = await before.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());

        // "Restart": a fresh service over the same durable store reconciles.
        var after = new ExternalBuildService(store, [provider], () => opts, clock);
        var recovered = await after.ReconcileAsync(uncertain.Id);

        Assert.Equal(ExternalBuildState.Queued, recovered.State);
        Assert.NotNull(recovered.ProviderRunId);
    }

    [Fact]
    public async Task Completion_DeliveredOnce_DespiteDuplicateDrain()
    {
        var provider = new FakeSnapshotBuildProvider { PollsToTerminal = 1 };
        var (service, store, clock, opts) = ExternalBuildTestKit.BuildService(provider);
        var history = new InMemoryExternalBuildHistory();
        var coordinator = new ExternalBuildParkCoordinator(service, store, history, () => opts, clock);

        var record = await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());
        await service.ReconcileAsync(record.Id);

        var resumes = new List<string>();
        var first = await coordinator.DeliverCompletionsAsync((r, kind, ct) =>
        {
            resumes.Add(r.Id + ":" + kind);
            return Task.CompletedTask;
        });
        var second = await coordinator.DeliverCompletionsAsync((r, kind, ct) =>
        {
            resumes.Add(r.Id + ":" + kind);
            return Task.CompletedTask;
        });

        Assert.Single(first);
        Assert.Empty(second);
        Assert.Single(resumes);
        Assert.StartsWith(record.Id + ":completed", resumes[0]);
    }

    [Fact]
    public async Task SqliteStore_RestartReadsBackRecord_AndBackwardReadsV1()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xb-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "state.db");
            var clock = new ControllableClock(DateTimeOffset.UtcNow);
            var opts = ExternalBuildTestKit.Options();
            var provider = new FakeSnapshotBuildProvider();
            using (var store = new SqliteExternalBuildStore(path))
            {
                var service = new ExternalBuildService(store, [provider], () => opts, clock);
                var record = await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());
                Assert.Equal(ExternalBuildState.Queued, record.State);
            }
            using (var reopened = new SqliteExternalBuildStore(path))
            {
                var list = await reopened.ListActiveAsync("proj");
                Assert.Single(list);
                Assert.Equal(1, list[0].SchemaVersion);
                var service2 = new ExternalBuildService(reopened, [provider], () => opts, clock);
                var recovered = await service2.ReconcileAsync(list[0].Id);
                Assert.NotNull(recovered.ProviderRunId);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DisabledFeature_RejectsStart()
    {
        var provider = new FakeSnapshotBuildProvider();
        var clock = new ControllableClock(DateTimeOffset.UtcNow);
        var opts = ExternalBuildTestKit.Options();
        opts.Enabled = false;
        var service = new ExternalBuildService(new InMemoryExternalBuildStore(), [provider], () => opts, clock);
        await Assert.ThrowsAsync<ExternalBuildNotEnabledException>(() =>
            service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput()));
    }

    [Fact]
    public async Task UnapprovedTarget_Rejected()
    {
        var provider = new FakeSnapshotBuildProvider();
        var (service, _, _, _) = ExternalBuildTestKit.BuildService(provider);
        var request = ExternalBuildTestKit.Request() with { ApprovedTargetName = "evil-custom-endpoint" };
        await Assert.ThrowsAsync<ExternalBuildTargetNotApprovedException>(() =>
            service.StartAsync(request, new ExternalBuildSubmitInput()));
    }
}
