using System.Collections.Concurrent;

namespace CodeyBox.Core;

public sealed class AgentQuotaExhaustionTracker
{
    private readonly ConcurrentDictionary<AgentQuotaMemberKey, AgentQuotaExhaustionEntry> _entries = new();

    /// <summary>
    /// Records an in-process exhaustion gate for <paramref name="member"/>.
    /// Returns <c>true</c> when the member remains actively exhausted after the
    /// call, including the case where an existing shorter-lived entry is kept;
    /// returns <c>false</c> only when no active entry remains.
    /// Future reset hints shorten the gate; past or current hints are ignored.
    /// </summary>
    public bool MarkExhausted(
        AgentMembership member,
        TimeSpan ttl,
        DateTimeOffset nowUtc,
        DateTimeOffset? resetAt = null,
        DateTimeOffset? earliestKnownReset = null)
    {
        var key = AgentQuotaMemberKey.From(member);
        if (ttl <= TimeSpan.Zero)
        {
            _entries.TryRemove(key, out _);
            return false;
        }

        var expiresAt = nowUtc + ttl;
        ConsiderCap(resetAt);
        ConsiderCap(earliestKnownReset);
        DateTimeOffset? storedResetAt = resetAt is { } reset && reset > nowUtc ? reset : null;

        if (expiresAt <= nowUtc)
        {
            _entries.TryRemove(key, out _);
            return false;
        }

        var next = new AgentQuotaExhaustionEntry(expiresAt, storedResetAt);
        _entries.AddOrUpdate(key, next, (_, existing) =>
            existing.ExpiresAt <= nowUtc || next.ExpiresAt < existing.ExpiresAt
                ? next
                : existing);
        return true;

        void ConsiderCap(DateTimeOffset? candidate)
        {
            if (candidate is { } cap && cap > nowUtc && cap < expiresAt)
                expiresAt = cap;
        }
    }

    public bool TryGet(AgentMembership member, DateTimeOffset nowUtc, out AgentQuotaExhaustionEntry entry)
    {
        var key = AgentQuotaMemberKey.From(member);
        if (!_entries.TryGetValue(key, out entry))
            return false;

        if (entry.ExpiresAt > nowUtc)
            return true;

        _entries.TryRemove(new KeyValuePair<AgentQuotaMemberKey, AgentQuotaExhaustionEntry>(key, entry));
        entry = default;
        return false;
    }

    public bool TryClear(AgentMembership member, out AgentQuotaExhaustionEntry removed) =>
        _entries.TryRemove(AgentQuotaMemberKey.From(member), out removed);

    public bool TryShorten(AgentMembership member, DateTimeOffset expiresAt, out AgentQuotaExhaustionEntry previous)
    {
        var key = AgentQuotaMemberKey.From(member);
        while (_entries.TryGetValue(key, out previous))
        {
            if (expiresAt >= previous.ExpiresAt)
                return false;

            var shortened = previous with { ExpiresAt = expiresAt };
            if (_entries.TryUpdate(key, shortened, previous))
                return true;
        }

        previous = default;
        return false;
    }

    public void PruneExpired(DateTimeOffset nowUtc)
    {
        foreach (var entry in _entries)
        {
            if (entry.Value.ExpiresAt <= nowUtc)
                _entries.TryRemove(entry);
        }
    }

    public void Clear() => _entries.Clear();
}

public readonly record struct AgentQuotaExhaustionEntry(DateTimeOffset ExpiresAt, DateTimeOffset? ResetAt);
