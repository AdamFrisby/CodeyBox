using CodeyBox.Core;

namespace CodeyBox.Audit;

/// <summary>
/// Default IAuditorRegistry — wraps the DI-injected list of auditors and
/// keeps deterministic gates before later advisory auditors.
/// </summary>
public sealed class AuditorRegistry : IAuditorRegistry
{
    public AuditorRegistry(IEnumerable<IAuditor> auditors)
    {
        // Stable order: declared short-circuit gates first, then tool-only,
        // then LLM/network auditors.
        All = auditors
            .OrderBy(a => a.CanShortCircuitOnBlockingFinding ? 0 : 1)
            .ThenBy(a => a.Required.HasFlag(AuditCapabilities.AgentCredentials) ? 1 : 0)
            .ToList();
    }

    public IReadOnlyList<IAuditor> All { get; }
}
