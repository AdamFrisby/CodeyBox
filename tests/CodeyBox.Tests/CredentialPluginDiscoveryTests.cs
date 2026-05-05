using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that plugin assemblies implementing <see cref="ICredentialProvider"/>
/// are discovered and registered by the plugin foundation, and that the chain
/// built from loaded plugins places them in the BUILT-IN-OAUTH → PLUGINS →
/// BUILT-IN-ENV order.
/// </summary>
public sealed class CredentialPluginDiscoveryTests
{
    private static PluginLoader MakeLoader(PluginOptions opts)
        => new(opts, new ConfigurationBuilder().Build(), NullLogger<PluginLoader>.Instance);

    [Fact]
    public async Task RegisterPlugins_PluginImplementingICredentialProvider_IsRegisteredUnderInterface()
    {
        // Arrange: fake plugin in-process (no ALC needed for type identity test)
        var plugin = new LoadedPlugin(
            PluginId: "test.fake-creds",
            DisplayName: "Fake Creds",
            AssemblyPath: "/fake.dll",
            RegisteredTypes: [typeof(FakeCredentialProvider)]);

        var loader = MakeLoader(new PluginOptions { Allowlist = ["*"] });
        var services = new ServiceCollection();
        loader.RegisterPlugins(services, [plugin]);

        // Act
        await using var sp = services.BuildServiceProvider();
        var resolved = sp.GetServices<ICredentialProvider>().ToList();

        // ICredentialProvider is no longer blocked — the plugin is registered.
        Assert.Single(resolved);
        Assert.IsType<FakeCredentialProvider>(resolved[0]);
    }

    [Fact]
    public async Task RegisterPlugins_MultipleCredentialPlugins_AllRegistered()
    {
        var pluginA = new LoadedPlugin("test.creds-a", "A", "/a.dll", [typeof(FakeCredentialProvider)]);
        var pluginB = new LoadedPlugin("test.creds-b", "B", "/b.dll", [typeof(FakeCredentialProvider2)]);

        var loader = MakeLoader(new PluginOptions { Allowlist = ["*"] });
        var services = new ServiceCollection();
        loader.RegisterPlugins(services, [pluginA, pluginB]);

        await using var sp = services.BuildServiceProvider();
        var resolved = sp.GetServices<ICredentialProvider>().ToList();

        Assert.Equal(2, resolved.Count);
    }

    [Fact]
    public async Task Chain_PluginInsertedBetweenOAuthFileAndEnvVar()
    {
        // Arrange: simulate the BUILT-IN-OAUTH → PLUGINS → BUILT-IN-ENV order.
        // OAuth returns null for Claude (e.g. file absent), plugin also returns
        // null, env provider returns the credential — verifying the order.
        var callLog = new List<string>();

        var oauthProvider = new NamedReturnProvider("oauth", callLog, result: null);
        var pluginProvider = new NamedReturnProvider("plugin", callLog, result: null);
        var envProvider = new NamedReturnProvider("env", callLog,
            result: new AgentCredential(
                AgentKind.Claude,
                new Dictionary<string, string> { ["CLAUDE_CODE_OAUTH_TOKEN"] = "env-token" },
                new Dictionary<string, string>()));

        var chain = new ChainedCredentialProvider([oauthProvider, pluginProvider, envProvider]);

        // Act
        var cred = await chain.GetAsync(AgentKind.Claude);

        // Assert: providers tried in declaration order
        Assert.Equal(["oauth", "plugin", "env"], callLog);
        Assert.NotNull(cred);
        Assert.Equal("env-token", cred!.EnvironmentVariables["CLAUDE_CODE_OAUTH_TOKEN"]);
    }

    [Fact]
    public async Task Chain_OAuthReturnsValue_PluginAndEnvNotTried()
    {
        var callLog = new List<string>();

        var oauthProvider = new NamedReturnProvider("oauth", callLog,
            result: new AgentCredential(AgentKind.Claude,
                new Dictionary<string, string> { ["CLAUDE_CODE_OAUTH_TOKEN"] = "oauth-token" },
                new Dictionary<string, string>()));
        var pluginProvider = new NamedReturnProvider("plugin", callLog, result: null);
        var envProvider = new NamedReturnProvider("env", callLog, result: null);

        var chain = new ChainedCredentialProvider([oauthProvider, pluginProvider, envProvider]);

        var cred = await chain.GetAsync(AgentKind.Claude);

        // Only the first provider (oauth) should have been called.
        Assert.Equal(["oauth"], callLog);
        Assert.NotNull(cred);
        Assert.Equal("oauth-token", cred!.EnvironmentVariables["CLAUDE_CODE_OAUTH_TOKEN"]);
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    [CodeyBoxPlugin(id: "test.fake-creds", displayName: "Fake Creds")]
    private sealed class FakeCredentialProvider : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
            => Task.FromResult<AgentCredential?>(null);
    }

    [CodeyBoxPlugin(id: "test.fake-creds-2", displayName: "Fake Creds 2")]
    private sealed class FakeCredentialProvider2 : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
            => Task.FromResult<AgentCredential?>(null);
    }

    private sealed class NamedReturnProvider(string name, List<string> log, AgentCredential? result)
        : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
        {
            log.Add(name);
            return Task.FromResult(result);
        }
    }
}
