using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that a plugin returning <see langword="null"/> from
/// <see cref="ICredentialProvider.GetAsync"/> causes the chain to fall through
/// to the next provider, matching the semantics required by vault-style plugins
/// that may not hold credentials for every agent.
/// </summary>
public sealed class CredentialPluginFallthroughTests
{
    [Fact]
    public async Task Plugin_ReturnsNull_ChainFallsThroughToNextProvider()
    {
        var expected = new AgentCredential(
            AgentKind.Claude,
            new Dictionary<string, string> { ["TOKEN"] = "fallback-token" },
            new Dictionary<string, string>());

        var pluginReturningNull = new FixedProvider(null);
        var fallbackProvider = new FixedProvider(expected);

        var chain = new ChainedCredentialProvider([pluginReturningNull, fallbackProvider]);

        var result = await chain.GetAsync(AgentKind.Claude);

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task AllPluginsReturnNull_ChainReturnsNull()
    {
        var chain = new ChainedCredentialProvider(
        [
            new FixedProvider(null),
            new FixedProvider(null),
            new FixedProvider(null),
        ]);

        var result = await chain.GetAsync(AgentKind.Claude);

        Assert.Null(result);
    }

    [Fact]
    public async Task FirstPluginReturnsCredential_SecondNotCalled()
    {
        var secondCalled = false;

        var firstProvider = new FixedProvider(new AgentCredential(
            AgentKind.Claude,
            new Dictionary<string, string> { ["TOKEN"] = "first" },
            new Dictionary<string, string>()));

        var secondProvider = new CallbackProvider(() => { secondCalled = true; return null; });

        var chain = new ChainedCredentialProvider([firstProvider, secondProvider]);

        await chain.GetAsync(AgentKind.Claude);

        Assert.False(secondCalled);
    }

    [Fact]
    public async Task Plugin_ReturnsNullForSomeAgents_ChainFallsThroughPerAgent()
    {
        // Simulate a vault plugin that only covers Claude; Codex falls through
        // to the env-var provider.
        var claudeCred = new AgentCredential(
            AgentKind.Claude,
            new Dictionary<string, string> { ["TOKEN"] = "vault-claude" },
            new Dictionary<string, string>());

        var codexCred = new AgentCredential(
            AgentKind.Codex,
            new Dictionary<string, string> { ["OPENAI_API_KEY"] = "env-codex" },
            new Dictionary<string, string>());

        var vaultPlugin = new AgentSelectiveProvider(AgentKind.Claude, claudeCred);
        var envProvider = new AgentSelectiveProvider(AgentKind.Codex, codexCred);

        var chain = new ChainedCredentialProvider([vaultPlugin, envProvider]);

        var claudeResult = await chain.GetAsync(AgentKind.Claude);
        var codexResult = await chain.GetAsync(AgentKind.Codex);

        Assert.NotNull(claudeResult);
        Assert.Equal("vault-claude", claudeResult!.EnvironmentVariables["TOKEN"]);

        Assert.NotNull(codexResult);
        Assert.Equal("env-codex", codexResult!.EnvironmentVariables["OPENAI_API_KEY"]);
    }

    [Fact]
    public async Task Plugin_ReturnsNull_ThenEnvProviderSucceeds_ChainReturnsEnvCredential()
    {
        // Mirrors the production chain: vault plugin has no creds for this agent,
        // falls through to env-var provider which always has something.
        var envCred = new AgentCredential(
            AgentKind.Gemini,
            new Dictionary<string, string> { ["GEMINI_API_KEY"] = "AIza-abc123" },
            new Dictionary<string, string>());

        var vaultPlugin = new FixedProvider(null);   // no Gemini creds in this vault
        var envProvider = new FixedProvider(envCred);

        var chain = new ChainedCredentialProvider([vaultPlugin, envProvider]);

        var result = await chain.GetAsync(AgentKind.Gemini);

        Assert.NotNull(result);
        Assert.Equal("AIza-abc123", result!.EnvironmentVariables["GEMINI_API_KEY"]);
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    private sealed class FixedProvider(AgentCredential? result) : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
            => Task.FromResult(result);
    }

    private sealed class CallbackProvider(Func<AgentCredential?> callback) : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
            => Task.FromResult(callback());
    }

    private sealed class AgentSelectiveProvider(AgentKind coveredAgent, AgentCredential cred)
        : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
            => Task.FromResult(agent == coveredAgent ? cred : (AgentCredential?)null);
    }
}
