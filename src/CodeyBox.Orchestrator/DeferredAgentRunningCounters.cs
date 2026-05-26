using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Resolves <see cref="IAgentRunningCounters"/> through a lazy delegate so the
/// router (a constructor dependency of <see cref="OrchestratorService"/>) can
/// observe the live in-flight counts without the DI container hitting a
/// circular dependency. The delegate is invoked on every read; it should
/// return the cached singleton.
/// </summary>
public sealed class DeferredAgentRunningCounters : IAgentRunningCounters
{
    private readonly Func<IAgentRunningCounters> _resolve;

    public DeferredAgentRunningCounters(Func<IAgentRunningCounters> resolve)
    {
        _resolve = resolve;
    }

    public int GetRunning(AgentKind agent) => _resolve().GetRunning(agent);

    public IReadOnlyDictionary<AgentKind, int> Snapshot() => _resolve().Snapshot();
}
