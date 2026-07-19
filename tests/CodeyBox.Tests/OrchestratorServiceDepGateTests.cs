using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Exercises the OrchestratorService dependency gate and double-enqueue guard
/// by running a real worker loop with an in-memory store + queue and a
/// FakePipelineRunner that marks items Done instantly.
/// </summary>
[Collection("Background service timing")]
public sealed class OrchestratorServiceDepGateTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-orchtest-{Guid.NewGuid():N}.db");

    private readonly SqliteWorkItemStore _store;

    public OrchestratorServiceDepGateTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        TestTempArtifacts.DeleteSqliteDatabase(_dbPath);
    }

    private static WorkItem Sample(
        WorkItemState state = WorkItemState.Queued,
        params WorkItemId[] deps) => new()
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "t",
            Prompt = "p",
            State = state,
            DependsOn = deps,
        };

    private (OrchestratorService svc, InMemoryTaskQueue queue, FakePipelineRunner pipeline,
             CancellationRegistry registry)
        BuildOrchestrator(int concurrency = 1)
    {
        var queue = new InMemoryTaskQueue();
        var registry = new CancellationRegistry(CancellationToken.None);
        var pipeline = new FakePipelineRunner(_store);
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = concurrency };
        var svc = new OrchestratorService(
            queue, _store, pipeline, registry, opts,
            NullLogger<OrchestratorService>.Instance);
        return (svc, queue, pipeline, registry);
    }

    // ── Dependency gate: item with unsatisfied dep is NOT picked up ───────────

    [Fact]
    public async Task ItemWithUnsatisfiedDep_IsNotProcessed()
    {
        // dep is NOT in the store — its ID is referenced by dependent but
        // never created, so statesById won't contain it and AreSatisfied
        // returns false. This simulates any unsatisfied dependency state.
        var depId = WorkItemId.New();
        var dependent = Sample(WorkItemState.Queued, depId);
        await _store.CreateAsync(dependent);

        var (svc, queue, pipeline, registry) = BuildOrchestrator();
        using var _ = registry;

        // Manually enqueue the dependent (ReplayPendingAsync won't enqueue it
        // because depId is absent from the state map → not satisfied).
        await queue.EnqueueAsync(dependent.Id);

        // Start the service, give the single worker time to dequeue + skip.
        await svc.StartAsync(CancellationToken.None);
        await Task.Delay(300);

        // StopAsync cancels the worker and awaits the executing task — this
        // ensures we don't race the worker when we read pipeline.Executed.
        await svc.StopAsync(CancellationToken.None);

        // The dependent must still be in Queued state — NOT picked up.
        Assert.Empty(pipeline.Executed);
        var item = await _store.GetAsync(dependent.Id);
        Assert.Equal(WorkItemState.Queued, item!.State);
    }

    // ── After dep reaches Done, dependent is picked up ────────────────────────

    [Fact]
    public async Task AfterDepCompletes_DependentIsPickedUp()
    {
        var dep = Sample(WorkItemState.Queued);
        var dependent = Sample(WorkItemState.Queued, dep.Id);
        await _store.CreateAsync(dep);
        await _store.CreateAsync(dependent);

        // Only enqueue dep — it has no deps so it's immediately eligible.
        var (svc, queue, pipeline, registry) = BuildOrchestrator();
        using var _ = registry;
        await queue.EnqueueAsync(dep.Id);

        await svc.StartAsync(CancellationToken.None);

        // Wait for both items to reach Done (dep first, then dependent).
        var done = await WaitForStateAsync(dependent.Id, WorkItemState.Done, TimeSpan.FromSeconds(30));
        await svc.StopAsync(CancellationToken.None);

        Assert.True(done, "Dependent item should have been enqueued and processed after dep completed");
        Assert.Contains(dep.Id, pipeline.Executed);
        Assert.Contains(dependent.Id, pipeline.Executed);
    }

    // ── Failed dep BLOCKS the gate (conservative posture) ─────────────────────

    [Fact]
    public async Task DepInFailedState_DependentIsBlocked()
    {
        // A Failed dep does NOT satisfy the gate. The dependent stays Queued
        // until an operator retries the parent and it reaches Done. Running
        // the dependent against a failed prerequisite would burn agent quota
        // on work that cannot be validated end-to-end.
        var dep = Sample(WorkItemState.Failed);
        var dependent = Sample(WorkItemState.Queued, dep.Id);
        await _store.CreateAsync(dep);
        await _store.CreateAsync(dependent);

        var (svc, queue, pipeline, registry) = BuildOrchestrator();
        using var _ = registry;
        await queue.EnqueueAsync(dependent.Id);

        await svc.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        await svc.StopAsync(CancellationToken.None);

        Assert.DoesNotContain(dependent.Id, pipeline.Executed);
        var still = await _store.GetAsync(dependent.Id);
        Assert.Equal(WorkItemState.Queued, still!.State);
    }

    [Theory]
    [InlineData(WorkItemState.AuditFailed)]
    [InlineData(WorkItemState.Cancelled)]
    [InlineData(WorkItemState.MergeConflictResolutionFailed)]
    [InlineData(WorkItemState.AbandonedAfterRecoveryAttempts)]
    public async Task DepInNonDoneTerminalState_DependentIsBlocked(WorkItemState terminalState)
    {
        var dep = Sample(terminalState);
        var dependent = Sample(WorkItemState.Queued, dep.Id);
        await _store.CreateAsync(dep);
        await _store.CreateAsync(dependent);

        var (svc, queue, pipeline, registry) = BuildOrchestrator();
        using var _ = registry;
        await queue.EnqueueAsync(dependent.Id);

        await svc.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        await svc.StopAsync(CancellationToken.None);

        Assert.DoesNotContain(dependent.Id, pipeline.Executed);
        var still = await _store.GetAsync(dependent.Id);
        Assert.Equal(WorkItemState.Queued, still!.State);
    }

    // ── Live-incident reproduction: Queued dep must block dispatch ────────────

    [Fact]
    public async Task DepInQueuedState_DispatcherRefusesToPickUpDependent()
    {
        // Live incident 2026-05-18: a Queued dep let the dependent's worker
        // proceed into Auditing. The dispatch query MUST honour the dep gate
        // even when a kick exists for the dependent and a worker slot is
        // free. We exercise PickNextEligibleAsync directly here so the test
        // pins the gate decision rather than racing it against the loop.
        var dep = Sample(WorkItemState.Queued);
        var dependent = Sample(WorkItemState.Queued, dep.Id);
        await _store.CreateAsync(dep);
        await _store.CreateAsync(dependent);

        var (svc, _, _, registry) = BuildOrchestrator(concurrency: 2);
        using var _r = registry;

        // Pre-claim the dep slot so the priority pickup must consider the
        // dependent next — mirrors the production race where dep is in
        // flight on another worker while the dispatcher decides whether
        // the dependent is eligible. The Queued state in store means the
        // gate's AreSatisfied check sees a non-satisfying state.
        svc.MarkDeferredForTest(dep.Id);

        var pick = await svc.PickNextEligibleForTestAsync(CancellationToken.None);
        Assert.Null(pick);
    }

    // ── Acceptance #1: concurrency=2, only A runs first; B follows after Done ─

    [Fact]
    public async Task TwoItemsAB_OnlyARunsFirst_ThenBAfterADone()
    {
        var a = Sample(WorkItemState.Queued);
        var b = Sample(WorkItemState.Queued, a.Id);
        await _store.CreateAsync(a);
        await _store.CreateAsync(b);

        // Pausable pipeline: A blocks until we release it, so we can observe
        // that B remains Queued while A is in-flight — i.e. the dep gate
        // actually held even with a second worker slot free.
        var queue = new InMemoryTaskQueue();
        var registry = new CancellationRegistry(CancellationToken.None);
        var release = new TaskCompletionSource();
        var pipeline = new PausablePipelineRunner(_store, release.Task, a.Id);
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 2 };
        var svc = new OrchestratorService(
            queue, _store, pipeline, registry, opts,
            NullLogger<OrchestratorService>.Instance);
        using var _ = registry;

        await queue.EnqueueAsync(a.Id);
        await queue.EnqueueAsync(b.Id);
        await svc.StartAsync(CancellationToken.None);

        // Wait for A to actually enter RunAsync (so its state is at least
        // Working) before checking B.
        var aStarted = await pipeline.WaitForEnteredAsync(a.Id, TimeSpan.FromSeconds(5));
        Assert.True(aStarted, "A should have entered the pipeline");

        // While A is held, the dispatcher's second worker slot is free —
        // but B's gate must hold because A has not yet reached Done.
        await Task.Delay(200);
        var bMid = await _store.GetAsync(b.Id);
        Assert.Equal(WorkItemState.Queued, bMid!.State);
        Assert.DoesNotContain(b.Id, pipeline.Executed);

        // Release A → it transitions to Done → B's gate is satisfied →
        // B is enqueued and runs.
        release.SetResult();
        var bDone = await WaitForStateAsync(b.Id, WorkItemState.Done, TimeSpan.FromSeconds(30));
        await svc.StopAsync(CancellationToken.None);

        Assert.True(bDone, "B should complete after A reaches Done");
        Assert.Contains(a.Id, pipeline.Executed);
        Assert.Contains(b.Id, pipeline.Executed);
    }

    // ── Acceptance #2: items without deps continue to dispatch normally ───────

    [Fact]
    public async Task ItemsWithoutDeps_DispatchNormally()
    {
        var a = Sample();
        var b = Sample();
        var c = Sample();
        await _store.CreateAsync(a);
        await _store.CreateAsync(b);
        await _store.CreateAsync(c);

        var (svc, queue, pipeline, registry) = BuildOrchestrator(concurrency: 2);
        using var _ = registry;
        await queue.EnqueueAsync(a.Id);
        await queue.EnqueueAsync(b.Id);
        await queue.EnqueueAsync(c.Id);

        await svc.StartAsync(CancellationToken.None);
        var allDone = await WaitForStateAsync(c.Id, WorkItemState.Done, TimeSpan.FromSeconds(30))
            && await WaitForStateAsync(b.Id, WorkItemState.Done, TimeSpan.FromSeconds(5))
            && await WaitForStateAsync(a.Id, WorkItemState.Done, TimeSpan.FromSeconds(5));
        await svc.StopAsync(CancellationToken.None);

        Assert.True(allDone);
        Assert.Contains(a.Id, pipeline.Executed);
        Assert.Contains(b.Id, pipeline.Executed);
        Assert.Contains(c.Id, pipeline.Executed);
    }

    // ── Acceptance #4: PickNextEligibleAsync recomputes on each pickup ────────

    [Fact]
    public async Task PickNextEligible_RecomputesAfterDepStateChanges()
    {
        // Start with dep=Queued and dependent=Queued. PickNextEligibleAsync
        // must return null (gate not satisfied). Flip dep to Done, call
        // again — must now return the dependent. Confirms the gate is
        // recomputed from a fresh store snapshot per pickup, not from a
        // cached boolean.
        var dep = Sample(WorkItemState.Queued);
        var dependent = Sample(WorkItemState.Queued, dep.Id);
        await _store.CreateAsync(dep);
        await _store.CreateAsync(dependent);

        var (svc, _, _, registry) = BuildOrchestrator();
        using var _r = registry;

        var first = await svc.PickNextEligibleForTestAsync(CancellationToken.None);
        Assert.Equal(dep.Id, first);

        // Flip dep to Done and pretend it's no longer in flight.
        await _store.UpdateAsync(dep.With(WorkItemState.Done));

        var second = await svc.PickNextEligibleForTestAsync(CancellationToken.None);
        Assert.Equal(dependent.Id, second);
    }

    // ── Double-enqueue: same item not processed twice concurrently ────────────

    [Fact]
    public async Task DoubleEnqueue_ItemProcessedOnlyOnce()
    {
        var item = Sample(WorkItemState.Queued);
        await _store.CreateAsync(item);

        var (svc, queue, pipeline, registry) = BuildOrchestrator(concurrency: 2);
        using var _ = registry;

        // Enqueue the same item twice to simulate double-enqueue race.
        await queue.EnqueueAsync(item.Id);
        await queue.EnqueueAsync(item.Id);

        await svc.StartAsync(CancellationToken.None);
        await Task.Delay(500);
        await svc.StopAsync(CancellationToken.None);

        // Should have been executed at most once (not twice).
        Assert.True(
            pipeline.Executed.Count(id => id == item.Id) <= 1,
            "Item must not be processed concurrently by two workers");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<bool> WaitForStateAsync(
        WorkItemId id,
        WorkItemState target,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var item = await _store.GetAsync(id);
            if (item?.State == target) return true;
            await Task.Delay(50);
        }
        return false;
    }
}

