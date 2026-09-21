using System.Net;
using System.Text;
using CodeyBox.AzureDevOpsUpstreamPlugin;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

// Tests for the Azure DevOps upstream remote plugin. All HTTP traffic is
// served by an in-memory stub returning recorded Azure DevOps REST
// (api-version 7.1) response shapes — no live instance exists in this
// environment (see the plugin README for why), so these fixtures plus the
// documented shapes are the integration evidence.
public sealed class AzureDevOpsUpstreamPluginTests
{
    private const string Org = "contoso";
    private const string Project = "Fabrikam";
    private const string Repo = "WebApp";
    private const string PatEnvVar = "CODEYBOX_TEST_ADO_PAT";
    private const string TestPat = "test-pat-not-a-real-credential";

    // -- recorded-shape fixtures (Azure DevOps REST api-version 7.1) --------

    private const string CreatedPrJson = """
        {
          "pullRequestId": 42,
          "status": "active",
          "mergeStatus": "succeeded",
          "sourceRefName": "refs/heads/feature/work-1",
          "targetRefName": "refs/heads/main",
          "lastMergeSourceCommit": { "commitId": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
          "url": "https://dev.azure.com/contoso/Fabrikam/_apis/git/repositories/WebApp/pullRequests/42"
        }
        """;

    private const string ActivePrJson = """
        {
          "pullRequestId": 42,
          "status": "active",
          "mergeStatus": "succeeded",
          "sourceRefName": "refs/heads/feature/work-1",
          "targetRefName": "refs/heads/main",
          "lastMergeSourceCommit": { "commitId": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
          "lastMergeCommit": { "commitId": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }
        }
        """;

    private const string CompletedPrJson = """
        {
          "pullRequestId": 42,
          "status": "completed",
          "mergeStatus": "succeeded",
          "sourceRefName": "refs/heads/feature/work-1",
          "targetRefName": "refs/heads/main",
          "lastMergeSourceCommit": { "commitId": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
          "lastMergeCommit": { "commitId": "cccccccccccccccccccccccccccccccccccccccc" }
        }
        """;

    private const string AbandonedPrJson = """
        {
          "pullRequestId": 43,
          "status": "abandoned",
          "mergeStatus": "succeeded",
          "sourceRefName": "refs/heads/feature/work-2",
          "targetRefName": "refs/heads/main"
        }
        """;

    // -- helpers -------------------------------------------------------------

