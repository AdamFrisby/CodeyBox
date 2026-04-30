using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class ClaudeOAuthFileCredentialProviderTests : IDisposable
{
    private readonly string _tempDir;

    public ClaudeOAuthFileCredentialProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "codeybox-oauth-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string WriteCredFile(string content)
    {
        var path = Path.Combine(_tempDir, ".credentials.json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task ReturnsTokenForClaudeWhenFilePresent()
    {
        var path = WriteCredFile("""{"claudeAiOauth":{"accessToken":"sk-ant-oat01-abc"}}""");
        var p = new ClaudeOAuthFileCredentialProvider(path, "CLAUDE_CODE_OAUTH_TOKEN");

        var cred = await p.GetAsync(AgentKind.Claude);

        Assert.NotNull(cred);
        Assert.Equal("sk-ant-oat01-abc", cred!.EnvironmentVariables["CLAUDE_CODE_OAUTH_TOKEN"]);
    }

    [Fact]
    public async Task RereadsTokenOnEachCall()
    {
        var path = WriteCredFile("""{"claudeAiOauth":{"accessToken":"first"}}""");
        var p = new ClaudeOAuthFileCredentialProvider(path, "CLAUDE_CODE_OAUTH_TOKEN");

        var cred1 = await p.GetAsync(AgentKind.Claude);
        File.WriteAllText(path, """{"claudeAiOauth":{"accessToken":"second"}}""");
        var cred2 = await p.GetAsync(AgentKind.Claude);

        Assert.Equal("first", cred1!.EnvironmentVariables["CLAUDE_CODE_OAUTH_TOKEN"]);
        Assert.Equal("second", cred2!.EnvironmentVariables["CLAUDE_CODE_OAUTH_TOKEN"]);
    }

    [Fact]
    public async Task ReturnsNullForNonClaudeAgents()
    {
        var path = WriteCredFile("""{"claudeAiOauth":{"accessToken":"x"}}""");
        var p = new ClaudeOAuthFileCredentialProvider(path, "CLAUDE_CODE_OAUTH_TOKEN");

        Assert.Null(await p.GetAsync(AgentKind.Codex));
        Assert.Null(await p.GetAsync(AgentKind.Copilot));
    }

    [Fact]
    public async Task ReturnsNullWhenFileMissing()
    {
        var p = new ClaudeOAuthFileCredentialProvider(
            Path.Combine(_tempDir, "nonexistent.json"), "CLAUDE_CODE_OAUTH_TOKEN");

        Assert.Null(await p.GetAsync(AgentKind.Claude));
    }

    [Fact]
    public async Task ReturnsNullWhenJsonMalformed()
    {
        var path = WriteCredFile("not valid json");
        var p = new ClaudeOAuthFileCredentialProvider(path, "CLAUDE_CODE_OAUTH_TOKEN");

        Assert.Null(await p.GetAsync(AgentKind.Claude));
    }

    [Fact]
    public async Task ReturnsNullWhenAccessTokenFieldAbsent()
    {
        var path = WriteCredFile("""{"claudeAiOauth":{"otherField":"x"}}""");
        var p = new ClaudeOAuthFileCredentialProvider(path, "CLAUDE_CODE_OAUTH_TOKEN");

        Assert.Null(await p.GetAsync(AgentKind.Claude));
    }

    [Fact]
    public async Task ChainFallsThroughToEnvWhenFileAbsent()
    {
        Environment.SetEnvironmentVariable("CODEYBOX_CHAINED_TEST_KEY", "from-env");
        try
        {
            var fileProvider = new ClaudeOAuthFileCredentialProvider(
                Path.Combine(_tempDir, "missing.json"), "CLAUDE_CODE_OAUTH_TOKEN");
            var envProvider = new EnvironmentCredentialProvider(new[]
            {
                new AgentCredentialMapping(AgentKind.Claude, "CODEYBOX_CHAINED_TEST_KEY", "CLAUDE_CODE_OAUTH_TOKEN"),
            });
            var chain = new ChainedCredentialProvider(new ICredentialProvider[] { fileProvider, envProvider });

            var cred = await chain.GetAsync(AgentKind.Claude);

            Assert.NotNull(cred);
            Assert.Equal("from-env", cred!.EnvironmentVariables["CLAUDE_CODE_OAUTH_TOKEN"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CHAINED_TEST_KEY", null);
        }
    }

    [Fact]
    public async Task ChainPrefersFileOverEnv()
    {
        Environment.SetEnvironmentVariable("CODEYBOX_CHAINED_TEST_KEY", "stale-env");
        try
        {
            var path = WriteCredFile("""{"claudeAiOauth":{"accessToken":"fresh-file"}}""");
            var fileProvider = new ClaudeOAuthFileCredentialProvider(path, "CLAUDE_CODE_OAUTH_TOKEN");
            var envProvider = new EnvironmentCredentialProvider(new[]
            {
                new AgentCredentialMapping(AgentKind.Claude, "CODEYBOX_CHAINED_TEST_KEY", "CLAUDE_CODE_OAUTH_TOKEN"),
            });
            var chain = new ChainedCredentialProvider(new ICredentialProvider[] { fileProvider, envProvider });

            var cred = await chain.GetAsync(AgentKind.Claude);

            Assert.Equal("fresh-file", cred!.EnvironmentVariables["CLAUDE_CODE_OAUTH_TOKEN"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CHAINED_TEST_KEY", null);
        }
    }
}
