using Bunit;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using SuggestionsPage = CodeyBox.Admin.Web.Components.Pages.Suggestions;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Renders the Suggestions list component with a fake API client and asserts
/// that the table and filters reflect the returned suggestions.
/// </summary>
public sealed class SuggestionListPageTests : TestContext
{
    private static SuggestionDto MakeSuggestion(
        string id = "aaaaaaaa-0000-0000-0000-000000000001",
        string title = "Add tests",
        string category = "test-coverage",
        string severity = "minor",
        string sourceWorkItemId = "bbbbbbbb-0000-0000-0000-000000000001") => new()
    {
        Id = id,
        Title = title,
        Category = category,
        Severity = severity,
        EstimatedEffort = "small",
        SourceWorkItemId = sourceWorkItemId,
        ProjectId = "proj",
        Rationale = "Some rationale",
        CreatedAt = DateTimeOffset.UtcNow,
        State = "open",
    };

    [Fact]
    public void Suggestions_EmptyList_ShowsNoOpenMessage()
    {
        Services.AddSingleton<ICodeyBoxApiClient>(new SuggestionFakeClient([]));
        var cut = RenderComponent<SuggestionsPage>();
        Assert.Contains("No open suggestions", cut.Markup);
    }

    [Fact]
    public void Suggestions_EmptyList_DoesNotRenderTable()
    {
        Services.AddSingleton<ICodeyBoxApiClient>(new SuggestionFakeClient([]));
        var cut = RenderComponent<SuggestionsPage>();
        Assert.DoesNotContain("queue-table", cut.Markup);
    }

    [Fact]
    public void Suggestions_WithItems_RendersTable()
    {
        Services.AddSingleton<ICodeyBoxApiClient>(new SuggestionFakeClient([MakeSuggestion()]));
        var cut = RenderComponent<SuggestionsPage>();
        Assert.Contains("queue-table", cut.Markup);
    }

    [Fact]
    public void Suggestions_TitleAppearsInRow()
    {
        Services.AddSingleton<ICodeyBoxApiClient>(new SuggestionFakeClient([MakeSuggestion(title: "Fix the bug")]));
        var cut = RenderComponent<SuggestionsPage>();
        Assert.Contains("Fix the bug", cut.Markup);
    }

    [Fact]
    public void Suggestions_TitleLinkPointsToDetailPage()
    {
        var id = "aaaaaaaa-0000-0000-0000-000000000001";
        Services.AddSingleton<ICodeyBoxApiClient>(new SuggestionFakeClient([MakeSuggestion(id: id, title: "Fix bug")]));
        var cut = RenderComponent<SuggestionsPage>();
        Assert.Contains($"/suggestions/{id}", cut.Markup);
    }

    [Fact]
    public void Suggestions_ShowsCategoryBadge()
    {
        Services.AddSingleton<ICodeyBoxApiClient>(new SuggestionFakeClient([MakeSuggestion(category: "security")]));
        var cut = RenderComponent<SuggestionsPage>();
        Assert.Contains("category-badge", cut.Markup);
        Assert.Contains("security", cut.Markup);
    }

    [Fact]
    public void Suggestions_ShowsSeverityCssClass()
    {
        Services.AddSingleton<ICodeyBoxApiClient>(new SuggestionFakeClient([MakeSuggestion(severity: "important")]));
        var cut = RenderComponent<SuggestionsPage>();
        Assert.Contains("severity-important", cut.Markup);
    }

    [Fact]
    public void Suggestions_MinorSeverity_ShowsMinorCss()
    {
        Services.AddSingleton<ICodeyBoxApiClient>(new SuggestionFakeClient([MakeSuggestion(severity: "minor")]));
        var cut = RenderComponent<SuggestionsPage>();
        Assert.Contains("severity-minor", cut.Markup);
    }

    [Fact]
    public void Suggestions_MultipleItems_AllRendered()
    {
        var items = new List<SuggestionDto>
        {
            MakeSuggestion(id: "id-0001", title: "Alpha"),
            MakeSuggestion(id: "id-0002", title: "Beta"),
            MakeSuggestion(id: "id-0003", title: "Gamma"),
        };
        Services.AddSingleton<ICodeyBoxApiClient>(new SuggestionFakeClient(items));
        var cut = RenderComponent<SuggestionsPage>();
        Assert.Contains("Alpha", cut.Markup);
        Assert.Contains("Beta", cut.Markup);
        Assert.Contains("Gamma", cut.Markup);
    }

