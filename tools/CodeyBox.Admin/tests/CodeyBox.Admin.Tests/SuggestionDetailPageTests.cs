using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using SuggestionDetailPage = CodeyBox.Admin.Web.Components.Pages.SuggestionDetail;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Tests for the SuggestionDetail page: loading, 404 handling, promote, and dismiss flows.
/// </summary>
public sealed class SuggestionDetailPageTests : TestContext
{
    private static SuggestionDto MakeSuggestion(string id = "aaaa-0001", string state = "open") => new()
    {
        Id = id,
        Title = "Add tests for edge case",
        Category = "test-coverage",
        Severity = "minor",
        EstimatedEffort = "small",
        SourceWorkItemId = "bbbb-0001",
        ProjectId = "proj",
        Rationale = "This is why we need it",
        CreatedAt = DateTimeOffset.UtcNow,
        State = state,
    };

    [Fact]
    public void SuggestionDetail_LoadedSuccessfully_ShowsTitle()
    {
        var s = MakeSuggestion();
        Services.AddSingleton<ICodeyBoxApiClient>(new DetailFakeClient(s));
        var cut = RenderComponent<SuggestionDetailPage>(p => p.Add(x => x.Id, s.Id));
        Assert.Contains("Add tests for edge case", cut.Markup);
    }

    [Fact]
    public void SuggestionDetail_LoadedSuccessfully_ShowsRationale()
    {
        var s = MakeSuggestion();
        Services.AddSingleton<ICodeyBoxApiClient>(new DetailFakeClient(s));
        var cut = RenderComponent<SuggestionDetailPage>(p => p.Add(x => x.Id, s.Id));
        Assert.Contains("This is why we need it", cut.Markup);
    }

    [Fact]
    public void SuggestionDetail_NotFound_ShowsErrorBanner()
    {
        Services.AddSingleton<ICodeyBoxApiClient>(new DetailFakeClient(null));
        var cut = RenderComponent<SuggestionDetailPage>(p => p.Add(x => x.Id, "no-such-id"));
        Assert.Contains("not found", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SuggestionDetail_OpenSuggestion_ShowsPromoteButton()
    {
        var s = MakeSuggestion();
        Services.AddSingleton<ICodeyBoxApiClient>(new DetailFakeClient(s));
        var cut = RenderComponent<SuggestionDetailPage>(p => p.Add(x => x.Id, s.Id));
        Assert.Contains("Promote", cut.Markup);
    }

    [Fact]
    public void SuggestionDetail_OpenSuggestion_ShowsDismissButton()
    {
        var s = MakeSuggestion();
        Services.AddSingleton<ICodeyBoxApiClient>(new DetailFakeClient(s));
        var cut = RenderComponent<SuggestionDetailPage>(p => p.Add(x => x.Id, s.Id));
        Assert.Contains("Dismiss", cut.Markup);
    }

    [Fact]
    public async Task SuggestionDetail_PromoteButton_NavigatesToNewWorkItemForm()
    {
        var s = MakeSuggestion();
        Services.AddSingleton<ICodeyBoxApiClient>(new DetailFakeClient(s));
        var nav = Services.GetRequiredService<NavigationManager>();
        var cut = RenderComponent<SuggestionDetailPage>(p => p.Add(x => x.Id, s.Id));

        await cut.InvokeAsync(() => cut.Find("button.btn-primary").Click());

        Assert.Contains("/work-items/new", nav.Uri);
        Assert.Contains("fromSuggestion", nav.Uri);
        Assert.Contains(s.Id, nav.Uri);
    }

    [Fact]
    public async Task SuggestionDetail_DismissButton_OpensDismissModal()
    {
        var s = MakeSuggestion();
        Services.AddSingleton<ICodeyBoxApiClient>(new DetailFakeClient(s));
        var cut = RenderComponent<SuggestionDetailPage>(p => p.Add(x => x.Id, s.Id));

        await cut.InvokeAsync(() => cut.Find("button.btn-danger").Click());

        Assert.Contains("modal-overlay", cut.Markup);
    }

    [Fact]
    public async Task SuggestionDetail_ConfirmDismiss_CallsDismissApiAndHidesModal()
    {
        var s = MakeSuggestion();
        var client = new DetailCapturingClient(s);
        Services.AddSingleton<ICodeyBoxApiClient>(client);
        var cut = RenderComponent<SuggestionDetailPage>(p => p.Add(x => x.Id, s.Id));

        // Open the dismiss modal by clicking the main Dismiss button
        await cut.InvokeAsync(() => cut.Find("button.btn-danger").Click());

        // Confirm dismiss using the modal-actions button
        await cut.InvokeAsync(() => cut.Find(".modal-actions button.btn-danger").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.True(client.DismissCalled);
            Assert.DoesNotContain("modal-overlay", cut.Markup);
        });
    }
}

/// <summary>
/// Minimal fake client for SuggestionDetail read-only tests.
/// </summary>
internal sealed class DetailFakeClient : ICodeyBoxApiClient
{
    private readonly SuggestionDto? _suggestion;

