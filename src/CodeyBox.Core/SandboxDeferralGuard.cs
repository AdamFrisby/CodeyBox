namespace CodeyBox.Core;

/// <summary>
/// Single shared definition of the sandbox-provisioning deferral exclusion.
/// Every catch boundary that converts an arbitrary exception into a terminal
/// audit or verification outcome (AuditUnavailableException,
/// RequiredBuildVerificationResult.Unavailable, terminal Failed) must consult
/// this guard so a <see cref="SandboxProvisioningDeferredException"/> —
/// including its <see cref="SandboxDiskDeferredException"/> subtype —
/// propagates to the caller that owns the defer-and-requeue path instead of
/// being flattened into a terminal failure. Two such boundaries already
/// drifted apart once; new boundaries must use this predicate rather than
/// restating the exclusion.
/// </summary>
public static class SandboxDeferralGuard
{
    /// <summary>
    /// Returns true when <paramref name="ex"/> is a sandbox-provisioning
    /// deferral that must propagate rather than be wrapped into a terminal
    /// outcome. Matches the <see cref="SandboxProvisioningDeferredException"/>
    /// base type so every current and future deferral subtype is covered.
    /// Pure: no I/O, no ambient state.
    /// </summary>
    public static bool IsDeferral(Exception ex) => ex is SandboxProvisioningDeferredException;

    /// <summary>
    /// Returns true when <paramref name="ex"/> proves the sandbox's execution
    /// transport was lost underneath running work — the VM was destroyed
    /// (e.g. by a leak reaper), the host crashed, or the exec channel died.
    /// Like a deferral, this must propagate to the layer that owns recoverable
    /// infrastructure classification: wrapping it into a terminal outcome
    /// marks the item failed and hands its working tree to the clone reaper
    /// even though a fresh sandbox reproduces a working environment. The
    /// complete set is recognized through <see cref="IExecutionTransportLoss"/>,
    /// so flagged exception types declared in other assemblies (e.g.
    /// AgentInfrastructureFailureException, SandboxCredentialFileWriteException)
    /// are covered here too — do NOT widen this predicate in a downstream
    /// assembly or the terminal-mapping boundaries split again.
    /// Pure: no I/O, no ambient state.
    /// </summary>
    public static bool IsExecutionTransportLoss(Exception ex) =>
        ex is IExecutionTransportLoss { ExecutionUnavailable: true };

    /// <summary>
    /// Returns true when <paramref name="ex"/> is safe to wrap into a
    /// terminal audit or verification outcome: any exception that is neither
    /// cooperative cancellation, a provisioning deferral, nor an execution
    /// transport loss. Intended for exception filters at terminal-mapping
    /// boundaries, e.g.
    /// <c>catch (Exception ex) when (SandboxDeferralGuard.ShouldWrap(ex))</c>.
    /// Pure: no I/O, no ambient state.
    /// </summary>
    public static bool ShouldWrap(Exception ex)
        => ex is not OperationCanceledException
            && !IsDeferral(ex)
            && !IsExecutionTransportLoss(ex);
}
