using System.Diagnostics;
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
/// Transport shim: rewrites a single GitHub https URL to a local bare path
/// and execs the real git binary for everything else. Lets tests drive a
/// genuine diverged work-branch conflict through the real
/// <see cref="LocalGitHost"/> and the real
/// <see cref="GitHubUpstreamRemote"/> with no external network. The shim only
/// rewrites the transport URL (exact argv match); merge/rebase/conflict
/// detection all run in real git. Each instance logs every invocation so
/// tests can prove bounded attempts instead of generic retries.
/// </summary>
internal sealed class GitHubUrlRewriteShim : IDisposable
{
    public string ShimPath { get; }
    public string LogPath { get; }

    public GitHubUrlRewriteShim(string workspace, string githubUrl, string localPath)
    {
        var realGit = ResolveRealGit();
        ShimPath = Path.Combine(workspace, "git-shim-" + Guid.NewGuid().ToString("N")[..8] + ".sh");
        LogPath = Path.Combine(workspace, "git-shim-log-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        File.WriteAllText(LogPath, string.Empty);
        var script = "#!/bin/bash\n"
            + $"REAL_GIT=\"{realGit}\"\n"
            + $"WANT=\"{githubUrl}\"\n"
            + $"HAVE=\"{localPath}\"\n"
            + $"LOG=\"{LogPath}\"\n"
            + "args=()\n"
            + "for a in \"$@\"; do\n"
            + "  if [ \"$a\" = \"$WANT\" ]; then a=\"$HAVE\"; fi\n"
            + "  args+=(\"$a\")\n"
            + "done\n"
            + "printf '%s\\n' \"$*\" >> \"$LOG\"\n"
            + "exec \"$REAL_GIT\" \"${args[@]}\"\n";
        File.WriteAllText(ShimPath, script);
        MakeExecutable(ShimPath);
    }

    public IReadOnlyList<string> Invocations =>
        File.Exists(LogPath) ? File.ReadAllLines(LogPath) : [];

    public void Dispose()
    {
        foreach (var path in new[] { ShimPath, LogPath })
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void MakeExecutable(string path)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "chmod",
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("+x");
        psi.ArgumentList.Add(path);
        using var p = Process.Start(psi);
        p?.WaitForExit();
    }

    private static string ResolveRealGit()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("command -v git");
        using var p = Process.Start(psi)!;
        var path = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return string.IsNullOrEmpty(path) ? "/usr/bin/git" : path;
    }
}

internal sealed class GitHubTypedConflictTestFactory : IUpstreamRemoteFactory
{
    private readonly Func<Project, IUpstreamRemote> _create;
    public GitHubTypedConflictTestFactory(Func<Project, IUpstreamRemote> create) => _create = create;
    public IUpstreamRemote Create(Project project) => _create(project);
}

