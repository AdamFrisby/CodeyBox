namespace CodeyBox.Core;

/// <summary>
/// Stable string labels for what cancelled a phase. Persisted in
/// <see cref="WorkItem.CancellationSource"/> and surfaced in webhook /
/// audit-log events; values must not be renumbered or renamed without
/// a migration plan for already-stored work items.
///
/// <para>
/// The two previously-conflated cases — an actual configured timeout
/// (<see cref="PhaseTimeout"/>) and a host-side cancellation whose origin
/// we can't attribute (<see cref="Unknown"/>) — used to both surface as
/// <c>failureKind=timeout</c> with <c>lastError='A task was canceled.'</c>.
/// They now resolve to distinct sources, so the operator can tell apart
/// "the work phase exceeded WorkTimeout" from "something transient
/// cancelled mid-iteration".
/// </para>
/// </summary>
public static class CancellationSources
{
    /// <summary>Operator pressed cancel (DELETE /workitems/{id}).</summary>
    public const string Operator = "operator";

    /// <summary>Host shutdown requested (BackgroundService stopping).</summary>
    public const string HostShutdown = "host-shutdown";

    /// <summary>
    /// Host shutdown grace window elapsed before the phase drained. Distinct
    /// from <see cref="HostShutdown"/> so observers can tell apart "we asked
    /// the phase to drain" from "we ran past the grace and force-cancelled".
    /// </summary>
    public const string HostShutdownDeadline = "host-shutdown-deadline";

    /// <summary>Stuck-probe cancelled the phase after zero-activity threshold.</summary>
    public const string StuckProbe = "stuck-probe";

    /// <summary>
    /// An <see cref="OperationCanceledException"/> propagated out of the phase
    /// without any contributor having recorded itself. Treated as a transient
    /// host-side cancellation candidate — the auto-retry path may pick the
    /// work item back up rather than fail it.
    /// </summary>
    public const string Unknown = "unknown";

    /// <summary>Prefix for configured per-phase wall-clock timeouts.</summary>
    public const string TimeoutPrefix = "timeout:";

    /// <summary>Returns the canonical source label for a per-phase timeout.</summary>
    public static string PhaseTimeout(string phase) => TimeoutPrefix + phase;

    /// <summary>True when <paramref name="source"/> denotes a configured per-phase timeout.</summary>
    public static bool IsPhaseTimeout(string? source) =>
        source is not null && source.StartsWith(TimeoutPrefix, StringComparison.Ordinal);

    /// <summary>
    /// True when <paramref name="source"/> is host-side noise that the auto-retry
    /// loop should treat as transient (re-run the phase rather than fail the item).
    /// Configured timeouts and operator cancellations are excluded — those reflect
    /// the operator's intent and should not be silently retried.
    /// <para>
    /// <see cref="HostShutdownDeadline"/> is intentionally NOT transient: by
    /// construction it can only fire after <see cref="HostShutdown"/> has
    /// already been recorded, and the host-shutdown catch in
    /// <c>PipelineRunner.RunAsync</c> wins ordering — the item is left
    /// mid-flight for the recovery loop to pick up on next startup, which is
    /// the correct behaviour while the host is going away.
    /// </para>
    /// </summary>
    public static bool IsTransient(string? source) => source is Unknown;
}
