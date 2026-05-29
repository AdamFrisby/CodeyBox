namespace CodeyBox.Orchestrator;

/// <summary>
/// How the suspend-on-shutdown handler tears down in-flight worker sandboxes
/// during a graceful host shutdown. Picked by operator config
/// (<c>CodeyBox:Shutdown:SandboxTeardownMode</c>) per the post-incident review
/// of 2026-05-29: <c>multipass suspend</c> writes a multi-GiB RAM snapshot and
/// the qemu disk-image write-lock is held for the duration; an interrupted
/// suspend can leave the VM wedged in <c>Suspending</c> with the lock still
/// held by an orphan qemu process, blocking subsequent <c>multipass stop</c>
/// and <c>multipass delete --purge</c>. The Stop and Dispose modes give
/// operators robust alternatives whose shutdown windows are seconds, not
/// minutes, at the cost of losing in-VM RAM state across the restart.
/// </summary>
public enum SandboxTeardownMode
{
    /// <summary>
    /// Original behaviour: freeze the VM's RAM via <c>multipass suspend</c> and
    /// resume from the snapshot on restart. Preserves in-VM agent state across
    /// the restart. Vulnerable to qemu-lock wedging if the host is SIGKILLed
    /// before the snapshot finishes. Default for backward-compat.
    /// </summary>
    Suspend = 0,

    /// <summary>
    /// Clean stop: <c>multipass stop</c> kills the in-VM agent process but
    /// preserves the VM's disk. Far less likely to wedge multipassd than
    /// suspend (no RAM snapshot, qemu shuts down cleanly and releases the
    /// disk-image lock). The work item recovers via its preempt-checkpoint
    /// the same way it would under any non-suspending provider.
    /// </summary>
    Stop = 1,

    /// <summary>
    /// Full dispose: <c>multipass delete --purge</c>. The VM is gone after
    /// shutdown; no resume bookkeeping is written. The simplest mode against
    /// multipassd lock contention, at the cost of repaying VM provisioning
    /// (image clone / cloud-init) on the next pickup.
    /// </summary>
    Dispose = 2,
}

