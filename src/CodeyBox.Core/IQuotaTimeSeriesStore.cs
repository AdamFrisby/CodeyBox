namespace CodeyBox.Core;

/// <summary>
/// Read-only access to the per-agent quota time-series captured by the
/// statistics plugin's quota sampler. Lives in <c>CodeyBox.Core</c> so the
/// API layer can expose a REST query endpoint that gracefully degrades to
/// 503 when no implementation is registered (i.e. the statistics plugin is
/// not loaded or disabled).
///
/// <para>Implementations MUST be thread-safe — the host may call
/// <see cref="QueryAsync"/> and <see cref="QueryRawAsync"/> concurrently
/// with sampler writes.</para>
/// </summary>
public interface IQuotaTimeSeriesStore
{
    /// <summary>
    /// Returns normalised quota-sample rows matching <paramref name="filter"/>,
    /// ordered by <c>sampled_at</c> ascending. Each call to the underlying
    /// quota probe produces one row per (overall + per-window + per-model
    /// permutation), so a typical filter for "the last 24 hours of
    /// <c>claude</c> overall availability" returns one row per sampler tick.
    /// </summary>
    Task<IReadOnlyList<QuotaSampleRow>> QueryAsync(
        QuotaTimeSeriesFilter filter,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the raw <see cref="AgentQuotaSnapshot"/> JSON for each probe
    /// invocation matching <paramref name="filter"/>, ordered by
    /// <c>sampled_at</c> ascending. Useful for back-fill or for fields the
    /// normalised schema does not yet expose.
    /// </summary>
    Task<IReadOnlyList<QuotaRawSnapshotRow>> QueryRawAsync(
        QuotaTimeSeriesFilter filter,
        CancellationToken ct = default);
}

/// <summary>
/// Filter accepted by <see cref="IQuotaTimeSeriesStore"/>. All fields are
/// optional; an empty filter returns the most recent rows up to
/// <see cref="Limit"/>.
/// </summary>
public sealed record QuotaTimeSeriesFilter
{
    /// <summary>Agent kind to filter on (case-insensitive). Null = all agents.</summary>
    public string? Agent { get; init; }

    /// <summary>
    /// Window name to filter on (e.g. <c>five_hour</c>, <c>seven_day</c>,
    /// <c>five-hour-rolling</c>). Special value <c>"overall"</c> matches rows
    /// whose <c>window_name</c> is null (the aggregated overall reading).
    /// Null = no window filter (returns both overall and per-window rows).
    /// </summary>
    public string? WindowName { get; init; }

    /// <summary>Model id to filter on (case-insensitive). Null = all models.</summary>
    public string? ModelId { get; init; }

    /// <summary>Lower bound on <c>sampled_at</c> (inclusive). Null = no lower bound.</summary>
    public DateTimeOffset? FromUtc { get; init; }

    /// <summary>Upper bound on <c>sampled_at</c> (exclusive). Null = no upper bound.</summary>
    public DateTimeOffset? ToUtc { get; init; }

    /// <summary>
    /// Maximum number of rows to return. Implementations clamp to a hard
    /// ceiling to avoid runaway responses; the default 1000 is enough for
    /// 10+ days of 15-minute samples for a single (agent, window) pair.
    /// </summary>
    public int Limit { get; init; } = 1000;
}

/// <summary>One normalised quota-sample row.</summary>
public sealed record QuotaSampleRow(
    DateTimeOffset SampledAt,
    string Agent,
    string? ModelId,
    double OverallPct,
    bool WouldAllow,
    string? Notes,
    string? WindowName,
    double? WindowPct,
    DateTimeOffset? WindowResetAt,
    bool IsKnown,
    string? UnknownReason);

/// <summary>
/// One raw quota-snapshot row, returned as JSON for fidelity with the
/// underlying <see cref="AgentQuotaSnapshot"/>. The JSON shape is the same
/// one <c>/quota</c> emits per probe (one snapshot per (agent, instance)).
/// </summary>
public sealed record QuotaRawSnapshotRow(
    DateTimeOffset SampledAt,
    string Agent,
    string? ModelId,
    string RawJson);