    private sealed class AdoStubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, string, HttpResponseMessage> Router { get; set; } =
            (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound);
        public List<(string Method, string Url, string Body)> Calls { get; } = new();
        public List<string?> AuthParams { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request.Method.Method, request.RequestUri!.ToString(), body));
            AuthParams.Add(request.Headers.Authorization?.Parameter);
            return Router(request, body);
        }
    }

    private sealed class AdoHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public AdoHttpClientFactory(HttpMessageHandler handler) => _client = new HttpClient(handler);
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("azure-devops-upstream", name);
            return _client;
        }
    }

    private sealed class AdoFakePluginHost : IPluginHost, IUpstreamPluginHost
    {
        private readonly Dictionary<ProjectId, IReadOnlyDictionary<string, string>> _projects;
        public AdoFakePluginHost(
            Dictionary<string, string>? scoped = null,
            Dictionary<ProjectId, IReadOnlyDictionary<string, string>>? projects = null)
        {
            var pairs = (scoped ?? new()).Select(kv =>
                new KeyValuePair<string, string?>("Ado:" + kv.Key, kv.Value));
            ScopedConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(pairs)
                .Build()
                .GetSection("Ado");
            _projects = projects ?? new();
        }
        public Microsoft.Extensions.Logging.ILogger Logger => NullLogger.Instance;
        public IConfigurationSection ScopedConfig { get; }
        public IReadOnlyDictionary<string, string> GetProjectUpstreamConfig(ProjectId projectId)
            => _projects.TryGetValue(projectId, out var cfg)
                ? cfg
                : new Dictionary<string, string>();
    }

    private static readonly ProjectId TestProjectId = new("ado-test-project");

    private static Dictionary<ProjectId, IReadOnlyDictionary<string, string>> ProjectConfigs(
        Dictionary<string, string>? extra = null)
    {
        var cfg = new Dictionary<string, string>
        {
            ["Organization"] = Org,
            ["Project"] = Project,
            ["Repository"] = Repo,
        };
        if (extra is not null)
            foreach (var (k, v) in extra)
                cfg[k] = v;
        return new() { [TestProjectId] = cfg };
    }

    private static Dictionary<string, string> ScopedDefaults() => new()
    {
        ["Organization"] = Org,
        ["Project"] = Project,
        ["Repository"] = Repo,
        ["TokenEnvVar"] = PatEnvVar,
    };

    private static AzureDevOpsUpstreamRemote CreateRemote(
        FakeGitHost git,
        AdoStubHandler http,
        Dictionary<string, string>? scoped = null,
        Dictionary<ProjectId, IReadOnlyDictionary<string, string>>? projects = null)
    {
        var remote = new AzureDevOpsUpstreamRemote(git, new AdoHttpClientFactory(http));
        var host = new AdoFakePluginHost(scoped, projects);
        remote.InitializeAsync(new PluginContext("1.0", "test.ado", "ADO test", host)).GetAwaiter().GetResult();
        return remote;
    }

    private static UpstreamCompletionRequest CompletionRequest(bool autoMerge = true) => new()
    {
        RepositoryId = "repo-id",
        WorkItemId = WorkItemId.New(),
        ProjectId = TestProjectId,
        WorkBranch = "feature/work-1",
        BaseBranch = "main",
        Title = "Do the thing",
        Description = "Static description",
        TokenEnvVar = PatEnvVar,
        AutoMerge = autoMerge,
        MergeMethod = "merge",
    };

    private static async Task WithPatAsync(Func<Task> action)
    {
        Environment.SetEnvironmentVariable(PatEnvVar, TestPat);
        try { await action(); }
        finally { Environment.SetEnvironmentVariable(PatEnvVar, null); }
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string DecodeBasic(AdoStubHandler http, int index)
    {
        var param = http.AuthParams[index];
        Assert.NotNull(param);
        return Encoding.ASCII.GetString(Convert.FromBase64String(param));
    }

    // -- lifecycle -----------------------------------------------------------

    [Fact]
    public async Task Complete_PushOpenReadMerge_FullLifecycle()
    {
        var git = new FakeGitHost();
        var http = new AdoStubHandler();
        http.Router = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && url.Contains("/pullrequests?"))
                return JsonResponse(CreatedPrJson, HttpStatusCode.Created);
            if (req.Method == HttpMethod.Get && url.Contains("/pullrequests/42?"))
                return JsonResponse(ActivePrJson);
            if (req.Method.Method == "PATCH" && url.Contains("/pullrequests/42?"))
                return JsonResponse(CompletedPrJson);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var remote = CreateRemote(git, http, projects: ProjectConfigs());

        UpstreamCompletionOutcome? outcome = null;
        await WithPatAsync(async () => outcome = await remote.CompleteAsync(CompletionRequest()));

        Assert.NotNull(outcome);
        Assert.True(outcome.BranchPushed);
        Assert.Equal(42, outcome.PullRequestNumber);
        Assert.Equal(
            $"https://dev.azure.com/{Org}/{Project}/_git/{Repo}/pullrequest/42",
            outcome.PullRequestUrl);
        Assert.Equal("cccccccccccccccccccccccccccccccccccccccc", outcome.MergedSha);
        Assert.False(outcome.AutoMergeRaced);

        var push = Assert.Single(git.Pushes);
        Assert.Equal("feature/work-1", push.Branch);
        Assert.Contains($"/{Org}/{Project}/_git/{Repo}", push.Url);

        var create = http.Calls.First(c => c.Method == "POST");
        Assert.Contains("refs/heads/feature/work-1", create.Body);
        Assert.Contains("refs/heads/main", create.Body);
        var patch = http.Calls.First(c => c.Method == "PATCH");
        Assert.Contains("noFastForward", patch.Body);
        Assert.Contains("completed", patch.Body);

        // The PR detail read + state mapping round-trips through GetPullRequestAsync.
        var getRemote = CreateRemote(new FakeGitHost(), http, ScopedDefaults());
        UpstreamPullRequestState? state = null;
        await WithPatAsync(async () => state = await getRemote.GetPullRequestAsync(42));
        Assert.NotNull(state);
        Assert.Equal(PullRequestStatus.Open, state.Status);
    }

    [Fact]
    public async Task Complete_WithoutAutoMerge_LeavesPrOpen()
    {
        var git = new FakeGitHost();
        var http = new AdoStubHandler();
        http.Router = (req, _) => req.Method == HttpMethod.Post
            ? JsonResponse(CreatedPrJson, HttpStatusCode.Created)
            : new HttpResponseMessage(HttpStatusCode.NotFound);
        var remote = CreateRemote(git, http, projects: ProjectConfigs());

        UpstreamCompletionOutcome? outcome = null;
        await WithPatAsync(async () => outcome = await remote.CompleteAsync(CompletionRequest(autoMerge: false)));

        Assert.NotNull(outcome);
        Assert.True(outcome.BranchPushed);
        Assert.Equal(42, outcome.PullRequestNumber);
        Assert.Null(outcome.MergedSha);
        Assert.DoesNotContain(http.Calls, c => c.Method == "PATCH");
    }

    [Fact]
    public async Task Complete_ExistingPr_SkipsCreate()
    {
        var git = new FakeGitHost();
        var http = new AdoStubHandler();
        http.Router = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Get && url.Contains("/pullrequests/7?"))
                return JsonResponse(ActivePrJson.Replace("42", "7"));
            if (req.Method.Method == "PATCH" && url.Contains("/pullrequests/7?"))
                return JsonResponse(CompletedPrJson.Replace("42", "7"));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var remote = CreateRemote(git, http, projects: ProjectConfigs());
        var request = CompletionRequest() with { ExistingPullRequestNumber = 7 };

        UpstreamCompletionOutcome? outcome = null;
        await WithPatAsync(async () => outcome = await remote.CompleteAsync(request));

        Assert.NotNull(outcome);
        Assert.Equal(7, outcome.PullRequestNumber);
        Assert.DoesNotContain(http.Calls, c => c.Method == "POST");
        Assert.Contains(http.Calls, c => c.Method == "PATCH");
    }

    [Fact]
    public async Task Complete_PrAlreadyExists_ReturnsPartialWithoutThrowing()
    {
        var git = new FakeGitHost();
        var http = new AdoStubHandler();
        http.Router = (req, _) => req.Method == HttpMethod.Post
            ? new HttpResponseMessage(HttpStatusCode.Conflict)
            : new HttpResponseMessage(HttpStatusCode.NotFound);
        var remote = CreateRemote(git, http, projects: ProjectConfigs());

        UpstreamCompletionOutcome? outcome = null;
        await WithPatAsync(async () => outcome = await remote.CompleteAsync(CompletionRequest()));

        Assert.NotNull(outcome);
        Assert.True(outcome.BranchPushed);
        Assert.Null(outcome.PullRequestNumber);
        Assert.NotNull(outcome.Notes);
        Assert.False(outcome.AutoMergeRaced);
    }

    [Fact]
    public async Task Complete_MergeConflict409_SetsAutoMergeRaced()
    {
        var git = new FakeGitHost();
        var http = new AdoStubHandler();
        http.Router = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post)
                return JsonResponse(CreatedPrJson, HttpStatusCode.Created);
            if (req.Method == HttpMethod.Get)
                return JsonResponse(ActivePrJson);
            return new HttpResponseMessage(HttpStatusCode.Conflict);
        };
        var remote = CreateRemote(git, http, projects: ProjectConfigs());

        UpstreamCompletionOutcome? outcome = null;
        await WithPatAsync(async () => outcome = await remote.CompleteAsync(CompletionRequest()));

        Assert.NotNull(outcome);
        Assert.True(outcome.AutoMergeRaced);
        Assert.Null(outcome.MergedSha);
        Assert.Equal(42, outcome.PullRequestNumber);
        Assert.NotNull(outcome.Notes);
    }

    // -- failure classification: infrastructure throws, soft outcomes return -

    [Fact]
    public async Task Complete_Unauthorized_ThrowsInfrastructure()
    {
        var git = new FakeGitHost();
        var http = new AdoStubHandler();
        http.Router = (_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var remote = CreateRemote(git, http, projects: ProjectConfigs());

        await WithPatAsync(async () =>
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => remote.CompleteAsync(CompletionRequest(autoMerge: false)));
            Assert.Contains("infrastructure", ex.Message);
        });
    }

    [Fact]
    public async Task Complete_ForgeError_ThrowsInfrastructure()
    {
        var git = new FakeGitHost();
        var http = new AdoStubHandler();
        http.Router = (_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var scoped = ScopedDefaults();
        scoped["MaxRetries"] = "0";
        var remote = CreateRemote(git, http, projects: ProjectConfigs(), scoped: scoped);

        // GET paths retry boundedly then throw; force zero retries for speed.
        await WithPatAsync(async () =>
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => remote.GetPullRequestAsync(42));
            Assert.Contains("infrastructure", ex.Message);
        });
    }

    [Fact]
    public async Task Complete_NetworkFailure_ThrowsInfrastructure()
    {
        var git = new FakeGitHost();
        var http = new AdoStubHandler();
        http.Router = (_, _) => throw new HttpRequestException("connection reset");
        var remote = CreateRemote(git, http, projects: ProjectConfigs());

        await WithPatAsync(async () =>
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => remote.CompleteAsync(CompletionRequest(autoMerge: false)));
            Assert.Contains("infrastructure", ex.Message);
        });
    }

    // -- PR reads ------------------------------------------------------------

    [Fact]
    public async Task GetPullRequest_MapsCompletedWithMergeSha()
    {
        var handler = new AdoStubHandler();
        handler.Router = (_, _) => JsonResponse(CompletedPrJson);
        var remote = CreateRemote(new FakeGitHost(), handler, ScopedDefaults());

        Environment.SetEnvironmentVariable(PatEnvVar, TestPat);
        try
        {
            var state = await remote.GetPullRequestAsync(42);
            Assert.NotNull(state);
            Assert.Equal(PullRequestStatus.Merged, state.Status);
            Assert.Equal("cccccccccccccccccccccccccccccccccccccccc", state.MergeCommitSha);
            Assert.EndsWith("/pullrequest/42", state.Url);
        }
        finally { Environment.SetEnvironmentVariable(PatEnvVar, null); }
    }

    [Fact]
    public async Task GetPullRequest_MapsAbandonedToClosed_AndMissingToNull()
    {
        Environment.SetEnvironmentVariable(PatEnvVar, TestPat);
        try
        {
            var handler = new AdoStubHandler();
            handler.Router = (req, _) => req.RequestUri!.ToString().Contains("/pullrequests/43?")
                ? JsonResponse(AbandonedPrJson)
                : new HttpResponseMessage(HttpStatusCode.NotFound);
            var remote = CreateRemote(new FakeGitHost(), handler, ScopedDefaults());

            var closed = await remote.GetPullRequestAsync(43);
            Assert.NotNull(closed);
            Assert.Equal(PullRequestStatus.Closed, closed.Status);
            Assert.Null(closed.MergeCommitSha);

            var missing = await remote.GetPullRequestAsync(999);
            Assert.Null(missing);
        }
        finally { Environment.SetEnvironmentVariable(PatEnvVar, null); }
    }

    [Fact]
    public async Task ListOpen_FiltersPrefix_SkipsUnknownMergeStatus_AndPaginates()
    {
        const string page1 = """
            {
              "count": 3,
              "value": [
                { "pullRequestId": 11, "status": "active", "mergeStatus": "succeeded",
                  "sourceRefName": "refs/heads/codeybox/work-a", "targetRefName": "refs/heads/main",
                  "lastMergeSourceCommit": { "commitId": "1111111111111111111111111111111111111111" } },
                { "pullRequestId": 12, "status": "active", "mergeStatus": "succeeded",
                  "sourceRefName": "refs/heads/other/branch", "targetRefName": "refs/heads/main",
                  "lastMergeSourceCommit": { "commitId": "2222222222222222222222222222222222222222" } },
                { "pullRequestId": 13, "status": "active", "mergeStatus": "notSet",
                  "sourceRefName": "refs/heads/codeybox/work-b", "targetRefName": "refs/heads/main",
                  "lastMergeSourceCommit": { "commitId": "3333333333333333333333333333333333333333" } }
              ]
            }
            """;
        const string page2 = """
            {
              "count": 1,
              "value": [
                { "pullRequestId": 14, "status": "active", "mergeStatus": "conflicts",
                  "sourceRefName": "refs/heads/codeybox/work-c", "targetRefName": "refs/heads/main",
                  "lastMergeSourceCommit": { "commitId": "4444444444444444444444444444444444444444" } }
              ]
            }
            """;
        var handler = new AdoStubHandler();
        handler.Router = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("continuationToken=next-page"))
                return JsonResponse(page2);
            var first = JsonResponse(page1);
            first.Headers.Add("x-ms-continuationtoken", "next-page");
            return first;
        };
        var remote = CreateRemote(new FakeGitHost(), handler, ScopedDefaults());

        Environment.SetEnvironmentVariable(PatEnvVar, TestPat);
        IReadOnlyList<UpstreamPullRequest>? prs = null;
        try { prs = await remote.ListOpenPullRequestsAsync("codeybox/"); }
        finally { Environment.SetEnvironmentVariable(PatEnvVar, null); }

        Assert.NotNull(prs);
        Assert.Equal(2, prs.Count);
        Assert.Equal(11, prs[0].Number);
        Assert.False(prs[0].HasMergeConflict);
        Assert.Equal("codeybox/work-a", prs[0].HeadBranch);
        Assert.Equal(14, prs[1].Number);
        Assert.True(prs[1].HasMergeConflict);
        Assert.Equal(2, handler.Calls.Count);
    }

    // -- reviews -------------------------------------------------------------

    private const string ReviewersJson = """
        {
          "count": 3,
          "value": [
            { "id": "aaa", "displayName": "Alice", "uniqueName": "alice@example.invalid", "vote": 10, "isRequired": true },
            { "id": "bbb", "displayName": "Bob", "uniqueName": "bob@example.invalid", "vote": 0, "isRequired": true },
            { "id": "ccc", "displayName": "Cara", "uniqueName": "cara@example.invalid", "vote": -10, "isRequired": false }
          ]
        }
        """;

    private const string ReviewerPolicyJson = """
        {
          "count": 1,
          "value": [
            { "id": 5, "isEnabled": true, "isBlocking": true,
              "type": { "id": "fa4e907d-c16b-4a4c-9dfa-4916e5d171ab", "displayName": "Minimum number of reviewers" },
              "settings": { "minimumApproverCount": 2, "creatorVoteCounts": false,
                "scope": [{ "refName": "refs/heads/main", "repositoryId": "repo-guid-1" }] } }
          ]
        }
        """;

    private const string RepositoryJson = """
        { "id": "repo-guid-1", "name": "WebApp", "defaultBranch": "refs/heads/main",
          "remoteUrl": "https://dev.azure.com/contoso/Fabrikam/_git/WebApp" }
        """;

    [Fact]
    public async Task GetReviewState_MapsVotes_AndReportsUnmetRequirements()
    {
        var handler = new AdoStubHandler();
        handler.Router = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/reviewers?"))
                return JsonResponse(ReviewersJson);
            if (url.Contains("/git/repositories/WebApp?"))
                return JsonResponse(RepositoryJson);
            if (url.Contains("/policy/configurations?"))
                return JsonResponse(ReviewerPolicyJson);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var remote = CreateRemote(new FakeGitHost(), handler, ScopedDefaults());

        Environment.SetEnvironmentVariable(PatEnvVar, TestPat);
        UpstreamReviewState? state = null;
        try { state = await remote.GetReviewStateAsync(42); }
        finally { Environment.SetEnvironmentVariable(PatEnvVar, null); }

        Assert.NotNull(state);
        Assert.Equal(3, state.Reviews.Count);
        Assert.Contains(state.Reviews, r => r.Reviewer == "Alice" && r.Verdict == UpstreamReviewVerdict.Approved);
        Assert.Contains(state.Reviews, r => r.Reviewer == "Bob" && r.Verdict == UpstreamReviewVerdict.Pending);
        Assert.Contains(state.Reviews, r => r.Reviewer == "Cara" && r.Verdict == UpstreamReviewVerdict.ChangesRequested);
        Assert.Contains("Bob", state.RequiredReviewers);
        Assert.Equal(2, state.RequiredApprovalCount);
        Assert.False(state.RequirementsMet);
    }

    [Fact]
    public async Task GetReviewState_AllApproved_MeetsRequirements()
    {
        const string clean = """
            { "count": 1, "value": [
              { "id": "aaa", "displayName": "Alice", "uniqueName": "a@x.invalid", "vote": 5, "isRequired": true } ] }
            """;
        var handler = new AdoStubHandler();
        handler.Router = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/reviewers?"))
                return JsonResponse(clean);
            if (url.Contains("/policy/configurations?"))
                return JsonResponse("""{ "count": 0, "value": [] }""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var remote = CreateRemote(new FakeGitHost(), handler, ScopedDefaults());

        Environment.SetEnvironmentVariable(PatEnvVar, TestPat);
        UpstreamReviewState? state = null;
        try { state = await remote.GetReviewStateAsync(42); }
        finally { Environment.SetEnvironmentVariable(PatEnvVar, null); }

        Assert.NotNull(state);
        Assert.True(state.RequirementsMet);
        Assert.Empty(state.RequiredReviewers);
        Assert.Equal(0, state.RequiredApprovalCount);
    }

    [Fact]
    public async Task GetReviewState_MissingPr_ReturnsNull()
    {
        var handler = new AdoStubHandler();
        var remote = CreateRemote(new FakeGitHost(), handler, ScopedDefaults());
        Environment.SetEnvironmentVariable(PatEnvVar, TestPat);
        try
        {
            Assert.Null(await remote.GetReviewStateAsync(404));
        }
        finally { Environment.SetEnvironmentVariable(PatEnvVar, null); }
    }

    // -- checks --------------------------------------------------------------

    private const string CommitStatusesJson = """
        {
          "count": 3,
          "value": [
            { "id": 1, "state": "succeeded", "description": "Build passed",
              "context": { "name": "ci/build", "genre": "continuous-integration" },
              "targetUrl": "https://dev.azure.com/contoso/_build/results?buildId=1" },
            { "id": 2, "state": "failed", "description": "Lint failed",
              "context": { "name": "ci/lint", "genre": "continuous-integration" } },
            { "id": 3, "state": "pending", "description": "Waiting",
              "context": { "name": "ci/e2e", "genre": "continuous-integration" } }
          ]
        }
        """;

    private const string BuildsJson = """
        {
          "count": 1,
          "value": [
            { "id": 99, "buildNumber": "20260921.3", "status": "completed", "result": "succeeded",
              "definition": { "id": 7, "name": "WebApp-CI" },
              "_links": { "web": { "href": "https://dev.azure.com/contoso/_build/results?buildId=99" } } }
          ]
        }
        """;

    [Fact]
    public async Task GetCheckResults_MapsStatusesAndBuilds()
    {
        var handler = new AdoStubHandler();
        handler.Router = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/commits/") && url.Contains("/statuses?"))
                return JsonResponse(CommitStatusesJson);
            if (url.Contains("/git/repositories/WebApp?"))
                return JsonResponse(RepositoryJson);
            if (url.Contains("/_apis/build/builds?"))
                return JsonResponse(BuildsJson);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var remote = CreateRemote(new FakeGitHost(), handler, ScopedDefaults());
        const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        Environment.SetEnvironmentVariable(PatEnvVar, TestPat);
        UpstreamCheckSummary? summary = null;
        try { summary = await remote.GetCheckResultsAsync(sha); }
        finally { Environment.SetEnvironmentVariable(PatEnvVar, null); }

        Assert.NotNull(summary);
        Assert.Equal(4, summary.Checks.Count);
        Assert.Contains(summary.Checks, c => c.Name == "ci/build" && c.State == UpstreamCheckState.Passing);
        Assert.Contains(summary.Checks, c => c.Name == "ci/lint" && c.State == UpstreamCheckState.Failing);
        Assert.Contains(summary.Checks, c => c.Name == "ci/e2e" && c.State == UpstreamCheckState.Pending);
        Assert.Contains(summary.Checks, c => c.Name == "WebApp-CI" && c.State == UpstreamCheckState.Passing);
        Assert.False(summary.RequiredChecksPassed);
    }

    [Fact]
    public async Task GetCheckResults_SupportedButEmpty_ReturnsEmptyNotNull()
    {
        var handler = new AdoStubHandler();
        handler.Router = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/statuses?"))
                return JsonResponse("""{ "count": 0, "value": [] }""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var remote = CreateRemote(new FakeGitHost(), handler, ScopedDefaults());

        Environment.SetEnvironmentVariable(PatEnvVar, TestPat);
        UpstreamCheckSummary? summary = null;
        try { summary = await remote.GetCheckResultsAsync("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"); }
        finally { Environment.SetEnvironmentVariable(PatEnvVar, null); }

        Assert.NotNull(summary);
        Assert.Empty(summary.Checks);
        Assert.True(summary.RequiredChecksPassed);
    }

    // -- comments ------------------------------------------------------------

    private const string ThreadsJson = """
        {
          "count": 3,
          "value": [
            { "id": 10, "status": "active",
              "threadContext": null,
              "comments": [
                { "id": 1, "content": "Looks good.", "author": { "displayName": "Alice" },
                  "publishedDate": "2026-09-20T10:00:00Z" } ] },
            { "id": 11, "status": "active",
              "threadContext": { "filePath": "/src/app.cs",
                "rightFileStart": { "line": 42 }, "rightFileEnd": { "line": 42 } },
              "comments": [
                { "id": 2, "content": "Nit: rename this.", "author": { "displayName": "Bob" },
                  "publishedDate": "2026-09-20T11:00:00Z" } ] },
            { "id": 12, "status": "deleted", "threadContext": null,
              "comments": [ { "id": 3, "content": "gone", "author": { "displayName": "X" } } ] }
          ]
        }
        """;

    [Fact]
    public async Task ListComments_FlattensThreads_AndSkipsDeleted()
    {
        var handler = new AdoStubHandler();
        handler.Router = (_, _) => JsonResponse(ThreadsJson);
        var remote = CreateRemote(new FakeGitHost(), handler, ScopedDefaults());

        Environment.SetEnvironmentVariable(PatEnvVar, TestPat);
        IReadOnlyList<UpstreamComment>? comments = null;
        try { comments = await remote.ListCommentsAsync(42); }
        finally { Environment.SetEnvironmentVariable(PatEnvVar, null); }

        Assert.NotNull(comments);
        Assert.Equal(2, comments.Count);
        Assert.Equal("10/1", comments[0].Id);
        Assert.Equal("Alice", comments[0].Author);
        Assert.Null(comments[0].FilePath);
        Assert.Equal("11/2", comments[1].Id);
        Assert.Equal("src/app.cs", comments[1].FilePath);
        Assert.Equal(42, comments[1].Line);
    }

    [Fact]
    public async Task PostComment_CreatesThread_AndReplyTargetsThread()
    {
        var handler = new AdoStubHandler();
        handler.Router = (req, body) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && url.Contains("/threads/10/comments?"))
                return JsonResponse(
                    """{ "id": 9, "content": "reply", "author": { "displayName": "Me" } }""",
                    HttpStatusCode.Created);
            if (req.Method == HttpMethod.Post && url.Contains("/threads?"))
                return JsonResponse(
                    """{ "id": 20, "status": "active", "threadContext": { "filePath": "/src/app.cs", "rightFileStart": { "line": 7 } }, "comments": [{ "id": 4, "content": "new thread", "author": { "displayName": "Me" } }] }""",
                    HttpStatusCode.Created);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var remote = CreateRemote(new FakeGitHost(), handler, ScopedDefaults());

        Environment.SetEnvironmentVariable(PatEnvVar, TestPat);
        try
        {
            var created = await remote.PostCommentAsync(
                42, new NewUpstreamComment("new thread", "src/app.cs", 7));
            Assert.NotNull(created);
            Assert.Equal("20/4", created.Id);
            Assert.Equal("src/app.cs", created.FilePath);
            Assert.Equal(7, created.Line);

            var reply = await remote.PostCommentAsync(
                42, new NewUpstreamComment("reply", replyToId: "10/1"));
            Assert.NotNull(reply);
            Assert.Equal("10/9", reply.Id);
        }
        finally { Environment.SetEnvironmentVariable(PatEnvVar, null); }
    }

    // -- webhooks / service hooks --------------------------------------------

    private const string SubscriptionsJson = """
        {
          "count": 1,
          "value": [
            { "id": "sub-guid-1", "status": "enabled", "publisherId": "tfs",
              "eventType": "git.pullrequest.created", "resourceVersion": "1.0",
              "consumerId": "webHooks", "consumerActionId": "httpRequest",
              "publisherInputs": { "projectId": "proj-guid" },
              "consumerInputs": { "url": "https://hooks.example.invalid/ado" } }
          ]
        }
        """;

    [Fact]
    public async Task Webhooks_UnsupportedScopeReturnsNull_WhileEmptyListIsDistinct()
    {
        var handler = new AdoStubHandler();
        handler.Router = (_, _) => JsonResponse("""{ "count": 0, "value": [] }""");
        var remote = CreateRemote(new FakeGitHost(), handler, ScopedDefaults());

        Environment.SetEnvironmentVariable(PatEnvVar, TestPat);
        try
        {
            // "repository" has no native Azure DevOps equivalent: unsupported -> null.
            var unsupported = await remote.CreateWebhookSubscriptionAsync(
                new NewUpstreamWebhookSubscription(
                    ["git.pullrequest.created"], "https://hooks.example.invalid/ado", "repository"));
            Assert.Null(unsupported);

            // Supported scope with no subscriptions: empty list, not null.
            var empty = await remote.ListWebhookSubscriptionsAsync();
            Assert.NotNull(empty);
            Assert.Empty(empty);
        }
        finally { Environment.SetEnvironmentVariable(PatEnvVar, null); }
    }

    [Fact]
    public async Task Webhooks_CreateListDelete_RoundTrip()
    {
        var handler = new AdoStubHandler();
        handler.Router = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && url.Contains("/hooks/subscriptions?"))
                return JsonResponse(
                    """{ "id": "sub-guid-9", "eventType": "git.pullrequest.created", "consumerInputs": { "url": "https://hooks.example.invalid/ado" } }""",
                    HttpStatusCode.Created);
            if (req.Method == HttpMethod.Get && url.Contains("/hooks/subscriptions?"))
                return JsonResponse(SubscriptionsJson);
            if (req.Method == HttpMethod.Delete && url.Contains("/hooks/subscriptions/sub-guid-1?"))
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (req.Method == HttpMethod.Delete)
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var remote = CreateRemote(new FakeGitHost(), handler, ScopedDefaults());

        Environment.SetEnvironmentVariable(PatEnvVar, TestPat);
        try
        {
            var created = await remote.CreateWebhookSubscriptionAsync(
                new NewUpstreamWebhookSubscription(
                    ["git.pullrequest.created"], "https://hooks.example.invalid/ado", "project"));
            Assert.NotNull(created);
            Assert.Equal("sub-guid-9", created.Id);
            Assert.Equal("project", created.Scope);
            Assert.Equal(["git.pullrequest.created"], created.Events);

            var listed = await remote.ListWebhookSubscriptionsAsync();
            Assert.NotNull(listed);
            var sub = Assert.Single(listed);
            Assert.Equal("sub-guid-1", sub.Id);
            Assert.Equal("https://hooks.example.invalid/ado", sub.TargetUrl);

            Assert.True(await remote.DeleteWebhookSubscriptionAsync("sub-guid-1"));
            Assert.False(await remote.DeleteWebhookSubscriptionAsync("no-such-sub"));
        }
        finally { Environment.SetEnvironmentVariable(PatEnvVar, null); }
    }

    // -- metadata ------------------------------------------------------------

    private const string ProjectJson = """{ "id": "proj-guid", "name": "Fabrikam", "visibility": "private" }""";

    private const string PoliciesJson = """
        {
          "count": 2,
          "value": [
            { "id": 5, "isEnabled": true, "isBlocking": true,
              "type": { "id": "fa4e907d-c16b-4a4c-9dfa-4916e5d171ab", "displayName": "Minimum number of reviewers" },
              "settings": { "minimumApproverCount": 2, "creatorVoteCounts": false,
                "scope": [{ "refName": "refs/heads/main", "repositoryId": "repo-guid-1" }] } },
            { "id": 6, "isEnabled": true, "isBlocking": true,
              "type": { "id": "0609b279-4645-4f57-8c96-0c9a1518102f", "displayName": "Build" },
              "settings": { "minimumApproverCount": 0, "creatorVoteCounts": false,
                "scope": [{ "refName": "refs/heads/main", "repositoryId": "repo-guid-1" }] } }
          ]
        }
        """;

    [Fact]
    public async Task GetRepositoryMetadata_MapsDefaultsVisibilityAndPolicies()
    {
        var handler = new AdoStubHandler();
        handler.Router = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/git/repositories/WebApp?"))
                return JsonResponse(RepositoryJson);
            if (url.Contains("/_apis/projects/Fabrikam?"))
                return JsonResponse(ProjectJson);
            if (url.Contains("/policy/configurations?"))
                return JsonResponse(PoliciesJson);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var remote = CreateRemote(new FakeGitHost(), handler, ScopedDefaults());

        Environment.SetEnvironmentVariable(PatEnvVar, TestPat);
        UpstreamRepositoryMetadata? metadata = null;
        try { metadata = await remote.GetRepositoryMetadataAsync(); }
        finally { Environment.SetEnvironmentVariable(PatEnvVar, null); }

        Assert.NotNull(metadata);
        Assert.Equal("main", metadata.DefaultBranch);
        Assert.Equal(UpstreamRepositoryVisibility.Private, metadata.Visibility);
        var protection = Assert.Single(metadata.BranchProtections);
        Assert.Equal("main", protection.BranchPattern);
        Assert.Equal(2, protection.RequiredApprovalCount);
        Assert.True(protection.RequiresStatusChecks);
    }

    // -- unsupported stays unsupported ---------------------------------------

    [Fact]
    public async Task CreateTagAndRelease_IsUnsupported_ReturnsNull()
    {
        // Azure DevOps has no PR-linked forge release object; the contract
        // default (null) must stand rather than a forced translation.
        var remote = CreateRemote(new FakeGitHost(), new AdoStubHandler(), ScopedDefaults());
        Assert.Null(await ((IUpstreamRemote)remote).CreateTagAndReleaseAsync("v1.0", "abc123", null));
    }

    // -- credentials ---------------------------------------------------------

    [Fact]
    public async Task Credentials_ComeFromEnvVarOnly_ConfigValuesAreIgnored()
    {
        var git = new FakeGitHost();
        var handler = new AdoStubHandler();
        handler.Router = (req, _) => req.Method == HttpMethod.Post
            ? JsonResponse(CreatedPrJson, HttpStatusCode.Created)
            : new HttpResponseMessage(HttpStatusCode.NotFound);
        var projects = ProjectConfigs(new Dictionary<string, string>
        {
            // Decoys: must never be used as credentials.
            ["Token"] = "decoy-from-config",
            ["Pat"] = "decoy-from-config",
            ["Password"] = "decoy-from-config",
        });
        var remote = CreateRemote(git, handler, projects: projects);

        await WithPatAsync(async () =>
            await remote.CompleteAsync(CompletionRequest(autoMerge: false)));

        Assert.NotEmpty(handler.AuthParams);
        foreach (var index in Enumerable.Range(0, handler.AuthParams.Count))
        {
            var decoded = DecodeBasic(handler, index);
            Assert.Equal(":" + TestPat, decoded);
            Assert.DoesNotContain("decoy", decoded);
        }
        var create = handler.Calls.First(c => c.Method == "POST");
        Assert.DoesNotContain("decoy", create.Body);
    }

    [Fact]
    public void Credentials_PluginHoldsNoSandboxDependencies()
    {
        // The upstream remote must be constructible without any sandbox or
        // credential-provider service: credentials stay on the host.
        foreach (var ctor in typeof(AzureDevOpsUpstreamRemote).GetConstructors())
        {
            foreach (var param in ctor.GetParameters())
            {
                var name = param.ParameterType.FullName ?? param.ParameterType.Name;
                Assert.DoesNotContain("Sandbox", name);
                Assert.DoesNotContain("CredentialProvider", name);
            }
        }
    }

    [Fact]
    public async Task PushAndFetch_UseHostGit_WithoutSandboxAccess()
    {
        // FakeGitHost.GetSandboxAccess throws; completing push/fetch against
        // it proves the plugin never routes credentials through a sandbox.
        var git = new FakeGitHost { FetchUpstreamShaToReturn = "deadbeef" };
        var handler = new AdoStubHandler();
        var remote = CreateRemote(git, handler, ScopedDefaults());

        await WithPatAsync(async () =>
        {
            var push = await remote.PushAsync("repo-id", "feature/work-1");
            Assert.True(push.Success);

            var sha = await remote.FetchBaseBranchAsync("repo-id", "main");
            Assert.Equal("deadbeef", sha);
        });

        Assert.Single(git.Pushes);
        Assert.Single(git.Fetches);
        Assert.DoesNotContain(git.Pushes[0].Url, TestPat);
        Assert.DoesNotContain(git.Fetches[0].Url, TestPat);
    }

    [Fact]
    public async Task Push_WithoutConfiguration_ReturnsFailureInsteadOfThrowing()
    {
        var remote = CreateRemote(new FakeGitHost(), new AdoStubHandler());
        var result = await remote.PushAsync("repo-id", "feature/work-1");
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Complete_OptionLikeBranch_IsRejected()
    {
        var remote = CreateRemote(new FakeGitHost(), new AdoStubHandler(), projects: ProjectConfigs());
        var request = CompletionRequest() with { WorkBranch = "--upload-pack=evil" };
        await WithPatAsync(async () =>
            await Assert.ThrowsAsync<ArgumentException>(() => remote.CompleteAsync(request)));
    }

    [Fact]
    public async Task Webhooks_MultiEventFailure_RollsBackCreatedSubscriptions()
    {
        var handler = new AdoStubHandler();
        handler.Router = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && url.Contains("/hooks/subscriptions?"))
            {
                var created = handler.Calls.Count(c => c.Method == "POST");
                return created == 1
                    ? JsonResponse("""{ "id": "sub-orphan", "eventType": "git.pullrequest.created" }""",
                        HttpStatusCode.Created)
                    : new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }
            if (req.Method == HttpMethod.Delete && url.Contains("/hooks/subscriptions/sub-orphan?"))
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var remote = CreateRemote(new FakeGitHost(), handler, ScopedDefaults());

        Environment.SetEnvironmentVariable(PatEnvVar, TestPat);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                remote.CreateWebhookSubscriptionAsync(new NewUpstreamWebhookSubscription(
                    ["git.pullrequest.created", "git.push"], "https://hooks.example.invalid/ado", "project")));
            Assert.Contains(handler.Calls, c => c.Method == "DELETE" && c.Url.Contains("sub-orphan"));
        }
        finally { Environment.SetEnvironmentVariable(PatEnvVar, null); }
    }
}
