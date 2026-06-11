using System;
using System.Collections.Generic;
using System.Threading;

namespace CodeyBox.Core;

/// <summary>
/// Shared, swappable holder for the current per-agent network tolerance options.
/// Registered as a DI singleton so every runner reads through the same
/// reference. The hot-reload coordinator updates this holder,
/// and subsequent agent runs pick up the new settings without a process restart.
/// </summary>
public sealed class AgentNetworkToleranceSnapshot
{
    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _current;

    public AgentNetworkToleranceSnapshot(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = CopyTolerance(initial);
    }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Current => Volatile.Read(ref _current);

    public void Replace(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Volatile.Write(ref _current, CopyTolerance(next));
    }

    public IReadOnlyDictionary<string, string>? GetTolerance(string agentKind)
    {
        if (Current.TryGetValue(agentKind, out var dict))
        {
            return dict;
        }
        return null;
    }

    private static Dictionary<string, IReadOnlyDictionary<string, string>> CopyTolerance(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> source)
    {
        var copy = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in source)
        {
            if (kvp.Value != null)
            {
                copy[kvp.Key] = new Dictionary<string, string>(kvp.Value, StringComparer.OrdinalIgnoreCase);
            }
        }
        return copy;
    }
}
