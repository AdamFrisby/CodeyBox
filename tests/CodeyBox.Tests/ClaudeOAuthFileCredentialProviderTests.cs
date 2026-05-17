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
    public async Task ShipsFullOAuthJsonBundleForInVmRefresh()
    {
        // The in-VM claude CLI needs the full credentials file (refresh_token
        // included) to auto-rotate when the host's access_token expires
        // mid-run. ClaudeAgentRunner materialises this env var back to
        // ~/.claude/.credentials.json inside the sandbox.
        const string raw =
            """{"claudeAiOauth":{"accessToken":"sk-ant-oat01-abc","refreshToken":"rt-xyz","expiresAt":1234567890}}""";
        var path = WriteCredFile(raw);
        var p = new ClaudeOAuthFileCredentialProvider(path, "CLAUDE_CODE_OAUTH_TOKEN");

        var cred = await p.GetAsync(AgentKind.Claude);

        Assert.NotNull(cred);
        Assert.Equal(raw, cred!.EnvironmentVariables[ClaudeOAuthFileCredentialProvider.OAuthJsonEnvVar]);
        Assert.Equal("sk-ant-oat01-abc", cred.EnvironmentVariables["CLAUDE_CODE_OAUTH_TOKEN"]);
    }

    [Fact]
    public async Task OAuthJsonEnvVarConstantMatchesGeminiNamingConvention()
    {
        // Sanity check the env-var name is the one ClaudeAgentRunner reads.
        Assert.Equal("CODEYBOX_CLAUDE_OAUTH_JSON", ClaudeOAuthFileCredentialProvider.OAuthJsonEnvVar);
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

public sealed class ClaudeEnvironmentCredentialProviderTests : IDisposable
{
    private const string CodeyBoxEnv = "CODEYBOX_TEST_CLAUDE_API_KEY";
    private const string AnthropicEnv = "ANTHROPIC_TEST_API_KEY";

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(CodeyBoxEnv, null);
        Environment.SetEnvironmentVariable(AnthropicEnv, null);
    }

    [Fact]
    public async Task CodeyBoxApiKey_IsExposedAsAnthropicApiKey()
    {
        Environment.SetEnvironmentVariable(CodeyBoxEnv, "sk-ant-api03-test");
        var provider = new ClaudeEnvironmentCredentialProvider(CodeyBoxEnv, AnthropicEnv);

        var cred = await provider.GetAsync(AgentKind.Claude);

        Assert.NotNull(cred);
        Assert.Equal("sk-ant-api03-test", cred!.EnvironmentVariables["ANTHROPIC_API_KEY"]);
        Assert.DoesNotContain("CLAUDE_CODE_OAUTH_TOKEN", cred.EnvironmentVariables.Keys);
    }

    [Fact]
    public async Task CodeyBoxOAuthToken_IsExposedAsClaudeOAuthToken()
    {
        Environment.SetEnvironmentVariable(CodeyBoxEnv, "sk-ant-oat01-test");
        var provider = new ClaudeEnvironmentCredentialProvider(CodeyBoxEnv, AnthropicEnv);

        var cred = await provider.GetAsync(AgentKind.Claude);

        Assert.NotNull(cred);
        Assert.Equal("sk-ant-oat01-test", cred!.EnvironmentVariables["CLAUDE_CODE_OAUTH_TOKEN"]);
        Assert.DoesNotContain("ANTHROPIC_API_KEY", cred.EnvironmentVariables.Keys);
    }

    [Fact]
    public async Task ConventionalAnthropicApiKey_IsAccepted()
    {
        Environment.SetEnvironmentVariable(AnthropicEnv, "sk-ant-api03-direct");
        var provider = new ClaudeEnvironmentCredentialProvider(CodeyBoxEnv, AnthropicEnv);

        var cred = await provider.GetAsync(AgentKind.Claude);

        Assert.NotNull(cred);
        Assert.Equal("sk-ant-api03-direct", cred!.EnvironmentVariables["ANTHROPIC_API_KEY"]);
    }

    [Fact]
    public async Task ReturnsNullForNonClaudeAgents()
    {
        Environment.SetEnvironmentVariable(CodeyBoxEnv, "sk-ant-api03-test");
        var provider = new ClaudeEnvironmentCredentialProvider(CodeyBoxEnv, AnthropicEnv);

        Assert.Null(await provider.GetAsync(AgentKind.Codex));
    }
}
