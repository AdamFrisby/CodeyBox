using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// OrchestratorService-level escrow lifecycle: the ledger alone cannot prove
/// the worker-slot wiring — calling <see cref="QuotaReservationLedger.Complete"/>
/// directly mocks away the <see cref="OrchestratorService"/> finally block and
/// the recovery reaper. These tests run a real worker loop with a real ledger,
/// a real <see cref="AgentClassRouter"/>, and a fake <see cref="IWorkItemCostStore"/>,
/// so deleting the finally-block reconcile or the reaper orphan-release turns
/// them red.
/// </summary>
[Collection("Background service timing")]
public sealed class QuotaReservationEscrowLifecycleTests : IDisposable
{
    private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(10);

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-escrow-lifecycle-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public QuotaReservationEscrowLifecycleTests() => _store = new SqliteWorkItemStore(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static QuotaRouterOptions EscrowOptions(double estimate = 4.0) => new()
    {
        MinQuotaPct = 10.0,
        QuotaRecheckInterval = TimeSpan.FromMinutes(5),
        CapRetryRecheckInterval = TimeSpan.FromSeconds(15),
        DispatchReservationEstimatePct = estimate,
        DispatchReservationMinPct = 0.5,
        DispatchReservationMaxPct = 25.0,
        QuotaReservationMaxAge = TimeSpan.FromHours(6),
    };

    private static AgentBurnEstimatorOptions BurnOptions(long budget = 100_000) => new()
    {
        WindowTokenBudget = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = budget,
        },
    };

    private static AgentClass FrontierClass() => new()
    {
        Id = "frontier",
        DisplayName = "Frontier",
        Members =
        [
            new AgentMembership
            {
                Agent = AgentKind.Claude,
                Billing = AgentBilling.Subscription,
                QualityScore = 100,
            },
        ],
    };

