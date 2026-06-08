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
        // Stable order:
        //   1. BuildTestGate auditors first (deterministic build+test must
        //      provably pass before any LLM panel asserts CI did so).
        //   2. Other declared short-circuit gates.
        //   3. Other tool/local auditors.
        //   4. LLM/network auditors last.
        // OrderBy is stable, so within each tier the original registration
        // order is preserved.
        All = auditors
            .OrderBy(a => a.Role == AuditorRole.BuildTestGate ? 0
                : a.CanShortCircuitOnBlockingFinding ? 1
                : a.Required.HasFlag(AuditCapabilities.AgentCredentials) ? 3
                : 2)
            .ToList();
    }

    public IReadOnlyList<IAuditor> All { get; }
}
