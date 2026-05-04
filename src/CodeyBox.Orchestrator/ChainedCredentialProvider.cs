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
/// </summary>
public sealed class ChainedCredentialProvider : ICredentialProvider
{
    private readonly IReadOnlyList<ICredentialProvider> _providers;
    private readonly Func<DateTimeOffset> _utcNow;

    private readonly Dictionary<AgentKind, (AgentCredential Credential, DateTimeOffset ExpiresAt)> _cache = new();
    private readonly object _cacheLock = new();

    public ChainedCredentialProvider(
        IEnumerable<ICredentialProvider> providers,
        Func<DateTimeOffset>? utcNow = null)
    {
        _providers = providers.ToList();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
    {
        // Return a still-valid cached time-bound credential without hitting providers.
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
                // Only cache credentials that declare an expiry. Non-expiring
                // credentials (all built-in providers) flow through on every call
                // so live rotations (e.g. OAuth-file token refresh) propagate
                // without an orchestrator restart.
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
    ///   <see cref="CodeyBox.Core.Project.CredentialProviderPriority"/>.</param>
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
}
