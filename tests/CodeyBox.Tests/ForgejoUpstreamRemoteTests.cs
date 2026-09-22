using System.Net;
using System.Reflection;
using System.Text;
using CodeyBox.Core;
using CodeyBox.ForgejoUpstreamPlugin;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the Forgejo upstream remote (<c>codeybox.forgejo-upstream</c>).
/// All forge traffic goes through a queued <see cref="HttpMessageHandler"/>;
/// recorded-shape fixtures under <c>Fixtures/Forgejo</c> were transcribed
/// from the live Forgejo API v1 swagger
/// (https://try.next.forgejo.org/swagger.v1.json) because no live instance
/// exists in CI — see the plugin README for the documented reason.
/// </summary>
public sealed class ForgejoUpstreamRemoteTests : IDisposable
{
    private const string TokenEnvVar = "CODEYBOX_FORGEJO_TEST_TOKEN";
    private const string TokenValue = "forgejo-test-token-not-a-secret";

    private static readonly ProjectId ProjectId = new("forgejo-project");

    private static readonly UpstreamCompletionRequest SampleRequest = new()
    {
        RepositoryId = "repo-id",
        WorkItemId = new WorkItemId(Guid.Parse("00000000-0000-0000-0000-000000000007")),
        ProjectId = ProjectId,
        WorkBranch = "codeybox/abc123",
        BaseBranch = "main",
        MergeSha = "deadbeef",
        Title = "Add feature Z",
        Description = "Automated via CodeyBox",
        TokenEnvVar = TokenEnvVar,
    };

    public ForgejoUpstreamRemoteTests()
        => Environment.SetEnvironmentVariable(TokenEnvVar, TokenValue);

