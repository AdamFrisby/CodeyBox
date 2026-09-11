namespace CodeyBox.Core;

/// <summary>
/// What an executor host does with a running sandbox when its orchestrator
/// connection drops, and what happens when the connection is re-established.
/// Losing the connection must not orphan a running sandbox: either the
/// sandbox is retained for resumption or it is torn down, and reconnection
/// must never leave a sandbox running that nothing tracks.
/// </summary>
public enum ExecutorDisconnectPolicy
{
    /// <summary>
    /// The default and currently only policy. On connection loss the executor
    /// keeps every in-flight sandbox alive (no disposal) and keeps tracking
    /// its phase binding locally, so a dropped TCP connection or an
    /// orchestrator restart does not kill running agent work. On reconnect the
    /// executor reconciles local inventory against its tracked phases: phases
    /// still owned resume, and any running sandbox with no tracked owner is
    /// torn down so no sandbox runs untracked. Heartbeats into the existing
    /// worker registry keep the registration live across the outage; if the
    /// outage outlasts the dead-worker threshold, the existing dead-worker
    /// reaper reclaims the registration exactly as it would a dead in-process
    /// worker — no second liveness scheme is involved.
    /// </summary>
    RetainSandboxForResume = 0,
}
