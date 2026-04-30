using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the PATCH /workitems/{id} logic: state guard and field update behaviour.
/// The endpoint uses TryUpdateIfStateAsync to enforce the Queued-only constraint;
/// these tests exercise that path directly against the real store.
/// </summary>
public sealed class PatchWorkItemTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-patch-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public PatchWorkItemTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkItem Sample(WorkItemState state = WorkItemState.Queued) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "original title",
        Prompt = "original prompt",
        Agent = AgentKind.Claude,
        State = state,
    };

    [Fact]
    public async Task PatchTitle_WhenQueued_Succeeds()
    {
        var item = Sample(WorkItemState.Queued);
        await _store.CreateAsync(item);

        var patched = item with { Title = "new title", UpdatedAt = DateTimeOffset.UtcNow };
        var written = await _store.TryUpdateIfStateAsync(patched, WorkItemState.Queued);

        Assert.True(written);
        var read = await _store.GetAsync(item.Id);
        Assert.Equal("new title", read!.Title);
    }

    [Fact]
    public async Task PatchPrompt_WhenQueued_Succeeds()
    {
        var item = Sample(WorkItemState.Queued);
        await _store.CreateAsync(item);

        var patched = item with { Prompt = "updated prompt", UpdatedAt = DateTimeOffset.UtcNow };
        var written = await _store.TryUpdateIfStateAsync(patched, WorkItemState.Queued);

        Assert.True(written);
        var read = await _store.GetAsync(item.Id);
        Assert.Equal("updated prompt", read!.Prompt);
    }

    [Fact]
    public async Task Patch_WhenAuditing_ReturnsFalse()
    {
        // Item is in Auditing state — patch must be rejected (endpoint returns 409).
        var item = Sample(WorkItemState.Auditing);
        await _store.CreateAsync(item);

        var patched = item with { Title = "attempted edit", UpdatedAt = DateTimeOffset.UtcNow };
        // The endpoint calls TryUpdateIfStateAsync(patched, Queued); since the item is
        // Auditing, the conditional WHERE fails and returns false → endpoint returns 409.
        var written = await _store.TryUpdateIfStateAsync(patched, WorkItemState.Queued);

        Assert.False(written);
        // Original title should be unchanged
        var read = await _store.GetAsync(item.Id);
        Assert.Equal("original title", read!.Title);
    }

    [Fact]
    public async Task Patch_WhenWorking_ReturnsFalse()
    {
        var item = Sample(WorkItemState.Working);
        await _store.CreateAsync(item);

        var patched = item with { Title = "attempted edit", UpdatedAt = DateTimeOffset.UtcNow };
        var written = await _store.TryUpdateIfStateAsync(patched, WorkItemState.Queued);

        Assert.False(written);
    }

    [Fact]
    public async Task Patch_WhenDone_ReturnsFalse()
    {
        var item = Sample(WorkItemState.Done);
        await _store.CreateAsync(item);

        var patched = item with { Title = "attempted edit", UpdatedAt = DateTimeOffset.UtcNow };
        var written = await _store.TryUpdateIfStateAsync(patched, WorkItemState.Queued);

        Assert.False(written);
    }

    [Fact]
    public async Task Patch_WhenFailed_ReturnsFalse()
    {
        var item = Sample(WorkItemState.Failed);
        await _store.CreateAsync(item);

        var patched = item with { Title = "attempted edit", UpdatedAt = DateTimeOffset.UtcNow };
        var written = await _store.TryUpdateIfStateAsync(patched, WorkItemState.Queued);

        Assert.False(written);
    }

    [Fact]
    public async Task PatchAgent_WhenQueued_Persists()
    {
        var item = Sample(WorkItemState.Queued);
        await _store.CreateAsync(item);

        var patched = item with { Agent = AgentKind.Codex, UpdatedAt = DateTimeOffset.UtcNow };
        var written = await _store.TryUpdateIfStateAsync(patched, WorkItemState.Queued);

        Assert.True(written);
        var read = await _store.GetAsync(item.Id);
        Assert.Equal(AgentKind.Codex, read!.Agent);
    }

    [Fact]
    public async Task PatchPreservesQueuePosition()
    {
        // Patching title should not zero out the queue_position.
        var item = Sample(WorkItemState.Queued) with { QueuePosition = 42L };
        await _store.CreateAsync(item);

        var patched = item with { Title = "patched", UpdatedAt = DateTimeOffset.UtcNow };
        var written = await _store.TryUpdateIfStateAsync(patched, WorkItemState.Queued);

        Assert.True(written);
        var read = await _store.GetAsync(item.Id);
        Assert.Equal(42L, read!.QueuePosition);
    }
}
