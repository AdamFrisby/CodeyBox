namespace CodeyBox.Core;

/// <summary>
/// One durable accounting row per completed agent invocation, written from the
/// same site that records <see cref="WorkItemCost"/>. Unlike
/// <see cref="IWorkItemCostStore"/> (whose rows cascade-delete with the work
/// item), usage events live in an independent table so multi-window budget
/// accounting is not corrupted when a work item is deleted. Cost is stored in
/// <b>microcents</b> (1 cent = 10000 microcents, 1 USD = 1_000_000 microcents)
/// to keep an integer column without losing per-call precision.
/// </summary>
public sealed record AgentUsageEvent
{
    public required string Id { get; init; }
    public required DateTimeOffset TimeUtc { get; init; }
    public required string AgentKind { get; init; }
    public string? AgentInstanceId { get; init; }
    public string? ModelId { get; init; }
    public string? Phase { get; init; }
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? EndedUtc { get; init; }
    public long ElapsedMs { get; init; }
    public required int InputTokens { get; init; }
    public int CachedInputTokens { get; init; }
    public required int OutputTokens { get; init; }

    /// <summary>Equivalent pay-per-API cost of this invocation in microcents (USD × 1_000_000).</summary>
    public required long CostMicroCents { get; init; }

    /// <summary>The work item this invocation belonged to. Kept for traceability; not a FK.</summary>
    public string? WorkItemId { get; init; }

    /// <summary>Converts an equivalent-USD figure to the microcents unit used by this table.</summary>
    public static long UsdToMicroCents(decimal usd) =>
        (long)decimal.Round(usd * 1_000_000m, 0, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Aggregate of usage events over a time window for one (agent, model) pair.
/// </summary>
public readonly record struct AgentUsageWindowAggregate(
    long SumMicroCents,
    DateTimeOffset? EarliestUtc,
    int Count);

/// <summary>
/// Durable per-agent/per-model usage accounting. Writes are best-effort: callers
/// must never let a store failure abort a pipeline phase.
/// </summary>
public interface IAgentUsageStore
{
    /// <summary>Persists a single usage event.</summary>
    Task RecordAsync(AgentUsageEvent usage, CancellationToken ct = default);

    /// <summary>
    /// Sums <see cref="AgentUsageEvent.CostMicroCents"/> for events with the given
    /// <paramref name="agentKind"/> and <paramref name="modelId"/> whose
    /// <see cref="AgentUsageEvent.TimeUtc"/> falls within
    /// [<paramref name="fromUtc"/>, <paramref name="toUtc"/>). Also returns the
    /// earliest event time in the window (for rolling-window reset hints) and the
    /// row count. Returns all-zero/null when no rows match.
    /// </summary>
    Task<AgentUsageWindowAggregate> SumWindowAsync(
        string agentKind, string? modelId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default);

    /// <summary>Deletes events older than <paramref name="cutoffUtc"/>. Returns the number deleted.</summary>
    Task<int> PruneAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default);
}
