using CodeyBox.Core;

namespace CodeyBox.Audit;

/// <summary>
/// Single source of truth for audit-panel ordering. This lives in the audit
/// layer so Core stays limited to neutral auditor contract metadata.
/// </summary>
public static class AuditorOrdering
{
    public static int TierOf(IAuditor auditor)
        => auditor.Role == AuditorRole.BuildTestGate ? 0
            : auditor.CanShortCircuitOnBlockingFinding ? 1
            : auditor.Required.HasFlag(AuditCapabilities.AgentCredentials) ? 3
            : 2;
}
