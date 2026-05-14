using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests.Uat.AgentRunnersAndCredentials;

/// <summary>
/// UAT coverage for <c>Credential provider chain - Resolves built-in, plugin, and environment credentials per project</c>.
/// Plan anchor: docs/uat/00-plan.md#credential-provider-chain---resolves-built-in-plugin-and-environment-credentials-per-project
/// </summary>
public sealed class CredentialProviderChainUatTests
{
    [Fact]
    public async Task ProjectPriority_FiltersAndOrdersPluginSegmentBetweenBuiltInProviders()
    {
        var calls = new List<string>();
        var pluginCredential = Credential(AgentKind.Claude, "PLUGIN_VALUE", "plugin");
        IProjectAwareCredentialProvider chain = new ChainedCredentialProvider(
            builtInFirst: [new OrderedCredentialProvider("built-in-first", calls)],
            namedPlugins:
            [
                ("vault-a", new OrderedCredentialProvider("vault-a", calls)),
                ("vault-b", new OrderedCredentialProvider("vault-b", calls, pluginCredential)),
            ],
            builtInLast: [new OrderedCredentialProvider("built-in-last", calls)]);

        var result = await chain.GetAsync(AgentKind.Claude, ["vault-b", "vault-a"]);

        Assert.Same(pluginCredential, result);
        Assert.Equal(["built-in-first", "vault-b"], calls);
    }

    [Fact]
    public async Task PluginFallthrough_ReachesEnvironmentCredentialProvider()
    {
        var hostEnv = "CODEYBOX_UAT_CLAUDE_VALUE";
        var original = Environment.GetEnvironmentVariable(hostEnv);
        Environment.SetEnvironmentVariable(hostEnv, "uat-env-value");
        try
        {
            var calls = new List<string>();
            var envProvider = new EnvironmentCredentialProvider(
            [
                new AgentCredentialMapping(AgentKind.Claude, hostEnv, "ANTHROPIC_API_KEY"),
            ]);
            IProjectAwareCredentialProvider chain = new ChainedCredentialProvider(
                builtInFirst: [],
                namedPlugins: [("empty-plugin", new OrderedCredentialProvider("empty-plugin", calls))],
                builtInLast: [envProvider]);

            var result = await chain.GetAsync(AgentKind.Claude, ["empty-plugin"]);

            Assert.Equal(["empty-plugin"], calls);
            Assert.NotNull(result);
            Assert.Equal("uat-env-value", result!.EnvironmentVariables["ANTHROPIC_API_KEY"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(hostEnv, original);
        }
    }

    [Fact]
    public async Task TimeBoundCredentials_AreCachedUntilExpiryThenRefetched()
    {
        var calls = new List<string>();
        var now = new DateTimeOffset(2026, 5, 14, 0, 0, 0, TimeSpan.Zero);
        var first = Credential(AgentKind.Codex, "OPENAI_API_KEY", "first") with
        {
            ExpiresAt = now.AddMinutes(5),
        };
        var second = Credential(AgentKind.Codex, "OPENAI_API_KEY", "second") with
        {
            ExpiresAt = now.AddMinutes(10),
        };
        var provider = new RotatingCredentialProvider("vault", calls, first, second);
        var chain = new ChainedCredentialProvider([provider], utcNow: () => now);

        var beforeExpiryA = await chain.GetAsync(AgentKind.Codex);
        var beforeExpiryB = await chain.GetAsync(AgentKind.Codex);
        now = now.AddMinutes(6);
        var afterExpiry = await chain.GetAsync(AgentKind.Codex);

        Assert.Same(first, beforeExpiryA);
        Assert.Same(first, beforeExpiryB);
        Assert.Same(second, afterExpiry);
        Assert.Equal(["vault", "vault"], calls);
    }

    private static AgentCredential Credential(AgentKind kind, string envName, string value)
        => new(kind, new Dictionary<string, string> { [envName] = value }, new Dictionary<string, string>());

    private sealed class RotatingCredentialProvider(
        string id,
        List<string> calls,
        params AgentCredential[] credentials) : ICredentialProvider
    {
        private int _index;

        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
        {
            _ = agent;
            _ = ct;
            calls.Add(id);
            var credential = credentials[Math.Min(_index, credentials.Length - 1)];
            _index++;
            return Task.FromResult<AgentCredential?>(credential);
        }
    }
}
