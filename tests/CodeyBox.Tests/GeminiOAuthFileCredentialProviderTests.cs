using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class GeminiOAuthFileCredentialProviderTests : IDisposable
{
    private readonly string _tempDir;

    public GeminiOAuthFileCredentialProviderTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("codeybox-gemini-oauth-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private (string OAuth, string Settings) WriteGeminiFiles()
        => (
            WriteFile("oauth_creds.json", """{"access_token":"gemini-token"}"""),
            WriteFile("settings.json", """{"security":{"auth":{"selectedType":"oauth-personal"}}}"""));

    [Fact]
    public void PathConstructor_WithWatchFalse_DoesNotCreateWatchers()
    {
        var paths = WriteGeminiFiles();

        using var provider = new GeminiOAuthFileCredentialProvider(
            paths.OAuth,
            paths.Settings,
            watch: false);

        Assert.False(TestFileSystemWatcherLeakTracker.IsTrackingPath(paths.OAuth));
        Assert.False(TestFileSystemWatcherLeakTracker.IsTrackingPath(paths.Settings));
    }

    [Fact]
    public void Dispose_ReleasesBothOwnedCredentialFileSourceWatchers()
    {
        var paths = WriteGeminiFiles();
        var provider = new GeminiOAuthFileCredentialProvider(
            paths.OAuth,
            paths.Settings,
            watch: true);

        try
        {
            Assert.True(TestFileSystemWatcherLeakTracker.IsTrackingPath(paths.OAuth));
            Assert.True(TestFileSystemWatcherLeakTracker.IsTrackingPath(paths.Settings));
        }
        finally
        {
            provider.Dispose();
        }

        Assert.False(TestFileSystemWatcherLeakTracker.IsTrackingPath(paths.OAuth));
        Assert.False(TestFileSystemWatcherLeakTracker.IsTrackingPath(paths.Settings));
    }

    [Fact]
    public void Dispose_DoesNotDisposeExternallyOwnedCredentialFileSources()
    {
        var paths = WriteGeminiFiles();
        using var oauthSource = new CredentialFileSource(paths.OAuth, watch: false);
        using var settingsSource = new CredentialFileSource(paths.Settings, watch: false);
        var provider = new GeminiOAuthFileCredentialProvider(oauthSource, settingsSource);

        provider.Dispose();

        Assert.Equal("""{"access_token":"gemini-token"}""", oauthSource.GetRaw());
        Assert.Equal(
            """{"security":{"auth":{"selectedType":"oauth-personal"}}}""",
            settingsSource.GetRaw());
    }

    [Fact]
    public async Task GetAsync_ReadsGeminiOAuthAndSettingsFilesForGemini()
    {
        var paths = WriteGeminiFiles();
        using var provider = new GeminiOAuthFileCredentialProvider(
            paths.OAuth,
            paths.Settings,
            watch: false);

        var credential = await provider.GetAsync(AgentKind.Gemini);

        Assert.NotNull(credential);
        Assert.Equal(AgentKind.Gemini, credential!.Agent);
        Assert.Equal(
            """{"access_token":"gemini-token"}""",
            credential.EnvironmentVariables[GeminiOAuthFileCredentialProvider.OAuthCredsEnvVar]);
        Assert.Equal(
            """{"security":{"auth":{"selectedType":"oauth-personal"}}}""",
            credential.EnvironmentVariables[GeminiOAuthFileCredentialProvider.SettingsEnvVar]);
    }

    [Fact]
    public async Task GetAsync_UsesDefaultSettingsWhenSettingsFileMissing()
    {
        var oauthPath = WriteFile("oauth_creds.json", """{"access_token":"gemini-token"}""");
        var missingSettingsPath = Path.Combine(_tempDir, "missing-settings.json");
        using var provider = new GeminiOAuthFileCredentialProvider(
            oauthPath,
            missingSettingsPath,
            watch: false);

        var credential = await provider.GetAsync(AgentKind.Gemini);

        Assert.NotNull(credential);
        Assert.Contains(
            "oauth-personal",
            credential!.EnvironmentVariables[GeminiOAuthFileCredentialProvider.SettingsEnvVar]);
    }

    [Fact]
    public async Task GetAsync_ReturnsNullForOtherAgents()
    {
        var paths = WriteGeminiFiles();
        using var provider = new GeminiOAuthFileCredentialProvider(
            paths.OAuth,
            paths.Settings,
            watch: false);

        Assert.Null(await provider.GetAsync(AgentKind.Claude));
    }
}
