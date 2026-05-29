namespace CodeyBox.Core;

/// <summary>
/// Shared, swappable holder for the current per-agent default model IDs.
/// Registered as a DI singleton so every runner reads through the same
/// reference. The hot-reload coordinator updates this holder via
/// <see cref="Replace"/>, and subsequent agent runs pick up the new
/// defaults without a process restart.
///
/// <para>
/// Mirrors the <c>AgentConcurrencySnapshot</c> pattern: Volatile read/write
/// so a concurrent <see cref="Replace"/> cannot tear the reference; callers
/// should bind once into a local for any compound read.
/// </para>
/// </summary>
public sealed class AgentDefaultsSnapshot
{
    private IReadOnlyDictionary<string, string?> _current;

    public AgentDefaultsSnapshot(IReadOnlyDictionary<string, string?> initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
    }

    public IReadOnlyDictionary<string, string?> Current => Volatile.Read(ref _current);

    public void Replace(IReadOnlyDictionary<string, string?> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Volatile.Write(ref _current, next);
    }

    public string? GetDefault(string agentKindValue)
    {
        if (Current.TryGetValue(agentKindValue, out var value) && !string.IsNullOrWhiteSpace(value))
            return value;
        return null;
    }
}
