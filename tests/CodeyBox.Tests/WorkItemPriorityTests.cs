using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies priority-aware dispatch ordering, PATCH /workitems/{id}/priority
/// validation and re-ordering, persistence across restart, and the
/// equal-priority FIFO tie-break.
///
/// <para>Pinned to the "Background service timing" collection because the
/// dispatch ordering tests poll a live <see cref="OrchestratorService"/> with
/// a 30s wall-clock budget — suite-level threadpool contention from parallel
/// fixtures was tripping the timeout.</para>
/// </summary>
[Collection("Background service timing")]
public sealed class WorkItemPriorityTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-prio-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public WorkItemPriorityTests() => _store = new SqliteWorkItemStore(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkItem MakeItem(int priority = 0, DateTimeOffset? createdAt = null) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test"),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Queued,
        Priority = priority,
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
    };

    // ── Store layer ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Store_PreservesPriorityRoundTrip()
    {
        var item = MakeItem(priority: 250);
        await _store.CreateAsync(item);
        var read = await _store.GetAsync(item.Id);
        Assert.Equal(250, read!.Priority);

        var updated = await _store.UpdatePriorityAsync(item.Id, -75, DateTimeOffset.UtcNow);
        Assert.Equal(PriorityUpdateOutcome.Updated, updated.Outcome);
        read = await _store.GetAsync(item.Id);
        Assert.Equal(-75, read!.Priority);
    }

    [Fact]
    public async Task Store_PreservesAuditBudgetRoundTrip()
    {
        var item = MakeItem() with { AuditMaxIterations = 42, AuditComplexity = "hard" };
        await _store.CreateAsync(item);

        var read = await _store.GetAsync(item.Id);

        Assert.Equal(42, read!.AuditMaxIterations);
        Assert.Equal("hard", read.AuditComplexity);
    }

    [Fact]
    public async Task UpdateAsync_DoesNotClobberAuditBudget_OnStaleSnapshot()
    {
        var item = MakeItem();
        await _store.CreateAsync(item);
        await _store.UpdateAuditBudgetAsync(item.Id, 42, "hard", DateTimeOffset.UtcNow);

        await _store.UpdateAsync(item.With(WorkItemState.Working));

        var read = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Working, read!.State);
        Assert.Equal(42, read.AuditMaxIterations);
        Assert.Equal("hard", read.AuditComplexity);
    }

    [Fact]
    public async Task TryUpdateIfStateAsync_DoesNotClobberAuditBudget_OnStaleSnapshot()
    {
        var item = MakeItem() with { State = WorkItemState.Working };
        await _store.CreateAsync(item);
        await _store.UpdateAuditBudgetAsync(item.Id, 42, "hard", DateTimeOffset.UtcNow);

        var written = await _store.TryUpdateIfStateAsync(
            item.With(WorkItemState.Auditing),
            WorkItemState.Working);

        Assert.True(written);
        var read = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Auditing, read!.State);
        Assert.Equal(42, read.AuditMaxIterations);
        Assert.Equal("hard", read.AuditComplexity);
    }

    [Fact]
    public async Task ListDispatchEligible_OrdersHighestFirstThenFifo()
    {
        var t0 = DateTimeOffset.UtcNow;
        var lowOlder = MakeItem(priority: 0, createdAt: t0);
        var lowNewer = MakeItem(priority: 0, createdAt: t0.AddSeconds(2));
        var high = MakeItem(priority: 100, createdAt: t0.AddSeconds(1));
        var negative = MakeItem(priority: -50, createdAt: t0.AddSeconds(3));

        await _store.CreateAsync(lowNewer);
        await _store.CreateAsync(negative);
        await _store.CreateAsync(high);
        await _store.CreateAsync(lowOlder);

        var ordered = new List<WorkItemId>();
        await foreach (var w in _store.ListDispatchEligibleByPriorityAsync(new HashSet<WorkItemId>()))
            ordered.Add(w.Id);

        Assert.Equal(new[] { high.Id, lowOlder.Id, lowNewer.Id, negative.Id }, ordered);
    }

    [Fact]
    public async Task ListDispatchEligible_SkipsRequestedIds()
    {
        var a = MakeItem(priority: 100);
        var b = MakeItem(priority: 50);
        await _store.CreateAsync(a);
        await _store.CreateAsync(b);

        var ordered = new List<WorkItemId>();
        await foreach (var w in _store.ListDispatchEligibleByPriorityAsync(new HashSet<WorkItemId> { a.Id }))
            ordered.Add(w.Id);

        Assert.Equal(new[] { b.Id }, ordered);
    }

    [Fact]
    public async Task ListDispatchEligible_OmitsTerminalAndParkedStates()
    {
        // Terminal and parked states must never enter the dispatch enumerator;
        // mid-pipeline states like Working are kept (the dispatcher relies on this
        // for resuming preempted work after a host restart).
        var queued = MakeItem(priority: 100);
        var done = MakeItem(priority: 999) with { State = WorkItemState.Done };
        var failed = MakeItem(priority: 999) with { State = WorkItemState.Failed };
        var parked = MakeItem(priority: 999) with { State = WorkItemState.NeedsOperatorInput };
        var transientParked = MakeItem(priority: 999) with
        {
            State = WorkItemState.WaitingForTransientRetry,
            FailureKind = "transient",
        };
        var working = MakeItem(priority: 200) with { State = WorkItemState.Working };

        await _store.CreateAsync(queued);
        await _store.CreateAsync(done);
        await _store.CreateAsync(failed);
        await _store.CreateAsync(parked);
        await _store.CreateAsync(transientParked);
        await _store.CreateAsync(working);

        var ordered = new List<WorkItemId>();
        await foreach (var w in _store.ListDispatchEligibleByPriorityAsync(new HashSet<WorkItemId>()))
            ordered.Add(w.Id);

        Assert.Equal(new[] { working.Id, queued.Id }, ordered);
    }

    // ── Orchestrator dispatch ordering ───────────────────────────────────────

    [Fact]
    public async Task Dispatch_PicksHigherPriorityFirst()
    {
        var t0 = DateTimeOffset.UtcNow;
        var low = MakeItem(priority: -50, createdAt: t0);
        var mid = MakeItem(priority: 0, createdAt: t0.AddMilliseconds(1));
        var high = MakeItem(priority: 100, createdAt: t0.AddMilliseconds(2));

        await _store.CreateAsync(low);
        await _store.CreateAsync(mid);
        await _store.CreateAsync(high);

        var (svc, queue, pipeline, registry) = BuildOrchestrator(concurrency: 1);
        using var _ = registry;

        // Enqueue in arrival order; FIFO would pick low/mid/high but priority should reorder.
        await queue.EnqueueAsync(low.Id);
        await queue.EnqueueAsync(mid.Id);
        await queue.EnqueueAsync(high.Id);

        await svc.StartAsync(CancellationToken.None);
        var done = await WaitForAllDoneAsync(new[] { low.Id, mid.Id, high.Id }, TimeSpan.FromSeconds(30));
        await svc.StopAsync(CancellationToken.None);

        Assert.True(done, "All items should reach Done");
        Assert.Equal(new[] { high.Id, mid.Id, low.Id }, pipeline.Order);
    }

    [Fact]
    public async Task Dispatch_EqualPriority_PicksOldestCreatedFirst()
    {
        var t0 = DateTimeOffset.UtcNow.AddSeconds(-10);
        var older = MakeItem(priority: 10, createdAt: t0);
        var newer = MakeItem(priority: 10, createdAt: t0.AddSeconds(5));

        await _store.CreateAsync(newer);
        await _store.CreateAsync(older);

        var (svc, queue, pipeline, registry) = BuildOrchestrator(concurrency: 1);
        using var _ = registry;

        await queue.EnqueueAsync(newer.Id);
        await queue.EnqueueAsync(older.Id);

        await svc.StartAsync(CancellationToken.None);
        var done = await WaitForAllDoneAsync(new[] { older.Id, newer.Id }, TimeSpan.FromSeconds(30));
        await svc.StopAsync(CancellationToken.None);

        Assert.True(done);
        Assert.Equal(new[] { older.Id, newer.Id }, pipeline.Order);
    }

    [Fact]
    public async Task Dispatch_BumpedPriority_JumpsAheadOnNextPickup()
    {
        // Construct a scenario where FIFO order would be A, B, C; we bump C's
        // priority while A is in-flight so that the next pickup must be C — not
        // B — to prove priority overrides creation order on a mid-queue bump.
        var t0 = DateTimeOffset.UtcNow;
        var a = MakeItem(priority: 0, createdAt: t0);
        var b = MakeItem(priority: 0, createdAt: t0.AddSeconds(1));
        var c = MakeItem(priority: 0, createdAt: t0.AddSeconds(2));

        await _store.CreateAsync(a);
        await _store.CreateAsync(b);
        await _store.CreateAsync(c);

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<WorkItemId>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new GatedPipelineRunner(_store, release.Task, entered);

        var queue = new InMemoryTaskQueue();
        var registry = new CancellationRegistry(CancellationToken.None);
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 1 };
        var svc = new OrchestratorService(queue, _store, pipeline, registry, opts,
            NullLogger<OrchestratorService>.Instance);

        await queue.EnqueueAsync(a.Id);
        await queue.EnqueueAsync(b.Id);
        await queue.EnqueueAsync(c.Id);

        await svc.StartAsync(CancellationToken.None);

        // Wait for the first worker to enter the pipeline.
        var firstIn = await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(a.Id, firstIn);

        // Bump C's priority while A is held in the pipeline. The dispatcher's
        // next pickup must see the new value and pick C over B (FIFO older).
        var current = await _store.GetAsync(c.Id);
        Assert.NotNull(current);
        var bump = await _store.UpdatePriorityAsync(current!.Id, 500, DateTimeOffset.UtcNow);
        Assert.Equal(PriorityUpdateOutcome.Updated, bump.Outcome);
        await queue.EnqueueAsync(c.Id); // kick

        // Let A finish. The next pickup should be C, not B.
        pipeline.ResetEntered();
        release.SetResult();

        var secondIn = await pipeline.NextEnteredAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(c.Id, secondIn);

        // Let remaining items finish.
        pipeline.ReleaseAll();

        var allDone = await WaitForAllDoneAsync(new[] { a.Id, b.Id, c.Id },
            TimeSpan.FromSeconds(30));
        await svc.StopAsync(CancellationToken.None);

        Assert.True(allDone);
    }

    [Fact]
    public async Task UpdatePriorityAsync_DoesNotStompConcurrentStateTransition()
    {
        // Regression: a previous implementation of PATCH /priority did a full-row
        // UpdateAsync from a stale in-memory snapshot. If the worker transitioned
        // the item Queued→Working between the API's read and write, the partial
        // PATCH would stomp state back to Queued (and reset started_at, etc).
        // UpdatePriorityAsync must touch only priority + updated_at.
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var item = MakeItem(priority: 0);
        await _store.CreateAsync(item);

        // Simulate the worker transitioning the item out of Queued AFTER the
        // hypothetical PATCH read but BEFORE the partial UPDATE. Use the
        // store's normal UpdateAsync because that's what the orchestrator
        // would call.
        var workingNow = item with
        {
            State = WorkItemState.Working,
            StartedAt = startedAt,
            UpdatedAt = startedAt,
            RecoveryAttempts = 3,
        };
        await _store.UpdateAsync(workingNow);

        // Now PATCH priority using only the WorkItemId — the partial UPDATE must
        // not regress state, started_at, or recovery_attempts.
        var patchedAt = DateTimeOffset.UtcNow;
        var result = await _store.UpdatePriorityAsync(item.Id, 250, patchedAt);
        Assert.Equal(PriorityUpdateOutcome.Updated, result.Outcome);
        Assert.Equal(0, result.OldPriority);
        Assert.Equal(250, result.Item!.Priority);

        var read = await _store.GetAsync(item.Id);
        Assert.Equal(250, read!.Priority);
        Assert.Equal(WorkItemState.Working, read.State);
        Assert.Equal(startedAt, read.StartedAt);
        Assert.Equal(3, read.RecoveryAttempts);
    }

    [Fact]
    public async Task UpdateAsync_DoesNotStompConcurrentPriorityPatch()
    {
        // Worker writes often carry a stale in-memory WorkItem snapshot. Priority
        // is intentionally updated through UpdatePriorityAsync only, so those
        // worker writes must preserve a priority PATCH made while the item runs.
        var item = MakeItem(priority: 0);
        await _store.CreateAsync(item);

        var patchedAt = DateTimeOffset.UtcNow;
        var result = await _store.UpdatePriorityAsync(item.Id, 600, patchedAt);
        Assert.Equal(PriorityUpdateOutcome.Updated, result.Outcome);

        var staleWorkerSnapshot = item with
        {
            State = WorkItemState.Working,
            StartedAt = DateTimeOffset.UtcNow,
            MergeSha = "abc123",
        };
        await _store.UpdateAsync(staleWorkerSnapshot);

        var read = await _store.GetAsync(item.Id);
        Assert.Equal(600, read!.Priority);
        Assert.Equal(WorkItemState.Working, read.State);
        Assert.Equal("abc123", read.MergeSha);
    }

    [Theory]
    [InlineData(WorkItemState.Done)]
    [InlineData(WorkItemState.Failed)]
    [InlineData(WorkItemState.Cancelled)]
    [InlineData(WorkItemState.AuditFailed)]
    [InlineData(WorkItemState.MergeConflictResolutionFailed)]
    [InlineData(WorkItemState.AbandonedAfterRecoveryAttempts)]
    public async Task UpdatePriorityAsync_RejectsTerminalStates(WorkItemState terminalState)
    {
        var item = MakeItem(priority: 100) with { State = terminalState };
        await _store.CreateAsync(item);

        var result = await _store.UpdatePriorityAsync(item.Id, 500, DateTimeOffset.UtcNow);
        Assert.Equal(PriorityUpdateOutcome.TerminalState, result.Outcome);
        Assert.NotNull(result.Item);
        Assert.Equal(terminalState, result.Item!.State);

        // Priority must not have been mutated on a terminal row.
        var read = await _store.GetAsync(item.Id);
        Assert.Equal(100, read!.Priority);
    }

    [Fact]
    public async Task UpdatePriorityAsync_NotFound_ForMissingId()
    {
        var result = await _store.UpdatePriorityAsync(WorkItemId.New(), 100, DateTimeOffset.UtcNow);
        Assert.Equal(PriorityUpdateOutcome.NotFound, result.Outcome);
        Assert.Null(result.Item);
    }

    [Fact]
    public async Task PriorityPersistsAcrossOrchestratorRestart()
    {
        var item = MakeItem(priority: 750);
        await _store.CreateAsync(item);

        // Simulate a restart by closing the connection and re-opening.
        _store.Dispose();
        using var reopened = new SqliteWorkItemStore(_dbPath);
        var read = await reopened.GetAsync(item.Id);
        Assert.Equal(750, read!.Priority);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private (OrchestratorService svc, InMemoryTaskQueue queue, OrderedPipelineRunner pipeline,
             CancellationRegistry registry) BuildOrchestrator(int concurrency)
    {
        var queue = new InMemoryTaskQueue();
        var registry = new CancellationRegistry(CancellationToken.None);
        var pipeline = new OrderedPipelineRunner(_store);
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = concurrency };
        var svc = new OrchestratorService(queue, _store, pipeline, registry, opts,
            NullLogger<OrchestratorService>.Instance);
        return (svc, queue, pipeline, registry);
    }

    private async Task<bool> WaitForAllDoneAsync(IEnumerable<WorkItemId> ids, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var allDone = true;
            foreach (var id in ids)
            {
                var item = await _store.GetAsync(id);
                if (item is null || item.State != WorkItemState.Done) { allDone = false; break; }
            }
            if (allDone) return true;
            await Task.Delay(50);
        }
        return false;
    }
}

