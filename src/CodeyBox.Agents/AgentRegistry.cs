using CodeyBox.Core;

namespace CodeyBox.Agents;

/// <summary>
/// Default IAgentRegistry: a simple immutable map. Construct with all known
/// runners at composition time; consumers depend on the interface so they
/// don't need to know which agents the deployment supports.
/// </summary>
public sealed class AgentRegistry : IAgentRegistry
{
    private readonly Dictionary<AgentKind, IAgentRunner> _byKind;

    public AgentRegistry(IEnumerable<IAgentRunner> runners)
    {
        _byKind = runners.ToDictionary(r => r.Kind);
    }

    public IReadOnlyCollection<AgentKind> Available => _byKind.Keys;

    public bool TryGet(AgentKind kind, out IAgentRunner runner)
    {
        if (_byKind.TryGetValue(kind, out var r))
        {
            runner = r;
            return true;
        }
        runner = null!;
        return false;
    }
}
