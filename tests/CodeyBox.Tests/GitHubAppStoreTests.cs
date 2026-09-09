using CodeyBox.Upstream.GitHub;

namespace CodeyBox.Tests;

public sealed class GitHubAppStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"codeybox-app-store-{Guid.NewGuid():N}");

    [Fact]
    public void SaveAndInstall_RoundTripsPrivateAppMetadata()
    {
        var store = new GitHubAppStore(_directory);
        var created = store.SaveCreated(123, "codeybox-test", "owner", "private-key");
        store.CompleteInstall(created.AppId, 456);

        var reloaded = new GitHubAppStore(_directory);
        var app = reloaded.Get("codeybox-test");

        Assert.NotNull(app);
        Assert.Equal(456, app!.InstallationId);
        Assert.Equal("private-key", File.ReadAllText(app.PrivateKeyPath));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(app.PrivateKeyPath));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
