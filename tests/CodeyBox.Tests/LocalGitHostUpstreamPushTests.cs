using CodeyBox.Core;
using CodeyBox.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class LocalGitHostUpstreamPushTests : IDisposable
{
    private readonly string _workspace;

    public LocalGitHostUpstreamPushTests() =>
        _workspace = Directory.CreateTempSubdirectory("codeybox-upstream-push-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task PushToUpstreamAsync_OnNonFastForward_FetchesRebasesAndRetriesOnce()
    {
        var (gitHost, repoId, upstreamBare) = await CreateSeededHostRepoAsync();
        await CommitToHostRepoAsync(gitHost.GetRepoPath(repoId), "agent.txt", "agent\n", "local agent change");
        await CommitToUpstreamAsync(upstreamBare, "remote.txt", "remote\n", "remote upstream change");

        await gitHost.PushToUpstreamAsync(repoId, upstreamBare, "main", new Dictionary<string, string>());

        var (_, agentFile, _) = await TestSupport.RunGit(upstreamBare, "show", "main:agent.txt");
        var (_, remoteFile, _) = await TestSupport.RunGit(upstreamBare, "show", "main:remote.txt");
        var (_, subjects, _) = await TestSupport.RunGit(upstreamBare, "log", "--format=%s", "--max-count=3", "main");

        Assert.Equal("agent\n", agentFile);
        Assert.Equal("remote\n", remoteFile);
        Assert.Equal(
            ["local agent change", "remote upstream change", "initial"],
            subjects.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task PushToUpstreamAsync_WhenRebaseConflicts_AbortsAndReportsClearError()
    {
        var (gitHost, repoId, upstreamBare) = await CreateSeededHostRepoAsync();
        await CommitToHostRepoAsync(gitHost.GetRepoPath(repoId), "README.md", "local\n", "local conflicting change");
        await CommitToUpstreamAsync(upstreamBare, "README.md", "remote\n", "remote conflicting change");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gitHost.PushToUpstreamAsync(repoId, upstreamBare, "main", new Dictionary<string, string>()));

        Assert.Contains("upstream rebase conflict on main; manual resolution required", ex.Message);
        var (_, upstreamReadme, _) = await TestSupport.RunGit(upstreamBare, "show", "main:README.md");
        var (_, localReadme, _) = await TestSupport.RunGit(gitHost.GetRepoPath(repoId), "show", "main:README.md");
        var (_, worktrees, _) = await TestSupport.RunGit(gitHost.GetRepoPath(repoId), "worktree", "list", "--porcelain");

        Assert.Equal("remote\n", upstreamReadme);
        Assert.Equal("local\n", localReadme);
        Assert.DoesNotContain("codeybox-upstream-reconcile", worktrees);
    }

    [Fact]
    public async Task PushToUpstreamAsync_WithMergeStrategy_PullsNoRebaseBeforeRetrying()
    {
        var (gitHost, repoId, upstreamBare) = await CreateSeededHostRepoAsync();
        await CommitToHostRepoAsync(gitHost.GetRepoPath(repoId), "agent.txt", "agent\n", "local agent change");
        await CommitToUpstreamAsync(upstreamBare, "remote.txt", "remote\n", "remote upstream change");

        await gitHost.PushToUpstreamAsync(
            repoId,
            upstreamBare,
            "main",
            new Dictionary<string, string>(),
            UpstreamPushReconcileStrategy.Merge);

        var (_, agentFile, _) = await TestSupport.RunGit(upstreamBare, "show", "main:agent.txt");
        var (_, remoteFile, _) = await TestSupport.RunGit(upstreamBare, "show", "main:remote.txt");
        var (_, headParents, _) = await TestSupport.RunGit(upstreamBare, "rev-list", "--parents", "-n", "1", "main");

        Assert.Equal("agent\n", agentFile);
        Assert.Equal("remote\n", remoteFile);
        Assert.Equal(3, headParents.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    private async Task<(LocalGitHost GitHost, string RepoId, string UpstreamBare)> CreateSeededHostRepoAsync()
    {
        var upstreamWork = await TestSupport.CreateSeedRepoAsync(_workspace, "upstream-work");
        var upstreamBare = Path.Combine(_workspace, "upstream-" + Guid.NewGuid().ToString("N")[..8] + ".git");
        await TestSupport.RunGit(_workspace, "clone", "--bare", "--local", upstreamWork, upstreamBare);

        var gitRoot = Path.Combine(_workspace, "repos");
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var repoId = await gitHost.EnsureRepositoryAsync(WorkItemId.New(), upstreamBare);
        return (gitHost, repoId, upstreamBare);
    }

    private async Task CommitToHostRepoAsync(string bareRepoPath, string path, string content, string message)
    {
        var clone = Path.Combine(_workspace, "host-work-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", bareRepoPath, clone);
        await ConfigureIdentityAsync(clone);
        await File.WriteAllTextAsync(Path.Combine(clone, path), content);
        await TestSupport.RunGit(clone, "add", path);
        await TestSupport.RunGit(clone, "commit", "-m", message);
        await TestSupport.RunGit(clone, "push", "origin", "main");
    }

    private async Task CommitToUpstreamAsync(string upstreamBare, string path, string content, string message)
    {
        var clone = Path.Combine(_workspace, "upstream-work-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", upstreamBare, clone);
        await ConfigureIdentityAsync(clone);
        await File.WriteAllTextAsync(Path.Combine(clone, path), content);
        await TestSupport.RunGit(clone, "add", path);
        await TestSupport.RunGit(clone, "commit", "-m", message);
        await TestSupport.RunGit(clone, "push", "origin", "main");
    }

    private static async Task ConfigureIdentityAsync(string repoPath)
    {
        await TestSupport.RunGit(repoPath, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(repoPath, "config", "user.name", "Test");
    }
}
