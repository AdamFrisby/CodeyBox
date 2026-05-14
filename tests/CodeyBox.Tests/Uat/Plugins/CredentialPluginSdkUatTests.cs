using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests.Uat.Plugins;

/// <summary>
/// UAT coverage for <c>Credential provider plugin SDK - Allows external secret sources</c>.
/// Plan anchor: docs/uat/00-plan.md#plugins
/// </summary>
public sealed class CredentialPluginSdkUatTests
{
    [Fact]
    public async Task ProjectPriority_FiltersAndOrdersCredentialPluginsBetweenBuiltIns()
    {
        var calls = new List<string>();
        var selectedCredential = Credential(AgentKind.Claude, "CLAUDE_CODE_OAUTH_TOKEN", "vault-b-token");
        IProjectAwareCredentialProvider chain = new ChainedCredentialProvider(
            builtInFirst: [new TrackingCredentialProvider("oauth-file", calls)],
            namedPlugins:
            [
                ("vault-a", new TrackingCredentialProvider("vault-a", calls)),
                ("vault-b", new TrackingCredentialProvider("vault-b", calls, selectedCredential)),
                ("vault-c", new TrackingCredentialProvider("vault-c", calls)),
            ],
            builtInLast: [new TrackingCredentialProvider("env", calls)]);

        var result = await chain.GetAsync(AgentKind.Claude, ["vault-b", "vault-a"]);

        Assert.Same(selectedCredential, result);
        Assert.Equal(["oauth-file", "vault-b"], calls);
    }

    [Fact]
    public async Task PluginReturnsNullForAgent_ChainFallsThroughToEnvironmentFallback()
    {
        var calls = new List<string>();
        var fallback = Credential(AgentKind.Codex, "OPENAI_API_KEY", "env-token");
        IProjectAwareCredentialProvider chain = new ChainedCredentialProvider(
            builtInFirst: [],
            namedPlugins: [("vault", new TrackingCredentialProvider("vault", calls))],
            builtInLast: [new TrackingCredentialProvider("env", calls, fallback)]);

        var result = await chain.GetAsync(AgentKind.Codex, ["vault"]);

        Assert.Same(fallback, result);
        Assert.Equal(["vault", "env"], calls);
    }

    [Fact]
    public async Task TimeBoundCredential_IsCachedUntilExpiryThenRefetched()
    {
        var calls = new List<string>();
        var now = new DateTimeOffset(2026, 5, 14, 0, 0, 0, TimeSpan.Zero);
        var first = Credential(AgentKind.Gemini, "GEMINI_API_KEY", "first") with
        {
            ExpiresAt = now.AddMinutes(10),
        };
        var second = Credential(AgentKind.Gemini, "GEMINI_API_KEY", "second") with
        {
            ExpiresAt = now.AddMinutes(20),
        };
        var provider = new RotatingCredentialProvider("vault", calls, first, second);
        var chain = new ChainedCredentialProvider([provider], utcNow: () => now);

        var cachedA = await chain.GetAsync(AgentKind.Gemini);
        var cachedB = await chain.GetAsync(AgentKind.Gemini);
        now = now.AddMinutes(11);
        var refreshed = await chain.GetAsync(AgentKind.Gemini);

        Assert.Same(first, cachedA);
        Assert.Same(first, cachedB);
        Assert.Same(second, refreshed);
        Assert.Equal(["vault", "vault"], calls);
    }

    [Fact]
    public async Task BackendFailure_IsSurfacedDeterministicallyWithoutCallingFallback()
    {
        var calls = new List<string>();
        var chain = new ChainedCredentialProvider(
        [
            new ThrowingCredentialProvider("vault", calls),
            new TrackingCredentialProvider("env", calls, Credential(AgentKind.Claude, "TOKEN", "fallback")),
        ]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => chain.GetAsync(AgentKind.Claude));

        Assert.Equal("vault unavailable", ex.Message);
        Assert.Equal(["vault"], calls);
    }

    private static AgentCredential Credential(AgentKind agent, string envName, string value)
        => new(agent, new Dictionary<string, string> { [envName] = value }, new Dictionary<string, string>());

    private sealed class TrackingCredentialProvider(
        string name,
        List<string> calls,
        AgentCredential? credential = null) : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
        {
            calls.Add(name);
            return Task.FromResult(credential);
        }
    }

    private sealed class RotatingCredentialProvider(
        string name,
        List<string> calls,
        params AgentCredential[] credentials) : ICredentialProvider
    {
        private int _index;

        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
        {
            calls.Add(name);
            var credential = credentials[Math.Min(_index, credentials.Length - 1)];
            _index++;
            return Task.FromResult<AgentCredential?>(credential);
        }
    }

    private sealed class ThrowingCredentialProvider(string name, List<string> calls) : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
        {
            calls.Add(name);
            throw new InvalidOperationException("vault unavailable");
        }
    }
}
