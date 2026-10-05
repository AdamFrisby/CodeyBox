using System.Net;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Upstream.GitHub;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Owned-branch push semantics through the real <see cref="LocalGitHost"/>
/// and the real <see cref="GitHubUpstreamRemote"/> against a local bare
/// remote (rewritten from the GitHub https URL by
/// <see cref="GitHubUrlRewriteShim"/>) and a scripted fake GitHub API:
/// a stale remote on a <c>codeybox/*</c> branch is rewritten only under
/// <c>--force-with-lease</c>, third-party motion parks without clobbering,
/// and a stale closed PR is superseded by a fresh linked PR.
/// </summary>
public sealed class GitHubOwnedBranchPushTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-gh-lease-").FullName;

    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    private static string NewBranch() => "codeybox/work-" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task LeasePush_RewritesStaleRemoteAndCompletesMerge()
    {
        var workBranch = NewBranch();
        using var setup = await SetupAsync();
        // Old head pushed by the previous attempt; the new head is composed
        // on a fresh base and shares no history with it.
        var oldHead = await CommitBranchFileAsync(setup.UpstreamBare, workBranch, "old.txt", "old\n", "old work");
        var newHead = await RecomposeHostBranchOnFreshBaseAsync(setup.HostPath, workBranch);

        var (owner, repo) = SplitGithubUrl(setup.GithubUrl);
        // Script the fake forge precisely once the shas are known:
        // create → 422, open listing → #612, detail → open at pushed tip,
        // merge PUT → merged.
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(ValidationFailed());
        handler.Enqueue(JsonList(new[] { new { number = 612 } })); // open PR listing
        handler.Enqueue(Json(new // GET /pulls/612: open, head == pushed tip
        {
            number = 612,
            html_url = $"https://github.com/{owner}/{repo}/pull/612",
            state = "open",
            merged = false,
            mergeable = true,
            mergeable_state = "clean",
            head = new { @ref = workBranch, sha = newHead, user = new { login = owner } },
            @base = new { @ref = "main", sha = "basesha" },
        }));
        handler.Enqueue(JsonList(Array.Empty<object>())); // GET /pulls/612/commits for squash message
        handler.Enqueue(Json(new // PUT /pulls/612/merge
        {
            sha = "merge-sha-612",
            merged = true,
        }));
        var remote = BuildRemote(setup.Host, handler, owner, repo, setup.Token, AutoMerge: true);

        var outcome = await remote.CompleteAsync(
            SampleRequest(setup.RepoId, workBranch, "squash", ExpectedRemoteHeadSha: oldHead),
            CancellationToken.None);

        Assert.Equal("merge-sha-612", outcome.MergedSha);
        Assert.Equal(612, outcome.PullRequestNumber);
        Assert.Equal(newHead, outcome.PushedWorkBranchSha);
        var (_, tip, _) = await TestSupport.RunGit(setup.UpstreamBare, "rev-parse", workBranch);
        Assert.Equal(newHead, tip.Trim());
        Assert.Contains(setup.Shim.Invocations, i => i.Contains("--force-with-lease="));
    }

    [Fact]
    public async Task ThirdPartyPush_LeaseMismatchParksWithoutClobber()
    {
        var workBranch = NewBranch();
        using var setup = await SetupAsync();
        var oldHead = await CommitBranchFileAsync(setup.UpstreamBare, workBranch, "old.txt", "old\n", "old work");
        var newHead = await RecomposeHostBranchOnFreshBaseAsync(setup.HostPath, workBranch);
        var thirdPartyHead = await CommitBranchFileAsync(
            setup.UpstreamBare, workBranch, "third-party.txt", "third\n", "third party change");

        var (owner, repo) = SplitGithubUrl(setup.GithubUrl);
        var handler = new FakeHttpMessageHandler();
        var remote = BuildRemote(setup.Host, handler, owner, repo, setup.Token, AutoMerge: true);

        var ex = await Assert.ThrowsAsync<UpstreamLeaseMismatchException>(() =>
            remote.CompleteAsync(
                SampleRequest(setup.RepoId, workBranch, "squash", ExpectedRemoteHeadSha: oldHead),
                CancellationToken.None));

        Assert.Equal(workBranch, ex.Branch);
        Assert.Equal(oldHead, ex.ExpectedSha);
        // Nothing clobbered: the third-party tip is intact and no PR API call
        // was ever issued (the push guard fired first).
        var (_, tip, _) = await TestSupport.RunGit(setup.UpstreamBare, "rev-parse", workBranch);
        Assert.Equal(thirdPartyHead, tip.Trim());
        Assert.NotEqual(newHead, tip.Trim());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DivergedWithoutRecord_ParksDistinctFromMergeConflict()
    {
        var workBranch = NewBranch();
        using var setup = await SetupAsync();
        var oldHead = await CommitBranchFileAsync(setup.UpstreamBare, workBranch, "old.txt", "old\n", "old work");
        await RecomposeHostBranchOnFreshBaseAsync(setup.HostPath, workBranch);

        var (owner, repo) = SplitGithubUrl(setup.GithubUrl);
        var handler = new FakeHttpMessageHandler();
        var remote = BuildRemote(setup.Host, handler, owner, repo, setup.Token, AutoMerge: true);

        // No ExpectedRemoteHeadSha: CodeyBox cannot prove the remote tip is
        // its own history, so it must refuse — distinctly from a merge
        // conflict, which would route into conflict-rework.
        var ex = await Assert.ThrowsAsync<UpstreamOwnedBranchDivergedException>(() =>
            remote.CompleteAsync(
                SampleRequest(setup.RepoId, workBranch, "squash", ExpectedRemoteHeadSha: null),
                CancellationToken.None));

        Assert.Equal(workBranch, ex.Branch);
        Assert.Equal(oldHead, ex.RemoteSha);
        Assert.Contains("not a merge conflict", ex.Message);
        Assert.False(UpstreamPushReconcileConflictException.TryFindIn(ex, out _));
        var (_, tip, _) = await TestSupport.RunGit(setup.UpstreamBare, "rev-parse", workBranch);
        Assert.Equal(oldHead, tip.Trim());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task StaleClosedPr_OpensFreshPrAndLinksOld()
    {
        var workBranch = NewBranch();
        using var setup = await SetupAsync();
        // Remote already at the local tip: the push is a no-op and the whole
        // test exercises PR recovery.
        var tip = await CommitBranchFileAsync(setup.UpstreamBare, workBranch, "work.txt", "work\n", "work");
        // Mirror the pushed tip into the host bare repo so the push step is a
        // no-op and the test exercises PR recovery only.
        await TestSupport.RunGit(setup.HostPath, "fetch", setup.UpstreamBare, $"{workBranch}:refs/heads/{workBranch}");

        var (owner, repo) = SplitGithubUrl(setup.GithubUrl);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(ValidationFailed()); // initial POST /pulls → 422
        handler.Enqueue(JsonList(Array.Empty<object>())); // no open PR for the branch
        handler.Enqueue(JsonList(new[] { new { number = 611 } })); // closed PR listing
        handler.Enqueue(Json(new // GET /pulls/611: closed, head == pushed tip
        {
            number = 611,
            html_url = $"https://github.com/{owner}/{repo}/pull/611",
            state = "closed",
            merged = false,
            head = new { @ref = workBranch, sha = tip, user = new { login = owner } },
            @base = new { @ref = "main", sha = "basesha" },
        }));
        handler.Enqueue(Json(new // recovery POST /pulls → fresh PR
        {
            number = 612,
            html_url = $"https://github.com/{owner}/{repo}/pull/612",
        }));
        handler.Enqueue(Json(new { id = 1 })); // POST comment on #611
        handler.Enqueue(Json(new { number = 611, state = "closed" })); // PATCH close #611
        var remote = BuildRemote(setup.Host, handler, owner, repo, setup.Token, AutoMerge: false);

        var outcome = await remote.CompleteAsync(
            SampleRequest(setup.RepoId, workBranch, "squash", ExpectedRemoteHeadSha: tip),
            CancellationToken.None);

        Assert.Equal(612, outcome.PullRequestNumber);
        Assert.Equal($"https://github.com/{owner}/{repo}/pull/612", outcome.PullRequestUrl);
        var comment = handler.Requests
            .Where(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/issues/611/comments"))
            .ToList();
        Assert.Single(comment);
        Assert.Contains("#612", handler.RequestBodies[handler.Requests.IndexOf(comment[0])]);
        var close = handler.Requests
            .Where(r => r.Method.Method == "PATCH" && r.RequestUri!.AbsolutePath.EndsWith("/pulls/611"))
            .ToList();
        Assert.Single(close);
        Assert.Contains("closed", handler.RequestBodies[handler.Requests.IndexOf(close[0])]);
    }

    [Fact]
    public async Task NonOwnedBranch_UsesPlainPushWithoutLease()
    {
        // Branches outside codeybox/* keep the historical plain push: no
        // lease, no force, no ownership refusal.
        var workBranch = "feature/not-owned-" + Guid.NewGuid().ToString("N")[..8];
        using var setup = await SetupAsync();
        var tip = await CommitBranchFileAsync(setup.HostPath, workBranch, "work.txt", "work\n", "work");
        // Mirror into the upstream bare so the plain push fast-forwards.
        await TestSupport.RunGit(setup.UpstreamBare, "fetch", setup.HostPath, $"{workBranch}:refs/heads/{workBranch}");

        var (owner, repo) = SplitGithubUrl(setup.GithubUrl);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(Json(new
        {
            number = 11,
            html_url = $"https://github.com/{owner}/{repo}/pull/11",
        }));
        var remote = BuildRemote(setup.Host, handler, owner, repo, setup.Token, AutoMerge: false);

        var outcome = await remote.CompleteAsync(
            SampleRequest(setup.RepoId, workBranch, "squash", ExpectedRemoteHeadSha: null),
            CancellationToken.None);

        Assert.True(outcome.BranchPushed);
        Assert.Equal(11, outcome.PullRequestNumber);
        Assert.Equal(tip, outcome.PushedWorkBranchSha);
        Assert.DoesNotContain(
            setup.Shim.Invocations,
            i => i.Contains("--force-with-lease=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OwnedBranchEqualToBase_RefusesBeforePush()
    {
        // Even an owned name is refused when it IS the base branch: base
        // branches are never force-pushed.
        using var setup = await SetupAsync();
        await CommitBranchFileAsync(setup.HostPath, "codeybox/base-work", "work.txt", "work\n", "work");
        var (owner, repo) = SplitGithubUrl(setup.GithubUrl);
        var handler = new FakeHttpMessageHandler();
        var remote = BuildRemote(setup.Host, handler, owner, repo, setup.Token, AutoMerge: false);

        var invocationsBefore = setup.Shim.Invocations.Count;
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            remote.CompleteAsync(
                new UpstreamCompletionRequest
                {
                    RepositoryId = setup.RepoId,
                    WorkItemId = WorkItemId.New(),
                    ProjectId = new ProjectId("test-project"),
                    WorkBranch = "codeybox/base-work",
                    BaseBranch = "codeybox/base-work",
                    Title = "t",
                    Description = "d",
                    MergeMethod = "squash",
                },
                CancellationToken.None));
        Assert.Contains("base branch", ex.Message);

        Assert.Empty(handler.Requests);
        Assert.Equal(invocationsBefore, setup.Shim.Invocations.Count);
    }

    private static UpstreamCompletionRequest SampleRequest(
        string repoId, string workBranch, string mergeMethod, string? ExpectedRemoteHeadSha) => new()
        {
            RepositoryId = repoId,
            WorkItemId = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            WorkBranch = workBranch,
            BaseBranch = "main",
            MergeSha = null,
            Title = "owned branch push",
            Description = "desc",
            MergeMethod = mergeMethod,
            ExpectedRemoteHeadSha = ExpectedRemoteHeadSha,
        };

    private static GitHubUpstreamRemote BuildRemote(
        IGitHost host, FakeHttpMessageHandler handler, string owner, string repo, string token, bool AutoMerge)
        => new(
            host,
            new FakeHttpClientFactory(handler),
            NullLogger<GitHubUpstreamRemote>.Instance,
            new GitHubUpstreamOptions { Owner = owner, Repository = repo, Token = token, MergeMethod = "squash", AutoMerge = AutoMerge });

    private static (string Owner, string Repo) SplitGithubUrl(string githubUrl)
    {
        var uri = new Uri(githubUrl);
        var segs = uri.AbsolutePath.Trim('/').Split('/');
        return (segs[0], segs[1].Replace(".git", string.Empty, StringComparison.Ordinal));
    }

    private static HttpResponseMessage ValidationFailed() =>
        new(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent(
                """{"message":"Validation Failed","errors":[{"resource":"PullRequest","code":"custom","message":"A pull request already exists"}]}""",
                Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage JsonList<T>(T body) => Json(body!);

    private sealed record OwnedBranchSetup(
        LocalGitHost Host,
        string HostPath,
        string RepoId,
        string UpstreamBare,
        GitHubUrlRewriteShim Shim,
        string GithubUrl,
        string Token) : IDisposable
    {
        public void Dispose() => Shim.Dispose();
    }

    private async Task<OwnedBranchSetup> SetupAsync()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var upstreamBare = Path.Combine(_workspace, "upstream-" + Guid.NewGuid().ToString("N")[..8] + ".git");
        await TestSupport.RunGit(_workspace, "clone", "--bare", "--local", seed, upstreamBare);

        var owner = "o" + Guid.NewGuid().ToString("N")[..12];
        var repo = "r" + Guid.NewGuid().ToString("N")[..12];
        var githubUrl = $"https://github.com/{owner}/{repo}.git";
        var token = "tok_owned_" + Guid.NewGuid().ToString("N")[..12];
        var shim = new GitHubUrlRewriteShim(_workspace, githubUrl, upstreamBare);
        var host = new LocalGitHost(
            new LocalGitHostOptions
            {
                RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]),
                GitExecutable = shim.ShimPath,
            },
            NullLogger<LocalGitHost>.Instance);
        var repoId = await host.EnsureRepositoryAsync(WorkItemId.New(), upstreamBare);
        return new OwnedBranchSetup(host, host.GetRepoPath(repoId), repoId, upstreamBare, shim, githubUrl, token);
    }

    private async Task<string> CommitBranchFileAsync(string barePath, string branch, string file, string content, string message)
    {
        var clone = Path.Combine(_workspace, "obw-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await TestSupport.RunGit(_workspace, "clone", barePath, clone);
            await TestSupport.RunGit(clone, "config", "user.email", "t@t");
            await TestSupport.RunGit(clone, "config", "user.name", "T");
            var (code, _, _) = await TestSupport.RunGitNoThrow(clone, "rev-parse", "--verify", $"origin/{branch}");
            await TestSupport.RunGit(clone, "checkout", "-B", branch, code == 0 ? $"origin/{branch}" : "main");
            await File.WriteAllTextAsync(Path.Combine(clone, file), content);
            await TestSupport.RunGit(clone, "add", file);
            await TestSupport.RunGit(clone, "commit", "-m", message);
            await TestSupport.RunGit(clone, "push", "origin", branch);
            var (_, sha, _) = await TestSupport.RunGit(clone, "rev-parse", "HEAD");
            return sha.Trim();
        }
        finally
        {
            if (Directory.Exists(clone))
                Directory.Delete(clone, recursive: true);
        }
    }

    /// <summary>Simulates a retry on a fresh base inside the host repo: moves
    /// main forward, recomposes the work branch onto it, and returns the new
    /// tip. The new head shares no history with any previously pushed head.
    /// </summary>
    private async Task<string> RecomposeHostBranchOnFreshBaseAsync(string hostBare, string branch)
    {
        var clone = Path.Combine(_workspace, "obh-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await TestSupport.RunGit(_workspace, "clone", hostBare, clone);
            await TestSupport.RunGit(clone, "config", "user.email", "t@t");
            await TestSupport.RunGit(clone, "config", "user.name", "T");
            await TestSupport.RunGit(clone, "checkout", "main");
            await File.WriteAllTextAsync(Path.Combine(clone, "fresh-base.txt"), "fresh base\n");
            await TestSupport.RunGit(clone, "add", "fresh-base.txt");
            await TestSupport.RunGit(clone, "commit", "-m", "fresh base");
            await TestSupport.RunGit(clone, "push", "origin", "main");
            await TestSupport.RunGit(clone, "checkout", "-B", branch, "main");
            await File.WriteAllTextAsync(Path.Combine(clone, "new-work.txt"), "new work\n");
            await TestSupport.RunGit(clone, "add", "new-work.txt");
            await TestSupport.RunGit(clone, "commit", "-m", "new work on fresh base");
            await TestSupport.RunGit(clone, "push", "--force", "origin", branch);
            var (_, sha, _) = await TestSupport.RunGit(clone, "rev-parse", "HEAD");
            return sha.Trim();
        }
        finally
        {
            if (Directory.Exists(clone))
                Directory.Delete(clone, recursive: true);
        }
    }
}
