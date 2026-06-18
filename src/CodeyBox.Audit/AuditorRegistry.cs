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
        // Tier 0 (BuildTestGate) -> Tier 1 (declared short-circuit gates) ->
        // Tier 2 (other tool/local) -> Tier 3 (credentialed LLM/network). The
        // pipeline applies the same tier function per iteration; the ordering
        // invariant lives in AuditorOrdering.TierOf so one site owns it.
        // OrderBy is stable, so within each tier the original registration
        // order is preserved.
        All = auditors.OrderBy(AuditorOrdering.TierOf).ToList();
    }

    public IReadOnlyList<IAuditor> All { get; }
}
