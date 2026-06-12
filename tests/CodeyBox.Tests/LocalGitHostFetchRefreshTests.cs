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
    public async Task ExistingBareRepo_ConfigSanitizationFailureIsSurfaced()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var logger = new CapturingLogger<LocalGitHost>();
        var gitHost = CreateGitHost(logger);
        var id = WorkItemId.New();

        var repoId = await gitHost.EnsureRepositoryAsync(id, seed, "main");
        var barePath = gitHost.GetRepoPath(repoId);
        var configPath = Path.Combine(barePath, "config");
        File.Delete(configPath);
        Directory.CreateDirectory(configPath);

        await Assert.ThrowsAsync<IOException>(() => gitHost.EnsureRepositoryAsync(id, seed, "main"));
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning
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
    public async Task ExistingBareRepo_NullBaseBranchIgnoresSandboxWritableHead()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitHost = CreateGitHost();
        var id = WorkItemId.New();

        var repoId = await gitHost.EnsureRepositoryAsync(id, seed, "main");
        var barePath = gitHost.GetRepoPath(repoId);
        await File.WriteAllTextAsync(
            Path.Combine(barePath, "HEAD"),
            "ref: refs/heads/" + new string('a', 1024 * 1024));

        var returnedRepoId = await gitHost.EnsureRepositoryAsync(id, seed, baseBranch: null);

        Assert.Equal(repoId, returnedRepoId);
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
    public async Task ExistingBareRepo_RefreshDisablesSandboxWritableHooks()
    {
        if (OperatingSystem.IsWindows())
            return;

        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitHost = CreateGitHost();
        var id = WorkItemId.New();

        var repoId = await gitHost.EnsureRepositoryAsync(id, seed, "main");
        var barePath = gitHost.GetRepoPath(repoId);
        var marker = Path.Combine(_workspace, "host-refresh-hook-ran");
        var hookPath = Path.Combine(barePath, "hooks", "reference-transaction");
        await File.WriteAllTextAsync(
            hookPath,
            $"""
            #!/bin/sh
            touch '{marker}'
            """);
        File.SetUnixFileMode(
            hookPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        await CommitToRepoAsync(seed, "after-hook.txt", "after hook\n", "advance main after hook");
        var after = await RevParseAsync(seed, "main");

        await gitHost.EnsureRepositoryAsync(id, seed, "main");

        Assert.Equal(after, await RevParseAsync(barePath, "main"));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task ExistingBareRepo_RefreshReplacesConfigSymlinkWithoutReadingTarget()
    {
        if (OperatingSystem.IsWindows())
            return;

        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitHost = CreateGitHost();
        var id = WorkItemId.New();

        var repoId = await gitHost.EnsureRepositoryAsync(id, seed, "main");
        var barePath = gitHost.GetRepoPath(repoId);
        var configPath = Path.Combine(barePath, "config");
        var hostileTarget = Path.Combine(_workspace, "hostile-config");
        await File.WriteAllTextAsync(
            hostileTarget,
            """
            [core]
                repositoryformatversion = 999
                filemode = true
                bare = true

            """);
        File.Delete(configPath);
        try
        {
            File.CreateSymbolicLink(configPath, hostileTarget);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (PlatformNotSupportedException)
        {
            return;
        }

        await CommitToRepoAsync(seed, "after-symlink.txt", "after symlink\n", "advance main after symlink");
        var after = await RevParseAsync(seed, "main");

        await gitHost.EnsureRepositoryAsync(id, seed, "main");

        Assert.Equal(after, await RevParseAsync(barePath, "main"));
        Assert.False(File.GetAttributes(configPath).HasFlag(FileAttributes.ReparsePoint));
        Assert.Contains("repositoryformatversion = 0", await File.ReadAllTextAsync(configPath), StringComparison.Ordinal);
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
    public async Task BranchHasCommitsAheadAsync_ReturnsTrueOnlyForConfirmedAheadCommits()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitHost = CreateGitHost();
        var id = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(id, seed, "main");
        var barePath = gitHost.GetRepoPath(repoId);
        await TestSupport.RunGit(barePath, "update-ref", "refs/heads/codeybox/equal", "refs/heads/main");
        await CommitToBareBranchAsync(
            barePath,
            "codeybox/ahead",
            "ahead.txt",
            "ahead\n",
            "ahead branch");

        Assert.False(await gitHost.BranchHasCommitsAheadAsync(repoId, "main", "codeybox/equal"));
        Assert.True(await gitHost.BranchHasCommitsAheadAsync(repoId, "main", "codeybox/ahead"));
    }

    [Fact]
    public async Task BranchHasCommitsAheadAsync_ThrowsWhenComparedBranchIsMissing()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitHost = CreateGitHost();
        var id = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(id, seed, "main");
        var barePath = gitHost.GetRepoPath(repoId);
        await TestSupport.RunGit(barePath, "update-ref", "refs/heads/codeybox/equal", "refs/heads/main");

        var missingBase = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gitHost.BranchHasCommitsAheadAsync(repoId, "missing-base", "codeybox/equal"));
        Assert.Contains("base branch 'missing-base'", missingBase.Message);

        var missingWork = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gitHost.BranchHasCommitsAheadAsync(repoId, "main", "codeybox/missing-work"));
        Assert.Contains("work branch 'codeybox/missing-work'", missingWork.Message);
    }

    [Fact]
    public async Task BranchHasCommitsAheadAsync_ThrowsWhenBareRepositoryIsMissing()
    {
        var gitHost = CreateGitHost();
        var missingRepoId = WorkItemId.New().ToString();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gitHost.BranchHasCommitsAheadAsync(missingRepoId, "main", "codeybox/work"));

        Assert.Contains("bare repo", ex.Message);
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

    [Fact]
    public async Task ExistingBareRepo_NullBaseBranchRefreshesUpstreamDefaultBranch()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await TestSupport.RunGit(seed, "checkout", "-b", "develop");
        await CommitToRepoAsync(seed, "develop.txt", "develop\n", "create develop");

        var gitHost = CreateGitHost();
        var id = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(id, seed, "develop");
        var barePath = gitHost.GetRepoPath(repoId);
        var mainBefore = await RevParseAsync(barePath, "main");

        await TestSupport.RunGit(seed, "checkout", "main");
        await CommitToRepoAsync(seed, "main-after.txt", "main after\n", "advance main");
        await TestSupport.RunGit(seed, "checkout", "develop");
        await CommitToRepoAsync(seed, "develop-after.txt", "develop after\n", "advance develop");
        var developAfter = await RevParseAsync(seed, "develop");

        await gitHost.EnsureRepositoryAsync(id, seed, baseBranch: null);

        Assert.Equal(developAfter, await RevParseAsync(barePath, "develop"));
        Assert.Equal(mainBefore, await RevParseAsync(barePath, "main"));
    }

    [Fact]
    public async Task ExistingBareRepo_SharedMirror_ClonesWithReferenceAndUsesAlternates()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var mirrorDir = Path.Combine(_workspace, "mirrors-" + Guid.NewGuid().ToString("N")[..8]);

        var gitHost = new LocalGitHost(
            new LocalGitHostOptions
            {
                RootDirectory = gitRoot,
                EnableSharedUpstreamMirror = true,
                SharedUpstreamMirrorDirectory = mirrorDir
            },
            NullLogger<LocalGitHost>.Instance);

        var id = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(id, seed, "main");
        var barePath = gitHost.GetRepoPath(repoId);

        // Verify alternates file was written and references the mirror
        var alternatesPath = Path.Combine(barePath, "objects", "info", "alternates");
        Assert.True(File.Exists(alternatesPath), "Alternates file should exist");
        var alternatesContent = await File.ReadAllTextAsync(alternatesPath);
        Assert.Contains(mirrorDir, alternatesContent);

        // Advancing main on seed
        await CommitToRepoAsync(seed, "after-mirror.txt", "after mirror\n", "advance main in seed");
        var seedTip = await RevParseAsync(seed, "main");

        // EnsureRepository again should update from the mirror
        await gitHost.EnsureRepositoryAsync(id, seed, "main");

        var bareTip = await RevParseAsync(barePath, "main");
        Assert.Equal(seedTip, bareTip);
    }

    [Fact]
    public async Task FetchUpstreamBranchAsync_SharedMirror_UsesLocalMirrorAndAvoidsNetwork()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var mirrorDir = Path.Combine(_workspace, "mirrors-" + Guid.NewGuid().ToString("N")[..8]);

        var gitHost = new LocalGitHost(
            new LocalGitHostOptions
            {
                RootDirectory = gitRoot,
                EnableSharedUpstreamMirror = true,
                SharedUpstreamMirrorDirectory = mirrorDir
            },
            NullLogger<LocalGitHost>.Instance);

        var id = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(id, seed, "main");

        // Advancing main on seed
        await CommitToRepoAsync(seed, "after-mirror-fetch.txt", "after mirror fetch\n", "advance main for fetch test");
        var seedTip = await RevParseAsync(seed, "main");

        // Call FetchUpstreamBranchAsync
        var resolvedTip = await gitHost.FetchUpstreamBranchAsync(
            repoId,
            seed,
            "main",
            new Dictionary<string, string>());

        Assert.Equal(seedTip, resolvedTip);

        // Verify mirror tip is correct
        var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        var sb = new System.Text.StringBuilder();
        foreach (var b in hashBytes) sb.Append(b.ToString("x2"));
        var mirrorRepoPath = Path.Combine(mirrorDir, sb.ToString() + ".git");
        var mirrorTip = await RevParseAsync(mirrorRepoPath, "main");
        Assert.Equal(seedTip, mirrorTip);
    }

    [Fact]
    public async Task SharedMirror_FallbackOnCorruption()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var mirrorDir = Path.Combine(_workspace, "mirrors-" + Guid.NewGuid().ToString("N")[..8]);

        var gitHost = new LocalGitHost(
            new LocalGitHostOptions
            {
                RootDirectory = gitRoot,
                EnableSharedUpstreamMirror = true,
                SharedUpstreamMirrorDirectory = mirrorDir
            },
            NullLogger<LocalGitHost>.Instance);

        var id = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(id, seed, "main");

        // Corrupt/delete the mirror repo directory
        var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        var sb = new System.Text.StringBuilder();
        foreach (var b in hashBytes) sb.Append(b.ToString("x2"));
        var mirrorRepoPath = Path.Combine(mirrorDir, sb.ToString() + ".git");

        if (Directory.Exists(mirrorRepoPath))
        {
            Directory.Delete(mirrorRepoPath, recursive: true);
        }
        Directory.CreateDirectory(mirrorRepoPath);
        await File.WriteAllTextAsync(Path.Combine(mirrorRepoPath, "HEAD"), "this-is-corrupted");

        // Now advance seed and fetch
        await CommitToRepoAsync(seed, "corrupt-mirror-test.txt", "corrupt mirror test\n", "advance main after corruption");
        var seedTip = await RevParseAsync(seed, "main");

        // EnsureRepository or FetchUpstreamBranchAsync should fall back to direct remote fetch and succeed
        var resolvedTip = await gitHost.FetchUpstreamBranchAsync(
            repoId,
            seed,
            "main",
            new Dictionary<string, string>());

        Assert.Equal(seedTip, resolvedTip);
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
