using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CodeyBox.Core;
using CodeyBox.GiteaUpstreamPlugin;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the Gitea upstream-remote plugin (<c>codeybox.gitea-upstream</c>).
/// HTTP is faked at the wire level with Gitea 1.22-verified shapes (see
/// <c>Fixtures/Gitea/</c>); git is faked through a recording host. Every test
/// asserts a value the provider produced — a wrong mapping, a swallowed
/// failure, or a leaked credential flips it red.
/// </summary>
public sealed class GiteaUpstreamPluginTests : IDisposable
{
    private const string TokenEnvVar = "CODEYBOX_TEST_GITEA_TOKEN";
    private const string FakeToken = "test-token-not-a-real-gitea-token";

    private static readonly ProjectId TestProjectId = new("gitea-test-project");

    private readonly List<string> _envToRestore = [];

    public void Dispose()
    {
        foreach (var name in _envToRestore)
            Environment.SetEnvironmentVariable(name, null);
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private sealed class GiteaRecordingGitHost : IGitHost
    {
        public List<(string RepositoryId, string Url, string Branch)> Pushes { get; } = [];
        public List<IReadOnlyDictionary<string, string>> PushEnvs { get; } = [];
        public List<(string RepositoryId, string Url, string Branch)> Fetches { get; } = [];
        public int SandboxAccessCalls { get; private set; }
        public string? FetchShaToReturn { get; set; }
        public Exception? PushToThrow { get; set; }

        public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default)
            => Task.FromResult(id.ToString());

        public Task<string> EnsureRepositoryAsync(
            WorkItemId id, string? seedFromUrl, string? baseBranch, CancellationToken ct = default)
            => EnsureRepositoryAsync(id, seedFromUrl, ct);

        public SandboxRepositoryAccess GetSandboxAccess(string repositoryId)
        {
            SandboxAccessCalls++;
            throw new NotSupportedException("Upstream remotes must never touch sandboxes.");
        }

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
            Pushes.Add((repositoryId, upstreamUrl, branch));
            PushEnvs.Add(upstreamEnv);
            if (PushToThrow is not null)
                throw PushToThrow;
            return Task.CompletedTask;
        }

        public Task<string?> FetchUpstreamBranchAsync(
            string repositoryId,
            string upstreamUrl,
            string branch,
            IReadOnlyDictionary<string, string> upstreamEnv,
            CancellationToken ct = default)
        {
            Fetches.Add((repositoryId, upstreamUrl, branch));
            return Task.FromResult(FetchShaToReturn);
        }

