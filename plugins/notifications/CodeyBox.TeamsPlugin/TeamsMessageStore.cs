namespace CodeyBox.TeamsPlugin;

/// <summary>
/// Bounded in-memory index of posted Teams activities, keyed by the
/// notification's correlation token so a landed decision can refresh the
/// card that carried the question. Entries expire after
/// <see cref="TeamsPluginOptions.EntryLifetime"/> and the store holds at
/// most <see cref="TeamsPluginOptions.MaxEntries"/> — a restart simply
/// starts new cards rather than failing.
/// </summary>
internal sealed class TeamsMessageStore
{
    public sealed record MessageIdentity(string ConversationId, string ActivityId);

    private readonly object _gate = new();
    private readonly Dictionary<string, (MessageIdentity Identity, DateTimeOffset ExpiresAt)> _entries = new(StringComparer.Ordinal);
    private TimeSpan _lifetime = TimeSpan.FromHours(24);
    private int _maxEntries = 10_000;
    private readonly TimeProvider _clock;

    public TeamsMessageStore(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    public void Configure(TimeSpan lifetime, int maxEntries)
    {
        lock (_gate)
        {
            _lifetime = lifetime > TimeSpan.Zero ? lifetime : TimeSpan.FromHours(24);
            _maxEntries = maxEntries >= 1 ? maxEntries : 10_000;
        }
    }

    public void RememberMessage(string correlationToken, string conversationId, string activityId)
    {
        if (string.IsNullOrWhiteSpace(correlationToken)
            || string.IsNullOrWhiteSpace(conversationId)
            || string.IsNullOrWhiteSpace(activityId))
            return;
        lock (_gate)
        {
            PruneLocked();
            if (_entries.Count >= _maxEntries)
                return;
            _entries[correlationToken] = (new MessageIdentity(conversationId, activityId), _clock.GetUtcNow().Add(_lifetime));
        }
    }

    public MessageIdentity? MessageFor(string? correlationToken)
    {
        if (string.IsNullOrWhiteSpace(correlationToken))
            return null;
        lock (_gate)
        {
            if (!_entries.TryGetValue(correlationToken, out var entry))
                return null;
            if (_clock.GetUtcNow() >= entry.ExpiresAt)
            {
                _entries.Remove(correlationToken);
                return null;
            }
            return entry.Identity;
        }
    }

    private void PruneLocked()
    {
        var now = _clock.GetUtcNow();
        foreach (var key in _entries.Where(kv => now >= kv.Value.ExpiresAt).Select(kv => kv.Key).ToList())
            _entries.Remove(key);
    }
}
