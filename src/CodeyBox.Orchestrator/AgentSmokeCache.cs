using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// In-memory <see cref="IAgentSmokeCache"/> implementation. Thread-safe.
/// Not persisted — cleared on orchestrator restart.
/// </summary>
public sealed class AgentSmokeCache : IAgentSmokeCache
{
    private readonly TimeSpan _ttl;
    private readonly Dictionary<(AgentKind, string), (AgentSmokeResult Result, DateTimeOffset ExpiresAt)> _entries = new();
    private readonly object _lock = new();

    public AgentSmokeCache(TimeSpan ttl) => _ttl = ttl;

    public AgentSmokeResult? TryGet(AgentKind kind, string fingerprint)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue((kind, fingerprint), out var entry) &&
                DateTimeOffset.UtcNow < entry.ExpiresAt)
                return entry.Result;
            return null;
        }
    }

    public void Set(AgentKind kind, string fingerprint, AgentSmokeResult result)
    {
        lock (_lock)
        {
            _entries[(kind, fingerprint)] = (result, DateTimeOffset.UtcNow + _ttl);
        }
    }
}
