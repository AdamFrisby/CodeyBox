using System.Collections.Concurrent;

namespace CodeyBox.MatrixPlugin;

/// <summary>
/// Matrix event identity remembered by the plugin so follow-ups thread and
/// retries stay idempotent:
/// <list type="bullet">
/// <item>thread root event ID per (room, work item) — follow-up notifications
/// for the same work item post as <c>m.thread</c> replies, so a long-running
/// item reads as one conversation;</item>
/// <item>sent event identity per correlation token — a bounded durable mapping
/// from the question notification to the room/event that carried it.</item>
/// </list>
/// Thread-safe, bounded, and time-expired. Inject <see cref="TimeProvider"/>
/// in tests for deterministic expiry. A restart starts new threads rather
/// than failing: entries are memory-resident by design (the server-side
/// transaction-ID dedup in <see cref="MatrixTxnIds"/> is what makes retries
/// safe across restarts, not this store).
/// </summary>
internal sealed class MatrixThreadStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private TimeSpan _lifetime;
    private int _maxEntries;

    private sealed record Entry(string RoomId, string EventId, DateTimeOffset StoredAt);

    public MatrixThreadStore(TimeProvider? clock = null, TimeSpan? lifetime = null, int maxEntries = 10_000)
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

    private static string ThreadKey(string roomId, string workItemId) => $"thread:{roomId}:{workItemId}";

    private static string EventKey(string correlationToken) => $"event:{correlationToken}";

    public void RememberThreadRoot(string roomId, string workItemId, string eventId)
    {
        if (string.IsNullOrWhiteSpace(roomId) || string.IsNullOrWhiteSpace(workItemId) || string.IsNullOrWhiteSpace(eventId))
            return;
        Store(ThreadKey(roomId, workItemId), new Entry(roomId, eventId, _clock.GetUtcNow()));
    }

    public string? ThreadRootFor(string roomId, string workItemId)
    {
        if (string.IsNullOrWhiteSpace(roomId) || string.IsNullOrWhiteSpace(workItemId))
            return null;
        return Lookup(ThreadKey(roomId, workItemId))?.EventId;
    }

    public void RememberEvent(string correlationToken, string roomId, string eventId)
    {
        if (string.IsNullOrWhiteSpace(correlationToken) || string.IsNullOrWhiteSpace(roomId) || string.IsNullOrWhiteSpace(eventId))
            return;
        Store(EventKey(correlationToken), new Entry(roomId, eventId, _clock.GetUtcNow()));
    }

    public (string RoomId, string EventId)? EventFor(string correlationToken)
    {
        if (string.IsNullOrWhiteSpace(correlationToken))
            return null;
        var entry = Lookup(EventKey(correlationToken));
        return entry is null ? null : (entry.RoomId, entry.EventId);
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
