namespace CodeyBox.Core;

/// <summary>
/// Persistent record of in-iteration agent fallback events for a work item.
/// One row per recorded event: a successful Codex→Claude swap, or an
/// all-members-exhausted park (in which case <see cref="ToAgent"/> is null).
/// </summary>
public sealed record AgentFallbackRecord(
    Guid Id,
    WorkItemId WorkItemId,
    string Phase,
    int? Iteration,
    AgentKind FromAgent,
    string? FromModel,
    AgentKind? ToAgent,
    string? ToModel,
    string Reason,
    DateTimeOffset OccurredAt,
    string? FromInstanceId = null,
    string? ToInstanceId = null);

/// <summary>
/// Durable store of mid-iteration agent fallback events. Exposed on the
/// <c>/workitems/{id}</c> read model so operators can audit which agents
/// were tried and why fallback was needed.
/// </summary>
public interface IAgentFallbackHistoryStore
{
    Task RecordAsync(AgentFallbackRecord record, CancellationToken ct = default);
    Task<IReadOnlyList<AgentFallbackRecord>> ListByWorkItemAsync(WorkItemId workItemId, CancellationToken ct = default);
}
