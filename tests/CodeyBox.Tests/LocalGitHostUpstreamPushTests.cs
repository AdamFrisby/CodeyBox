using CodeyBox.Core;
using CodeyBox.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class LocalGitHostUpstreamPushTests : IDisposable
{
    private readonly string _workspace;

    public LocalGitHostUpstreamPushTests()
    {
        _workspace = Directory.CreateTempSubdirectory("codeybox-upstream-push-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task PushToUpstreamAsync_WhenRemoteTipUnchanged_PushesSuccessfully()
    {
        var upstream = await CreateBareUpstreamAsync();
        var host = NewHost();
        var repoId = await host.EnsureRepositoryAsync(WorkItemId.New(), upstream);
        var hostBare = host.GetRepoPath(repoId);

        await CommitToRemoteBranchAsync(hostBare, "main", "agent.txt", "agent\n", "agent change");

        await host.PushToUpstreamAsync(repoId, upstream, "main", new Dictionary<string, string>());

        var (_, blob, _) = await TestSupport.RunGit(upstream, "show", "main:agent.txt");
        Assert.Equal("agent\n", blob);
    }

    [Fact]
    public async Task PushToUpstreamAsync_WhenRemoteTipIsStale_FetchesRebasesAndRetries()
    {
        var upstream = await CreateBareUpstreamAsync();
        var host = NewHost();
        var repoId = await host.EnsureRepositoryAsync(WorkItemId.New(), upstream);
        var hostBare = host.GetRepoPath(repoId);

        await CommitToRemoteBranchAsync(hostBare, "main", "agent.txt", "agent\n", "agent change");
        await CommitToRemoteBranchAsync(upstream, "main", "human.txt", "human\n", "human change");

        await host.PushToUpstreamAsync(repoId, upstream, "main", new Dictionary<string, string>());

        var (_, agentBlob, _) = await TestSupport.RunGit(upstream, "show", "main:agent.txt");
        var (_, humanBlob, _) = await TestSupport.RunGit(upstream, "show", "main:human.txt");
        var (_, subjects, _) = await TestSupport.RunGit(upstream, "log", "--format=%s", "-3", "main");

        Assert.Equal("agent\n", agentBlob);
        Assert.Equal("human\n", humanBlob);
        Assert.StartsWith("agent change\nhuman change\n", subjects, StringComparison.Ordinal);
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(hostBare)!, ".upstream-rebase-*"));
    }

    [Fact]
    public async Task PushToUpstreamAsync_WhenRebaseConflicts_AbortsAndSurfacesClearFailure()
    {
        var upstream = await CreateBareUpstreamAsync();
        await CommitToRemoteBranchAsync(upstream, "main", "conflict.txt", "base\n", "add conflict base");

        var host = NewHost();
        var repoId = await host.EnsureRepositoryAsync(WorkItemId.New(), upstream);
        var hostBare = host.GetRepoPath(repoId);

        await CommitToRemoteBranchAsync(hostBare, "main", "conflict.txt", "agent\n", "agent change");
        await CommitToRemoteBranchAsync(upstream, "main", "conflict.txt", "human\n", "human change");

        var ex = await Assert.ThrowsAsync<UpstreamRebaseConflictException>(() =>
            host.PushToUpstreamAsync(repoId, upstream, "main", new Dictionary<string, string>()));

        Assert.Contains("upstream rebase conflict on main; manual resolution required", ex.Message);
        var (_, upstreamBlob, _) = await TestSupport.RunGit(upstream, "show", "main:conflict.txt");
        Assert.Equal("human\n", upstreamBlob);
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(hostBare)!, ".upstream-rebase-*"));
    }

    [Fact]
    public async Task PushToUpstreamAsync_WhenPushFailsForOtherCause_DoesNotRunRecovery()
    {
        var upstream = await CreateBareUpstreamAsync();
        var host = NewHost();
        var repoId = await host.EnsureRepositoryAsync(WorkItemId.New(), upstream);
        var hostBare = host.GetRepoPath(repoId);
        var missingRemote = Path.Combine(_workspace, "missing.git");

        await CommitToRemoteBranchAsync(hostBare, "main", "agent.txt", "agent\n", "agent change");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.PushToUpstreamAsync(repoId, missingRemote, "main", new Dictionary<string, string>()));

        Assert.Contains("git push to upstream failed", ex.Message);
        Assert.DoesNotContain("non-fast-forward recovery", ex.Message);
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(hostBare)!, ".upstream-rebase-*"));
    }

    private LocalGitHost NewHost()
        => new(new LocalGitHostOptions
        {
            RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]),
        }, NullLogger<LocalGitHost>.Instance);

    private async Task<string> CreateBareUpstreamAsync()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var upstream = Path.Combine(_workspace, "upstream-" + Guid.NewGuid().ToString("N")[..8] + ".git");
        await TestSupport.RunGit(_workspace, "clone", "--bare", "--local", seed, upstream);
        return upstream;
    }

    private async Task CommitToRemoteBranchAsync(
        string remote,
        string branch,
        string fileName,
        string contents,
        string message)
    {
        var workdir = Path.Combine(_workspace, "commit-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", remote, workdir);
        await TestSupport.RunGit(workdir, "config", "user.email", "test@example.invalid");
        await TestSupport.RunGit(workdir, "config", "user.name", "Test User");
        await TestSupport.RunGit(workdir, "checkout", branch);
        await File.WriteAllTextAsync(Path.Combine(workdir, fileName), contents);
        await TestSupport.RunGit(workdir, "add", fileName);
        await TestSupport.RunGit(workdir, "commit", "-m", message);
        await TestSupport.RunGit(workdir, "push", "origin", $"HEAD:{branch}");
    }
}