    public DetailFakeClient(SuggestionDto? suggestion) => _suggestion = suggestion;

    public Task<SuggestionDto?> GetSuggestionAsync(string id, CancellationToken ct = default)
        => Task.FromResult(_suggestion);

    public Task<string?> PromoteSuggestionAsync(
        string id, string? extraInstructions = null, string? agent = null,
        string? workBranch = null, string? baseBranch = null, bool? pushUpstream = null,
        string? agentClassId = null, string? externalId = null, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task<SuggestionDto?> DismissSuggestionAsync(string id, string? reason = null, CancellationToken ct = default)
    {
        if (_suggestion is null) return Task.FromResult<SuggestionDto?>(null);
        _suggestion.State = "dismissed";
        return Task.FromResult<SuggestionDto?>(_suggestion);
    }

    public Task<int> GetSuggestionsCountAsync(CancellationToken ct = default) => Task.FromResult(0);
    public Task<List<SuggestionDto>> GetSuggestionsAsync(string? projectId = null, string? category = null, string? severity = null, CancellationToken ct = default) => Task.FromResult(new List<SuggestionDto>());
    public Task<List<WorkItemDto>> GetWorkItemsAsync(CancellationToken ct = default) => Task.FromResult(new List<WorkItemDto>());
    public Task<WorkItemDto?> GetWorkItemAsync(string id, CancellationToken ct = default) => Task.FromResult<WorkItemDto?>(null);
    public Task<List<ProjectDto>> GetProjectsAsync(CancellationToken ct = default) => Task.FromResult(new List<ProjectDto>());
    public Task<WorkItemDto?> CreateWorkItemAsync(CreateWorkItemRequest req, CancellationToken ct = default) => Task.FromResult<WorkItemDto?>(null);
    public Task<WorkItemDto?> PatchWorkItemAsync(string id, PatchWorkItemRequest req, CancellationToken ct = default) => Task.FromResult<WorkItemDto?>(null);
    public Task<bool> DeleteWorkItemAsync(string id, CancellationToken ct = default) => Task.FromResult(false);
    public Task<bool> RetryWorkItemAsync(string id, string from = "work", CancellationToken ct = default) => Task.FromResult(false);
    public Task<bool> ReorderWorkItemsAsync(IReadOnlyList<string> ids, CancellationToken ct = default) => Task.FromResult(false);
    public Task<QueueStatusDto?> GetQueueStatusAsync(CancellationToken ct = default) => Task.FromResult<QueueStatusDto?>(null);
    public Task<QueueStatusDto?> PauseQueueAsync(string reason, CancellationToken ct = default) => Task.FromResult<QueueStatusDto?>(null);
    public Task<QueueStatusDto?> ResumeQueueAsync(CancellationToken ct = default) => Task.FromResult<QueueStatusDto?>(null);
    public Task<BudgetUsageDto?> GetBudgetUsageAsync(string projectId, CancellationToken ct = default) => Task.FromResult<BudgetUsageDto?>(null);
    public Task<WorkItemTimelineDto?> GetWorkItemTimelineAsync(string id, string? kind = null, string? since = null, int? iteration = null, CancellationToken ct = default) => Task.FromResult<WorkItemTimelineDto?>(null);
    public Task<AuditReportsDto?> GetAuditReportsAsync(string workItemId, CancellationToken ct = default) => Task.FromResult<AuditReportsDto?>(null);
    public Task<string?> GetAuditReportRawOutputAsync(string workItemId, int iteration, string auditorName, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<WorkItemTimingsDto?> GetWorkItemTimingsAsync(string id, CancellationToken ct = default) => Task.FromResult<WorkItemTimingsDto?>(null);
    public Task<AggregateTimingsDto?> GetAggregateTimingsAsync(int? n = null, CancellationToken ct = default) => Task.FromResult<AggregateTimingsDto?>(null);
    public Task<WorkItemCostsDto?> GetWorkItemCostsAsync(string id, CancellationToken ct = default) => Task.FromResult<WorkItemCostsDto?>(null);
    public Task<ProjectCostsDto?> GetProjectCostsAsync(string projectId, string? from = null, string? to = null, CancellationToken ct = default) => Task.FromResult<ProjectCostsDto?>(null);
}

/// <summary>
/// Capturing fake client that records whether promote and dismiss were called.
/// </summary>
internal sealed class DetailCapturingClient : ICodeyBoxApiClient
{
    private readonly SuggestionDto _suggestion;
    public bool PromoteCalled { get; private set; }
    public bool DismissCalled { get; private set; }

    public DetailCapturingClient(SuggestionDto suggestion) => _suggestion = suggestion;

    public Task<SuggestionDto?> GetSuggestionAsync(string id, CancellationToken ct = default)
        => Task.FromResult<SuggestionDto?>(_suggestion);

    public Task<string?> PromoteSuggestionAsync(
        string id, string? extraInstructions = null, string? agent = null,
        string? workBranch = null, string? baseBranch = null, bool? pushUpstream = null,
        string? agentClassId = null, string? externalId = null, CancellationToken ct = default)
    {
        PromoteCalled = true;
        return Task.FromResult<string?>("fake-work-item-id");
    }

    public Task<SuggestionDto?> DismissSuggestionAsync(string id, string? reason = null, CancellationToken ct = default)
    {
        DismissCalled = true;
        _suggestion.State = "dismissed";
        return Task.FromResult<SuggestionDto?>(_suggestion);
    }

    public Task<int> GetSuggestionsCountAsync(CancellationToken ct = default) => Task.FromResult(0);
    public Task<List<SuggestionDto>> GetSuggestionsAsync(string? projectId = null, string? category = null, string? severity = null, CancellationToken ct = default) => Task.FromResult(new List<SuggestionDto>());
    public Task<List<WorkItemDto>> GetWorkItemsAsync(CancellationToken ct = default) => Task.FromResult(new List<WorkItemDto>());
    public Task<WorkItemDto?> GetWorkItemAsync(string id, CancellationToken ct = default) => Task.FromResult<WorkItemDto?>(null);
    public Task<List<ProjectDto>> GetProjectsAsync(CancellationToken ct = default) => Task.FromResult(new List<ProjectDto>());
    public Task<WorkItemDto?> CreateWorkItemAsync(CreateWorkItemRequest req, CancellationToken ct = default) => Task.FromResult<WorkItemDto?>(null);
    public Task<WorkItemDto?> PatchWorkItemAsync(string id, PatchWorkItemRequest req, CancellationToken ct = default) => Task.FromResult<WorkItemDto?>(null);
    public Task<bool> DeleteWorkItemAsync(string id, CancellationToken ct = default) => Task.FromResult(false);
    public Task<bool> RetryWorkItemAsync(string id, string from = "work", CancellationToken ct = default) => Task.FromResult(false);
    public Task<bool> ReorderWorkItemsAsync(IReadOnlyList<string> ids, CancellationToken ct = default) => Task.FromResult(false);
    public Task<QueueStatusDto?> GetQueueStatusAsync(CancellationToken ct = default) => Task.FromResult<QueueStatusDto?>(null);
    public Task<QueueStatusDto?> PauseQueueAsync(string reason, CancellationToken ct = default) => Task.FromResult<QueueStatusDto?>(null);
    public Task<QueueStatusDto?> ResumeQueueAsync(CancellationToken ct = default) => Task.FromResult<QueueStatusDto?>(null);
    public Task<BudgetUsageDto?> GetBudgetUsageAsync(string projectId, CancellationToken ct = default) => Task.FromResult<BudgetUsageDto?>(null);
    public Task<WorkItemTimelineDto?> GetWorkItemTimelineAsync(string id, string? kind = null, string? since = null, int? iteration = null, CancellationToken ct = default) => Task.FromResult<WorkItemTimelineDto?>(null);
    public Task<AuditReportsDto?> GetAuditReportsAsync(string workItemId, CancellationToken ct = default) => Task.FromResult<AuditReportsDto?>(null);
    public Task<string?> GetAuditReportRawOutputAsync(string workItemId, int iteration, string auditorName, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<WorkItemTimingsDto?> GetWorkItemTimingsAsync(string id, CancellationToken ct = default) => Task.FromResult<WorkItemTimingsDto?>(null);
    public Task<AggregateTimingsDto?> GetAggregateTimingsAsync(int? n = null, CancellationToken ct = default) => Task.FromResult<AggregateTimingsDto?>(null);
    public Task<WorkItemCostsDto?> GetWorkItemCostsAsync(string id, CancellationToken ct = default) => Task.FromResult<WorkItemCostsDto?>(null);
    public Task<ProjectCostsDto?> GetProjectCostsAsync(string projectId, string? from = null, string? to = null, CancellationToken ct = default) => Task.FromResult<ProjectCostsDto?>(null);
}
