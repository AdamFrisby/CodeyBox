using System.Net;
using System.Net.Http.Json;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that POST /suggestions/{id}/promote creates a linked work item with the
/// suggestion's content prepended to its prompt, transitions the suggestion to 'accepted',
/// and links the new work item ID back onto the suggestion.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class PromoteCreatesLinkedWorkItemTests : IDisposable
{
    private readonly SuggestionsApiFactory _factory = new();
    private readonly HttpClient _client;

    public PromoteCreatesLinkedWorkItemTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private Suggestion MakeSuggestion() => new()
    {
        Id = Guid.NewGuid().ToString(),
        SourceWorkItemId = Guid.NewGuid().ToString(),
        ProjectId = SuggestionsApiFactory.ProjectId,
        Title = "Add idempotency tests",
        Rationale = "While editing the file I noticed the duplicate-id test does not cover index 0.",
        Category = "test-coverage",
        Severity = "minor",
        EstimatedEffort = "small",
        CreatedAt = DateTimeOffset.UtcNow,
        State = "open",
    };

    [Fact]
    public async Task Promote_WorkItemPromptPrependsTitle_AndRationale()
    {
        var s = MakeSuggestion();
        await _factory.SuggestionStore.CreateAsync(s);

        var resp = await _client.PostAsJsonAsync($"/suggestions/{s.Id}/promote", new { });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<PromoteBody>();

        var wi = await _factory.WorkItemStore.GetAsync(WorkItemId.Parse(body!.WorkItemId));
        Assert.NotNull(wi);
        Assert.Contains("<agent_advisory>", wi.Prompt);
        Assert.Contains("# From suggestion:", wi.Prompt);
        Assert.Contains(s.Title, wi.Prompt);
        Assert.Contains(s.Rationale, wi.Prompt);
        Assert.Equal(s.Title, wi.Title);
        // Structural order: heading must appear BEFORE the advisory block so it acts
        // as the operator-level task instruction (not buried inside the advisory fence).
        var headingIdx = wi.Prompt.IndexOf("# From suggestion:", StringComparison.Ordinal);
        var advisoryIdx = wi.Prompt.IndexOf("<agent_advisory>", StringComparison.Ordinal);
        Assert.True(headingIdx < advisoryIdx, "'# From suggestion:' must precede '<agent_advisory>'");
    }

    [Fact]
    public async Task Promote_SuggestionTransitionsToAccepted()
    {
        var s = MakeSuggestion();
        await _factory.SuggestionStore.CreateAsync(s);

        var resp = await _client.PostAsJsonAsync($"/suggestions/{s.Id}/promote", new { });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<PromoteBody>();
        Assert.NotNull(body);
        Assert.Equal("accepted", body.Suggestion.State);
    }

    [Fact]
    public async Task Promote_SuggestionLinkedToNewWorkItem()
    {
        var s = MakeSuggestion();
        await _factory.SuggestionStore.CreateAsync(s);

        var resp = await _client.PostAsJsonAsync($"/suggestions/{s.Id}/promote", new { });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<PromoteBody>();

        var got = await _factory.SuggestionStore.GetAsync(s.Id);
        Assert.Equal("accepted", got!.State);
        Assert.Equal(body!.WorkItemId, got.PromotedToWorkItemId);
        Assert.Equal(body.WorkItemId, body.Suggestion.PromotedToWorkItemId);
    }

    [Fact]
    public async Task Promote_ExtraInstructions_AppendedAfterAdvisoryBlock()
    {
        var s = MakeSuggestion();
        await _factory.SuggestionStore.CreateAsync(s);

        const string extra = "Please also update the changelog.";
        var resp = await _client.PostAsJsonAsync($"/suggestions/{s.Id}/promote",
            new { extraInstructions = extra });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<PromoteBody>();

        var wi = await _factory.WorkItemStore.GetAsync(WorkItemId.Parse(body!.WorkItemId));
        Assert.NotNull(wi);
        Assert.Contains(extra, wi.Prompt);
        // Extra instructions must follow the advisory block, not precede it.
        var advisoryCloseIdx = wi.Prompt.IndexOf("</agent_advisory>", StringComparison.Ordinal);
        var extraIdx = wi.Prompt.IndexOf(extra, StringComparison.Ordinal);
        Assert.True(advisoryCloseIdx < extraIdx, "extraInstructions must appear after </agent_advisory>");
    }

    [Fact]
    public async Task Promote_ExtraInstructions_TooLong_ReturnsBadRequest()
    {
        var s = MakeSuggestion();
        await _factory.SuggestionStore.CreateAsync(s);

        var tooLong = new string('x', 64 * 1024 + 1);
        var resp = await _client.PostAsJsonAsync($"/suggestions/{s.Id}/promote",
            new { extraInstructions = tooLong });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Promote_WithExternalId_StoresUnderLegacyNamespace()
    {
        // Operator-supplied externalId on promotion lands in the new work
        // item's ExternalIds dict under the reserved 'legacy' namespace —
        // both the singular projection and the dict round-trip.
        var s = MakeSuggestion();
        await _factory.SuggestionStore.CreateAsync(s);

        var resp = await _client.PostAsJsonAsync($"/suggestions/{s.Id}/promote",
            new { externalId = "PROMOTE-EXT-1" });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<PromoteBody>();

        var wi = await _factory.WorkItemStore.GetAsync(WorkItemId.Parse(body!.WorkItemId));
        Assert.NotNull(wi);
        Assert.Equal("PROMOTE-EXT-1", wi!.ExternalId);
        Assert.True(wi.ExternalIds.TryGetValue("legacy", out var v));
        Assert.Equal("PROMOTE-EXT-1", v);
    }

    [Fact]
    public async Task Promote_WithDuplicateExternalId_InLegacyNamespace_ReturnsBadRequest()
    {
        // Pre-existing item in the same project already owns the legacy
        // value 'DUP-EXT-1'. Promoting a suggestion with the same externalId
        // must hit the GetByNamespacedExternalIdAsync(pid, 'legacy', value)
        // duplicate check and 400 — not silently let the create proceed.
        await _factory.WorkItemStore.CreateAsync(new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId(SuggestionsApiFactory.ProjectId),
            Title = "occupies the legacy namespace",
            Prompt = "p",
            ExternalIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["legacy"] = "DUP-EXT-1",
            },
        });

        var s = MakeSuggestion();
        await _factory.SuggestionStore.CreateAsync(s);

        var resp = await _client.PostAsJsonAsync($"/suggestions/{s.Id}/promote",
            new { externalId = "DUP-EXT-1" });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);

        // The suggestion must remain promotable (TryAcceptAsync was never called).
        var afterAttempt = await _factory.SuggestionStore.GetAsync(s.Id);
        Assert.Equal("open", afterAttempt!.State);
        Assert.Null(afterAttempt.PromotedToWorkItemId);
    }

    [Fact]
    public async Task Promote_WithExternalId_DoesNotConflictWith_NonLegacyNamespace()
    {
        // The legacy-namespace duplicate check must scope to 'legacy' only —
        // an item already carrying the same value under a DIFFERENT namespace
        // (e.g. 'github') must not block a suggestion promote with the same
        // externalId. This pins the namespace constant: a regression that
        // dropped or replaced 'legacy' with bare/cross-namespace matching
        // would 400 here.
        await _factory.WorkItemStore.CreateAsync(new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId(SuggestionsApiFactory.ProjectId),
            Title = "uses github namespace, not legacy",
            Prompt = "p",
            ExternalIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["github"] = "PROMOTE-EXT-NS-1",
            },
        });

        var s = MakeSuggestion();
        await _factory.SuggestionStore.CreateAsync(s);

        var resp = await _client.PostAsJsonAsync($"/suggestions/{s.Id}/promote",
            new { externalId = "PROMOTE-EXT-NS-1" });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<PromoteBody>();
        var wi = await _factory.WorkItemStore.GetAsync(WorkItemId.Parse(body!.WorkItemId));
        Assert.Equal("PROMOTE-EXT-NS-1", wi!.ExternalIds["legacy"]);
    }

    private sealed record SuggestionShape(string State, string? PromotedToWorkItemId);
    private sealed record PromoteBody(string WorkItemId, SuggestionShape Suggestion);
}
