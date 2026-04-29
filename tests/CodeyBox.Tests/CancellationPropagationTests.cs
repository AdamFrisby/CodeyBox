using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for cascade cancellation via
/// <see cref="WorkItemDependencies.FindCascadeCancelTargets"/>.
/// </summary>
public sealed class CancellationPropagationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-cancel-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public CancellationPropagationTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkItem Item(WorkItemState state, params WorkItemId[] deps) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "t",
        Prompt = "p",
        State = state,
        DependsOn = deps,
    };

    [Fact]
    public async Task CancelParent_CascadesToQueuedDependent()
    {
        var parent = Item(WorkItemState.Queued);
        var dependent = Item(WorkItemState.Queued, parent.Id);
        await _store.CreateAsync(parent);
        await _store.CreateAsync(dependent);

        var all = new List<WorkItem>();
        await foreach (var i in _store.ListAsync()) all.Add(i);

        var targets = WorkItemDependencies.FindCascadeCancelTargets(parent.Id, all);

        Assert.Single(targets);
        Assert.Equal(dependent.Id, targets[0].Id);
    }

    [Fact]
    public async Task CancelParent_DoesNotCascadeToInFlightDependent()
    {
        var parent = Item(WorkItemState.Queued);
        var inFlight = Item(WorkItemState.Working, parent.Id);
        await _store.CreateAsync(parent);
        await _store.CreateAsync(inFlight);

        var all = new List<WorkItem>();
        await foreach (var i in _store.ListAsync()) all.Add(i);

        var targets = WorkItemDependencies.FindCascadeCancelTargets(parent.Id, all);
        Assert.Empty(targets);
    }

    [Fact]
    public async Task CancelParent_TransitivelyCascades()
    {
        // A → B → C: cancelling A should cascade to both B and C.
        var a = Item(WorkItemState.Queued);
        var b = Item(WorkItemState.Queued, a.Id);
        var c = Item(WorkItemState.Queued, b.Id);
        await _store.CreateAsync(a);
        await _store.CreateAsync(b);
        await _store.CreateAsync(c);

        var all = new List<WorkItem>();
        await foreach (var i in _store.ListAsync()) all.Add(i);

        var targets = WorkItemDependencies.FindCascadeCancelTargets(a.Id, all);
        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, t => t.Id == b.Id);
        Assert.Contains(targets, t => t.Id == c.Id);
    }

    [Fact]
    public async Task CancelParent_MixedDependents_OnlyQueuedCancelled()
    {
        // A → B (Queued), A → C (Working). Cancel A: only B targeted.
        var a = Item(WorkItemState.Queued);
        var b = Item(WorkItemState.Queued, a.Id);
        var c = Item(WorkItemState.Working, a.Id);
        await _store.CreateAsync(a);
        await _store.CreateAsync(b);
        await _store.CreateAsync(c);

        var all = new List<WorkItem>();
        await foreach (var i in _store.ListAsync()) all.Add(i);

        var targets = WorkItemDependencies.FindCascadeCancelTargets(a.Id, all);
        Assert.Single(targets);
        Assert.Equal(b.Id, targets[0].Id);
    }

    [Fact]
    public async Task CancelParent_NoDependent_ReturnsEmpty()
    {
        var a = Item(WorkItemState.Queued);
        await _store.CreateAsync(a);

        var all = new List<WorkItem>();
        await foreach (var i in _store.ListAsync()) all.Add(i);

        var targets = WorkItemDependencies.FindCascadeCancelTargets(a.Id, all);
        Assert.Empty(targets);
    }

    [Fact]
    public async Task CancelParent_TerminalDependent_NotCascaded()
    {
        // A → B (Done). Cancelling A should not re-cancel B.
        var a = Item(WorkItemState.Queued);
        var b = Item(WorkItemState.Done, a.Id);
        await _store.CreateAsync(a);
        await _store.CreateAsync(b);

        var all = new List<WorkItem>();
        await foreach (var i in _store.ListAsync()) all.Add(i);

        var targets = WorkItemDependencies.FindCascadeCancelTargets(a.Id, all);
        Assert.Empty(targets);
    }

    [Fact]
    public async Task CascadeCancelPersists_InStore()
    {
        // Simulate the full cascade-cancel write path used by CancelAsync.
        var parent = Item(WorkItemState.Cancelled);
        var dependent = Item(WorkItemState.Queued, parent.Id);
        await _store.CreateAsync(parent);
        await _store.CreateAsync(dependent);

        var all = new List<WorkItem>();
        await foreach (var i in _store.ListAsync()) all.Add(i);

        var targets = WorkItemDependencies.FindCascadeCancelTargets(parent.Id, all);
        foreach (var target in targets)
        {
            var cancelled = target.With(WorkItemState.Cancelled, "parent dependency cancelled");
            await _store.UpdateAsync(cancelled);
        }

        var readBack = await _store.GetAsync(dependent.Id);
        Assert.NotNull(readBack);
        Assert.Equal(WorkItemState.Cancelled, readBack!.State);
        Assert.Equal("parent dependency cancelled", readBack.LastError);
    }
}
