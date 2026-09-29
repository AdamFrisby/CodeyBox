using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CodeyBox.BitbucketUpstreamPlugin;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the Bitbucket Cloud upstream-remote plugin
/// (<c>codeybox.bitbucket-upstream</c>). HTTP is faked at the wire level with
/// Bitbucket Cloud API 2.0 shapes (see <c>Fixtures/Bitbucket/</c>, faithful
/// to the published 2.0 reference); git is faked through a recording host.
/// Every test asserts a value the provider produced — a wrong mapping, a
/// swallowed failure, or a leaked credential flips it red.
/// </summary>
public sealed class BitbucketUpstreamPluginTests : IDisposable
{
    private const string TokenEnvVar = "CODEYBOX_TEST_BITBUCKET_CREDENTIAL";
    private const string AppUser = "codeybox-bot-user";
    private const string AppPassword = "s3cret-app-password-not-real";
    private static string AppCredential => $"{AppUser}:{AppPassword}";
    private const string BearerToken = "bearer-token-not-a-real-bitbucket-token";

    private static readonly ProjectId TestProjectId = new("bitbucket-test-project");

    private readonly List<string> _envToRestore = [];

    public void Dispose()
    {
        foreach (var name in _envToRestore)
            Environment.SetEnvironmentVariable(name, null);
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private sealed class BitbucketRecordingGitHost : IGitHost
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

    private sealed class BitbucketFakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public BitbucketFakeHttpClientFactory(HttpMessageHandler handler)
            => _client = new HttpClient(handler);

        public HttpClient CreateClient(string name)
        {
            Assert.Equal("bitbucket-upstream", name);
            return _client;
        }
    }

    private sealed class BitbucketFakePluginHost : IPluginHost, IUpstreamPluginHost
    {
        public ILogger Logger { get; } = NullLogger.Instance;
        public IConfigurationSection ScopedConfig { get; }
        private readonly IReadOnlyDictionary<string, string> _projectConfig;

        public BitbucketFakePluginHost(
            Dictionary<string, string?> scoped,
            IReadOnlyDictionary<string, string>? projectConfig = null)
        {
            ScopedConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(scoped)
                .Build()
                .GetSection("Bitbucket");
            _projectConfig = projectConfig ?? new Dictionary<string, string>();
        }

        public IReadOnlyDictionary<string, string> GetProjectUpstreamConfig(ProjectId projectId)
            => _projectConfig;
    }

    private static Dictionary<string, string?> ScopedConfig() => new()
    {
        ["Bitbucket:BaseUrl"] = "https://api.bitbucket.org/2.0",
        ["Bitbucket:Workspace"] = "myteam",
        ["Bitbucket:Repository"] = "myproject",
        ["Bitbucket:TokenEnvVar"] = TokenEnvVar,
    };

