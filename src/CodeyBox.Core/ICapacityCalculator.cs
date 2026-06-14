using System.Text.Json.Serialization;

namespace CodeyBox.Core;

/// <summary>
/// Joins the quota-snapshot time-series (per-agent / per-window %) against
/// recorded token consumption (<c>agent_usage_events</c>) to estimate how
/// many tokens / events a subscription window holds.
///
/// <para>For each pair of consecutive quota samples we read the percent drop
/// and sum the usage events whose timestamp falls in the interval — that
/// gives a tokens-per-percent reading. Aggregated across many intervals it
/// converges on a stable estimate of full-window capacity.</para>
///
/// <para>Lives in <c>CodeyBox.Core</c> so the API layer can resolve it from
/// DI and gracefully degrade to 503 when no implementation is registered
/// (i.e. the statistics plugin is not loaded). Implementations MUST be
/// thread-safe — callers may invoke <see cref="ComputeAsync"/> concurrently
/// with sampler writes against the underlying time-series.</para>
/// </summary>
public interface ICapacityCalculator
{
    /// <summary>
    /// Computes capacity estimates over the supplied time range.
    /// <para>An empty <paramref name="filter"/> covers the default capacity
    /// horizon (last 7 days). Implementations clamp absurd ranges to a hard
    /// ceiling.</para>
    /// </summary>
    Task<CapacityReport> ComputeAsync(CapacityFilter filter, CancellationToken ct = default);
}

/// <summary>
/// Filter accepted by <see cref="ICapacityCalculator.ComputeAsync"/>. All
/// fields are optional; an empty filter returns the most recent
/// <see cref="DefaultHorizonHours"/> hours of data.
/// </summary>
public sealed record CapacityFilter
{
    /// <summary>Default horizon in hours when no <c>FromUtc</c> is supplied.</summary>
    public const int DefaultHorizonHours = 168; // 7 days

    /// <summary>Agent kind to filter on (case-insensitive). Null = every agent that has data.</summary>
    public string? Agent { get; init; }

    /// <summary>Window name to filter on (e.g. <c>five_hour</c>, <c>seven_day</c>). Null = every per-window series found.</summary>
    public string? WindowName { get; init; }

    /// <summary>Model id to filter on (case-insensitive). Null = aggregate across all models for the agent.</summary>
    public string? ModelId { get; init; }

    /// <summary>Lower bound on <c>sampled_at</c> (inclusive). Null = <c>now - DefaultHorizonHours</c>.</summary>
    public DateTimeOffset? FromUtc { get; init; }

    /// <summary>Upper bound on <c>sampled_at</c> (exclusive). Null = <c>now</c>.</summary>
    public DateTimeOffset? ToUtc { get; init; }

    /// <summary>
    /// Minimum quota-percent drop between consecutive samples for an interval
    /// to count toward the burn-rate average. Filters out the per-sample
    /// noise floor of quota probes (typical probe granularity is ~0.1%–1%).
    /// </summary>
    public double MinDeltaPct { get; init; } = 0.25;

    /// <summary>
    /// When true, populate <see cref="CapacityEntry.Intervals"/> on each entry
    /// with the per-interval burn-rate series. Default true — the dashboard
    /// renders the series and the chart is the value of this endpoint.
    /// </summary>
    public bool IncludeIntervals { get; init; } = true;
}

/// <summary>Top-level capacity report — one entry per (agent, window) pair.</summary>
public sealed record CapacityReport(
    DateTimeOffset GeneratedAt,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    IReadOnlyList<CapacityEntry> Entries);

/// <summary>
/// Capacity estimate for one (agent, window) pair, computed by differencing
/// consecutive quota samples and summing token usage in each interval.
/// </summary>
public sealed record CapacityEntry
{
    public required string Agent { get; init; }

    /// <summary>
    /// Provider's window name. <see cref="IQuotaTimeSeriesStore"/> uses the
    /// sentinel <c>"overall"</c> for the aggregated overall reading (no
    /// specific window). The <see cref="CapacityEntry"/> normalizes that to
    /// <c>"overall"</c> rather than null so JSON consumers don't have to
    /// branch on null.
    /// </summary>
    public required string WindowName { get; init; }

    /// <summary>Optional model id this entry was scoped to. Null when the entry aggregates across every model.</summary>
    public string? ModelId { get; init; }

