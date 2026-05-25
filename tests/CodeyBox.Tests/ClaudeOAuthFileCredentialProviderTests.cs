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

    private static ClaudeOAuthFileCredentialProvider NewProvider(string path)
        => new(path, "CLAUDE_CODE_OAUTH_TOKEN", watch: false);

    [Fact]
    public void PathConstructor_WithWatchFalse_DoesNotCreateWatcher()
    {
        var path = WriteCredFile("""{"claudeAiOauth":{"accessToken":"sk-ant-oat01-abc"}}""");

        using var provider = new ClaudeOAuthFileCredentialProvider(
            path,
            "CLAUDE_CODE_OAUTH_TOKEN",
            watch: false);

        Assert.False(TestFileSystemWatcherLeakTracker.IsTrackingPath(path));
    }

    [Fact]
    public void Dispose_ReleasesOwnedCredentialFileSourceWatcher()
    {
        var path = WriteCredFile("""{"claudeAiOauth":{"accessToken":"sk-ant-oat01-abc"}}""");
        var provider = new ClaudeOAuthFileCredentialProvider(
            path,
            "CLAUDE_CODE_OAUTH_TOKEN",
            watch: true);

        try
        {
            Assert.True(TestFileSystemWatcherLeakTracker.IsTrackingPath(path));
        }
        finally
        {
            provider.Dispose();
        }

        Assert.False(TestFileSystemWatcherLeakTracker.IsTrackingPath(path));
    }

    [Fact]
    public void Dispose_DoesNotDisposeExternallyOwnedCredentialFileSource()
    {
        const string raw = """{"claudeAiOauth":{"accessToken":"sk-ant-oat01-abc"}}""";
        var path = WriteCredFile(raw);
        using var source = new CredentialFileSource(path, watch: false);
        var provider = new ClaudeOAuthFileCredentialProvider(
            source,
            "CLAUDE_CODE_OAUTH_TOKEN");

        provider.Dispose();

        Assert.Equal(raw, source.GetRaw());
    }

    [Fact]
    public async Task ReturnsTokenForClaudeWhenFilePresent()
    {
        var path = WriteCredFile("""{"claudeAiOauth":{"accessToken":"sk-ant-oat01-abc"}}""");
        var p = NewProvider(path);

        var cred = await p.GetAsync(AgentKind.Claude);

        Assert.NotNull(cred);
        Assert.Equal("sk-ant-oat01-abc", cred!.EnvironmentVariables["CLAUDE_CODE_OAUTH_TOKEN"]);
    }

    [Fact]
    public async Task ShipsSanitisedOAuthJsonBundleWithoutRefreshToken()
    {
        // The bundle materialised into the VM must omit the refresh_token so
        // the in-VM CLI cannot redeem it concurrently with the host CLI —
        // shared single-use refresh tokens cause intermittent 401s that pin
        // Claude as unavailable for the breaker window. See
        // ClaudeOAuthFileCredentialProvider's class summary.
        const string raw =
            """{"claudeAiOauth":{"accessToken":"sk-ant-oat01-abc","refreshToken":"rt-xyz","expiresAt":1234567890}}""";
        var path = WriteCredFile(raw);
        var p = NewProvider(path);

        var cred = await p.GetAsync(AgentKind.Claude);

        Assert.NotNull(cred);
        var bundle = cred!.EnvironmentVariables[ClaudeOAuthFileCredentialProvider.OAuthJsonEnvVar];
        Assert.Equal("sk-ant-oat01-abc", cred.EnvironmentVariables["CLAUDE_CODE_OAUTH_TOKEN"]);
        Assert.Contains("\"accessToken\":\"sk-ant-oat01-abc\"", bundle);
        Assert.Contains("\"expiresAt\":1234567890", bundle);
        Assert.DoesNotContain("refreshToken", bundle, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rt-xyz", bundle);

        // Sanity: still valid JSON shaped like the original.
        using var doc = System.Text.Json.JsonDocument.Parse(bundle);
        var oauth = doc.RootElement.GetProperty("claudeAiOauth");
        Assert.Equal("sk-ant-oat01-abc", oauth.GetProperty("accessToken").GetString());
    }

    [Fact]
    public async Task SanitisedBundle_OmitsExpiresAtWhenAbsentInSource()
    {
        // No expiresAt field in the source file: don't fabricate one.
        const string raw = """{"claudeAiOauth":{"accessToken":"sk-ant-oat01-abc","refreshToken":"rt-xyz"}}""";
        var path = WriteCredFile(raw);
        var p = NewProvider(path);

        var cred = await p.GetAsync(AgentKind.Claude);

        var bundle = cred!.EnvironmentVariables[ClaudeOAuthFileCredentialProvider.OAuthJsonEnvVar];
        Assert.DoesNotContain("expiresAt", bundle, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refreshToken", bundle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SanitisedBundle_NeverEmitsRefreshTokenAcrossRepeatedCalls()
    {
        // The shared-OAuth race fix is enforced structurally: the in-VM CLI
        // never receives a refresh_token, so it is incapable of calling
        // Anthropic's refresh endpoint at all. This test pins that structural
        // guarantee — across repeated provider calls (including concurrent
        // ones from different consumers), every emitted bundle must omit the
        // refresh_token even when the host file still contains one. It is NOT
        // a simulation of two concurrent refreshes reaching Anthropic; the
        // provider itself never refreshes, so there is no race to simulate at
        // this layer.
        const string raw =
            """{"claudeAiOauth":{"accessToken":"sk-ant-oat01-abc","refreshToken":"rt-xyz","expiresAt":9999999999}}""";
        var path = WriteCredFile(raw);
        var p = NewProvider(path);

        var taskA = Task.Run(() => p.GetAsync(AgentKind.Claude));
        var taskB = Task.Run(() => p.GetAsync(AgentKind.Claude));
        var creds = await Task.WhenAll(taskA, taskB);
        var a = creds[0];
        var b = creds[1];

        var bundleA = a!.EnvironmentVariables[ClaudeOAuthFileCredentialProvider.OAuthJsonEnvVar];
        var bundleB = b!.EnvironmentVariables[ClaudeOAuthFileCredentialProvider.OAuthJsonEnvVar];

        Assert.Equal(a.EnvironmentVariables["CLAUDE_CODE_OAUTH_TOKEN"],
                     b.EnvironmentVariables["CLAUDE_CODE_OAUTH_TOKEN"]);
        Assert.Equal(bundleA, bundleB);
        Assert.DoesNotContain("refreshToken", bundleA, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refreshToken", bundleB, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rt-xyz", bundleA);
        Assert.DoesNotContain("rt-xyz", bundleB);
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
        var p = NewProvider(path);

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
        var p = NewProvider(path);

        Assert.Null(await p.GetAsync(AgentKind.Codex));
        Assert.Null(await p.GetAsync(AgentKind.Copilot));
    }

    [Fact]
    public async Task ReturnsNullWhenFileMissing()
    {
        var p = NewProvider(Path.Combine(_tempDir, "nonexistent.json"));

        Assert.Null(await p.GetAsync(AgentKind.Claude));
    }

    [Fact]
    public async Task ReturnsNullWhenJsonMalformed()
    {
        var path = WriteCredFile("not valid json");
        var p = NewProvider(path);

        Assert.Null(await p.GetAsync(AgentKind.Claude));
    }

    [Fact]
    public async Task ReturnsNullWhenAccessTokenFieldAbsent()
    {
        var path = WriteCredFile("""{"claudeAiOauth":{"otherField":"x"}}""");
        var p = NewProvider(path);

        Assert.Null(await p.GetAsync(AgentKind.Claude));
    }

    [Fact]
    public async Task ReturnsNullWhenAccessTokenFieldEmpty()
    {
        var path = WriteCredFile("""{"claudeAiOauth":{"accessToken":""}}""");
        var p = NewProvider(path);

        Assert.Null(await p.GetAsync(AgentKind.Claude));
    }

    [Fact]
    public async Task ChainFallsThroughToEnvWhenFileAbsent()
    {
        Environment.SetEnvironmentVariable("CODEYBOX_CHAINED_TEST_KEY", "from-env");
        try
        {
            var fileProvider = NewProvider(Path.Combine(_tempDir, "missing.json"));
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
            var fileProvider = NewProvider(path);
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