    private static WorkItem EscrowItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Queued,
        AgentClassId = "frontier",
        MinModelScore = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static WorkItemCost CostRow(WorkItemId id, long input, long output, DateTimeOffset startedAt) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        WorkItemId = id.ToString(),
        Phase = "work",
        Iteration = null,
        AgentKind = "claude",
        InputTokens = (int)input,
        OutputTokens = (int)output,
        EstimatedUsd = 0.01,
        StartedAt = startedAt,
        EndedAt = startedAt.AddSeconds(1),
        HasExtractedTokenUsage = true,
    };

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(25);
        }
        return predicate();
    }

    private static async Task<WorkerRegistration?> WaitForWorkerRegistrationAsync(
        IWorkerRegistry registry, WorkItemId workItemId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var worker = (await registry.ListAsync())
                .FirstOrDefault(w => w.CurrentWorkItemId == workItemId.ToString());
            if (worker is not null) return worker;
            await Task.Delay(25);
        }
        return (await registry.ListAsync())
            .FirstOrDefault(w => w.CurrentWorkItemId == workItemId.ToString());
    }

    [Fact]
    public async Task PhaseSuccess_ReconcilesEstimateToObservedViaWorkerLifecycle()
    {
        var item = EscrowItem();
        await _store.CreateAsync(item);

        var opts = EscrowOptions();
        var ledger = new QuotaReservationLedger(opts);
        var costs = new InMemoryCostStore();
        var pipeline = new EscrowPipeline(_store, async (run, ct) =>
        {
            await costs.RecordAsync(CostRow(item.Id, input: 1000, output: 500, DateTimeOffset.UtcNow), ct);
            await _store.UpdateAsync(run.With(WorkItemState.Done), ct);
        });
        var router = new AgentClassRouter(
            [FrontierClass()],
            [new EscrowProbe(AgentKind.Claude, 20.0)],
            opts,
            NullLogger<AgentClassRouter>.Instance,
            reservationLedger: ledger);
        var queue = new InMemoryTaskQueue();
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            router: router,
            reservationLedger: ledger,
            costStore: costs,
            burnEstimatorOptions: BurnOptions());

        await svc.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(item.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(item.Id, DispatchTimeout));
        Assert.True(
            await WaitUntilAsync(
                () => Math.Abs(ledger.GetOutstandingPct("claude") - 4.0) < 1e-5, SettleTimeout),
            "Authorising the dispatch must escrow the 4-point estimate via the worker path.");
        Assert.True(await pipeline.WaitForExitedAsync(item.Id, DispatchTimeout));

        // 1500 tokens against a 100k budget reconcile to 1.5%: the estimate is
        // replaced, not merely released. Without the worker-exit finally this
        // stays at the 4-point estimate.
        Assert.True(
            await WaitUntilAsync(
                () => Math.Abs(ledger.GetOutstandingPct("claude") - 1.5) < 1e-5, SettleTimeout),
            "Phase success must reconcile the estimate to observed usage.");
        Assert.Equal(1, ledger.GetReservationCount(new AgentMembership
        {
            Agent = AgentKind.Claude,
            Billing = AgentBilling.Subscription,
            QualityScore = 100,
        }));

        // A newer probe reading supersedes the reconciled tail instead of
        // double-counting it.
        ledger.NoteProbeReading("claude", 18.5, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(0.0, ledger.GetOutstandingPct("claude"), precision: 5);

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PhaseWithoutExtractedUsage_RetainsEstimateViaWorkerLifecycle()
    {
        // The copilot-shaped defect: the run produced a cost row with zero
        // tokens and has_extracted_token_usage = 0. Settling must retain the
        // reserved estimate instead of releasing the run as free.
        var item = EscrowItem();
        await _store.CreateAsync(item);

        var opts = EscrowOptions();
        var ledger = new QuotaReservationLedger(opts);
        var costs = new InMemoryCostStore();
        var pipeline = new EscrowPipeline(_store, async (run, ct) =>
        {
            var row = CostRow(item.Id, input: 0, output: 0, DateTimeOffset.UtcNow) with
            {
                HasExtractedTokenUsage = false,
            };
            await costs.RecordAsync(row, ct);
            await _store.UpdateAsync(run.With(WorkItemState.Done), ct);
        });
        var router = new AgentClassRouter(
            [FrontierClass()],
            [new EscrowProbe(AgentKind.Claude, 20.0)],
            opts,
            NullLogger<AgentClassRouter>.Instance,
            reservationLedger: ledger);
        var queue = new InMemoryTaskQueue();
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            router: router,
            reservationLedger: ledger,
            costStore: costs,
            burnEstimatorOptions: BurnOptions());

        await svc.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(item.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(item.Id, DispatchTimeout));
        Assert.True(
            await WaitUntilAsync(
                () => Math.Abs(ledger.GetOutstandingPct("claude") - 4.0) < 1e-5, SettleTimeout),
            "Authorising the dispatch must escrow the 4-point estimate via the worker path.");
        Assert.True(await pipeline.WaitForExitedAsync(item.Id, DispatchTimeout));

        // The unmeasured run consumed quota: the worker-exit settle retains
        // the estimate (still escrowed, counted as retained) instead of
        // releasing it. Without the extraction gate this drops to zero.
        Assert.True(
            await WaitUntilAsync(
                () => ledger.GetSettlementStats("claude").RetainedAtEstimate == 1, SettleTimeout),
            "Phase without extracted usage must retain the estimate via the worker-exit settle.");
        Assert.Equal(4.0, ledger.GetOutstandingPct("claude"), precision: 5);
        Assert.Equal(1, ledger.GetReservationCount(new AgentMembership
        {
            Agent = AgentKind.Claude,
            Billing = AgentBilling.Subscription,
            QualityScore = 100,
        }));
        Assert.Equal(0, ledger.GetSettlementStats("claude").SettledFromActuals);

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PhaseFailure_ReleasesReservationViaWorkerLifecycle()
    {
        var item = EscrowItem();
        await _store.CreateAsync(item);

        var opts = EscrowOptions();
        var ledger = new QuotaReservationLedger(opts);
        var costs = new InMemoryCostStore();
        var pipeline = new EscrowPipeline(_store, async (run, ct) =>
        {
            await _store.UpdateAsync(run.With(WorkItemState.Failed, "test failure"), ct);
            throw new InvalidOperationException("test phase failure");
        });
        var router = new AgentClassRouter(
            [FrontierClass()],
            [new EscrowProbe(AgentKind.Claude, 20.0)],
            opts,
            NullLogger<AgentClassRouter>.Instance,
            reservationLedger: ledger);
        var queue = new InMemoryTaskQueue();
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            router: router,
            reservationLedger: ledger,
            costStore: costs,
            burnEstimatorOptions: BurnOptions());

        await svc.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(item.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(item.Id, DispatchTimeout));
        Assert.True(await pipeline.WaitForExitedAsync(item.Id, DispatchTimeout));

        // No cost rows: nothing measurable ran, so the estimate is released
        // outright. Without the worker-exit finally this stays escrowed.
        Assert.True(
            await WaitUntilAsync(
                () => Math.Abs(ledger.GetOutstandingPct("claude")) < 1e-5, SettleTimeout),
            "Phase failure must release the reservation via the worker-exit finally.");
        Assert.Equal(0, ledger.GetReservationCount(new AgentMembership
        {
            Agent = AgentKind.Claude,
            Billing = AgentBilling.Subscription,
            QualityScore = 100,
        }));

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Cancellation_ReleasesReservationViaWorkerLifecycle()
    {
        var item = EscrowItem();
        await _store.CreateAsync(item);

        var opts = EscrowOptions();
        var ledger = new QuotaReservationLedger(opts);
        var costs = new InMemoryCostStore();
        var pipeline = new EscrowPipeline(_store, async (run, ct) =>
        {
            await _store.UpdateAsync(run.With(WorkItemState.Cancelled, "test cancel"), ct);
            throw new OperationCanceledException("test cancellation");
        });
        var router = new AgentClassRouter(
            [FrontierClass()],
            [new EscrowProbe(AgentKind.Claude, 20.0)],
            opts,
            NullLogger<AgentClassRouter>.Instance,
            reservationLedger: ledger);
        var queue = new InMemoryTaskQueue();
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            router: router,
            reservationLedger: ledger,
            costStore: costs,
            burnEstimatorOptions: BurnOptions());

        await svc.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(item.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(item.Id, DispatchTimeout));
        Assert.True(await pipeline.WaitForExitedAsync(item.Id, DispatchTimeout));

        Assert.True(
            await WaitUntilAsync(
                () => Math.Abs(ledger.GetOutstandingPct("claude")) < 1e-5, SettleTimeout),
            "Cancellation must release the reservation via the worker-exit finally.");
        Assert.Equal(0, ledger.GetReservationCount(new AgentMembership
        {
            Agent = AgentKind.Claude,
            Billing = AgentBilling.Subscription,
            QualityScore = 100,
        }));

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DeadWorker_ReaperReleasesOrphanedReservation()
    {
        var item = EscrowItem();
        await _store.CreateAsync(item);

        var opts = EscrowOptions();
        var ledger = new QuotaReservationLedger(opts);
        var costs = new InMemoryCostStore();
        var pipeline = new BlockingPipeline(_store);
        var router = new AgentClassRouter(
            [FrontierClass()],
            [new EscrowProbe(AgentKind.Claude, 20.0)],
            opts,
            NullLogger<AgentClassRouter>.Instance,
            reservationLedger: ledger);
        var queue = new InMemoryTaskQueue();
        using var workerRegistry = new SqliteWorkerRegistry(_dbPath, NullLogger<SqliteWorkerRegistry>.Instance);
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            router: router,
            workerRegistry: workerRegistry,
            deadWorkerOpts: new DeadWorkerOptions(),
            reservationLedger: ledger,
            costStore: costs,
            burnEstimatorOptions: BurnOptions());

        await svc.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(item.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(item.Id, DispatchTimeout));
        var worker = await WaitForWorkerRegistrationAsync(workerRegistry, item.Id, DispatchTimeout);
        Assert.NotNull(worker);
        Assert.True(
            await WaitUntilAsync(
                () => Math.Abs(ledger.GetOutstandingPct("claude") - 4.0) < 1e-5, SettleTimeout),
            "The blocked worker must hold the 4-point escrow mid-phase.");

        // Mark worker-owned so the recovery release does not fan out a wake
        // that could re-dispatch the still-running item.
        await _store.UpdateAsync(item with
        {
            State = WorkItemState.Working,
            StartedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        // The worker died without running its exit finally: the recovery path
        // releases the slot and the paired quota escrow. Without the
        // orphan-release this stays escrowed until the TTL sweep.
        Assert.True(await svc.TryReleaseRecoveredWorkerSlotAsync(
            worker!.WorkerId, item.Id, "test dead-worker recovery"));
        Assert.Equal(0.0, ledger.GetOutstandingPct("claude"), precision: 5);

        pipeline.Release(item.Id);
        Assert.True(await pipeline.WaitForDoneAsync(item.Id, DispatchTimeout));
        Assert.Equal(0.0, ledger.GetOutstandingPct("claude"), precision: 5);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void CustomKeyProvider_IsolatesOutstandingByCustomKey()
    {
        var opts = EscrowOptions();
        var ledger = new QuotaReservationLedger(
            opts,
            time: null,
            keyProvider: m => m.InstanceId ?? m.Agent.Value);
        var shared = new AgentMembership
        {
            Agent = AgentKind.Claude,
            Billing = AgentBilling.Subscription,
            QualityScore = 100,
            InstanceId = "acct-a",
        };
        var other = shared with { InstanceId = "acct-b" };

        Assert.True(ledger.TryReserve(shared, 20.0, 10.0).Allowed);
        Assert.Equal(4.0, ledger.GetOutstandingPct(shared), precision: 5);
        Assert.Equal(0.0, ledger.GetOutstandingPct(other), precision: 5);
        Assert.Equal(0.0, ledger.GetOutstandingPct("claude"), precision: 5);
    }

    private sealed class EscrowProbe : IAgentQuotaProbe
    {
        private readonly AgentQuotaSnapshot _snapshot;
        public EscrowProbe(AgentKind kind, double availablePct)
        {
            Kind = kind;
            _snapshot = new AgentQuotaSnapshot { AvailablePct = availablePct };
        }
        public AgentKind Kind { get; }
        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct) =>
            Task.FromResult(_snapshot);
    }

    private sealed class InMemoryCostStore : IWorkItemCostStore
    {
        private readonly object _sync = new();
        private readonly List<WorkItemCost> _rows = [];

        public Task RecordAsync(WorkItemCost cost, CancellationToken ct = default)
        {
            lock (_sync) _rows.Add(cost);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkItemCost>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default)
        {
            lock (_sync)
            {
                IReadOnlyList<WorkItemCost> copy = _rows
                    .Where(r => string.Equals(r.WorkItemId, workItemId, StringComparison.Ordinal))
                    .ToList();
                return Task.FromResult(copy);
            }
        }

        public Task<IReadOnlyList<WorkItemCost>> GetByProjectAsync(string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkItemCost>>(Array.Empty<WorkItemCost>());

        public Task<IReadOnlyList<(string ProjectId, double TotalUsd)>> GetFleetCostSummaryAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(string ProjectId, double TotalUsd)>>(Array.Empty<(string, double)>());

        public Task DeleteByWorkItemAsync(string workItemId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<decimal> SumEstimatedUsdAsync(string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult(0m);
    }

    private sealed class EscrowPipeline : IPipelineRunner
    {
        private readonly IWorkItemStore _store;
        private readonly Func<WorkItem, CancellationToken, Task> _behavior;
        private readonly ConcurrentDictionary<WorkItemId, TaskCompletionSource> _entered = new();
        private readonly ConcurrentDictionary<WorkItemId, TaskCompletionSource> _exited = new();

        public EscrowPipeline(IWorkItemStore store, Func<WorkItem, CancellationToken, Task> behavior)
        {
            _store = store;
            _behavior = behavior;
        }

        public Task<bool> WaitForEnteredAsync(WorkItemId id, TimeSpan timeout) =>
            WaitForSignalAsync(_entered.GetOrAdd(id, static _ => NewSignal()), timeout);

        public Task<bool> WaitForExitedAsync(WorkItemId id, TimeSpan timeout) =>
            WaitForSignalAsync(_exited.GetOrAdd(id, static _ => NewSignal()), timeout);

        public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            _entered.GetOrAdd(item.Id, static _ => NewSignal()).TrySetResult();
            try
            {
                await _behavior(item, ct);
            }
            finally
            {
                _exited.GetOrAdd(item.Id, static _ => NewSignal()).TrySetResult();
            }
        }

        private static async Task<bool> WaitForSignalAsync(TaskCompletionSource signal, TimeSpan timeout)
        {
            var completed = await Task.WhenAny(signal.Task, Task.Delay(timeout));
            return completed == signal.Task;
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class BlockingPipeline : IPipelineRunner
    {
        private readonly IWorkItemStore _store;
        private readonly ConcurrentDictionary<WorkItemId, TaskCompletionSource> _entered = new();
        private readonly ConcurrentDictionary<WorkItemId, TaskCompletionSource> _released = new();
        private readonly ConcurrentDictionary<WorkItemId, TaskCompletionSource> _done = new();

        public BlockingPipeline(IWorkItemStore store) => _store = store;

        public void Release(WorkItemId id) =>
            _released.GetOrAdd(id, static _ => NewSignal()).TrySetResult();

        public Task<bool> WaitForEnteredAsync(WorkItemId id, TimeSpan timeout) =>
            WaitForSignalAsync(_entered.GetOrAdd(id, static _ => NewSignal()), timeout);

        public Task<bool> WaitForDoneAsync(WorkItemId id, TimeSpan timeout) =>
            WaitForSignalAsync(_done.GetOrAdd(id, static _ => NewSignal()), timeout);

        public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            _entered.GetOrAdd(item.Id, static _ => NewSignal()).TrySetResult();
            await _released.GetOrAdd(item.Id, static _ => NewSignal()).Task.WaitAsync(ct);
            await _store.UpdateAsync(item.With(WorkItemState.Done), ct);
            _done.GetOrAdd(item.Id, static _ => NewSignal()).TrySetResult();
        }

        private static async Task<bool> WaitForSignalAsync(TaskCompletionSource signal, TimeSpan timeout)
        {
            var completed = await Task.WhenAny(signal.Task, Task.Delay(timeout));
            return completed == signal.Task;
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
