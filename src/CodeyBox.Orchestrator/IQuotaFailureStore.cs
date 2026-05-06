using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

public enum QuotaFailureKind
{
    LimitReached,
    RateLimitExceeded,
    Unauthorized,
}

public sealed record QuotaFailureObservation(
    AgentKind Agent,
    string? ModelId,
    QuotaFailureKind FailureKind,
    DateTimeOffset ObservedAt,
    ProjectId? ProjectId = null);

public interface IQuotaFailureStore
{
    Task RecordAsync(AgentKind agent, string? modelId, QuotaFailureKind kind, DateTimeOffset observedAt, CancellationToken ct = default);

    Task RecordForProjectAsync(AgentKind agent, string? modelId, ProjectId projectId, QuotaFailureKind kind, DateTimeOffset observedAt, CancellationToken ct = default);

    Task<bool> HasRecentAsync(AgentKind agent, string? modelId, TimeSpan window, DateTimeOffset now, CancellationToken ct = default);

    Task<bool> HasRecentForProjectAsync(AgentKind agent, string? modelId, ProjectId projectId, TimeSpan window, DateTimeOffset now, CancellationToken ct = default);

    Task<IReadOnlyList<QuotaFailureObservation>> ListRecentAsync(TimeSpan window, DateTimeOffset now, CancellationToken ct = default);

    Task PruneOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
