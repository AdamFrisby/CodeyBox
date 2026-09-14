using CodeyBox.Core;

namespace CodeyBox.Audit;

/// <summary>
/// Single source of truth for audit-panel ordering. This lives in the audit
/// layer so Core stays limited to neutral auditor contract metadata.
/// Tiers derive from declared capabilities only — never from concrete types.
/// </summary>
public static class AuditorOrdering
{
    /// <summary>
    /// True when the auditor is a human reviewer (declared
    /// <c>Kind = "human"</c>). Human reviewers park the pipeline awaiting an
    /// operator verdict, so they sort after every automated auditor.
    /// </summary>
    public static bool IsHuman(IAuditor auditor)
    {
        ArgumentNullException.ThrowIfNull(auditor);
        return string.Equals(auditor.Kind, WellKnownAuditorKinds.Human, StringComparison.OrdinalIgnoreCase);
    }

    public static int TierOf(IAuditor auditor)
        => auditor.Role == AuditorRole.BuildTestGate ? 0
            : auditor.CanShortCircuitOnBlockingFinding ? 1
            : IsHuman(auditor) ? 4
            : auditor.Required.HasFlag(AuditCapabilities.AgentCredentials) ? 3
            : 2;
}
