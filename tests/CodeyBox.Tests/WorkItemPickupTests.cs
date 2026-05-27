using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests that dependency gating works correctly at enqueue-decision time.
/// These tests exercise <see cref="WorkItemDependencies"/> and the
/// <see cref="OrchestratorService.EnqueueSatisfiedDependentsAsync"/> logic
/// via the store + queue directly.
/// </summary>
public sealed class WorkItemPickupTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-pickup-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public WorkItemPickupTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkItem Sample(WorkItemState state = WorkItemState.Queued, params WorkItemId[] deps) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "t",
        Prompt = "p",
        State = state,
        DependsOn = deps,
    };

    // ── Dep satisfaction at create time ───────────────────────────────────────

    [Fact]
    public async Task ItemWithNoDeps_IsSatisfied()
    {
        var item = Sample();
        await _store.CreateAsync(item);

        var all = new List<WorkItem>();
        await foreach (var i in _store.ListAsync()) all.Add(i);
        var states = WorkItemDependencies.BuildStateMap(all);

        Assert.True(WorkItemDependencies.AreSatisfied(item.DependsOn, states));
    }

    [Fact]
    public async Task ItemWithQueuedDep_IsNotSatisfied()
    {
        var dep = Sample(WorkItemState.Queued);
        var dependent = Sample(WorkItemState.Queued, dep.Id);
        await _store.CreateAsync(dep);
        await _store.CreateAsync(dependent);

        var all = new List<WorkItem>();
        await foreach (var i in _store.ListAsync()) all.Add(i);
        var states = WorkItemDependencies.BuildStateMap(all);

        Assert.False(WorkItemDependencies.AreSatisfied(dependent.DependsOn, states));
    }

    [Fact]
    public async Task ItemWithDoneDep_IsSatisfied()
    {
        var dep = Sample(WorkItemState.Done);
        var dependent = Sample(WorkItemState.Queued, dep.Id);
        await _store.CreateAsync(dep);
        await _store.CreateAsync(dependent);

        var all = new List<WorkItem>();
        await foreach (var i in _store.ListAsync()) all.Add(i);
        var states = WorkItemDependencies.BuildStateMap(all);

        Assert.True(WorkItemDependencies.AreSatisfied(dependent.DependsOn, states));
    }

    [Fact]
    public async Task ItemWithFailedDep_IsNotSatisfied()
    {
        // A Failed dependency BLOCKS the gate — operator must retry-and-
        // resolve the parent (so it reaches Done) before dependents become
        // eligible. Running a dependent against a failed prerequisite would
        // burn agent quota on work that cannot be validated end-to-end.
        var dep = Sample(WorkItemState.Failed);
        var dependent = Sample(WorkItemState.Queued, dep.Id);
        await _store.CreateAsync(dep);
        await _store.CreateAsync(dependent);

        var all = new List<WorkItem>();
        await foreach (var i in _store.ListAsync()) all.Add(i);
        var states = WorkItemDependencies.BuildStateMap(all);

        Assert.False(WorkItemDependencies.AreSatisfied(dependent.DependsOn, states));
    }

    [Fact]
    public async Task ItemWithAuditFailedDep_IsNotSatisfied()
    {
        var dep = Sample(WorkItemState.AuditFailed);
        var dependent = Sample(WorkItemState.Queued, dep.Id);
        await _store.CreateAsync(dep);
        await _store.CreateAsync(dependent);

        var all = new List<WorkItem>();
        await foreach (var i in _store.ListAsync()) all.Add(i);
        var states = WorkItemDependencies.BuildStateMap(all);

        Assert.False(WorkItemDependencies.AreSatisfied(dependent.DependsOn, states));
    }

    [Fact]
    public async Task ItemWithCancelledDep_IsNotSatisfied()
    {
        var dep = Sample(WorkItemState.Cancelled);
        var dependent = Sample(WorkItemState.Queued, dep.Id);
        await _store.CreateAsync(dep);
        await _store.CreateAsync(dependent);

        var all = new List<WorkItem>();
        await foreach (var i in _store.ListAsync()) all.Add(i);
        var states = WorkItemDependencies.BuildStateMap(all);

        Assert.False(WorkItemDependencies.AreSatisfied(dependent.DependsOn, states));
    }

    // ── FindSatisfiedDependents ───────────────────────────────────────────────

    [Fact]
    public async Task FindSatisfiedDependents_DepBecomesTerminal_ReturnsDependent()
    {
        var dep = Sample(WorkItemState.Queued);
        var dependent = Sample(WorkItemState.Queued, dep.Id);
        await _store.CreateAsync(dep);
        await _store.CreateAsync(dependent);

        // Simulate dep reaching Done.
        await _store.UpdateAsync(dep.With(WorkItemState.Done));

        var all = new List<WorkItem>();
        await foreach (var i in _store.ListAsync()) all.Add(i);
        var states = WorkItemDependencies.BuildStateMap(all);

        var ready = WorkItemDependencies.FindSatisfiedDependents(dep.Id, all, states).ToList();
        Assert.Single(ready);
        Assert.Equal(dependent.Id, ready[0].Id);
    }

    [Fact]
    public async Task FindSatisfiedDependents_OneDepStillWorking_ReturnsNone()
    {
        var dep1 = Sample(WorkItemState.Done);
        var dep2 = Sample(WorkItemState.Working);
        var dependent = Sample(WorkItemState.Queued, dep1.Id, dep2.Id);
        await _store.CreateAsync(dep1);
        await _store.CreateAsync(dep2);
        await _store.CreateAsync(dependent);

        var all = new List<WorkItem>();
        await foreach (var i in _store.ListAsync()) all.Add(i);
        var states = WorkItemDependencies.BuildStateMap(all);

        // dep1 just became terminal, but dep2 is still Working.
        var ready = WorkItemDependencies.FindSatisfiedDependents(dep1.Id, all, states).ToList();
        Assert.Empty(ready);
    }

    [Fact]
    public async Task FindSatisfiedDependents_OneDepFailed_ReturnsNone()
    {
        // dep2 ended in Failed — the dependent stays blocked even though
        // dep1 reached Done. Operator must retry-and-resolve dep2 first.
        var dep1 = Sample(WorkItemState.Done);
        var dep2 = Sample(WorkItemState.Failed);
        var dependent = Sample(WorkItemState.Queued, dep1.Id, dep2.Id);
        await _store.CreateAsync(dep1);
        await _store.CreateAsync(dep2);
        await _store.CreateAsync(dependent);

        var all = new List<WorkItem>();
        await foreach (var i in _store.ListAsync()) all.Add(i);
        var states = WorkItemDependencies.BuildStateMap(all);

        // Trigger from dep2 reaching Failed: dependent must NOT be returned.
        var ready = WorkItemDependencies.FindSatisfiedDependents(dep2.Id, all, states).ToList();
        Assert.Empty(ready);
    }

    [Fact]
    public async Task FindSatisfiedDependents_BothDepsDone_ReturnsDependentOnce()
    {
        var dep1 = Sample(WorkItemState.Done);
        var dep2 = Sample(WorkItemState.Done);
        var dependent = Sample(WorkItemState.Queued, dep1.Id, dep2.Id);
        await _store.CreateAsync(dep1);
        await _store.CreateAsync(dep2);
        await _store.CreateAsync(dependent);

        var all = new List<WorkItem>();
        await foreach (var i in _store.ListAsync()) all.Add(i);
        var states = WorkItemDependencies.BuildStateMap(all);

        // Trigger from dep2 becoming Done — dependent is now eligible.
        var ready = WorkItemDependencies.FindSatisfiedDependents(dep2.Id, all, states).ToList();
        Assert.Single(ready);
        Assert.Equal(dependent.Id, ready[0].Id);
    }

    [Fact]
    public async Task FindSatisfiedDependents_DependentAlreadyWorking_NotReturned()
    {
        // If a dependent was somehow already picked up (race), it should be
        // excluded (state != Queued).
        var dep = Sample(WorkItemState.Done);
        var dependent = Sample(WorkItemState.Working, dep.Id);
        await _store.CreateAsync(dep);
        await _store.CreateAsync(dependent);

        var all = new List<WorkItem>();
        await foreach (var i in _store.ListAsync()) all.Add(i);
        var states = WorkItemDependencies.BuildStateMap(all);

        var ready = WorkItemDependencies.FindSatisfiedDependents(dep.Id, all, states).ToList();
        Assert.Empty(ready);
    }
}
