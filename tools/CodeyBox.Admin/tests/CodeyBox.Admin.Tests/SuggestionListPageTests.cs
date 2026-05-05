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

    [Fact]
    public void Suggestions_ChangeCategoryFilter_CallsApiWithCategoryArgument()
    {
        var client = new SuggestionCapturingClient([
            MakeSuggestion(category: "security"),
            MakeSuggestion(category: "docs"),
        ]);
        Services.AddSingleton<ICodeyBoxApiClient>(client);
        var cut = RenderComponent<SuggestionsPage>();

        var selects = cut.FindAll("select");
        selects[0].Change("security");  // first select is Category

        cut.WaitForAssertion(() => Assert.Equal("security", client.LastCategory));
    }

    [Fact]
    public void Suggestions_ChangeSeverityFilter_CallsApiWithSeverityArgument()
    {
        var client = new SuggestionCapturingClient([
            MakeSuggestion(severity: "important"),
            MakeSuggestion(severity: "minor"),
        ]);
        Services.AddSingleton<ICodeyBoxApiClient>(client);
        var cut = RenderComponent<SuggestionsPage>();

        var selects = cut.FindAll("select");
        selects[1].Change("important");  // second select is Severity

        cut.WaitForAssertion(() => Assert.Equal("important", client.LastSeverity));
    }

    [Fact]
    public async Task Suggestions_BulkDismiss_CallsDismissForEachSelected()
    {
        var s1 = MakeSuggestion(id: "id-bb01", title: "Alpha");
        var s2 = MakeSuggestion(id: "id-bb02", title: "Beta");
        var client = new SuggestionCapturingClient([s1, s2]);
        Services.AddSingleton<ICodeyBoxApiClient>(client);
        var cut = RenderComponent<SuggestionsPage>();

        // Use InvokeAsync to wrap Find+Change atomically on the renderer's sync context,
        // preventing re-renders from invalidating the event handler ID mid-operation.
        await cut.InvokeAsync(() => cut.Find("th input[type=checkbox]").Change(true));

        // Bulk-actions bar is now visible; dismiss all selected items.
        await cut.InvokeAsync(() => cut.Find(".bulk-actions .btn-danger").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("id-bb01", client.DismissedIds);
            Assert.Contains("id-bb02", client.DismissedIds);
        });
    }
}

/// <summary>
/// Fake client that records the arguments passed to suggestion API calls.
/// </summary>
internal sealed class SuggestionCapturingClient : ICodeyBoxApiClient
{
    private readonly List<SuggestionDto> _suggestions;
    public string? LastCategory { get; private set; }
    public string? LastSeverity { get; private set; }
    public List<string> DismissedIds { get; } = [];

    public SuggestionCapturingClient(List<SuggestionDto> suggestions) => _suggestions = suggestions;

    public Task<List<SuggestionDto>> GetSuggestionsAsync(
        string? projectId = null, string? category = null, string? severity = null,
        CancellationToken ct = default)
    {
        LastCategory = category;
        LastSeverity = severity;
        return Task.FromResult(_suggestions
            .Where(s => (category == null || s.Category == category)
                     && (severity == null || s.Severity == severity))
            .ToList());
    }

    public Task<int> GetSuggestionsCountAsync(CancellationToken ct = default)
        => Task.FromResult(_suggestions.Count);

    public Task<SuggestionDto?> GetSuggestionAsync(string id, CancellationToken ct = default)
        => Task.FromResult(_suggestions.FirstOrDefault(s => s.Id == id));

    public Task<SuggestionDto?> DismissSuggestionAsync(string id, string? reason = null,
        CancellationToken ct = default)
    {
        DismissedIds.Add(id);
        var s = _suggestions.FirstOrDefault(x => x.Id == id);
        _suggestions.RemoveAll(x => x.Id == id);
        return Task.FromResult<SuggestionDto?>(s);
    }

    public Task<string?> PromoteSuggestionAsync(
        string id, string? extraInstructions = null, string? agent = null,
        string? workBranch = null, string? baseBranch = null, bool? pushUpstream = null,
        string? agentClassId = null, string? externalId = null, CancellationToken ct = default)
        => Task.FromResult<string?>("fake-work-item-id");

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
    public Task<AuditReportsDto?> GetAuditReportsAsync(string workItemId, CancellationToken ct = default)
        => Task.FromResult<AuditReportsDto?>(null);
    public Task<string?> GetAuditReportRawOutputAsync(string workItemId, int iteration, string auditorName, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
    public Task<WorkItemTimingsDto?> GetWorkItemTimingsAsync(string id, CancellationToken ct = default)
        => Task.FromResult<WorkItemTimingsDto?>(null);
    public Task<AggregateTimingsDto?> GetAggregateTimingsAsync(int? n = null, CancellationToken ct = default)
        => Task.FromResult<AggregateTimingsDto?>(null);
    public Task<WorkItemCostsDto?> GetWorkItemCostsAsync(string id, CancellationToken ct = default)
        => Task.FromResult<WorkItemCostsDto?>(null);
    public Task<ProjectCostsDto?> GetProjectCostsAsync(string projectId, string? from = null, string? to = null, CancellationToken ct = default)
        => Task.FromResult<ProjectCostsDto?>(null);

    public Task<string?> GetStdoutTailAsync(string workItemId, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
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

    public Task<int> GetSuggestionsCountAsync(CancellationToken ct = default)
        => Task.FromResult(_suggestions.Count);

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

    public Task<string?> PromoteSuggestionAsync(
        string id, string? extraInstructions = null, string? agent = null,
        string? workBranch = null, string? baseBranch = null, bool? pushUpstream = null,
        string? agentClassId = null, string? externalId = null, CancellationToken ct = default)
        => Task.FromResult<string?>("fake-work-item-id");

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
    public Task<AuditReportsDto?> GetAuditReportsAsync(string workItemId, CancellationToken ct = default)
        => Task.FromResult<AuditReportsDto?>(null);
    public Task<string?> GetAuditReportRawOutputAsync(string workItemId, int iteration, string auditorName, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
    public Task<WorkItemTimingsDto?> GetWorkItemTimingsAsync(string id, CancellationToken ct = default)
        => Task.FromResult<WorkItemTimingsDto?>(null);
    public Task<AggregateTimingsDto?> GetAggregateTimingsAsync(int? n = null, CancellationToken ct = default)
        => Task.FromResult<AggregateTimingsDto?>(null);
    public Task<WorkItemCostsDto?> GetWorkItemCostsAsync(string id, CancellationToken ct = default)
        => Task.FromResult<WorkItemCostsDto?>(null);
    public Task<ProjectCostsDto?> GetProjectCostsAsync(string projectId, string? from = null, string? to = null, CancellationToken ct = default)
        => Task.FromResult<ProjectCostsDto?>(null);

    public Task<string?> GetStdoutTailAsync(string workItemId, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
