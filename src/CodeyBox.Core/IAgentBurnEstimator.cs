namespace CodeyBox.Core;

/// <summary>
/// Provides a rolling-average estimate of how much of an agent's primary quota
/// window a single work item burns. Combined with the router's
/// <see cref="AgentQuotaSnapshot.AvailablePct"/> reading, the router converts
/// this into an "EstimatedConcurrentBurnsFitInWindow" gate: when the number of
/// items already running on the agent meets or exceeds the fit estimate, the
/// router refuses to dispatch another concurrent burn even though the raw
/// availability is still above its configured minimum-quota floor.
///
/// <para>
/// Implementations MUST be thread-safe (the router probes from concurrent
/// pickups). Implementations SHOULD cache aggressively — every dispatch tick
/// invokes this for every eligible member.
/// </para>
/// </summary>
public interface IAgentBurnEstimator
{
    /// <summary>
    /// Returns the rolling-average per-item burn estimate for
    /// <paramref name="agent"/>. The router never blocks on this call:
    /// implementations MUST surface "no data yet" as
    /// <see cref="AgentBurnEstimate.SampleCount"/> = 0 rather than throwing.
    /// </summary>
    Task<AgentBurnEstimate> GetEstimateAsync(AgentKind agent, CancellationToken ct = default);
}

/// <summary>
/// Rolling-average burn estimate for one agent kind, sourced from recent
/// <see cref="WorkItemCost"/> rows. The router multiplies these numbers with
/// the live <see cref="AgentQuotaSnapshot.AvailablePct"/> reading to decide
/// whether another concurrent burn will fit.
/// </summary>
public sealed record AgentBurnEstimate
{
    /// <summary>
    /// Historical avg fraction of the agent's primary window that a single
    /// item is expected to consume, as a percentage 0-100. Negative means
    /// unknown; use <see cref="Status"/> and <see cref="SampleCount"/> to
    /// distinguish no-history from samples that cannot be converted because no
    /// positive window budget is configured.
    /// </summary>
    public double AvgBurnPctPerItem { get; init; }

    /// <summary>
    /// Number of past Done items found by the estimator. For
    /// <see cref="AgentBurnEstimateStatus.Measured"/>, these samples contributed
    /// to <see cref="AvgBurnPctPerItem"/>. For
    /// <see cref="AgentBurnEstimateStatus.NoWindowBudget"/>, these samples exist
    /// but cannot be converted into a percentage. Zero means the router should
    /// fall back to the conservative default fit (spec: "fits 2 concurrent
    /// burns") so the queue does not stall on cold start.
    /// </summary>
    public int SampleCount { get; init; }

    /// <summary>
    /// Explains how this estimate was produced so callers can distinguish true
    /// cold start from "we found history but cannot divide it by a window
    /// budget". Older implementations may leave this as
    /// <see cref="AgentBurnEstimateStatus.Unknown"/>; callers should then fall
    /// back to <see cref="SampleCount"/> and <see cref="AvgBurnPctPerItem"/>.
    /// </summary>
    public AgentBurnEstimateStatus Status { get; init; }

    /// <summary>
    /// True when this estimate is usable as measured history for
    /// work-denominated quota math: a positive finite burn backed by at least
    /// <paramref name="minSamples"/> samples (and at least one sample no
    /// matter how low the threshold is set). Guards the quota reservation and
    /// absolute-floor paths so unmeasured agents keep today's configured
    /// constants instead of deriving from noise. Pure.
    /// </summary>
    public bool HasMeasuredBurn(int minSamples) =>
        SampleCount >= Math.Max(1, minSamples)
        && double.IsFinite(AvgBurnPctPerItem)
        && AvgBurnPctPerItem > 0;

    /// <summary>
    /// Converts a work-denominated quantity (<paramref name="items"/> dispatches)
    /// into quota-percentage points using the measured per-item burn, e.g. one
    /// item at 0.6% burn reserves 0.6 points. Returns null when the quantity is
    /// not a positive finite count or no measured burn is available (see
    /// <see cref="HasMeasuredBurn"/>), so callers fall through to the
    /// percentage-denominated tier instead of escrowing a guess. Pure.
    /// </summary>
    public double? ToItemsPct(double? items, int minSamples)
    {
        if (items is not { } count || !double.IsFinite(count) || count <= 0)
            return null;
        if (!HasMeasuredBurn(minSamples))
            return null;
        var pct = count * AvgBurnPctPerItem;
        return double.IsFinite(pct) && pct > 0 ? pct : null;
    }
}

/// <summary>Source/status for an <see cref="AgentBurnEstimate"/>.</summary>
public enum AgentBurnEstimateStatus
{
    /// <summary>Status was not provided by the estimator implementation.</summary>
    Unknown = 0,

    /// <summary>Historical samples and a positive window budget produced the burn percentage.</summary>
    Measured = 1,

    /// <summary>No recent historical cost samples were available.</summary>
    NoHistory = 2,

    /// <summary>Historical samples exist, but no positive window budget is configured.</summary>
    NoWindowBudget = 3,

    /// <summary>The estimator's historical sample source could not provide samples.</summary>
    SampleSourceUnavailable = 4,
}