/// <summary>
/// Adapter-level regression: diverged <c>codeybox/*</c> work branches through
/// the real <see cref="LocalGitHost"/> and the real
/// <see cref="GitHubUpstreamRemote"/> surface the owned-branch typed
/// contracts without credential leakage — never a flattened message-only
/// <see cref="InvalidOperationException"/>, and never a reconcile-conflict
/// routed into conflict-rework (a diverged owned branch is not a merge
/// conflict). A recorded prior push rewrites the stale tip under
/// <c>--force-with-lease</c> instead of resurrecting it.
/// </summary>
public sealed class GitHubUpstreamTypedConflictAdapterTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-gh-typed-").FullName;
    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    private static string NewBranch() => "codeybox/work-" + Guid.NewGuid().ToString("N")[..8];

    private static UpstreamCompletionRequest SampleRequest(string repoId, string workBranch, string mergeMethod) => new()
    {
        RepositoryId = repoId,
        WorkItemId = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        WorkBranch = workBranch,
        BaseBranch = "main",
        MergeSha = null,
        Title = "typed conflict",
        Description = "desc",
        MergeMethod = mergeMethod,
    };

    private async Task<(LocalGitHost Host, string RepoId, string UpstreamBare, GitHubUrlRewriteShim Shim, string GithubUrl, string Token, FakeHttpMessageHandler Handler)> SetupDivergedAsync(
        string workBranch, string localContent, string remoteContent)
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var upstreamBare = Path.Combine(_workspace, "upstream-" + Guid.NewGuid().ToString("N")[..8] + ".git");
        await TestSupport.RunGit(_workspace, "clone", "--bare", "--local", seed, upstreamBare);

        var owner = "o" + Guid.NewGuid().ToString("N")[..12];
        var repo = "r" + Guid.NewGuid().ToString("N")[..12];
        var githubUrl = $"https://github.com/{owner}/{repo}.git";
        var token = "tok_canary_" + Guid.NewGuid().ToString("N")[..12];
        var shim = new GitHubUrlRewriteShim(_workspace, githubUrl, upstreamBare);
        var host = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]), GitExecutable = shim.ShimPath },
            NullLogger<LocalGitHost>.Instance);
        var repoId = await host.EnsureRepositoryAsync(WorkItemId.New(), upstreamBare);

        await CommitBranchFileAsync(host.GetRepoPath(repoId), workBranch, "conflict.txt", localContent, "local work");
        await CommitBranchFileAsync(upstreamBare, workBranch, "conflict.txt", remoteContent, "remote work");

        var handler = new FakeHttpMessageHandler();
        return (host, repoId, upstreamBare, shim, githubUrl, token, handler);
    }

    private static async Task CommitBranchFileAsync(string barePath, string branch, string file, string content, string message)
    {
        var clone = Path.Combine(Path.GetTempPath(), "codeybox-tc-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await TestSupport.RunGit(Path.GetTempPath(), "clone", barePath, clone);
            await TestSupport.RunGit(clone, "config", "user.email", "t@t");
            await TestSupport.RunGit(clone, "config", "user.name", "T");
            await TestSupport.RunGit(clone, "checkout", "-b", branch);
            await File.WriteAllTextAsync(Path.Combine(clone, file), content);
            await TestSupport.RunGit(clone, "add", file);
            await TestSupport.RunGit(clone, "commit", "-m", message);
            await TestSupport.RunGit(clone, "push", "origin", branch);
        }
        finally
        {
            if (Directory.Exists(clone))
                Directory.Delete(clone, recursive: true);
        }
    }

    private static GitHubUpstreamRemote BuildRemote(
        IGitHost host, FakeHttpMessageHandler handler, string owner, string repo, string token, string mergeMethod)
    {
        var factory = new FakeHttpClientFactory(handler);
        return new GitHubUpstreamRemote(
            host,
            factory,
            NullLogger<GitHubUpstreamRemote>.Instance,
            new GitHubUpstreamOptions { Owner = owner, Repository = repo, Token = token, MergeMethod = mergeMethod, AutoMerge = false });
    }

    private static (string Owner, string Repo) SplitGithubUrl(string githubUrl)
    {
        var uri = new Uri(githubUrl);
        var segs = uri.AbsolutePath.Trim('/').Split('/');
        return (segs[0], segs[1].Replace(".git", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RebaseDivergenceWithoutLeaseRecord_ParksDistinctFromConflict()
    {
        // Owned-branch rule: a diverged remote with no recorded CodeyBox push
        // is NOT a merge conflict — it parks distinctly instead of rebasing
        // the new head onto the stale tip. Typed, branch-identifying, and
        // free of credential material.
        var workBranch = NewBranch();
        var (host, repoId, upstreamBare, shim, githubUrl, token, handler) = await SetupDivergedAsync(workBranch, "local\n", "remote\n");
        using (shim)
        {
            var (owner, repo) = SplitGithubUrl(githubUrl);
            var remote = BuildRemote(host, handler, owner, repo, token, "rebase");
            var ex = await Assert.ThrowsAsync<UpstreamOwnedBranchDivergedException>(() =>
                remote.CompleteAsync(SampleRequest(repoId, workBranch, "rebase"), CancellationToken.None));
            Assert.Equal(workBranch, ex.Branch);
            Assert.False(UpstreamPushReconcileConflictException.TryFindIn(ex, out _));
            Assert.DoesNotContain(token, ex.Message);
            Assert.DoesNotContain(token, ex.Branch);
            Assert.Empty(handler.Requests);
            // The stale remote tip is untouched.
            var (_, content, _) = await TestSupport.RunGit(upstreamBare, "show", $"{workBranch}:conflict.txt");
            Assert.Equal("remote\n", content);
        }
    }

    [Fact]
    public async Task MergeDivergenceWithoutLeaseRecord_ParksDistinctFromConflict()
    {
        var workBranch = NewBranch();
        var (host, repoId, _, shim, githubUrl, token, handler) = await SetupDivergedAsync(workBranch, "local\n", "remote\n");
        using (shim)
        {
            var (owner, repo) = SplitGithubUrl(githubUrl);
            var remote = BuildRemote(host, handler, owner, repo, token, "merge");
            var ex = await Assert.ThrowsAsync<UpstreamOwnedBranchDivergedException>(() =>
                remote.CompleteAsync(SampleRequest(repoId, workBranch, "merge"), CancellationToken.None));
            Assert.Equal(workBranch, ex.Branch);
            Assert.False(UpstreamPushReconcileConflictException.TryFindIn(ex, out _));
            Assert.DoesNotContain(token, ex.Message);
        }
    }

    [Fact]
    public async Task RecordedDivergence_LeasePushRewritesWithoutResurrecting()
    {
        // With a recorded prior push (the lease base), a diverged owned
        // branch is rewritten under --force-with-lease: the stale tip is
        // replaced, never merged — superseded commits stay superseded.
        var workBranch = NewBranch();
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var upstreamBare = Path.Combine(_workspace, "upstream-" + Guid.NewGuid().ToString("N")[..8] + ".git");
        await TestSupport.RunGit(_workspace, "clone", "--bare", "--local", seed, upstreamBare);
        var owner = "o" + Guid.NewGuid().ToString("N")[..12];
        var repo = "r" + Guid.NewGuid().ToString("N")[..12];
        var githubUrl = $"https://github.com/{owner}/{repo}.git";
        var token = "tok_canary_" + Guid.NewGuid().ToString("N")[..12];
        using var shim = new GitHubUrlRewriteShim(_workspace, githubUrl, upstreamBare);
        var host = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]), GitExecutable = shim.ShimPath },
            NullLogger<LocalGitHost>.Instance);
        var repoId = await host.EnsureRepositoryAsync(WorkItemId.New(), upstreamBare);
        await CommitBranchFileAsync(host.GetRepoPath(repoId), workBranch, "agent.txt", "agent\n", "local agent change");
        await CommitBranchFileAsync(upstreamBare, workBranch, "remote.txt", "remote\n", "remote upstream change");

        var tipBefore = (await TestSupport.RunGit(host.GetRepoPath(repoId), "rev-parse", workBranch)).stdout.Trim();
        var (_, remoteTip, _) = await TestSupport.RunGit(upstreamBare, "rev-parse", workBranch);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { number = 11, html_url = $"https://github.com/{owner}/{repo}/pull/11" }), Encoding.UTF8, "application/json"),
        });
        var remote = BuildRemote(host, handler, owner, repo, token, "rebase");
        var request = SampleRequest(repoId, workBranch, "rebase") with
        {
            ExpectedRemoteHeadSha = remoteTip.Trim(),
        };
        var outcome = await remote.CompleteAsync(request, CancellationToken.None);

        Assert.True(outcome.BranchPushed);
        Assert.Equal(11, outcome.PullRequestNumber);
        Assert.Equal(tipBefore, outcome.PushedWorkBranchSha);
        var (_, tipAfter, _) = await TestSupport.RunGit(upstreamBare, "rev-parse", workBranch);
        Assert.Equal(tipBefore, tipAfter.Trim());
        // Rewritten, not reconciled: the stale remote-only file is gone and
        // no merge of the two histories happened.
        var (showCode, _, _) = await TestSupport.RunGitNoThrow(upstreamBare, "show", $"{workBranch}:remote.txt");
        Assert.NotEqual(0, showCode);
        var (_, subjects, _) = await TestSupport.RunGit(upstreamBare, "log", "--format=%s", "--max-count=3", workBranch);
        Assert.DoesNotContain("remote upstream change", subjects);
    }

    [Fact]
    public async Task GenericPushFailure_RemainsSanitizedAndUntyped()
    {
        var token = "SECRET-CANARY-" + Guid.NewGuid().ToString("N")[..8];
        var inner = new InvalidOperationException($"git push to upstream failed: auth with {token} denied");
        var gitHost = new ThrowingFakeGitHost(inner);
        var handler = new FakeHttpMessageHandler();
        var remote = new GitHubUpstreamRemote(
            gitHost, new FakeHttpClientFactory(handler),
            NullLogger<GitHubUpstreamRemote>.Instance,
            new GitHubUpstreamOptions { Owner = "myorg", Repository = "myrepo", Token = token, MergeMethod = "rebase" });
        var request = SampleRequest("repo-id", "codeybox/work-abc123", "rebase");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => remote.CompleteAsync(request, CancellationToken.None));
        Assert.False(ex is UpstreamPushReconcileConflictException);
        Assert.DoesNotContain(token, ex.Message);
        Assert.Contains("***", ex.Message);
    }

    [Fact]
    public async Task ConflictLikeMessageText_DoesNotTriggerTypedRecovery()
    {
        var token = "tok_" + Guid.NewGuid().ToString("N")[..8];
        var generic = new InvalidOperationException("upstream rebase conflict on main; manual resolution required");
        var gitHost = new ThrowingFakeGitHost(generic);
        var handler = new FakeHttpMessageHandler();
        var remote = new GitHubUpstreamRemote(
            gitHost, new FakeHttpClientFactory(handler),
            NullLogger<GitHubUpstreamRemote>.Instance,
            new GitHubUpstreamOptions { Owner = "myorg", Repository = "myrepo", Token = token, MergeMethod = "rebase" });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            remote.CompleteAsync(SampleRequest("repo-id", "codeybox/work-abc123", "rebase"), CancellationToken.None));
        Assert.False(ex is UpstreamPushReconcileConflictException);
    }

    [Fact]
    public async Task NestedTokenInInnerChain_DoesNotLeakThroughTypedConflict()
    {
        var token = "NESTED-CANARY-" + Guid.NewGuid().ToString("N")[..8];
        var typed = new UpstreamPushReconcileConflictException("codeybox/work-abc123", "rebase");
        var outer = new InvalidOperationException($"outer wrapper leaking {token}", typed);
        var gitHost = new ThrowingFakeGitHost(outer);
        var handler = new FakeHttpMessageHandler();
        var remote = new GitHubUpstreamRemote(
            gitHost, new FakeHttpClientFactory(handler),
            NullLogger<GitHubUpstreamRemote>.Instance,
            new GitHubUpstreamOptions { Owner = "myorg", Repository = "myrepo", Token = token, MergeMethod = "rebase" });
        var ex = await Assert.ThrowsAsync<UpstreamPushReconcileConflictException>(() =>
            remote.CompleteAsync(SampleRequest("repo-id", "codeybox/work-abc123", "rebase"), CancellationToken.None));
        Assert.Null(ex.InnerException);
        Assert.DoesNotContain(token, ex.Message);
        Assert.DoesNotContain(token, ex.Branch);
        Assert.Equal("codeybox/work-abc123", ex.Branch);
        Assert.Equal("rebase", ex.Strategy);
    }

    [Fact]
    public async Task BranchContainingToken_IsScrubbedAndNotLeaked()
    {
        var token = "tok" + Guid.NewGuid().ToString("N")[..8];
        var workBranch = $"codeybox/{token}";
        var typed = new UpstreamPushReconcileConflictException(workBranch, "rebase");
        var gitHost = new ThrowingFakeGitHost(typed);
        var handler = new FakeHttpMessageHandler();
        var remote = new GitHubUpstreamRemote(
            gitHost, new FakeHttpClientFactory(handler),
            NullLogger<GitHubUpstreamRemote>.Instance,
            new GitHubUpstreamOptions { Owner = "myorg", Repository = "myrepo", Token = token, MergeMethod = "rebase" });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            remote.CompleteAsync(SampleRequest("repo-id", "codeybox/work-abc123", "rebase"), CancellationToken.None));
        Assert.DoesNotContain(token, ex.Message);
    }
}

