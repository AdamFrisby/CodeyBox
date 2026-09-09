using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Decorator that slows quota polling for operator-paused agents while still
/// probing them periodically for observability and recovery signals.
/// </summary>
public sealed class PausedAgentQuotaProbe : IAgentQuotaProbe, IAgentQuotaCacheInvalidator, IAgentQuotaRecoveryStateInvalidator
{
    private readonly IAgentQuotaProbe _inner;
    private readonly IAgentPauseController _pauses;
    private readonly Func<PausedAgentQuotaProbeOptions> _optionsProvider;
    private readonly ILogger? _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<CacheKey, CacheEntry> _pausedCache = new();
    private readonly Dictionary<CacheKey, KeyedProbeLock> _pausedFetchLocks = new();

    public AgentKind Kind => _inner.Kind;

    public PausedAgentQuotaProbe(
        IAgentQuotaProbe inner,
        IAgentPauseController pauses,
        Func<PausedAgentQuotaProbeOptions> optionsProvider,
        ILogger? log = null,
        TimeProvider? timeProvider = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _pauses = pauses ?? throw new ArgumentNullException(nameof(pauses));
        _optionsProvider = optionsProvider ?? throw new ArgumentNullException(nameof(optionsProvider));
        _log = log;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
    {
        var key = CacheKey.For(member);
        if (!await IsPausedAsync(member, ct).ConfigureAwait(false))
        {
            await RemovePausedCacheAsync(key, ct).ConfigureAwait(false);
            return await FetchInnerOrTransientAsync(member, ct).ConfigureAwait(false);
        }

        var options = Validate(_optionsProvider());
        if (await TryGetPausedCacheAsync(key, options, ct).ConfigureAwait(false) is { } entry)
            return entry.Snapshot;

        var fetchLock = await AddPausedFetchWaiterAsync(key, ct).ConfigureAwait(false);
        var acquired = false;
        try
        {
            await fetchLock.Semaphore.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;

            options = Validate(_optionsProvider());
            if (await TryGetPausedCacheAsync(key, options, ct).ConfigureAwait(false) is { } cachedEntry)
                return cachedEntry.Snapshot;

            var snapshot = await FetchInnerOrTransientAsync(member, ct).ConfigureAwait(false);

            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var now = _time.GetUtcNow();
                StorePausedCache(key, snapshot, options, now);
                return snapshot;
            }
            finally
            {
                _lock.Release();
            }
        }
        finally
        {
            if (acquired)
                fetchLock.Semaphore.Release();

            await RemovePausedFetchWaiterAsync(key, fetchLock).ConfigureAwait(false);
        }
    }

    public async Task MarkExhaustedAsync(
        AgentMembership member,
        TimeSpan ttl,
        DateTimeOffset? resetAt = null,
        CancellationToken ct = default)
    {
        var key = CacheKey.For(member);
        try
        {
            await _inner.MarkExhaustedAsync(member, ttl, resetAt, ct).ConfigureAwait(false);
            if (_inner is IAgentQuotaCacheInvalidator invalidator)
                invalidator.InvalidateResponseCache();
        }
        finally
        {
            await ClearMemberStateAsync(key, ct).ConfigureAwait(false);
        }
    }

