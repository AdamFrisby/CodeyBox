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
    Task<bool> RetryWorkItemAsync(string id, string? from = null, CancellationToken ct = default);
    Task<bool> ReorderWorkItemsAsync(IReadOnlyList<string> ids, CancellationToken ct = default);

    // ── Queue control ─────────────────────────────────────────────────────────
    Task<QueueStatusDto?> GetQueueStatusAsync(CancellationToken ct = default);
    Task<QueueStatusDto?> PauseQueueAsync(string reason, CancellationToken ct = default);
    Task<QueueStatusDto?> ResumeQueueAsync(CancellationToken ct = default);
    Task<List<AgentPauseStateDto>> GetPausedAgentsAsync(CancellationToken ct = default)
        => Task.FromResult(new List<AgentPauseStateDto>());
    Task<AgentPauseStateDto?> PauseAgentAsync(string agent, string reason, double? durationSeconds = null, CancellationToken ct = default)
        => Task.FromResult<AgentPauseStateDto?>(null);
    Task<bool> ResumeAgentAsync(string agent, CancellationToken ct = default)
        => Task.FromResult(false);

    // ── Budget usage ──────────────────────────────────────────────────────────
    Task<BudgetUsageDto?> GetBudgetUsageAsync(string projectId, CancellationToken ct = default);

    // ── Monthly cost budget ───────────────────────────────────────────────────
    Task<ProjectBudgetDto?> GetProjectBudgetAsync(string projectId, CancellationToken ct = default);
    Task<ProjectQueueStateDto?> PauseProjectQueueAsync(string projectId, string reason, CancellationToken ct = default);
    Task<ProjectQueueStateDto?> ResumeProjectQueueAsync(string projectId, CancellationToken ct = default);
    // ── Live stdout tail ──────────────────────────────────────────────────────
    Task<string?> GetStdoutTailAsync(string workItemId, CancellationToken ct = default);
    Task<AgentSupervisionSessionsResponse?> GetAgentSupervisionSessionsAsync(CancellationToken ct = default)
        => Task.FromResult<AgentSupervisionSessionsResponse?>(null);
    Task<AgentSupervisionInjectionReceiptDto?> InjectAgentSupervisionAsync(
        string sessionId,
        AgentSupervisionInjectionRequestDto request,
        CancellationToken ct = default)
        => Task.FromResult<AgentSupervisionInjectionReceiptDto?>(null);

    // ── Audit timeline ────────────────────────────────────────────────────────
    Task<WorkItemTimelineDto?> GetWorkItemTimelineAsync(
        string id, string? kind = null, string? since = null, int? iteration = null,
        CancellationToken ct = default);

    // ── Audit reports ─────────────────────────────────────────────────────────
    Task<AuditReportsDto?> GetAuditReportsAsync(string workItemId, CancellationToken ct = default);
    Task<string?> GetAuditReportRawOutputAsync(
        string workItemId,
        string target,
        int iteration,
        string auditorName,
        CancellationToken ct = default);

    // ── Timings ───────────────────────────────────────────────────────────────
    Task<WorkItemTimingsDto?> GetWorkItemTimingsAsync(string id, CancellationToken ct = default);
    Task<AggregateTimingsDto?> GetAggregateTimingsAsync(int? n = null, CancellationToken ct = default);
    Task<AgentStreamAggregateDto?> GetWorkItemAgentStreamAggregateAsync(string id, CancellationToken ct = default);
    Task<AgentStreamAggregateDto?> GetFleetAgentStreamAggregateAsync(int? n = null, CancellationToken ct = default)
        => Task.FromResult<AgentStreamAggregateDto?>(null);

    // ── Costs ─────────────────────────────────────────────────────────────────
    Task<WorkItemCostsDto?> GetWorkItemCostsAsync(string id, CancellationToken ct = default);
    Task<ProjectCostsDto?> GetProjectCostsAsync(string projectId, string? from = null, string? to = null, CancellationToken ct = default);

    // ── Statistics ────────────────────────────────────────────────────────────
    Task<QuotaReportDto?> GetQuotaAsync(CancellationToken ct = default)
        => Task.FromResult<QuotaReportDto?>(null);
    Task<WorkersStatusDto?> GetWorkersStatusAsync(CancellationToken ct = default)
        => Task.FromResult<WorkersStatusDto?>(null);
    Task<ConcurrencyDto?> GetConcurrencyAsync(CancellationToken ct = default)
        => Task.FromResult<ConcurrencyDto?>(null);
    Task<CapacityReportDto?> GetCapacityAsync(
        string? agent = null,
        string? window = null,
        string? model = null,
        int? hours = null,
        bool includeIntervals = true,
        CancellationToken ct = default)
        => Task.FromResult<CapacityReportDto?>(null);

    // ── Agent questions ───────────────────────────────────────────────────────
    Task<List<QuestionDto>> GetQuestionsAsync(string workItemId, CancellationToken ct = default);
    Task<bool> AnswerQuestionAsync(string workItemId, string questionId, string answer, CancellationToken ct = default);
    Task<bool> DismissQuestionAsync(string workItemId, string questionId, string reason, CancellationToken ct = default);
    // ── Fleet ─────────────────────────────────────────────────────────────────
    Task<List<FleetSummaryDto>> GetFleetSummaryAsync(CancellationToken ct = default);
    Task<bool> PauseProjectAsync(string projectId, string? reason = null, CancellationToken ct = default);
    Task<bool> ResumeProjectAsync(string projectId, CancellationToken ct = default);
    // ── Plugins ───────────────────────────────────────────────────────────────
    Task<List<PluginDto>> GetAuditorPluginsAsync(CancellationToken ct = default);
    // ── Replay ────────────────────────────────────────────────────────────────
    Task<WorkItemDto?> ReplayWorkItemAsync(string id, ReplayWorkItemRequest req, CancellationToken ct = default);
    Task<WorkItemReplaysDto?> GetReplaysAsync(string id, CancellationToken ct = default);
    // ── Diff ──────────────────────────────────────────────────────────────────
    /// <summary>
    /// Fetches the pending diff for a work item as JSON. Returns null when the
    /// work item has no diff yet (204 No Content) or does not exist (404).
    /// </summary>
    Task<WorkItemDiffDto?> GetWorkItemDiffAsync(string id, CancellationToken ct = default);

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
        string? externalId = null,
        CancellationToken ct = default);

    // ── Releases ──────────────────────────────────────────────────────────────
    Task<List<ReleaseDto>> GetReleasesAsync(string? projectId = null, string? state = null, int? limit = null, int? offset = null, CancellationToken ct = default);
    Task<int> GetOpenReleasesCountAsync(CancellationToken ct = default);
    Task<ReleaseDto?> GetReleaseAsync(string id, CancellationToken ct = default);
    Task<List<object>> GetReleaseWorkItemsAsync(string id, CancellationToken ct = default);
    Task<List<ReleaseAuditIterationDto>> GetReleaseAuditIterationsAsync(string id, CancellationToken ct = default);
    Task<ReleaseDto?> CreateReleaseAsync(CreateReleaseRequest req, CancellationToken ct = default);
    Task<ReleaseDto?> CloseReleaseAsync(string id, CancellationToken ct = default);
    Task<ReleaseDto?> ReopenReleaseAsync(string id, string reason, CancellationToken ct = default);
    Task<ReleaseDto?> AbandonReleaseAsync(string id, CancellationToken ct = default);
    Task<ReleaseDto?> TriggerReleaseAsync(string id, CancellationToken ct = default);
}

/// <summary>Request body for PATCH /workitems/{id}.</summary>
public sealed class PatchWorkItemRequest
{
    public string? Title { get; set; }
    public string? Prompt { get; set; }
    public string? Agent { get; set; }
    public int? WorkTimeoutMinutes { get; set; }
    public int? MergeTimeoutMinutes { get; set; }
    public int? MinModelScore { get; set; }
}

/// <summary>Request body for POST /workitems/reorder.</summary>
public sealed class ReorderRequest
{
    public IReadOnlyList<string> Ids { get; set; } = [];
}
