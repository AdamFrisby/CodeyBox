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
        try { File.Delete(_dbPath); } catch { }
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
        var opts = new OrchestratorOptions { Concurrency = concurrency };
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
        var done = await WaitForStateAsync(dependent.Id, WorkItemState.Done, TimeSpan.FromSeconds(5));
        await svc.StopAsync(CancellationToken.None);

        Assert.True(done, "Dependent item should have been enqueued and processed after dep completed");
        Assert.Contains(dep.Id, pipeline.Executed);
        Assert.Contains(dependent.Id, pipeline.Executed);
    }

    // ── Failed dep still satisfies gate ──────────────────────────────────────

    [Fact]
    public async Task DepInFailedState_DependentIsPickedUp()
    {
        var dep = Sample(WorkItemState.Failed);
        var dependent = Sample(WorkItemState.Queued, dep.Id);
        await _store.CreateAsync(dep);
        await _store.CreateAsync(dependent);

        var (svc, queue, pipeline, registry) = BuildOrchestrator();
        using var _ = registry;
        await queue.EnqueueAsync(dependent.Id);

        await svc.StartAsync(CancellationToken.None);
        var done = await WaitForStateAsync(dependent.Id, WorkItemState.Done, TimeSpan.FromSeconds(5));
        await svc.StopAsync(CancellationToken.None);

        Assert.True(done, "Dependent should be picked up when dep is in Failed state");
        Assert.Contains(dependent.Id, pipeline.Executed);
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

    public async Task RunAsync(WorkItem item, CancellationToken ct)
    {
        _executed.Add(item.Id);
        await _store.UpdateAsync(item.With(WorkItemState.Done), ct);
    }
}
