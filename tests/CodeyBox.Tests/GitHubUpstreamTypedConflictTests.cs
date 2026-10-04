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
    private readonly string _githubUrl;
    private readonly string _localPath;

    public GitHubUrlRewriteShim(string workspace, string githubUrl, string localPath)
    {
        _githubUrl = githubUrl;
        _localPath = localPath;
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
/// Adapter-level regression: genuine diverged work-branch conflicts through
/// the real <see cref="LocalGitHost"/> and the real
/// <see cref="GitHubUpstreamRemote"/> must surface the shared typed contract
/// (branch + strategy) without credential leakage — not a flattened
/// message-only <see cref="InvalidOperationException"/>.
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
        string workBranch, string mergeMethod, string localContent, string remoteContent)
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
        await TestSupport.RunGit(Path.GetTempPath(), "clone", barePath, clone);
        await TestSupport.RunGit(clone, "config", "user.email", "t@t");
        await TestSupport.RunGit(clone, "config", "user.name", "T");
        await TestSupport.RunGit(clone, "checkout", "-b", branch);
        await File.WriteAllTextAsync(Path.Combine(clone, file), content);
        await TestSupport.RunGit(clone, "add", file);
        await TestSupport.RunGit(clone, "commit", "-m", message);
        await TestSupport.RunGit(clone, "push", "origin", branch);
        Directory.Delete(clone, recursive: true);
    }

    private static GitHubUpstreamRemote BuildRemote(
        IGitHost host, FakeHttpMessageHandler handler, string owner, string repo, string token, string mergeMethod)
    {
        var factory = new FakeHttpClientFactory(handler);
        var parts = new Uri($"https://github.com/{owner}/{repo}.git");
        _ = parts;
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
    public async Task RebaseConflict_PreservesTypedBranchAndStrategyWithoutInnerOrToken()
    {
        var workBranch = NewBranch();
        var (host, repoId, _, shim, githubUrl, token, handler) = await SetupDivergedAsync(workBranch, "rebase", "local\n", "remote\n");
        using (shim)
        {
            var (owner, repo) = SplitGithubUrl(githubUrl);
            var remote = BuildRemote(host, handler, owner, repo, token, "rebase");
            var ex = await Assert.ThrowsAsync<UpstreamPushReconcileConflictException>(() =>
                remote.CompleteAsync(SampleRequest(repoId, workBranch, "rebase"), CancellationToken.None));
            Assert.Equal(workBranch, ex.Branch);
            Assert.Equal("rebase", ex.Strategy);
            Assert.Null(ex.InnerException);
            Assert.DoesNotContain(token, ex.Message);
            Assert.DoesNotContain(token, ex.Branch);
            Assert.Empty(handler.Requests);
        }
    }

    [Fact]
    public async Task MergeConflict_PreservesTypedMergeIdentity()
    {
        var workBranch = NewBranch();
        var (host, repoId, _, shim, githubUrl, token, handler) = await SetupDivergedAsync(workBranch, "merge", "local\n", "remote\n");
        using (shim)
        {
            var (owner, repo) = SplitGithubUrl(githubUrl);
            var remote = BuildRemote(host, handler, owner, repo, token, "merge");
            var ex = await Assert.ThrowsAsync<UpstreamPushReconcileConflictException>(() =>
                remote.CompleteAsync(SampleRequest(repoId, workBranch, "merge"), CancellationToken.None));
            Assert.Equal(workBranch, ex.Branch);
            Assert.Equal("merge", ex.Strategy);
            Assert.Null(ex.InnerException);
            Assert.DoesNotContain(token, ex.Message);
        }
    }

    [Fact]
    public async Task NonConflictingDivergence_ReconcilesAndPushesBothHistories()
    {
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
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { number = 11, html_url = $"https://github.com/{owner}/{repo}/pull/11" }), Encoding.UTF8, "application/json"),
        });
        var remote = BuildRemote(host, handler, owner, repo, token, "rebase");
        var outcome = await remote.CompleteAsync(SampleRequest(repoId, workBranch, "rebase"), CancellationToken.None);

        Assert.True(outcome.BranchPushed);
        Assert.Equal(11, outcome.PullRequestNumber);
        var tipAfter = (await TestSupport.RunGit(host.GetRepoPath(repoId), "rev-parse", workBranch)).stdout.Trim();
        Assert.NotEqual(tipBefore, tipAfter);
        var (_, agentFile, _) = await TestSupport.RunGit(upstreamBare, "show", $"{workBranch}:agent.txt");
        var (_, remoteFile, _) = await TestSupport.RunGit(upstreamBare, "show", $"{workBranch}:remote.txt");
        Assert.Equal("agent\n", agentFile);
        Assert.Equal("remote\n", remoteFile);
        var (_, subjects, _) = await TestSupport.RunGit(upstreamBare, "log", "--format=%s", "--max-count=3", workBranch);
        Assert.Contains("local agent change", subjects);
        Assert.Contains("remote upstream change", subjects);
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
        await TestSupport.RunGit(workspace, "clone", upstreamBare, clone);
        await TestSupport.RunGit(clone, "config", "user.email", "t@t");
        await TestSupport.RunGit(clone, "config", "user.name", "T");
        await TestSupport.RunGit(clone, "checkout", "-b", workBranch);
        await File.WriteAllTextAsync(Path.Combine(clone, "push-conflict.txt"), remoteContent);
        await TestSupport.RunGit(clone, "add", "push-conflict.txt");
        await TestSupport.RunGit(clone, "commit", "-m", "remote conflicting work");
        await TestSupport.RunGit(clone, "push", "origin", workBranch);
        Directory.Delete(clone, recursive: true);
        return upstreamBare;
    }

    [Fact]
    public async Task PushConflictThroughRealAdapter_RoutesToConflictReworkWithSinglePushAttempt()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var workBranch = "feature/gh-typed-" + Guid.NewGuid().ToString("N")[..8];
        var upstreamBare = await CreateUpstreamWithConflictingWorkBranchAsync(_workspace, seed, workBranch, "remote content\n");

        var owner = "o" + Guid.NewGuid().ToString("N")[..12];
        var repo = "r" + Guid.NewGuid().ToString("N")[..12];
        var githubUrl = $"https://github.com/{owner}/{repo}.git";
        var token = "tok_canary_" + Guid.NewGuid().ToString("N")[..12];
        using var shim = new GitHubUrlRewriteShim(_workspace, githubUrl, upstreamBare);
        var shimRoot = Path.Combine(_workspace, "shim-repos-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(shimRoot);
        var shimHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = shimRoot, GitExecutable = shim.ShimPath },
            NullLogger<LocalGitHost>.Instance);
        var handler = new FakeHttpMessageHandler();
        var opts = new GitHubUpstreamOptions { Owner = owner, Repository = repo, Token = token, MergeMethod = "rebase", AutoMerge = false };
        var factory = new GitHubTypedConflictTestFactory(_ => new GitHubUpstreamRemote(
            shimHost, new FakeHttpClientFactory(handler),
            NullLogger<GitHubUpstreamRemote>.Instance, opts));

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            upstream: new ProjectUpstream { Kind = "github", MergeMethod = "rebase" },
            upstreamFactory: factory,
            pipelineOptions: new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
                UpstreamPushMaxAttempts = 5,
                UpstreamPushBackoff = TimeSpan.Zero,
            },
            gitHostDecorator: _ => shimHost,
            staleBaseReworkOptions: new StalePullRequestSweeperOptions { RouteToConflictRework = true, MaxReworkAttempts = 2 });
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
        var upstreamPushes = shim.Invocations.Count(line =>
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
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var workBranch = "feature/gh-typed-" + Guid.NewGuid().ToString("N")[..8];
        var upstreamBare = await CreateUpstreamWithConflictingWorkBranchAsync(_workspace, seed, workBranch, "remote content\n");

        var owner = "o" + Guid.NewGuid().ToString("N")[..12];
        var repo = "r" + Guid.NewGuid().ToString("N")[..12];
        var githubUrl = $"https://github.com/{owner}/{repo}.git";
        var token = "tok_canary_" + Guid.NewGuid().ToString("N")[..12];
        using var shim = new GitHubUrlRewriteShim(_workspace, githubUrl, upstreamBare);
        var shimRoot = Path.Combine(_workspace, "shim-repos-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(shimRoot);
        var shimHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = shimRoot, GitExecutable = shim.ShimPath },
            NullLogger<LocalGitHost>.Instance);
        var handler = new FakeHttpMessageHandler();
        var opts = new GitHubUpstreamOptions { Owner = owner, Repository = repo, Token = token, MergeMethod = "merge", AutoMerge = false };
        var factory = new GitHubTypedConflictTestFactory(_ => new GitHubUpstreamRemote(
            shimHost, new FakeHttpClientFactory(handler),
            NullLogger<GitHubUpstreamRemote>.Instance, opts));

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            upstream: new ProjectUpstream { Kind = "github", MergeMethod = "merge" },
            upstreamFactory: factory,
            pipelineOptions: new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
                UpstreamPushMaxAttempts = 5,
                UpstreamPushBackoff = TimeSpan.Zero,
            },
            gitHostDecorator: _ => shimHost,
            staleBaseReworkOptions: new StalePullRequestSweeperOptions { RouteToConflictRework = false, MaxReworkAttempts = 2 });
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

    [Fact]
    public async Task PushConflictThroughRealAdapter_CapExhaustedParksAtMergeConflictTerminal()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var workBranch = "feature/gh-typed-" + Guid.NewGuid().ToString("N")[..8];
        var upstreamBare = await CreateUpstreamWithConflictingWorkBranchAsync(_workspace, seed, workBranch, "remote content\n");

        var owner = "o" + Guid.NewGuid().ToString("N")[..12];
        var repo = "r" + Guid.NewGuid().ToString("N")[..12];
        var githubUrl = $"https://github.com/{owner}/{repo}.git";
        var token = "tok_canary_" + Guid.NewGuid().ToString("N")[..12];
        using var shim = new GitHubUrlRewriteShim(_workspace, githubUrl, upstreamBare);
        var shimRoot = Path.Combine(_workspace, "shim-repos-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(shimRoot);
        var shimHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = shimRoot, GitExecutable = shim.ShimPath },
            NullLogger<LocalGitHost>.Instance);
        var handler = new FakeHttpMessageHandler();
        var opts = new GitHubUpstreamOptions { Owner = owner, Repository = repo, Token = token, MergeMethod = "rebase", AutoMerge = false };
        var factory = new GitHubTypedConflictTestFactory(_ => new GitHubUpstreamRemote(
            shimHost, new FakeHttpClientFactory(handler),
            NullLogger<GitHubUpstreamRemote>.Instance, opts));

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            upstream: new ProjectUpstream { Kind = "github", MergeMethod = "rebase" },
            upstreamFactory: factory,
            pipelineOptions: new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
                UpstreamPushMaxAttempts = 5,
                UpstreamPushBackoff = TimeSpan.Zero,
            },
            gitHostDecorator: _ => shimHost,
            staleBaseReworkOptions: new StalePullRequestSweeperOptions { RouteToConflictRework = true, MaxReworkAttempts = 2 });
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