    [Fact]
    public void Suggestions_ShowsDismissButton()
    {
        Services.AddSingleton<ICodeyBoxApiClient>(new SuggestionFakeClient([MakeSuggestion()]));
        var cut = RenderComponent<SuggestionsPage>();
        Assert.Contains("dismiss", cut.Markup);
    }

    [Fact]
    public void Suggestions_ShowsSourceWorkItemLink()
    {
        var wiId = "bbbbbbbb-0000-0000-0000-000000000001";
        Services.AddSingleton<ICodeyBoxApiClient>(new SuggestionFakeClient([MakeSuggestion(sourceWorkItemId: wiId)]));
        var cut = RenderComponent<SuggestionsPage>();
        // Short work item ID (first 8 chars) appears as link text
        Assert.Contains("bbbbbbbb", cut.Markup);
    }

    [Fact]
    public void Suggestions_PageTitle_ContainsSuggestions()
    {
        Services.AddSingleton<ICodeyBoxApiClient>(new SuggestionFakeClient([]));
        var cut = RenderComponent<SuggestionsPage>();
        Assert.Contains("Suggestions", cut.Markup);
    }
}

/// <summary>
/// Minimal <see cref="ICodeyBoxApiClient"/> fake for suggestion list tests.
/// All methods not related to suggestions return safe stubs.
/// </summary>
internal sealed class SuggestionFakeClient : ICodeyBoxApiClient
{
    private readonly List<SuggestionDto> _suggestions;

    public SuggestionFakeClient(List<SuggestionDto> suggestions) => _suggestions = suggestions;

    public Task<List<SuggestionDto>> GetSuggestionsAsync(
        string? projectId = null, string? category = null, string? severity = null,
        CancellationToken ct = default)
        => Task.FromResult(_suggestions);

    public Task<SuggestionDto?> GetSuggestionAsync(string id, CancellationToken ct = default)
        => Task.FromResult(_suggestions.FirstOrDefault(s => s.Id == id));

    public Task<SuggestionDto?> DismissSuggestionAsync(string id, string? reason = null,
        CancellationToken ct = default)
    {
        var s = _suggestions.FirstOrDefault(x => x.Id == id);
        if (s is not null) s.State = "dismissed";
        _suggestions.RemoveAll(x => x.Id == id);
        return Task.FromResult<SuggestionDto?>(s);
    }

    public Task<bool> PromoteSuggestionAsync(string id, CancellationToken ct = default)
        => Task.FromResult(true);

    // ── Stubs for remaining interface members ─────────────────────────────────
    public Task<List<WorkItemDto>> GetWorkItemsAsync(CancellationToken ct = default)
        => Task.FromResult(new List<WorkItemDto>());
    public Task<WorkItemDto?> GetWorkItemAsync(string id, CancellationToken ct = default)
        => Task.FromResult<WorkItemDto?>(null);
    public Task<List<ProjectDto>> GetProjectsAsync(CancellationToken ct = default)
        => Task.FromResult(new List<ProjectDto>());
    public Task<WorkItemDto?> CreateWorkItemAsync(CreateWorkItemRequest req, CancellationToken ct = default)
        => Task.FromResult<WorkItemDto?>(null);
    public Task<WorkItemDto?> PatchWorkItemAsync(string id, PatchWorkItemRequest req, CancellationToken ct = default)
        => Task.FromResult<WorkItemDto?>(null);
    public Task<bool> DeleteWorkItemAsync(string id, CancellationToken ct = default)
        => Task.FromResult(false);
    public Task<bool> RetryWorkItemAsync(string id, string from = "work", CancellationToken ct = default)
        => Task.FromResult(false);
    public Task<bool> ReorderWorkItemsAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
        => Task.FromResult(false);
    public Task<QueueStatusDto?> GetQueueStatusAsync(CancellationToken ct = default)
        => Task.FromResult<QueueStatusDto?>(null);
    public Task<QueueStatusDto?> PauseQueueAsync(string reason, CancellationToken ct = default)
        => Task.FromResult<QueueStatusDto?>(null);
    public Task<QueueStatusDto?> ResumeQueueAsync(CancellationToken ct = default)
        => Task.FromResult<QueueStatusDto?>(null);
    public Task<BudgetUsageDto?> GetBudgetUsageAsync(string projectId, CancellationToken ct = default)
        => Task.FromResult<BudgetUsageDto?>(null);
    public Task<WorkItemTimelineDto?> GetWorkItemTimelineAsync(
        string id, string? kind = null, string? since = null, int? iteration = null,
        CancellationToken ct = default)
        => Task.FromResult<WorkItemTimelineDto?>(null);
}
