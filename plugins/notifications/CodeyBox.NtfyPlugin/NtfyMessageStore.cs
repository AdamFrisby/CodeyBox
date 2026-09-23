using System.Collections.Concurrent;

namespace CodeyBox.NtfyPlugin;

/// <summary>
/// Correlation-token → topic memory so the decision update publishes to the
/// topic the question notification actually went to (the update call carries
/// only the token, not the routing). The ntfy sequence id itself is derived
/// deterministically from the token, so nothing else needs remembering.
/// Thread-safe, bounded, and time-expired — a restart simply falls back to
/// the configured default topic. Inject <see cref="TimeProvider"/> in tests
/// for deterministic expiry.
/// </summary>
internal sealed class NtfyMessageStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private TimeSpan _lifetime;
    private int _maxEntries;

    private sealed record Entry(string Topic, DateTimeOffset StoredAt);

    public NtfyMessageStore(TimeProvider? clock = null, TimeSpan? lifetime = null, int maxEntries = 10_000)
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

    public void Remember(string correlationToken, string topic)
    {
        if (string.IsNullOrWhiteSpace(correlationToken) || string.IsNullOrWhiteSpace(topic))
            return;
        EvictExpired();
        if (_entries.Count >= _maxEntries)
            EvictOldest();
        _entries[correlationToken] = new Entry(topic, _clock.GetUtcNow());
    }

    public string? TopicFor(string correlationToken)
    {
        if (string.IsNullOrWhiteSpace(correlationToken))
            return null;
        if (!_entries.TryGetValue(correlationToken, out var entry))
            return null;
        if (_clock.GetUtcNow() - entry.StoredAt >= _lifetime)
        {
            _entries.TryRemove(correlationToken, out _);
            return null;
        }
        return entry.Topic;
    }

    private void EvictExpired()
    {
        var now = _clock.GetUtcNow();
        var fullScan = _entries.Count < _maxEntries;
        foreach (var (key, entry) in _entries)
        {
            if (now - entry.StoredAt >= _lifetime)
                _entries.TryRemove(key, out _);
            if (!fullScan && _entries.Count < _maxEntries)
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
