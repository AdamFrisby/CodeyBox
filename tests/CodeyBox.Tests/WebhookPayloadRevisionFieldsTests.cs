using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Acceptance test: terminal-state webhook payloads must surface
/// <c>promptRevision</c>, <c>revisionAtCompletion</c>, and <c>revisionMatches</c>
/// as TOP-LEVEL fields (not nested under <c>details</c>), matching the JT-2
/// contract. Locks in both the wire shape and the null-handling for events
/// that don't carry revision attribution.
/// </summary>
public sealed class WebhookPayloadRevisionFieldsTests
{
    private static WorkItem MakeItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "test",
        Prompt = "do the thing",
        PromptRevision = 3,
    };

    private static Project MakeProject() => new()
    {
        Id = new ProjectId("proj"),
        DisplayName = "Test Project",
        RepositoryUrl = "https://example.com/repo.git",
    };

    [Fact]
    public void TerminalEvent_HasTopLevelRevisionFields_RevisionMatches()
    {
        var evt = new WebhookEvent
        {
            Event = "work_item.done",
            WorkItem = MakeItem(),
            Project = MakeProject(),
            PromptRevision = 3,
            RevisionAtCompletion = 3,
            RevisionMatches = true,
        };
        var json = HttpWebhookDispatcher.BuildPayload(evt);
        using var doc = JsonDocument.Parse(json);

        // The three fields MUST be readable as payload.<field>, not
        // payload.details.<field> — that is the JT-2 wire contract.
        Assert.Equal(3, doc.RootElement.GetProperty("promptRevision").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("revisionAtCompletion").GetInt32());
        Assert.True(doc.RootElement.GetProperty("revisionMatches").GetBoolean());
    }

    [Fact]
    public void TerminalEvent_RevisionMismatch_IsSurfaced()
    {
        // The whole point of the feature: a stale-prompt completion must be
        // visible to the tracker so it can offer a one-click re-run.
        var evt = new WebhookEvent
        {
            Event = "work_item.done",
            WorkItem = MakeItem(),
            Project = MakeProject(),
            PromptRevision = 3,
            RevisionAtCompletion = 2,
            RevisionMatches = false,
        };
        var json = HttpWebhookDispatcher.BuildPayload(evt);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(3, doc.RootElement.GetProperty("promptRevision").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("revisionAtCompletion").GetInt32());
        Assert.False(doc.RootElement.GetProperty("revisionMatches").GetBoolean());
    }

    [Fact]
    public void TerminalEvent_NoIterationsDispatched_OmitsRevisionAtCompletionAndMatches()
    {
        // E.g. an item that failed before any dispatch row was written. The
        // payload's JsonIgnoreCondition.WhenWritingNull strips the per-iteration
        // attribution fields; promptRevision (a non-null current value) still
        // serialises. Trackers must treat missing-or-null as "no attribution
        // available" — never as "matches=false".
        var evt = new WebhookEvent
        {
            Event = "work_item.failed",
            WorkItem = MakeItem(),
            Project = MakeProject(),
            PromptRevision = 1,
            RevisionAtCompletion = null,
            RevisionMatches = null,
        };
        var json = HttpWebhookDispatcher.BuildPayload(evt);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(1, doc.RootElement.GetProperty("promptRevision").GetInt32());

        // WhenWritingNull omits the keys; if a future serializer change brings
        // them back as explicit nulls, that's still acceptable wire output.
        if (doc.RootElement.TryGetProperty("revisionAtCompletion", out var rac))
            Assert.Equal(JsonValueKind.Null, rac.ValueKind);
        if (doc.RootElement.TryGetProperty("revisionMatches", out var rm))
            Assert.Equal(JsonValueKind.Null, rm.ValueKind);
    }

    [Fact]
    public void NonTerminalEvent_OmitsRevisionFieldsOrSerialisesAsNull()
    {
        // A non-terminal event (the orchestrator only stamps the fields on
        // terminal transitions) must still serialise without crashing and the
        // fields must be absent or null — never carry a stale value.
        // The non-conditional assert on `event` ensures the payload is still
        // intact: a refactor that dropped the entire payload shape would now
        // fail loudly instead of passing all three conditional null checks
        // vacuously.
        var evt = new WebhookEvent
        {
            Event = "work_item.audit_iteration",
            WorkItem = MakeItem(),
            Project = MakeProject(),
        };
        var json = HttpWebhookDispatcher.BuildPayload(evt);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("work_item.audit_iteration", doc.RootElement.GetProperty("event").GetString());

        if (doc.RootElement.TryGetProperty("promptRevision", out var pr))
            Assert.Equal(JsonValueKind.Null, pr.ValueKind);
        if (doc.RootElement.TryGetProperty("revisionAtCompletion", out var rac))
            Assert.Equal(JsonValueKind.Null, rac.ValueKind);
        if (doc.RootElement.TryGetProperty("revisionMatches", out var rm))
            Assert.Equal(JsonValueKind.Null, rm.ValueKind);
    }

    [Fact]
    public void RevisionFields_AreNotNestedUnderDetails()
    {
        // Explicit pin: if a future refactor reverts to carrying the fields on
        // a TerminalRevisionDetails object inside Details, this test fails so
        // the JT-2 contract regression is caught at CI rather than at the
        // tracker.
        var evt = new WebhookEvent
        {
            Event = "work_item.done",
            WorkItem = MakeItem(),
            Project = MakeProject(),
            PromptRevision = 2,
            RevisionAtCompletion = 2,
            RevisionMatches = true,
        };
        var json = HttpWebhookDispatcher.BuildPayload(evt);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("details", out var details)
            && details.ValueKind == JsonValueKind.Object)
        {
            Assert.False(details.TryGetProperty("promptRevision", out _));
            Assert.False(details.TryGetProperty("revisionAtCompletion", out _));
            Assert.False(details.TryGetProperty("revisionMatches", out _));
        }
    }
}
