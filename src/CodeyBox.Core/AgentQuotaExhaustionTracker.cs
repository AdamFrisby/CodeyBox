using System.Collections.Concurrent;

namespace CodeyBox.Core;

public sealed class AgentQuotaExhaustionTracker
{
    private readonly AgentQuotaExhaustionTracker<AgentQuotaMemberKey> _inner = new();

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
        DateTimeOffset? earliestKnownReset = null,
        QuotaExhaustionEvidence? evidence = null)
        => _inner.MarkExhausted(
            AgentQuotaMemberKey.From(member),
            ttl,
            nowUtc,
            resetAt,
            earliestKnownReset,
            evidence);

    public bool TryGet(AgentMembership member, DateTimeOffset nowUtc, out AgentQuotaExhaustionEntry entry)
        => _inner.TryGet(AgentQuotaMemberKey.From(member), nowUtc, out entry);

    public bool TryClear(AgentMembership member, out AgentQuotaExhaustionEntry removed) =>
        _inner.TryClear(AgentQuotaMemberKey.From(member), out removed);

    public bool TryShorten(AgentMembership member, DateTimeOffset expiresAt, out AgentQuotaExhaustionEntry previous)
        => _inner.TryShorten(AgentQuotaMemberKey.From(member), expiresAt, out previous);

    /// <summary>
    /// Clears every active entry for <paramref name="kind"/> (all models /
    /// instances). Used by the operator reset path. Returns the removed
    /// entries with their recorded evidence for audit.
    /// </summary>
    public IReadOnlyList<KeyValuePair<AgentQuotaMemberKey, AgentQuotaExhaustionEntry>> ClearForAgent(
        AgentKind kind,
        DateTimeOffset nowUtc)
        => _inner.ClearWhere(key => key.Agent == kind, nowUtc);

    public void PruneExpired(DateTimeOffset nowUtc) => _inner.PruneExpired(nowUtc);
}

public sealed class AgentQuotaExhaustionTracker<TKey>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, AgentQuotaExhaustionEntry> _entries = new();

    /// <summary>
    /// Records an in-process exhaustion gate for <paramref name="key"/>.
    /// Non-positive TTLs clear the key instead of installing a synthetic gate.
    /// Future reset hints shorten the gate; past or current hints are ignored.
    /// </summary>
    public bool MarkExhausted(
        TKey key,
        TimeSpan ttl,
        DateTimeOffset nowUtc,
        DateTimeOffset? resetAt = null,
        DateTimeOffset? earliestKnownReset = null,
        QuotaExhaustionEvidence? evidence = null)
    {
        if (ttl <= TimeSpan.Zero)
        {
            _entries.TryRemove(key, out _);
            return false;
        }

        // The sink enforces the narrowing: only provider quota/rate-limit
        // evidence may install a gate. Anything else (a null-evidence legacy
        // write is still accepted for probe-internal runtime hints, but the
        // router-level entry point requires evidence — see AgentClassRouter)
        // must never bench a member.
        if (evidence is { Signal: var signal } && !signal.IsExhaustionSignal())
            return false;

        var expiresAt = nowUtc + ttl;
        ConsiderCap(resetAt);
        ConsiderCap(earliestKnownReset);
        DateTimeOffset? storedResetAt = resetAt is { } reset && reset > nowUtc ? reset : null;

        if (expiresAt <= nowUtc)
        {
            _entries.TryRemove(key, out _);
            return false;
        }

        var next = new AgentQuotaExhaustionEntry(expiresAt, storedResetAt, evidence, RecordedAt: nowUtc);
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

    public bool TryGet(TKey key, DateTimeOffset nowUtc, out AgentQuotaExhaustionEntry entry)
    {
        if (!_entries.TryGetValue(key, out entry))
            return false;

        if (entry.ExpiresAt > nowUtc)
            return true;

        _entries.TryRemove(new KeyValuePair<TKey, AgentQuotaExhaustionEntry>(key, entry));
        entry = default;
        return false;
    }

    public bool TryClear(TKey key, out AgentQuotaExhaustionEntry removed) =>
        _entries.TryRemove(key, out removed);

    /// <summary>
    /// Clears every active entry matching <paramref name="predicate"/> (e.g. all
    /// members of one agent kind on operator reset). Expired entries are pruned
    /// as a side effect. Returns the removed entries with their recorded
    /// evidence so the caller can audit what was cleared.
    /// </summary>
    public IReadOnlyList<KeyValuePair<TKey, AgentQuotaExhaustionEntry>> ClearWhere(
        Func<TKey, bool> predicate,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var removed = new List<KeyValuePair<TKey, AgentQuotaExhaustionEntry>>();
        foreach (var entry in _entries)
        {
            if (entry.Value.ExpiresAt <= nowUtc || predicate(entry.Key))
            {
                if (_entries.TryRemove(entry))
                    removed.Add(entry);
            }
        }
        return removed;
    }

    public bool TryShorten(TKey key, DateTimeOffset expiresAt, out AgentQuotaExhaustionEntry previous)
    {
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
}

/// <summary>
/// An active in-process exhaustion gate. <see cref="Evidence"/> names the
/// provider quota/rate-limit signal that installed it (null only for
/// probe-internal runtime hints that carry no pipeline-level provenance);
/// router-level verdicts always carry evidence. <see cref="RecordedAt"/> is
/// the moment the verdict was installed: a fresh verdict is trusted without
/// re-probing (the live probe lags a just-observed 429), while a verdict
/// older than the router's revalidation age is re-checked against a live
/// probe before it may keep refusing dispatches.
/// </summary>
public readonly record struct AgentQuotaExhaustionEntry(
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ResetAt,
    QuotaExhaustionEvidence? Evidence = null,
    DateTimeOffset RecordedAt = default);
