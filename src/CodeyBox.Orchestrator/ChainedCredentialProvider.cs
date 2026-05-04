using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Tries each wrapped <see cref="ICredentialProvider"/> in order and returns
/// the first non-null credential. Lets operators stack a fresh-from-file
/// provider in front of the env-var provider so a file refresh on the host
/// is picked up without restarting the orchestrator.
///
/// <para>Chain order for the default DI registration is:
/// BUILT-IN-OAUTH → PLUGINS → BUILT-IN-ENV. Plugins go between the Claude-
/// specific OAuth-file provider and the catch-all env-var provider so vault
/// credentials are preferred over plain env vars but never override an
/// operator's explicitly configured OAuth token file.</para>
///
/// <para>Time-bound credentials: if a provider returns an
/// <see cref="AgentCredential"/> with <see cref="AgentCredential.ExpiresAt"/>
/// set, the chain caches that credential until the expiry instant and re-fetches
/// afterward. Credentials without an expiry (all built-in providers) bypass
/// the cache entirely — every call re-reads the underlying source.</para>
///
/// <para>Per-project priority: use
/// <see cref="GetAsync(AgentKind, IReadOnlyList{string}, CancellationToken)"/>
/// to apply <see cref="Project.CredentialProviderPriority"/> at pickup time.
/// The built-in-first and built-in-last providers are always included; only
/// the plugin segment is filtered and reordered.</para>
/// </summary>
public sealed class ChainedCredentialProvider : IProjectAwareCredentialProvider
{
    // Full chain: built-in-first + plugins (global order) + built-in-last.
    // Used by the global GetAsync(AgentKind, ct) path.
    private readonly IReadOnlyList<ICredentialProvider> _providers;

    // Segmented storage for per-project priority filtering.
    private readonly IReadOnlyList<ICredentialProvider> _builtInFirst;
    private readonly IReadOnlyList<(string Id, ICredentialProvider Provider)> _namedPlugins;
    private readonly IReadOnlyList<ICredentialProvider> _builtInLast;

    private readonly Func<DateTimeOffset> _utcNow;
    private readonly ILogger? _log;

    // Global-order cache keyed by AgentKind.
    private readonly Dictionary<AgentKind, (AgentCredential Credential, DateTimeOffset ExpiresAt)> _cache = new();

    // Per-priority cache keyed by (AgentKind, priority-fingerprint).
    private readonly Dictionary<(AgentKind, string), (AgentCredential Credential, DateTimeOffset ExpiresAt)> _priorityCache = new();

    private readonly object _cacheLock = new();

    // Per-agent SemaphoreSlim(1,1) to serialise concurrent refetches and prevent
    // stampede when a cached time-bound credential expires simultaneously.
    private readonly Dictionary<AgentKind, SemaphoreSlim> _fetchLocks = new();
    private readonly Dictionary<(AgentKind, string), SemaphoreSlim> _priorityFetchLocks = new();

    /// <summary>
    /// Simple ordered-list constructor. Used by unit tests and for chains that
    /// have no named plugin segment (priority filtering not supported).
    /// </summary>
    public ChainedCredentialProvider(
        IEnumerable<ICredentialProvider> providers,
        Func<DateTimeOffset>? utcNow = null)
    {
        _providers = providers.ToList();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _builtInFirst = [];
        _namedPlugins = [];
        _builtInLast = [];
    }

    /// <summary>
    /// Segmented constructor used by the production DI registration. Stores
    /// plugin providers WITH their IDs so per-project
    /// <see cref="Project.CredentialProviderPriority"/> can be applied via
    /// <see cref="GetAsync(AgentKind, IReadOnlyList{string}, CancellationToken)"/>.
    /// </summary>
    public ChainedCredentialProvider(
        IEnumerable<ICredentialProvider> builtInFirst,
        IReadOnlyList<(string Id, ICredentialProvider Provider)> namedPlugins,
        IEnumerable<ICredentialProvider> builtInLast,
        Func<DateTimeOffset>? utcNow = null,
        ILogger? log = null)
    {
        _builtInFirst = builtInFirst.ToList();
        _namedPlugins = namedPlugins;
        _builtInLast = builtInLast.ToList();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _log = log;
        // Build the global chain: all three segments in order.
        _providers = [.. _builtInFirst, .. _namedPlugins.Select(p => p.Provider), .. _builtInLast];
    }