    public void Dispose()
        => Environment.SetEnvironmentVariable(TokenEnvVar, null);

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "Forgejo", name));

    // ------------------------------------------------------------------
    // Full lifecycle: push, open PR, read state, merge
    // ------------------------------------------------------------------

    [Fact]
    public async Task CompleteAsync_FullLifecycle_PushesOpensReadsAndMerges()
    {
        var git = new ForgejoFakeGitHost();
        var handler = new ForgejoFakeHandler();
        // create PR, merge, re-read merged PR
        handler.EnqueueJson(Fixture("pull-open.json"), HttpStatusCode.Created);
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));
        handler.EnqueueJson(Fixture("pull-merged.json"));
        var remote = BuildRemote(git, handler, ProjectConfig());

        var outcome = await remote.CompleteAsync(SampleRequest with { AutoMerge = true });

        Assert.True(outcome.BranchPushed);
        Assert.Equal("https://forge.example.com/team/repo/pulls/7", outcome.PullRequestUrl);
        Assert.Equal(7, outcome.PullRequestNumber);
        Assert.Equal("cafef00dcafef00dcafef00dcafef00dcafef00d", outcome.MergedSha);
        Assert.Null(outcome.Notes);

        // Push went to the git clone URL derived from the API base …
        var push = Assert.Single(git.Pushes);
        Assert.Equal("https://forge.example.com/team/repo.git", push.Url);
        Assert.Equal("codeybox/abc123", push.Branch);
        // … with host-side askpass auth (never on argv) …
        Assert.True(push.Env.ContainsKey("GIT_ASKPASS"));
        // … and every API call carried the token header.
        Assert.NotEmpty(handler.Requests);
        foreach (var request in handler.Requests)
            Assert.Equal("token", request.Headers.Authorization?.Scheme);
        Assert.DoesNotContain(TokenValue, outcome.Notes ?? string.Empty);
    }

    [Fact]
    public async Task CompleteAsync_ExistingPullRequestNumber_SkipsCreate()
    {
        var git = new ForgejoFakeGitHost();
        var handler = new ForgejoFakeHandler();
        handler.EnqueueJson(Fixture("pull-open.json"));
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));
        handler.EnqueueJson(Fixture("pull-merged.json"));
        var remote = BuildRemote(git, handler, ProjectConfig());

        var outcome = await remote.CompleteAsync(SampleRequest with
        {
            AutoMerge = true,
            ExistingPullRequestNumber = 7,
        });

        Assert.Equal(7, outcome.PullRequestNumber);
        Assert.Equal("cafef00dcafef00dcafef00dcafef00dcafef00d", outcome.MergedSha);
        Assert.DoesNotContain(handler.Requests, r =>
            r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/pulls"));
    }

    [Fact]
    public async Task CompleteAsync_MergeBlocked_ReturnsPartialResult()
    {
        var git = new ForgejoFakeGitHost();
        var handler = new ForgejoFakeHandler();
        handler.EnqueueJson(Fixture("pull-open.json"), HttpStatusCode.Created);
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
        var remote = BuildRemote(git, handler, ProjectConfig());

        var outcome = await remote.CompleteAsync(SampleRequest with { AutoMerge = true });

        Assert.True(outcome.BranchPushed);
        Assert.Equal(7, outcome.PullRequestNumber);
        Assert.Null(outcome.MergedSha);
        Assert.NotNull(outcome.Notes);
        Assert.DoesNotContain(TokenValue, outcome.Notes);
    }

    [Fact]
    public async Task GetPullRequest_Merged_MapsStatusAndSha()
    {
        var handler = new ForgejoFakeHandler();
        handler.EnqueueJson(Fixture("pull-merged.json"));
        var remote = BuildRemote(new ForgejoFakeGitHost(), handler, scoped: ScopedConfig());

        var state = await remote.GetPullRequestAsync(7);

        Assert.NotNull(state);
        Assert.Equal(PullRequestStatus.Merged, state.Status);
        Assert.Equal("cafef00dcafef00dcafef00dcafef00dcafef00d", state.MergeCommitSha);
    }

    // ------------------------------------------------------------------
    // Unsupported is distinguishable from empty
    // ------------------------------------------------------------------

    [Fact]
    public async Task Comments_PlainSupported_AnchoredUnsupported()
    {
        var handler = new ForgejoFakeHandler();
        handler.EnqueueJson(Fixture("comments.json"));
        var remote = BuildRemote(new ForgejoFakeGitHost(), handler, scoped: ScopedConfig());

        var comments = await remote.ListCommentsAsync(7);

        Assert.NotNull(comments);
        Assert.Equal(2, comments.Count);
        Assert.Equal("approver-bob", comments[0].Author);
        Assert.All(comments, c => Assert.Null(c.FilePath));

        // File-anchored and threaded posts have no Forgejo equivalent:
        // unsupported (null), not an error, not silently downgraded.
        Assert.Null(await remote.PostCommentAsync(7, new NewUpstreamComment("hi", "a.cs", 3)));
        Assert.Null(await remote.PostCommentAsync(7, new NewUpstreamComment("hi", replyToId: "301")));
        // The declined posts made no HTTP calls.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Webhooks_NonRepositoryScopeUnsupported_EmptyListIsEmpty()
    {
        var handler = new ForgejoFakeHandler();
        handler.EnqueueJson("[]");
        var remote = BuildRemote(new ForgejoFakeGitHost(), handler, scoped: ScopedConfig());

        var subs = await remote.ListWebhookSubscriptionsAsync();

        Assert.NotNull(subs);
        Assert.Empty(subs);

        Assert.Null(await remote.CreateWebhookSubscriptionAsync(
            new NewUpstreamWebhookSubscription(["push"], "https://hooks.example.com/x", "organization")));
        Assert.Null(await remote.CreateWebhookSubscriptionAsync(
            new NewUpstreamWebhookSubscription(["push"], "https://hooks.example.com/x", "system")));
        // Only the list call went out; unsupported creates never hit the forge.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task UnconfiguredPlugin_ReadSurfacesReturnUnsupported()
    {
        var remote = BuildRemote(new ForgejoFakeGitHost(), new ForgejoFakeHandler(), scoped: EmptyScopedConfig());

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
        var handler = new ForgejoFakeHandler();
        handler.Enqueue(new HttpResponseMessage(status));
        var remote = BuildRemote(new ForgejoFakeGitHost(), handler, ProjectConfig());

        var ex = await Assert.ThrowsAsync<ForgejoUpstreamException>(() =>
            remote.CompleteAsync(SampleRequest));
        Assert.DoesNotContain(TokenValue, ex.Message);
    }

    [Fact]
    public async Task CompleteAsync_RateLimited_ThrowsWithRetryAfter()
    {
        var handler = new ForgejoFakeHandler();
        var limited = new HttpResponseMessage((HttpStatusCode)429);
        limited.Headers.TryAddWithoutValidation("Retry-After", "120");
        handler.Enqueue(limited);
        var remote = BuildRemote(new ForgejoFakeGitHost(), handler, ProjectConfig());

        var ex = await Assert.ThrowsAsync<ForgejoUpstreamException>(() =>
            remote.CompleteAsync(SampleRequest));
        Assert.Equal(120, ex.RetryAfterSeconds);
    }

    [Fact]
    public async Task CompleteAsync_ForgeUnreachable_ThrowsInfrastructure()
    {
        var handler = new ForgejoFakeHandler();
        handler.EnqueueException(new HttpRequestException("connection refused"));
        var remote = BuildRemote(new ForgejoFakeGitHost(), handler, ProjectConfig());

        await Assert.ThrowsAsync<ForgejoUpstreamException>(() =>
            remote.CompleteAsync(SampleRequest));
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task CompleteAsync_PrAlreadyExists_ReturnsPartialResult(HttpStatusCode status)
    {
        var git = new ForgejoFakeGitHost();
        var handler = new ForgejoFakeHandler();
        handler.Enqueue(new HttpResponseMessage(status));
        var remote = BuildRemote(git, handler, ProjectConfig());

        var outcome = await remote.CompleteAsync(SampleRequest);

        Assert.True(outcome.BranchPushed);
        Assert.Null(outcome.PullRequestUrl);
        Assert.Null(outcome.PullRequestNumber);
        Assert.NotNull(outcome.Notes);
        Assert.Single(git.Pushes);
    }

    [Fact]
    public async Task CompleteAsync_PushFails_ThrowsBeforeAnyApiCall()
    {
        var git = new ForgejoFakeGitHost { PushException = new InvalidOperationException("git push failed") };
        var handler = new ForgejoFakeHandler();
        var remote = BuildRemote(git, handler, ProjectConfig());

        await Assert.ThrowsAsync<ForgejoUpstreamException>(() =>
            remote.CompleteAsync(SampleRequest));
        Assert.Empty(handler.Requests);
    }

    // ------------------------------------------------------------------
    // Extended surfaces against recorded shapes
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetReviewState_MapsReviewsQuorumAndBlockers()
    {
        var handler = new ForgejoFakeHandler();
        handler.EnqueueJson(Fixture("reviews.json"));
        handler.EnqueueJson(Fixture("pull-open.json"));
        handler.EnqueueJson(Fixture("branch-protections.json"));
        var remote = BuildRemote(new ForgejoFakeGitHost(), handler, scoped: ScopedConfig());

        var state = await remote.GetReviewStateAsync(7);

        Assert.NotNull(state);
        Assert.Equal(3, state.Reviews.Count);
        Assert.Equal(UpstreamReviewVerdict.Approved, state.Reviews[0].Verdict);
        Assert.Equal(UpstreamReviewVerdict.ChangesRequested, state.Reviews[1].Verdict);
        Assert.Equal(UpstreamReviewVerdict.Commented, state.Reviews[2].Verdict);
        // Quorum comes from the main branch protection, not hardcoded.
        Assert.Equal(2, state.RequiredApprovalCount);
        Assert.Equal(["reviewer-ada"], state.RequiredReviewers);
        // One approval < quorum of two, plus an outstanding change request.
        Assert.False(state.RequirementsMet);
    }

    [Fact]
    public async Task GetReviewState_OldInstanceWithoutProtections_DegradesQuorum()
    {
        var handler = new ForgejoFakeHandler();
        handler.EnqueueJson("[]");
        handler.EnqueueJson(Fixture("pull-open-no-reviewers.json"));
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        var remote = BuildRemote(new ForgejoFakeGitHost(), handler, scoped: ScopedConfig());

        var state = await remote.GetReviewStateAsync(7);

        Assert.NotNull(state);
        Assert.Empty(state.Reviews);
        Assert.Equal(0, state.RequiredApprovalCount);
        Assert.True(state.RequirementsMet);
    }

    [Fact]
    public async Task GetCheckResults_MapsForgejoStates()
    {
        var handler = new ForgejoFakeHandler();
        handler.EnqueueJson(Fixture("combined-status.json"));
        var remote = BuildRemote(new ForgejoFakeGitHost(), handler, scoped: ScopedConfig());

        var summary = await remote.GetCheckResultsAsync("9f2c4a1b8d3e5f60718293a4b5c6d7e8f90a1b2c");

        Assert.NotNull(summary);
        Assert.Equal(2, summary.Checks.Count);
        Assert.Equal(UpstreamCheckState.Passing, summary.Checks[0].State);
        Assert.Equal(UpstreamCheckState.Pending, summary.Checks[1].State);
        // The forge's combined state is "success": required checks pass.
        Assert.True(summary.RequiredChecksPassed);
    }

    [Fact]
    public async Task GetCheckResults_EmptyMeansSupportedAndEmpty()
    {
        var handler = new ForgejoFakeHandler();
        handler.EnqueueJson("""{"state":"success","statuses":[]}""");
        var remote = BuildRemote(new ForgejoFakeGitHost(), handler, scoped: ScopedConfig());

        var summary = await remote.GetCheckResultsAsync("abc123");

        Assert.NotNull(summary);
        Assert.Empty(summary.Checks);
        Assert.True(summary.RequiredChecksPassed);
    }

    [Fact]
    public async Task ListOpenPullRequests_FiltersPrefixAndSkipsUnknownMergeability()
    {
        var handler = new ForgejoFakeHandler();
        handler.EnqueueJson(Fixture("pull-list.json"));
        var remote = BuildRemote(new ForgejoFakeGitHost(), handler, scoped: ScopedConfig());

        var prs = await remote.ListOpenPullRequestsAsync("codeybox/");

        // PR #8 reports mergeable=null (still computing) so it is skipped
        // for reconsideration on the next tick — never guessed.
        var pr = Assert.Single(prs);
        Assert.Equal(7, pr.Number);
        Assert.Equal("codeybox/abc123", pr.HeadBranch);
        Assert.Equal("9f2c4a1b8d3e5f60718293a4b5c6d7e8f90a1b2c", pr.HeadSha);
        Assert.Equal("main", pr.BaseBranch);
        Assert.False(pr.HasMergeConflict);
    }

    [Fact]
    public async Task WebhookLifecycle_CreateListDelete()
    {
        var handler = new ForgejoFakeHandler();
        handler.EnqueueJson(Fixture("hook-created.json"), HttpStatusCode.Created);
        handler.EnqueueJson(Fixture("hooks.json"));
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
        var remote = BuildRemote(new ForgejoFakeGitHost(), handler, scoped: ScopedConfig());

        var created = await remote.CreateWebhookSubscriptionAsync(
            new NewUpstreamWebhookSubscription(["push", "pull_request"], "https://codeybox.example.com/hooks/forgejo"));

        Assert.NotNull(created);
        Assert.Equal("repository", created.Scope);
        Assert.Equal("https://codeybox.example.com/hooks/forgejo", created.TargetUrl);

        var subs = await remote.ListWebhookSubscriptionsAsync();
        Assert.NotNull(subs);
        var sub = Assert.Single(subs);
        Assert.Equal("401", sub.Id);

        Assert.True(await remote.DeleteWebhookSubscriptionAsync("401"));
    }

    [Fact]
    public async Task DeleteWebhookSubscription_UnknownId_ReturnsFalse()
    {
        var handler = new ForgejoFakeHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        var remote = BuildRemote(new ForgejoFakeGitHost(), handler, scoped: ScopedConfig());

        Assert.False(await remote.DeleteWebhookSubscriptionAsync("999"));
    }

    [Fact]
    public async Task GetRepositoryMetadata_MapsVisibilityAndProtections()
    {
        var handler = new ForgejoFakeHandler();
        handler.EnqueueJson(Fixture("repository.json"));
        handler.EnqueueJson(Fixture("branch-protections.json"));
        var remote = BuildRemote(new ForgejoFakeGitHost(), handler, scoped: ScopedConfig());

        var metadata = await remote.GetRepositoryMetadataAsync();

        Assert.NotNull(metadata);
        Assert.Equal("main", metadata.DefaultBranch);
        Assert.Equal(UpstreamRepositoryVisibility.Public, metadata.Visibility);
        var rule = Assert.Single(metadata.BranchProtections);
        Assert.Equal("main", rule.BranchPattern);
        Assert.Equal(2, rule.RequiredApprovalCount);
        Assert.True(rule.RequiresStatusChecks);
    }

    [Fact]
    public async Task GetRepositoryMetadata_OldInstanceWithoutProtections_DegradesHonestly()
    {
        var handler = new ForgejoFakeHandler();
        handler.EnqueueJson(Fixture("repository.json"));
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        var remote = BuildRemote(new ForgejoFakeGitHost(), handler, scoped: ScopedConfig());

        var metadata = await remote.GetRepositoryMetadataAsync();

        Assert.NotNull(metadata);
        Assert.Equal("main", metadata.DefaultBranch);
        Assert.Empty(metadata.BranchProtections);
    }

    // ------------------------------------------------------------------
    // Pagination, config, and guards
    // ------------------------------------------------------------------

    [Fact]
    public async Task ListOpenPullRequests_PaginatesUntilShortPage()
    {
        var handler = new ForgejoFakeHandler();
        handler.EnqueueJson(Fixture("pull-list.json"));
        handler.EnqueueJson("[]");
        var remote = BuildRemote(
            new ForgejoFakeGitHost(), handler,
            scoped: ScopedConfig(new Dictionary<string, string> { ["PageSize"] = "2" }));

        _ = await remote.ListOpenPullRequestsAsync("codeybox/");

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("page=1", handler.Requests[0].RequestUri!.Query);
        Assert.Contains("page=2", handler.Requests[1].RequestUri!.Query);
        Assert.Contains("limit=2", handler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task ListOpenPullRequests_MaxListPagesCapsRequests()
    {
        var handler = new ForgejoFakeHandler();
        handler.EnqueueJson(Fixture("pull-list.json"));
        var remote = BuildRemote(
            new ForgejoFakeGitHost(), handler,
            scoped: ScopedConfig(new Dictionary<string, string>
            {
                ["PageSize"] = "2",
                ["MaxListPages"] = "1",
            }));

        _ = await remote.ListOpenPullRequestsAsync("codeybox/");

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CompleteAsync_MissingConfig_ThrowsActionableError()
    {
        var remote = BuildRemote(
            new ForgejoFakeGitHost(), new ForgejoFakeHandler(),
            project: new Dictionary<string, string>());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            remote.CompleteAsync(SampleRequest));
        Assert.Contains("BaseUrl", ex.Message);
    }

    [Fact]
    public async Task CompleteAsync_SelfHostedHttpBaseUrl_Allowed()
    {
        var git = new ForgejoFakeGitHost();
        var handler = new ForgejoFakeHandler();
        handler.EnqueueJson(Fixture("pull-open.json"), HttpStatusCode.Created);
        var remote = BuildRemote(git, handler, ProjectConfig("http://forgejo.lan:3000/api/v1"));

        var outcome = await remote.CompleteAsync(SampleRequest);

        Assert.True(outcome.BranchPushed);
        Assert.Equal("http://forgejo.lan:3000/team/repo.git", git.Pushes[0].Url);
    }

    [Fact]
    public async Task CompleteAsync_BaseUrlWithCredentials_Rejected()
    {
        var remote = BuildRemote(
            new ForgejoFakeGitHost(), new ForgejoFakeHandler(),
            ProjectConfig("https://user:pass@forge.example.com/api/v1"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            remote.CompleteAsync(SampleRequest));
        Assert.Contains("credentials", ex.Message);
    }

    [Fact]
    public async Task CompleteAsync_InvalidMergeMethod_Throws()
    {
        var remote = BuildRemote(
            new ForgejoFakeGitHost(), new ForgejoFakeHandler(), ProjectConfig());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            remote.CompleteAsync(SampleRequest with { MergeMethod = "octopus" }));
    }

    [Fact]
    public async Task BranchGuards_RejectControlCharactersBeforeAnySideEffect()
    {
        var git = new ForgejoFakeGitHost();
        var handler = new ForgejoFakeHandler();
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
            new ForgejoFakeGitHost(), new ForgejoFakeHandler(), scoped: EmptyScopedConfig());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            remote.TryMergeUpstreamBranchAsync("main", "release/1"));
        Assert.Contains("BaseUrl", ex.Message);
    }

    [Fact]
    public async Task TryMergeUpstreamBranch_GitFailure_ThrowsInfrastructureWithoutToken()
    {
        // Unroutable discard port: clone fails fast at transport level.
        var remote = BuildRemote(
            new ForgejoFakeGitHost(), new ForgejoFakeHandler(),
            scoped: ScopedConfig("http://127.0.0.1:9/api/v1"));

        var ex = await Assert.ThrowsAsync<ForgejoUpstreamException>(() =>
            remote.TryMergeUpstreamBranchAsync("main", "release/1"));
        Assert.DoesNotContain(TokenValue, ex.Message);
    }

    // ------------------------------------------------------------------
    // Credentials never reach a sandbox
    // ------------------------------------------------------------------

    [Fact]
    public async Task Plugin_HasNoSandboxSurface_AnywhereInItsContract()
    {
        var assembly = typeof(ForgejoUpstreamRemote).Assembly;

        // The plugin must not reference sandbox assemblies at all.
        Assert.DoesNotContain(
            assembly.GetReferencedAssemblies(),
            name => name.Name is not null && name.Name.Contains("Sandbox", StringComparison.Ordinal));

        // Its injectable surface is host git + HTTP only: no sandbox
        // provider, agent registry, or credential-mount channel to leak
        // the upstream token through.
        foreach (var ctor in typeof(ForgejoUpstreamRemote).GetConstructors())
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
        var git = new ForgejoFakeGitHost();
        var handler = new ForgejoFakeHandler();
        handler.EnqueueJson(Fixture("pull-open.json"), HttpStatusCode.Created);
        var remote = BuildRemote(git, handler, ProjectConfig());
        var outcome = await remote.CompleteAsync(SampleRequest);
        Assert.True(outcome.BranchPushed);

        // The token reached the host-side git auth env (askpass), which is
        // the documented host boundary — and appears nowhere else.
        var pushEnv = git.Pushes[0].Env;
        Assert.Equal(TokenValue, pushEnv["CODEYBOX_FORGEJO_GIT_PASS"]);
        Assert.DoesNotContain(pushEnv, kvp => kvp.Key.Contains("SANDBOX", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Plugin_RegistersUnderStableName()
    {
        var attribute = typeof(ForgejoUpstreamRemote).GetCustomAttribute<CodeyBoxPluginAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal("codeybox.forgejo-upstream", attribute.Id);

        var remote = new ForgejoUpstreamRemote(new ForgejoFakeGitHost(), new ForgejoFakeHandler().AsFactory());
        Assert.Equal("forgejo", remote.Name);
    }

    // ------------------------------------------------------------------
    // Fixture provenance: recorded shapes parse into the provider's model
    // ------------------------------------------------------------------

    [Fact]
    public void Fixtures_RecordedShapes_MapToContractValues()
    {
        Assert.Equal(UpstreamReviewVerdict.Approved, ForgejoUpstreamRemote.MapReviewVerdict("APPROVED", false));
        Assert.Equal(UpstreamReviewVerdict.ChangesRequested, ForgejoUpstreamRemote.MapReviewVerdict("REQUEST_CHANGES", false));
        Assert.Equal(UpstreamReviewVerdict.ChangesRequested, ForgejoUpstreamRemote.MapReviewVerdict("CHANGES_REQUESTED", false));
        Assert.Equal(UpstreamReviewVerdict.Pending, ForgejoUpstreamRemote.MapReviewVerdict("PENDING", false));
        Assert.Equal(UpstreamReviewVerdict.Dismissed, ForgejoUpstreamRemote.MapReviewVerdict("APPROVED", true));

        Assert.Equal(UpstreamCheckState.Passing, ForgejoUpstreamRemote.MapCheckState("success"));
        Assert.Equal(UpstreamCheckState.Pending, ForgejoUpstreamRemote.MapCheckState("pending"));
        Assert.Equal(UpstreamCheckState.Failing, ForgejoUpstreamRemote.MapCheckState("failure"));
        Assert.Equal(UpstreamCheckState.Failing, ForgejoUpstreamRemote.MapCheckState("error"));
        Assert.Equal(UpstreamCheckState.Neutral, ForgejoUpstreamRemote.MapCheckState("warning"));

        Assert.True(ForgejoUpstreamRemote.ProtectionMatchesBranch("main", "main"));
        Assert.True(ForgejoUpstreamRemote.ProtectionMatchesBranch("release/*", "release/1"));
        Assert.False(ForgejoUpstreamRemote.ProtectionMatchesBranch("main", "main2"));
    }

    // ------------------------------------------------------------------
    // Test infrastructure
    // ------------------------------------------------------------------

    private static ForgejoUpstreamRemote BuildRemote(
        ForgejoFakeGitHost git,
        ForgejoFakeHandler handler,
        IReadOnlyDictionary<string, string>? project = null,
        IConfigurationSection? scoped = null)
    {
        var remote = new ForgejoUpstreamRemote(git, handler.AsFactory());
        var host = new ForgejoFakePluginHost(
            scoped ?? EmptyScopedConfig(),
            project is null
                ? new Dictionary<ProjectId, IReadOnlyDictionary<string, string>>()
                : new Dictionary<ProjectId, IReadOnlyDictionary<string, string>> { [ProjectId] = project });
        remote.InitializeAsync(new PluginContext("1.0", "codeybox.forgejo-upstream", "Forgejo", host))
            .GetAwaiter().GetResult();
        return remote;
    }

    private static Dictionary<string, string> ProjectConfig(string baseUrl = "https://forge.example.com/api/v1") =>
        new()
        {
            ["BaseUrl"] = baseUrl,
            ["Owner"] = "team",
            ["Repository"] = "repo",
        };

    private static IConfigurationSection ScopedConfig(Dictionary<string, string> extra) =>
        ScopedConfig("https://forge.example.com/api/v1", extra);

    private static IConfigurationSection ScopedConfig(
        string baseUrl = "https://forge.example.com/api/v1",
        Dictionary<string, string>? extra = null)
    {
        var pairs = new Dictionary<string, string?>
        {
            ["Forgejo:BaseUrl"] = baseUrl,
            ["Forgejo:Owner"] = "team",
            ["Forgejo:Repository"] = "repo",
            ["Forgejo:TokenEnvVar"] = TokenEnvVar,
        };
        if (extra is not null)
            foreach (var (k, v) in extra)
                pairs[$"Forgejo:{k}"] = v;
        return new ConfigurationBuilder()
            .AddInMemoryCollection(pairs!)
            .Build()
            .GetSection("Forgejo");
    }

    private static IConfigurationSection EmptyScopedConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build().GetSection("Forgejo");
}

internal sealed class ForgejoFakeGitHost : IGitHost
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
        => throw new NotSupportedException("Forgejo provider must not request sandbox access.");

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

internal sealed class ForgejoFakeHandler : HttpMessageHandler
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

    public IHttpClientFactory AsFactory() => new ForgejoFakeClientFactory(this);

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

    private sealed class ForgejoFakeClientFactory(ForgejoFakeHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(ForgejoUpstreamRemote.HttpClientName, name);
            return new HttpClient(handler) { BaseAddress = new Uri("https://forge.example.com/api/v1/") };
        }
    }
}

internal sealed class ForgejoFakePluginHost(
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