/// <summary>
/// Stub pipeline that records executed work items and immediately marks each
/// one Done in the store. This lets the orchestrator's dep-satisfaction logic
/// trigger and re-enqueue waiting dependents.
/// </summary>
internal sealed class FakePipelineRunner : IPipelineRunner
{
    private readonly IWorkItemStore _store;
    private readonly ConcurrentBag<WorkItemId> _executed = new();

    public IReadOnlyCollection<WorkItemId> Executed => _executed;

    public FakePipelineRunner(IWorkItemStore store) { _store = store; }

    public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
    {
        _executed.Add(item.Id);
        await _store.UpdateAsync(item.With(WorkItemState.Done), ct);
    }
}

/// <summary>
/// Pipeline runner that pauses RunAsync for a specified work item until an
/// external <c>release</c> signal fires. Lets tests observe the dispatcher's
/// behaviour mid-flight — specifically that a dep-gated dependent stays
/// Queued while its parent is still in flight.
/// </summary>
internal sealed class PausablePipelineRunner : IPipelineRunner
{
    private readonly IWorkItemStore _store;
    private readonly Task _release;
    private readonly WorkItemId _pauseFor;
    private readonly ConcurrentBag<WorkItemId> _executed = new();
    private readonly ConcurrentDictionary<WorkItemId, TaskCompletionSource> _enteredSignals = new();

    public IReadOnlyCollection<WorkItemId> Executed => _executed;

    public PausablePipelineRunner(IWorkItemStore store, Task release, WorkItemId pauseFor)
    {
        _store = store;
        _release = release;
        _pauseFor = pauseFor;
    }

    public async Task<bool> WaitForEnteredAsync(WorkItemId id, TimeSpan timeout)
    {
        var tcs = _enteredSignals.GetOrAdd(id, _ => new TaskCompletionSource());
        return await Task.WhenAny(tcs.Task, Task.Delay(timeout)) == tcs.Task;
    }

    public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
    {
        _executed.Add(item.Id);
        _enteredSignals.GetOrAdd(item.Id, _ => new TaskCompletionSource()).TrySetResult();
        if (item.Id == _pauseFor) await _release;
        await _store.UpdateAsync(item.With(WorkItemState.Done), ct);
    }
}
