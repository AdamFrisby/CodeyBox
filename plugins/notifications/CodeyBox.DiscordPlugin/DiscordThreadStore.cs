using System.Collections.Concurrent;

namespace CodeyBox.DiscordPlugin;

/// <summary>
/// Discord message identity remembered by the plugin so follow-ups thread
/// and decisions land in place:
/// <list type="bullet">
/// <item>thread root per (channel, work item) — follow-up notifications for
/// the same work item post into the thread rooted at the first message, so
/// a long-running item reads as one conversation;</item>
/// <item>message identity per correlation token — the decision edit
/// (PATCH) targets the message that carried the buttons.</item>
/// </list>
/// Thread-safe, bounded, and time-expired. Inject <see cref="TimeProvider"/>
/// in tests for deterministic expiry.
/// </summary>
internal sealed class DiscordThreadStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private TimeSpan _lifetime;
    private int _maxEntries;

    private sealed record Entry(string Channel, string MessageId, DateTimeOffset StoredAt);

    public DiscordThreadStore(TimeProvider? clock = null, TimeSpan? lifetime = null, int maxEntries = 10_000)
    {
        _clock = clock ?? TimeProvider.System;
        _lifetime = lifetime is { Ticks: > 0 } value ? value : TimeSpan.FromHours(24);
        _maxEntries = maxEntries >= 1 ? maxEntries : 10_000;
    }

    /// <summary>Apply the operator-configured bounds (hot-reloadable).
    /// Non-positive values fall back to the built-in defaults so a bad
    /// config edit can never unboundedly grow the store or expire
    /// everything immediately.</summary>
    public void Configure(TimeSpan lifetime, int maxEntries)
    {
        _lifetime = lifetime.Ticks > 0 ? lifetime : TimeSpan.FromHours(24);
        _maxEntries = maxEntries >= 1 ? maxEntries : 10_000;
        while (_entries.Count > _maxEntries)
            EvictOldest();
    }

    private static string ThreadKey(string channel, string workItemId) => $"thread:{channel}:{workItemId}";

    private static string MessageKey(string correlationToken) => $"msg:{correlationToken}";

    public void RememberThreadRoot(string channel, string workItemId, string threadId)
    {
        if (string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(workItemId) || string.IsNullOrWhiteSpace(threadId))
            return;
        Store(ThreadKey(channel, workItemId), new Entry(threadId, string.Empty, _clock.GetUtcNow()));
    }

    public string? ThreadRootFor(string channel, string workItemId)
    {
        if (string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(workItemId))
            return null;
        return Lookup(ThreadKey(channel, workItemId))?.Channel;
    }

    public void RememberMessage(string correlationToken, string channelId, string messageId)
    {
        if (string.IsNullOrWhiteSpace(correlationToken) || string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(messageId))
            return;
        Store(MessageKey(correlationToken), new Entry(channelId, messageId, _clock.GetUtcNow()));
    }

    public (string ChannelId, string MessageId)? MessageFor(string correlationToken)
    {
        if (string.IsNullOrWhiteSpace(correlationToken))
            return null;
        var entry = Lookup(MessageKey(correlationToken));
        return entry is null || string.IsNullOrEmpty(entry.MessageId)
            ? null
            : (entry.Channel, entry.MessageId);
    }

    private void Store(string key, Entry entry)
    {
        EvictExpired();
        if (_entries.Count >= _maxEntries)
            EvictOldest();
        _entries[key] = entry;
    }

    private Entry? Lookup(string key)
    {
        if (!_entries.TryGetValue(key, out var entry))
            return null;
        if (_clock.GetUtcNow() - entry.StoredAt >= _lifetime)
        {
            _entries.TryRemove(key, out _);
            return null;
        }
        return entry;
    }

    private void EvictExpired()
    {
        if (_entries.Count < _maxEntries)
        {
            var now = _clock.GetUtcNow();
            foreach (var (key, entry) in _entries)
            {
                if (now - entry.StoredAt >= _lifetime)
                    _entries.TryRemove(key, out _);
            }
            return;
        }
        foreach (var (key, entry) in _entries)
        {
            var now = _clock.GetUtcNow();
            if (now - entry.StoredAt >= _lifetime)
                _entries.TryRemove(key, out _);
            if (_entries.Count < _maxEntries)
                break;
        }
        if (_entries.Count >= _maxEntries)
            EvictOldest();
    }

    private void EvictOldest()
    {
        var oldest = _entries.OrderBy(kv => kv.Value.StoredAt).FirstOrDefault();
        if (oldest.Key is not null)
            _entries.TryRemove(oldest.Key, out _);
    }
}
