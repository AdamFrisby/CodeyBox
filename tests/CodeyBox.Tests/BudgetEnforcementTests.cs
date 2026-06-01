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
    /// Pipeline stub that blocks on a per-invocation gate. Gates[0] blocks the
    /// first item picked up, Gates[1] the second, etc. Items beyond the last
    /// gate run immediately.
    /// </summary>
    private sealed class GatedPipelineRunner(TaskCompletionSource[] gates, IWorkItemStore store) : IPipelineRunner
    {
        private int _started;

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
        // Strategy: construct the service with a short interval, wait until one
        // item is actually running, hot-reload to a long interval, then enqueue
        // one more item. That item's first budget deferral must use the current
        // snapshot value, not the value captured at service construction. If
        // the service kept the constructor-time interval, the deferred item
        // would be retried and spawn another worker inside the assertion window.
        var pid = new ProjectId("budget-recheck-conc");
        var projectRepo = new InMemoryProjectRepository(new Project
        {
            Id = pid,
            DisplayName = "Concurrent Budget Project",
            RepositoryUrl = "https://github.com/test/repo",
            Budget = new ProjectBudget { MaxConcurrentForProject = 1 },
        });

        var gate1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new GatedPipelineRunner([gate1], _store);
        var queue = new InMemoryTaskQueue();
        var spawnCount = 0;
        var opts = new OrchestratorOptions
        {
            MaxConcurrentWorkers = 2,
            OnWorkerSpawned = () => Interlocked.Increment(ref spawnCount),
        };
        var reg = new CancellationRegistry(CancellationToken.None);

        var snapshot = new BudgetDeferralRecheckSnapshot(new BudgetDeferralRecheckOptions
        {
            ConcurrentLimitRecheck = TimeSpan.FromMilliseconds(200),
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

        // Hot-reload to a long recheck interval. The following deferral must
        // read this new value from the snapshot.
        snapshot.Replace(new BudgetDeferralRecheckOptions
        {
            ConcurrentLimitRecheck = TimeSpan.FromSeconds(10),
        });

        var second = MakeQueued("budget-recheck-conc");
        await _store.CreateAsync(second);
        await queue.EnqueueAsync(second.Id);

        var deferDeadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!svc.IsDeferredForTest(second.Id) && DateTimeOffset.UtcNow < deferDeadline)
        {
            await Task.Delay(50);
        }

        Assert.True(
            svc.IsDeferredForTest(second.Id),
            "the second item must be deferred by the concurrent cap after hot-reload");
        Assert.Equal(2, Volatile.Read(ref spawnCount));

        // Now the item is deferred with the hot-reloaded 10 s interval.
        // If the old 200 ms value was cached, it would have re-enqueued the
        // item and spawned another worker within 500 ms. Wait past the old
        // window and verify no retry happened.
        await Task.Delay(500);

        Assert.True(
            svc.IsDeferredForTest(second.Id),
            "the deferred item must still be deferred after hot-reload " +
            "(the long hot-reloaded ConcurrentLimitRecheck was consumed, " +
            "not the short initial value)");
        Assert.Equal(2, Volatile.Read(ref spawnCount));

        // Release the blocked item so StopAsync can drain cleanly.
        gate1.TrySetResult();

        await svc.StopAsync(CancellationToken.None);

        static bool IsRunning(WorkItem? item) =>
            item?.StartedAt is not null && !WorkItemDependencies.TerminalStates.Contains(item.State);
    }
}
