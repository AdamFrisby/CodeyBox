using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class CodexOAuthFileCredentialProviderTests : IDisposable
{
    private readonly string _workspace;

    public CodexOAuthFileCredentialProviderTests()
    {
        _workspace = Directory.CreateTempSubdirectory("codeybox-codex-oauth-").FullName;
    }

    public void Dispose()
    {
        Directory.Delete(_workspace, recursive: true);
    }

    private static CodexOAuthFileCredentialProvider NewFileProvider(string path)
        => new(path, watch: false);

    [Fact]
    public async Task PathConstructor_WithWatchFalse_DoesNotCreateWatcher()
    {
        var authPath = Path.Combine(_workspace, "auth.json");
        await File.WriteAllTextAsync(authPath, "{\"tokens\":{\"access_token\":\"test-token\"}}");

        using var provider = new CodexOAuthFileCredentialProvider(authPath, watch: false);

        Assert.False(TestFileSystemWatcherLeakTracker.IsTrackingPath(authPath));
    }

    [Fact]
    public async Task Dispose_ReleasesOwnedCredentialFileSourceWatcher()
    {
        var authPath = Path.Combine(_workspace, "auth.json");
        await File.WriteAllTextAsync(authPath, "{\"tokens\":{\"access_token\":\"test-token\"}}");
        var provider = new CodexOAuthFileCredentialProvider(authPath, watch: true);

        try
        {
            Assert.True(TestFileSystemWatcherLeakTracker.IsTrackingPath(authPath));
        }
        finally
        {
            provider.Dispose();
        }

        Assert.False(TestFileSystemWatcherLeakTracker.IsTrackingPath(authPath));
    }

    [Fact]
    public async Task Dispose_DoesNotDisposeExternallyOwnedCredentialFileSource()
    {
        var authPath = Path.Combine(_workspace, "auth.json");
        const string authJson = "{\"tokens\":{\"access_token\":\"test-token\"}}";
        await File.WriteAllTextAsync(authPath, authJson);
        using var source = new CredentialFileSource(authPath, watch: false);
        var provider = new CodexOAuthFileCredentialProvider(source);

        provider.Dispose();

        Assert.Equal(authJson, source.GetRaw());
    }

    [Fact]
    public async Task GetAsync_ReadsCodexAuthJsonForCodex()
    {
        var authPath = Path.Combine(_workspace, "auth.json");
        var authJson = "{\"tokens\":{\"access_token\":\"test-token\"}}";
        await File.WriteAllTextAsync(authPath, authJson);
        var provider = NewFileProvider(authPath);

        var credential = await provider.GetAsync(AgentKind.Codex);

        Assert.NotNull(credential);
        Assert.Equal(AgentKind.Codex, credential!.Agent);
        Assert.Equal(authJson, credential.EnvironmentVariables["CODEX_AUTH_JSON"]);
    }

    [Fact]
    public async Task GetAsync_DoesNotEmitHostCredentialMount()
    {
        // The provider must not bind-mount the host .codex directory into an
        // untrusted agent sandbox. CodexAgentRunner materialises a private
        // auth.json snapshot from CODEX_AUTH_JSON instead.
        var authPath = Path.Combine(_workspace, "auth.json");
        await File.WriteAllTextAsync(authPath, "{\"tokens\":{\"access_token\":\"t\"}}");
        var provider = NewFileProvider(authPath);

        var credential = await provider.GetAsync(AgentKind.Codex);

        Assert.NotNull(credential);
        Assert.Empty(credential!.Mounts);
    }

    [Fact]
    public async Task GetAsync_WhenHostDirMissing_OmitsBindMountButStillReturnsEnvVarCredential()
    {
        // Exercise the stale-cache fallback: if the source has a valid cached
        // auth.json but the host directory later disappears, the provider can
        // still return the env-var credential without fabricating any mount.
        var hostDir = Path.Combine(_workspace, "deleted-codex");
        Directory.CreateDirectory(hostDir);
        var authPath = Path.Combine(hostDir, "auth.json");
        await File.WriteAllTextAsync(authPath, "{\"tokens\":{\"access_token\":\"t\"}}");
        using var source = new CredentialFileSource(authPath, watch: false);
        var provider = new CodexOAuthFileCredentialProvider(source);

        Directory.Delete(hostDir, recursive: true);

        var credential = await provider.GetAsync(AgentKind.Codex);

        Assert.NotNull(credential);
        Assert.NotEmpty(credential!.EnvironmentVariables["CODEX_AUTH_JSON"]);
        Assert.Empty(credential.Mounts);
    }

    [Fact]
    public async Task GetAsync_EnvVarProviderDoesNotEmitMount()
    {
        // The env-var provider has no host file to mount, so it must not emit
        // a Mounts entry. The CodexAgentRunner's exec-time guard then falls
        // back to materialising from CODEX_AUTH_JSON via the snapshot write.
        const string envVar = "CODEYBOX_TEST_CODEX_AUTH_JSON";
        var original = Environment.GetEnvironmentVariable(envVar);
        Environment.SetEnvironmentVariable(envVar, "{\"tokens\":{\"access_token\":\"t\"}}");
        try
        {
            var provider = new CodexAuthJsonEnvironmentCredentialProvider(envVar);
            var credential = await provider.GetAsync(AgentKind.Codex);
            Assert.NotNull(credential);
            Assert.Empty(credential!.Mounts);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, original);
        }
    }

    [Fact]
    public async Task GetAsync_ReadsCodexAuthJsonFromEnvironmentForCodex()
    {
        const string envVar = "CODEYBOX_TEST_CODEX_AUTH_JSON";
        var original = Environment.GetEnvironmentVariable(envVar);
        var authJson = "{\"tokens\":{\"access_token\":\"env-token\"}}";
        Environment.SetEnvironmentVariable(envVar, authJson);
        try
        {
            var provider = new CodexAuthJsonEnvironmentCredentialProvider(envVar);

            var credential = await provider.GetAsync(AgentKind.Codex);

            Assert.NotNull(credential);
            Assert.Equal(AgentKind.Codex, credential!.Agent);
            Assert.Equal(authJson, credential.EnvironmentVariables["CODEX_AUTH_JSON"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, original);
        }
    }

    [Fact]
    public async Task GetAsync_WhenCodexAuthJsonEnvironmentMalformed_FallsThroughToNextProvider()
    {
        const string envVar = "CODEYBOX_TEST_CODEX_AUTH_JSON";
        var original = Environment.GetEnvironmentVariable(envVar);
        Environment.SetEnvironmentVariable(envVar, "not json");
        try
        {
            var envAuthProvider = new CodexAuthJsonEnvironmentCredentialProvider(envVar);
            var apiCredential = new AgentCredential(
                AgentKind.Codex,
                new Dictionary<string, string> { ["OPENAI_API_KEY"] = "api-key" },
                new Dictionary<string, string>());
            var chain = new ChainedCredentialProvider([envAuthProvider, new FixedProvider(apiCredential)]);

            var credential = await chain.GetAsync(AgentKind.Codex);

            Assert.NotNull(credential);
            Assert.Equal("api-key", credential!.EnvironmentVariables["OPENAI_API_KEY"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, original);
        }
    }

    [Fact]
    public async Task GetAsync_WhenFileMissing_FallsThroughToNextProvider()
    {
        var fileProvider = NewFileProvider(Path.Combine(_workspace, "missing.json"));
        var envCredential = new AgentCredential(
            AgentKind.Codex,
            new Dictionary<string, string> { ["OPENAI_API_KEY"] = "env-key" },
            new Dictionary<string, string>());
        var chain = new ChainedCredentialProvider([fileProvider, new FixedProvider(envCredential)]);

        var credential = await chain.GetAsync(AgentKind.Codex);

        Assert.NotNull(credential);
        Assert.Equal("env-key", credential!.EnvironmentVariables["OPENAI_API_KEY"]);
    }

    [Fact]
    public async Task GetAsync_ReturnsNullForOtherAgents()
    {
        var authPath = Path.Combine(_workspace, "auth.json");
        await File.WriteAllTextAsync(authPath, "{\"tokens\":{\"access_token\":\"test-token\"}}");
        var provider = NewFileProvider(authPath);

        var credential = await provider.GetAsync(AgentKind.Claude);

        Assert.Null(credential);
    }

    [Fact]
    public async Task ChainPrefersCodexAuthJsonEnvironmentOverFile()
    {
        const string envVar = "CODEYBOX_TEST_CODEX_AUTH_JSON";
        var original = Environment.GetEnvironmentVariable(envVar);
        var envAuthJson = "{\"tokens\":{\"access_token\":\"env-token\"}}";
        var fileAuthJson = "{\"tokens\":{\"access_token\":\"file-token\"}}";
        Environment.SetEnvironmentVariable(envVar, envAuthJson);
        try
        {
            var authPath = Path.Combine(_workspace, "auth.json");
            await File.WriteAllTextAsync(authPath, fileAuthJson);
            var chain = new ChainedCredentialProvider([
                new CodexAuthJsonEnvironmentCredentialProvider(envVar),
                NewFileProvider(authPath),
            ]);

            var credential = await chain.GetAsync(AgentKind.Codex);

            Assert.NotNull(credential);
            Assert.Equal(envAuthJson, credential!.EnvironmentVariables["CODEX_AUTH_JSON"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, original);
        }
    }

    [Fact]
    public async Task ChainFallsBackToConventionalOpenAiApiKeyForCodex()
    {
        const string namespacedVar = "CODEYBOX_TEST_CODEX_API_KEY";
        const string conventionalVar = "CODEYBOX_TEST_OPENAI_API_KEY";
        var originalNamespaced = Environment.GetEnvironmentVariable(namespacedVar);
        var originalConventional = Environment.GetEnvironmentVariable(conventionalVar);
        Environment.SetEnvironmentVariable(namespacedVar, null);
        Environment.SetEnvironmentVariable(conventionalVar, "direct-openai-key");
        try
        {
            var chain = new ChainedCredentialProvider([
                new EnvironmentCredentialProvider([
                    new AgentCredentialMapping(AgentKind.Codex, namespacedVar, "OPENAI_API_KEY"),
                ]),
                new EnvironmentCredentialProvider([
                    new AgentCredentialMapping(AgentKind.Codex, conventionalVar, "OPENAI_API_KEY"),
                ]),
            ]);

            var credential = await chain.GetAsync(AgentKind.Codex);

            Assert.NotNull(credential);
            Assert.Equal("direct-openai-key", credential!.EnvironmentVariables["OPENAI_API_KEY"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(namespacedVar, originalNamespaced);
            Environment.SetEnvironmentVariable(conventionalVar, originalConventional);
        }
    }

    private sealed class FixedProvider(AgentCredential credential) : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
            => Task.FromResult<AgentCredential?>(credential);
    }
}
