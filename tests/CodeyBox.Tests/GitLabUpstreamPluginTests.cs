using System.Net;
using System.Reflection;
using System.Text;
using CodeyBox.Core;
using CodeyBox.GitLabUpstreamPlugin;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the GitLab upstream remote (<c>codeybox.gitlab-upstream</c>).
/// All forge traffic goes through a queued <see cref="HttpMessageHandler"/>;
/// recorded-shape fixtures under <c>Fixtures/GitLab</c> were transcribed
/// from the published GitLab REST API v4 object shapes (merge requests,
/// approvals, pipelines, jobs, commit statuses, notes, hooks, projects)
/// because no live instance exists in CI — see the plugin README for the
/// documented reason.
/// </summary>
public sealed class GitLabUpstreamPluginTests : IDisposable
{
    private const string TokenEnvVar = "CODEYBOX_GITLAB_TEST_TOKEN";
    private const string TokenValue = "gitlab-test-token-not-a-secret";

    private static readonly ProjectId ProjectId = new("gitlab-project");

    private static readonly UpstreamCompletionRequest SampleRequest = new()
    {
        RepositoryId = "repo-id",
        WorkItemId = new WorkItemId(Guid.Parse("00000000-0000-0000-0000-000000000009")),
        ProjectId = ProjectId,
        WorkBranch = "codeybox/abc123",
        BaseBranch = "main",
        MergeSha = "deadbeef",
        Title = "Add feature Z",
        Description = "Automated via CodeyBox",
        TokenEnvVar = TokenEnvVar,
    };

    public GitLabUpstreamPluginTests()
        => Environment.SetEnvironmentVariable(TokenEnvVar, TokenValue);

