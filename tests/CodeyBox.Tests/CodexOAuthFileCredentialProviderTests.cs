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

    [Fact]
    public async Task GetAsync_ReadsCodexAuthJsonForCodex()
    {
        var authPath = Path.Combine(_workspace, "auth.json");
        var authJson = "{\"tokens\":{\"access_token\":\"test-token\"}}";
        await File.WriteAllTextAsync(authPath, authJson);
        var provider = new CodexOAuthFileCredentialProvider(authPath);

        var credential = await provider.GetAsync(AgentKind.Codex);

        Assert.NotNull(credential);
        Assert.Equal(AgentKind.Codex, credential!.Agent);
        Assert.Equal(authJson, credential.EnvironmentVariables["CODEX_AUTH_JSON"]);
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
        var fileProvider = new CodexOAuthFileCredentialProvider(Path.Combine(_workspace, "missing.json"));
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
        var provider = new CodexOAuthFileCredentialProvider(authPath);

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
                new CodexOAuthFileCredentialProvider(authPath),
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
