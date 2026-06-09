using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies per-project budget cap enforcement at pickup time.
/// Store-level tests validate the query logic; the OrchestratorService
/// integration test validates that CheckBudgetAsync gates real pickups.
/// </summary>
public sealed class BudgetEnforcementTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-budget-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public BudgetEnforcementTests() => _store = new SqliteWorkItemStore(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkItem MakeQueued(string projectId = "proj-a") => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId(projectId),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Queued,
    };

    // ── CountStartedInWindowAsync ─────────────────────────────────────────────

    [Fact]
    public async Task CountStarted_NoItems_ReturnsZero()
    {
        var count = await _store.CountStartedInWindowAsync(
            new ProjectId("proj-a"), DateTimeOffset.UtcNow.AddHours(-1));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CountStarted_ItemsWithStartedAt_Counted()
    {
        var pid = new ProjectId("proj-x");
        var item = MakeQueued("proj-x") with { StartedAt = DateTimeOffset.UtcNow.AddMinutes(-30) };
        await _store.CreateAsync(item);

        var count = await _store.CountStartedInWindowAsync(pid, DateTimeOffset.UtcNow.AddHours(-1));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CountStarted_ItemsOutsideWindow_NotCounted()
    {
        var pid = new ProjectId("proj-x");
        // Started 2 hours ago — outside the 1-hour window.
        var item = MakeQueued("proj-x") with { StartedAt = DateTimeOffset.UtcNow.AddHours(-2) };
        await _store.CreateAsync(item);

        var count = await _store.CountStartedInWindowAsync(pid, DateTimeOffset.UtcNow.AddHours(-1));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CountStarted_NullStartedAt_NotCounted()
    {
        var pid = new ProjectId("proj-x");
        // Item with no StartedAt (not yet picked up).
        var item = MakeQueued("proj-x");
        await _store.CreateAsync(item);

        var count = await _store.CountStartedInWindowAsync(pid, DateTimeOffset.UtcNow.AddHours(-1));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CountStarted_OnlyCountsMatchingProject()
    {
        var now = DateTimeOffset.UtcNow;
        var itemA = MakeQueued("proj-a") with { StartedAt = now.AddMinutes(-10) };
        var itemB = MakeQueued("proj-b") with { StartedAt = now.AddMinutes(-10) };
        await _store.CreateAsync(itemA);
        await _store.CreateAsync(itemB);

        var countA = await _store.CountStartedInWindowAsync(new ProjectId("proj-a"), now.AddHours(-1));
        var countB = await _store.CountStartedInWindowAsync(new ProjectId("proj-b"), now.AddHours(-1));

        Assert.Equal(1, countA);
        Assert.Equal(1, countB);
    }

    // ── CountInFlightAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task CountInFlight_NoItems_ReturnsZero()
    {
        var count = await _store.CountInFlightAsync(new ProjectId("proj-a"));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CountInFlight_WorkingItems_Counted()
    {
        var item = MakeQueued() with { State = WorkItemState.Working, StartedAt = DateTimeOffset.UtcNow };
        await _store.CreateAsync(item);

        var count = await _store.CountInFlightAsync(new ProjectId("proj-a"));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CountInFlight_QueuedItems_NotCounted()
    {
        var item = MakeQueued();
        await _store.CreateAsync(item);

        var count = await _store.CountInFlightAsync(new ProjectId("proj-a"));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CountInFlight_TerminalItems_NotCounted()
    {
        // Items have StartedAt set so the state NOT IN exclusion is actually exercised
        // (previously StartedAt was null, so started_at IS NOT NULL filtered them first).
        foreach (var state in new[] { WorkItemState.Done, WorkItemState.Failed, WorkItemState.Cancelled, WorkItemState.AuditFailed })
        {
            var item = MakeQueued() with { State = state, StartedAt = DateTimeOffset.UtcNow };
            await _store.CreateAsync(item);
        }

        var count = await _store.CountInFlightAsync(new ProjectId("proj-a"));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CountInFlight_RetriedItem_NotCounted()
    {
        // A retried item goes through With(WorkItemState.Queued) which clears StartedAt.
        // Verify both that the With() call clears it and that the store returns 0.
        var working = MakeQueued() with { State = WorkItemState.Working, StartedAt = DateTimeOffset.UtcNow };
        var retried = working.With(WorkItemState.Queued, error: null);
        Assert.Null(retried.StartedAt);

        await _store.CreateAsync(retried);

        var count = await _store.CountInFlightAsync(new ProjectId("proj-a"));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CountInFlight_PreemptedItem_NotCounted()
    {
        var item = MakeQueued() with
        {
            State = WorkItemState.Working,
            StartedAt = DateTimeOffset.UtcNow,
            PreemptedAt = DateTimeOffset.UtcNow,
            PreemptCheckpoint = $"refs/heads/codeybox/preempt/{Guid.NewGuid()}",
        };
        await _store.CreateAsync(item);

        var count = await _store.CountInFlightAsync(new ProjectId("proj-a"));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CountInFlight_WaitingForAgentResumeWithStartedAt_NotCounted()
    {
        var item = MakeQueued() with
        {
            State = WorkItemState.WaitingForAgentResume,
            StartedAt = DateTimeOffset.UtcNow,
        };
        await _store.CreateAsync(item);

        var count = await _store.CountInFlightAsync(new ProjectId("proj-a"));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CountInFlight_AllActiveStates_Counted()
    {
        var pid = new ProjectId("proj-multi");
        foreach (var state in new[]
        {
            WorkItemState.Working, WorkItemState.WorkComplete,
            WorkItemState.Auditing, WorkItemState.Reworking, WorkItemState.AuditPassed,
            WorkItemState.Merging, WorkItemState.Merged, WorkItemState.UpstreamPushing,
        })
        {
            await _store.CreateAsync(MakeQueued("proj-multi") with { State = state, StartedAt = DateTimeOffset.UtcNow });
        }

        var count = await _store.CountInFlightAsync(pid);
        Assert.Equal(8, count);
    }

    // ── StartedAt set on first pickup ─────────────────────────────────────────

    [Fact]
    public async Task StartedAt_SetOnFirstPickup_PersistedToStore()
    {
        var item = MakeQueued();
        await _store.CreateAsync(item);
        Assert.Null(item.StartedAt);

        // Simulate setting StartedAt (as OrchestratorService does before calling the pipeline).
        var started = DateTimeOffset.UtcNow;
        var updated = item with { StartedAt = started };
        await _store.UpdateAsync(updated);

        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read!.StartedAt);
        Assert.True(read.StartedAt >= started.AddSeconds(-1));
    }

    [Fact]
    public async Task StartedAt_RoundTrips_Precisely()
    {
        var ts = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var item = MakeQueued() with { StartedAt = ts };
        await _store.CreateAsync(item);

        var read = await _store.GetAsync(item.Id);
        Assert.Equal(ts, read!.StartedAt);
    }

    // ── OrchestratorService integration ──────────────────────────────────────

    /// <summary>
    /// End-to-end: with MaxItemsPerHour=2 and 5 queued items in the same
    /// project, the orchestrator picks up exactly 2 and defers the rest.
    /// Validates the CheckBudgetAsync gate in the OrchestratorService pickup path.
    /// </summary>
    [Fact]
    public async Task OrchestratorPickup_HourlyBudget_EnforcedAtPickupTime()
    {
        var pid = new ProjectId("budget-hourly");
        var projectRepo = new InMemoryProjectRepository(new Project
        {
            Id = pid,
            DisplayName = "Budget Project",
            RepositoryUrl = "https://github.com/test/repo",
            Budget = new ProjectBudget { MaxItemsPerHour = 2 },
        });

        var pickupCount = 0;
        var pipeline = new CountingPipelineRunner(
            _store, onRun: () => Interlocked.Increment(ref pickupCount));
        var queue = new InMemoryTaskQueue();
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 5 };
        var reg = new CancellationRegistry(CancellationToken.None);
        var svc = new OrchestratorService(
            queue, _store, pipeline, reg, opts,
            NullLogger<OrchestratorService>.Instance,
            projects: projectRepo);

        for (var i = 0; i < 5; i++)
        {
            var item = MakeQueued("budget-hourly");
            await _store.CreateAsync(item);
            await queue.EnqueueAsync(item.Id);
        }

        await svc.StartAsync(CancellationToken.None);

        // Wait for exactly 2 pickups.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && Volatile.Read(ref pickupCount) < 2)
            await Task.Delay(50);

        // Short settling window to confirm no further pickups occur within the hour.
        await Task.Delay(300);

        await svc.StopAsync(CancellationToken.None);

        Assert.Equal(2, Volatile.Read(ref pickupCount));
    }

    // ── BudgetDeferralRecheckSnapshot consumer hot-reload ───────────────────

    /// <summary>
    /// Pipeline stub that blocks on a per-invocation gate so we can trigger
    /// deterministic second-cycle deferrals. Gates[0] blocks the first item
    /// picked up, Gates[1] the second, etc. Items beyond the last gate run
    /// immediately.
    /// </summary>
    private sealed class GatedPipelineRunner(TaskCompletionSource[] gates, IWorkItemStore store) : IPipelineRunner
    {
        private int _started;

        public int StartedCount => Volatile.Read(ref _started);

        public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            var n = Interlocked.Increment(ref _started) - 1;
            if (n < gates.Length)
                await gates[n].Task.WaitAsync(ct);
            await store.UpdateAsync(item.With(WorkItemState.Done), ct);
        }
    }

    [Fact]
    public async Task BudgetDeferralRecheckSnapshot_IsConsumedByOrchestratorService()
    {
        // Consumer-side test: prove that OrchestratorService reads
        // _budgetDeferralRecheck.Current on each budget-cap deferral, not a
        // value cached at construction time.
        //
        // Strategy: block the first item, enqueue several successors so they
        // all defer under the initial short interval, then hot-reload to a long
        // interval before the short deferrals expire. When the first item is
        // released, one successor grabs the freed slot and blocks on gate2;
        // the others defer again. The second-cycle deferral must read the
        // hot-reloaded snapshot value, not a value cached at construction time
        // or on the first deferral.
        var pid = new ProjectId("budget-recheck-conc");
        var projectRepo = new InMemoryProjectRepository(new Project
        {
            Id = pid,
            DisplayName = "Concurrent Budget Project",
            RepositoryUrl = "https://github.com/test/repo",
            Budget = new ProjectBudget { MaxConcurrentForProject = 1 },
        });

        var gate1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new GatedPipelineRunner([gate1, gate2], _store);
        var queue = new InMemoryTaskQueue();
        var spawnCount = 0;
        var opts = new OrchestratorOptions
        {
            MaxConcurrentWorkers = 3,
            OnWorkerSpawned = () => Interlocked.Increment(ref spawnCount),
        };
        var reg = new CancellationRegistry(CancellationToken.None);

        var initialRecheck = TimeSpan.FromSeconds(2);
        var hotReloadedRecheck = TimeSpan.FromSeconds(10);
        var oldIntervalGrace = initialRecheck + TimeSpan.FromMilliseconds(400);

        var snapshot = new BudgetDeferralRecheckSnapshot(new BudgetDeferralRecheckOptions
        {
            ConcurrentLimitRecheck = initialRecheck,
        });

        var svc = new OrchestratorService(
            queue, _store, pipeline, reg, opts,
            NullLogger<OrchestratorService>.Instance,
            projects: projectRepo,
            budgetDeferralRecheck: snapshot);

        var first = MakeQueued("budget-recheck-conc");
        await _store.CreateAsync(first);
        await queue.EnqueueAsync(first.Id);

        await svc.StartAsync(CancellationToken.None);

        var runningDeadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!IsRunning(await _store.GetAsync(first.Id)) && DateTimeOffset.UtcNow < runningDeadline)
        {
            await Task.Delay(50);
        }

        Assert.True(
            IsRunning(await _store.GetAsync(first.Id)),
            "the first item must be running before the budget deferral is exercised");

        var ids = new List<WorkItemId> { first.Id };
        for (var i = 0; i < 3; i++)
        {
            var item = MakeQueued("budget-recheck-conc");
            await _store.CreateAsync(item);
            ids.Add(item.Id);
            await queue.EnqueueAsync(item.Id);
        }

        var successors = ids.Skip(1).ToArray();

        // Poll until every successor has been deferred under the initial
        // snapshot value while the first item is still running.
        var deferDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!successors.All(svc.IsDeferredForTest)
               && DateTimeOffset.UtcNow < deferDeadline)
        {
            await Task.Delay(50);
        }

        Assert.True(
            successors.All(svc.IsDeferredForTest),
            "all successor items must be deferred by the concurrent cap before hot-reload");

        // Hot-reload to a long recheck interval. The next deferral cycle
        // (after items are re-enqueued and hit the cap again) must read this
        // new value from the snapshot. The initial interval is deliberately
        // long enough that this replacement wins the race against the first
        // deferred requeue on loaded test hosts.
        snapshot.Replace(new BudgetDeferralRecheckOptions
        {
            ConcurrentLimitRecheck = hotReloadedRecheck,
        });

        // Unblock the first item so the deferred items can be re-enqueued
        // when their initial deferral expires.
        gate1.TrySetResult();

        // Wait for the initial deferral to expire and the dispatch
        // loop to process the deferred items again. One will grab the freed slot
        // and block on gate2; at least one other hits the concurrent cap and is
        // deferred again — this time reading the hot-reloaded interval.
        await Task.Delay(oldIntervalGrace);

        int? secondDeferredIdx = null;
        var secondDeferDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (secondDeferredIdx is null && DateTimeOffset.UtcNow < secondDeferDeadline)
        {
            // Identify an item deferred in the second cycle. With several
            // successors queued, one can grab the freed slot and block on gate2
            // while another is deferred under the cap. On a loaded test
            // host, the worker that consumes the expired initial deferral can lag
            // behind the fixed delay above, so poll like the first-cycle
            // assertion instead of baking in a scheduler timing assumption.
            var successorStates = new Dictionary<WorkItemId, WorkItem?>();
            foreach (var id in successors)
                successorStates[id] = await _store.GetAsync(id);

            var runningSuccessors = successors.Count(id => IsRunning(successorStates[id]));
            var deferredSuccessors = successors.Count(svc.IsDeferredForTest);
            var activeOrDeferred = runningSuccessors + deferredSuccessors;

            secondDeferredIdx = Enumerable.Range(1, ids.Count - 1)
                .Cast<int?>()
                .FirstOrDefault(i => svc.IsDeferredForTest(ids[i!.Value]));

            if (secondDeferredIdx is not null
                && (activeOrDeferred != successors.Length
                    || runningSuccessors == 0
                    || deferredSuccessors == 0
                    || pipeline.StartedCount < 2))
                secondDeferredIdx = null;

            if (secondDeferredIdx is null)
                await Task.Delay(50);
        }

        Assert.NotNull(secondDeferredIdx);

        // Now the item is deferred with the hot-reloaded interval.
        // If the old value was cached, it would have re-enqueued the
        // item and spawned another worker shortly after the old interval. Wait
        // past the old window and verify no retry happened.
        var spawnCountAfterHotReloadDeferral = Volatile.Read(ref spawnCount);
        await Task.Delay(oldIntervalGrace);

        Assert.True(
            svc.IsDeferredForTest(ids[secondDeferredIdx.Value]),
            "the deferred item must still be deferred after hot-reload " +
            "(the long hot-reloaded ConcurrentLimitRecheck was consumed, " +
            "not the short initial value)");
        Assert.Equal(spawnCountAfterHotReloadDeferral, Volatile.Read(ref spawnCount));

        // Release the blocked item so StopAsync can drain cleanly.
        gate2.TrySetResult();

        await svc.StopAsync(CancellationToken.None);

        static bool IsRunning(WorkItem? item) =>
            item?.StartedAt is not null && !WorkItemDependencies.TerminalStates.Contains(item.State);
    }
}