    public void Dispose()
        => Environment.SetEnvironmentVariable(TokenEnvVar, null);

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "GitLab", name));

    // ------------------------------------------------------------------
    // Full lifecycle: push, open MR, read state, merge
    // ------------------------------------------------------------------

    [Fact]
    public async Task CompleteAsync_FullLifecycle_PushesOpensReadsAndMerges()
    {
        var git = new GitLabFakeGitHost();
        var handler = new GitLabFakeHandler();
        // create MR, merge (accept), re-read merged MR
        handler.EnqueueJson(Fixture("mr-open.json"), HttpStatusCode.Created);
        handler.EnqueueJson("""{"id":1001,"iid":7,"state":"opened"}""", HttpStatusCode.Accepted);
        handler.EnqueueJson(Fixture("mr-merged.json"));
        var remote = BuildRemote(git, handler, ProjectConfig());

        var outcome = await remote.CompleteAsync(SampleRequest with { AutoMerge = true });

        Assert.True(outcome.BranchPushed);
        Assert.Equal("https://gitlab.example.com/myteam/myproject/-/merge_requests/7", outcome.PullRequestUrl);
        Assert.Equal(7, outcome.PullRequestNumber);
        Assert.Equal("cafef00dcafef00dcafef00dcafef00dcafef00d", outcome.MergedSha);
        Assert.Null(outcome.Notes);
        Assert.False(outcome.AutoMergeRaced);

        // Push went to the git clone URL derived from the API base …
        var push = Assert.Single(git.Pushes);
        Assert.Equal("https://gitlab.example.com/myteam/myproject.git", push.Url);
        Assert.Equal("codeybox/abc123", push.Branch);
        // … with host-side askpass auth (never on argv) …
        Assert.True(push.Env.ContainsKey("GIT_ASKPASS"));
        // … and every API call carried the token header.
        Assert.NotEmpty(handler.Requests);
        foreach (var request in handler.Requests)
            Assert.Equal(TokenValue, request.Headers.GetValues("PRIVATE-TOKEN").Single());
        Assert.DoesNotContain(TokenValue, outcome.Notes ?? string.Empty);
    }

    [Fact]
    public async Task CompleteAsync_ExistingPullRequestNumber_SkipsCreate()
    {
        var git = new GitLabFakeGitHost();
        var handler = new GitLabFakeHandler();
        handler.EnqueueJson(Fixture("mr-open.json"));
        handler.EnqueueJson("""{"id":1001,"iid":7,"state":"opened"}""", HttpStatusCode.Accepted);
        handler.EnqueueJson(Fixture("mr-merged.json"));
        var remote = BuildRemote(git, handler, ProjectConfig());

        var outcome = await remote.CompleteAsync(SampleRequest with
        {
            AutoMerge = true,
            ExistingPullRequestNumber = 7,
        });

        Assert.Equal(7, outcome.PullRequestNumber);
        Assert.Equal("cafef00dcafef00dcafef00dcafef00dcafef00d", outcome.MergedSha);
        Assert.DoesNotContain(handler.Requests, r =>
            r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/merge_requests"));
    }

    [Fact]
    public async Task CompleteAsync_MergeBlocked_ReturnsPartialResult()
    {
        var git = new GitLabFakeGitHost();
        var handler = new GitLabFakeHandler();
        handler.EnqueueJson(Fixture("mr-open.json"), HttpStatusCode.Created);
        handler.EnqueueJson(
            """{"message":"Conflicts detected during merge. Please resolve them and try again."}""",
            HttpStatusCode.Conflict);
        var remote = BuildRemote(git, handler, ProjectConfig());

        var outcome = await remote.CompleteAsync(SampleRequest with { AutoMerge = true });

        Assert.True(outcome.BranchPushed);
        Assert.Equal(7, outcome.PullRequestNumber);
        Assert.Null(outcome.MergedSha);
        Assert.NotNull(outcome.Notes);
        Assert.False(outcome.AutoMergeRaced);
        Assert.DoesNotContain(TokenValue, outcome.Notes);
    }

    [Fact]
    public async Task CompleteAsync_MergeRaced_ReturnsRaceSignal()
    {
        var git = new GitLabFakeGitHost();
        var handler = new GitLabFakeHandler();
        handler.EnqueueJson(Fixture("mr-open.json"), HttpStatusCode.Created);
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
        var remote = BuildRemote(git, handler, ProjectConfig());

        var outcome = await remote.CompleteAsync(SampleRequest with { AutoMerge = true });

        Assert.True(outcome.BranchPushed);
        Assert.Equal(7, outcome.PullRequestNumber);
        Assert.Null(outcome.MergedSha);
        Assert.True(outcome.AutoMergeRaced);
    }

    [Fact]
    public async Task GetPullRequest_Merged_MapsStatusAndSha()
    {
        var handler = new GitLabFakeHandler();
        handler.EnqueueJson(Fixture("mr-merged.json"));
        var remote = BuildRemote(new GitLabFakeGitHost(), handler, scoped: ScopedConfig());

        var state = await remote.GetPullRequestAsync(7);

        Assert.NotNull(state);
        Assert.Equal(PullRequestStatus.Merged, state.Status);
        Assert.Equal("cafef00dcafef00dcafef00dcafef00dcafef00d", state.MergeCommitSha);
    }

    [Fact]
    public async Task PushAsync_ScopedProject_PushesAndReturnsSuccess()
    {
        var git = new GitLabFakeGitHost();
        var remote = BuildRemote(git, new GitLabFakeHandler(), scoped: ScopedConfig());

        var result = await remote.PushAsync("repo-id", "codeybox/abc123");

        Assert.True(result.Success);
        var push = Assert.Single(git.Pushes);
        Assert.Equal("https://gitlab.example.com/myteam/myproject.git", push.Url);
    }

    [Fact]
    public async Task PushAsync_Unconfigured_ReturnsFailure()
    {
        var remote = BuildRemote(
            new GitLabFakeGitHost(), new GitLabFakeHandler(), scoped: EmptyScopedConfig());

        var result = await remote.PushAsync("repo-id", "main");

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    // ------------------------------------------------------------------
    // Unsupported is distinguishable from empty
    // ------------------------------------------------------------------

    [Fact]
    public async Task Comments_PlainAndAnchoredSupported_SystemFiltered()
    {
        var handler = new GitLabFakeHandler();
        handler.EnqueueJson(Fixture("notes.json"));
        var remote = BuildRemote(new GitLabFakeGitHost(), handler, scoped: ScopedConfig());

        var comments = await remote.ListCommentsAsync(7);

        Assert.NotNull(comments);
        // The system note ("added 1 commit") is filtered out.
        Assert.Equal(2, comments.Count);
        Assert.Equal("reviewer-ada", comments[0].Author);
        Assert.Null(comments[0].FilePath);
        Assert.Equal("src/widget.cs", comments[1].FilePath);
        Assert.Equal(42, comments[1].Line);
    }

    [Fact]
    public async Task PostComment_PlainPostsNote()
    {
        var handler = new GitLabFakeHandler();
        handler.EnqueueJson(Fixture("note-created.json"), HttpStatusCode.Created);
        var remote = BuildRemote(new GitLabFakeGitHost(), handler, scoped: ScopedConfig());

        var posted = await remote.PostCommentAsync(7, new NewUpstreamComment("Acknowledged."));

        Assert.NotNull(posted);
        Assert.Equal("304", posted.Id);
        Assert.Single(handler.Requests);
        Assert.EndsWith("/notes", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task PostComment_ReplyPostsIntoDiscussion()
    {
        var handler = new GitLabFakeHandler();
        handler.EnqueueJson(Fixture("note-created.json"), HttpStatusCode.Created);
        var remote = BuildRemote(new GitLabFakeGitHost(), handler, scoped: ScopedConfig());

        var posted = await remote.PostCommentAsync(7, new NewUpstreamComment("Acknowledged.", replyToId: "aaa111"));

        Assert.NotNull(posted);
        Assert.Equal("304", posted.Id);
        Assert.Contains("/discussions/aaa111/notes", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task PostComment_AnchoredCreatesDiscussion()
    {
        var handler = new GitLabFakeHandler();
        handler.EnqueueJson(Fixture("mr-open.json"));
        handler.EnqueueJson(Fixture("discussion-created.json"), HttpStatusCode.Created);
        var remote = BuildRemote(new GitLabFakeGitHost(), handler, scoped: ScopedConfig());

        var posted = await remote.PostCommentAsync(7, new NewUpstreamComment("Consider a guard.", "src/widget.cs", 7));

        Assert.NotNull(posted);
        Assert.Equal("305", posted.Id);
        Assert.Equal("src/widget.cs", posted.FilePath);
        Assert.Equal(7, posted.Line);
        Assert.Equal(2, handler.Requests.Count);
        Assert.EndsWith("/discussions", handler.Requests[1].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Webhooks_UserScopeUnsupported_EmptyListIsEmpty()
    {
        var handler = new GitLabFakeHandler();
        handler.EnqueueJson("[]");
        var remote = BuildRemote(new GitLabFakeGitHost(), handler, scoped: ScopedConfig());

        var subs = await remote.ListWebhookSubscriptionsAsync();

        Assert.NotNull(subs);
        Assert.Empty(subs);

        Assert.Null(await remote.ListWebhookSubscriptionsAsync("user"));
        Assert.Null(await remote.ListWebhookSubscriptionsAsync("nonsense"));
        Assert.Null(await remote.CreateWebhookSubscriptionAsync(
            new NewUpstreamWebhookSubscription(["push"], "https://hooks.example.com/x", "user")));
        Assert.Null(await remote.CreateWebhookSubscriptionAsync(
            new NewUpstreamWebhookSubscription(["push"], "https://hooks.example.com/x", "nonsense")));
        Assert.Null(await remote.CreateWebhookSubscriptionAsync(
            new NewUpstreamWebhookSubscription(["not-a-gitlab-event"], "https://hooks.example.com/x")));
        // Only the repository-scoped list call went out; unsupported
        // shapes never hit the forge.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task UnconfiguredPlugin_ReadSurfacesReturnUnsupported()
    {
        var remote = BuildRemote(new GitLabFakeGitHost(), new GitLabFakeHandler(), scoped: EmptyScopedConfig());

        Assert.Null(await remote.GetPullRequestAsync(7));
        Assert.Null(await remote.GetReviewStateAsync(7));
        Assert.Null(await remote.GetCheckResultsAsync("abc123"));
        Assert.Null(await remote.ListCommentsAsync(7));
        Assert.Null(await remote.PostCommentAsync(7, new NewUpstreamComment("hi")));
        Assert.Null(await remote.ListWebhookSubscriptionsAsync());
        Assert.Null(await remote.CreateWebhookSubscriptionAsync(
            new NewUpstreamWebhookSubscription(["push"], "https://hooks.example.com/x")));
        Assert.Null(await remote.DeleteWebhookSubscriptionAsync("1"));
        Assert.Null(await remote.GetRepositoryMetadataAsync());
        Assert.Empty(await remote.ListOpenPullRequestsAsync("codeybox/"));
        Assert.Null(await remote.FetchBaseBranchAsync("repo-id", "main"));
    }

    // ------------------------------------------------------------------
    // Failure classification: infra throws, soft returns partial
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task CompleteAsync_ForgeRejects_CreateThrowsInfrastructure(HttpStatusCode status)
    {
        var handler = new GitLabFakeHandler();
        handler.Enqueue(new HttpResponseMessage(status));
        var remote = BuildRemote(new GitLabFakeGitHost(), handler, ProjectConfig());

        var ex = await Assert.ThrowsAsync<GitLabUpstreamException>(() =>
            remote.CompleteAsync(SampleRequest));
        Assert.DoesNotContain(TokenValue, ex.Message);
    }

    [Fact]
    public async Task CompleteAsync_RateLimited_ThrowsWithRetryAfter()
    {
        var handler = new GitLabFakeHandler();
        var limited = new HttpResponseMessage((HttpStatusCode)429);
        limited.Headers.TryAddWithoutValidation("Retry-After", "120");
        handler.Enqueue(limited);
        var remote = BuildRemote(new GitLabFakeGitHost(), handler, ProjectConfig());

        var ex = await Assert.ThrowsAsync<GitLabUpstreamException>(() =>
            remote.CompleteAsync(SampleRequest));
        Assert.Equal(120, ex.RetryAfterSeconds);
    }

    [Fact]
    public async Task CompleteAsync_ForgeUnreachable_ThrowsInfrastructure()
    {
        var handler = new GitLabFakeHandler();
        handler.EnqueueException(new HttpRequestException("connection refused"));
        var remote = BuildRemote(new GitLabFakeGitHost(), handler, ProjectConfig());

        await Assert.ThrowsAsync<GitLabUpstreamException>(() =>
            remote.CompleteAsync(SampleRequest));
    }

    [Fact]
    public async Task CompleteAsync_MrAlreadyExists_ReturnsPartialResult()
    {
        var git = new GitLabFakeGitHost();
        var handler = new GitLabFakeHandler();
        handler.EnqueueJson(Fixture("mr-already-exists.json"), HttpStatusCode.Conflict);
        var remote = BuildRemote(git, handler, ProjectConfig());

        var outcome = await remote.CompleteAsync(SampleRequest);

        Assert.True(outcome.BranchPushed);
        Assert.Null(outcome.PullRequestUrl);
        Assert.Null(outcome.PullRequestNumber);
        Assert.NotNull(outcome.Notes);
        Assert.Single(git.Pushes);
    }

    [Fact]
    public async Task CompleteAsync_ConflictWithoutAlreadyExists_Throws()
    {
        var handler = new GitLabFakeHandler();
        handler.EnqueueJson("""{"message":"Some other conflict"}""", HttpStatusCode.Conflict);
        var remote = BuildRemote(new GitLabFakeGitHost(), handler, ProjectConfig());

        await Assert.ThrowsAsync<GitLabUpstreamException>(() =>
            remote.CompleteAsync(SampleRequest));
    }

    [Fact]
    public async Task CompleteAsync_PushFails_ThrowsBeforeAnyApiCall()
    {
        var git = new GitLabFakeGitHost { PushException = new InvalidOperationException("git push failed") };
        var handler = new GitLabFakeHandler();
        var remote = BuildRemote(git, handler, ProjectConfig());

        await Assert.ThrowsAsync<GitLabUpstreamException>(() =>
            remote.CompleteAsync(SampleRequest));
        Assert.Empty(handler.Requests);
    }

    // ------------------------------------------------------------------
    // Extended surfaces against recorded shapes
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetReviewState_MapsApprovalsQuorumAndRules()
    {
        var handler = new GitLabFakeHandler();
        handler.EnqueueJson(Fixture("approvals.json"));
        handler.EnqueueJson(Fixture("approval-state.json"));
        var remote = BuildRemote(new GitLabFakeGitHost(), handler, scoped: ScopedConfig());

        var state = await remote.GetReviewStateAsync(7);

        Assert.NotNull(state);
        // One approval recorded; the satisfied "default" rule drops out.
        Assert.Equal(UpstreamReviewVerdict.Approved, Assert.Single(state.Reviews).Verdict);
        Assert.Equal("approver-bob", state.Reviews[0].Reviewer);
        Assert.Equal(2, state.RequiredApprovalCount);
        Assert.Equal(["security"], state.RequiredReviewers);
        // The forge itself reports approved=false (one approval < quorum of two).
        Assert.False(state.RequirementsMet);
    }

    [Fact]
    public async Task GetReviewState_OldInstanceWithoutApprovalState_FallsBackToRules()
    {
        var handler = new GitLabFakeHandler();
        handler.EnqueueJson(Fixture("approvals.json"));
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        handler.EnqueueJson(Fixture("approval-rules.json"));
        var remote = BuildRemote(new GitLabFakeGitHost(), handler, scoped: ScopedConfig());

        var state = await remote.GetReviewStateAsync(7);

        Assert.NotNull(state);
        Assert.Equal(["security"], state.RequiredReviewers);
        Assert.False(state.RequirementsMet);
    }

    [Fact]
    public async Task GetReviewState_UnknownMr_ReturnsNull()
    {
        var handler = new GitLabFakeHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        var remote = BuildRemote(new GitLabFakeGitHost(), handler, scoped: ScopedConfig());

        Assert.Null(await remote.GetReviewStateAsync(4242));
    }

    [Fact]
    public async Task GetCheckResults_MapsPipelinesJobsAndStatuses()
    {
        var handler = new GitLabFakeHandler();
        handler.EnqueueJson(Fixture("commit-statuses.json"));
        handler.EnqueueJson(Fixture("pipelines.json"));
        handler.EnqueueJson(Fixture("pipeline-jobs.json"));
        var remote = BuildRemote(new GitLabFakeGitHost(), handler, scoped: ScopedConfig());

        var summary = await remote.GetCheckResultsAsync("9f2c4a1b8d3e5f60718293a4b5c6d7e8f90a1b2c");

        Assert.NotNull(summary);
        Assert.Equal(3, summary.Checks.Count);
        Assert.Equal(UpstreamCheckState.Passing, summary.Checks[0].State);
        Assert.Equal("coverage", summary.Checks[0].Name);
        Assert.Equal(UpstreamCheckState.Passing, summary.Checks[1].State);
        Assert.Equal(UpstreamCheckState.Pending, summary.Checks[2].State);
        // The latest pipeline is still running: required checks not satisfied.
        Assert.False(summary.RequiredChecksPassed);
    }

    [Fact]
    public async Task GetCheckResults_SuccessfulPipeline_PassesRequired()
    {
        var handler = new GitLabFakeHandler();
        handler.EnqueueJson("[]");
        handler.EnqueueJson(
            """[{"id":47,"iid":12,"project_id":4,"sha":"abc","ref":"main","status":"success","web_url":"https://gitlab.example.com/x/-/pipelines/47"}]""");
        handler.EnqueueJson("[]");
        var remote = BuildRemote(new GitLabFakeGitHost(), handler, scoped: ScopedConfig());

        var summary = await remote.GetCheckResultsAsync("abc123");

        Assert.NotNull(summary);
        Assert.True(summary.RequiredChecksPassed);
    }

    [Fact]
    public async Task GetCheckResults_EmptyMeansSupportedAndEmpty()
    {
        var handler = new GitLabFakeHandler();
        handler.EnqueueJson("[]");
        handler.EnqueueJson("[]");
        var remote = BuildRemote(new GitLabFakeGitHost(), handler, scoped: ScopedConfig());

        var summary = await remote.GetCheckResultsAsync("abc123");

        Assert.NotNull(summary);
        Assert.Empty(summary.Checks);
        Assert.True(summary.RequiredChecksPassed);
    }

    [Fact]
    public async Task GetCheckResults_UnknownSha_ReturnsNull()
    {
        var handler = new GitLabFakeHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        var remote = BuildRemote(new GitLabFakeGitHost(), handler, scoped: ScopedConfig());

        Assert.Null(await remote.GetCheckResultsAsync("deadbeef"));
    }

    [Fact]
    public async Task ListOpenPullRequests_FiltersPrefixAndSkipsChecking()
    {
        var handler = new GitLabFakeHandler();
        handler.EnqueueJson(Fixture("mr-list.json"));
        var remote = BuildRemote(new GitLabFakeGitHost(), handler, scoped: ScopedConfig());

        var prs = await remote.ListOpenPullRequestsAsync("codeybox/");

        // MR !8 reports detailed_merge_status=checking (still computing) so
        // it is skipped for reconsideration on the next tick — never guessed.
        // MR !9 has a different branch prefix.
        var pr = Assert.Single(prs);
        Assert.Equal(7, pr.Number);
        Assert.Equal("codeybox/abc123", pr.HeadBranch);
        Assert.Equal("9f2c4a1b8d3e5f60718293a4b5c6d7e8f90a1b2c", pr.HeadSha);
        Assert.Equal("main", pr.BaseBranch);
        Assert.False(pr.HasMergeConflict);
    }

    [Fact]
    public async Task ListOpenPullRequests_ConflictStatus_MapsConflictFlag()
    {
        var handler = new GitLabFakeHandler();
        handler.EnqueueJson(
            """[{"id":1003,"iid":9,"project_id":4,"state":"opened","detailed_merge_status":"conflict","source_branch":"codeybox/stale","target_branch":"main","sha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","web_url":"https://gitlab.example.com/x/-/merge_requests/9"}]""");
        var remote = BuildRemote(new GitLabFakeGitHost(), handler, scoped: ScopedConfig());

        var pr = Assert.Single(await remote.ListOpenPullRequestsAsync("codeybox/"));

        Assert.True(pr.HasMergeConflict);
    }

    [Fact]
    public async Task WebhookLifecycle_CreateListDelete()
    {
        var handler = new GitLabFakeHandler();
        handler.EnqueueJson(Fixture("hook-created.json"), HttpStatusCode.Created);
        handler.EnqueueJson(Fixture("hooks.json"));
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
        var remote = BuildRemote(new GitLabFakeGitHost(), handler, scoped: ScopedConfig());

        var created = await remote.CreateWebhookSubscriptionAsync(
            new NewUpstreamWebhookSubscription(
                ["push", "merge_request"], "https://codeybox.example.com/hooks/gitlab"));

        Assert.NotNull(created);
        Assert.Equal("repository", created.Scope);
        Assert.Equal("https://codeybox.example.com/hooks/gitlab", created.TargetUrl);
        Assert.Contains("push", created.Events);
        Assert.Contains("merge_request", created.Events);

        var subs = await remote.ListWebhookSubscriptionsAsync();
        Assert.NotNull(subs);
        var sub = Assert.Single(subs);
        Assert.Equal("401", sub.Id);

        Assert.True(await remote.DeleteWebhookSubscriptionAsync("401"));
    }

    [Fact]
    public async Task Webhooks_OrganizationScope_ResolvesGroup()
    {
        var handler = new GitLabFakeHandler();
        handler.EnqueueJson(Fixture("project.json"));
        handler.EnqueueJson(Fixture("hooks.json"));
        var remote = BuildRemote(new GitLabFakeGitHost(), handler, scoped: ScopedConfig());

        var subs = await remote.ListWebhookSubscriptionsAsync("organization");

        Assert.NotNull(subs);
        Assert.Single(subs);
        Assert.Contains("/groups/2/hooks", handler.Requests[1].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task DeleteWebhookSubscription_UnknownId_ReturnsFalse()
    {
        var handler = new GitLabFakeHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        var remote = BuildRemote(new GitLabFakeGitHost(), handler, scoped: ScopedConfig());

        Assert.False(await remote.DeleteWebhookSubscriptionAsync("999"));
    }

    [Fact]
    public async Task GetRepositoryMetadata_MapsVisibilityAndProtections()
    {
        var handler = new GitLabFakeHandler();
        handler.EnqueueJson(Fixture("project.json"));
        handler.EnqueueJson(Fixture("protected-branches.json"));
        handler.EnqueueJson(Fixture("project-approvals.json"));
        var remote = BuildRemote(new GitLabFakeGitHost(), handler, scoped: ScopedConfig());

        var metadata = await remote.GetRepositoryMetadataAsync();

        Assert.NotNull(metadata);
        Assert.Equal("main", metadata.DefaultBranch);
        Assert.Equal(UpstreamRepositoryVisibility.Private, metadata.Visibility);
        var rule = Assert.Single(metadata.BranchProtections);
        Assert.Equal("main", rule.BranchPattern);
        Assert.Equal(2, rule.RequiredApprovalCount);
        Assert.True(rule.RequiresStatusChecks);
    }

    [Fact]
    public async Task GetRepositoryMetadata_OldInstanceWithoutProtections_DegradesHonestly()
    {
        var handler = new GitLabFakeHandler();
        handler.EnqueueJson(Fixture("project.json"));
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        var remote = BuildRemote(new GitLabFakeGitHost(), handler, scoped: ScopedConfig());

        var metadata = await remote.GetRepositoryMetadataAsync();

        Assert.NotNull(metadata);
        Assert.Equal("main", metadata.DefaultBranch);
        Assert.Empty(metadata.BranchProtections);
    }

    // ------------------------------------------------------------------
    // Pagination, config, and guards
    // ------------------------------------------------------------------

    [Fact]
    public async Task ListOpenPullRequests_PaginatesOnNextPageHeader()
    {
        var first = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Fixture("mr-list.json"), Encoding.UTF8, "application/json"),
        };
        first.Headers.Add("X-Next-Page", "2");
        var handler = new GitLabFakeHandler();
        handler.Enqueue(first);
        handler.EnqueueJson("[]");
        var remote = BuildRemote(
            new GitLabFakeGitHost(), handler,
            scoped: ScopedConfig(new Dictionary<string, string> { ["PerPage"] = "1" }));

        _ = await remote.ListOpenPullRequestsAsync("codeybox/");

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("page=1", handler.Requests[0].RequestUri!.Query);
        Assert.Contains("page=2", handler.Requests[1].RequestUri!.Query);
        Assert.Contains("per_page=1", handler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task ListOpenPullRequests_MaxListPagesCapsRequests()
    {
        var first = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Fixture("mr-list.json"), Encoding.UTF8, "application/json"),
        };
        first.Headers.Add("X-Next-Page", "2");
        var handler = new GitLabFakeHandler();
        handler.Enqueue(first);
        var remote = BuildRemote(
            new GitLabFakeGitHost(), handler,
            scoped: ScopedConfig(new Dictionary<string, string>
            {
                ["PerPage"] = "1",
                ["MaxListPages"] = "1",
            }));

        _ = await remote.ListOpenPullRequestsAsync("codeybox/");

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CompleteAsync_MissingConfig_ThrowsActionableError()
    {
        var remote = BuildRemote(
            new GitLabFakeGitHost(), new GitLabFakeHandler(),
            project: new Dictionary<string, string>());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            remote.CompleteAsync(SampleRequest));
        Assert.Contains("Project", ex.Message);
    }

    [Fact]
    public async Task CompleteAsync_SelfHostedHttpBaseUrl_Allowed()
    {
        var git = new GitLabFakeGitHost();
        var handler = new GitLabFakeHandler();
        handler.EnqueueJson(Fixture("mr-open.json"), HttpStatusCode.Created);
        var remote = BuildRemote(git, handler, ProjectConfig("http://gitlab.lan:8080/api/v4"));

        var outcome = await remote.CompleteAsync(SampleRequest);

        Assert.True(outcome.BranchPushed);
        Assert.Equal("http://gitlab.lan:8080/myteam/myproject.git", git.Pushes[0].Url);
    }

    [Fact]
    public async Task CompleteAsync_NumericProjectId_ResolvesPathForGitAndMrUrls()
    {
        var git = new GitLabFakeGitHost();
        var handler = new GitLabFakeHandler();
        handler.EnqueueJson(Fixture("project.json"));
        handler.EnqueueJson(Fixture("mr-open.json"), HttpStatusCode.Created);
        var remote = BuildRemote(git, handler, ProjectConfig(
            "https://gitlab.example.com/api/v4", "4"));

        var outcome = await remote.CompleteAsync(SampleRequest);

        Assert.True(outcome.BranchPushed);
        Assert.Equal(
            "https://gitlab.example.com/myteam/myproject/-/merge_requests/7",
            outcome.PullRequestUrl);
        Assert.Contains("/projects/4/merge_requests", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Equal("https://gitlab.example.com/myteam/myproject.git", git.Pushes[0].Url);
    }

    [Fact]
    public async Task CompleteAsync_BaseUrlWithCredentials_Rejected()
    {
        var remote = BuildRemote(
            new GitLabFakeGitHost(), new GitLabFakeHandler(),
            ProjectConfig("https://oauth2:secret@gitlab.example.com/api/v4"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            remote.CompleteAsync(SampleRequest));
        Assert.Contains("credentials", ex.Message);
    }

    [Fact]
    public async Task CompleteAsync_InvalidMergeMethod_Throws()
    {
        var remote = BuildRemote(
            new GitLabFakeGitHost(), new GitLabFakeHandler(), ProjectConfig());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            remote.CompleteAsync(SampleRequest with { MergeMethod = "octopus" }));
    }

    [Fact]
    public async Task BranchGuards_RejectControlCharactersBeforeAnySideEffect()
    {
        var git = new GitLabFakeGitHost();
        var handler = new GitLabFakeHandler();
        var remote = BuildRemote(git, handler, ProjectConfig());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            remote.CompleteAsync(SampleRequest with { WorkBranch = "bad branch" }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            remote.TryMergeUpstreamBranchAsync("main", "x\ny"));
        // Leading-dash branch names would be parsed as git options
        // (--upload-pack RCE); they must be rejected before any side effect.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            remote.CompleteAsync(SampleRequest with { WorkBranch = "--upload-pack=evil" }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            remote.TryMergeUpstreamBranchAsync("--upload-pack=evil", "main"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            remote.TryMergeUpstreamBranchAsync("main", "--upload-pack=evil"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            remote.FetchBaseBranchAsync("repo-id", "--upload-pack=evil"));
        Assert.Empty(git.Pushes);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TryMergeUpstreamBranch_Unconfigured_ThrowsConfigError()
    {
        var remote = BuildRemote(
            new GitLabFakeGitHost(), new GitLabFakeHandler(), scoped: EmptyScopedConfig());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            remote.TryMergeUpstreamBranchAsync("main", "release/1"));
        Assert.Contains("BaseUrl", ex.Message);
    }

    [Fact]
    public async Task TryMergeUpstreamBranch_GitFailure_ThrowsInfrastructureWithoutToken()
    {
        // Unroutable discard port: clone fails fast at transport level.
        var remote = BuildRemote(
            new GitLabFakeGitHost(), new GitLabFakeHandler(),
            scoped: ScopedConfig("http://127.0.0.1:9/api/v4"));

        var ex = await Assert.ThrowsAsync<GitLabUpstreamException>(() =>
            remote.TryMergeUpstreamBranchAsync("main", "release/1"));
        Assert.DoesNotContain(TokenValue, ex.Message);
    }

    // ------------------------------------------------------------------
    // Credentials never reach a sandbox
    // ------------------------------------------------------------------

    [Fact]
    public async Task Plugin_HasNoSandboxSurface_AnywhereInItsContract()
    {
        var assembly = typeof(GitLabUpstreamRemote).Assembly;

        // The plugin must not reference sandbox assemblies at all.
        Assert.DoesNotContain(
            assembly.GetReferencedAssemblies(),
            name => name.Name is not null && name.Name.Contains("Sandbox", StringComparison.Ordinal));

        // Its injectable surface is host git + HTTP only: no sandbox
        // provider, agent registry, or credential-mount channel to leak
        // the upstream token through.
        foreach (var ctor in typeof(GitLabUpstreamRemote).GetConstructors())
        {
            foreach (var parameter in ctor.GetParameters())
            {
                var typeName = parameter.ParameterType.FullName ?? string.Empty;
                Assert.DoesNotContain("Sandbox", typeName, StringComparison.Ordinal);
                Assert.DoesNotContain("Credential", typeName, StringComparison.Ordinal);
            }
        }

        // Belt and braces: the fake git host explodes if sandbox access is
        // ever requested, and the full lifecycle below passes without that.
        var git = new GitLabFakeGitHost();
        var handler = new GitLabFakeHandler();
        handler.EnqueueJson(Fixture("mr-open.json"), HttpStatusCode.Created);
        var remote = BuildRemote(git, handler, ProjectConfig());
        var outcome = await remote.CompleteAsync(SampleRequest);
        Assert.True(outcome.BranchPushed);

        // The token reached the host-side git auth env (askpass), which is
        // the documented host boundary — and appears nowhere else.
        var pushEnv = git.Pushes[0].Env;
        Assert.Equal(TokenValue, pushEnv["CODEYBOX_GITLAB_GIT_PASS"]);
        Assert.DoesNotContain(pushEnv, kvp => kvp.Key.Contains("SANDBOX", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Plugin_RegistersUnderStableName()
    {
        var attribute = typeof(GitLabUpstreamRemote).GetCustomAttribute<CodeyBoxPluginAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal("codeybox.gitlab-upstream", attribute.Id);

        var remote = new GitLabUpstreamRemote(new GitLabFakeGitHost(), new GitLabFakeHandler().AsFactory());
        Assert.Equal("gitlab", remote.Name);
    }

    // ------------------------------------------------------------------
    // Fixture provenance: recorded shapes parse into the provider's model
    // ------------------------------------------------------------------

    [Fact]
    public void Fixtures_RecordedShapes_MapToContractValues()
    {
        Assert.Equal(UpstreamCheckState.Passing, GitLabUpstreamRemote.MapCheckState("success"));
        Assert.Equal(UpstreamCheckState.Pending, GitLabUpstreamRemote.MapCheckState("running"));
        Assert.Equal(UpstreamCheckState.Pending, GitLabUpstreamRemote.MapCheckState("created"));
        Assert.Equal(UpstreamCheckState.Failing, GitLabUpstreamRemote.MapCheckState("failed"));
        Assert.Equal(UpstreamCheckState.Cancelled, GitLabUpstreamRemote.MapCheckState("canceled"));
        Assert.Equal(UpstreamCheckState.Skipped, GitLabUpstreamRemote.MapCheckState("skipped"));
        Assert.Equal(UpstreamCheckState.Neutral, GitLabUpstreamRemote.MapCheckState("manual"));

        Assert.True(GitLabUpstreamRemote.IsMergeStatusKnown("mergeable"));
        Assert.True(GitLabUpstreamRemote.IsMergeStatusKnown("conflict"));
        Assert.False(GitLabUpstreamRemote.IsMergeStatusKnown("checking"));
        Assert.False(GitLabUpstreamRemote.IsMergeStatusKnown(null));

        Assert.True(GitLabUpstreamRemote.IsMergeConflict("conflict"));
        Assert.True(GitLabUpstreamRemote.IsMergeConflict("need_rebase"));
        Assert.False(GitLabUpstreamRemote.IsMergeConflict("mergeable"));

        Assert.Equal(UpstreamRepositoryVisibility.Private, GitLabUpstreamRemote.MapVisibility("private"));
        Assert.Equal(UpstreamRepositoryVisibility.Internal, GitLabUpstreamRemote.MapVisibility("internal"));
        Assert.Equal(UpstreamRepositoryVisibility.Public, GitLabUpstreamRemote.MapVisibility("public"));
        Assert.Null(GitLabUpstreamRemote.MapVisibility("mystery"));

        Assert.True(GitLabUpstreamRemote.TryMapHookEvents(["push", "merge_request"], out var flags));
        Assert.Contains("push", flags);
        Assert.Contains("merge_request", flags);
        Assert.False(GitLabUpstreamRemote.TryMapHookEvents(["not-a-gitlab-event"], out _));
    }

    // ------------------------------------------------------------------
    // Live integration (opt-in): read-only surfaces against a real instance
    // ------------------------------------------------------------------

    [SkippableFact]
    public async Task Live_ReadOnlySurfaces()
    {
        var baseUrl = Environment.GetEnvironmentVariable("GITLAB_TEST_BASEURL");
        var project = Environment.GetEnvironmentVariable("GITLAB_TEST_PROJECT");
        Skip.IfNot(!string.IsNullOrWhiteSpace(baseUrl) && !string.IsNullOrWhiteSpace(project),
            "Set GITLAB_TEST_BASEURL/PROJECT (and GITLAB_TEST_TOKEN) to run the live GitLab test.");

        const string liveTokenVar = "GITLAB_TEST_TOKEN";
        var git = new GitLabFakeGitHost();
        var live = new GitLabUpstreamRemote(
            git,
            new GitLabLiveHttpClientFactory());
        // Point the scoped config at the live token holder.
        var host = new GitLabFakePluginHost(
            ScopedConfig(baseUrl!, project!, liveTokenVar),
            new Dictionary<ProjectId, IReadOnlyDictionary<string, string>>());
        await live.InitializeAsync(new PluginContext("1.0", "codeybox.gitlab-upstream", "GitLab", host));

        var metadata = await live.GetRepositoryMetadataAsync();
        Assert.NotNull(metadata);
        Assert.False(string.IsNullOrWhiteSpace(metadata.DefaultBranch));

        var mrs = await live.ListOpenPullRequestsAsync("codeybox/");
        Assert.NotNull(mrs);

        var mrNumber = Environment.GetEnvironmentVariable("GITLAB_TEST_MR");
        if (int.TryParse(mrNumber, out var number) && number > 0)
        {
            var mr = await live.GetPullRequestAsync(number);
            Assert.NotNull(mr);
        }
    }

    // ------------------------------------------------------------------
    // Test infrastructure
    // ------------------------------------------------------------------

    private static GitLabUpstreamRemote BuildRemote(
        GitLabFakeGitHost git,
        GitLabFakeHandler handler,
        IReadOnlyDictionary<string, string>? project = null,
        IConfigurationSection? scoped = null)
    {
        var remote = new GitLabUpstreamRemote(git, handler.AsFactory());
        var host = new GitLabFakePluginHost(
            scoped ?? EmptyScopedConfig(),
            project is null
                ? new Dictionary<ProjectId, IReadOnlyDictionary<string, string>>()
                : new Dictionary<ProjectId, IReadOnlyDictionary<string, string>> { [ProjectId] = project });
        remote.InitializeAsync(new PluginContext("1.0", "codeybox.gitlab-upstream", "GitLab", host))
            .GetAwaiter().GetResult();
        return remote;
    }

    private static Dictionary<string, string> ProjectConfig(
        string baseUrl = "https://gitlab.example.com/api/v4",
        string project = "myteam/myproject") =>
        new()
        {
            ["BaseUrl"] = baseUrl,
            ["Project"] = project,
        };

    private static IConfigurationSection ScopedConfig(Dictionary<string, string> extra) =>
        ScopedConfig("https://gitlab.example.com/api/v4", "myteam/myproject", TokenEnvVar, extra);

    private static IConfigurationSection ScopedConfig(
        string baseUrl = "https://gitlab.example.com/api/v4",
        string project = "myteam/myproject",
        string? tokenEnvVar = TokenEnvVar,
        Dictionary<string, string>? extra = null)
    {
        var pairs = new Dictionary<string, string?>
        {
            ["GitLab:BaseUrl"] = baseUrl,
            ["GitLab:Project"] = project,
            ["GitLab:TokenEnvVar"] = tokenEnvVar,
        };
        if (extra is not null)
            foreach (var (k, v) in extra)
                pairs[$"GitLab:{k}"] = v;
        return new ConfigurationBuilder()
            .AddInMemoryCollection(pairs!)
            .Build()
            .GetSection("GitLab");
    }

    private static IConfigurationSection EmptyScopedConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build().GetSection("GitLab");
}

internal sealed class GitLabFakeGitHost : IGitHost
{
    public List<(string RepositoryId, string Url, string Branch, IReadOnlyDictionary<string, string> Env)> Pushes { get; } = new();
    public List<(string RepositoryId, string Url, string Branch, IReadOnlyDictionary<string, string> Env)> Fetches { get; } = new();
    public Exception? PushException { get; set; }
    public string? FetchShaToReturn { get; set; }

    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default)
        => Task.FromResult(id.ToString());
    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, string? baseBranch, CancellationToken ct = default)
        => Task.FromResult(id.ToString());

    // Any sandbox access attempt explodes: the provider must never ask.
    public SandboxRepositoryAccess GetSandboxAccess(string repositoryId)
        => throw new NotSupportedException("GitLab provider must not request sandbox access.");

    public Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default)
        => Task.FromResult("main");

    public Task PushToUpstreamAsync(
        string repositoryId, string upstreamUrl, string branch,
        IReadOnlyDictionary<string, string> upstreamEnv,
        UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase,
        CancellationToken ct = default)
    {
        if (PushException is not null)
            throw PushException;
        Pushes.Add((repositoryId, upstreamUrl, branch, new Dictionary<string, string>(upstreamEnv)));
        return Task.CompletedTask;
    }

    public Task<string?> FetchUpstreamBranchAsync(
        string repositoryId, string upstreamUrl, string branch,
        IReadOnlyDictionary<string, string> upstreamEnv, CancellationToken ct = default)
    {
        Fetches.Add((repositoryId, upstreamUrl, branch, new Dictionary<string, string>(upstreamEnv)));
        return Task.FromResult(FetchShaToReturn);
    }

    public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<(string DiffStat, string FullDiff)> GetDiffAsync(
        string repositoryId, string baseBranch, string workBranch, CancellationToken ct = default)
        => Task.FromResult<(string DiffStat, string FullDiff)>(("", ""));
}

internal sealed class GitLabFakeHandler : HttpMessageHandler
{
    private readonly Queue<object> _queue = new();
    public List<HttpRequestMessage> Requests { get; } = new();

    public void Enqueue(HttpResponseMessage response) => _queue.Enqueue(response);
    public void EnqueueException(Exception exception) => _queue.Enqueue(exception);

    public void EnqueueJson(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        _queue.Enqueue(new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

    public IHttpClientFactory AsFactory() => new GitLabFakeClientFactory(this);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (request.Content is not null)
            await request.Content.ReadAsStringAsync(cancellationToken);
        if (_queue.Count == 0)
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        var next = _queue.Dequeue();
        if (next is Exception exception)
            throw exception;
        return (HttpResponseMessage)next;
    }

    private sealed class GitLabFakeClientFactory(GitLabFakeHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(GitLabUpstreamRemote.HttpClientName, name);
            return new HttpClient(handler);
        }
    }
}

internal sealed class GitLabLiveHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        Assert.Equal(GitLabUpstreamRemote.HttpClientName, name);
        return new HttpClient(new HttpClientHandler());
    }
}

internal sealed class GitLabFakePluginHost(
    IConfigurationSection scoped,
    IReadOnlyDictionary<ProjectId, IReadOnlyDictionary<string, string>> projects) : IPluginHost, IUpstreamPluginHost
{
    public ILogger Logger { get; } = NullLogger.Instance;
    public IConfigurationSection ScopedConfig { get; } = scoped;

    public IReadOnlyDictionary<string, string> GetProjectUpstreamConfig(ProjectId projectId) =>
        projects.TryGetValue(projectId, out var config)
            ? config
            : new Dictionary<string, string>();
}
