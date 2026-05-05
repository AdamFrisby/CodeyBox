using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that the orchestrator caches credentials whose
/// <see cref="AgentCredential.ExpiresAt"/> is set and re-fetches them after
/// the expiry instant. Credentials without an expiry are never cached.
/// </summary>
public sealed class CredentialPluginTimeBoundTests
{
    [Fact]
    public async Task TimeBoundCredential_CachedWithinExpiry()
    {
        var callCount = 0;
        var fakeNow = DateTimeOffset.UtcNow;

        var provider = new CountingProvider(() =>
        {
            callCount++;
            return new AgentCredential(
                AgentKind.Claude,
                new Dictionary<string, string> { ["TOKEN"] = $"token-{callCount}" },
                new Dictionary<string, string>())
            {
                ExpiresAt = fakeNow.AddHours(1),
            };
        });

        var chain = new ChainedCredentialProvider([provider], utcNow: () => fakeNow);

        var first = await chain.GetAsync(AgentKind.Claude);
        var second = await chain.GetAsync(AgentKind.Claude);

        // Provider called only once; second call served from cache.
        Assert.Equal(1, callCount);
        Assert.Equal("token-1", first!.EnvironmentVariables["TOKEN"]);
        Assert.Equal("token-1", second!.EnvironmentVariables["TOKEN"]);
    }

    [Fact]
    public async Task TimeBoundCredential_RefetchedAfterExpiry()
    {
        var callCount = 0;
        var fakeNow = DateTimeOffset.UtcNow;

        var provider = new CountingProvider(() =>
        {
            callCount++;
            return new AgentCredential(
                AgentKind.Claude,
                new Dictionary<string, string> { ["TOKEN"] = $"token-{callCount}" },
                new Dictionary<string, string>())
            {
                ExpiresAt = fakeNow.AddMinutes(5),
            };
        });

        var chain = new ChainedCredentialProvider([provider], utcNow: () => fakeNow);

        // First call — fetches and caches.
        var first = await chain.GetAsync(AgentKind.Claude);
        Assert.Equal(1, callCount);
        Assert.Equal("token-1", first!.EnvironmentVariables["TOKEN"]);

        // Advance time past the 5-minute expiry.
        fakeNow = fakeNow.AddMinutes(10);

        // Second call — cache expired, provider is called again.
        var second = await chain.GetAsync(AgentKind.Claude);
        Assert.Equal(2, callCount);
        Assert.Equal("token-2", second!.EnvironmentVariables["TOKEN"]);
    }

    [Fact]
    public async Task NonExpiringCredential_NotCached_ProviderCalledEveryTime()
    {
        // Credentials without ExpiresAt are never cached so live rotations
        // (e.g. OAuth-file token refresh) are picked up without a restart.
        var callCount = 0;

        var provider = new CountingProvider(() =>
        {
            callCount++;
            return new AgentCredential(
                AgentKind.Claude,
                new Dictionary<string, string> { ["TOKEN"] = $"token-{callCount}" },
                new Dictionary<string, string>());   // no ExpiresAt
        });

        var chain = new ChainedCredentialProvider([provider]);

        var first = await chain.GetAsync(AgentKind.Claude);
        var second = await chain.GetAsync(AgentKind.Claude);

        Assert.Equal(2, callCount);
        Assert.Equal("token-1", first!.EnvironmentVariables["TOKEN"]);
        Assert.Equal("token-2", second!.EnvironmentVariables["TOKEN"]);
    }

    [Fact]
    public async Task CacheIsScopedPerAgent()
    {
        var claudeCallCount = 0;
        var codexCallCount = 0;
        var fakeNow = DateTimeOffset.UtcNow;

        var provider = new DelegateProvider(agent =>
        {
            if (agent == AgentKind.Claude)
            {
                claudeCallCount++;
                return new AgentCredential(AgentKind.Claude,
                    new Dictionary<string, string>(), new Dictionary<string, string>())
                { ExpiresAt = fakeNow.AddHours(1) };
            }
            if (agent == AgentKind.Codex)
            {
                codexCallCount++;
                return new AgentCredential(AgentKind.Codex,
                    new Dictionary<string, string>(), new Dictionary<string, string>())
                { ExpiresAt = fakeNow.AddHours(1) };
            }
            return null;
        });

        var chain = new ChainedCredentialProvider([provider], utcNow: () => fakeNow);

        await chain.GetAsync(AgentKind.Claude);
        await chain.GetAsync(AgentKind.Claude);   // served from cache
        await chain.GetAsync(AgentKind.Codex);
        await chain.GetAsync(AgentKind.Codex);   // served from cache

        Assert.Equal(1, claudeCallCount);
        Assert.Equal(1, codexCallCount);
    }

    [Fact]
    public async Task ExpiredCachedEntry_Evicted_FreshCredentialStoredAfterRefetch()
    {
        var callCount = 0;
        var fakeNow = DateTimeOffset.UtcNow;

        var provider = new CountingProvider(() =>
        {
            callCount++;
            // Each refetch returns a fresh token valid for 5 more minutes.
            return new AgentCredential(
                AgentKind.Claude,
                new Dictionary<string, string> { ["TOKEN"] = $"token-{callCount}" },
                new Dictionary<string, string>())
            {
                ExpiresAt = fakeNow.AddMinutes(5),
            };
        });

        var chain = new ChainedCredentialProvider([provider], utcNow: () => fakeNow);

        await chain.GetAsync(AgentKind.Claude);              // call 1: caches token-1
        fakeNow = fakeNow.AddMinutes(10);                    // expire the cache
        var second = await chain.GetAsync(AgentKind.Claude); // call 2: refetches token-2
        var third = await chain.GetAsync(AgentKind.Claude);  // call 3: served from new cache

        Assert.Equal(2, callCount);
        Assert.Equal("token-2", second!.EnvironmentVariables["TOKEN"]);
        Assert.Equal("token-2", third!.EnvironmentVariables["TOKEN"]);
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    private sealed class CountingProvider(Func<AgentCredential?> factory) : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
            => Task.FromResult(factory());
    }

    private sealed class DelegateProvider(Func<AgentKind, AgentCredential?> factory) : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
            => Task.FromResult(factory(agent));
    }
}
