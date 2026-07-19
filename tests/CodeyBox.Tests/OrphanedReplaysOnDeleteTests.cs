using System.Net;
using System.Net.Http.Json;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that cancelling a source work item orphans its replays — the replays'
/// replay_of_work_item_id is set to null but they keep running (state unchanged).
///
/// "Deleting" in this system means cancelling (work items are never hard-deleted).
/// </summary>
[Collection("GlobalSerilog")]
public sealed class OrphanedReplaysOnDeleteTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public OrphanedReplaysOnDeleteTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static WorkItem Item(WorkItemState state) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "item",
        Prompt = "p",
        Agent = AgentKind.Claude,
        State = state,
    };

    [Fact]
    public async Task CancelSource_OrphansReplay_ReplayOfBecomesNull()
    {
        var source = Item(WorkItemState.Done);
        await _factory.Store.CreateAsync(source);

        var replayResp = await _client.PostAsJsonAsync($"/workitems/{source.Id}/replay", new { });
        var replayDto = await replayResp.Content.ReadFromJsonAsync<IdOnlyResponse>();
        var replayId = new WorkItemId(Guid.Parse(replayDto!.Id));

        // Confirm replay_of is set before cancel
        var beforeCancel = await _factory.Store.GetAsync(replayId);
        Assert.Equal(source.Id, beforeCancel!.ReplayOfWorkItemId);

        // Cancel (= "delete") the source — it transitions to Queued first for cancel to work
        var queuedSource = source with { State = WorkItemState.Queued };
        await _factory.Store.UpdateAsync(queuedSource);
        await _client.DeleteAsync($"/workitems/{source.Id}");

        // Replay's replay_of link must now be null
        var afterCancel = await _factory.Store.GetAsync(replayId);
        Assert.Null(afterCancel!.ReplayOfWorkItemId);
    }

    [Fact]
    public async Task CancelSource_OrphanedReplay_KeepsItsQueuedState()
    {
        var source = Item(WorkItemState.Done);
        await _factory.Store.CreateAsync(source);

        var replayResp = await _client.PostAsJsonAsync($"/workitems/{source.Id}/replay", new { });
        var replayDto = await replayResp.Content.ReadFromJsonAsync<IdOnlyResponse>();
        var replayId = new WorkItemId(Guid.Parse(replayDto!.Id));

        // Transition source to Queued so cancel is allowed
        var queuedSource = source with { State = WorkItemState.Queued };
        await _factory.Store.UpdateAsync(queuedSource);
        await _client.DeleteAsync($"/workitems/{source.Id}");

        // Replay still Queued — cancel does not cascade to it
        var afterCancel = await _factory.Store.GetAsync(replayId);
        Assert.Equal(WorkItemState.Queued, afterCancel!.State);
    }

    [Fact]
    public async Task CancelSource_MultipleReplays_AllOrphaned()
    {
        var source = Item(WorkItemState.Done);
        await _factory.Store.CreateAsync(source);

        var r1Resp = await _client.PostAsJsonAsync($"/workitems/{source.Id}/replay", new { agent = "codex" });
        var r2Resp = await _client.PostAsJsonAsync($"/workitems/{source.Id}/replay", new { agent = "gemini" });

        var r1Id = new WorkItemId(Guid.Parse((await r1Resp.Content.ReadFromJsonAsync<IdOnlyResponse>())!.Id));
        var r2Id = new WorkItemId(Guid.Parse((await r2Resp.Content.ReadFromJsonAsync<IdOnlyResponse>())!.Id));

        var queuedSource = source with { State = WorkItemState.Queued };
        await _factory.Store.UpdateAsync(queuedSource);
        await _client.DeleteAsync($"/workitems/{source.Id}");

        var after1 = await _factory.Store.GetAsync(r1Id);
        var after2 = await _factory.Store.GetAsync(r2Id);
        Assert.Null(after1!.ReplayOfWorkItemId);
        Assert.Null(after2!.ReplayOfWorkItemId);
    }

    [Fact]
    public async Task StoreOrphanReplays_DirectCall_ClearsLink()
    {
        // Test the store method directly (without going through HTTP cancel).
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-orphan-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new SqliteWorkItemStore(dbPath);
            var sourceId = WorkItemId.New();
            var source = new WorkItem
            {
                Id = sourceId,
                ProjectId = new ProjectId("p"),
                Title = "source",
                Prompt = "p",
                State = WorkItemState.Done,
            };
            await store.CreateAsync(source);

            var replay = new WorkItem
            {
                Id = WorkItemId.New(),
                ProjectId = new ProjectId("p"),
                Title = "replay",
                Prompt = "p",
                State = WorkItemState.Queued,
                ReplayOfWorkItemId = sourceId,
            };
            await store.CreateAsync(replay);

            // Confirm link present
            var before = await store.GetAsync(replay.Id);
            Assert.Equal(sourceId, before!.ReplayOfWorkItemId);

            // Orphan
            await store.OrphanReplaysAsync(sourceId);

            var after = await store.GetAsync(replay.Id);
            Assert.Null(after!.ReplayOfWorkItemId);
        }
        finally
        {
            TestTempArtifacts.DeleteSqliteDatabase(dbPath);
        }
    }

    private sealed record IdOnlyResponse(string Id);
}
