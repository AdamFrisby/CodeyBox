using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
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
        // PATCH-prompt persistence is routed through TryReplacePromptAsync —
        // the only write path that touches prompt + prompt_revision atomically.
        // TryUpdateIfStateAsync deliberately omits both columns (see
        // IWorkItemStore.UpdateAsync docstring) so a worker's stale in-memory
        // snapshot cannot revert a concurrent PUT /workitems/{id}/prompt.
        var item = Sample(WorkItemState.Queued);
        await _store.CreateAsync(item);

        var result = await _store.TryReplacePromptAsync(item.Id, "updated prompt", DateTimeOffset.UtcNow);
        Assert.Equal(PromptReplaceOutcome.Updated, result.Outcome);
        Assert.Equal(item.PromptRevision + 1, result.NewRevision);

        var read = await _store.GetAsync(item.Id);
        Assert.Equal("updated prompt", read!.Prompt);
        Assert.Equal(item.PromptRevision + 1, read.PromptRevision);
    }

    [Fact]
    public async Task TryUpdateIfStateAsync_DoesNotClobberPrompt_OnStaleSnapshot()
    {
        // Regression guard: the orchestrator routinely calls UpdateAsync /
        // TryUpdateIfStateAsync with an in-memory snapshot of the work item to
        // record state transitions. Earlier rows of that pipeline may carry an
        // older PromptRevision than the row currently in SQLite (a PUT
        // /workitems/{id}/prompt landed mid-flight). The full-row UPDATE must
        // therefore exclude the prompt + prompt_revision columns; otherwise the
        // mid-flight bump silently disappears and the next iteration dispatches
        // against the wrong revision.
        var item = Sample(WorkItemState.Queued);
        await _store.CreateAsync(item);

        await _store.TryReplacePromptAsync(item.Id, "fresh-prompt", DateTimeOffset.UtcNow);

        var stale = item with { Title = "stale-update", UpdatedAt = DateTimeOffset.UtcNow };
        var written = await _store.TryUpdateIfStateAsync(stale, WorkItemState.Queued);
        Assert.True(written);

        var read = await _store.GetAsync(item.Id);
        Assert.Equal("stale-update", read!.Title);
        Assert.Equal("fresh-prompt", read.Prompt);
        Assert.Equal(item.PromptRevision + 1, read.PromptRevision);
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

    // ── DependsOn replace-set ───────────────────────────────────────────────
    //
    // PATCH /workitems/{id} accepts an optional dependsOn array. Unlike the
    // other fields it is allowed on any non-terminal item — adding a dep to a
    // Queued item that's still waiting is the whole reason this field exists,
    // and avoids the cancel-and-recreate workaround.

    [Fact]
    public async Task PatchDependsOn_OnQueuedItem_AddDep_DoesNotKickQueueAndGateBlocksDispatch()
    {
        // Dep A is still Working; B's dependsOn gate stays unsatisfied. The
        // endpoint must NOT issue an EnqueueAsync kick (that would race a
        // dispatch tick where the gate predicate is the only guard).
        var depA = QueuedItem() with { State = WorkItemState.Working, Title = "A" };
        var itemB = QueuedItem() with { Title = "B" };
        await _factory.Store.CreateAsync(depA);
        await _factory.Store.CreateAsync(itemB);

        var queue = _factory.Services.GetRequiredService<ITaskQueue>();
        Assert.Equal(0, queue.Count);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{itemB.Id}",
            new { dependsOn = new[] { depA.Id.ToString() } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Unsatisfied dep → no kick.
        Assert.Equal(0, queue.Count);

        var storedB = await _factory.Store.GetAsync(itemB.Id);
        Assert.Single(storedB!.DependsOn);
        Assert.Equal(depA.Id, storedB.DependsOn[0]);

        // Gate evaluation: A is still Working, so B is NOT dispatch-eligible.
        var states = new Dictionary<CodeyBox.Core.WorkItemId, WorkItemState>
        {
            [depA.Id] = WorkItemState.Working,
        };
        Assert.False(WorkItemDependencies.AreSatisfied(storedB.DependsOn, states));

        // Flip A to Done — the gate opens and B becomes dispatch-eligible.
        states[depA.Id] = WorkItemState.Done;
        Assert.True(WorkItemDependencies.AreSatisfied(storedB.DependsOn, states));
    }

    [Fact]
    public async Task PatchDependsOn_DepAlreadyDone_KicksDispatcherQueue()
    {
        // PATCH lands a dep that's already Done — the gate is already
        // satisfied so the endpoint must enqueue a kick instead of leaving
        // the item to wait for the next scan tick.
        var depDone = QueuedItem() with { State = WorkItemState.Done, Title = "done-dep" };
        var itemB = QueuedItem() with { Title = "B" };
        await _factory.Store.CreateAsync(depDone);
        await _factory.Store.CreateAsync(itemB);

        var queue = _factory.Services.GetRequiredService<ITaskQueue>();
        Assert.Equal(0, queue.Count);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{itemB.Id}",
            new { dependsOn = new[] { depDone.Id.ToString() } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(1, queue.Count);
        var kicked = await queue.DequeueAsync();
        Assert.Equal(itemB.Id, kicked);
    }

    [Fact]
    public async Task PatchDependsOn_SelfDependency_Returns400()
    {
        var item = QueuedItem();
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new { dependsOn = new[] { item.Id.ToString() } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Lock the operator-visible error message: a refactor that swallows
        // the specific self-loop string into a generic 400 must be caught.
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("a work item cannot depend on itself", body);
    }

    [Fact]
    public async Task PatchDependsOn_TransitiveCycle_Returns400()
    {
        // Pre-existing graph: B → A (B depends on A). PATCH A to depend on B
        // would create the cycle A → B → A, which must be rejected.
        var itemA = QueuedItem() with { Title = "A" };
        var itemB = QueuedItem() with { Title = "B", DependsOn = new[] { itemA.Id } };
        await _factory.Store.CreateAsync(itemA);
        await _factory.Store.CreateAsync(itemB);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{itemA.Id}",
            new { dependsOn = new[] { itemB.Id.ToString() } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Lock the operator-visible message — the cycle-path projection is
        // the only signal an operator has to diagnose which edge to break.
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("circular dependency detected", body);

        // Confirm A's deps did NOT get written by the rejected request.
        var storedA = await _factory.Store.GetAsync(itemA.Id);
        Assert.Empty(storedA!.DependsOn);
    }

    [Fact]
    public async Task PatchDependsOn_NamespacedExternalId_ResolvesAndPersists()
    {
        // PATCH-side analogue of NamespacedExternalIdsTests: a 'ns:value'
        // entry must resolve via the same byNamespacedExternalId lookup that
        // CreateAsync uses. Without this test a typo in the PATCH branch
        // (swapped tuple order, missing project filter, wrong dictionary
        // key) goes unnoticed because every other test uses bare GUIDs.
        var dep = QueuedItem() with
        {
            Title = "dep",
            ExternalIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["github"] = "issue-42",
            },
        };
        var target = QueuedItem() with { Title = "target" };
        await _factory.Store.CreateAsync(dep);
        await _factory.Store.CreateAsync(target);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{target.Id}",
            new { dependsOn = new[] { "github:issue-42" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await _factory.Store.GetAsync(target.Id);
        Assert.Single(stored!.DependsOn);
        Assert.Equal(dep.Id, stored.DependsOn[0]);
    }

    [Fact]
    public async Task PatchDependsOn_BareUnambiguousExternalId_Resolves()
    {
        // Bare externalId path: the value appears under exactly one namespace
        // in the project, so it resolves without a 'ns:' qualifier.
        var dep = QueuedItem() with
        {
            Title = "dep",
            ExternalIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["linear"] = "LIN-7",
            },
        };
        var target = QueuedItem() with { Title = "target" };
        await _factory.Store.CreateAsync(dep);
        await _factory.Store.CreateAsync(target);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{target.Id}",
            new { dependsOn = new[] { "LIN-7" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await _factory.Store.GetAsync(target.Id);
        Assert.Single(stored!.DependsOn);
        Assert.Equal(dep.Id, stored.DependsOn[0]);
    }

    [Fact]
    public async Task PatchDependsOn_AmbiguousBareExternalId_Returns400()
    {
        // Same bare value lives under two different namespaces — the bare
        // form is ambiguous and must 400 with a 'qualify as namespace:value'
        // hint. Mirrors the create-handler contract.
        var depA = QueuedItem() with
        {
            Title = "depA",
            ExternalIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["github"] = "DUP",
            },
        };
        var depB = QueuedItem() with
        {
            Title = "depB",
            ExternalIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["linear"] = "DUP",
            },
        };
        var target = QueuedItem() with { Title = "target" };
        await _factory.Store.CreateAsync(depA);
        await _factory.Store.CreateAsync(depB);
        await _factory.Store.CreateAsync(target);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{target.Id}",
            new { dependsOn = new[] { "DUP" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ambiguous", body);

        var stored = await _factory.Store.GetAsync(target.Id);
        Assert.Empty(stored!.DependsOn);
    }

    [Fact]
    public async Task PatchDependsOn_NonExistentDepId_Returns400()
    {
        var item = QueuedItem();
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new { dependsOn = new[] { Guid.NewGuid().ToString() } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PatchDependsOn_OversizedArray_Returns400()
    {
        var item = QueuedItem();
        await _factory.Store.CreateAsync(item);

        var tooMany = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid().ToString()).ToArray();
        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new { dependsOn = tooMany });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(WorkItemState.Done)]
    [InlineData(WorkItemState.Failed)]
    [InlineData(WorkItemState.Cancelled)]
    [InlineData(WorkItemState.AuditFailed)]
    [InlineData(WorkItemState.MergeConflictResolutionFailed)]
    [InlineData(WorkItemState.AbandonedAfterRecoveryAttempts)]
    public async Task PatchDependsOn_OnTerminalItem_Returns409(WorkItemState terminal)
    {
        var dep = QueuedItem() with { Title = "dep" };
        var item = QueuedItem() with { State = terminal };
        await _factory.Store.CreateAsync(dep);
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new { dependsOn = new[] { dep.Id.ToString() } });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // Confirm the partial UPDATE did NOT touch the row.
        var fetched = await _factory.Store.GetAsync(item.Id);
        Assert.Empty(fetched!.DependsOn);
        Assert.Equal(terminal, fetched.State);
    }

    [Fact]
    public async Task PatchDependsOn_OnWorkingItem_Succeeds_StateUntouched()
    {
        // Non-terminal non-Queued items go through the partial UPDATE path,
        // which must not stomp state/started_at/etc.
        var dep = QueuedItem() with { Title = "dep" };
        var working = QueuedItem() with
        {
            Title = "working",
            State = WorkItemState.Working,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        };
        await _factory.Store.CreateAsync(dep);
        await _factory.Store.CreateAsync(working);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{working.Id}",
            new { dependsOn = new[] { dep.Id.ToString() } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var fetched = await _factory.Store.GetAsync(working.Id);
        Assert.Equal(WorkItemState.Working, fetched!.State);
        Assert.NotNull(fetched.StartedAt);
        Assert.Single(fetched.DependsOn);
        Assert.Equal(dep.Id, fetched.DependsOn[0]);
    }

    [Fact]
    public async Task PatchDependsOn_EmptyArray_ClearsDependencies_AndKicksQueue()
    {
        var depA = QueuedItem() with { State = WorkItemState.Working, Title = "A" };
        var itemB = QueuedItem() with
        {
            Title = "B",
            DependsOn = new[] { depA.Id },
        };
        await _factory.Store.CreateAsync(depA);
        await _factory.Store.CreateAsync(itemB);

        var queue = _factory.Services.GetRequiredService<ITaskQueue>();
        Assert.Equal(0, queue.Count);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{itemB.Id}",
            new { dependsOn = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var storedB = await _factory.Store.GetAsync(itemB.Id);
        Assert.Empty(storedB!.DependsOn);

        // Clearing deps on a Queued item leaves the gate satisfied — the
        // endpoint must enqueue a dispatch kick.
        Assert.Equal(1, queue.Count);
        var kicked = await queue.DequeueAsync();
        Assert.Equal(itemB.Id, kicked);
    }

    [Fact]
    public async Task PatchDependsOn_CombinedWithTitle_OnWorkingItem_Returns409()
    {
        // dependsOn is allowed on non-terminal; title is Queued-only. A
        // combined PATCH that hits both on a Working item must 409 BEFORE any
        // partial write lands — no half-applied state.
        var dep = QueuedItem() with { Title = "dep" };
        var working = QueuedItem() with
        {
            Title = "working",
            State = WorkItemState.Working,
        };
        await _factory.Store.CreateAsync(dep);
        await _factory.Store.CreateAsync(working);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{working.Id}",
            new { dependsOn = new[] { dep.Id.ToString() }, title = "new title" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // Neither the deps nor the title should have been written.
        var fetched = await _factory.Store.GetAsync(working.Id);
        Assert.Empty(fetched!.DependsOn);
        Assert.Equal("working", fetched.Title);
    }
}
