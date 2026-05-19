using System.Net;
using System.Net.Http.Json;
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

/// <summary>
/// HTTP-level tests for PATCH /workitems/{id}. Verifies status codes returned by
/// the real endpoint handler — routing, validation, and state-guard paths.
/// A fresh server + store is created per test method for isolation.
///
/// Joined to <c>GlobalSerilog</c> because <c>WebApplicationFactory</c> startup
/// runs Program.cs's Serilog bootstrap, which mutates the static
/// <see cref="Serilog.Log.Logger"/>; this serializes us with other tests that
/// observe or write to that global.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class PatchWorkItemHttpTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public PatchWorkItemHttpTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static WorkItem QueuedItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "Original",
        Prompt = "Original prompt",
        Agent = AgentKind.Claude,
        State = WorkItemState.Queued,
    };

    [Fact]
    public async Task Patch_WhenQueued_Returns200Ok()
    {
        var item = QueuedItem();
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new { title = "Updated Title" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Patch_WhenNotQueued_Returns409Conflict()
    {
        var item = QueuedItem() with { State = WorkItemState.Working };
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new { title = "Updated" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Patch_WhenAuditing_Returns409Conflict()
    {
        var item = QueuedItem() with { State = WorkItemState.Auditing };
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new { title = "Updated" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Patch_UnknownAgent_Returns400BadRequest()
    {
        var item = QueuedItem();
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new { agent = "unknown-agent-xyz" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Patch_OversizedTitle_Returns400BadRequest()
    {
        var item = QueuedItem();
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new { title = new string('x', 201) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Patch_OversizedPrompt_Returns400BadRequest()
    {
        var item = QueuedItem();
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new { prompt = new string('p', 65 * 1024 + 1) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Patch_NotFound_Returns404()
    {
        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{Guid.NewGuid()}",
            new { title = "Anything" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Patch_InvalidId_Returns400()
    {
        var response = await _client.PatchAsJsonAsync(
            "/workitems/not-a-guid",
            new { title = "Anything" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Timeout / score / priority patches ──────────────────────────────────
    //
    // These guard the operator-led defaults-migration path: when WorkItem
    // defaults change (commit c980620 bumped WorkTimeout 60→240m), pre-existing
    // Queued items keep their old persisted value and die at the old wall. PATCH
    // lets the operator bulk-bump those items in place without losing queue
    // position or breaking dependsOn references.

    [Fact]
    public async Task PatchWorkTimeout_WhenQueued_PersistsAndReturnsNewValue()
    {
        var item = QueuedItem() with { WorkTimeout = TimeSpan.FromMinutes(60) };
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new { workTimeoutMinutes = 240 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var fetched = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(TimeSpan.FromMinutes(240), fetched!.WorkTimeout);
    }

    [Fact]
    public async Task PatchMergeTimeout_WhenQueued_Persists()
    {
        var item = QueuedItem() with { MergeTimeout = TimeSpan.FromMinutes(15) };
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new { mergeTimeoutMinutes = 60 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var fetched = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(TimeSpan.FromMinutes(60), fetched!.MergeTimeout);
    }

    [Fact]
    public async Task PatchWorkTimeout_AboveMax_ClampsToBoundary()
    {
        // Mirrors POST /workitems behaviour: out-of-range values clamp silently
        // rather than 400. This is intentional so an operator bulk-bumping a
        // queue after a defaults change never has to special-case stray inputs.
        var item = QueuedItem();
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new { workTimeoutMinutes = 9999 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var fetched = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(TimeSpan.FromMinutes(480), fetched!.WorkTimeout);
    }

    [Fact]
    public async Task PatchWorkTimeout_BelowMin_ClampsToOne()
    {
        var item = QueuedItem();
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new { workTimeoutMinutes = 0 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var fetched = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(TimeSpan.FromMinutes(1), fetched!.WorkTimeout);
    }

    [Fact]
    public async Task PatchMergeTimeout_AboveMax_ClampsTo240()
    {
        var item = QueuedItem();
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new { mergeTimeoutMinutes = 9999 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var fetched = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(TimeSpan.FromMinutes(240), fetched!.MergeTimeout);
    }

    [Fact]
    public async Task PatchMinModelScore_WhenQueued_PersistsClamped()
    {
        var item = QueuedItem();
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new { minModelScore = 80 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var fetched = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(80, fetched!.MinModelScore);
    }

    [Fact]
    public async Task PatchMinModelScore_AboveMax_ClampsTo200()
    {
        var item = QueuedItem();
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new { minModelScore = 9999 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var fetched = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(200, fetched!.MinModelScore);
    }

    [Fact]
    public async Task PatchPriority_FieldIsIgnoredSilently()
    {
        // Sanity check: the JSON binder accepts unknown fields, so a request body
        // with `priority` (which is NOT in PatchWorkItemRequest) just no-ops on
        // priority. Operators wanting to change priority must use
        // PATCH /workitems/{id}/priority. See the endpoint summary for why
        // priority isn't on this request: it has its own TOCTOU-safe partial
        // UPDATE column path that TryUpdateIfStateAsync deliberately bypasses.
        var item = QueuedItem() with { ProjectId = new ProjectId("test-project"), Priority = 0 };
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new { priority = 250, title = "still-patches-other-fields" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var fetched = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(0, fetched!.Priority);
        Assert.Equal("still-patches-other-fields", fetched.Title);
    }

    [Fact]
    public async Task PatchWorkTimeout_WhenNotQueued_Returns409()
    {
        var item = QueuedItem() with
        {
            State = WorkItemState.Working,
            WorkTimeout = TimeSpan.FromMinutes(60),
        };
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new { workTimeoutMinutes = 240 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var fetched = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(TimeSpan.FromMinutes(60), fetched!.WorkTimeout);
    }

    [Fact]
    public async Task PatchMergeTimeout_WhenAuditing_Returns409()
    {
        var item = QueuedItem() with
        {
            State = WorkItemState.Auditing,
            MergeTimeout = TimeSpan.FromMinutes(15),
        };
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new { mergeTimeoutMinutes = 60 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var fetched = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(TimeSpan.FromMinutes(15), fetched!.MergeTimeout);
    }

    [Fact]
    public async Task PatchCombinedFields_AppliesAllInOneCall()
    {
        // Operator-led bulk migration: one PATCH that updates both timeouts
        // and the model-score floor. Validates that the handler accumulates
        // the with-clones rather than silently dropping fields.
        var item = QueuedItem() with
        {
            WorkTimeout = TimeSpan.FromMinutes(60),
            MergeTimeout = TimeSpan.FromMinutes(15),
        };
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new
            {
                workTimeoutMinutes = 240,
                mergeTimeoutMinutes = 60,
                minModelScore = 70,
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var fetched = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(TimeSpan.FromMinutes(240), fetched!.WorkTimeout);
        Assert.Equal(TimeSpan.FromMinutes(60), fetched.MergeTimeout);
        Assert.Equal(70, fetched.MinModelScore);
    }

    [Fact]
    public async Task PatchWorkTimeout_PreBumpSimulation_ItemPicksUpNewCap()
    {
        // End-to-end simulation of the bug-report scenario:
        //   1. Item created under the OLD 60-minute default.
        //   2. Defaults bump ships; the item is still Queued.
        //   3. Operator PATCHes the new cap onto the item.
        //   4. Dispatch reads the patched timeout, not the old default.
        var item = QueuedItem() with { WorkTimeout = TimeSpan.FromMinutes(60) };
        await _factory.Store.CreateAsync(item);

        var patch = await _client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new { workTimeoutMinutes = 240 });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        // Re-read as the dispatcher would: the next pickup sees the patched value.
        var ready = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, ready!.State);
        Assert.Equal(TimeSpan.FromMinutes(240), ready.WorkTimeout);

        // Verify queue position survived the patch — operator must not lose
        // ordering when bulk-bumping the queue.
        Assert.Equal(item.QueuePosition, ready.QueuePosition);
    }
}
