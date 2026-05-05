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

    private sealed class FixedProvider(AgentCredential credential) : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
            => Task.FromResult<AgentCredential?>(credential);
    }
}