/// <summary>
/// Records the exact order in which items entered the pipeline.
/// </summary>
internal sealed class OrderedPipelineRunner : IPipelineRunner
{
    private readonly IWorkItemStore _store;
    private readonly System.Collections.Concurrent.ConcurrentQueue<WorkItemId> _order = new();

    public IReadOnlyList<WorkItemId> Order => _order.ToList();

    public OrderedPipelineRunner(IWorkItemStore store) => _store = store;

    public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
    {
        _order.Enqueue(item.Id);
        await _store.UpdateAsync(item.With(WorkItemState.Done), ct);
    }
}

/// <summary>
/// Pipeline that blocks each item on a shared TaskCompletionSource so tests can
/// observe pickup order one item at a time. <see cref="ResetEntered"/> rearms
/// the "next-entered" probe.
/// </summary>
internal sealed class GatedPipelineRunner : IPipelineRunner
{
    private readonly IWorkItemStore _store;
    private Task _release;
    private TaskCompletionSource<WorkItemId> _entered;
    private readonly object _lock = new();

    public GatedPipelineRunner(IWorkItemStore store, Task release, TaskCompletionSource<WorkItemId> entered)
    {
        _store = store;
        _release = release;
        _entered = entered;
    }

    public void ResetEntered()
    {
        lock (_lock)
        {
            _entered = new TaskCompletionSource<WorkItemId>(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = Task.CompletedTask;
        }
    }

    public Task<WorkItemId> NextEnteredAsync(TimeSpan timeout)
    {
        TaskCompletionSource<WorkItemId> tcs;
        lock (_lock) { tcs = _entered; }
        return tcs.Task.WaitAsync(timeout);
    }

    public void ReleaseAll()
    {
        lock (_lock) { _release = Task.CompletedTask; }
    }

    public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
    {
        TaskCompletionSource<WorkItemId> enteredSnapshot;
        Task releaseSnapshot;
        lock (_lock)
        {
            enteredSnapshot = _entered;
            releaseSnapshot = _release;
        }
        enteredSnapshot.TrySetResult(item.Id);
        await releaseSnapshot.WaitAsync(ct);
        await _store.UpdateAsync(item.With(WorkItemState.Done), ct);
    }
}