    /// <summary>
    /// Global chain lookup — no per-project filtering. Used by smoke gates and
    /// startup validation which have no project context.
    /// </summary>
    public async Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
    {
        // Fast path: still-valid cached credential.
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(agent, out var cached) && cached.ExpiresAt > _utcNow())
                return cached.Credential;
        }

        // Serialise concurrent refetches for this agent kind to prevent stampede
        // when a cached time-bound credential expires simultaneously.
        var fetchLock = GetOrCreateFetchLock(agent);
        await fetchLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring the per-agent lock.
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(agent, out var cached) && cached.ExpiresAt > _utcNow())
                    return cached.Credential;
            }

            foreach (var p in _providers)
            {
                var cred = await p.GetAsync(agent, ct);
                if (cred is not null)
                {
                    if (cred.ExpiresAt.HasValue)
                    {
                        lock (_cacheLock)
                            _cache[agent] = (cred, cred.ExpiresAt.Value);
                    }
                    return cred;
                }
            }
            return null;
        }
        finally
        {
            fetchLock.Release();
        }
    }

    /// <summary>
    /// Per-project chain lookup. Applies <paramref name="credentialProviderPriority"/>
    /// to filter and reorder the plugin segment; built-in-first and built-in-last
    /// providers are always included unchanged. Falls back to global discovery
    /// order when <paramref name="credentialProviderPriority"/> is empty.
    /// </summary>
    public async Task<AgentCredential?> GetAsync(
        AgentKind agent,
        IReadOnlyList<string> credentialProviderPriority,
        CancellationToken ct = default)
    {
        // No named plugins or empty priority — global chain is correct.
        if (_namedPlugins.Count == 0 || credentialProviderPriority.Count == 0)
            return await GetAsync(agent, ct);

        var priorityKey = string.Join("\0", credentialProviderPriority);
        var cacheKey = (agent, priorityKey);

        // Fast path: still-valid priority-keyed cached credential.
        lock (_cacheLock)
        {
            if (_priorityCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > _utcNow())
                return cached.Credential;
        }

        // Serialise concurrent refetches for this (agent, priority) combination.
        var fetchLock = GetOrCreatePriorityFetchLock(cacheKey);
        await fetchLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring the lock.
            lock (_cacheLock)
            {
                if (_priorityCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > _utcNow())
                    return cached.Credential;
            }

            // Build the per-project chain: built-in-first + filtered/ordered plugins + built-in-last.
            var orderedPlugins = OrderByPriority(
                _namedPlugins,
                credentialProviderPriority,
                onMissing: id => _log?.LogWarning(
                    "Project credential priority lists unknown plugin ID '{PluginId}'; skipping", id));

            var chain = _builtInFirst
                .Concat(orderedPlugins.Select(p => p.Provider))
                .Concat(_builtInLast);

            foreach (var p in chain)
            {
                var cred = await p.GetAsync(agent, ct);
                if (cred is not null)
                {
                    if (cred.ExpiresAt.HasValue)
                    {
                        lock (_cacheLock)
                            _priorityCache[cacheKey] = (cred, cred.ExpiresAt.Value);
                    }
                    return cred;
                }
            }
            return null;
        }
        finally
        {
            fetchLock.Release();
        }
    }

    /// <summary>
    /// Reorders <paramref name="plugins"/> according to <paramref name="priority"/>.
    /// Only IDs present in <paramref name="priority"/> are included in the result;
    /// any installed plugin whose ID is absent from the list is excluded (this is
    /// how a project can say "use vault and aws-ssm but not 1password").
    ///
    /// <para>When <paramref name="priority"/> is empty, the original
    /// <paramref name="plugins"/> list is returned unchanged (global discovery
    /// order, all installed plugins included).</para>
    /// </summary>
    /// <param name="plugins">Plugin ID → provider pairs in discovery order.</param>
    /// <param name="priority">Ordered list of plugin IDs from
    ///   <see cref="Project.CredentialProviderPriority"/>.</param>
    /// <param name="onMissing">Called for each ID in <paramref name="priority"/>
    ///   that has no matching installed plugin. Use for warning logs.</param>
    public static IReadOnlyList<(string Id, ICredentialProvider Provider)> OrderByPriority(
        IReadOnlyList<(string Id, ICredentialProvider Provider)> plugins,
        IReadOnlyList<string> priority,
        Action<string>? onMissing = null)
    {
        if (priority.Count == 0)
            return plugins;

        var byId = plugins.ToDictionary(
            p => p.Id,
            p => p.Provider,
            StringComparer.OrdinalIgnoreCase);

        var ordered = new List<(string Id, ICredentialProvider Provider)>(priority.Count);
        foreach (var id in priority)
        {
            if (byId.TryGetValue(id, out var provider))
                ordered.Add((id, provider));
            else
                onMissing?.Invoke(id);
        }

        return ordered;
    }

    private SemaphoreSlim GetOrCreateFetchLock(AgentKind kind)
    {
        lock (_cacheLock)
            return _fetchLocks.TryGetValue(kind, out var s) ? s : (_fetchLocks[kind] = new SemaphoreSlim(1, 1));
    }

    private SemaphoreSlim GetOrCreatePriorityFetchLock((AgentKind, string) key)
    {
        lock (_cacheLock)
            return _priorityFetchLocks.TryGetValue(key, out var s) ? s : (_priorityFetchLocks[key] = new SemaphoreSlim(1, 1));
    }
}
