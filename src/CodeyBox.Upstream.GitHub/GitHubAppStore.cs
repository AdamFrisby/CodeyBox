using System.Text.Json;

namespace CodeyBox.Upstream.GitHub;

public sealed record StoredGitHubApp(
    long AppId,
    string Slug,
    long InstallationId,
    string Account,
    string PrivateKeyPath);

public sealed class GitHubAppStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _metadataPath;
    private List<StoredGitHubApp> _apps;

    public GitHubAppStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("GitHub App store directory must be absolute.", nameof(directory));
        _directory = directory;
        _metadataPath = Path.Combine(directory, "apps.json");
        Directory.CreateDirectory(directory);
        SetMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _apps = Load();
    }

    public IReadOnlyList<StoredGitHubApp> List()
    {
        lock (_gate) return _apps.ToArray();
    }

    public StoredGitHubApp? Get(string slug)
    {
        lock (_gate)
            return _apps.FirstOrDefault(app =>
                string.Equals(app.Slug, slug, StringComparison.OrdinalIgnoreCase));
    }

    public StoredGitHubApp? Get(long appId)
    {
        lock (_gate) return _apps.FirstOrDefault(app => app.AppId == appId);
    }

    public StoredGitHubApp SaveCreated(long appId, string slug, string account, string pem)
    {
        if (appId <= 0 || string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(pem))
            throw new ArgumentException("GitHub manifest conversion was incomplete.");
        lock (_gate)
        {
            var keyPath = Path.Combine(_directory, $"{appId}.pem");
            AtomicWrite(keyPath, pem);
            var stored = new StoredGitHubApp(appId, slug, 0, account, keyPath);
            Upsert(stored);
            return stored;
        }
    }

    public StoredGitHubApp CompleteInstall(long appId, long installationId)
    {
        if (appId <= 0)
            throw new ArgumentOutOfRangeException(nameof(appId));
        if (installationId <= 0)
            throw new ArgumentOutOfRangeException(nameof(installationId));
        lock (_gate)
        {
            var app = _apps.FirstOrDefault(existing => existing.AppId == appId)
                ?? throw new InvalidOperationException($"GitHub App {appId} was not created by CodeyBox.");
            var installed = app with { InstallationId = installationId };
            Upsert(installed);
            return installed;
        }
    }

    private void Upsert(StoredGitHubApp app)
    {
        var index = _apps.FindIndex(existing => existing.AppId == app.AppId);
        if (index >= 0) _apps[index] = app;
        else _apps.Add(app);
        AtomicWrite(_metadataPath, JsonSerializer.Serialize(_apps, JsonOptions));
    }

    private List<StoredGitHubApp> Load()
    {
        if (!File.Exists(_metadataPath)) return [];
        var apps = JsonSerializer.Deserialize<List<StoredGitHubApp>>(
            File.ReadAllText(_metadataPath), JsonOptions) ?? [];
        foreach (var app in apps)
        {
            if (!Path.GetFullPath(app.PrivateKeyPath).StartsWith(
                    Path.GetFullPath(_directory) + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal)
                || !File.Exists(app.PrivateKeyPath))
                throw new InvalidDataException("GitHub App store contains an invalid private-key path.");
        }
        return apps;
    }

    private static void AtomicWrite(string path, string content)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, content);
        SetMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, overwrite: true);
        SetMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void SetMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
    }
}
