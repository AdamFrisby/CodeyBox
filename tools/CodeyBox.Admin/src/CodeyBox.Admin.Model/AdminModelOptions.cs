namespace CodeyBox.Admin.Model;

/// <summary>
/// Hot-reloadable knobs for the projection layer. Operational values live
/// here — never as literals in the decision logic — so a future admin
/// options screen can bind them directly.
/// </summary>
public sealed record AdminModelOptions
{
    /// <summary>Minimum history samples before a vital may leave Neutral.</summary>
    public int MinimumHistorySamples { get; init; } = 5;

    /// <summary>History longer than this is truncated (most recent kept).</summary>
    public int MaxHistorySamples { get; init; } = 1008;

    /// <summary>
    /// QueueDepth reads Bad when current exceeds this multiple of the history
    /// median (fleet's own past, never an absolute target).
    /// </summary>
    public double QueueDepthBadMultiple { get; init; } = 2.0;

    /// <summary>QueueDepth reads Good when current is below this fraction of the median.</summary>
    public double QueueDepthGoodFraction { get; init; } = 0.5;

    /// <summary>
    /// InFlight reads Bad when nothing runs while items queue and the fleet
    /// usually runs at least this multiple of the current count.
    /// </summary>
    public double InFlightStallMultiple { get; init; } = 2.0;

    /// <summary>
    /// Trailing window over which the failure rate is computed from item states.
    /// </summary>
    public TimeSpan FailureWindow { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Trailing window over which throughput (completions/hour) is computed.</summary>
    public TimeSpan ThroughputWindow { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// A Queued item whose agent reports at or below this remaining-quota
    /// percent counts as blocked by agent availability.
    /// </summary>
    public int QuotaExhaustedAtOrBelowPct { get; init; } = 5;

    /// <summary>Hard ceiling for a healthy running item's attention score.</summary>
    public double RunningAttentionCap { get; init; } = 10.0;

    /// <summary>Hard ceiling for a slot-waiting item's attention score.</summary>
    public double WaitingAttentionCap { get; init; } = 30.0;

    /// <summary>Hard ceiling for a dependency-blocked item's attention score.</summary>
    public double BlockedAttentionCap { get; init; } = 65.0;

    /// <summary>Titles longer than this skip series parsing (bounded work).</summary>
    public int MaxTitleParseLength { get; init; } = 1000;
}
