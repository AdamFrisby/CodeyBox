namespace CodeyBox.Core;

/// <summary>
/// One entry in a work item's per-phase agent involvement trail. Appended on
/// every phase transition (Work / Audit / Rework / Merge) so operators can see
/// who-did-what at every stage instead of inferring from the single, mutable
/// <see cref="WorkItem.Agent"/> field — which only ever reflects the current
/// phase and is overwritten as the item moves through the pipeline.
///
/// <para>
/// Entries are an immutable audit trail: an entry's identity
/// (<see cref="AgentKind"/>, <see cref="ModelId"/>, <see cref="Phase"/>,
/// <see cref="Iteration"/>, <see cref="StartedAt"/>) is never rewritten once
/// recorded. The only mutation is a one-time completion stamp via
/// <see cref="IAgentInvolvementStore.FinalizeAsync"/>, which fills in
/// <see cref="EndedAt"/> and <see cref="Outcome"/> when the phase finishes —
/// these are null while the agent is still in progress.
/// </para>
/// </summary>
public sealed record AgentInvolvement(
    Guid Id,
    WorkItemId WorkItemId,
    AgentKind AgentKind,
    string? ModelId,
    string Phase,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int? Iteration,
    string? Outcome,
    string? AgentInstanceId = null);

/// <summary>
/// Durable, append-only store of per-phase agent involvement. Surfaced on the
/// <c>/workitems/{id}</c> read model (and the cheaper
/// <c>/workitems/{id}/agent-history</c> endpoint) so operators can attribute
/// quota burn to the correct phase and reconstruct which agent ran each
/// audit / rework iteration without grepping orchestrator logs.
/// </summary>
public interface IAgentInvolvementStore
{
    /// <summary>
    /// Appends a new in-progress entry (<see cref="AgentInvolvement.EndedAt"/>
    /// and <see cref="AgentInvolvement.Outcome"/> null). Call
    /// <see cref="FinalizeAsync"/> with the same <see cref="AgentInvolvement.Id"/>
    /// once the phase completes.
    /// </summary>
    Task RecordStartAsync(AgentInvolvement entry, CancellationToken ct = default);

    /// <summary>
    /// One-time completion stamp for the entry with <paramref name="id"/>: sets
    /// <see cref="AgentInvolvement.EndedAt"/> and
    /// <see cref="AgentInvolvement.Outcome"/>. A no-op if the entry is already
    /// finalized or does not exist — preserving the immutable-identity invariant.
    /// </summary>
    Task FinalizeAsync(Guid id, DateTimeOffset endedAt, string outcome, CancellationToken ct = default);

    /// <summary>Returns the full involvement trail for a work item, oldest first.</summary>
    Task<IReadOnlyList<AgentInvolvement>> ListByWorkItemAsync(WorkItemId workItemId, CancellationToken ct = default);
}
