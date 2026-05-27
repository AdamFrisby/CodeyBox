using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="OpencodeOAuthFileCredentialProvider"/>. Mirrors the
/// per-agent OAuth-file provider test layout (Claude/Codex/Gemini). Pins:
/// agent-kind gating, env-var bundle contents,
/// <c>OPENCODE_AUTH_DEST_PATH</c> conditional inclusion, fall-through on
/// missing/empty file, and source ownership semantics across the two
/// constructor overloads.
/// </summary>
public sealed class OpencodeOAuthFileCredentialProviderTests : IDisposable
{
    private readonly string _workspace;

    public OpencodeOAuthFileCredentialProviderTests()
    {
        _workspace = Directory.CreateTempSubdirectory("codeybox-opencode-oauth-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    private string WriteAuthFile(string content)
    {
        var path = Path.Combine(_workspace, "auth.json");
        File.WriteAllText(path, content);
        return path;
    }

    private static OpencodeOAuthFileCredentialProvider NewFileProvider(
        string path, string? destinationPath = null)
        => new(path, destinationPath, log: null, watch: false);

    [Fact]
    public async Task GetAsync_ReadsRawBytesForOpencode()
    {
        const string raw = """{"providers":{"deepseek":{"apiKey":"sk-deepseek-test"}}}""";
        var path = WriteAuthFile(raw);
        using var provider = NewFileProvider(path);

        var credential = await provider.GetAsync(AgentKind.Opencode);

        Assert.NotNull(credential);
        Assert.Equal(AgentKind.Opencode, credential!.Agent);
        Assert.Equal(raw, credential.EnvironmentVariables["OPENCODE_AUTH_JSON"]);
    }

    [Fact]
    public async Task GetAsync_DoesNotEmitBindMount()
    {
        // The runner materialises the credential file inside the VM from the
        // env var; the provider must never bind-mount the host credential
        // path into an untrusted sandbox (see "Don't do" rule in the brief).
        var path = WriteAuthFile("""{"providers":{}}""");
        using var provider = NewFileProvider(path);

        var credential = await provider.GetAsync(AgentKind.Opencode);

        Assert.NotNull(credential);
        Assert.Empty(credential!.Mounts);
    }

    [Fact]
    public async Task GetAsync_OmitsDestinationPath_WhenNotConfigured()
    {
        // No CODEYBOX_OPENCODE_AUTH_DEST configured ⇒ the runner falls back
        // to the XDG default; the env var must be absent so the script's
        // ${OPENCODE_AUTH_DEST_PATH:-$HOME/.local/share/opencode/auth.json}
        // expansion picks the default branch.
        var path = WriteAuthFile("""{"providers":{}}""");
        using var provider = NewFileProvider(path, destinationPath: null);

        var credential = await provider.GetAsync(AgentKind.Opencode);

        Assert.NotNull(credential);
        Assert.False(credential!.EnvironmentVariables.ContainsKey("OPENCODE_AUTH_DEST_PATH"));
    }

    [Fact]
    public async Task GetAsync_IncludesDestinationPath_WhenConfigured()
    {
        var path = WriteAuthFile("""{"providers":{}}""");
        using var provider = NewFileProvider(path, destinationPath: "/home/runner/.config/opencode/auth.json");

        var credential = await provider.GetAsync(AgentKind.Opencode);

        Assert.NotNull(credential);
        Assert.Equal(
            "/home/runner/.config/opencode/auth.json",
            credential!.EnvironmentVariables["OPENCODE_AUTH_DEST_PATH"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t  ")]
    public async Task GetAsync_WhitespaceOrEmptyDestinationPath_IsTreatedAsNull(string dest)
    {
        // Defensive: don't emit a junk OPENCODE_AUTH_DEST_PATH that would
        // make the in-VM script try to mkdir a meaningless path.
        var path = WriteAuthFile("""{"providers":{}}""");
        using var provider = NewFileProvider(path, destinationPath: dest);

        var credential = await provider.GetAsync(AgentKind.Opencode);

        Assert.NotNull(credential);
        Assert.False(credential!.EnvironmentVariables.ContainsKey("OPENCODE_AUTH_DEST_PATH"));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("gemini")]
    [InlineData("copilot")]
    public async Task GetAsync_ReturnsNullForOtherAgents(string agentValue)
    {
        // The provider must scope strictly to AgentKind.Opencode so a chained
        // provider list does not accidentally leak opencode credentials into
        // a different agent's environment bundle.
        var path = WriteAuthFile("""{"providers":{}}""");
        using var provider = NewFileProvider(path);
        var agent = new AgentKind(agentValue);

        var credential = await provider.GetAsync(agent);

        Assert.Null(credential);
    }

    [Fact]
    public async Task GetAsync_FileMissing_ReturnsNull()
    {
        // Falls through cleanly so a chained env-based provider can take over.
        using var provider = NewFileProvider(Path.Combine(_workspace, "nonexistent.json"));

        var credential = await provider.GetAsync(AgentKind.Opencode);

        Assert.Null(credential);
    }

    [Fact]
    public async Task GetAsync_FileEmpty_ReturnsNull()
    {
        var path = WriteAuthFile(string.Empty);
        using var provider = NewFileProvider(path);

        var credential = await provider.GetAsync(AgentKind.Opencode);

        Assert.Null(credential);
    }

    [Fact]
    public async Task GetAsync_FileWhitespaceOnly_ReturnsNull()
    {
        var path = WriteAuthFile("   \n\t  ");
        using var provider = NewFileProvider(path);

        var credential = await provider.GetAsync(AgentKind.Opencode);

        Assert.Null(credential);
    }

    [Fact]
    public async Task GetAsync_RereadsOnEachCall()
    {
        // Token rotation: the host's `opencode auth` flow may overwrite the
        // credentials file mid-run; subsequent pickups must see the fresh
        // bytes without an orchestrator restart.
        var path = WriteAuthFile("""{"providers":{"deepseek":{"apiKey":"sk-first"}}}""");
        using var provider = NewFileProvider(path);

        var cred1 = await provider.GetAsync(AgentKind.Opencode);
        File.WriteAllText(path, """{"providers":{"deepseek":{"apiKey":"sk-second"}}}""");
        var cred2 = await provider.GetAsync(AgentKind.Opencode);

        Assert.Contains("sk-first", cred1!.EnvironmentVariables["OPENCODE_AUTH_JSON"]);
        Assert.Contains("sk-second", cred2!.EnvironmentVariables["OPENCODE_AUTH_JSON"]);
    }

    [Fact]
    public async Task PathConstructor_WithWatchFalse_DoesNotCreateWatcher()
    {
        var path = WriteAuthFile("""{"providers":{}}""");

        using var provider = new OpencodeOAuthFileCredentialProvider(path, watch: false);

        Assert.False(TestFileSystemWatcherLeakTracker.IsTrackingPath(path));
        Assert.NotNull(await provider.GetAsync(AgentKind.Opencode));
    }

    [Fact]
    public async Task Dispose_ReleasesOwnedCredentialFileSourceWatcher()
    {
        var path = WriteAuthFile("""{"providers":{}}""");
        var provider = new OpencodeOAuthFileCredentialProvider(path, watch: true);

        try
        {
            Assert.True(TestFileSystemWatcherLeakTracker.IsTrackingPath(path));
        }
        finally
        {
            provider.Dispose();
        }

        Assert.False(TestFileSystemWatcherLeakTracker.IsTrackingPath(path));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Dispose_DoesNotDisposeExternallyOwnedCredentialFileSource()
    {
        // The DI registration in Program.cs constructs the source as a
        // separately-registered singleton and passes it in via the source
        // constructor; that lifecycle is owned by the DI container, so
        // Dispose on the provider must not tear down the shared source.
        var path = WriteAuthFile("""{"providers":{}}""");
        using var source = new OpencodeCredentialFileSource(path, watch: false);
        var provider = new OpencodeOAuthFileCredentialProvider(source);

        provider.Dispose();

        Assert.NotNull(source.GetRaw());
        var cred = await new OpencodeOAuthFileCredentialProvider(source).GetAsync(AgentKind.Opencode);
        Assert.NotNull(cred);
    }

    [Fact]
    public async Task ChainFallsThroughWhenFileMissing()
    {
        // When the opencode file path isn't present, the provider must yield
        // and let the next provider in the chain answer (the operator may
        // have a CODEYBOX_OPENCODE_AUTH_JSON env var as a side-channel).
        var fileProvider = NewFileProvider(Path.Combine(_workspace, "missing.json"));
        var envCredential = new AgentCredential(
            AgentKind.Opencode,
            new Dictionary<string, string> { ["OPENCODE_AUTH_JSON"] = "fallback-json" },
            new Dictionary<string, string>());
        var chain = new ChainedCredentialProvider([fileProvider, new FixedProvider(envCredential)]);

        var credential = await chain.GetAsync(AgentKind.Opencode);

        Assert.NotNull(credential);
        Assert.Equal("fallback-json", credential!.EnvironmentVariables["OPENCODE_AUTH_JSON"]);
    }

    private sealed class FixedProvider(AgentCredential credential) : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
            => Task.FromResult<AgentCredential?>(credential);
    }
}
