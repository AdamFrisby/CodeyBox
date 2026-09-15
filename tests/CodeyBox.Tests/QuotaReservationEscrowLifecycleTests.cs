using System.Collections.Concurrent;
using System.Diagnostics;
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
    // Safety nets only: the ledger waits below are event-driven (the worker's
    // own reserve/settle/release notifications wake them) and the pipeline
    // waits ride completion sources the worker sets, so a slow or
    // oversubscribed agent only delays success — it must not turn into a red
    // product assertion. The timeouts stay generous on purpose and every wait
    // reports itself as a timeout naming the condition it waited on.
    private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(60);

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

    private static WorkItem EscrowItem(TimeProvider time) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Queued,
        AgentClassId = "frontier",
        MinModelScore = 0,
        CreatedAt = time.GetUtcNow(),
        UpdatedAt = time.GetUtcNow(),
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

    /// <summary>
    /// Test clock anchoring "now" at construction and then flowing with real
    /// elapsed time, with an additional manual <see cref="Advance"/> for
    /// jumping past TTLs and completion stamps deterministically. The ledger
    /// and router read this clock, so accounting stamps never depend on when
    /// the OS schedules the worker; the flowing anchor keeps cost-row
    /// timestamps comparable with the worker's own run-started stamps (which
    /// use the service clock) without freezing them in the past.
    /// </summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private readonly object _sync = new();
        private DateTimeOffset _start;
        private TimeSpan _advanced;

        public FakeTimeProvider(DateTimeOffset start) => _start = start;

        public void Advance(TimeSpan delta)
        {
            lock (_sync) _advanced += delta;
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync) return _start + _elapsed.Elapsed + _advanced;
        }
    }

    private static bool Near(double actual, double expected) =>
        Math.Abs(actual - expected) < 1e-5;

    /// <summary>
    /// Immutable snapshot of the ledger state these tests step through.
    /// </summary>
    private sealed record LedgerState(double OutstandingPct, long RetainedAtEstimate, long SettledFromActuals)
    {
        public override string ToString() =>
            $"outstanding={OutstandingPct:F4} retainedAtEstimate={RetainedAtEstimate} settledFromActuals={SettledFromActuals}";
    }

    /// <summary>
    /// Records every ledger state the worker passes through, from subscription
    /// on. The worker's reserve/settle/release path raises
    /// <see cref="QuotaReservationLedger.ReservationsChanged"/> synchronously,
    /// so subscribing before the dispatch is authorised captures the whole
    /// escrow sequence — the transient 4-point estimate as well as the later
    /// reconcile/retain/release — even if the test thread is not scheduled
    /// again until the run has already settled. A point-in-time poll cannot do
    /// this: once the estimate has been reconciled, no later read can prove it
    /// was ever escrowed.
    /// </summary>
    private sealed class LedgerSequence : IDisposable
    {
        private readonly QuotaReservationLedger _ledger;
        private readonly string _key;
        private readonly object _sync = new();
        private readonly List<LedgerState> _history = [];
        private TaskCompletionSource _advanced = NewSignal();
        private bool _disposed;

        public LedgerSequence(QuotaReservationLedger ledger, string key)
        {
            _ledger = ledger;
            _key = key;
            _ledger.ReservationsChanged += OnReservationsChanged;
            Capture();
        }

        public bool Seen(Func<LedgerState, bool> predicate)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            lock (_sync) return _history.Any(predicate);
        }

        /// <summary>
        /// Whether the recorded sequence passes through every
        /// <paramref name="stages"/> in order (other states may interleave).
        /// Needed for terminal states that also match the pre-dispatch ledger:
        /// a release to zero must be observed <em>after</em> the escrow, not in
        /// the initial empty snapshot.
        /// </summary>
        public bool SeenInOrder(params Func<LedgerState, bool>[] stages)
        {
            ArgumentNullException.ThrowIfNull(stages);
            lock (_sync)
            {
                var stage = 0;
                foreach (var state in _history)
                {
                    if (stage < stages.Length && stages[stage](state)) stage++;
                }
                return stage == stages.Length;
            }
        }

        public LedgerState Current()
        {
            lock (_sync) return _history[^1];
        }

        /// <summary>
        /// Waits until the recorded sequence contains a state satisfying
        /// <paramref name="predicate"/>. Woken by the worker's own ledger
        /// notifications; <paramref name="timeout"/> is only a hang guard.
        /// Throws <see cref="TimeoutException"/> naming the condition (with
        /// the last observed state for diagnosis) so scheduler starvation
        /// reports as a timeout, never as a product assertion.
        /// </summary>
        public Task WaitForSeenAsync(Func<LedgerState, bool> predicate, string condition, TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            ArgumentNullException.ThrowIfNull(condition);
            return WaitUntilAsync(() => Seen(predicate), condition, timeout);
        }

        /// <summary>
        /// Waits until <see cref="SeenInOrder"/> holds for
        /// <paramref name="stages"/>; same event-driven hang guard as
        /// <see cref="WaitForSeenAsync"/>.
        /// </summary>
        public Task WaitForSeenInOrderAsync(string condition, TimeSpan timeout, params Func<LedgerState, bool>[] stages)
        {
            ArgumentNullException.ThrowIfNull(condition);
            ArgumentNullException.ThrowIfNull(stages);
            return WaitUntilAsync(() => SeenInOrder(stages), condition, timeout);
        }

        private async Task WaitUntilAsync(Func<bool> isSatisfied, string condition, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (true)
            {
                if (isSatisfied()) return;
                TaskCompletionSource signal;
                lock (_sync) signal = _advanced;
                if (isSatisfied()) return;
                try
                {
                    await signal.Task.WaitAsync(cts.Token);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Timed out after {timeout} waiting for {condition}. Last observed state: {Current()}.");
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _ledger.ReservationsChanged -= OnReservationsChanged;
        }

        private void OnReservationsChanged()
        {
            var state = CaptureState();
            TaskCompletionSource pulse;
            lock (_sync)
            {
                if (_disposed) return;
                _history.Add(state);
                pulse = _advanced;
                _advanced = NewSignal();
            }
            pulse.TrySetResult();
        }

        private void Capture()
        {
            var state = CaptureState();
            lock (_sync) _history.Add(state);
        }

        private LedgerState CaptureState()
        {
            var stats = _ledger.GetSettlementStats(_key);
            return new LedgerState(_ledger.GetOutstandingPct(_key), stats.RetainedAtEstimate, stats.SettledFromActuals);
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Asserts a worker-set completion signal, translating a miss into a
    /// timeout naming the condition rather than a bare boolean assert.
    /// </summary>
    private static async Task AwaitSignalAsync(Task<bool> signal, string condition, TimeSpan timeout)
    {
        if (await signal) return;
        throw new TimeoutException(
            $"Timed out after {timeout} waiting for {condition}.");
    }

    private static async Task<WorkerRegistration?> WaitForWorkerRegistrationAsync(
        IWorkerRegistry registry, WorkItemId workItemId, TimeSpan timeout, string condition)
    {
        // The registry exposes no change notification, so this still polls —
        // but against a monotonic clock (immune to wall-clock steps) rather
        // than a calendar-clock deadline, and a miss throws naming the
        // condition instead of asserting a product claim.
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            var worker = (await registry.ListAsync())
                .FirstOrDefault(w => w.CurrentWorkItemId == workItemId.ToString());
            if (worker is not null) return worker;
            if (elapsed.Elapsed >= timeout)
                throw new TimeoutException(
                    $"Timed out after {timeout} waiting for {condition}.");
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task PhaseSuccess_ReconcilesEstimateToObservedViaWorkerLifecycle()
    {
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var item = EscrowItem(fakeTime);
        await _store.CreateAsync(item);

        var opts = EscrowOptions();
        var ledger = new QuotaReservationLedger(opts, time: fakeTime);
        using var sequence = new LedgerSequence(ledger, "claude");
        var costs = new InMemoryCostStore();
        var pipeline = new EscrowPipeline(_store, async (run, ct) =>
        {
            await costs.RecordAsync(CostRow(item.Id, input: 1000, output: 500, fakeTime.GetUtcNow()), ct);
            await _store.UpdateAsync(run.With(WorkItemState.Done), ct);
        });
        var router = new AgentClassRouter(
            [FrontierClass()],
            [new EscrowProbe(AgentKind.Claude, 20.0)],
            opts,
            NullLogger<AgentClassRouter>.Instance,
            timeProvider: fakeTime,
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

        await AwaitSignalAsync(
            pipeline.WaitForEnteredAsync(item.Id, DispatchTimeout),
            $"pipeline entry for work item '{item.Id}'",
            DispatchTimeout);
        await sequence.WaitForSeenAsync(
            s => Near(s.OutstandingPct, 4.0),
            "escrow of the 4-point estimate for 'claude' after dispatch authorisation",
            SettleTimeout);
        Assert.True(
            sequence.Seen(s => Near(s.OutstandingPct, 4.0)),
            "The recorded ledger sequence must show the 4-point estimate escrowed.");
        await AwaitSignalAsync(
            pipeline.WaitForExitedAsync(item.Id, DispatchTimeout),
            $"pipeline exit for work item '{item.Id}'",
            DispatchTimeout);

        // 1500 tokens against a 100k budget reconcile to 1.5%: the estimate is
        // replaced, not merely released. Without the worker-exit finally this
        // stays at the 4-point estimate.
        await sequence.WaitForSeenAsync(
            s => Near(s.OutstandingPct, 1.5),
            "reconcile of the escrow to 1.5% observed usage for 'claude' after phase success",
            SettleTimeout);
        Assert.Equal(1.5, ledger.GetOutstandingPct("claude"), precision: 5);
        Assert.Equal(1, ledger.GetReservationCount(new AgentMembership
        {
            Agent = AgentKind.Claude,
            Billing = AgentBilling.Subscription,
            QualityScore = 100,
        }));

        // A newer probe reading supersedes the reconciled tail instead of
        // double-counting it. Advancing the test clock (rather than stamping
        // wall-clock time) keeps the reading deterministically newer than the
        // settle stamp.
        fakeTime.Advance(TimeSpan.FromMinutes(1));
        ledger.NoteProbeReading("claude", 18.5, fakeTime.GetUtcNow());
        Assert.Equal(0.0, ledger.GetOutstandingPct("claude"), precision: 5);

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PhaseWithoutExtractedUsage_RetainsEstimateViaWorkerLifecycle()
    {
        // The copilot-shaped defect: the run produced a cost row with zero
        // tokens and has_extracted_token_usage = 0. Settling must retain the
        // reserved estimate instead of releasing the run as free.
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var item = EscrowItem(fakeTime);
        await _store.CreateAsync(item);

        var opts = EscrowOptions();
        var ledger = new QuotaReservationLedger(opts, time: fakeTime);
        using var sequence = new LedgerSequence(ledger, "claude");
        var costs = new InMemoryCostStore();
        var pipeline = new EscrowPipeline(_store, async (run, ct) =>
        {
            var row = CostRow(item.Id, input: 0, output: 0, fakeTime.GetUtcNow()) with
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
            timeProvider: fakeTime,
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

        await AwaitSignalAsync(
            pipeline.WaitForEnteredAsync(item.Id, DispatchTimeout),
            $"pipeline entry for work item '{item.Id}'",
            DispatchTimeout);
        await sequence.WaitForSeenAsync(
            s => Near(s.OutstandingPct, 4.0),
            "escrow of the 4-point estimate for 'claude' after dispatch authorisation",
            SettleTimeout);
        Assert.True(
            sequence.Seen(s => Near(s.OutstandingPct, 4.0)),
            "The recorded ledger sequence must show the 4-point estimate escrowed.");
        await AwaitSignalAsync(
            pipeline.WaitForExitedAsync(item.Id, DispatchTimeout),
            $"pipeline exit for work item '{item.Id}'",
            DispatchTimeout);

        // The unmeasured run consumed quota: the worker-exit settle retains
        // the estimate (still escrowed, counted as retained) instead of
        // releasing it. Without the extraction gate this drops to zero.
        await sequence.WaitForSeenAsync(
            s => s.RetainedAtEstimate == 1,
            "retention of the estimate for 'claude' after a phase without extracted usage",
            SettleTimeout);
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
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var item = EscrowItem(fakeTime);
        await _store.CreateAsync(item);

        var opts = EscrowOptions();
        var ledger = new QuotaReservationLedger(opts, time: fakeTime);
        using var sequence = new LedgerSequence(ledger, "claude");
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
            timeProvider: fakeTime,
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

        await AwaitSignalAsync(
            pipeline.WaitForEnteredAsync(item.Id, DispatchTimeout),
            $"pipeline entry for work item '{item.Id}'",
            DispatchTimeout);
        await AwaitSignalAsync(
            pipeline.WaitForExitedAsync(item.Id, DispatchTimeout),
            $"pipeline exit for work item '{item.Id}'",
            DispatchTimeout);

        // No cost rows: nothing measurable ran, so the estimate is released
        // outright. Without the worker-exit finally this stays escrowed.
        await sequence.WaitForSeenInOrderAsync(
            "release of the escrow for 'claude' after phase failure",
            SettleTimeout,
            s => Near(s.OutstandingPct, 4.0),
            s => Near(s.OutstandingPct, 0.0));
        Assert.Equal(0.0, ledger.GetOutstandingPct("claude"), precision: 5);
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
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var item = EscrowItem(fakeTime);
        await _store.CreateAsync(item);

        var opts = EscrowOptions();
        var ledger = new QuotaReservationLedger(opts, time: fakeTime);
        using var sequence = new LedgerSequence(ledger, "claude");
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
            timeProvider: fakeTime,
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

        await AwaitSignalAsync(
            pipeline.WaitForEnteredAsync(item.Id, DispatchTimeout),
            $"pipeline entry for work item '{item.Id}'",
            DispatchTimeout);
        await AwaitSignalAsync(
            pipeline.WaitForExitedAsync(item.Id, DispatchTimeout),
            $"pipeline exit for work item '{item.Id}'",
            DispatchTimeout);

        await sequence.WaitForSeenInOrderAsync(
            "release of the escrow for 'claude' after cancellation",
            SettleTimeout,
            s => Near(s.OutstandingPct, 4.0),
            s => Near(s.OutstandingPct, 0.0));
        Assert.Equal(0.0, ledger.GetOutstandingPct("claude"), precision: 5);
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
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var item = EscrowItem(fakeTime);

        var opts = EscrowOptions();
        var ledger = new QuotaReservationLedger(opts, time: fakeTime);
        using var sequence = new LedgerSequence(ledger, "claude");
        var costs = new InMemoryCostStore();
        var pipeline = new BlockingPipeline(_store);
        var router = new AgentClassRouter(
            [FrontierClass()],
            [new EscrowProbe(AgentKind.Claude, 20.0)],
            opts,
            NullLogger<AgentClassRouter>.Instance,
            timeProvider: fakeTime,
            reservationLedger: ledger);
        var queue = new InMemoryTaskQueue();
        using var workerRegistry = new SqliteWorkerRegistry(_dbPath, NullLogger<SqliteWorkerRegistry>.Instance);
        using var registry = new CancellationRegistry(CancellationToken.None);
        var recoveryBarrier = new StartupRecoveryBarrier();
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            router: router,
            workerRegistry: workerRegistry,
            deadWorkerOpts: new DeadWorkerOptions(),
            reservationLedger: ledger,
            costStore: costs,
            burnEstimatorOptions: BurnOptions(),
            startupRecoveryCompletion: recoveryBarrier);

        await svc.StartAsync(CancellationToken.None);

        // Create the item only after startup replay has finished: items
        // present at startup are re-enqueued by ReplayPendingAsync, so
        // creating before StartAsync leaves two dispatch signals (replay +
        // explicit enqueue). The parked second turn would then pick up the
        // still-running item once the recovery release below frees the slot
        // (Working is dispatch-eligible and the release clears the active
        // claim), spawning a duplicate worker that re-escrows quota and
        // flakes the final assertions.
        var replayDone = recoveryBarrier.InitialRecoveryCompleted;
        var replayWinner = await Task.WhenAny(replayDone, Task.Delay(DispatchTimeout));
        if (replayWinner != replayDone)
            throw new TimeoutException(
                $"Timed out after {DispatchTimeout} waiting for startup recovery replay to complete before creating the work item.");
        await _store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);

        await AwaitSignalAsync(
            pipeline.WaitForEnteredAsync(item.Id, DispatchTimeout),
            $"pipeline entry for work item '{item.Id}'",
            DispatchTimeout);
        var worker = await WaitForWorkerRegistrationAsync(
            workerRegistry, item.Id, DispatchTimeout,
            $"worker registration for work item '{item.Id}'");
        Assert.NotNull(worker);
        await sequence.WaitForSeenAsync(
            s => Near(s.OutstandingPct, 4.0),
            $"escrow of the 4-point estimate for 'claude' while work item '{item.Id}' is blocked mid-phase",
            SettleTimeout);
        Assert.Equal(4.0, ledger.GetOutstandingPct("claude"), precision: 5);

        // Mark worker-owned so the recovery release does not fan out a wake
        // that could re-dispatch the still-running item.
        await _store.UpdateAsync(item with
        {
            State = WorkItemState.Working,
            StartedAt = fakeTime.GetUtcNow(),
            UpdatedAt = fakeTime.GetUtcNow(),
        });

        // The worker died without running its exit finally: the recovery path
        // releases the slot and the paired quota escrow. Without the
        // orphan-release this stays escrowed until the TTL sweep.
        Assert.True(await svc.TryReleaseRecoveredWorkerSlotAsync(
            worker!.WorkerId, item.Id, "test dead-worker recovery"));
        Assert.Equal(0.0, ledger.GetOutstandingPct("claude"), precision: 5);

        pipeline.Release(item.Id);
        await AwaitSignalAsync(
            pipeline.WaitForDoneAsync(item.Id, DispatchTimeout),
            $"pipeline completion for work item '{item.Id}'",
            DispatchTimeout);
        Assert.Equal(0.0, ledger.GetOutstandingPct("claude"), precision: 5);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LedgerWait_TimeoutNamesConditionInsteadOfProductClaim()
    {
        // The wait helper's failure mode is part of the contract: under
        // scheduler starvation it must report a timeout naming the condition,
        // never a product assertion about escrow state that was never reached.
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var ledger = new QuotaReservationLedger(EscrowOptions(), time: fakeTime);
        const string condition = "never-satisfied test condition";

        using var sequence = new LedgerSequence(ledger, "claude");
        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            sequence.WaitForSeenAsync(
                s => s.OutstandingPct > 1000,
                condition,
                TimeSpan.FromMilliseconds(100)));

        Assert.Contains("Timed out", ex.Message);
        Assert.Contains(condition, ex.Message);
        Assert.Contains("Last observed state", ex.Message);
    }

    [Fact]
    public void CustomKeyProvider_IsolatesOutstandingByCustomKey()
    {
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var opts = EscrowOptions();
        var ledger = new QuotaReservationLedger(
            opts,
            time: fakeTime,
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
