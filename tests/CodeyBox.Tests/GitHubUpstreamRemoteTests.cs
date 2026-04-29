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
        GitHubUpstreamOptions? opts = null,
        IWebhookDispatcher? webhooks = null)
    {
        opts ??= DefaultOpts;
        var factory = new FakeHttpClientFactory(handler, userAgent: "codeybox");
        return new GitHubUpstreamRemote(
            gitHost,
            factory,
            webhooks ?? NullWebhookDispatcher.Instance,
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
    public async Task CompleteAsync_MergeReturns405_ReturnsPrUrlWithNullMergedSha()
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
        Assert.Null(outcome.MergedSha);  // graceful — PR left open
        // Notes must be populated so operators get an orchestrator-level diagnostic.
        Assert.NotNull(outcome.Notes);
        Assert.Contains("405", outcome.Notes);
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
    public async Task CompleteAsync_PrCreated_DispatchesPullRequestOpenedWebhookEvent()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(42, "https://github.com/myorg/myrepo/pull/42"));

        var dispatcher = new FakeWebhookDispatcher();
        var remote = BuildRemote(gitHost, handler, DefaultOpts with { AutoMerge = false }, webhooks: dispatcher);
        await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        Assert.Single(dispatcher.Events);
        var (eventName, payload) = dispatcher.Events[0];
        Assert.Equal("work_item.pull_request_opened", eventName);
        var prPayload = Assert.IsType<PullRequestOpenedPayload>(payload);
        Assert.Equal(SampleRequest.WorkBranch, prPayload.WorkBranch);
        Assert.Equal(SampleRequest.BaseBranch, prPayload.BaseBranch);
        Assert.Equal(42, prPayload.PullRequestNumber);
        Assert.Equal("https://github.com/myorg/myrepo/pull/42", prPayload.PullRequestUrl);
        Assert.Equal(SampleRequest.WorkItemId.ToString(), prPayload.WorkItemId);
        Assert.Equal(SampleRequest.ProjectId.ToString(), prPayload.ProjectId);
    }

    [Fact]
    public async Task CompleteAsync_PullsReturns422_DoesNotDispatchWebhookEvent()
    {
        var gitHost = new FakeGitHost();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(JsonResponse(HttpStatusCode.UnprocessableEntity,
            """{"message":"Validation Failed"}"""));

        var dispatcher = new FakeWebhookDispatcher();
        var remote = BuildRemote(gitHost, handler, webhooks: dispatcher);
        await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        Assert.Empty(dispatcher.Events);
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
    public List<(string RepositoryId, string Url, string Branch)> Pushes { get; } = new();

    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default)
        => Task.FromResult(id.ToString());

    public SandboxRepositoryAccess GetSandboxAccess(string repositoryId)
        => throw new NotSupportedException();

    public Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default)
        => Task.FromResult("main");

    public Task PushToUpstreamAsync(
        string repositoryId,
        string upstreamUrl,
        string branch,
        IReadOnlyDictionary<string, string> upstreamEnv,
        CancellationToken ct = default)
    {
        Pushes.Add((repositoryId, upstreamUrl, branch));
        return Task.CompletedTask;
    }

    public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default)
        => Task.FromResult(true);
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

internal sealed class FakeWebhookDispatcher : IWebhookDispatcher
{
    public List<(string EventName, object Payload)> Events { get; } = new();

    public Task DispatchAsync(string eventName, object payload, CancellationToken ct = default)
    {
        Events.Add((eventName, payload));
        return Task.CompletedTask;
    }
}

internal sealed class ThrowingFakeGitHost : IGitHost
{
    private readonly Exception _ex;
    public ThrowingFakeGitHost(Exception ex) => _ex = ex;

    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default)
        => Task.FromResult(id.ToString());

    public SandboxRepositoryAccess GetSandboxAccess(string repositoryId)
        => throw new NotSupportedException();

    public Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default)
        => Task.FromResult("main");

    public Task PushToUpstreamAsync(
        string repositoryId,
        string upstreamUrl,
        string branch,
        IReadOnlyDictionary<string, string> upstreamEnv,
        CancellationToken ct = default)
        => throw _ex;

    public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default)
        => Task.FromResult(true);
}
