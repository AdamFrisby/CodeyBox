using System.Collections.Concurrent;

namespace CodeyBox.SlackPlugin;

/// <summary>
/// Slack message identity remembered by the plugin so follow-ups thread and
/// decisions land in place:
/// <list type="bullet">
/// <item>thread root per (channel, work item) — follow-up notifications for
/// the same work item post as threaded replies, so a long-running item reads
/// as one conversation;</item>
/// <item>message identity per correlation token — the decision update
/// (<c>chat.update</c>) targets the message that carried the buttons.</item>
/// </list>
/// Thread-safe, bounded, and time-expired. Inject <see cref="TimeProvider"/>
/// in tests for deterministic expiry.
/// </summary>
internal sealed class SlackThreadStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly TimeSpan _lifetime;
    private readonly int _maxEntries;

    private sealed record Entry(string Channel, string Ts, DateTimeOffset StoredAt);

    public SlackThreadStore(TimeProvider? clock = null, TimeSpan? lifetime = null, int maxEntries = 10_000)
    {
        _clock = clock ?? TimeProvider.System;
        _lifetime = lifetime ?? TimeSpan.FromHours(24);
        _maxEntries = maxEntries >= 1 ? maxEntries : 10_000;
    }

    private static string ThreadKey(string channel, string workItemId) => $"thread:{channel}:{workItemId}";

    private static string MessageKey(string correlationToken) => $"msg:{correlationToken}";

    public void RememberThreadRoot(string channel, string workItemId, string threadTs)
    {
        if (string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(workItemId) || string.IsNullOrWhiteSpace(threadTs))
            return;
        Store(ThreadKey(channel, workItemId), new Entry(channel, threadTs, _clock.GetUtcNow()));
    }

    public string? ThreadRootFor(string channel, string workItemId)
    {
        if (string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(workItemId))
            return null;
        return Lookup(ThreadKey(channel, workItemId))?.Ts;
    }

    public void RememberMessage(string correlationToken, string channel, string messageTs)
    {
        if (string.IsNullOrWhiteSpace(correlationToken) || string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(messageTs))
            return;
        Store(MessageKey(correlationToken), new Entry(channel, messageTs, _clock.GetUtcNow()));
    }

    public (string Channel, string Ts)? MessageFor(string correlationToken)
    {
        if (string.IsNullOrWhiteSpace(correlationToken))
            return null;
        var entry = Lookup(MessageKey(correlationToken));
        return entry is null ? null : (entry.Channel, entry.Ts);
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
