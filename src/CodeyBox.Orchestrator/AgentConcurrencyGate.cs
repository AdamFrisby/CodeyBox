using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Per-agent concurrency-cap view used by candidate selection / dispatch to
/// deprioritize agents whose operator-configured cap is at ceiling. The cap is
/// shorthand for "this agent's API account budget is currently saturated"; a
/// second concurrent call from the resolver against the same account is what
/// produces the HTTP 429 reported in c9fd5b75. Both collaborators are optional
/// so tests/embeddings that don't wire concurrency keep their previous
/// "always-route-to-primary" semantics (caps read as 0 = unlimited).
/// </summary>
internal sealed class AgentConcurrencyGate
{
    private readonly IAgentRunningCounters? _agentRunningCounters;
    // Shared swappable holder for per-agent caps. The same instance is held by
    // OrchestratorService, so the hot-reload coordinator's call to
    // OrchestratorService.ApplyAgentConcurrencyReload (which writes through the
    // shared snapshot) is observable here on the next GetCapSafe read.
    private readonly AgentConcurrencySnapshot? _concurrencySnapshot;

    public AgentConcurrencyGate(
        IAgentRunningCounters? agentRunningCounters,
        AgentConcurrencySnapshot? concurrencySnapshot)
    {
        _agentRunningCounters = agentRunningCounters;
        _concurrencySnapshot = concurrencySnapshot;
    }

    /// <summary>
    /// Returns true when <paramref name="agent"/> has an operator-configured
    /// per-agent cap and the live in-flight count is at or above that cap.
    /// Always false when either the cap config or the running counters are
    /// not wired — keeping the resolver's behaviour stable for tests /
    /// embeddings that don't register concurrency.
    /// </summary>
    public bool IsAtAgentCap(AgentKind agent)
    {
        var cap = GetCapSafe(agent);
        if (cap <= 0) return false;
        if (_agentRunningCounters is null) return false;
        return _agentRunningCounters.GetRunning(agent) >= cap;
    }

    public bool IsAtAgentCap(AgentMembership member)
    {
        var cap = GetCapSafe(member);
        if (cap <= 0) return false;
        if (_agentRunningCounters is null) return false;
        return _agentRunningCounters.GetRunning(member) >= cap;
    }

    public int GetCapSafe(AgentKind agent)
    {
        // Bind the snapshot reference once so a concurrent ApplyConcurrencyReload
        // can't tear the read between the existence check and the lookup.
        // Defence-in-depth on MaxConcurrent: AgentConcurrencyOptions.ValidateAndThrow
        // rejects values <= 0 at load, but tests can construct an options
        // instance directly without the validator, so we keep the > 0 guard.
        var opts = _concurrencySnapshot?.Current;
        return opts is not null
            && opts.Members.TryGetValue(agent.Value, out var entry)
            && entry is { MaxConcurrent: > 0 }
            ? entry.MaxConcurrent
            : 0;
    }

    public int GetCapSafe(AgentMembership member)
    {
        var opts = _concurrencySnapshot?.Current;
        if (opts is null)
            return 0;

        if (opts.Members.TryGetValue(member.RouteKey, out var exact)
            && exact is { MaxConcurrent: > 0 })
            return exact.MaxConcurrent;

        if (opts.Members.TryGetValue(member.Agent.Value, out var byKind)
            && byKind is { MaxConcurrent: > 0 })
            return byKind.MaxConcurrent;

        return 0;
    }

    public int GetRunningSafe(AgentKind agent) =>
        _agentRunningCounters?.GetRunning(agent) ?? 0;

    public int GetRunningSafe(AgentMembership member) =>
        _agentRunningCounters?.GetRunning(member) ?? 0;
}
