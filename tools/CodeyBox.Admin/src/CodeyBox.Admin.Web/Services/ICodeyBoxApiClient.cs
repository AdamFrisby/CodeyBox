using CodeyBox.Admin.Web.Models;

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
