using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Forwards every <see cref="IWorkItemStore"/> member to a real
/// <see cref="SqliteWorkItemStore"/> so a test can override just the member
/// under study. Members that carry a default interface implementation —
/// notably
/// <see cref="IWorkItemStore.ListDispatchEligibleIncludingDueQuotaRetryByPriorityAsync"/>
/// — are deliberately NOT declared here: on a subclass they fall through to
/// the interface default, which is what parity tests between the LINQ
/// ordering and the SQLite query override rely on.
/// </summary>
internal abstract class ForwardingWorkItemStore(SqliteWorkItemStore inner) : IWorkItemStore
{
    protected SqliteWorkItemStore Inner { get; } = inner;

    public virtual Task CreateAsync(WorkItem item, CancellationToken ct = default) => Inner.CreateAsync(item, ct);
    // Forward the batch commit AND its atomicity flag together — a decorator
    // that reported the inner's atomicity while running the sequential
    // interface default would silently break the chain-creation contract.
    public virtual Task CreateAllAsync(IReadOnlyList<WorkItem> items, CancellationToken ct = default) =>
        Inner.CreateAllAsync(items, ct);
    public virtual bool CreateAllIsAtomic => Inner.CreateAllIsAtomic;
    public virtual Task UpdateAsync(WorkItem item, CancellationToken ct = default) => Inner.UpdateAsync(item, ct);
    public virtual Task<bool> TryUpdateIfStateAsync(WorkItem item, WorkItemState onlyIfState, CancellationToken ct = default) =>
        Inner.TryUpdateIfStateAsync(item, onlyIfState, ct);
    public virtual Task<bool> TryUpdateIfStateAndUpdatedAtAsync(WorkItem item, WorkItemState onlyIfState, DateTimeOffset onlyIfUpdatedAt, CancellationToken ct = default) =>
        Inner.TryUpdateIfStateAndUpdatedAtAsync(item, onlyIfState, onlyIfUpdatedAt, ct);
    public virtual Task<PriorityUpdateResult> UpdatePriorityAsync(WorkItemId id, int priority, DateTimeOffset updatedAt, CancellationToken ct = default) =>
        Inner.UpdatePriorityAsync(id, priority, updatedAt, ct);
    public virtual Task<PriorityUpdateResult> UpdatePriorityIfStateAsync(WorkItemId id, int priority, DateTimeOffset updatedAt, WorkItemState onlyIfState, CancellationToken ct = default) =>
        Inner.UpdatePriorityIfStateAsync(id, priority, updatedAt, onlyIfState, ct);
    public virtual Task<DependsOnUpdateResult> UpdateDependsOnAsync(WorkItemId id, IReadOnlyList<WorkItemId> dependsOn, DateTimeOffset updatedAt, CancellationToken ct = default) =>
        Inner.UpdateDependsOnAsync(id, dependsOn, updatedAt, ct);
    public virtual Task<AuditBudgetUpdateResult> UpdateAuditBudgetAsync(WorkItemId id, int? auditMaxIterations, string? auditComplexity, DateTimeOffset updatedAt, CancellationToken ct = default) =>
        Inner.UpdateAuditBudgetAsync(id, auditMaxIterations, auditComplexity, updatedAt, ct);
    public virtual Task<bool> TryReplaceKnobsIfStateAndUpdatedAtAsync(WorkItemId id, IReadOnlyDictionary<string, string> knobs, DateTimeOffset updatedAt, WorkItemState onlyIfState, DateTimeOffset onlyIfUpdatedAt, CancellationToken ct = default) =>
        Inner.TryReplaceKnobsIfStateAndUpdatedAtAsync(id, knobs, updatedAt, onlyIfState, onlyIfUpdatedAt, ct);
    public virtual Task<bool> TryUpdateQueuedFieldsAndKnobsIfStateAndUpdatedAtAsync(WorkItem item, WorkItemState onlyIfState, DateTimeOffset onlyIfUpdatedAt, CancellationToken ct = default) =>
        Inner.TryUpdateQueuedFieldsAndKnobsIfStateAndUpdatedAtAsync(item, onlyIfState, onlyIfUpdatedAt, ct);
    public virtual Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default) => Inner.GetAsync(id, ct);
    public virtual IAsyncEnumerable<WorkItem> ListAsync(CancellationToken ct = default) => Inner.ListAsync(ct);
    public virtual IAsyncEnumerable<WorkItem> ListByStateAsync(WorkItemState state, CancellationToken ct = default) => Inner.ListByStateAsync(state, ct);
    public virtual Task<int> CountByStateAsync(WorkItemState state, CancellationToken ct = default) => Inner.CountByStateAsync(state, ct);
    public virtual Task ReorderAsync(IReadOnlyList<WorkItemId> orderedIds, CancellationToken ct = default) => Inner.ReorderAsync(orderedIds, ct);
    public virtual IAsyncEnumerable<WorkItem> ListDispatchEligibleByPriorityAsync(IReadOnlySet<WorkItemId> skipIds, DispatchCandidateOrdering ordering, CancellationToken ct = default) =>
        Inner.ListDispatchEligibleByPriorityAsync(skipIds, ordering, ct);
    public virtual Task<int> CountStartedInWindowAsync(ProjectId projectId, DateTimeOffset since, CancellationToken ct = default) =>
        Inner.CountStartedInWindowAsync(projectId, since, ct);
    public virtual Task<int> CountInFlightAsync(ProjectId projectId, CancellationToken ct = default) => Inner.CountInFlightAsync(projectId, ct);
    public virtual Task<(int Refactor, int Other)> CountInFlightSplitByRefactorAsync(ProjectId projectId, CancellationToken ct = default, WorkItemId? excludeId = null) =>
        Inner.CountInFlightSplitByRefactorAsync(projectId, ct, excludeId);
    public virtual Task<WorkItem?> GetByExternalIdAsync(ProjectId projectId, string externalId, CancellationToken ct = default) =>
        Inner.GetByExternalIdAsync(projectId, externalId, ct);
    public virtual Task<WorkItem?> GetByNamespacedExternalIdAsync(ProjectId projectId, string @namespace, string externalId, CancellationToken ct = default) =>
        Inner.GetByNamespacedExternalIdAsync(projectId, @namespace, externalId, ct);
    public virtual Task<WorkItem?> ReplaceExternalIdsAsync(WorkItemId id, IReadOnlyDictionary<string, string> externalIds, DateTimeOffset updatedAt, CancellationToken ct = default) =>
        Inner.ReplaceExternalIdsAsync(id, externalIds, updatedAt, ct);
    public virtual Task<IReadOnlyList<(string ProjectId, int State, int Count, string MaxUpdatedAt)>> GetFleetStateCountsAsync(CancellationToken ct = default) =>
        Inner.GetFleetStateCountsAsync(ct);
    public virtual Task<IReadOnlyList<(string ProjectId, int State)>> GetFleetRecentOutcomesAsync(int perProject = 5, CancellationToken ct = default) =>
        Inner.GetFleetRecentOutcomesAsync(perProject, ct);
    public virtual Task<IReadOnlyDictionary<string, bool>> GetFleetPauseStatesAsync(CancellationToken ct = default) =>
        Inner.GetFleetPauseStatesAsync(ct);
    public virtual IAsyncEnumerable<WorkItem> ListByReplaySourceAsync(WorkItemId sourceId, CancellationToken ct = default) =>
        Inner.ListByReplaySourceAsync(sourceId, ct);
    public virtual IAsyncEnumerable<WorkItem> ListSuspendedAsync(CancellationToken ct = default) => Inner.ListSuspendedAsync(ct);
    public virtual Task<IReadOnlySet<string>> GetActiveBaselineImageRefsAsync(CancellationToken ct = default) =>
        Inner.GetActiveBaselineImageRefsAsync(ct);
    public virtual Task<IReadOnlyList<(WorkItemId Id, string Title, WorkItemState State)>> ListWorkItemsForBaselineAsync(string baselineImageRef, CancellationToken ct = default) =>
        Inner.ListWorkItemsForBaselineAsync(baselineImageRef, ct);
    public virtual Task OrphanReplaysAsync(WorkItemId sourceId, CancellationToken ct = default) => Inner.OrphanReplaysAsync(sourceId, ct);
    public virtual IAsyncEnumerable<WorkItem> ListByReleaseAsync(ReleaseId releaseId, CancellationToken ct = default) => Inner.ListByReleaseAsync(releaseId, ct);
    public virtual Task<PromptReplaceResult> TryReplacePromptAsync(WorkItemId id, string newPrompt, DateTimeOffset updatedAt, CancellationToken ct = default) =>
        Inner.TryReplacePromptAsync(id, newPrompt, updatedAt, ct);
    public virtual Task RecordIterationDispatchAsync(WorkItemId workItemId, int iteration, int promptRevisionAtDispatch, DateTimeOffset dispatchedAt, CancellationToken ct = default) =>
        Inner.RecordIterationDispatchAsync(workItemId, iteration, promptRevisionAtDispatch, dispatchedAt, ct);
    public virtual Task<IReadOnlyList<WorkItemIteration>> GetIterationsAsync(WorkItemId workItemId, CancellationToken ct = default) =>
        Inner.GetIterationsAsync(workItemId, ct);
}
