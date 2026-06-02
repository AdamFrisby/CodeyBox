using System.Diagnostics.Metrics;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Exercises <see cref="CodeyBoxObservableMetrics"/> — the OTel observable
/// gauges that poll live store/process state. Uses a real SQLite store and the
/// built-in MeterListener so no OTel SDK or collector is required.
/// </summary>
[Collection("Observable metrics")]
public sealed class ObservableMetricsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cb-obs-metrics-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort */ }
    }

    private static WorkItem Item(WorkItemState state) => new()
    {
        Id = new WorkItemId(Guid.NewGuid()),
        ProjectId = new ProjectId("proj"),
        Title = "t",
        Prompt = "p",
        State = state,
    };

    private static List<(string Instrument, long Value, string? Tag)> CollectLong(
        CodeyBoxObservableMetrics svc, string tagKey, params string[] instruments)
    {
        var observed = new List<(string, long, string?)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name is "CodeyBox.Pipeline" or "CodeyBox.Sandbox" &&
                instruments.Contains(instrument.Name))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            string? tag = null;
            for (var i = 0; i < tags.Length; i++)
                if (tags[i].Key == tagKey) tag = tags[i].Value?.ToString();
            lock (observed) observed.Add((instrument.Name, value, tag));
        });
        listener.Start();
        for (var i = 0; i < 3; i++)
        {
            listener.RecordObservableInstruments();
            Thread.Sleep(TimeSpan.FromMilliseconds(10));
        }
        GC.KeepAlive(svc);
        return observed;
    }

    [Fact]
    public async Task WorkItemActiveGauge_ReportsCountPerState()
    {
        var store = new SqliteWorkItemStore(_dbPath);
        await store.CreateAsync(Item(WorkItemState.Queued));
        await store.CreateAsync(Item(WorkItemState.Queued));
        await store.CreateAsync(Item(WorkItemState.Working));

        using var svc = new CodeyBoxObservableMetrics(
            store,
            new InertSandboxProvider(),
            new OrchestratorOptions { MaxConcurrentWorkers = 8 },
            NullLogger<CodeyBoxObservableMetrics>.Instance,
            workerPool: new FakeWorkerPool(3),
            quotaSnapshot: null,
            refreshInterval: TimeSpan.FromMinutes(10));
        await svc.StartAsync(CancellationToken.None);

        var observed = CollectLong(svc, "state",
            "codeybox.work_item.active", "codeybox.workers.in_use", "codeybox.workers.max");

        Assert.Contains(observed, m => m.Instrument == "codeybox.work_item.active" && m.Tag == "Queued" && m.Value == 2);
        Assert.Contains(observed, m => m.Instrument == "codeybox.work_item.active" && m.Tag == "Working" && m.Value == 1);
        // Worker occupancy comes from the worker-pool abstraction (pool total),
        // not from summing per-agent routing reservations.
        Assert.Contains(observed, m => m.Instrument == "codeybox.workers.in_use" && m.Value == 3);
        Assert.Contains(observed, m => m.Instrument == "codeybox.workers.max" && m.Value == 8);

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SandboxActiveGauge_ReportsSandboxLiveCounter_ForEphemeralProvider()
    {
        // Non-suspending provider (process/bubblewrap fallback path) is expected
        // to surface SandboxLiveCounter.Active. Without this assertion, a
        // regression that always took the suspendable branch — or skipped the
        // Increment/Decrement calls in the ephemeral providers — would still
        // pass SandboxActiveGauge_ReportsSuspendableCount.
        var store = new SqliteWorkItemStore(_dbPath);

        // The live counter is intentionally process-wide, and other tests may
        // have ephemeral sandboxes alive at the same time or leave a non-zero
        // baseline. Use a large local contribution so this test proves the
        // gauge is reading the counter without assuming exclusive ownership of
        // the static value.
        const int localContribution = 1_000;
        for (var i = 0; i < localContribution; i++)
            SandboxLiveCounter.Increment();
        var remainingContribution = localContribution;
        try
        {
            using var svc = new CodeyBoxObservableMetrics(
                store,
                new InertSandboxProvider(),
                new OrchestratorOptions { MaxConcurrentWorkers = 4 },
                NullLogger<CodeyBoxObservableMetrics>.Instance,
                workerPool: new FakeWorkerPool(0),
                quotaSnapshot: null,
                refreshInterval: TimeSpan.FromMinutes(10));
            await svc.StartAsync(CancellationToken.None);

            var observed = CollectLong(svc, "provider", "codeybox.sandbox.active");
            Assert.Contains(observed,
                m => m.Instrument == "codeybox.sandbox.active"
                    && m.Tag == "inert"
                    && m.Value >= localContribution);

            SandboxLiveCounter.Decrement();
            remainingContribution--;
            observed = CollectLong(svc, "provider", "codeybox.sandbox.active");
            Assert.Contains(observed,
                m => m.Instrument == "codeybox.sandbox.active"
                    && m.Tag == "inert"
                    && m.Value >= localContribution - 1);

            await svc.StopAsync(CancellationToken.None);
        }
        finally
        {
            for (var i = 0; i < remainingContribution; i++)
                SandboxLiveCounter.Decrement();
        }
    }

    [Fact]
    public async Task SandboxActiveGauge_ReportsSuspendableCount()
    {
        var store = new SqliteWorkItemStore(_dbPath);
        using var svc = new CodeyBoxObservableMetrics(
            store,
            new FakeSuspendingProvider(2),
            new OrchestratorOptions { MaxConcurrentWorkers = 4 },
            NullLogger<CodeyBoxObservableMetrics>.Instance,
            workerPool: new FakeWorkerPool(0),
            quotaSnapshot: null,
            refreshInterval: TimeSpan.FromMinutes(10));
        await svc.StartAsync(CancellationToken.None);

        var observed = CollectLong(svc, "provider", "codeybox.sandbox.active");

        Assert.Contains(observed, m => m.Instrument == "codeybox.sandbox.active" && m.Value == 2 && m.Tag == "fake-vm");

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task QuotaAvailableGauge_RegisteredWhenSnapshotPresent_ReportsPerAgentPct()
    {
        var store = new SqliteWorkItemStore(_dbPath);
        var snapshot = new FakeQuotaSnapshot(
        [
            (new AgentKind("claude"), "claude-opus-4-8", 72.5),
            (new AgentKind("codex"), null, -1),
        ]);
        using var svc = new CodeyBoxObservableMetrics(
            store,
            new InertSandboxProvider(),
            new OrchestratorOptions { MaxConcurrentWorkers = 4 },
            NullLogger<CodeyBoxObservableMetrics>.Instance,
            workerPool: new FakeWorkerPool(0),
            quotaSnapshot: snapshot,
            refreshInterval: TimeSpan.FromMinutes(10));
        await svc.StartAsync(CancellationToken.None);

        var observed = new List<(double Value, string? Agent, string? Model)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "CodeyBox.Pipeline" && instrument.Name == "codeybox.agent.quota.available_pct")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            string? agent = null, model = null;
            for (var i = 0; i < tags.Length; i++)
            {
                if (tags[i].Key == "agent.kind") agent = tags[i].Value?.ToString();
                if (tags[i].Key == "model") model = tags[i].Value?.ToString();
            }
            lock (observed) observed.Add((value, agent, model));
        });
        listener.Start();
        for (var i = 0; i < 3; i++)
        {
            listener.RecordObservableInstruments();
            Thread.Sleep(TimeSpan.FromMilliseconds(10));
        }
        GC.KeepAlive(svc);

        Assert.Contains(observed, m => m.Agent == "claude" && m.Model == "claude-opus-4-8" && Math.Abs(m.Value - 72.5) < 1e-9);
        Assert.Contains(observed, m => m.Agent == "codex" && m.Model == "(default)" && m.Value == -1);

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WorkItemActiveGauge_RetainsLastGoodSnapshot_WhenStoreRefreshFails()
    {
        // First refresh (in StartAsync) succeeds and populates the snapshot;
        // every later refresh throws. The gauge must keep serving the last good
        // values and the boundary failure must be logged at Warning — a
        // regression that cleared the snapshot on failure, or swallowed the
        // error silently, would trip one of the two assertions below.
        var store = new FlakyFleetCountsStore(
            good: [("proj", (int)WorkItemState.Queued, 3, "2026-01-01T00:00:00Z")]);
        var logger = new CapturingLogger<CodeyBoxObservableMetrics>();
        using var svc = new CodeyBoxObservableMetrics(
            store,
            new InertSandboxProvider(),
            new OrchestratorOptions { MaxConcurrentWorkers = 4 },
            logger,
            workerPool: new FakeWorkerPool(0),
            quotaSnapshot: null,
            refreshInterval: TimeSpan.FromMilliseconds(50));

        await svc.StartAsync(CancellationToken.None);

        // Wait for the timer to drive at least one failing refresh.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (store.Calls < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        Assert.True(store.Calls >= 2, "expected the refresh timer to trigger a second (failing) refresh");

        var observed = CollectLong(svc, "state", "codeybox.work_item.active");
        Assert.Contains(observed, m => m.Tag == "Queued" && m.Value == 3);
        Assert.Contains(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);

        await svc.StopAsync(CancellationToken.None);
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────

    private sealed class FakeWorkerPool(int total) : IWorkerPoolOccupancy
    {
        public int CurrentlyRunningTotal => total;
    }

    private sealed class FakeQuotaSnapshot(IReadOnlyList<(AgentKind, string?, double)> rows)
        : IAgentQuotaAvailabilitySnapshot
    {
        public IReadOnlyList<(AgentKind Agent, string? ModelId, double AvailablePct)> SnapshotQuotaAvailability()
            => rows.Select(r => (r.Item1, r.Item2, r.Item3)).ToList();
    }

    private sealed class InertSandboxProvider : ISandboxProvider
    {
        public string Name => "inert";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeSuspendingProvider(int active) : ISandboxProvider, ISuspendingSandboxProvider
    {
        public string Name => "fake-vm";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;

        public IReadOnlyList<(WorkItemId WorkItemId, ISuspendableSandbox Sandbox)> SnapshotSuspendableActive() =>
            Enumerable.Range(0, active)
                .Select(_ => (new WorkItemId(Guid.NewGuid()), (ISuspendableSandbox)new FakeSuspendable()))
                .ToList();

        public Task ResumeSandboxAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeSuspendable : ISuspendableSandbox
    {
        public string Id => "fake";
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SuspendAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// IWorkItemStore that returns <paramref name="good"/> on the first
    /// <see cref="GetFleetStateCountsAsync"/> call and throws on every call
    /// after that. Only the fleet-counts read is exercised by
    /// <see cref="CodeyBoxObservableMetrics"/>; the rest of the surface throws so
    /// any unexpected call fails loudly.
    /// </summary>
    private sealed class FlakyFleetCountsStore(
        IReadOnlyList<(string ProjectId, int State, int Count, string MaxUpdatedAt)> good) : IWorkItemStore
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task<IReadOnlyList<(string ProjectId, int State, int Count, string MaxUpdatedAt)>> GetFleetStateCountsAsync(
            CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _calls);
            return n == 1
                ? Task.FromResult(good)
                : Task.FromException<IReadOnlyList<(string, int, int, string)>>(
                    new InvalidOperationException("store unavailable"));
        }

        public Task CreateAsync(WorkItem item, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(WorkItem item, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> TryUpdateIfStateAsync(WorkItem item, WorkItemState onlyIfState, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PriorityUpdateResult> UpdatePriorityAsync(WorkItemId id, int priority, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DependsOnUpdateResult> UpdateDependsOnAsync(WorkItemId id, IReadOnlyList<WorkItemId> dependsOn, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<WorkItem> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<WorkItem> ListByStateAsync(WorkItemState state, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> CountByStateAsync(WorkItemState state, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReorderAsync(IReadOnlyList<WorkItemId> orderedIds, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<WorkItem> ListDispatchEligibleByPriorityAsync(IReadOnlySet<WorkItemId> skipIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> CountStartedInWindowAsync(ProjectId projectId, DateTimeOffset since, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> CountInFlightAsync(ProjectId projectId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkItem?> GetByExternalIdAsync(ProjectId projectId, string externalId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkItem?> GetByNamespacedExternalIdAsync(ProjectId projectId, string @namespace, string externalId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkItem?> ReplaceExternalIdsAsync(WorkItemId id, IReadOnlyDictionary<string, string> externalIds, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<(string ProjectId, int State)>> GetFleetRecentOutcomesAsync(int perProject = 5, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, bool>> GetFleetPauseStatesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<WorkItem> ListByReplaySourceAsync(WorkItemId sourceId, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<WorkItem> ListSuspendedAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlySet<string>> GetActiveBaselineImageRefsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<(WorkItemId Id, string Title, WorkItemState State)>> ListWorkItemsForBaselineAsync(string baselineImageRef, CancellationToken ct = default) => throw new NotSupportedException();
        public Task OrphanReplaysAsync(WorkItemId sourceId, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<WorkItem> ListByReleaseAsync(ReleaseId releaseId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PromptReplaceResult> TryReplacePromptAsync(WorkItemId id, string newPrompt, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RecordIterationDispatchAsync(WorkItemId workItemId, int iteration, int promptRevisionAtDispatch, DateTimeOffset dispatchedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkItemIteration>> GetIterationsAsync(WorkItemId workItemId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}

[CollectionDefinition("Observable metrics", DisableParallelization = true)]
public sealed class ObservableMetricsCollection
{
}