    private async Task ClearMemberStateAsync(CacheKey key, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _pausedCache.Remove(key);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void InvalidateCache() => InvalidateResponseCache();

    public void InvalidateResponseCache()
    {
        ClearPausedCache();
        if (_inner is IAgentQuotaCacheInvalidator invalidator)
            invalidator.InvalidateResponseCache();
    }

    public void InvalidateCredentialState()
    {
        ClearPausedCache();
        if (_inner is IAgentQuotaCacheInvalidator invalidator)
            invalidator.InvalidateCredentialState();
    }

    public void InvalidateRecoveryState(AgentMembership member)
    {
        var key = CacheKey.For(member);
        _lock.Wait();
        try
        {
            _pausedCache.Remove(key);
        }
        finally
        {
            _lock.Release();
        }

        if (_inner is IAgentQuotaRecoveryStateInvalidator recoveryInvalidator)
        {
            recoveryInvalidator.InvalidateRecoveryState(member);
            return;
        }

        if (_inner is IAgentQuotaCacheInvalidator invalidator)
            invalidator.InvalidateResponseCache();
    }

    private void ClearPausedCache()
    {
        _lock.Wait();
        try
        {
            _pausedCache.Clear();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<bool> IsPausedAsync(AgentMembership member, CancellationToken ct)
    {
        var pause = await _pauses.GetAgentStateAsync(member.Agent, ct, member.InstanceId)
            .ConfigureAwait(false);
        return pause is not null;
    }

    private async Task<AgentQuotaSnapshot> FetchInnerOrTransientAsync(AgentMembership member, CancellationToken ct)
    {
        try
        {
            return await _inner.GetAvailabilityAsync(member, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "Quota probe {Kind} threw; treating as transient unknown", Kind.Value);
            return AgentQuotaSnapshot.UnknownSnapshot(
                QuotaUnknownReason.Transient, $"probe threw: {ex.GetType().Name}");
        }
    }

    private async Task<CacheEntry?> TryGetPausedCacheAsync(
        CacheKey key,
        PausedAgentQuotaProbeOptions options,
        CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            PruneExpiredPausedCache(options, now);
            if (_pausedCache.TryGetValue(key, out var cached)
                && IsCacheUsable(cached, options, now))
            {
                return cached;
            }

            _pausedCache.Remove(key);
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<KeyedProbeLock> AddPausedFetchWaiterAsync(CacheKey key, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_pausedFetchLocks.TryGetValue(key, out var fetchLock))
            {
                fetchLock = new KeyedProbeLock();
                _pausedFetchLocks[key] = fetchLock;
            }

            fetchLock.ReferenceCount++;
            return fetchLock;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task RemovePausedFetchWaiterAsync(CacheKey key, KeyedProbeLock fetchLock)
    {
        var shouldDispose = false;
        await _lock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            fetchLock.ReferenceCount--;
            if (fetchLock.ReferenceCount == 0
                && _pausedFetchLocks.TryGetValue(key, out var current)
                && ReferenceEquals(current, fetchLock))
            {
                _pausedFetchLocks.Remove(key);
                shouldDispose = true;
            }
        }
        finally
        {
            _lock.Release();
        }

        if (shouldDispose)
            fetchLock.Semaphore.Dispose();
    }

    private void StorePausedCache(
        CacheKey key,
        AgentQuotaSnapshot snapshot,
        PausedAgentQuotaProbeOptions options,
        DateTimeOffset now)
    {
        PruneExpiredPausedCache(options, now);
        if (!_pausedCache.ContainsKey(key) && _pausedCache.Count >= options.MaxCacheEntries)
            RemoveOldestPausedCacheEntry();

        _pausedCache[key] = new CacheEntry(snapshot, now);
    }

    private void PruneExpiredPausedCache(
        PausedAgentQuotaProbeOptions options,
        DateTimeOffset now)
    {
        foreach (var (key, entry) in _pausedCache.ToArray())
        {
            if (!IsCacheUsable(entry, options, now))
                _pausedCache.Remove(key);
        }
    }

    private void RemoveOldestPausedCacheEntry()
    {
        CacheKey? oldestKey = null;
        DateTimeOffset oldestCachedAt = DateTimeOffset.MaxValue;

        foreach (var (key, entry) in _pausedCache)
        {
            if (entry.CachedAt >= oldestCachedAt)
                continue;

            oldestKey = key;
            oldestCachedAt = entry.CachedAt;
        }

        if (oldestKey is { } keyToRemove)
            _pausedCache.Remove(keyToRemove);
    }

    private static bool IsCacheUsable(
        CacheEntry entry,
        PausedAgentQuotaProbeOptions options,
        DateTimeOffset now)
    {
        if (options.CacheTtl <= TimeSpan.Zero || now - entry.CachedAt > options.CacheTtl)
            return false;

        return true;
    }

    private async Task RemovePausedCacheAsync(CacheKey key, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _pausedCache.Remove(key);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static PausedAgentQuotaProbeOptions Validate(PausedAgentQuotaProbeOptions options)
    {
        if (options.CacheTtl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.CacheTtl,
                "Paused quota cache TTL must be positive.");

        if (options.MaxCacheEntries <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxCacheEntries,
                "Paused quota cache entry limit must be positive.");

        return options;
    }

    private readonly record struct CacheKey(string RouteKey, string ModelKey)
    {
        public static CacheKey For(AgentMembership member) =>
            new(member.RouteKey, string.IsNullOrWhiteSpace(member.ModelId) ? "" : member.ModelId!);
    }

    private readonly record struct CacheEntry(
        AgentQuotaSnapshot Snapshot,
        DateTimeOffset CachedAt);

    private sealed class KeyedProbeLock
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
    }

}

/// <summary>
/// Bounds for <see cref="PausedAgentQuotaProbe"/>. Read on every probe call so
/// values bound from <c>CodeyBox:QuotaRouter</c> hot-reload without restart.
/// </summary>
public sealed record PausedAgentQuotaProbeOptions
{
    /// <summary>How long a paused member's quota snapshot is cached.</summary>
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Maximum number of paused route/model cache entries retained in memory.</summary>
    public int MaxCacheEntries { get; init; } = 1024;
}
