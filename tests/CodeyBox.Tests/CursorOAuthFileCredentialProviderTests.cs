using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class CursorOAuthFileCredentialProviderTests : IDisposable
{
    private readonly string _workspace;

    public CursorOAuthFileCredentialProviderTests()
    {
        _workspace = Directory.CreateTempSubdirectory("codeybox-cursor-oauth-").FullName;
    }

    public void Dispose()
    {
        CodeyBox.Tests.TestTempArtifacts.DeleteDirectory(_workspace);
    }

    private string WriteCredFile(string content, string name = "credentials.json")
    {
        var path = Path.Combine(_workspace, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static CursorOAuthFileCredentialProvider NewProvider(string path)
        => new(path, log: null, watch: false);

    [Fact]
    public void PathConstructor_WithWatchFalse_DoesNotCreateWatcher()
    {
        var path = WriteCredFile("""{"token":"cursor-test"}""");

        using var provider = new CursorOAuthFileCredentialProvider(path, log: null, watch: false);

        Assert.False(TestFileSystemWatcherLeakTracker.IsTrackingPath(path));
    }

    [Fact]
    public void Dispose_ReleasesOwnedCredentialFileSourceWatcher()
    {
        var path = WriteCredFile("""{"token":"cursor-test"}""");
        var provider = new CursorOAuthFileCredentialProvider(path, log: null, watch: true);

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
        const string raw = """{"token":"cursor-test"}""";
        var path = WriteCredFile(raw);
        using var source = new CursorCredentialFileSource(path, watch: false);
        var provider = new CursorOAuthFileCredentialProvider(source);

        provider.Dispose();

        Assert.Equal(raw, source.GetRaw());
    }

    [Fact]
    public async Task GetAsync_ReadsCursorCredentialsFileForCursor()
    {
        // The env var key here is load-bearing — it must match what
        // CursorAgentRunner's bash heredoc reads ($CODEYBOX_CURSOR_AUTH_JSON)
        // and the AgentCredentialMapping wired up in Program.cs. A subtle
        // typo at any of those three sites silently drops the credential.
        const string raw = """{"token":"cursor-test"}""";
        var path = WriteCredFile(raw);
        var provider = NewProvider(path);

        var credential = await provider.GetAsync(AgentKind.Cursor);

        Assert.NotNull(credential);
        Assert.Equal(AgentKind.Cursor, credential!.Agent);
        Assert.Equal(raw, credential.EnvironmentVariables["CODEYBOX_CURSOR_AUTH_JSON"]);
    }

    [Fact]
    public async Task GetAsync_DoesNotEmitHostCredentialMount()
    {
        // The provider must not bind-mount the host ~/.cursor directory into
        // the untrusted agent sandbox. CursorAgentRunner materialises a
        // private credentials.json snapshot from CODEYBOX_CURSOR_AUTH_JSON
        // instead (mirroring the Codex pattern).
        var path = WriteCredFile("""{"token":"t"}""");
        var provider = NewProvider(path);

        var credential = await provider.GetAsync(AgentKind.Cursor);

        Assert.NotNull(credential);
        Assert.Empty(credential!.Mounts);
    }

    [Fact]
    public async Task GetAsync_RereadsFileOnEachCall()
    {
        // Re-reading on each pickup picks up host-side token rotations
        // without an orchestrator restart, per the provider's class summary.
        var path = WriteCredFile("""{"token":"first"}""");
        var provider = NewProvider(path);

        var first = await provider.GetAsync(AgentKind.Cursor);
        File.WriteAllText(path, """{"token":"second"}""");
        var second = await provider.GetAsync(AgentKind.Cursor);

        Assert.Equal("""{"token":"first"}""",
            first!.EnvironmentVariables["CODEYBOX_CURSOR_AUTH_JSON"]);
        Assert.Equal("""{"token":"second"}""",
            second!.EnvironmentVariables["CODEYBOX_CURSOR_AUTH_JSON"]);
    }

    [Fact]
    public async Task GetAsync_ReturnsNullForOtherAgents()
    {
        var path = WriteCredFile("""{"token":"cursor-test"}""");
        var provider = NewProvider(path);

        Assert.Null(await provider.GetAsync(AgentKind.Claude));
        Assert.Null(await provider.GetAsync(AgentKind.Codex));
        Assert.Null(await provider.GetAsync(AgentKind.Gemini));
    }

    [Fact]
    public async Task GetAsync_WhenFileMissing_ReturnsNull()
    {
        var provider = NewProvider(Path.Combine(_workspace, "missing.json"));

        var credential = await provider.GetAsync(AgentKind.Cursor);

        Assert.Null(credential);
    }

    [Fact]
    public async Task GetAsync_WhenFileEmpty_ReturnsNull()
    {
        var path = WriteCredFile("");
        var provider = NewProvider(path);

        var credential = await provider.GetAsync(AgentKind.Cursor);

        Assert.Null(credential);
    }

    [Fact]
    public async Task GetAsync_WhenFileWhitespaceOnly_ReturnsNull()
    {
        var path = WriteCredFile("   \n\t  ");
        var provider = NewProvider(path);

        var credential = await provider.GetAsync(AgentKind.Cursor);

        Assert.Null(credential);
    }

    [Fact]
    public async Task GetAsync_WhenFileMissing_ChainFallsThroughToNextProvider()
    {
        var fileProvider = NewProvider(Path.Combine(_workspace, "missing.json"));
        var envCredential = new AgentCredential(
            AgentKind.Cursor,
            new Dictionary<string, string> { ["CODEYBOX_CURSOR_AUTH_JSON"] = "from-env" },
            new Dictionary<string, string>());
        var chain = new ChainedCredentialProvider([fileProvider, new FixedProvider(envCredential)]);

        var credential = await chain.GetAsync(AgentKind.Cursor);

        Assert.NotNull(credential);
        Assert.Equal("from-env", credential!.EnvironmentVariables["CODEYBOX_CURSOR_AUTH_JSON"]);
    }

    [Fact]
    public void PathConstructor_NullPath_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new CursorOAuthFileCredentialProvider(null!, log: null, watch: false));
    }

    private sealed class FixedProvider(AgentCredential credential) : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
            => Task.FromResult<AgentCredential?>(credential);
    }
}