        public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<(string DiffStat, string FullDiff)> GetDiffAsync(
            string repositoryId, string baseBranch, string workBranch, CancellationToken ct = default)
            => Task.FromResult(("", ""));
    }

    private sealed class GiteaFakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public GiteaFakeHttpClientFactory(HttpMessageHandler handler)
            => _client = new HttpClient(handler);

        public HttpClient CreateClient(string name)
        {
            Assert.Equal("gitea-upstream", name);
            return _client;
        }
    }

    private sealed class GiteaFakePluginHost : IPluginHost, IUpstreamPluginHost
    {
        public ILogger Logger { get; } = NullLogger.Instance;
        public IConfigurationSection ScopedConfig { get; }
        private readonly IReadOnlyDictionary<string, string> _projectConfig;

        public GiteaFakePluginHost(
            Dictionary<string, string?> scoped,
            IReadOnlyDictionary<string, string>? projectConfig = null)
        {
            ScopedConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(scoped)
                .Build()
                .GetSection("Gitea");
            _projectConfig = projectConfig ?? new Dictionary<string, string>();
        }

        public IReadOnlyDictionary<string, string> GetProjectUpstreamConfig(ProjectId projectId)
            => _projectConfig;
    }

    private static Dictionary<string, string?> ScopedConfig() => new()
    {
        ["Gitea:BaseUrl"] = "https://git.example.com/api/v1",
        ["Gitea:Owner"] = "myteam",
        ["Gitea:Repository"] = "myproject",
        ["Gitea:TokenEnvVar"] = TokenEnvVar,
    };

    private void UseToken(string? value = FakeToken)
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, value);
        _envToRestore.Add(TokenEnvVar);
    }

    private static GiteaUpstreamRemote BuildRemote(
        GiteaRecordingGitHost git,
        FakeHttpMessageHandler http,
        GiteaFakePluginHost host)
    {
        var remote = new GiteaUpstreamRemote(git, new GiteaFakeHttpClientFactory(http));
        remote.InitializeAsync(new PluginContext("1.0", "codeybox.gitea-upstream", "Gitea", host))
            .GetAwaiter().GetResult();
        return remote;
    }

    private static UpstreamCompletionRequest CompletionRequest(bool autoMerge = false) => new()
    {
        RepositoryId = "repo-id",
        WorkItemId = new WorkItemId(Guid.Parse("00000000-0000-0000-0000-000000000021")),
        ProjectId = TestProjectId,
        WorkBranch = "codeybox/abc123",
        BaseBranch = "main",
        MergeSha = "deadbeef",
        Title = "Add feature Z",
        Description = "Automated via CodeyBox",
        TokenEnvVar = TokenEnvVar,
        AutoMerge = autoMerge,
        MergeMethod = "merge",
    };

    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine("Fixtures", "Gitea", name));

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    // ------------------------------------------------------------------
    // Identity
    // ------------------------------------------------------------------

    [Fact]
    public void Plugin_RegistersAsGitea()
    {
        var remote = new GiteaUpstreamRemote(new GiteaRecordingGitHost(), new GiteaFakeHttpClientFactory(new FakeHttpMessageHandler()));
        Assert.Equal("gitea", remote.Name);

        var attr = typeof(GiteaUpstreamRemote)
            .GetCustomAttributes(typeof(CodeyBoxPluginAttribute), inherit: false)
            .OfType<CodeyBoxPluginAttribute>()
            .Single();
        Assert.Equal("codeybox.gitea-upstream", attr.Id);
    }

    // ------------------------------------------------------------------
    // Full lifecycle: push, open, read, merge
    // ------------------------------------------------------------------

    [Fact]
    public async Task Lifecycle_PushOpenReadMerge()
    {
        UseToken();
        var git = new GiteaRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new GiteaFakePluginHost(ScopedConfig()));

        http.Enqueue(JsonResponse(Fixture("pull-created.json"), HttpStatusCode.Created));
        var opened = await remote.CompleteAsync(CompletionRequest());

        Assert.True(opened.BranchPushed);
        Assert.Equal(7, opened.PullRequestNumber);
        Assert.Equal("https://git.example.com/myteam/myproject/pulls/7", opened.PullRequestUrl);
        Assert.Null(opened.MergedSha);

        var (repoId, pushUrl, pushBranch) = Assert.Single(git.Pushes);
        Assert.Equal("repo-id", repoId);
        Assert.Equal("codeybox/abc123", pushBranch);
        Assert.Equal("https://git.example.com/myteam/myproject.git", pushUrl);

        http.Enqueue(JsonResponse(Fixture("pull-detail-open.json")));
        var state = await remote.GetPullRequestAsync(7);

        Assert.NotNull(state);
        Assert.Equal(PullRequestStatus.Open, state.Status);
        Assert.Null(state.MergeCommitSha);

        http.Enqueue(JsonResponse(Fixture("pull-created.json"), HttpStatusCode.Created));
        http.Enqueue(JsonResponse("{}", HttpStatusCode.OK));
        http.Enqueue(JsonResponse(Fixture("pull-detail-merged.json")));
        var merged = await remote.CompleteAsync(CompletionRequest(autoMerge: true));

        Assert.Equal(7, merged.PullRequestNumber);
        Assert.Equal("9aa8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0", merged.MergedSha);

        var mergeRequest = http.Requests.First(r =>
            r.RequestUri!.AbsolutePath.EndsWith("/pulls/7/merge", StringComparison.Ordinal));
        var mergeBody = http.RequestBodies[http.Requests.IndexOf(mergeRequest)];
        Assert.Contains("\"Do\":\"merge\"", mergeBody);
    }

    [Fact]
    public async Task Lifecycle_ListsOpenPrsFilteredByPrefix()
    {
        UseToken();
        var git = new GiteaRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new GiteaFakePluginHost(ScopedConfig()));

        http.Enqueue(JsonResponse(Fixture("pulls-list.json")));
        http.Enqueue(JsonResponse(Fixture("pull-detail-open.json")));
        var prs = await remote.ListOpenPullRequestsAsync("codeybox/");

        var pr = Assert.Single(prs);
        Assert.Equal(7, pr.Number);
        Assert.Equal("codeybox/abc123", pr.HeadBranch);
        Assert.Equal("main", pr.BaseBranch);
        Assert.Equal("4b5d3f4b0c1a2b3c4d5e6f708192a3b4c5d6e7f", pr.HeadSha);
        Assert.False(pr.HasMergeConflict);
    }

    [Fact]
    public async Task Lifecycle_UnmergeablePr_ReportsConflict()
    {
        UseToken();
        var git = new GiteaRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new GiteaFakePluginHost(ScopedConfig()));

        http.Enqueue(JsonResponse(Fixture("pulls-list.json")));
        http.Enqueue(JsonResponse(
            Fixture("pull-detail-open.json").Replace("\"mergeable\": true", "\"mergeable\": false")));
        var prs = await remote.ListOpenPullRequestsAsync("codeybox/");

        Assert.True(Assert.Single(prs).HasMergeConflict);
    }

    [Fact]
    public async Task Lifecycle_FetchBaseBranch_ReturnsSha()
    {
        UseToken();
        var git = new GiteaRecordingGitHost
        {
            FetchShaToReturn = "0be8717da52d7b24b5dd3586c8c4b7c8d0dcf0d",
        };
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new GiteaFakePluginHost(ScopedConfig()));

        var sha = await remote.FetchBaseBranchAsync("repo-id", "main");

        Assert.Equal("0be8717da52d7b24b5dd3586c8c4b7c8d0dcf0d", sha);
        var fetch = Assert.Single(git.Fetches);
        Assert.Equal("https://git.example.com/myteam/myproject.git", fetch.Url);
    }

    [Fact]
    public async Task Lifecycle_PushAsync_SucceedsAndFailsWithoutThrowing()
    {
        UseToken();
        var git = new GiteaRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new GiteaFakePluginHost(ScopedConfig()));

        var ok = await remote.PushAsync("repo-id", "codeybox/abc123");
        Assert.True(ok.Success);

        git.PushToThrow = new InvalidOperationException("transport down");
        var failed = await remote.PushAsync("repo-id", "codeybox/abc123");
        Assert.False(failed.Success);
        Assert.Contains("transport down", failed.Error);
    }

    [Fact]
    public async Task Lifecycle_ReleaseCreated_AndAlreadyExistsReturnsNull()
    {
        UseToken();
        var git = new GiteaRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new GiteaFakePluginHost(ScopedConfig()));

        http.Enqueue(JsonResponse(Fixture("release-created.json"), HttpStatusCode.Created));
        var url = await remote.CreateTagAndReleaseAsync("v1.2.3", "9aa8b7c6", "notes");
        Assert.Equal("https://git.example.com/myteam/myproject/releases/tag/v1.2.3", url);

        http.Enqueue(new HttpResponseMessage(HttpStatusCode.Conflict));
        var existing = await remote.CreateTagAndReleaseAsync("v1.2.3", "9aa8b7c6", null);
        Assert.Null(existing);
    }

    // ------------------------------------------------------------------
    // Soft outcomes vs infrastructure failures
    // ------------------------------------------------------------------

    [Fact]
    public async Task Complete_PrAlreadyExists422_ReturnsPartialResult()
    {
        UseToken();
        var git = new GiteaRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new GiteaFakePluginHost(ScopedConfig()));

        http.Enqueue(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity));
        var outcome = await remote.CompleteAsync(CompletionRequest());

        Assert.True(outcome.BranchPushed);
        Assert.Null(outcome.PullRequestNumber);
        Assert.NotNull(outcome.Notes);
        Assert.Single(git.Pushes);
    }

    [Fact]
    public async Task Complete_PrAlreadyExists409_ReturnsPartialResult()
    {
        UseToken();
        var git = new GiteaRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new GiteaFakePluginHost(ScopedConfig()));

        http.Enqueue(JsonResponse(
            """{"message": "pull request already exists for this branch"}""",
            HttpStatusCode.Conflict));
        var outcome = await remote.CompleteAsync(CompletionRequest());

        Assert.True(outcome.BranchPushed);
        Assert.Null(outcome.PullRequestNumber);
    }

    [Fact]
    public async Task Complete_MergeBlocked409_ReturnsPartialResult()
    {
        UseToken();
        var git = new GiteaRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new GiteaFakePluginHost(ScopedConfig()));

        http.Enqueue(JsonResponse(Fixture("pull-created.json"), HttpStatusCode.Created));
        http.Enqueue(JsonResponse("""{"message": "not mergeable"}""", HttpStatusCode.Conflict));
        var outcome = await remote.CompleteAsync(CompletionRequest(autoMerge: true));

        Assert.True(outcome.BranchPushed);
        Assert.Equal(7, outcome.PullRequestNumber);
        Assert.Null(outcome.MergedSha);
        Assert.False(outcome.AutoMergeRaced);
        Assert.NotNull(outcome.Notes);
    }

    [Fact]
    public async Task Complete_MergeRaced405_SetsRaceFlag()
    {
        UseToken();
        var git = new GiteaRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new GiteaFakePluginHost(ScopedConfig()));

        http.Enqueue(JsonResponse(Fixture("pull-created.json"), HttpStatusCode.Created));
        http.Enqueue(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
        var outcome = await remote.CompleteAsync(CompletionRequest(autoMerge: true));

        Assert.True(outcome.AutoMergeRaced);
        Assert.Null(outcome.MergedSha);
    }

    [Fact]
    public async Task ForgeFailures_ThrowInfrastructure_WithoutLeakingToken()
    {
        UseToken();
        var git = new GiteaRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new GiteaFakePluginHost(ScopedConfig()));

        http.Enqueue(JsonResponse("""{"message": "boom"}""", HttpStatusCode.InternalServerError));
        var createEx = await Assert.ThrowsAsync<GiteaUpstreamException>(
            () => remote.CompleteAsync(CompletionRequest()));
        Assert.IsAssignableFrom<InvalidOperationException>(createEx);
        Assert.DoesNotContain(FakeToken, createEx.Message);

        http.Enqueue(JsonResponse("""{"message": "denied"}""", HttpStatusCode.Unauthorized));
        var readEx = await Assert.ThrowsAsync<GiteaUpstreamException>(
            () => remote.GetPullRequestAsync(7));
        Assert.DoesNotContain(FakeToken, readEx.Message);

        var offlineHandler = new FakeHttpMessageHandler();
        offlineHandler.EnqueueException(new HttpRequestException("no route to host"));
        var offlineRemote = BuildRemote(git, offlineHandler, new GiteaFakePluginHost(ScopedConfig()));
        var offlineEx = await Assert.ThrowsAsync<GiteaUpstreamException>(
            () => offlineRemote.GetPullRequestAsync(7));
        Assert.DoesNotContain(FakeToken, offlineEx.Message);
    }

    [Fact]
    public async Task RateLimit_RetriesThenSucceeds()
    {
        UseToken();
        var git = new GiteaRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new GiteaFakePluginHost(ScopedConfig()));

        var limited = new HttpResponseMessage((HttpStatusCode)429);
        limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        http.Enqueue(limited);
        http.Enqueue(JsonResponse(Fixture("pull-detail-open.json")));

        var state = await remote.GetPullRequestAsync(7);

        Assert.NotNull(state);
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task PushTransportFailure_WrappedAsInfrastructure()
    {
        UseToken();
        var git = new GiteaRecordingGitHost
        {
            PushToThrow = new InvalidOperationException("connection reset"),
        };
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new GiteaFakePluginHost(ScopedConfig()));

        var ex = await Assert.ThrowsAsync<GiteaUpstreamException>(
            () => remote.CompleteAsync(CompletionRequest()));
        Assert.DoesNotContain(FakeToken, ex.Message);
    }

    // ------------------------------------------------------------------
    // Unsupported is null; supported-and-empty is empty
    // ------------------------------------------------------------------

    [Fact]
    public async Task UnsupportedCapabilities_ReturnNull_WithoutTouchingForge()
    {
        UseToken();
        var git = new GiteaRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new GiteaFakePluginHost(ScopedConfig()));

        Assert.Null(await remote.PostCommentAsync(7, new NewUpstreamComment("hi", filePath: "a.cs", line: 1)));
        Assert.Null(await remote.PostCommentAsync(7, new NewUpstreamComment("hi", replyToId: "401")));
        Assert.Null(await remote.CreateWebhookSubscriptionAsync(
            new NewUpstreamWebhookSubscription(["push"], "https://hooks.example.com/x", "project")));
        Assert.False(await remote.DeleteScopedWebhookAsync("repository", "not-a-number") ?? true);
        Assert.Empty(http.Requests);

        http.Enqueue(JsonResponse("[]"));
        var comments = await remote.ListCommentsAsync(7);
        Assert.NotNull(comments);
        Assert.Empty(comments);
    }

    [Fact]
    public async Task MissingPr_ReturnsNull_NotFailure()
    {
        UseToken();
        var git = new GiteaRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new GiteaFakePluginHost(ScopedConfig()));

        http.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await remote.GetPullRequestAsync(4242));

        http.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await remote.GetReviewStateAsync(4242));

        http.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await remote.GetCheckResultsAsync("deadbeef"));

        http.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await remote.ListCommentsAsync(4242));

        http.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await remote.GetRepositoryMetadataAsync());
    }

    // ------------------------------------------------------------------
    // Credentials never reach a sandbox, URL, or log
    // ------------------------------------------------------------------

    [Fact]
    public async Task Credentials_StayHostSide()
    {
        UseToken();
        var git = new GiteaRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new GiteaFakePluginHost(ScopedConfig()));

        http.Enqueue(JsonResponse(Fixture("pull-created.json"), HttpStatusCode.Created));
        await remote.CompleteAsync(CompletionRequest());

        var pushEnv = Assert.Single(git.PushEnvs);
        Assert.Equal(FakeToken, pushEnv["GIT_PASSWORD"]);
        Assert.Equal(0, git.SandboxAccessCalls);

        var pushUrl = Assert.Single(git.Pushes).Url;
        Assert.DoesNotContain(FakeToken, pushUrl);

        foreach (var request in http.Requests)
        {
            Assert.DoesNotContain(FakeToken, request.RequestUri!.ToString());
            Assert.Equal("token", request.Headers.Authorization?.Scheme);
            Assert.Equal(FakeToken, request.Headers.Authorization?.Parameter);
        }

        foreach (var body in http.RequestBodies)
            Assert.DoesNotContain(FakeToken, body);
    }

    // ------------------------------------------------------------------
    // Extended surfaces map Gitea's real shapes
    // ------------------------------------------------------------------

    [Fact]
    public async Task ReviewState_MapsVerdictsAndQuorum()
    {
        UseToken();
        var git = new GiteaRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new GiteaFakePluginHost(ScopedConfig()));

        http.Enqueue(JsonResponse(Fixture("pull-detail-open.json")));
        http.Enqueue(JsonResponse(Fixture("reviews.json")));
        http.Enqueue(JsonResponse(
            """{"rule_name": "main", "required_approvals": 2, "enable_status_check": true}"""));

        var state = await remote.GetReviewStateAsync(7);

        Assert.NotNull(state);
        Assert.Equal(4, state.Reviews.Count);
        Assert.Equal(UpstreamReviewVerdict.Approved, state.Reviews[0].Verdict);
        Assert.Equal("alice", state.Reviews[0].Reviewer);
        Assert.Equal(UpstreamReviewVerdict.ChangesRequested, state.Reviews[1].Verdict);
        Assert.Equal(UpstreamReviewVerdict.Dismissed, state.Reviews[2].Verdict);
        Assert.Equal(UpstreamReviewVerdict.Commented, state.Reviews[3].Verdict);
        Assert.Equal(2, state.RequiredApprovalCount);
        Assert.False(state.RequirementsMet);
    }

    [Fact]
    public async Task ReviewState_SatisfiedWhenQuorumMet()
    {
        UseToken();
        var git = new GiteaRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new GiteaFakePluginHost(ScopedConfig()));

        http.Enqueue(JsonResponse(Fixture("pull-detail-open.json")));
        http.Enqueue(JsonResponse(
            """[{"id": 1, "state": "APPROVED", "dismissed": false, "stale": false, "user": {"login": "alice"}}, {"id": 2, "state": "APPROVED", "dismissed": false, "stale": false, "user": {"login": "bob"}}]"""));
        http.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));

        var state = await remote.GetReviewStateAsync(7);

        Assert.NotNull(state);
        Assert.Equal(0, state.RequiredApprovalCount);
        Assert.True(state.RequirementsMet);
    }

    [Fact]
    public async Task CheckResults_MapStates()
    {
        UseToken();
        var git = new GiteaRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new GiteaFakePluginHost(ScopedConfig()));

        http.Enqueue(JsonResponse(Fixture("combined-status.json")));
        var summary = await remote.GetCheckResultsAsync("4b5d3f4b0c1a2b3c4d5e6f708192a3b4c5d6e7f");

        Assert.NotNull(summary);
        Assert.Equal(3, summary.Checks.Count);
        Assert.Equal(UpstreamCheckState.Passing, summary.Checks[0].State);
        Assert.Equal("continuous-integration/drone/push", summary.Checks[0].Name);
        Assert.Equal("https://ci.example.com/builds/1", summary.Checks[0].DetailsUrl);
        Assert.Equal(UpstreamCheckState.Failing, summary.Checks[1].State);
        Assert.Equal(UpstreamCheckState.Pending, summary.Checks[2].State);
        Assert.False(summary.RequiredChecksPassed);

        http.Enqueue(JsonResponse("""{"state": "success", "sha": "abc", "statuses": []}"""));
        var empty = await remote.GetCheckResultsAsync("abc");
        Assert.NotNull(empty);
        Assert.Empty(empty.Checks);
        Assert.True(empty.RequiredChecksPassed);
    }

    [Fact]
    public async Task Comments_RoundTrip()
    {
        UseToken();
        var git = new GiteaRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new GiteaFakePluginHost(ScopedConfig()));

        http.Enqueue(JsonResponse(Fixture("issue-comments.json")));
        var comments = await remote.ListCommentsAsync(7);

        Assert.NotNull(comments);
        Assert.Equal(2, comments.Count);
        Assert.Equal("401", comments[0].Id);
        Assert.Equal("alice", comments[0].Author);
        Assert.Equal("First comment", comments[0].Body);
        Assert.Null(comments[0].FilePath);

        http.Enqueue(JsonResponse(Fixture("comment-created.json"), HttpStatusCode.Created));
        var posted = await remote.PostCommentAsync(7, new NewUpstreamComment("Posted from CodeyBox"));

        Assert.NotNull(posted);
        Assert.Equal("403", posted.Id);
        Assert.Equal("codeybox-bot", posted.Author);

        http.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await remote.PostCommentAsync(4242, new NewUpstreamComment("hi")));
    }

    [Fact]
    public async Task Webhooks_AllFourScopes_RouteToNativeEndpoints()
    {
        UseToken();
        var git = new GiteaRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new GiteaFakePluginHost(ScopedConfig()));

        http.Enqueue(JsonResponse(Fixture("hooks.json")));
        var listed = await remote.ListWebhookSubscriptionsAsync();
        Assert.NotNull(listed);
        var sub = Assert.Single(listed);
        Assert.Equal("501", sub.Id);
        Assert.Equal("repository", sub.Scope);
        Assert.Equal(["push", "pull_request"], sub.Events);
        Assert.Equal("https://hooks.example.com/codeybox", sub.TargetUrl);
        Assert.Contains("/api/v1/repos/myteam/myproject/hooks", http.Requests[0].RequestUri!.ToString());

        foreach (var (scope, pathFragment) in new[]
                 {
                     ("organization", "orgs/myteam/hooks"),
                     ("organisation", "orgs/myteam/hooks"),
                     ("user", "user/hooks"),
                     ("system", "admin/hooks"),
                 })
        {
            http.Enqueue(JsonResponse(Fixture("hook-created.json"), HttpStatusCode.Created));
            var created = await remote.CreateScopedWebhookAsync(
                scope,
                new NewUpstreamWebhookSubscription(["pull_request"], "https://hooks.example.com/codeybox-new", scope));
            Assert.NotNull(created);
            Assert.Equal("502", created.Id);
            Assert.Equal(scope, created.Scope);
            Assert.Equal(["pull_request"], created.Events);
            Assert.Contains(pathFragment, http.Requests[^1].RequestUri!.ToString());
            var body = http.RequestBodies[^1];
            Assert.Contains("codeybox-new", body);
            Assert.Contains("pull_request", body);
        }

        http.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
        Assert.True(await remote.DeleteScopedWebhookAsync("repository", "501"));

        http.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.False(await remote.DeleteScopedWebhookAsync("repository", "999999"));
    }

    [Fact]
    public async Task RepositoryMetadata_MapsVisibilityAndProtections()
    {
        UseToken();
        var git = new GiteaRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new GiteaFakePluginHost(ScopedConfig()));

        http.Enqueue(JsonResponse(Fixture("repo.json")));
        http.Enqueue(JsonResponse(Fixture("branch-protections.json")));
        var metadata = await remote.GetRepositoryMetadataAsync();

        Assert.NotNull(metadata);
        Assert.Equal("main", metadata.DefaultBranch);
        Assert.Equal("private", metadata.Visibility);
        var rule = Assert.Single(metadata.BranchProtections);
        Assert.Equal("main", rule.BranchPattern);
        Assert.Equal(2, rule.RequiredApprovalCount);
        Assert.True(rule.RequiresStatusChecks);
    }

    // ------------------------------------------------------------------
    // Pagination and input guards
    // ------------------------------------------------------------------

    [Fact]
    public async Task ListOpenPullRequests_HitsPageCap_ThrowsRatherThanPartial()
    {
        UseToken();
        var git = new GiteaRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new GiteaFakePluginHost(ScopedConfig()));

        for (var page = 0; page < 10; page++)
        {
            var items = string.Join(",", Enumerable.Range(page * 50, 50).Select(i =>
                "{\"id\": " + (1000 + i)
                + ", \"number\": " + (1000 + i)
                + ", \"state\": \"open\", \"merged\": false"
                + ", \"head\": {\"ref\": \"codeybox/x" + i + "\", \"sha\": \"abc\"}"
                + ", \"base\": {\"ref\": \"main\"}}"));
            http.Enqueue(JsonResponse($"[{items}]"));
        }

        var ex = await Assert.ThrowsAsync<GiteaUpstreamException>(
            () => remote.ListOpenPullRequestsAsync("codeybox/"));
        Assert.Contains("partial", ex.Message);
    }

    [Fact]
    public void Options_RejectBadConfig()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new GiteaUpstreamOptions { BaseUrl = "http://git.example.com/api/v1", Owner = "o", Repository = "r" }
                .Validate("test"));
        Assert.Throws<InvalidOperationException>(() =>
            new GiteaUpstreamOptions { BaseUrl = "https://localhost/api/v1", Owner = "o", Repository = "r" }
                .Validate("test"));
        Assert.Throws<InvalidOperationException>(() =>
            new GiteaUpstreamOptions { BaseUrl = "https://10.0.0.5/api/v1", Owner = "o", Repository = "r" }
                .Validate("test"));
        Assert.Throws<InvalidOperationException>(() =>
            new GiteaUpstreamOptions { BaseUrl = "https://git.example.com/api/v1", Owner = "", Repository = "r" }
                .Validate("test"));
        Assert.Throws<InvalidOperationException>(() =>
            new GiteaUpstreamOptions { BaseUrl = "https://git.example.com/api/v1", Owner = "a/b", Repository = "r" }
                .Validate("test"));

        var apiBase = new GiteaUpstreamOptions
        {
            BaseUrl = "https://git.example.com/api/v1/",
            Owner = "myteam",
            Repository = "myproject",
        }.Validate("test");
        Assert.Equal("https://git.example.com/api/v1", apiBase);
    }

    [Fact]
    public async Task BranchGuards_RejectDangerousNames()
    {
        UseToken();
        var git = new GiteaRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new GiteaFakePluginHost(ScopedConfig()));

        var pushFailure = await remote.PushAsync("repo-id", "-evil");
        Assert.False(pushFailure.Success);
        await Assert.ThrowsAsync<ArgumentException>(() => remote.TryMergeUpstreamBranchAsync("main", "-evil"));
        await Assert.ThrowsAsync<ArgumentException>(() => remote.FetchBaseBranchAsync("repo-id", "has space"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => remote.GetPullRequestAsync(0));
        await Assert.ThrowsAsync<ArgumentException>(() => remote.ListOpenPullRequestsAsync(string.Empty));
        Assert.Empty(http.Requests);
    }

    // ------------------------------------------------------------------
    // Live integration (opt-in; read-only)
    // ------------------------------------------------------------------

    /// <summary>
    /// Read-only integration test against a real Gitea instance. Skipped
    /// unless <c>GITEA_TEST_BASEURL</c>, <c>GITEA_TEST_TOKEN</c>,
    /// <c>GITEA_TEST_OWNER</c> and <c>GITEA_TEST_REPO</c> are all set (no
    /// live instance exists in CI, so fixtures above carry the shape
    /// coverage). Optionally reads <c>GITEA_TEST_PR</c> for PR-level checks.
    /// </summary>
    [SkippableFact]
    public async Task Live_ReadOnlySurfaces()
    {
        var baseUrl = Environment.GetEnvironmentVariable("GITEA_TEST_BASEURL");
        var token = Environment.GetEnvironmentVariable("GITEA_TEST_TOKEN");
        var owner = Environment.GetEnvironmentVariable("GITEA_TEST_OWNER");
        var repo = Environment.GetEnvironmentVariable("GITEA_TEST_REPO");
        Skip.IfNot(!string.IsNullOrWhiteSpace(baseUrl)
            && !string.IsNullOrWhiteSpace(token)
            && !string.IsNullOrWhiteSpace(owner)
            && !string.IsNullOrWhiteSpace(repo),
            "Set GITEA_TEST_BASEURL/TOKEN/OWNER/REPO to run the live Gitea test.");

        const string liveTokenVar = "GITEA_TEST_TOKEN";
        var git = new GiteaRecordingGitHost();
        var live = new GiteaUpstreamRemote(
            git,
            new GiteaFakeHttpClientFactory(new HttpClientHandler()));
        var host = new GiteaFakePluginHost(new Dictionary<string, string?>
        {
            ["Gitea:BaseUrl"] = baseUrl,
            ["Gitea:Owner"] = owner,
            ["Gitea:Repository"] = repo,
            ["Gitea:TokenEnvVar"] = liveTokenVar,
        });
        await live.InitializeAsync(new PluginContext("1.0", "codeybox.gitea-upstream", "Gitea", host));

        var metadata = await live.GetRepositoryMetadataAsync();
        Assert.NotNull(metadata);
        Assert.False(string.IsNullOrWhiteSpace(metadata.DefaultBranch));

        var prs = await live.ListOpenPullRequestsAsync("codeybox/");
        Assert.NotNull(prs);

        var prNumber = Environment.GetEnvironmentVariable("GITEA_TEST_PR");
        if (int.TryParse(prNumber, out var number) && number > 0)
        {
            var pr = await live.GetPullRequestAsync(number);
            Assert.NotNull(pr);
        }
    }
}
