using System.Collections.Concurrent;

namespace CodeyBox.MattermostPlugin;

/// <summary>
/// Bounded in-memory index of Mattermost posts, keyed three ways so
/// follow-ups thread and redeliveries never spam:
/// <list type="bullet">
/// <item>thread root per (channel, work item) — follow-up notifications for
/// the same work item post as threaded replies (<c>root_id</c> set to the
/// first post's ID), so a long-running item reads as one conversation;</item>
/// <item>posted identity per correlation token — a redelivered notification
/// whose token already posted is skipped instead of posted twice;</item>
/// <item>ambiguity tombstone per correlation token — when the server answered
/// 2xx but the body carried no usable post ID, the post may already exist,
/// so a later redelivery of the same token is suppressed rather than risking
/// a duplicate.</item>
/// </list>
/// Thread-safe, bounded, and time-expired. Inject <see cref="TimeProvider"/>
/// in tests for deterministic expiry. A restart starts new threads rather
/// than failing — the store is a spam guard, not durable history.
/// </summary>
internal sealed class MattermostPostStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private TimeSpan _lifetime;
    private int _maxEntries;

    private sealed record Entry(string Channel, string PostId, bool Ambiguous, DateTimeOffset StoredAt);

    public MattermostPostStore(TimeProvider? clock = null, TimeSpan? lifetime = null, int maxEntries = 10_000)
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

    private static string PostedKey(string correlationToken) => $"posted:{correlationToken}";

    public void RememberThreadRoot(string channel, string workItemId, string rootPostId)
    {
        if (string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(workItemId) || string.IsNullOrWhiteSpace(rootPostId))
            return;
        Store(ThreadKey(channel, workItemId), new Entry(channel, rootPostId, false, _clock.GetUtcNow()));
    }

    public string? ThreadRootFor(string channel, string workItemId)
    {
        if (string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(workItemId))
            return null;
        var entry = Lookup(ThreadKey(channel, workItemId));
        return entry is null || entry.Ambiguous ? null : entry.PostId;
    }

    public void RememberPosted(string correlationToken, string channel, string postId)
    {
        if (string.IsNullOrWhiteSpace(correlationToken) || string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(postId))
            return;
        Store(PostedKey(correlationToken), new Entry(channel, postId, false, _clock.GetUtcNow()));
    }

    /// <summary>Record that a post for this token may already exist on the
    /// server even though no ID came back: later redeliveries of the same
    /// token are suppressed rather than risking a duplicate.</summary>
    public void RememberAmbiguous(string correlationToken, string channel)
    {
        if (string.IsNullOrWhiteSpace(correlationToken) || string.IsNullOrWhiteSpace(channel))
            return;
        Store(PostedKey(correlationToken), new Entry(channel, string.Empty, true, _clock.GetUtcNow()));
    }

    public (string Channel, string PostId)? PostedFor(string correlationToken)
    {
        if (string.IsNullOrWhiteSpace(correlationToken))
            return null;
        var entry = Lookup(PostedKey(correlationToken));
        return entry is null || entry.Ambiguous || string.IsNullOrEmpty(entry.PostId)
            ? null
            : (entry.Channel, entry.PostId);
    }

    public bool IsAmbiguous(string? correlationToken)
    {
        if (string.IsNullOrWhiteSpace(correlationToken))
            return false;
        return Lookup(PostedKey(correlationToken))?.Ambiguous is true;
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
        var now = _clock.GetUtcNow();
        foreach (var (key, entry) in _entries)
        {
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
