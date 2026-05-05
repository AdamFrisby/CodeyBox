using CodeyBox.Admin.Web.Models;
using System.Text.Json;

namespace CodeyBox.Admin.Web.Services;

/// <summary>
/// Abstraction over the CodeyBox orchestrator REST API.
/// Defined locally — no dependency on CodeyBox.Core.
/// </summary>
public interface ICodeyBoxApiClient
{
    Task<List<WorkItemDto>> GetWorkItemsAsync(CancellationToken ct = default);
    Task<WorkItemDto?> GetWorkItemAsync(string id, CancellationToken ct = default);
    Task<List<ProjectDto>> GetProjectsAsync(CancellationToken ct = default);
    Task<WorkItemDto?> CreateWorkItemAsync(CreateWorkItemRequest req, CancellationToken ct = default);
    Task<WorkItemDto?> PatchWorkItemAsync(string id, PatchWorkItemRequest req, CancellationToken ct = default);
    Task<bool> DeleteWorkItemAsync(string id, CancellationToken ct = default);
    Task<bool> RetryWorkItemAsync(string id, string from = "work", CancellationToken ct = default);
    Task<bool> ReorderWorkItemsAsync(IReadOnlyList<string> ids, CancellationToken ct = default);

    // ── Queue control ─────────────────────────────────────────────────────────
    Task<QueueStatusDto?> GetQueueStatusAsync(CancellationToken ct = default);
    Task<QueueStatusDto?> PauseQueueAsync(string reason, CancellationToken ct = default);
    Task<QueueStatusDto?> ResumeQueueAsync(CancellationToken ct = default);

    // ── Budget usage ──────────────────────────────────────────────────────────
    Task<BudgetUsageDto?> GetBudgetUsageAsync(string projectId, CancellationToken ct = default);

    // ── Monthly cost budget ───────────────────────────────────────────────────
    Task<ProjectBudgetDto?> GetProjectBudgetAsync(string projectId, CancellationToken ct = default);
    Task<ProjectQueueStateDto?> PauseProjectQueueAsync(string projectId, string reason, CancellationToken ct = default);
    Task<ProjectQueueStateDto?> ResumeProjectQueueAsync(string projectId, CancellationToken ct = default);

    // ── Audit timeline ────────────────────────────────────────────────────────
    Task<WorkItemTimelineDto?> GetWorkItemTimelineAsync(
        string id, string? kind = null, string? since = null, int? iteration = null,
        CancellationToken ct = default);

    // ── Audit reports ─────────────────────────────────────────────────────────
    Task<AuditReportsDto?> GetAuditReportsAsync(string workItemId, CancellationToken ct = default);
    Task<string?> GetAuditReportRawOutputAsync(string workItemId, int iteration, string auditorName, CancellationToken ct = default);

    // ── Timings ───────────────────────────────────────────────────────────────
    Task<WorkItemTimingsDto?> GetWorkItemTimingsAsync(string id, CancellationToken ct = default);
    Task<AggregateTimingsDto?> GetAggregateTimingsAsync(int? n = null, CancellationToken ct = default);

    // ── Costs ─────────────────────────────────────────────────────────────────
    Task<WorkItemCostsDto?> GetWorkItemCostsAsync(string id, CancellationToken ct = default);
    Task<ProjectCostsDto?> GetProjectCostsAsync(string projectId, string? from = null, string? to = null, CancellationToken ct = default);

    // ── Suggestions ───────────────────────────────────────────────────────────
    Task<List<SuggestionDto>> GetSuggestionsAsync(
        string? projectId = null, string? category = null, string? severity = null,
        CancellationToken ct = default);
    Task<int> GetSuggestionsCountAsync(CancellationToken ct = default);
    Task<SuggestionDto?> GetSuggestionAsync(string id, CancellationToken ct = default);
    Task<SuggestionDto?> DismissSuggestionAsync(string id, string? reason = null, CancellationToken ct = default);
    Task<string?> PromoteSuggestionAsync(
        string id,
        string? extraInstructions = null,
        string? agent = null,
        string? workBranch = null,
        string? baseBranch = null,
        bool? pushUpstream = null,
        string? agentClassId = null,
        CancellationToken ct = default);
}

/// <summary>Request body for PATCH /workitems/{id}.</summary>
public sealed class PatchWorkItemRequest
{
    public string? Title { get; set; }
    public string? Prompt { get; set; }
    public string? Agent { get; set; }
}

/// <summary>Request body for POST /workitems/reorder.</summary>
public sealed class ReorderRequest
{
    public IReadOnlyList<string> Ids { get; set; } = [];
}
