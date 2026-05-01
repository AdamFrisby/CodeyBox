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
        Assert.StartsWith("# From suggestion:", wi.Prompt);
        Assert.Contains(s.Title, wi.Prompt);
        Assert.Contains(s.Rationale, wi.Prompt);
        Assert.Equal(s.Title, wi.Title);
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

    private sealed record SuggestionShape(string State, string? PromotedToWorkItemId);
    private sealed record PromoteBody(string WorkItemId, SuggestionShape Suggestion);
}
