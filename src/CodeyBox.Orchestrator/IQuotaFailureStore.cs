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
    DateTimeOffset ObservedAt);

public interface IQuotaFailureStore
{
    Task RecordAsync(AgentKind agent, string? modelId, QuotaFailureKind kind, DateTimeOffset observedAt, CancellationToken ct = default);

    Task<bool> HasRecentAsync(AgentKind agent, string? modelId, TimeSpan window, DateTimeOffset now, CancellationToken ct = default);

    Task<IReadOnlyList<QuotaFailureObservation>> ListRecentAsync(TimeSpan window, DateTimeOffset now, CancellationToken ct = default);

    Task PruneOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
