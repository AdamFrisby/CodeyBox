namespace CodeyBox.Core;

/// <summary>
/// Marker implemented by every exception type that can signal the sandbox's
/// execution transport was lost underneath running work — the VM destroyed
/// (e.g. by the leak reaper), the host crashed, or the exec channel died.
/// <see cref="SandboxDeferralGuard"/> classifies through this single
/// Core-visible surface, so every terminal-outcome catch boundary — in any
/// assembly — covers the complete set identically instead of widening the
/// predicate locally and drifting.
/// </summary>
public interface IExecutionTransportLoss
{
    /// <summary>
    /// True when this occurrence proves the transport was lost. Types whose
    /// every instance means transport loss return a constant true; types that
    /// carry the signal as data — a failure flag on a broader exception —
    /// return the flag.
    /// </summary>
    bool ExecutionUnavailable { get; }
}
