using System.Diagnostics;
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
        TestTempArtifacts.DeleteDirectory(_workspace);
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
    public async Task FetchUpstreamBranchAsync_SharedMirror_FetchesCurrentBranchFromMirrorNotUpstream()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var mirrorDir = Path.Combine(_workspace, "mirrors-" + Guid.NewGuid().ToString("N")[..8]);
        var invocations = new List<GitInvocation>();

        var gitHost = new LocalGitHost(
            new LocalGitHostOptions
            {
                RootDirectory = gitRoot,
                EnableSharedUpstreamMirror = true,
                SharedUpstreamMirrorDirectory = mirrorDir
            },
            NullLogger<LocalGitHost>.Instance,
            psi => new RecordingLocalGitProcess(psi, invocations));

        var id = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(id, seed, "main");
        var barePath = gitHost.GetRepoPath(repoId);
        invocations.Clear();

        var resolvedTip = await gitHost.FetchUpstreamBranchAsync(
            repoId,
            seed,
            "main",
            new Dictionary<string, string>());

        Assert.Equal(await RevParseAsync(seed, "main"), resolvedTip);
        var repoFetch = Assert.Single(invocations, i =>
            i.WorkingDirectory == barePath
            && i.GitArgs.Count >= 4
            && i.GitArgs[0] == "fetch"
            && i.GitArgs[1] == "--no-tags");
        Assert.NotEqual(seed, repoFetch.GitArgs[2]);
        Assert.StartsWith(mirrorDir, repoFetch.GitArgs[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureRepositoryAsync_SharedMirror_UsesSharedCloneForMirrorBorrowing()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var mirrorDir = Path.Combine(_workspace, "mirrors-" + Guid.NewGuid().ToString("N")[..8]);
        var invocations = new List<GitInvocation>();

        var gitHost = new LocalGitHost(
            new LocalGitHostOptions
            {
                RootDirectory = gitRoot,
                EnableSharedUpstreamMirror = true,
                SharedUpstreamMirrorDirectory = mirrorDir
            },
            NullLogger<LocalGitHost>.Instance,
            psi => new RecordingLocalGitProcess(psi, invocations));

        await gitHost.EnsureRepositoryAsync(WorkItemId.New(), seed, "main");

        var mirrorBackedClone = Assert.Single(invocations, i =>
            i.GitArgs.Contains("clone", StringComparer.Ordinal)
            && i.GitArgs.Contains("--shared", StringComparer.Ordinal)
            && i.GitArgs.Any(a => a.StartsWith(mirrorDir, StringComparison.Ordinal)));
        Assert.Contains("--shared", mirrorBackedClone.GitArgs);
    }

    [Fact]
    public async Task EnsureRepositoryAsync_SharedMirror_InitialCloneFallbackClearsFailedDestination()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var mirrorDir = Path.Combine(_workspace, "mirrors-" + Guid.NewGuid().ToString("N")[..8]);
        var failedReferenceClone = false;

        var gitHost = new LocalGitHost(
            new LocalGitHostOptions
            {
                RootDirectory = gitRoot,
                EnableSharedUpstreamMirror = true,
                SharedUpstreamMirrorDirectory = mirrorDir
            },
            NullLogger<LocalGitHost>.Instance,
            psi =>
            {
                var invocation = GitInvocation.From(psi);
                if (!failedReferenceClone
                    && invocation.GitArgs.Contains("clone", StringComparer.Ordinal)
                    && invocation.GitArgs.Contains("--shared", StringComparer.Ordinal))
                {
                    failedReferenceClone = true;
                    var targetPath = invocation.GitArgs[^1];
                    return new FakeLocalGitProcess(
                        exitCode: 1,
                        stderr: "simulated mirror clone failure",
                        onStart: () =>
                        {
                            Directory.CreateDirectory(targetPath);
                            File.WriteAllText(Path.Combine(targetPath, "partial-residue"), "left by failed clone");
                        });
                }

                return new RecordingLocalGitProcess(psi, new List<GitInvocation>());
            });

        var repoId = await gitHost.EnsureRepositoryAsync(WorkItemId.New(), seed, "main");
        var barePath = gitHost.GetRepoPath(repoId);

        Assert.True(failedReferenceClone);
        Assert.Equal(await RevParseAsync(seed, "main"), await RevParseAsync(barePath, "main"));
        Assert.False(File.Exists(Path.Combine(barePath, "partial-residue")));
        Assert.False(File.Exists(Path.Combine(barePath, "objects", "info", "alternates")));
        Assert.False(File.Exists(barePath + ".mirror_metadata"));
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
        var barePath = gitHost.GetRepoPath(repoId);
        Assert.False(File.Exists(Path.Combine(barePath, "objects", "info", "alternates")));
        Assert.False(File.Exists(barePath + ".mirror_metadata"));
    }

    [Fact]
    public async Task SharedMirror_ConfiguresGcToProtectAlternatesDependents()
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

        await gitHost.EnsureRepositoryAsync(WorkItemId.New(), seed, "main");

        var mirrorRepoPath = MirrorRepoPath(mirrorDir, seed);
        var (_, gcAuto, _) = await TestSupport.RunGit(mirrorRepoPath, "config", "--get", "gc.auto");
        var (_, pruneExpire, _) = await TestSupport.RunGit(mirrorRepoPath, "config", "--get", "gc.pruneExpire");
        Assert.Equal("0", gcAuto.Trim());
        Assert.Equal("never", pruneExpire.Trim());
    }

    [Fact]
    public async Task EnsureRepositoryAsync_SharedMirror_NullBaseBranchUsesUpstreamDefaultBranch()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await TestSupport.RunGit(seed, "checkout", "-b", "develop");
        await CommitToRepoAsync(seed, "develop.txt", "develop\n", "create develop");

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

        var repoId = await gitHost.EnsureRepositoryAsync(WorkItemId.New(), seed, baseBranch: null);

        Assert.Equal(await RevParseAsync(seed, "develop"), await RevParseAsync(gitHost.GetRepoPath(repoId), "develop"));
    }

    [Fact]
    public async Task IsolatedMergeClone_SharedMirror_CopiesMountsRestoresAndCleansMetadata()
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

        var repoId = await gitHost.EnsureRepositoryAsync(WorkItemId.New(), seed, "main");
        var isolatedPath = await gitHost.CreateIsolatedMergeCloneAsync(repoId, WorkItemId.New());

        try
        {
            Assert.True(File.Exists(isolatedPath + ".mirror_metadata"));
            var access = gitHost.GetIsolatedRepoSandboxAccess(isolatedPath);
            var mirrorMount = Assert.Single(access.Mounts, m => m.ReadOnly);
            Assert.StartsWith(mirrorDir, mirrorMount.HostPath, StringComparison.Ordinal);

            Directory.Delete(isolatedPath, recursive: true);
            File.Delete(isolatedPath + ".mirror_metadata");
            await gitHost.RestoreIsolatedMergeCloneAsync(repoId, isolatedPath);

            Assert.True(File.Exists(isolatedPath + ".mirror_metadata"));
            Assert.True(Directory.Exists(isolatedPath));

            await gitHost.DisposeIsolatedMergeCloneAsync(repoId, isolatedPath);

            Assert.False(Directory.Exists(isolatedPath));
            Assert.False(File.Exists(isolatedPath + ".mirror_metadata"));
        }
        finally
        {
            await gitHost.DisposeIsolatedMergeCloneAsync(repoId, isolatedPath);
        }
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

    [Fact]
    public async Task EnsureRepositoryAsync_SharedMirror_OfflineCloneAndResetOrigin()
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

        // Verify remote origin points back to seed
        var (_, originUrl, _) = await TestSupport.RunGit(barePath, "remote", "get-url", "origin");
        Assert.Equal(seed.Trim(), originUrl.Trim());

        // Verify alternates points to mirror path
        var alternatesPath = Path.Combine(barePath, "objects", "info", "alternates");
        var alternatesContent = await File.ReadAllTextAsync(alternatesPath);
        Assert.Contains(mirrorDir, alternatesContent);
    }

    [Fact]
    public async Task GetSandboxAccess_MountsMirrorReadOnly()
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

        var access = gitHost.GetSandboxAccess(repoId);

        // There should be two mounts: one writable for repo itself, and one read-only for mirror
        Assert.Equal(2, access.Mounts.Count);

        var writableRepoMount = access.Mounts.Single(m => !m.ReadOnly);
        Assert.Equal("/repo", writableRepoMount.SandboxPath);
        Assert.Equal(gitHost.GetRepoPath(repoId), writableRepoMount.HostPath);

        var roMirrorMount = access.Mounts.Single(m => m.ReadOnly);
        Assert.Contains(mirrorDir, roMirrorMount.HostPath);
        Assert.Equal(roMirrorMount.HostPath, roMirrorMount.SandboxPath);
    }

    [Fact]
    public async Task SanitizeAlternates_RemovesUntrustedPaths()
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
        var alternatesPath = Path.Combine(barePath, "objects", "info", "alternates");

        // Overwrite alternates file with a valid mirror path AND an untrusted path
        var validPath = (await File.ReadAllLinesAsync(alternatesPath))[0].Trim();
        var untrustedPath = Path.GetFullPath(Path.Combine(_workspace, "untrusted-folder"));
        await File.WriteAllLinesAsync(alternatesPath, [validPath, untrustedPath]);

        // Run any git command which will trigger SanitizeAlternates internally
        await gitHost.FetchUpstreamBranchAsync(repoId, seed, "main", new Dictionary<string, string>());

        // Verify that the untrusted path was removed
        var lines = await File.ReadAllLinesAsync(alternatesPath);
        Assert.Single(lines);
        Assert.Equal(validPath, lines[0].Trim());
    }

    [Fact]
    public async Task GetSandboxAccess_IgnoresModifiedAlternatesFile()
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
        var alternatesPath = Path.Combine(barePath, "objects", "info", "alternates");

        var validPath = (await File.ReadAllLinesAsync(alternatesPath))[0].Trim();
        var otherMirrorPath = Path.GetFullPath(Path.Combine(mirrorDir, "other-mirror.git", "objects"));

        // Overwrite alternates file with the other mirror path
        await File.WriteAllLinesAsync(alternatesPath, [otherMirrorPath]);

        // Request sandbox access
        var access = gitHost.GetSandboxAccess(repoId);

        // Verify that only the original valid mirror path is mounted, not otherMirrorPath!
        var roMirrorMount = access.Mounts.Single(m => m.ReadOnly);
        Assert.Equal(validPath, roMirrorMount.HostPath);
        Assert.NotEqual(otherMirrorPath, roMirrorMount.HostPath);
    }

    [Fact]
    public async Task SanitizeAlternates_DiscardsPathsNotInMetadata()
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
        var alternatesPath = Path.Combine(barePath, "objects", "info", "alternates");

        var validPath = (await File.ReadAllLinesAsync(alternatesPath))[0].Trim();
        var otherMirrorPath = Path.GetFullPath(Path.Combine(mirrorDir, "other-mirror.git", "objects"));

        // Overwrite alternates file with both valid path and other mirror path (which starts with mirrorDir but is not in metadata)
        await File.WriteAllLinesAsync(alternatesPath, [validPath, otherMirrorPath]);

        // Run any git command to trigger SanitizeAlternates
        await gitHost.FetchUpstreamBranchAsync(repoId, seed, "main", new Dictionary<string, string>());

        // Verify that otherMirrorPath was removed because it's not in metadata
        var lines = await File.ReadAllLinesAsync(alternatesPath);
        Assert.Single(lines);
        Assert.Equal(validPath, lines[0].Trim());
    }

    [Fact]
    public async Task SanitizeAlternates_ReadFailureFailsClosed()
    {
        if (OperatingSystem.IsWindows())
            return;

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

        var repoId = await gitHost.EnsureRepositoryAsync(WorkItemId.New(), seed, "main");
        var alternatesPath = Path.Combine(gitHost.GetRepoPath(repoId), "objects", "info", "alternates");
        File.SetUnixFileMode(alternatesPath, UnixFileMode.None);

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                gitHost.SanitizeRepositoryAlternates(repoId));
            Assert.Contains("Failed to sanitize git alternates", ex.Message);
        }
        finally
        {
            File.SetUnixFileMode(alternatesPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
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

    private static string MirrorRepoPath(string mirrorDir, string upstreamUrl)
    {
        var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(upstreamUrl));
        var sb = new System.Text.StringBuilder();
        foreach (var b in hashBytes) sb.Append(b.ToString("x2"));
        return Path.Combine(mirrorDir, sb.ToString() + ".git");
    }

    private sealed record GitInvocation(string WorkingDirectory, IReadOnlyList<string> GitArgs)
    {
        public static GitInvocation From(ProcessStartInfo startInfo)
            => new(
                startInfo.WorkingDirectory,
                startInfo.ArgumentList
                    .Skip(2)
                    .ToArray());
    }

    private sealed class RecordingLocalGitProcess : ILocalGitProcess
    {
        private readonly Process _process;

        public RecordingLocalGitProcess(ProcessStartInfo startInfo, List<GitInvocation> invocations)
        {
            invocations.Add(GitInvocation.From(startInfo));
            _process = new Process { StartInfo = startInfo };
        }

        public TextReader StandardOutput => _process.StandardOutput;
        public TextReader StandardError => _process.StandardError;
        public int ExitCode => _process.ExitCode;
        public void Start() => _process.Start();
        public Task WaitForExitAsync(CancellationToken ct) => _process.WaitForExitAsync(ct);
        public void Kill(bool entireProcessTree) => _process.Kill(entireProcessTree);
        public void Dispose() => _process.Dispose();
    }

    private sealed class FakeLocalGitProcess(
        int exitCode = 0,
        string stdout = "",
        string stderr = "",
        Action? onStart = null) : ILocalGitProcess
    {
        private readonly StringReader _stdout = new(stdout);
        private readonly StringReader _stderr = new(stderr);

        public TextReader StandardOutput => _stdout;
        public TextReader StandardError => _stderr;
        public int ExitCode { get; } = exitCode;

        public void Start() => onStart?.Invoke();

        public Task WaitForExitAsync(CancellationToken ct) => Task.CompletedTask;

        public void Kill(bool entireProcessTree) { }

        public void Dispose()
        {
            _stdout.Dispose();
            _stderr.Dispose();
        }
    }
}
