using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// In-memory <see cref="IInVmSmokeCache"/> implementation keyed by
/// <c>(AgentKind, baselineImageRef)</c>. Thread-safe. Not persisted — cleared
/// on orchestrator restart.
///
/// <para>A baseline rebake yields a new content-hash ref, so its key never
/// collides with the prior baseline's entry — that is how a rebake forces the
/// probes to re-run (AC#3). The TTL only bounds staleness within a single
/// baseline (and for non-baseline providers whose ref is a fixed sentinel).</para>
/// </summary>
public sealed class InVmSmokeCache : IInVmSmokeCache
{
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _time;
    private readonly Dictionary<(AgentKind, string), (AgentSmokeResult Result, DateTimeOffset ExpiresAt)> _entries = new();
    private readonly object _lock = new();

    public InVmSmokeCache(TimeSpan ttl, TimeProvider? time = null)
    {
        _ttl = ttl;
        _time = time ?? TimeProvider.System;
    }

    public AgentSmokeResult? TryGet(AgentKind kind, string baselineRef)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue((kind, baselineRef), out var entry) &&
                _time.GetUtcNow() < entry.ExpiresAt)
                return entry.Result;
            return null;
        }
    }

    public void Set(AgentKind kind, string baselineRef, AgentSmokeResult result)
    {
        lock (_lock)
        {
            _entries[(kind, baselineRef)] = (result, _time.GetUtcNow() + _ttl);
        }
    }
}
