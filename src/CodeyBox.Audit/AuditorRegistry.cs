using CodeyBox.Core;

namespace CodeyBox.Audit;

/// <summary>
/// Default IAuditorRegistry — wraps the DI-injected list of auditors and
/// keeps them in registration order so deterministic auditors run before
/// LLM-based ones (failing-fast on cheap checks before paying for tokens).
/// </summary>
public sealed class AuditorRegistry : IAuditorRegistry
{
    public AuditorRegistry(IEnumerable<IAuditor> auditors)
    {
        // Stable order: tool-only first, LLM/network ones after. This lets
        // cheap deterministic checks short-circuit expensive LLM reviews on
        // each iteration if AuditOptions.StopOnFirstFailingAuditor is set.
        All = auditors
            .OrderBy(a => a.Required.HasFlag(AuditCapabilities.AgentCredentials) ? 1 : 0)
            .ToList();
    }

    public IReadOnlyList<IAuditor> All { get; }
}
