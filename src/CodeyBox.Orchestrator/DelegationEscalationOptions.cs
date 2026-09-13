namespace CodeyBox.Orchestrator;

/// <summary>
/// Operational tuning knobs for delegation triggers: the operator command
/// plus automatic escalation on non-convergence signals. Bound from
/// <c>CodeyBox:DelegationEscalation</c> and read through a hot-reloadable
/// accessor so edits take effect on the next trigger without restart.
/// </summary>
public sealed class DelegationEscalationOptions
{
    /// <summary>
    /// Master switch for automatic escalation. Default <c>false</c> so the
    /// feature is operator-only unless an operator deliberately opts into
    /// unsupervised escalation. The operator delegate command works
    /// regardless of this flag.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Escalate automatically when audit iterations reach the configured
    /// maximum without passing (currently parked for the operator).
    /// Individually disableable. Default <c>true</c> (effective only when
    /// <see cref="Enabled"/> is also true).
    /// </summary>
    public bool OnAuditMaxIterations { get; set; } = true;

    /// <summary>
    /// Escalate automatically when an item terminally fails repeatedly after
    /// retry. Individually disableable. Default <c>true</c> (effective only
    /// when <see cref="Enabled"/> is also true).
    /// </summary>
    public bool OnRepeatedTerminalFailure { get; set; } = true;

    /// <summary>
    /// Terminal-failure episodes (<see cref="CodeyBox.Core.WorkItem.TerminalFailureCount"/>)
    /// required before the repeated-failure condition fires. Episodes survive
    /// retries by design, so manual retries count too. Default 2: the item
    /// failed, was retried, and failed again. Clamped to a minimum of 1 at use.
    /// </summary>
    public int RepeatedTerminalFailureThreshold { get; set; } = 2;

    /// <summary>
    /// Upper bound on the operator note stored per delegation request.
    /// Matches the convergence-brief <c>MaxOperatorNoteChars</c> default so
    /// an accepted note is never silently truncated below what the API
    /// allowed. The API rejects longer notes; the service truncates
    /// defensively so it stays safe to call with anything.
    /// </summary>
    public int MaxNoteChars { get; set; } = 4_000;
}