    private void UseToken(string? value = null)
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, value ?? AppCredential);
        _envToRestore.Add(TokenEnvVar);
    }

    private static BitbucketUpstreamRemote BuildRemote(
        BitbucketRecordingGitHost git,
        FakeHttpMessageHandler http,
        BitbucketFakePluginHost host)
    {
        var remote = new BitbucketUpstreamRemote(git, new BitbucketFakeHttpClientFactory(http));
        remote.InitializeAsync(new PluginContext("1.0", "codeybox.bitbucket-upstream", "Bitbucket", host))
            .GetAwaiter().GetResult();
        return remote;
    }

    private static UpstreamCompletionRequest CompletionRequest(bool autoMerge = false) => new()
    {
        RepositoryId = "repo-id",
        WorkItemId = new WorkItemId(Guid.Parse("00000000-0000-0000-0000-000000000031")),
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
        => File.ReadAllText(Path.Combine("Fixtures", "Bitbucket", name));

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    // ------------------------------------------------------------------
    // Identity
    // ------------------------------------------------------------------

    [Fact]
    public void Plugin_RegistersAsBitbucket()
    {
        var remote = new BitbucketUpstreamRemote(
            new BitbucketRecordingGitHost(), new BitbucketFakeHttpClientFactory(new FakeHttpMessageHandler()));
        Assert.Equal("bitbucket", remote.Name);

        var attr = typeof(BitbucketUpstreamRemote)
            .GetCustomAttributes(typeof(CodeyBoxPluginAttribute), inherit: false)
            .OfType<CodeyBoxPluginAttribute>()
            .Single();
        Assert.Equal("codeybox.bitbucket-upstream", attr.Id);
    }

    // ------------------------------------------------------------------
    // Full lifecycle: push, open, read, merge
    // ------------------------------------------------------------------

    [Fact]
    public async Task Lifecycle_PushOpenReadMerge()
    {
        UseToken();
        var git = new BitbucketRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new BitbucketFakePluginHost(ScopedConfig()));

        http.Enqueue(JsonResponse(Fixture("pr-created.json"), HttpStatusCode.Created));
        var opened = await remote.CompleteAsync(CompletionRequest());

        Assert.True(opened.BranchPushed);
        Assert.Equal(12, opened.PullRequestNumber);
        Assert.Equal("https://bitbucket.org/myteam/myproject/pull-requests/12", opened.PullRequestUrl);
        Assert.Null(opened.MergedSha);

        var (repoId, pushUrl, pushBranch) = Assert.Single(git.Pushes);
        Assert.Equal("repo-id", repoId);
        Assert.Equal("codeybox/abc123", pushBranch);
        Assert.Equal("https://bitbucket.org/myteam/myproject.git", pushUrl);

        var createRequest = http.Requests.Single(r =>
            r.RequestUri!.AbsolutePath.EndsWith("/pullrequests", StringComparison.Ordinal));
        var createBody = http.RequestBodies[http.Requests.IndexOf(createRequest)];
        Assert.Contains("codeybox/abc123", createBody);
        Assert.Contains("\"main\"", createBody);

        http.Enqueue(JsonResponse(Fixture("pr-open.json")));
        var state = await remote.GetPullRequestAsync(12);

        Assert.NotNull(state);
        Assert.Equal(PullRequestStatus.Open, state.Status);
        Assert.Null(state.MergeCommitSha);

        http.Enqueue(JsonResponse(Fixture("pr-created.json"), HttpStatusCode.Created));
        http.Enqueue(JsonResponse(Fixture("pr-merged.json")));
        http.Enqueue(JsonResponse(Fixture("pr-merged.json")));
        var merged = await remote.CompleteAsync(CompletionRequest(autoMerge: true));

        Assert.Equal(12, merged.PullRequestNumber);
        Assert.Equal("9aa8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0", merged.MergedSha);

        var mergeRequest = http.Requests.First(r =>
            r.RequestUri!.AbsolutePath.EndsWith("/pullrequests/12/merge", StringComparison.Ordinal));
        var mergeBody = http.RequestBodies[http.Requests.IndexOf(mergeRequest)];
        Assert.Contains("merge_commit", mergeBody);
    }

    [Fact]
    public async Task Lifecycle_MergedAndDeclined_ReadAsTerminalStates()
    {
        UseToken();
        var git = new BitbucketRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new BitbucketFakePluginHost(ScopedConfig()));

        http.Enqueue(JsonResponse(Fixture("pr-merged.json")));
        var merged = await remote.GetPullRequestAsync(12);
        Assert.NotNull(merged);
        Assert.Equal(PullRequestStatus.Merged, merged.Status);
        Assert.Equal("9aa8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0", merged.MergeCommitSha);

        http.Enqueue(JsonResponse(
            Fixture("pr-open.json").Replace("\"state\": \"OPEN\"", "\"state\": \"DECLINED\"")));
        var declined = await remote.GetPullRequestAsync(12);
        Assert.NotNull(declined);
        Assert.Equal(PullRequestStatus.Closed, declined.Status);
    }

    [Fact]
    public async Task Lifecycle_ListsOpenPrsFilteredByPrefix()
    {
        UseToken();
        var git = new BitbucketRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new BitbucketFakePluginHost(ScopedConfig()));

        http.Enqueue(JsonResponse(Fixture("pr-list.json")));
        var prs = await remote.ListOpenPullRequestsAsync("codeybox/");

        var pr = Assert.Single(prs);
        Assert.Equal(12, pr.Number);
        Assert.Equal("codeybox/abc123", pr.HeadBranch);
        Assert.Equal("main", pr.BaseBranch);
        Assert.Equal("4b5d3f4b0c1a2b3c4d5e6f708192a3b4c5d6e7f", pr.HeadSha);
        // Bitbucket exposes no mergeability signal; the provider reports
        // that honestly instead of synthesising a conflict verdict.
        Assert.False(pr.HasMergeConflict);
    }

    [Fact]
    public async Task Lifecycle_FetchBaseBranch_ReturnsSha()
    {
        UseToken();
        var git = new BitbucketRecordingGitHost
        {
            FetchShaToReturn = "0be8717da52d7b24b5dd3586c8c4b7c8d0dcf0d",
        };
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new BitbucketFakePluginHost(ScopedConfig()));

        var sha = await remote.FetchBaseBranchAsync("repo-id", "main");

        Assert.Equal("0be8717da52d7b24b5dd3586c8c4b7c8d0dcf0d", sha);
        var fetch = Assert.Single(git.Fetches);
        Assert.Equal("https://bitbucket.org/myteam/myproject.git", fetch.Url);
    }

    [Fact]
    public async Task Lifecycle_PushAsync_SucceedsAndFailsWithoutThrowing()
    {
        UseToken();
        var git = new BitbucketRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new BitbucketFakePluginHost(ScopedConfig()));

        var ok = await remote.PushAsync("repo-id", "codeybox/abc123");
        Assert.True(ok.Success);

        git.PushToThrow = new InvalidOperationException("transport down");
        var failed = await remote.PushAsync("repo-id", "codeybox/abc123");
        Assert.False(failed.Success);
        Assert.Contains("transport down", failed.Error);
    }

    [Fact]
    public async Task Lifecycle_ReleasesUnsupported_ReturnsNullWithoutTouchingForge()
    {
        UseToken();
        var git = new BitbucketRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new BitbucketFakePluginHost(ScopedConfig()));

        // Bitbucket Cloud has no releases concept: the contract default
        // (null = unsupported) stands, and no HTTP is attempted.
        IUpstreamRemote upstream = remote;
        Assert.Null(await upstream.CreateTagAndReleaseAsync("v1.2.3", "9aa8b7c6", "notes"));
        Assert.Empty(http.Requests);
    }

    // ------------------------------------------------------------------
    // Soft outcomes vs infrastructure failures
    // ------------------------------------------------------------------

    [Fact]
    public async Task Complete_PrAlreadyExists_ReturnsPartialResult()
    {
        UseToken();
        var git = new BitbucketRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new BitbucketFakePluginHost(ScopedConfig()));

        http.Enqueue(JsonResponse(
            """{"type": "error", "error": {"message": "A pull request already exists for this branch"}}""",
            HttpStatusCode.BadRequest));
        var outcome = await remote.CompleteAsync(CompletionRequest());

        Assert.True(outcome.BranchPushed);
        Assert.Null(outcome.PullRequestNumber);
        Assert.NotNull(outcome.Notes);
        Assert.Single(git.Pushes);
    }

    [Fact]
    public async Task Complete_ExistingPrNumber_SkipsCreate()
    {
        UseToken();
        var git = new BitbucketRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new BitbucketFakePluginHost(ScopedConfig()));

        http.Enqueue(JsonResponse(Fixture("pr-open.json")));
        var request = CompletionRequest() with { ExistingPullRequestNumber = 12 };
        var outcome = await remote.CompleteAsync(request);

        Assert.True(outcome.BranchPushed);
        Assert.Equal(12, outcome.PullRequestNumber);
        Assert.DoesNotContain(http.RequestBodies, b => b.Contains("destination"));
    }

    [Fact]
    public async Task Complete_MergeBlocked_ReturnsPartialResult()
    {
        UseToken();
        var git = new BitbucketRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new BitbucketFakePluginHost(ScopedConfig()));

        http.Enqueue(JsonResponse(Fixture("pr-created.json"), HttpStatusCode.Created));
        http.Enqueue(JsonResponse(
            """{"type": "error", "error": {"message": "Pull request is not mergeable"}}""",
            HttpStatusCode.BadRequest));
        var outcome = await remote.CompleteAsync(CompletionRequest(autoMerge: true));

        Assert.True(outcome.BranchPushed);
        Assert.Equal(12, outcome.PullRequestNumber);
        Assert.Null(outcome.MergedSha);
        Assert.False(outcome.AutoMergeRaced);
        Assert.NotNull(outcome.Notes);
    }

    [Fact]
    public async Task ForgeFailures_ThrowInfrastructure_WithoutLeakingCredential()
    {
        UseToken();
        var git = new BitbucketRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new BitbucketFakePluginHost(ScopedConfig()));

        http.Enqueue(JsonResponse("""{"type": "error", "error": {"message": "boom"}}""",
            HttpStatusCode.InternalServerError));
        var createEx = await Assert.ThrowsAsync<BitbucketUpstreamException>(
            () => remote.CompleteAsync(CompletionRequest()));
        Assert.IsAssignableFrom<InvalidOperationException>(createEx);
        Assert.DoesNotContain(AppPassword, createEx.Message);

        http.Enqueue(JsonResponse("""{"type": "error", "error": {"message": "denied"}}""",
            HttpStatusCode.Unauthorized));
        var readEx = await Assert.ThrowsAsync<BitbucketUpstreamException>(
            () => remote.GetPullRequestAsync(12));
        Assert.DoesNotContain(AppPassword, readEx.Message);

        var offlineHandler = new FakeHttpMessageHandler();
        offlineHandler.EnqueueException(new HttpRequestException("no route to host"));
        var offlineRemote = BuildRemote(git, offlineHandler, new BitbucketFakePluginHost(ScopedConfig()));
        var offlineEx = await Assert.ThrowsAsync<BitbucketUpstreamException>(
            () => offlineRemote.GetPullRequestAsync(12));
        Assert.DoesNotContain(AppPassword, offlineEx.Message);
    }

    [Fact]
    public async Task RateLimit_RetriesThenSucceeds()
    {
        UseToken();
        var git = new BitbucketRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new BitbucketFakePluginHost(ScopedConfig()));

        var limited = new HttpResponseMessage((HttpStatusCode)429);
        limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        http.Enqueue(limited);
        http.Enqueue(JsonResponse(Fixture("pr-open.json")));

        var state = await remote.GetPullRequestAsync(12);

        Assert.NotNull(state);
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task PushTransportFailure_WrappedAsInfrastructure()
    {
        UseToken();
        var git = new BitbucketRecordingGitHost
        {
            PushToThrow = new InvalidOperationException("connection reset"),
        };
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new BitbucketFakePluginHost(ScopedConfig()));

        var ex = await Assert.ThrowsAsync<BitbucketUpstreamException>(
            () => remote.CompleteAsync(CompletionRequest()));
        Assert.DoesNotContain(AppPassword, ex.Message);
    }

    // ------------------------------------------------------------------
    // Unsupported is null; supported-and-empty is empty
    // ------------------------------------------------------------------

    [Fact]
    public async Task UnsupportedCapabilities_ReturnNull_WithoutTouchingForge()
    {
        UseToken();
        var git = new BitbucketRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new BitbucketFakePluginHost(ScopedConfig()));

        // Bitbucket inline posts need diff-hunk positions a path-plus-line
        // cannot supply; non-repository webhook scopes have no Bitbucket
        // equivalent. Both return null without an HTTP call.
        Assert.Null(await remote.PostCommentAsync(12, new NewUpstreamComment("hi", filePath: "a.cs", line: 1)));
        Assert.Null(await remote.PostCommentAsync(12, new NewUpstreamComment("hi", replyToId: "not-a-bitbucket-id")));
        Assert.Null(await remote.CreateWebhookSubscriptionAsync(
            new NewUpstreamWebhookSubscription(["repo:push"], "https://hooks.example.com/x", "organization")));
        Assert.Null(await remote.CreateWebhookSubscriptionAsync(
            new NewUpstreamWebhookSubscription(["repo:push"], "https://hooks.example.com/x", "user")));
        Assert.Null(await remote.CreateWebhookSubscriptionAsync(
            new NewUpstreamWebhookSubscription(["repo:push"], "https://hooks.example.com/x", "system")));
        Assert.Empty(http.Requests);

        http.Enqueue(JsonResponse("""{"pagelen": 50, "page": 1, "size": 0, "values": []}"""));
        var comments = await remote.ListCommentsAsync(12);
        Assert.NotNull(comments);
        Assert.Empty(comments);

        http.Enqueue(JsonResponse("""{"pagelen": 50, "page": 1, "size": 0, "values": []}"""));
        var hooks = await remote.ListWebhookSubscriptionsAsync();
        Assert.NotNull(hooks);
        Assert.Empty(hooks);

        http.Enqueue(JsonResponse("""{"pagelen": 50, "page": 1, "size": 0, "values": []}"""));
        var checks = await remote.GetCheckResultsAsync("4b5d3f4b0c1a2b3c4d5e6f708192a3b4c5d6e7f");
        Assert.NotNull(checks);
        Assert.Empty(checks.Checks);
        Assert.True(checks.RequiredChecksPassed);
    }

    [Fact]
    public async Task MissingPr_ReturnsNull_NotFailure()
    {
        UseToken();
        var git = new BitbucketRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new BitbucketFakePluginHost(ScopedConfig()));

        http.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await remote.GetPullRequestAsync(4242));

        http.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        http.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await remote.GetReviewStateAsync(4242));

        http.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await remote.GetCheckResultsAsync("deadbeef"));

        http.Enqueue(JsonResponse("""{"pagelen": 50, "page": 1, "size": 0, "next": null, "values": []}""",
            HttpStatusCode.NotFound));
        Assert.Null(await remote.ListCommentsAsync(4242));

        http.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await remote.GetRepositoryMetadataAsync());
    }

    // ------------------------------------------------------------------
    // Credentials never reach a sandbox, URL, or log
    // ------------------------------------------------------------------

    [Fact]
    public async Task Credentials_AppPassword_StayHostSide()
    {
        UseToken();
        var git = new BitbucketRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new BitbucketFakePluginHost(ScopedConfig()));

        http.Enqueue(JsonResponse(Fixture("pr-created.json"), HttpStatusCode.Created));
        await remote.CompleteAsync(CompletionRequest());

        var pushEnv = Assert.Single(git.PushEnvs);
        Assert.Equal(AppUser, pushEnv["GIT_USERNAME"]);
        Assert.Equal(AppPassword, pushEnv["GIT_PASSWORD"]);
        Assert.Equal(0, git.SandboxAccessCalls);

        var pushUrl = Assert.Single(git.Pushes).Url;
        Assert.DoesNotContain(AppPassword, pushUrl);

        foreach (var request in http.Requests)
        {
            Assert.DoesNotContain(AppPassword, request.RequestUri!.ToString());
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            var decoded = Encoding.UTF8.GetString(
                Convert.FromBase64String(request.Headers.Authorization!.Parameter!));
            Assert.Equal(AppCredential, decoded);
        }

        foreach (var body in http.RequestBodies)
            Assert.DoesNotContain(AppPassword, body);
    }

    [Fact]
    public async Task Credentials_BearerToken_UsesBearerScheme()
    {
        UseToken(BearerToken);
        var git = new BitbucketRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new BitbucketFakePluginHost(ScopedConfig()));

        http.Enqueue(JsonResponse(Fixture("pr-open.json")));
        await remote.GetPullRequestAsync(12);

        var request = Assert.Single(http.Requests);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal(BearerToken, request.Headers.Authorization?.Parameter);
        Assert.DoesNotContain(BearerToken, request.RequestUri!.ToString());

        var pushEnv = new Dictionary<string, string>();
        git.PushEnvs.Clear();
        http.Enqueue(JsonResponse(Fixture("pr-created.json"), HttpStatusCode.Created));
        await remote.CompleteAsync(CompletionRequest());
        pushEnv = (Dictionary<string, string>)Assert.Single(git.PushEnvs);
        Assert.Equal("x-token-auth", pushEnv["GIT_USERNAME"]);
        Assert.Equal(BearerToken, pushEnv["GIT_PASSWORD"]);
    }

    // ------------------------------------------------------------------
    // Extended surfaces map Bitbucket's real shapes
    // ------------------------------------------------------------------

    [Fact]
    public async Task ReviewState_MapsParticipantsAndQuorum()
    {
        UseToken();
        var git = new BitbucketRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new BitbucketFakePluginHost(ScopedConfig()));

        http.Enqueue(JsonResponse(Fixture("pr-open.json")));
        http.Enqueue(JsonResponse(Fixture("branch-restrictions.json")));

        var state = await remote.GetReviewStateAsync(12);

        Assert.NotNull(state);
        Assert.Equal(4, state.Reviews.Count);
        Assert.Equal(UpstreamReviewVerdict.Approved, state.Reviews[0].Verdict);
        Assert.Equal("Alice", state.Reviews[0].Reviewer);
        Assert.Equal(UpstreamReviewVerdict.Pending, state.Reviews[1].Verdict);
        Assert.Equal("Bob", state.Reviews[1].Reviewer);
        Assert.Equal(UpstreamReviewVerdict.ChangesRequested, state.Reviews[2].Verdict);
        Assert.Equal(UpstreamReviewVerdict.Commented, state.Reviews[3].Verdict);
        Assert.Equal(["Bob"], state.RequiredReviewers);
        Assert.Equal(2, state.RequiredApprovalCount);
        Assert.False(state.RequirementsMet);
    }

    [Fact]
    public async Task ReviewState_SatisfiedWhenQuorumMet()
    {
        UseToken();
        var git = new BitbucketRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new BitbucketFakePluginHost(ScopedConfig()));

        http.Enqueue(JsonResponse("""
            {"id": 12, "state": "OPEN",
             "links": {"html": {"href": "https://bitbucket.org/myteam/myproject/pull-requests/12"}},
             "source": {"branch": {"name": "codeybox/x"}, "commit": {"hash": "abc"}},
             "destination": {"branch": {"name": "main"}},
             "participants": [
               {"user": {"display_name": "Alice"}, "role": "REVIEWER", "approved": true, "state": "approved"},
               {"user": {"display_name": "Bob"}, "role": "REVIEWER", "approved": true, "state": "approved"}
             ]}
            """));
        http.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));

        var state = await remote.GetReviewStateAsync(12);

        Assert.NotNull(state);
        Assert.Equal(0, state.RequiredApprovalCount);
        Assert.True(state.RequirementsMet);
    }

    [Fact]
    public async Task CheckResults_MapBuildStates()
    {
        UseToken();
        var git = new BitbucketRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new BitbucketFakePluginHost(ScopedConfig()));

        http.Enqueue(JsonResponse(Fixture("builds.json")));
        var summary = await remote.GetCheckResultsAsync("4b5d3f4b0c1a2b3c4d5e6f708192a3b4c5d6e7f");

        Assert.NotNull(summary);
        Assert.Equal(3, summary.Checks.Count);
        Assert.Equal(UpstreamCheckState.Passing, summary.Checks[0].State);
        Assert.Equal("ci/build", summary.Checks[0].Name);
        Assert.Equal("https://ci.example.com/builds/1", summary.Checks[0].DetailsUrl);
        Assert.Equal(UpstreamCheckState.Failing, summary.Checks[1].State);
        Assert.Equal(UpstreamCheckState.Pending, summary.Checks[2].State);
        Assert.False(summary.RequiredChecksPassed);
        Assert.Contains("statuses/builds", http.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Comments_RoundTrip()
    {
        UseToken();
        var git = new BitbucketRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new BitbucketFakePluginHost(ScopedConfig()));

        http.Enqueue(JsonResponse(Fixture("comments.json")));
        var comments = await remote.ListCommentsAsync(12);

        Assert.NotNull(comments);
        Assert.Equal(2, comments.Count);
        Assert.Equal("501", comments[0].Id);
        Assert.Equal("alice", comments[0].Author);
        Assert.Equal("First comment", comments[0].Body);
        Assert.Null(comments[0].FilePath);
        Assert.Equal("src/app.cs", comments[1].FilePath);
        Assert.Equal(42, comments[1].Line);

        http.Enqueue(JsonResponse(Fixture("comment-created.json"), HttpStatusCode.Created));
        var posted = await remote.PostCommentAsync(12, new NewUpstreamComment("Posted from CodeyBox"));

        Assert.NotNull(posted);
        Assert.Equal("503", posted.Id);
        Assert.Equal("codeybox-bot", posted.Author);

        http.Enqueue(JsonResponse(Fixture("comment-created.json"), HttpStatusCode.Created));
        var replied = await remote.PostCommentAsync(12, new NewUpstreamComment("reply", replyToId: "501"));
        Assert.NotNull(replied);
        Assert.Contains("\"parent\"", http.RequestBodies[^1]);
        Assert.Contains("501", http.RequestBodies[^1]);

        http.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await remote.PostCommentAsync(4242, new NewUpstreamComment("hi")));
    }

    [Fact]
    public async Task Webhooks_RepositoryScope_ListsCreatesDeletes()
    {
        UseToken();
        var git = new BitbucketRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new BitbucketFakePluginHost(ScopedConfig()));

        http.Enqueue(JsonResponse(Fixture("hooks.json")));
        var listed = await remote.ListWebhookSubscriptionsAsync();
        Assert.NotNull(listed);
        var sub = Assert.Single(listed);
        Assert.Equal("{c3d4e5f6-7890-1234-5678-9abcdef01234}", sub.Id);
        Assert.Equal("repository", sub.Scope);
        Assert.Equal(["repo:push", "pullrequest:created"], sub.Events);
        Assert.Equal("https://hooks.example.com/codeybox", sub.TargetUrl);
        Assert.Contains("/2.0/repositories/myteam/myproject/hooks", http.Requests[0].RequestUri!.ToString());

        http.Enqueue(JsonResponse(Fixture("hook-created.json"), HttpStatusCode.Created));
        var created = await remote.CreateWebhookSubscriptionAsync(
            new NewUpstreamWebhookSubscription(
                ["pullrequest:created"], "https://hooks.example.com/codeybox-new", "repository"));
        Assert.NotNull(created);
        Assert.Equal("{d4e5f678-9012-3456-7890-abcdef012345}", created.Id);
        Assert.Equal("repository", created.Scope);
        var body = http.RequestBodies[^1];
        Assert.Contains("codeybox-new", body);
        Assert.Contains("pullrequest:created", body);

        http.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
        Assert.True(await remote.DeleteWebhookSubscriptionAsync("{c3d4e5f6-7890-1234-5678-9abcdef01234}"));

        http.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.False(await remote.DeleteWebhookSubscriptionAsync("{00000000-0000-0000-0000-000000000000}"));
    }

    [Fact]
    public async Task RepositoryMetadata_MapsVisibilityAndRestrictions()
    {
        UseToken();
        var git = new BitbucketRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new BitbucketFakePluginHost(ScopedConfig()));

        http.Enqueue(JsonResponse(Fixture("repo.json")));
        http.Enqueue(JsonResponse(Fixture("branch-restrictions.json")));
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
        var git = new BitbucketRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new BitbucketFakePluginHost(ScopedConfig()));

        for (var page = 1; page <= 10; page++)
        {
            var items = string.Join(",", Enumerable.Range((page - 1) * 50, 50).Select(i =>
                "{\"id\": " + (1000 + i)
                + ", \"state\": \"OPEN\""
                + ", \"source\": {\"branch\": {\"name\": \"codeybox/x" + i + "\"}, \"commit\": {\"hash\": \"abc\"}}"
                + ", \"destination\": {\"branch\": {\"name\": \"main\"}}}"));
            var next = page < 10
                ? $", \"next\": \"https://api.bitbucket.org/2.0/repositories/myteam/myproject/pullrequests?state=OPEN&pagelen=50&page={page + 1}\""
                : ", \"next\": \"https://api.bitbucket.org/2.0/repositories/myteam/myproject/pullrequests?state=OPEN&pagelen=50&page=11\"";
            http.Enqueue(JsonResponse(
                "{\"pagelen\": 50, \"page\": " + page + ", \"size\": 1000" + next + ", \"values\": [" + items + "]}"));
        }

        var ex = await Assert.ThrowsAsync<BitbucketUpstreamException>(
            () => remote.ListOpenPullRequestsAsync("codeybox/"));
        Assert.Contains("partial", ex.Message);
    }

    [Fact]
    public void Options_RejectBadConfig()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new BitbucketUpstreamOptions { BaseUrl = "http://example.com/2.0", Workspace = "w", Repository = "r" }
                .Validate("test"));
        Assert.Throws<InvalidOperationException>(() =>
            new BitbucketUpstreamOptions { BaseUrl = "https://localhost/2.0", Workspace = "w", Repository = "r" }
                .Validate("test"));
        Assert.Throws<InvalidOperationException>(() =>
            new BitbucketUpstreamOptions { BaseUrl = "https://10.0.0.5/2.0", Workspace = "w", Repository = "r" }
                .Validate("test"));
        Assert.Throws<InvalidOperationException>(() =>
            new BitbucketUpstreamOptions { BaseUrl = "https://api.bitbucket.org/2.0", Workspace = "", Repository = "r" }
                .Validate("test"));
        Assert.Throws<InvalidOperationException>(() =>
            new BitbucketUpstreamOptions { BaseUrl = "https://api.bitbucket.org/2.0", Workspace = "a/b", Repository = "r" }
                .Validate("test"));

        var apiBase = new BitbucketUpstreamOptions
        {
            Workspace = "myteam",
            Repository = "myproject",
        }.Validate("test");
        Assert.Equal("https://api.bitbucket.org/2.0", apiBase);
    }

    [Fact]
    public async Task BranchGuards_RejectDangerousNames()
    {
        UseToken();
        var git = new BitbucketRecordingGitHost();
        var http = new FakeHttpMessageHandler();
        var remote = BuildRemote(git, http, new BitbucketFakePluginHost(ScopedConfig()));

        var pushFailure = await remote.PushAsync("repo-id", "-evil");
        Assert.False(pushFailure.Success);
        await Assert.ThrowsAsync<ArgumentException>(() => remote.TryMergeUpstreamBranchAsync("main", "-evil"));
        await Assert.ThrowsAsync<ArgumentException>(() => remote.FetchBaseBranchAsync("repo-id", "has space"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => remote.GetPullRequestAsync(0));
        await Assert.ThrowsAsync<ArgumentException>(() => remote.ListOpenPullRequestsAsync(string.Empty));
        await Assert.ThrowsAsync<ArgumentException>(() => remote.GetCheckResultsAsync("  "));
        Assert.Empty(http.Requests);
    }

    // ------------------------------------------------------------------
    // Live integration (opt-in; read-only)
    // ------------------------------------------------------------------

    /// <summary>
    /// Read-only integration test against real Bitbucket Cloud. Skipped
    /// unless <c>BITBUCKET_TEST_WORKSPACE</c>, <c>BITBUCKET_TEST_REPO</c> and
    /// <c>BITBUCKET_TEST_CREDENTIAL</c> (username:app-password or token) are
    /// all set — no live instance exists in CI, so the fixtures above carry
    /// the shape coverage. Optionally reads <c>BITBUCKET_TEST_PR</c> for
    /// PR-level checks.
    /// </summary>
    [SkippableFact]
    public async Task Live_ReadOnlySurfaces()
    {
        var workspace = Environment.GetEnvironmentVariable("BITBUCKET_TEST_WORKSPACE");
        var repo = Environment.GetEnvironmentVariable("BITBUCKET_TEST_REPO");
        var credential = Environment.GetEnvironmentVariable("BITBUCKET_TEST_CREDENTIAL");
        Skip.IfNot(!string.IsNullOrWhiteSpace(workspace)
            && !string.IsNullOrWhiteSpace(repo)
            && !string.IsNullOrWhiteSpace(credential),
            "Set BITBUCKET_TEST_WORKSPACE/REPO/CREDENTIAL to run the live Bitbucket test.");

        const string liveCredentialVar = "BITBUCKET_TEST_CREDENTIAL";
        var git = new BitbucketRecordingGitHost();
        var live = new BitbucketUpstreamRemote(
            git,
            new BitbucketFakeHttpClientFactory(new HttpClientHandler()));
        var host = new BitbucketFakePluginHost(new Dictionary<string, string?>
        {
            ["Bitbucket:Workspace"] = workspace,
            ["Bitbucket:Repository"] = repo,
            ["Bitbucket:TokenEnvVar"] = liveCredentialVar,
        });
        await live.InitializeAsync(new PluginContext("1.0", "codeybox.bitbucket-upstream", "Bitbucket", host));

        var metadata = await live.GetRepositoryMetadataAsync();
        Assert.NotNull(metadata);
        Assert.False(string.IsNullOrWhiteSpace(metadata.DefaultBranch));

        var prs = await live.ListOpenPullRequestsAsync("codeybox/");
        Assert.NotNull(prs);

        var prNumber = Environment.GetEnvironmentVariable("BITBUCKET_TEST_PR");
        if (int.TryParse(prNumber, out var number) && number > 0)
        {
            var pr = await live.GetPullRequestAsync(number);
            Assert.NotNull(pr);
        }
    }
}
