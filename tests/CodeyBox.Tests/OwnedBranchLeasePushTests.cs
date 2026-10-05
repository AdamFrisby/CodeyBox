using CodeyBox.Core;
using CodeyBox.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Lease-guarded rewrite semantics on <see cref="LocalGitHost"/> against a
/// local bare remote: a matching lease rewrites an owned branch, a moved
/// remote refuses without clobbering, and non-owned branches are refused
/// before git is ever touched.
/// </summary>
public sealed class OwnedBranchLeasePushTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-lease-").FullName;

    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    [Fact]
    public async Task GetUpstreamBranchShaAsync_AbsentBranch_ReturnsNull()
    {
        var (gitHost, repoId, upstreamBare) = await CreateSeededHostRepoAsync();

        var sha = await gitHost.GetUpstreamBranchShaAsync(
            repoId, upstreamBare, "codeybox/never-pushed", new Dictionary<string, string>());

        Assert.Null(sha);
    }

    [Fact]
    public async Task GetUpstreamBranchShaAsync_PresentBranch_ReturnsTip()
    {
        var (gitHost, repoId, upstreamBare) = await CreateSeededHostRepoAsync();
        var oldHead = await CommitToBranchAsync(upstreamBare, "codeybox/item", "old.txt", "old\n", "old work");

        var sha = await gitHost.GetUpstreamBranchShaAsync(
            repoId, upstreamBare, "codeybox/item", new Dictionary<string, string>());

        Assert.Equal(oldHead, sha);
    }

    [Fact]
    public async Task PushBranchWithLeaseAsync_MatchingLease_RewritesOwnedBranch()
    {
        var (gitHost, repoId, upstreamBare) = await CreateSeededHostRepoAsync();
        var oldHead = await CommitToBranchAsync(upstreamBare, "codeybox/item", "old.txt", "old\n", "old work");
        // Fresh base + new work in the host repo: diverged from the old head.
        var newHead = await ResetHostBranchOntoNewBaseAsync(
            gitHost.GetRepoPath(repoId), "codeybox/item", "new.txt", "new\n", "new work");

        await gitHost.PushBranchWithLeaseAsync(
            repoId, upstreamBare, "codeybox/item", oldHead, new Dictionary<string, string>());

        var (_, tip, _) = await TestSupport.RunGit(upstreamBare, "rev-parse", "codeybox/item");
        Assert.Equal(newHead, tip.Trim());
    }

    [Fact]
    public async Task PushBranchWithLeaseAsync_ThirdPartyMovedBranch_ThrowsWithoutClobbering()
    {
        var (gitHost, repoId, upstreamBare) = await CreateSeededHostRepoAsync();
        var oldHead = await CommitToBranchAsync(upstreamBare, "codeybox/item", "old.txt", "old\n", "old work");
        await ResetHostBranchOntoNewBaseAsync(
            gitHost.GetRepoPath(repoId), "codeybox/item", "new.txt", "new\n", "new work");
        var thirdPartyHead = await CommitToBranchAsync(
            upstreamBare, "codeybox/item", "third-party.txt", "third\n", "third party change");

        var ex = await Assert.ThrowsAsync<UpstreamLeaseMismatchException>(() =>
            gitHost.PushBranchWithLeaseAsync(
                repoId, upstreamBare, "codeybox/item", oldHead, new Dictionary<string, string>()));

        Assert.Equal("codeybox/item", ex.Branch);
        Assert.Equal(oldHead, ex.ExpectedSha);
        var (_, tip, _) = await TestSupport.RunGit(upstreamBare, "rev-parse", "codeybox/item");
        Assert.Equal(thirdPartyHead, tip.Trim());
    }

    [Theory]
    [InlineData("main")]
    [InlineData("release/1.0")]
    [InlineData("feature/work")]
    [InlineData("codeybox/")]
    [InlineData("Codeybox/item")]
    public async Task PushBranchWithLeaseAsync_NonOwnedBranch_RefusesBeforePush(string branch)
    {
        var (gitHost, repoId, upstreamBare) = await CreateSeededHostRepoAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            gitHost.PushBranchWithLeaseAsync(
                repoId, upstreamBare, branch, new string('a', 40), new Dictionary<string, string>()));

        Assert.Contains("codeybox/", ex.Message);
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

    /// <summary>Commits on <paramref name="branch"/> in a scratch clone and
    /// pushes to the bare repo. Tracks the remote branch when it exists so
    /// follow-up commits fast-forward; creates from main otherwise. Returns
    /// the new tip sha.</summary>
    private async Task<string> CommitToBranchAsync(
        string bareRepo, string branch, string path, string content, string message)
    {
        var clone = Path.Combine(_workspace, "work-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", bareRepo, clone);
        await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(clone, "config", "user.name", "Test");
        var (code, _, _) = await TestSupport.RunGitNoThrow(clone, "rev-parse", "--verify", $"origin/{branch}");
        await TestSupport.RunGit(clone, "checkout", "-B", branch, code == 0 ? $"origin/{branch}" : "main");
        await File.WriteAllTextAsync(Path.Combine(clone, path), content);
        await TestSupport.RunGit(clone, "add", path);
        await TestSupport.RunGit(clone, "commit", "-m", message);
        await TestSupport.RunGit(clone, "push", "origin", branch);
        var (_, sha, _) = await TestSupport.RunGit(clone, "rev-parse", "HEAD");
        return sha.Trim();
    }

    /// <summary>Simulates a retry on a fresh base: advances main, resets the
    /// work branch onto it, and commits new work. Returns the new tip sha.</summary>
    private async Task<string> ResetHostBranchOntoNewBaseAsync(
        string hostBare, string branch, string path, string content, string message)
    {
        var clone = Path.Combine(_workspace, "host-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", hostBare, clone);
        await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(clone, "config", "user.name", "Test");
        await TestSupport.RunGit(clone, "checkout", "main");
        await File.WriteAllTextAsync(Path.Combine(clone, "base2.txt"), "fresh base\n");
        await TestSupport.RunGit(clone, "add", "base2.txt");
        await TestSupport.RunGit(clone, "commit", "-m", "fresh base");
        await TestSupport.RunGit(clone, "push", "origin", "main");
        await TestSupport.RunGit(clone, "checkout", "-B", branch, "main");
        await File.WriteAllTextAsync(Path.Combine(clone, path), content);
        await TestSupport.RunGit(clone, "add", path);
        await TestSupport.RunGit(clone, "commit", "-m", message);
        await TestSupport.RunGit(clone, "push", "--force", "origin", branch);
        // The host bare repo's main ref is stale after the clone pushed the
        // fresh base; fetch it so ancestry checks see the real base.
        await TestSupport.RunGit(hostBare, "fetch", clone, "main:main");
        var (_, sha, _) = await TestSupport.RunGit(clone, "rev-parse", "HEAD");
        return sha.Trim();
    }
}
