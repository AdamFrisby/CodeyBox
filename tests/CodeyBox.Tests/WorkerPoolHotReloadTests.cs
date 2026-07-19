using CodeyBox.Agents;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that <c>CodeyBox:WorkerPool:MaxConcurrentWorkers</c> is hot-reloadable
/// — i.e. an operator edit to the global worker-pool ceiling changes the live
/// admission cap and the surfaces (resolved-caps log,
/// <see cref="OrchestratorService.GetConcurrencyState"/>,
/// <see cref="OrchestratorService.GetStatusAsync"/>) reflect the new value
/// without a restart.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class WorkerPoolHotReloadTests
{
    // ── OrchestratorService.ApplyWorkerPoolReload (direct path) ─────────────

    [Fact]
    public void ApplyWorkerPoolReload_Grow_VisibleInConcurrencyState()
    {
        using var fixture = OrchFixture.Build(initialMaxConcurrent: 2);
        var before = fixture.Orchestrator.GetConcurrencyState();
        Assert.Equal(2, before.GlobalMaxConcurrent);

        fixture.Orchestrator.ApplyWorkerPoolReload(5);

        var after = fixture.Orchestrator.GetConcurrencyState();
        Assert.Equal(5, after.GlobalMaxConcurrent);
    }

    [Fact]
    public async Task ApplyWorkerPoolReload_Grow_AlsoVisibleInWorkersStatusEndpoint()
    {
        using var fixture = OrchFixture.Build(initialMaxConcurrent: 2);
        var before = await fixture.Orchestrator.GetStatusAsync();
        Assert.Equal(2, before.MaxConcurrent);

        fixture.Orchestrator.ApplyWorkerPoolReload(8);

        var after = await fixture.Orchestrator.GetStatusAsync();
        Assert.Equal(8, after.MaxConcurrent);
    }

    [Fact]
    public void ApplyWorkerPoolReload_Shrink_BelowInFlight_DoesNotAbortRunningWork()
    {
        using var fixture = OrchFixture.Build(initialMaxConcurrent: 3);

        // Load the GLOBAL concurrency gate — the surface ApplyWorkerPoolReload
        // resizes. Per-agent TryReserve calls would not exercise the gate at
        // all, so a regression that reset gate state during Resize (instead
        // of preserving in-flight permits) would still pass against the
        // per-route dictionary.
        Assert.True(fixture.Orchestrator.TryEnterGlobalConcurrencyGateForTest());
        Assert.True(fixture.Orchestrator.TryEnterGlobalConcurrencyGateForTest());
        Assert.True(fixture.Orchestrator.TryEnterGlobalConcurrencyGateForTest());
        Assert.Equal(3, fixture.Orchestrator.GlobalConcurrencyGateInFlightForTest);

        // Shrink the global pool well below the in-flight count. The contract:
        // existing permits keep their slots; only future admission is constrained.
        fixture.Orchestrator.ApplyWorkerPoolReload(1);

        Assert.Equal(1, fixture.Orchestrator.GetConcurrencyState().GlobalMaxConcurrent);
        Assert.Equal(3, fixture.Orchestrator.GlobalConcurrencyGateInFlightForTest);

        // Drain one permit and confirm the gate refuses to re-admit above
        // the new ceiling until in-flight drops below it.
        fixture.Orchestrator.ReleaseGlobalConcurrencyGateForTest();
        Assert.Equal(2, fixture.Orchestrator.GlobalConcurrencyGateInFlightForTest);
        Assert.False(fixture.Orchestrator.TryEnterGlobalConcurrencyGateForTest());

        fixture.Orchestrator.ReleaseGlobalConcurrencyGateForTest();
        fixture.Orchestrator.ReleaseGlobalConcurrencyGateForTest();
        Assert.Equal(0, fixture.Orchestrator.GlobalConcurrencyGateInFlightForTest);
        Assert.True(fixture.Orchestrator.TryEnterGlobalConcurrencyGateForTest());
        Assert.False(fixture.Orchestrator.TryEnterGlobalConcurrencyGateForTest());
        fixture.Orchestrator.ReleaseGlobalConcurrencyGateForTest();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void ApplyWorkerPoolReload_NonPositive_Rejected_PriorValueRetained(int badValue)
    {
        using var fixture = OrchFixture.Build(initialMaxConcurrent: 4);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            fixture.Orchestrator.ApplyWorkerPoolReload(badValue));

        // The rejected reload must leave the prior cap in effect; the
        // /concurrency surface confirms it.
        Assert.Equal(4, fixture.Orchestrator.GetConcurrencyState().GlobalMaxConcurrent);
    }

    [Fact]
    public void ApplyWorkerPoolReload_SameValue_IsIdempotent()
    {
        using var fixture = OrchFixture.Build(initialMaxConcurrent: 3);
        fixture.Orchestrator.ApplyWorkerPoolReload(3);
        Assert.Equal(3, fixture.Orchestrator.GetConcurrencyState().GlobalMaxConcurrent);
    }

    // ── AgentConfigHotReload coordinator (config-monitor path) ──────────────

    [Fact]
    public async Task Coordinator_OnChange_ResizesGlobalPool()
    {
        var initial = new CodeyBoxOptions
        {
            WorkerPool = new WorkerPoolOptions { MaxConcurrentWorkers = 2 },
        };
        await using var ctx = await CoordinatorFixture.StartAsync(initial);

        Assert.Equal(2, ctx.Orchestrator.GetConcurrencyState().GlobalMaxConcurrent);

        ctx.Monitor.Fire(new CodeyBoxOptions
        {
            WorkerPool = new WorkerPoolOptions { MaxConcurrentWorkers = 6 },
        });

        Assert.Equal(6, ctx.Orchestrator.GetConcurrencyState().GlobalMaxConcurrent);
    }

    [Fact]
    public async Task Coordinator_OnChange_InvalidValue_KeepsPriorPoolSize()
    {
        var initial = new CodeyBoxOptions
        {
            WorkerPool = new WorkerPoolOptions { MaxConcurrentWorkers = 3 },
        };
        await using var ctx = await CoordinatorFixture.StartAsync(initial);
        Assert.Equal(3, ctx.Orchestrator.GetConcurrencyState().GlobalMaxConcurrent);

        // Fire a reload with a value the orchestrator must reject. The
        // coordinator's per-block try/catch keeps the service alive and the
        // prior cap remains in effect.
        ctx.Monitor.Fire(new CodeyBoxOptions
        {
            WorkerPool = new WorkerPoolOptions { MaxConcurrentWorkers = 0 },
        });

        Assert.Equal(3, ctx.Orchestrator.GetConcurrencyState().GlobalMaxConcurrent);
    }

    // ── Grow path kicks the dispatcher (dispatch-wake fan-out) ──────────────

    [Fact]
    public void ApplyWorkerPoolReload_Grow_EnqueuesDispatchWakesForNewSlots()
    {
        // The dispatcher's typical steady state is parked on
        // DequeueDispatchSignalAsync (the kick channel), NOT on the gate. The
        // gate's DrainWaiters does NOT kick the dispatcher in that state, so
        // a grow that only resized the gate would leave the new capacity
        // idle until the next slot-release. ApplyWorkerPoolReload must
        // therefore enqueue (newTarget - oldTarget) dispatch wakes on grow.
        using var fixture = OrchFixture.Build(initialMaxConcurrent: 2);
        var before = fixture.Queue.Count;

        fixture.Orchestrator.ApplyWorkerPoolReload(5);

        var added = fixture.Queue.Count - before;
        Assert.Equal(3, added);
    }

    [Fact]
    public void ApplyWorkerPoolReload_Shrink_DoesNotEnqueueDispatchWakes()
    {
        using var fixture = OrchFixture.Build(initialMaxConcurrent: 5);
        var before = fixture.Queue.Count;

        fixture.Orchestrator.ApplyWorkerPoolReload(2);

        var added = fixture.Queue.Count - before;
        Assert.Equal(0, added);
    }

    [Fact]
    public void ApplyWorkerPoolReload_SameValue_DoesNotEnqueueDispatchWakes()
    {
        // Idempotent reload (same value) must be a true no-op — no kick spam
        // on every config-file save.
        using var fixture = OrchFixture.Build(initialMaxConcurrent: 3);
        var before = fixture.Queue.Count;

        fixture.Orchestrator.ApplyWorkerPoolReload(3);

        Assert.Equal(before, fixture.Queue.Count);
    }

    [Fact]
    public async Task SlotReleaseWakeBudget_HonorsResizedCeiling_AfterShrink()
    {
        // The slot-released wake fan-out (EnqueueSlotReleasedDispatchWakeAsync)
        // sizes itself off the live ceiling so a shrink visibly reduces the
        // fan-out. The dispatch loop's TryEnter would converge regardless, but
        // a stale read of _opts.MaxConcurrentWorkers here would over-enqueue
        // after a shrink — the exact pattern the task brief flagged.
        using var fixture = OrchFixture.Build(initialMaxConcurrent: 5);
        fixture.Orchestrator.ApplyWorkerPoolReload(2);

        // Drain the grow-wake buffer (if any) and the wakes the shrink
        // itself did not enqueue, so the next reading isolates the
        // slot-release fan-out cleanly.
        await DrainQueueAsync(fixture.Queue);

        // Mint one in-flight permit on the gate and release it through the
        // same code path the dispatcher uses on worker completion.
        Assert.True(fixture.Orchestrator.TryEnterGlobalConcurrencyGateForTest());
        fixture.Orchestrator.ReleaseGlobalConcurrencyGateForTest();
        // Fire one slot-released wake directly (the orchestrator's own
        // completion path runs ReleaseCompletedWorkerSlotLeaseAsync, which
        // is internal; the public observable is the wake count this method
        // produces). The internal helper exposed for this test mirrors the
        // production fan-out.
        await fixture.Orchestrator.FireSlotReleasedWakeForTestAsync();

        // freeSlots = max(1, target - inFlight) = max(1, 2 - 0) = 2.
        Assert.Equal(2, fixture.Queue.Count);
    }

    [Fact]
    public async Task SlotReleaseWakeBudget_HonorsResizedCeiling_AfterGrow()
    {
        // After a grow from 2 to 5, the slot-release wake fan-out must use
        // the new ceiling (5) — a stale read of _opts.MaxConcurrentWorkers
        // would only fan out 2 wakes, blunting the reload.
        using var fixture = OrchFixture.Build(initialMaxConcurrent: 2);
        fixture.Orchestrator.ApplyWorkerPoolReload(5);

        await DrainQueueAsync(fixture.Queue);

        await fixture.Orchestrator.FireSlotReleasedWakeForTestAsync();

        // freeSlots = max(1, 5 - 0) = 5.
        Assert.Equal(5, fixture.Queue.Count);
    }

    private static async Task DrainQueueAsync(InMemoryTaskQueue queue)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (queue.Count > 0)
            await queue.DequeueDispatchSignalAsync(cts.Token);
    }

    [Fact]
    public async Task Coordinator_OnChange_LegacyConcurrencyFallback_Applies()
    {
        // The startup factory accepts the deprecated top-level
        // CodeyBox:Concurrency key as the source of MaxConcurrentWorkers when
        // WorkerPool:MaxConcurrentWorkers is unset. Hot-reload must mirror
        // that fallback so an operator editing the legacy key sees the same
        // behavior as the explicit one.
        var initial = new CodeyBoxOptions
        {
            WorkerPool = new WorkerPoolOptions { MaxConcurrentWorkers = 2 },
        };
        await using var ctx = await CoordinatorFixture.StartAsync(initial);
        Assert.Equal(2, ctx.Orchestrator.GetConcurrencyState().GlobalMaxConcurrent);

        ctx.Monitor.Fire(new CodeyBoxOptions
        {
            Concurrency = 7,
            WorkerPool = new WorkerPoolOptions(),
        });

        Assert.Equal(7, ctx.Orchestrator.GetConcurrencyState().GlobalMaxConcurrent);
    }

    // ─── fixtures ────────────────────────────────────────────────────────────

    private sealed class OrchFixture : IDisposable
    {
        public OrchestratorService Orchestrator { get; private init; } = null!;
        public InMemoryTaskQueue Queue { get; private init; } = null!;
        private SqliteWorkItemStore? _store;
        private string? _dbPath;

        public static OrchFixture Build(int initialMaxConcurrent)
        {
            var dbPath = Path.Combine(
                Path.GetTempPath(),
                $"cb-wp-hotreload-{Guid.NewGuid():N}.db");
            var store = new SqliteWorkItemStore(dbPath);
            var queue = new InMemoryTaskQueue();
            var orch = new OrchestratorService(
                queue,
                store,
                new NoopPipeline(),
                new CancellationRegistry(CancellationToken.None),
                new OrchestratorOptions { MaxConcurrentWorkers = initialMaxConcurrent },
                NullLogger<OrchestratorService>.Instance,
                agentConcurrency: new AgentConcurrencyOptions());
            return new OrchFixture { Orchestrator = orch, Queue = queue, _store = store, _dbPath = dbPath };
        }

        public void Dispose()
        {
            _store?.Dispose();
            if (_dbPath is not null) { TestTempArtifacts.DeleteSqliteDatabase(_dbPath); }
        }
    }

    private sealed class CoordinatorFixture : IAsyncDisposable
    {
        public OrchestratorService Orchestrator { get; }
        public ManualMonitor<CodeyBoxOptions> Monitor { get; }
        private readonly AgentConfigHotReload _coordinator;
        private readonly SqliteWorkItemStore _store;
        private readonly string _dbPath;

        private CoordinatorFixture(
            AgentConfigHotReload coordinator,
            ManualMonitor<CodeyBoxOptions> monitor,
            OrchestratorService orchestrator,
            SqliteWorkItemStore store,
            string dbPath)
        {
            _coordinator = coordinator;
            Monitor = monitor;
            Orchestrator = orchestrator;
            _store = store;
            _dbPath = dbPath;
        }

        public static async Task<CoordinatorFixture> StartAsync(CodeyBoxOptions initial)
        {
            var monitor = new ManualMonitor<CodeyBoxOptions>(initial);
            var router = new AgentClassRouter(
                AgentClassesConfigBuilder.Build(initial.AgentClasses, NullLogger<AgentClassRouter>.Instance),
                Array.Empty<IAgentQuotaProbe>(),
                new QuotaRouterOptions { MinQuotaPct = 5.0 },
                NullLogger<AgentClassRouter>.Instance);
            var dbPath = Path.Combine(
                Path.GetTempPath(),
                $"cb-wp-coord-{Guid.NewGuid():N}.db");
            var store = new SqliteWorkItemStore(dbPath);
            var initialMax = initial.WorkerPool.MaxConcurrentWorkers
                ?? initial.Concurrency
                ?? 1;
            var orch = new OrchestratorService(
                new InMemoryTaskQueue(),
                store,
                new NoopPipeline(),
                new CancellationRegistry(CancellationToken.None),
                new OrchestratorOptions { MaxConcurrentWorkers = initialMax },
                NullLogger<OrchestratorService>.Instance,
                agentConcurrency: initial.AgentConcurrency);
            var burn = new AgentBurnEstimator(
                new InertCosts(),
                initial.AgentBurnEstimator,
                NullLogger<AgentBurnEstimator>.Instance);
            var coordinator = new AgentConfigHotReload(
                monitor, orch, router, burn,
                NullLogger<AgentConfigHotReload>.Instance);
            await coordinator.StartAsync(CancellationToken.None);
            return new CoordinatorFixture(coordinator, monitor, orch, store, dbPath);
        }

        public async ValueTask DisposeAsync()
        {
            await _coordinator.StopAsync(CancellationToken.None);
            _store.Dispose();
            TestTempArtifacts.DeleteSqliteDatabase(_dbPath);
        }
    }

    private sealed class ManualMonitor<T> : IOptionsMonitor<T>
    {
        private T _value;
        private readonly List<Action<T, string?>> _listeners = new();
        private readonly Lock _gate = new();

        public ManualMonitor(T initial) { _value = initial; }

        public T CurrentValue => _value;
        public T Get(string? name) => _value;

        public IDisposable OnChange(Action<T, string?> listener)
        {
            lock (_gate) _listeners.Add(listener);
            return new Subscription(() => { lock (_gate) _listeners.Remove(listener); });
        }

        public void Fire(T next)
        {
            _value = next;
            Action<T, string?>[] snapshot;
            lock (_gate) snapshot = _listeners.ToArray();
            foreach (var l in snapshot) l(next, null);
        }

        private sealed class Subscription : IDisposable
        {
            private readonly Action _onDispose;
            public Subscription(Action onDispose) { _onDispose = onDispose; }
            public void Dispose() => _onDispose();
        }
    }

    private sealed class NoopPipeline : IPipelineRunner
    {
        public Task RunAsync(WorkItem item, CancellationToken phaseCt, CancellationToken hostCt) =>
            Task.CompletedTask;
    }

    private sealed class InertCosts : IWorkItemCostStore, IRecentCostsByAgentQueryable
    {
        public Task<(long AvgTokens, int Samples)> GetAvgTokensPerItemAsync(
            string agentKind, int limit, CancellationToken ct = default) =>
            Task.FromResult<(long, int)>((0L, 0));

        public Task RecordAsync(WorkItemCost cost, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<WorkItemCost>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkItemCost>>(Array.Empty<WorkItemCost>());
        public Task<IReadOnlyList<WorkItemCost>> GetByProjectAsync(string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkItemCost>>(Array.Empty<WorkItemCost>());
        public Task<IReadOnlyList<(string ProjectId, double TotalUsd)>> GetFleetCostSummaryAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(string, double)>>(Array.Empty<(string, double)>());
        public Task DeleteByWorkItemAsync(string workItemId, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<decimal> SumEstimatedUsdAsync(string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult(0m);
    }
}
