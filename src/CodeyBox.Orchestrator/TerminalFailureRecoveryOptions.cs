namespace CodeyBox.Orchestrator;

/// <summary>
/// Configuration for <see cref="TerminalFailureRecoveryService"/> — the
/// failure-class policy that replaces the blunt external chaperone with
/// classified auto-retry (TRANSIENT) vs. parked-for-operator (DETERMINISTIC).
/// Hot-reloadable via the live accessor at Program.cs.
/// </summary>
/// <remarks>
/// <para>
/// The service is OFF by default — opting in is the operator's deliberate
/// decision because it changes how Failed / AuditFailed /
/// MergeConflictResolutionFailed rows accumulate (an auto-retry can land a
/// fresh attempt, reset counters, and trigger downstream webhook events
/// without operator action). Quota-shaped rows stay routed through
/// <c>QuotaRetryScheduler</c> regardless of this flag.
/// </para>
/// </remarks>
public sealed record TerminalFailureRecoveryOptions
{
    /// <summary>Master switch. Default <c>false</c>.</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// How often the service walks Failed / AuditFailed /
    /// MergeConflictResolutionFailed rows and asks the classifier whether
    /// any are eligible to retry. Default 5 minutes (matches the
    /// quota-retry scheduler's cadence so an operator only has one sweep
    /// cadence to reason about).
    /// </summary>
    public TimeSpan PeriodicCheckInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Base backoff between transient retries (attempt N → wait
    /// <c>min(BackoffMax, BaseBackoff * 2^(N-1))</c>). Default 1 minute —
    /// short enough to recover from a network blip within the same sweep,
    /// long enough that a thundering-herd retry storm cannot bury the
    /// workers.
    /// </summary>
    public TimeSpan BaseBackoff { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Upper bound for the exponential-backoff window. Default 30 minutes —
    /// matches how long an operator typically expects between retries on
    /// a flaky agent without a manual nudge.
    /// </summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Random jitter added to / subtracted from each backoff delay, expressed
    /// as a fraction of the deterministic delay (e.g. 0.2 = ±20%). 0 disables
    /// jitter (test-friendly); the default 0.2 keeps simultaneous failures
    /// from re-arming in lockstep.
    /// </summary>
    public double JitterFraction { get; init; } = 0.2;

    /// <summary>
    /// Hard cap on the number of auto-retries the recovery service may
    /// execute against a single work item. Past the cap the item is
    /// dead-lettered to <see cref="CodeyBox.Core.WorkItemState.NeedsOperatorInput"/>
    /// with an explicit <c>LastError</c> so the operator sees that
    /// auto-retry surrendered (vs. an item that is silently looping).
    /// Default 3 — enough for a transient blip to clear, low enough that
    /// a persistent fault gets a human within an hour.
    /// </summary>
    public int MaxAutoRetriesPerWorkItem { get; init; } = 3;
}