/// <summary>
/// Pipeline-level regression through the real adapter + real git host:
/// a genuine upstream work-branch conflict must enter bounded conflict rework
/// (one push attempt, not five generic retries); disabled/cap-exhausted paths
/// must preserve tips and never report false success.
/// </summary>
[Collection("Pipeline integration")]
public sealed class GitHubUpstreamTypedConflictPipelineTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-gh-pipe-").FullName;
    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    private static WorkItem NewItem(string workBranch, int conflictAttempts = 0) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "github typed conflict",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = workBranch,
        PushUpstream = true,
        ConflictReworkAttempts = conflictAttempts,
    };

    private static async Task<string> CreateUpstreamWithConflictingWorkBranchAsync(string workspace, string seed, string workBranch, string remoteContent)
    {
        var upstreamBare = Path.Combine(workspace, "upstream-" + Guid.NewGuid().ToString("N")[..8] + ".git");
        await TestSupport.RunGit(workspace, "clone", "--bare", "--local", seed, upstreamBare);
        var clone = Path.Combine(workspace, "up-pre-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await TestSupport.RunGit(workspace, "clone", upstreamBare, clone);
            await TestSupport.RunGit(clone, "config", "user.email", "t@t");
            await TestSupport.RunGit(clone, "config", "user.name", "T");
            await TestSupport.RunGit(clone, "checkout", "-b", workBranch);
            await File.WriteAllTextAsync(Path.Combine(clone, "push-conflict.txt"), remoteContent);
            await TestSupport.RunGit(clone, "add", "push-conflict.txt");
            await TestSupport.RunGit(clone, "commit", "-m", "remote conflicting work");
            await TestSupport.RunGit(clone, "push", "origin", workBranch);
        }
        finally
        {
            if (Directory.Exists(clone))
                Directory.Delete(clone, recursive: true);
        }

        return upstreamBare;
    }

    /// <summary>
    /// Shared fixture for the pipeline tests below: a seed repo, an upstream
    /// bare repo whose work branch genuinely conflicts with the agent's
    /// planned file, and the transport shim + real hosts wiring the
    /// <see cref="GitHubUpstreamRemote"/> to that upstream with no network.
    /// One helper so the setup cannot drift between the routing,
    /// disabled/cap, and resolution tests.
    /// </summary>
    private sealed record PipelineFixture(
        string Seed,
        string WorkBranch,
        string UpstreamBare,
        string Owner,
        string Repo,
        string Token,
        GitHubUrlRewriteShim Shim,
        LocalGitHost ShimHost,
        FakeHttpMessageHandler Handler,
        GitHubUpstreamOptions UpstreamOptions) : IDisposable
    {
        public void Dispose() => Shim.Dispose();
    }

    private async Task<PipelineFixture> SetupPipelineFixtureAsync(string mergeMethod)
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var workBranch = "feature/gh-typed-" + Guid.NewGuid().ToString("N")[..8];
        var upstreamBare = await CreateUpstreamWithConflictingWorkBranchAsync(_workspace, seed, workBranch, "remote content\n");

        var owner = "o" + Guid.NewGuid().ToString("N")[..12];
        var repo = "r" + Guid.NewGuid().ToString("N")[..12];
        var githubUrl = $"https://github.com/{owner}/{repo}.git";
        var token = "tok_canary_" + Guid.NewGuid().ToString("N")[..12];
        var shim = new GitHubUrlRewriteShim(_workspace, githubUrl, upstreamBare);
        var shimRoot = Path.Combine(_workspace, "shim-repos-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(shimRoot);
        var shimHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = shimRoot, GitExecutable = shim.ShimPath },
            NullLogger<LocalGitHost>.Instance);
        var handler = new FakeHttpMessageHandler();
        var opts = new GitHubUpstreamOptions { Owner = owner, Repository = repo, Token = token, MergeMethod = mergeMethod, AutoMerge = false };
        return new PipelineFixture(seed, workBranch, upstreamBare, owner, repo, token, shim, shimHost, handler, opts);
    }

    private static GitHubTypedConflictTestFactory BuildFactory(PipelineFixture fx) =>
        new(_ => new GitHubUpstreamRemote(
            fx.ShimHost, new FakeHttpClientFactory(fx.Handler),
            NullLogger<GitHubUpstreamRemote>.Instance, fx.UpstreamOptions));

    private static TestPipeline BuildTestPipeline(
        string workspace,
        string seed,
        string mergeMethod,
        GitHubTypedConflictTestFactory factory,
        LocalGitHost shimHost,
        bool routeToConflictRework,
        int maxReworkAttempts,
        int upstreamPushMaxAttempts = 5,
        IEnumerable<IAuditor>? auditors = null,
        IRequiredBuildVerifier? requiredBuildVerifier = null)
    {
        return TestSupport.BuildPipeline(
            workspace, seed,
            auditors: auditors,
            upstream: new ProjectUpstream { Kind = "github", MergeMethod = mergeMethod },
            upstreamFactory: factory,
            pipelineOptions: new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
                UpstreamPushMaxAttempts = upstreamPushMaxAttempts,
                UpstreamPushBackoff = TimeSpan.Zero,
            },
            gitHostDecorator: _ => shimHost,
            requiredBuildVerifier: requiredBuildVerifier,
            staleBaseReworkOptions: new StalePullRequestSweeperOptions { RouteToConflictRework = routeToConflictRework, MaxReworkAttempts = maxReworkAttempts });
    }

    [Fact]
    public async Task PushConflictThroughRealAdapter_RoutesToConflictReworkWithSinglePushAttempt()
    {
        using var fx = await SetupPipelineFixtureAsync("rebase");
        var seed = fx.Seed;
        var workBranch = fx.WorkBranch;
        var upstreamBare = fx.UpstreamBare;
        var token = fx.Token;
        var shimHost = fx.ShimHost;
        var handler = fx.Handler;
        var factory = BuildFactory(fx);

        using var tp = BuildTestPipeline(
            _workspace, seed, "rebase", factory, shimHost,
            routeToConflictRework: true, maxReworkAttempts: 2);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("push-conflict.txt", "work content\n"));

        var item = NewItem(workBranch);
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.ReworkingForConflict, final!.State);
        Assert.Equal(1, final.ConflictReworkAttempts);
        Assert.Equal(1, final.UpstreamPushAttempts);
        Assert.DoesNotContain(token, final.LastError ?? string.Empty);
        Assert.Empty(handler.Requests);
        var queue = Assert.IsType<InMemoryTaskQueue>(tp.Queue);
        Assert.Equal(item.Id, await queue.DequeueAsync(CancellationToken.None));
        var upstreamPushes = fx.Shim.Invocations.Count(line =>
            line.Contains("push", StringComparison.Ordinal) && line.Contains("github.com", StringComparison.Ordinal));
        Assert.Equal(1, upstreamPushes);

        var hostTip = (await TestSupport.RunGit(shimHost.GetRepoPath(item.Id.ToString()), "rev-parse", "--verify", workBranch)).stdout.Trim();
        var upstreamTip = (await TestSupport.RunGit(upstreamBare, "rev-parse", "--verify", workBranch)).stdout.Trim();
        Assert.False(string.IsNullOrWhiteSpace(hostTip));
        Assert.False(string.IsNullOrWhiteSpace(upstreamTip));
        Assert.NotEqual(hostTip, upstreamTip);
    }

    [Fact]
    public async Task PushConflictThroughRealAdapter_DisabledPreservesHistoricalParkWithoutDelivery()
    {
        using var fx = await SetupPipelineFixtureAsync("merge");
        var seed = fx.Seed;
        var workBranch = fx.WorkBranch;
        var upstreamBare = fx.UpstreamBare;
        var token = fx.Token;
        var shimHost = fx.ShimHost;
        var handler = fx.Handler;
        var factory = BuildFactory(fx);

        using var tp = BuildTestPipeline(
            _workspace, seed, "merge", factory, shimHost,
            routeToConflictRework: false, maxReworkAttempts: 2);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("push-conflict.txt", "work content\n"));

        var item = NewItem(workBranch);
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("upstream merge conflict on", final.LastError);
        Assert.DoesNotContain(token, final.LastError ?? string.Empty);
        Assert.Equal(0, final.ConflictReworkAttempts);
        Assert.Empty(handler.Requests);
        var queue = Assert.IsType<InMemoryTaskQueue>(tp.Queue);
        Assert.True(queue.Count == 0);

        var hostTip = (await TestSupport.RunGit(shimHost.GetRepoPath(item.Id.ToString()), "rev-parse", "--verify", workBranch)).stdout.Trim();
        var upstreamTip = (await TestSupport.RunGit(upstreamBare, "rev-parse", "--verify", workBranch)).stdout.Trim();
        Assert.False(string.IsNullOrWhiteSpace(hostTip));
        Assert.False(string.IsNullOrWhiteSpace(upstreamTip));
    }

    /// <summary>
    /// Counts audit invocations while always passing. Proves the audit phase
    /// genuinely ran (not skipped) on the first pickup.
    /// </summary>
    private sealed class CountingPassAuditor : IAuditor
    {
        public int Calls;
        public string Name => "counting-pass";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            _ = sandbox;
            _ = workingDirectory;
            _ = context;
            _ = ct;
            Calls++;
            return Task.FromResult(new AuditResult(true, []));
        }
    }

    /// <summary>
    /// Required-build verifier that always applies/passes while recording the
    /// work-branch tip it observed on every verification. Lets the
    /// resolution test prove the post-resolution pickup re-verified the
    /// CHANGED tip instead of reusing the pre-resolution approval.
    /// </summary>
    private sealed class TipRecordingBuildVerifier : IRequiredBuildVerifier
    {
        private readonly LocalGitHost _host;

        public TipRecordingBuildVerifier(LocalGitHost host) => _host = host;

        public int VerifyCalls;
        public List<string> ObservedTips { get; } = [];

        public Task<RequiredBuildProbeResult> ProbeAsync(RequiredBuildProbeRequest request, CancellationToken ct)
        {
            _ = request;
            _ = ct;
            return Task.FromResult(RequiredBuildProbeResult.Applies);
        }

        public async Task<RequiredBuildVerificationResult> VerifyAsync(RequiredBuildVerificationRequest request, CancellationToken ct)
        {
            _ = ct;
            VerifyCalls++;
            try
            {
                var tip = (await TestSupport.RunGit(_host.GetRepoPath(request.RepositoryId), "rev-parse", "--verify", request.WorkBranch)).stdout.Trim();
                ObservedTips.Add(tip);
            }
            catch (InvalidOperationException)
            {
                ObservedTips.Add("unresolvable:" + request.WorkBranch);
            }

            return RequiredBuildVerificationResult.Passed(0, "ok");
        }
    }

    private static async Task AssertAncestorAsync(string ancestor, string descendant, string repoPath)
    {
        var rc = await TestSupport.RunGitNoThrow(repoPath, "merge-base", "--is-ancestor", ancestor, descendant);
        Assert.Equal(0, rc.code);
    }

    /// <summary>
    /// Deterministically resolves the upstream work-branch divergence with
    /// real git: merges the upstream work-branch history into the local work
    /// branch, keeping both file contents under a fixed message. Returns the
    /// resolved tip. Throws (fails the test) when the merge is unexpectedly
    /// clean — the whole point is a genuine conflict.
    /// </summary>
    private static async Task<string> MergeUpstreamWorkBranchIntoLocalAsync(string localBare, string upstreamBare, string workBranch)
    {
        var clone = Path.Combine(Path.GetTempPath(), "codeybox-resolve-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await TestSupport.RunGit(Path.GetTempPath(), "clone", localBare, clone);
            await TestSupport.RunGit(clone, "config", "user.email", "t@t");
            await TestSupport.RunGit(clone, "config", "user.name", "T");
            await TestSupport.RunGit(clone, "checkout", workBranch);
            await TestSupport.RunGit(clone, "fetch", upstreamBare, workBranch);
            var merge = await TestSupport.RunGitNoThrow(clone, "merge", "FETCH_HEAD", "-m", "deterministic reconcile: keep remote and work");
            Assert.NotEqual(0, merge.code);
            await File.WriteAllTextAsync(Path.Combine(clone, "push-conflict.txt"), "remote content\nwork content\n");
            await TestSupport.RunGit(clone, "add", "push-conflict.txt");
            await TestSupport.RunGit(clone, "commit", "-m", "deterministic reconcile: keep remote and work");
            await TestSupport.RunGit(clone, "push", "origin", workBranch);
            return (await TestSupport.RunGit(clone, "rev-parse", "--verify", workBranch)).stdout.Trim();
        }
        finally
        {
            if (Directory.Exists(clone))
                Directory.Delete(clone, recursive: true);
        }
    }

    /// <summary>
    /// Successful-resolution test through the real adapter route. A genuine
    /// upstream work-branch conflict enters bounded conflict rework (one push
    /// attempt, not generic retries); the divergence is then resolved
    /// deterministically with real git (both histories retained under a
    /// merge); the requeued pickup must re-verify the build against the
    /// CHANGED tip — the pre-resolution approval is not carried forward —
    /// and deliver: Done with the PR opened, the exact resolved tip upstream,
    /// and no credential material in observable surfaces.
    /// </summary>
    [Fact]
    public async Task PushConflictThroughRealAdapter_ResolvesDeterministicallyWithFreshGatesAndDelivers()
    {
        using var fx = await SetupPipelineFixtureAsync("rebase");
        var token = fx.Token;
        var workBranch = fx.WorkBranch;
        var upstreamBare = fx.UpstreamBare;
        var shimHost = fx.ShimHost;

        var auditor = new CountingPassAuditor();
        var buildGate = new TipRecordingBuildVerifier(shimHost);
        var factory = BuildFactory(fx);
        using var tp = BuildTestPipeline(
            _workspace, fx.Seed, "rebase", factory, shimHost,
            routeToConflictRework: true, maxReworkAttempts: 2,
            auditors: [auditor],
            requiredBuildVerifier: buildGate);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("push-conflict.txt", "work content\n"));

        var item = NewItem(workBranch);
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var parked = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.ReworkingForConflict, parked!.State);
        Assert.Equal(1, parked.ConflictReworkAttempts);
        Assert.Equal(1, parked.UpstreamPushAttempts);
        Assert.True(auditor.Calls >= 1);
        var preResolutionVerifications = buildGate.VerifyCalls;
        Assert.True(preResolutionVerifications >= 1);

        var localRepo = shimHost.GetRepoPath(item.Id.ToString());
        var workTipBefore = (await TestSupport.RunGit(localRepo, "rev-parse", "--verify", workBranch)).stdout.Trim();
        var upstreamTipBefore = (await TestSupport.RunGit(upstreamBare, "rev-parse", "--verify", workBranch)).stdout.Trim();
        Assert.NotEqual(workTipBefore, upstreamTipBefore);
        Assert.Contains(workTipBefore, buildGate.ObservedTips.Take(preResolutionVerifications));

        var resolvedTip = await MergeUpstreamWorkBranchIntoLocalAsync(localRepo, upstreamBare, workBranch);
        Assert.NotEqual(workTipBefore, resolvedTip);
        await AssertAncestorAsync(workTipBefore, resolvedTip, localRepo);
        await AssertAncestorAsync(upstreamTipBefore, resolvedTip, localRepo);
        var (_, resolvedFile, _) = await TestSupport.RunGit(localRepo, "show", $"{resolvedTip}:push-conflict.txt");
        Assert.Contains("remote content", resolvedFile);
        Assert.Contains("work content", resolvedFile);

        fx.Handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { number = 12, html_url = $"https://github.com/{fx.Owner}/{fx.Repo}/pull/12" }), Encoding.UTF8, "application/json"),
        });
        var queue = Assert.IsType<InMemoryTaskQueue>(tp.Queue);
        Assert.Equal(item.Id, await queue.DequeueAsync(CancellationToken.None));
        var resume = await tp.Store.GetAsync(item.Id);
        await tp.Pipeline.RunAsync(resume!, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(12, final.MergedPrNumber);
        Assert.NotNull(final.MergedPrUrl);
        Assert.Equal(1, final.UpstreamPushAttempts);

        var postResolutionTips = buildGate.ObservedTips.Skip(preResolutionVerifications).ToList();
        Assert.True(buildGate.VerifyCalls > preResolutionVerifications);
        Assert.Contains(resolvedTip, postResolutionTips);
        Assert.DoesNotContain(workTipBefore, postResolutionTips);

        var upstreamTipAfter = (await TestSupport.RunGit(upstreamBare, "rev-parse", "--verify", workBranch)).stdout.Trim();
        Assert.Equal(resolvedTip, upstreamTipAfter);
        var (_, upstreamFile, _) = await TestSupport.RunGit(upstreamBare, "show", $"{upstreamTipAfter}:push-conflict.txt");
        Assert.Contains("remote content", upstreamFile);
        Assert.Contains("work content", upstreamFile);

        Assert.DoesNotContain(token, final.LastError ?? string.Empty);
        Assert.DoesNotContain(token, JsonSerializer.Serialize(final));
        Assert.DoesNotContain(token, string.Join("\n", fx.Shim.Invocations));
    }

    [Fact]
    public async Task PushConflictThroughRealAdapter_CapExhaustedParksAtMergeConflictTerminal()
    {
        using var fx = await SetupPipelineFixtureAsync("rebase");
        var seed = fx.Seed;
        var workBranch = fx.WorkBranch;
        var token = fx.Token;
        var shimHost = fx.ShimHost;
        var handler = fx.Handler;
        var factory = BuildFactory(fx);

        using var tp = BuildTestPipeline(
            _workspace, seed, "rebase", factory, shimHost,
            routeToConflictRework: true, maxReworkAttempts: 2);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("push-conflict.txt", "work content\n"));

        var item = NewItem(workBranch, conflictAttempts: 2);
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.MergeConflictResolutionFailed, final!.State);
        Assert.Equal(2, final.ConflictReworkAttempts);
        Assert.DoesNotContain(token, final.LastError ?? string.Empty);
        Assert.Empty(handler.Requests);
        var queue = Assert.IsType<InMemoryTaskQueue>(tp.Queue);
        Assert.True(queue.Count == 0);
    }
}