    /// <summary>Number of differencing intervals that survived <see cref="CapacityFilter.MinDeltaPct"/> filtering.</summary>
    public required int SampleIntervals { get; init; }

    /// <summary>Total quota-percent burned across all counted intervals.</summary>
    public required double TotalDeltaPct { get; init; }

    /// <summary>Sum of billable input tokens (uncached) consumed across the same intervals.</summary>
    public required long TotalInputTokens { get; init; }

    /// <summary>Sum of cache-read input tokens consumed across the same intervals — billed at a different rate, surfaced separately.</summary>
    public required long TotalCachedInputTokens { get; init; }

    /// <summary>Sum of output tokens consumed across the same intervals.</summary>
    public required long TotalOutputTokens { get; init; }

    /// <summary>Sum of recorded event count across the same intervals.</summary>
    public required long TotalRequests { get; init; }

    /// <summary>Sum of recorded cost in microcents across the same intervals (mostly informational for subscription members).</summary>
    public required long TotalCostMicroCents { get; init; }

    /// <summary>Weighted average input tokens (billable) per 1% of window drained. Null when no intervals survived filtering.</summary>
    public double? InputTokensPerPercent { get; init; }

    /// <summary>Weighted average cache-read input tokens per 1% of window drained.</summary>
    public double? CachedInputTokensPerPercent { get; init; }

    /// <summary>Weighted average output tokens per 1% of window drained.</summary>
    public double? OutputTokensPerPercent { get; init; }

    /// <summary>Weighted average request (event) count per 1% of window drained.</summary>
    public double? RequestsPerPercent { get; init; }

    /// <summary>Implied full-window capacity (100 × <see cref="InputTokensPerPercent"/>).</summary>
    public double? EstimatedFullWindowInputTokens { get; init; }

    /// <summary>Implied full-window capacity for cache-read input tokens.</summary>
    public double? EstimatedFullWindowCachedInputTokens { get; init; }

    /// <summary>Implied full-window capacity for output tokens.</summary>
    public double? EstimatedFullWindowOutputTokens { get; init; }

    /// <summary>Implied full-window capacity for request count (100 × <see cref="RequestsPerPercent"/>).</summary>
    public double? EstimatedFullWindowRequests { get; init; }

    /// <summary>Most recent observed percent remaining for this (agent, window). Null when no samples exist.</summary>
    public double? CurrentPct { get; init; }

    /// <summary>Most recently observed window reset time (passes through whatever the probe surfaced).</summary>
    public DateTimeOffset? ResetAt { get; init; }

    /// <summary>
    /// Projected time at which the window hits 0%, extrapolated from the
    /// most-recent-interval input-token burn rate and <see cref="CurrentPct"/>.
    /// Null when the most recent burn rate is non-positive (idle / window
    /// resetting) or current pct is missing.
    /// </summary>
    public DateTimeOffset? EstimatedExhaustionAt { get; init; }

    /// <summary>
    /// Confidence band carried alongside the estimate so the dashboard can
    /// render "low confidence" guarded by sample count and the caveats the
    /// brief calls out (cached vs billable, rolling windows).
    /// </summary>
    public required CapacityConfidence Confidence { get; init; }

    /// <summary>Free-form caveat notes — e.g. "rolling window: pct never resets, burn rate is amortised".</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>Per-interval burn-rate series. Empty when <see cref="CapacityFilter.IncludeIntervals"/> is false.</summary>
    public IReadOnlyList<CapacityInterval> Intervals { get; init; } = Array.Empty<CapacityInterval>();
}

/// <summary>One differencing interval between consecutive quota samples.</summary>
public sealed record CapacityInterval(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    double DeltaPct,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long Requests,
    long CostMicroCents,
    bool IsWindowReset);

/// <summary>Confidence band on a capacity estimate.
/// Serialized as a string so the wire contract is human-readable and stable
/// independent of the API's JSON-converter list — admin DTOs and operator
/// scripts can switch on "Low" / "Medium" / "High" / "None" directly.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CapacityConfidence
{
    /// <summary>Fewer than 3 counted intervals — the estimate is a hint, not a number.</summary>
    Low,
    /// <summary>3–9 counted intervals — directionally correct, treat the precise number with care.</summary>
    Medium,
    /// <summary>10+ counted intervals across multiple window-burn cycles.</summary>
    High,
    /// <summary>No usable intervals after filtering. Estimates are null.</summary>
    None,
}
