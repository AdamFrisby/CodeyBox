using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CodeyBox.Core;
using CodeyBox.Upstream.GitHub;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="GitHubUpstreamRemote.CompleteAsync"/>. Uses a
/// fake <see cref="HttpMessageHandler"/> so no real GitHub API is called.
/// </summary>
public sealed class GitHubUpstreamRemoteTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static readonly GitHubUpstreamOptions DefaultOpts = new()
    {
        Owner = "myorg",
        Repository = "myrepo",
        Token = "test-token-not-a-real-pat",
        MergeMethod = "merge",
        AutoMerge = false,
    };

    private static readonly UpstreamCompletionRequest SampleRequest = new()
    {
        RepositoryId = "repo-id",
        WorkItemId = new WorkItemId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
        ProjectId = new ProjectId("test-project"),
        WorkBranch = "codeybox/abc123",
        BaseBranch = "main",
        MergeSha = "deadbeef",
        Title = "Add feature X",
        Description = "Automated via CodeyBox",
    };

    private static GitHubUpstreamRemote BuildRemote(
        IGitHost gitHost,
        FakeHttpMessageHandler handler,
        GitHubUpstreamOptions? opts = null)
    {
        opts ??= DefaultOpts;
        var factory = new FakeHttpClientFactory(handler, userAgent: "codeybox");
        return new GitHubUpstreamRemote(
            gitHost,
            factory,
            NullLogger<GitHubUpstreamRemote>.Instance,
            opts);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage PrCreatedResponse(int number, string htmlUrl) =>
        JsonResponse(HttpStatusCode.Created,
            $$"""{"number":{{number}},"html_url":"{{htmlUrl}}"}""");

    private static HttpResponseMessage MergeOkResponse(string sha) =>
        JsonResponse(HttpStatusCode.OK,
            $$"""{"sha":"{{sha}}","merged":true,"message":"Pull Request successfully merged"}""");

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CompleteAsync_PrOnlyFlow_PushesWorkBranchAndOpensPr()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(42, "https://github.com/myorg/myrepo/pull/42"));

        var remote = BuildRemote(gitHost, handler, DefaultOpts with { AutoMerge = false });
        var outcome = await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        // Work branch pushed
        Assert.Single(gitHost.Pushes);
        Assert.Equal(SampleRequest.WorkBranch, gitHost.Pushes[0].Branch);

        // POST /pulls called, not /merge
        Assert.Single(handler.Requests);
        Assert.Contains("/pulls", handler.Requests[0].RequestUri!.PathAndQuery);
        Assert.DoesNotContain("/merge", handler.Requests[0].RequestUri!.PathAndQuery);

        // Outcome
        Assert.True(outcome.BranchPushed);
        Assert.Equal("https://github.com/myorg/myrepo/pull/42", outcome.PullRequestUrl);
        Assert.Equal(42, outcome.PullRequestNumber);
        Assert.Null(outcome.MergedSha);
    }

    [Fact]
    public async Task CompleteAsync_AutoMergeFlow_OpensPrThenMerges()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(7, "https://github.com/myorg/myrepo/pull/7"));
        handler.Enqueue(MergeOkResponse("abc123sha"));

        var remote = BuildRemote(gitHost, handler, DefaultOpts with { AutoMerge = true, MergeMethod = "squash" });
        var outcome = await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        // Two HTTP calls: POST /pulls then PUT /pulls/7/merge
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("/pulls", handler.Requests[0].RequestUri!.PathAndQuery);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Contains("/pulls/7/merge", handler.Requests[1].RequestUri!.PathAndQuery);
        Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);

        // Merge body contains the configured method
        Assert.Contains("squash", handler.RequestBodies[1]);

        Assert.True(outcome.BranchPushed);
        Assert.Equal("https://github.com/myorg/myrepo/pull/7", outcome.PullRequestUrl);
        Assert.Equal(7, outcome.PullRequestNumber);
        Assert.Equal("abc123sha", outcome.MergedSha);
    }

    [Fact]
    public async Task CompleteAsync_PullsReturns422_ReturnsGracefulOutcomeWithoutThrow()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(JsonResponse(HttpStatusCode.UnprocessableEntity,
            """{"message":"Validation Failed","errors":[{"message":"A pull request already exists"}]}"""));

        var remote = BuildRemote(gitHost, handler);
        var outcome = await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        // Branch was still pushed
        Assert.True(outcome.BranchPushed);
        // PR info absent — graceful
        Assert.Null(outcome.PullRequestUrl);
        Assert.Null(outcome.PullRequestNumber);
        Assert.NotNull(outcome.Notes);
        Assert.Contains("422", outcome.Notes);
    }

    [Fact]
    public async Task CompleteAsync_MergeReturns405_FlagsAutoMergeRacedSoOrchestratorCanRecover()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(99, "https://github.com/myorg/myrepo/pull/99"));
        handler.Enqueue(JsonResponse(HttpStatusCode.MethodNotAllowed,
            """{"message":"Pull Request is not mergeable"}"""));

        var remote = BuildRemote(gitHost, handler, DefaultOpts with { AutoMerge = true });
        var outcome = await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        Assert.True(outcome.BranchPushed);
        Assert.Equal("https://github.com/myorg/myrepo/pull/99", outcome.PullRequestUrl);
        Assert.Equal(99, outcome.PullRequestNumber);
        Assert.Null(outcome.MergedSha);  // PR left open for the orchestrator's race recovery
        // Notes must be populated so operators get an orchestrator-level diagnostic.
        Assert.NotNull(outcome.Notes);
        Assert.Contains("405", outcome.Notes);
        // AutoMergeRaced is the signal the orchestrator's retry loop watches
        // to trigger the "re-fetch base + re-run merge phase + retry merge"
        // recovery path — without it, the item would be parked at the cap.
        Assert.True(outcome.AutoMergeRaced);
    }

    [Fact]
    public async Task CompleteAsync_ExistingPrNumber_SkipsCreatePrAndCallsMergeDirectly()
    {
        // Simulates the orchestrator's race-recovery retry: it re-runs the merge
        // phase locally and then re-invokes CompleteAsync, passing the PR number
        // from the prior attempt so we don't re-create (which would 422).
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(MergeOkResponse("merged-after-retry"));

        var remote = BuildRemote(gitHost, handler, DefaultOpts with { AutoMerge = true });
        var request = SampleRequest with { ExistingPullRequestNumber = 42 };
        var outcome = await remote.CompleteAsync(request, CancellationToken.None);

        // Push still happens — we may have advanced the work branch locally
        // to the new merge sha and need to publish it.
        Assert.Single(gitHost.Pushes);
        Assert.Equal(SampleRequest.WorkBranch, gitHost.Pushes[0].Branch);

        // Only the PUT /merge call — no POST /pulls re-creation.
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Contains("/pulls/42/merge", handler.Requests[0].RequestUri!.PathAndQuery);

        Assert.True(outcome.BranchPushed);
        Assert.Equal(42, outcome.PullRequestNumber);
        Assert.Equal("merged-after-retry", outcome.MergedSha);
        Assert.False(outcome.AutoMergeRaced);
        // URL must be synthesized for the existing PR so consumers (webhooks,
        // logging, operator surface) can link back to the forge view.
        Assert.Equal("https://github.com/myorg/myrepo/pull/42", outcome.PullRequestUrl);
    }

    [Fact]
    public async Task CompleteAsync_ExistingPrNumberWith405_StillFlagsAutoMergeRaced()
    {
        // The race may recur — re-running the merge phase doesn't help if a
        // third writer is hammering base. The orchestrator caps total attempts;
        // each retry CompleteAsync still surfaces AutoMergeRaced.
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(JsonResponse(HttpStatusCode.MethodNotAllowed,
            """{"message":"Pull Request is not mergeable"}"""));

        var remote = BuildRemote(gitHost, handler, DefaultOpts with { AutoMerge = true });
        var request = SampleRequest with { ExistingPullRequestNumber = 7 };
        var outcome = await remote.CompleteAsync(request, CancellationToken.None);

        Assert.True(outcome.BranchPushed);
        Assert.Equal(7, outcome.PullRequestNumber);
        Assert.Null(outcome.MergedSha);
        Assert.True(outcome.AutoMergeRaced);
    }

    [Fact]
    public async Task CompleteAsync_RequestsCarryUserAgentHeader()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(1, "https://github.com/myorg/myrepo/pull/1"));

        var remote = BuildRemote(gitHost, handler);
        await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        Assert.All(handler.Requests, req =>
            Assert.True(
                req.Headers.UserAgent.ToString().Contains("codeybox", StringComparison.OrdinalIgnoreCase),
                $"Expected User-Agent 'codeybox' but got '{req.Headers.UserAgent}'"));
    }

    [Fact]
    public async Task CompleteAsync_RequestsCarryTokenAuthorizationHeader()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(1, "https://github.com/myorg/myrepo/pull/1"));

        var remote = BuildRemote(gitHost, handler);
        await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        Assert.All(handler.Requests, req =>
        {
            var auth = req.Headers.Authorization;
            Assert.NotNull(auth);
            Assert.Equal("token", auth!.Scheme);
            Assert.Equal(DefaultOpts.Token, auth.Parameter);
        });
    }

    [Fact]
    public async Task CompleteAsync_PullRequestTitleTemplate_SubstitutesPlaceholders()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(5, "https://github.com/myorg/myrepo/pull/5"));

        var opts = DefaultOpts with { PullRequestTitleTemplate = "[bot] {title} ({branch})" };
        var remote = BuildRemote(gitHost, handler, opts);
        await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        // The POST /pulls body should contain the resolved title
        Assert.Contains("[bot] Add feature X (codeybox/abc123)", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task FetchBaseBranchAsync_RejectsBranchWithWhitespaceOrControl()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        var remote = BuildRemote(gitHost, handler);

        await Assert.ThrowsAsync<ArgumentException>(
            () => remote.FetchBaseBranchAsync("repo-id", "main\n", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => remote.FetchBaseBranchAsync("repo-id", "main with space", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => remote.FetchBaseBranchAsync("repo-id", "main", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => remote.FetchBaseBranchAsync("repo-id", string.Empty, CancellationToken.None));

        // Validation must short-circuit before the git host runs — no fetch was
        // dispatched so an attacker can't bypass argv validation by piggybacking
        // on the askpass plumbing.
        Assert.Empty(gitHost.Fetches);
    }

    [Fact]
    public async Task FetchBaseBranchAsync_DelegatesToGitHostWithRepoUrlAndAskpassEnv()
    {
        var gitHost = new FakeGitHost { FetchUpstreamShaToReturn = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef" };
        var handler = new FakeHttpMessageHandler();
        var remote = BuildRemote(gitHost, handler);

        var sha = await remote.FetchBaseBranchAsync("repo-id-x", "main", CancellationToken.None);

        Assert.Equal("deadbeefdeadbeefdeadbeefdeadbeefdeadbeef", sha);
        var call = Assert.Single(gitHost.Fetches);
        Assert.Equal("repo-id-x", call.RepositoryId);
        // Bare URL only (no credentials embedded — those flow via askpass env).
        Assert.Equal("https://github.com/myorg/myrepo.git", call.Url);
        Assert.Equal("main", call.Branch);
        // The askpass env must carry the configured token so git can authenticate
        // the fetch against private repos. Validates the credential plumbing
        // didn't silently change to e.g. ambient environment.
        Assert.True(call.Env.ContainsKey("GIT_ASKPASS"));
        Assert.Equal(DefaultOpts.Token, call.Env["CODEYBOX_GIT_PASS"]);
        Assert.Equal("x-access-token", call.Env["CODEYBOX_GIT_USER"]);
    }

    [Fact]
    public async Task FetchBaseBranchAsync_PropagatesNullFromGitHost()
    {
        var gitHost = new FakeGitHost { FetchUpstreamShaToReturn = null };
        var handler = new FakeHttpMessageHandler();
        var remote = BuildRemote(gitHost, handler);

        // Upstream not advertising the branch → propagated as null so the
        // orchestrator can park with a distinct "upstream does not advertise"
        // message rather than treating it as a successful fetch.
        var sha = await remote.FetchBaseBranchAsync("repo-id", "main", CancellationToken.None);
        Assert.Null(sha);
        Assert.Single(gitHost.Fetches);
    }

    [Fact]
    public async Task CompleteAsync_PushToUpstreamThrows_PropagatesExceptionWithoutCallingGitHubApi()
    {
        // Verifies that a PushToUpstreamAsync failure is rethrown so the
        // orchestrator retry loop can engage, and that no GitHub API calls
        // are made when the push step itself fails.
        var gitHost = new ThrowingFakeGitHost(new InvalidOperationException("git push failed: connection refused"));
        var handler = new FakeHttpMessageHandler();

        var remote = BuildRemote(gitHost, handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            remote.CompleteAsync(SampleRequest, CancellationToken.None));

        Assert.Empty(handler.Requests);
    }
}

// -------------------------------------------------------------------------
// Test infrastructure
// -------------------------------------------------------------------------

internal sealed class FakeGitHost : IGitHost
{
    public List<(string RepositoryId, string Url, string Branch, UpstreamPushReconcileStrategy ReconcileStrategy)> Pushes { get; } = new();
    public List<(string RepositoryId, string Url, string Branch, IReadOnlyDictionary<string, string> Env)> Fetches { get; } = new();

    /// <summary>
    /// When set, <see cref="FetchUpstreamBranchAsync"/> returns this sha rather
    /// than the default-interface null. Lets tests assert the sha is propagated
    /// out of <see cref="GitHubUpstreamRemote.FetchBaseBranchAsync"/>.
    /// </summary>
    public string? FetchUpstreamShaToReturn { get; set; }

    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default)
        => Task.FromResult(id.ToString());
    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, string? baseBranch, CancellationToken ct = default)
        => EnsureRepositoryAsync(id, seedFromUrl, ct);

    public SandboxRepositoryAccess GetSandboxAccess(string repositoryId)
        => throw new NotSupportedException();

    public Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default)
        => Task.FromResult("main");

    public Task PushToUpstreamAsync(
        string repositoryId,
        string upstreamUrl,
        string branch,
        IReadOnlyDictionary<string, string> upstreamEnv,
        UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase,
        CancellationToken ct = default)
    {
        Pushes.Add((repositoryId, upstreamUrl, branch, reconcileStrategy));
        return Task.CompletedTask;
    }

    public Task<string?> FetchUpstreamBranchAsync(
        string repositoryId,
        string upstreamUrl,
        string branch,
        IReadOnlyDictionary<string, string> upstreamEnv,
        CancellationToken ct = default)
    {
        Fetches.Add((repositoryId, upstreamUrl, branch, upstreamEnv));
        return Task.FromResult(FetchUpstreamShaToReturn);
    }

    public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<(string DiffStat, string FullDiff)> GetDiffAsync(
        string repositoryId, string baseBranch, string workBranch, CancellationToken ct = default)
        => Task.FromResult(("", ""));
}

internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _queue = new();
    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string> RequestBodies { get; } = new();

    public void Enqueue(HttpResponseMessage response) => _queue.Enqueue(response);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var body = request.Content is not null
            ? await request.Content.ReadAsStringAsync(cancellationToken)
            : string.Empty;
        RequestBodies.Add(body);
        return _queue.Count > 0
            ? _queue.Dequeue()
            : new HttpResponseMessage(HttpStatusCode.OK);
    }
}

internal sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpClient _client;

    public FakeHttpClientFactory(HttpMessageHandler handler, string? userAgent = null)
    {
        _client = new HttpClient(handler);
        if (!string.IsNullOrEmpty(userAgent))
            _client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    public HttpClient CreateClient(string name)
    {
        Assert.Equal("github-upstream", name);
        return _client;
    }
}

internal sealed class ThrowingFakeGitHost : IGitHost
{
    private readonly Exception _ex;
    public ThrowingFakeGitHost(Exception ex) => _ex = ex;

    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default)
        => Task.FromResult(id.ToString());
    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, string? baseBranch, CancellationToken ct = default)
        => EnsureRepositoryAsync(id, seedFromUrl, ct);

    public SandboxRepositoryAccess GetSandboxAccess(string repositoryId)
        => throw new NotSupportedException();

    public Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default)
        => Task.FromResult("main");

    public Task PushToUpstreamAsync(
        string repositoryId,
        string upstreamUrl,
        string branch,
        IReadOnlyDictionary<string, string> upstreamEnv,
        UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase,
        CancellationToken ct = default)
        => throw _ex;

    public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<(string DiffStat, string FullDiff)> GetDiffAsync(
        string repositoryId, string baseBranch, string workBranch, CancellationToken ct = default)
        => Task.FromResult(("", ""));
}
