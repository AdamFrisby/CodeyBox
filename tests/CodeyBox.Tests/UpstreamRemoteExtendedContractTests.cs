using CodeyBox.Core;
using CodeyBox.Upstream;
using CodeyBox.Upstream.GitHub;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Contract tests for the extended <see cref="IUpstreamRemote"/> surfaces
/// (review state, checks, comments, webhook subscriptions, repository
/// metadata). Every addition is default-implemented as "unsupported", so the
/// built-ins (<c>noop</c>, <c>github</c>, <c>git-generic</c>) work unchanged;
/// capability is discoverable because unsupported returns <c>null</c> while
/// "supported and empty" returns a non-null empty result. The two fake forges
/// below are deliberately shaped like GitLab (approval rules, pipelines,
/// discussions, project hooks) and Azure DevOps (reviewer policies, build
/// validations, service hooks) so GitHub's shape is never assumed.
/// </summary>
public sealed class UpstreamRemoteExtendedContractTests
{
    private static readonly UpstreamCompletionRequest SampleRequest = new()
    {
        RepositoryId = "repo-id",
        WorkItemId = new WorkItemId(Guid.Parse("00000000-0000-0000-0000-000000000003")),
        ProjectId = new ProjectId("test-project"),
        WorkBranch = "codeybox/abc123",
        BaseBranch = "main",
        MergeSha = "deadbeef",
        Title = "Add feature Z",
        Description = "Automated via CodeyBox",
    };

    private static GitHubUpstreamRemote BuildGitHubRemote() =>
        new(
            new FakeGitHost(),
            new FakeHttpClientFactory(new FakeHttpMessageHandler()),
            NullLogger<GitHubUpstreamRemote>.Instance,
            new GitHubUpstreamOptions
            {
                Owner = "myorg",
                Repository = "myrepo",
                Token = "test-token-not-a-real-pat",
            });

    private static GitGenericUpstreamRemote BuildGitGenericRemote() =>
        new(
            new FakeGitHost(),
            new GitGenericUpstreamOptions { UpstreamUrl = "https://git.example.com/repo.git" });

    // ------------------------------------------------------------------
    // Built-ins: unchanged behaviour, all new surfaces unsupported
    // ------------------------------------------------------------------

    [Fact]
    public async Task BuiltIns_AllNewSurfacesReturnUnsupported()
    {
        IUpstreamRemote[] remotes = [new NoopUpstreamRemote(), BuildGitHubRemote(), BuildGitGenericRemote()];

        foreach (var remote in remotes)
        {
            Assert.Null(await remote.GetReviewStateAsync(1));
            Assert.Null(await remote.GetCheckResultsAsync("abc123"));
            Assert.Null(await remote.ListCommentsAsync(1));
            Assert.Null(await remote.PostCommentAsync(1, new NewUpstreamComment("hello")));
            Assert.Null(await remote.ListWebhookSubscriptionsAsync());
            Assert.Null(
                await remote.CreateWebhookSubscriptionAsync(
                    new NewUpstreamWebhookSubscription(["push"], "https://hooks.example.com/x")));
            Assert.Null(await remote.DeleteWebhookSubscriptionAsync("sub-1"));
            Assert.Null(await remote.GetRepositoryMetadataAsync());
        }
    }

    [Fact]
    public async Task Noop_LifecycleUnchanged()
    {
        var remote = new NoopUpstreamRemote();

        var push = await remote.PushAsync("repo-id", "main");
        var outcome = await remote.CompleteAsync(SampleRequest);
        var merged = await remote.TryMergeUpstreamBranchAsync("main", "codeybox/abc123");

        Assert.True(push.Success);
        Assert.True(outcome.Skipped);
        Assert.True(merged);
    }

    [Fact]
    public async Task LegacyProvider_ImplementingNoneOfTheNewMembers_CompletesFullLifecycle()
    {
        // Compiles against only the original members: proof the extension is additive.
        IUpstreamRemote remote = new LegacyOnlyUpstreamRemote();

        var push = await remote.PushAsync("repo-id", "codeybox/abc123");
        var outcome = await remote.CompleteAsync(SampleRequest with { AutoMerge = true });
        var merged = await remote.TryMergeUpstreamBranchAsync("main", "codeybox/abc123");
        var release = await remote.CreateTagAndReleaseAsync("v1", "deadbeef", null);
        var prs = await remote.ListOpenPullRequestsAsync("codeybox/");
        var pr = await remote.GetPullRequestAsync(7);

        Assert.True(push.Success);
        Assert.True(outcome.BranchPushed);
        Assert.Equal("https://forge.example.com/pr/7", outcome.PullRequestUrl);
        Assert.Equal(7, outcome.PullRequestNumber);
        Assert.Equal("merge-sha", outcome.MergedSha);
        Assert.True(merged);
        Assert.Null(release);
        Assert.Empty(prs);
        Assert.Null(pr);

        // And every new surface reports unsupported rather than failing.
        Assert.Null(await remote.GetReviewStateAsync(7));
        Assert.Null(await remote.GetCheckResultsAsync("deadbeef"));
        Assert.Null(await remote.ListCommentsAsync(7));
        Assert.Null(await remote.PostCommentAsync(7, new NewUpstreamComment("hi")));
        Assert.Null(await remote.ListWebhookSubscriptionsAsync());
        Assert.Null(await remote.DeleteWebhookSubscriptionAsync("x"));
        Assert.Null(await remote.GetRepositoryMetadataAsync());
    }

    // ------------------------------------------------------------------
    // Supported-and-empty is distinguishable from not-supported
    // ------------------------------------------------------------------

    [Fact]
    public async Task SupportedEmpty_DistinguishedFromUnsupported_OnEverySurface()
    {
        IUpstreamRemote supported = new GitLabStyleUpstreamRemote(requiredApprovals: 0);
        IUpstreamRemote unsupported = new NoopUpstreamRemote();

        var reviews = await supported.GetReviewStateAsync(1);
        Assert.NotNull(reviews);
        Assert.Empty(reviews.Reviews);
        Assert.True(reviews.RequirementsMet);
        Assert.Null(await unsupported.GetReviewStateAsync(1));

        var checks = await supported.GetCheckResultsAsync("sha-with-no-pipeline");
        Assert.NotNull(checks);
        Assert.Empty(checks.Checks);
        Assert.True(checks.RequiredChecksPassed);
        Assert.Null(await unsupported.GetCheckResultsAsync("sha-with-no-pipeline"));

        var comments = await supported.ListCommentsAsync(1);
        Assert.NotNull(comments);
        Assert.Empty(comments);
        Assert.Null(await unsupported.ListCommentsAsync(1));

        var subs = await supported.ListWebhookSubscriptionsAsync();
        Assert.NotNull(subs);
        Assert.Empty(subs);
        Assert.Null(await unsupported.ListWebhookSubscriptionsAsync());

        var metadata = await supported.GetRepositoryMetadataAsync();
        Assert.NotNull(metadata);
        Assert.Equal("main", metadata.DefaultBranch);
        Assert.Null(await unsupported.GetRepositoryMetadataAsync());
    }

    // ------------------------------------------------------------------
    // GitLab-shaped forge: approval rules, pipelines, discussions, hooks
    // ------------------------------------------------------------------

    [Fact]
    public async Task GitLabStyle_ApprovalRuleQuorum_DrivesRequirementsMet()
    {
        var remote = new GitLabStyleUpstreamRemote(requiredApprovals: 2);

        var empty = await remote.GetReviewStateAsync(10);
        Assert.NotNull(empty);
        Assert.False(empty.RequirementsMet);
        Assert.Contains("any_approver", empty.RequiredReviewers);

        remote.Approve(10, "alice");
        var one = (await remote.GetReviewStateAsync(10))!;
        Assert.Equal(UpstreamReviewVerdict.Approved, one.Reviews[0].Verdict);
        Assert.Equal("alice", one.Reviews[0].Reviewer);
        Assert.False(one.RequirementsMet);

        remote.Approve(10, "bob");
        var two = (await remote.GetReviewStateAsync(10))!;
        Assert.True(two.RequirementsMet);
        Assert.Empty(two.RequiredReviewers);
    }

    [Fact]
    public async Task GitLabStyle_PipelineJobs_ReportedWithRequiredGate()
    {
        var remote = new GitLabStyleUpstreamRemote(requiredApprovals: 1);
        remote.RunPipeline("sha-1", [("build", true), ("test", false)], requiredPassed: false);

        var summary = await remote.GetCheckResultsAsync("sha-1");
        Assert.NotNull(summary);
        Assert.False(summary.RequiredChecksPassed);
        Assert.Equal(2, summary.Checks.Count);
        Assert.Equal("build", summary.Checks[0].Name);
        Assert.Equal(UpstreamCheckState.Passing, summary.Checks[0].State);
        Assert.Equal(UpstreamCheckState.Failing, summary.Checks[1].State);
        Assert.Equal("https://gitlab.example.com/-/jobs/2", summary.Checks[1].DetailsUrl);
    }

    [Fact]
    public async Task GitLabStyle_DiscussionReply_AnchoredToFileAndLine()
    {
        var remote = new GitLabStyleUpstreamRemote(requiredApprovals: 1);

        var posted = await remote.PostCommentAsync(
            10, new NewUpstreamComment("needs a test", "src/Foo.cs", 42));
        Assert.NotNull(posted);
        Assert.Equal("src/Foo.cs", posted.FilePath);
        Assert.Equal(42, posted.Line);

        var reply = await remote.PostCommentAsync(
            10, new NewUpstreamComment("added", replyToId: posted.Id));
        Assert.NotNull(reply);
        Assert.Equal(posted.Id, reply.Id.Replace("-reply", string.Empty, StringComparison.Ordinal));

        var all = (await remote.ListCommentsAsync(10))!;
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task GitLabStyle_ProjectHook_RoundTrip()
    {
        var remote = new GitLabStyleUpstreamRemote(requiredApprovals: 1);

        var created = await remote.CreateWebhookSubscriptionAsync(
            new NewUpstreamWebhookSubscription(
                ["Merge Request Hook", "Pipeline Hook"],
                "https://codeybox.example.com/hooks/gitlab",
                UpstreamWebhookScopes.Repository));
        Assert.NotNull(created);
        Assert.NotNull(await remote.ListWebhookSubscriptionsAsync());
        Assert.Single((await remote.ListWebhookSubscriptionsAsync())!);

        Assert.True(await remote.DeleteWebhookSubscriptionAsync(created.Id));
        Assert.Empty((await remote.ListWebhookSubscriptionsAsync())!);
        Assert.False(await remote.DeleteWebhookSubscriptionAsync(created.Id));
    }

    // ------------------------------------------------------------------
    // Azure-shaped forge: reviewer policy, validations, service hooks
    // ------------------------------------------------------------------

    [Fact]
    public async Task AzureStyle_RequiredReviewerReject_DrivesRequirementsMet()
    {
        var remote = new AzureStyleUpstreamRemote(requiredReviewer: "lead@example.com", minApprovers: 1);

        var pending = (await remote.GetReviewStateAsync(3))!;
        Assert.False(pending.RequirementsMet);
        Assert.Contains("lead@example.com", pending.RequiredReviewers);

        remote.Vote(3, "lead@example.com", UpstreamReviewVerdict.ChangesRequested);
        var rejected = (await remote.GetReviewStateAsync(3))!;
        Assert.False(rejected.RequirementsMet);
        Assert.Equal(UpstreamReviewVerdict.ChangesRequested, rejected.Reviews[0].Verdict);

        remote.Vote(3, "lead@example.com", UpstreamReviewVerdict.Approved);
        var approved = (await remote.GetReviewStateAsync(3))!;
        Assert.True(approved.RequirementsMet);
    }

    [Fact]
    public async Task AzureStyle_ChecksUnsupported_WhileReviewsSupported()
    {
        // Mixed support must be expressible: no forced translation where the
        // concept has no equivalent the provider wants to expose.
        IUpstreamRemote remote = new AzureStyleUpstreamRemote(
            requiredReviewer: "lead@example.com", minApprovers: 1);

        Assert.NotNull(await remote.GetReviewStateAsync(3));
        Assert.Null(await remote.GetCheckResultsAsync("any-sha"));
    }

    [Fact]
    public async Task AzureStyle_ServiceHookScope_PassedThroughAsOpaque()
    {
        var remote = new AzureStyleUpstreamRemote(requiredReviewer: "lead@example.com", minApprovers: 1);

        // Azure has no "repository" hook scope; its native "project" scope is
        // carried as an opaque string rather than coerced into Gitea's four.
        var created = await remote.CreateWebhookSubscriptionAsync(
            new NewUpstreamWebhookSubscription(
                ["git.pullrequest.created"],
                "https://codeybox.example.com/hooks/azure",
                "project"));
        Assert.NotNull(created);
        Assert.Equal("project", created.Scope);

        var metadata = await remote.GetRepositoryMetadataAsync();
        Assert.NotNull(metadata);
        Assert.Equal(UpstreamRepositoryVisibility.Private, metadata.Visibility);
        Assert.Single(metadata.BranchProtections);
        Assert.Equal("main", metadata.BranchProtections[0].BranchPattern);
    }

    [Fact]
    public async Task AzureStyle_PlainComment_PostsWithoutThreadAnchor()
    {
        var remote = new AzureStyleUpstreamRemote(requiredReviewer: "lead@example.com", minApprovers: 1);

        var posted = await remote.PostCommentAsync(3, new NewUpstreamComment("LGTM pending build"));
        Assert.NotNull(posted);
        Assert.Equal("LGTM pending build", posted.Body);
        Assert.Null(posted.FilePath);
        Assert.Single((await remote.ListCommentsAsync(3))!);
    }

    // ------------------------------------------------------------------
    // Input validation at the contract boundary
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NewComment_BlankBody_Throws(string body)
        => Assert.Throws<ArgumentException>(() => new NewUpstreamComment(body));

    [Fact]
    public void NewComment_LineWithoutFile_Throws()
        => Assert.Throws<ArgumentException>(() => new NewUpstreamComment("x", line: 3));

    [Fact]
    public void NewComment_NonPositiveLine_Throws()
        => Assert.Throws<ArgumentException>(() => new NewUpstreamComment("x", "a.cs", 0));

    [Fact]
    public void NewComment_OversizedBody_Throws()
        => Assert.Throws<ArgumentException>(
            () => new NewUpstreamComment(new string('x', NewUpstreamComment.MaxBodyLength + 1)));

    [Fact]
    public void NewSubscription_NoEvents_Throws()
        => Assert.Throws<ArgumentException>(
            () => new NewUpstreamWebhookSubscription([], "https://hooks.example.com/x"));

    [Fact]
    public void NewSubscription_NonAbsoluteUrl_Throws()
        => Assert.Throws<ArgumentException>(
            () => new NewUpstreamWebhookSubscription(["push"], "not-a-url"));

    [Fact]
    public void NewSubscription_BlankScope_Throws()
        => Assert.Throws<ArgumentException>(
            () => new NewUpstreamWebhookSubscription(["push"], "https://hooks.example.com/x", " "));

    [Fact]
    public void BranchProtection_NegativeApprovals_Throws()
        => Assert.Throws<ArgumentException>(() => new UpstreamBranchProtection("main", -1));

    // ------------------------------------------------------------------
    // Fake forges
    // ------------------------------------------------------------------

    /// <summary>Implements only the original members: proves the extension is additive.</summary>
    private sealed class LegacyOnlyUpstreamRemote : IUpstreamRemote
    {
        public string Name => "legacy";

        public Task<UpstreamPushResult> PushAsync(string repositoryId, string branch, CancellationToken ct = default)
            => Task.FromResult(new UpstreamPushResult(true, null));

        public Task<UpstreamCompletionOutcome> CompleteAsync(UpstreamCompletionRequest request, CancellationToken ct = default)
            => Task.FromResult(new UpstreamCompletionOutcome
            {
                BranchPushed = true,
                PullRequestUrl = "https://forge.example.com/pr/7",
                PullRequestNumber = 7,
                MergedSha = request.AutoMerge ? "merge-sha" : null,
            });

        public Task<bool> TryMergeUpstreamBranchAsync(string targetBranch, string sourceBranch, CancellationToken ct = default)
            => Task.FromResult(true);
    }

    /// <summary>
    /// GitLab-shaped in-memory forge: approval-rule quorum, pipelines keyed by
    /// sha, discussions (file-anchored or reply), project hooks, repo metadata.
    /// </summary>
    private sealed class GitLabStyleUpstreamRemote(int requiredApprovals) : IUpstreamRemote
    {
        private readonly Dictionary<int, List<(string Reviewer, bool Approved)>> _approvals = new();
        private readonly Dictionary<string, (List<UpstreamCheckResult> Checks, bool RequiredPassed)> _pipelines = new();
        private readonly Dictionary<int, List<UpstreamComment>> _comments = new();
        private readonly Dictionary<string, UpstreamWebhookSubscription> _hooks = new();
        private int _nextId;

        public string Name => "gitlab-fake";

        public Task<UpstreamPushResult> PushAsync(string repositoryId, string branch, CancellationToken ct = default)
            => Task.FromResult(new UpstreamPushResult(true, null));

        public Task<UpstreamCompletionOutcome> CompleteAsync(UpstreamCompletionRequest request, CancellationToken ct = default)
            => Task.FromResult(new UpstreamCompletionOutcome { BranchPushed = true });

        public Task<bool> TryMergeUpstreamBranchAsync(string targetBranch, string sourceBranch, CancellationToken ct = default)
            => Task.FromResult(true);

        public void Approve(int mr, string reviewer)
        {
            if (!_approvals.TryGetValue(mr, out var list))
                _approvals[mr] = list = [];
            if (!list.Any(a => a.Reviewer == reviewer))
                list.Add((reviewer, true));
        }

        public void RunPipeline(string sha, IEnumerable<(string Job, bool Passed)> jobs, bool requiredPassed)
        {
            var checks = jobs.Select((j, i) => new UpstreamCheckResult
            {
                Name = j.Job,
                State = j.Passed ? UpstreamCheckState.Passing : UpstreamCheckState.Failing,
                DetailsUrl = $"https://gitlab.example.com/-/jobs/{i + 1}",
            }).ToList();
            _pipelines[sha] = (checks, requiredPassed);
        }

        public Task<UpstreamReviewState?> GetReviewStateAsync(int number, CancellationToken ct = default)
        {
            _approvals.TryGetValue(number, out var list);
            list ??= [];
            var count = list.Count(a => a.Approved);
            var met = count >= requiredApprovals;
            UpstreamReviewState state = new()
            {
                Reviews = list.Select(a => new UpstreamReview
                {
                    Reviewer = a.Reviewer,
                    Verdict = UpstreamReviewVerdict.Approved,
                    SubmittedAt = DateTimeOffset.UtcNow,
                }).ToList(),
                RequiredReviewers = met ? [] : ["any_approver"],
                RequiredApprovalCount = requiredApprovals,
                RequirementsMet = met,
            };
            return Task.FromResult<UpstreamReviewState?>(state);
        }

        public Task<UpstreamCheckSummary?> GetCheckResultsAsync(string headSha, CancellationToken ct = default)
        {
            if (!_pipelines.TryGetValue(headSha, out var pipeline))
                return Task.FromResult<UpstreamCheckSummary?>(new UpstreamCheckSummary
                {
                    Checks = [],
                    RequiredChecksPassed = true,
                });
            return Task.FromResult<UpstreamCheckSummary?>(new UpstreamCheckSummary
            {
                Checks = pipeline.Checks,
                RequiredChecksPassed = pipeline.RequiredPassed,
            });
        }

        public Task<IReadOnlyList<UpstreamComment>?> ListCommentsAsync(int number, CancellationToken ct = default)
        {
            _comments.TryGetValue(number, out var list);
            return Task.FromResult<IReadOnlyList<UpstreamComment>?>(list ?? []);
        }

        public Task<UpstreamComment?> PostCommentAsync(int number, NewUpstreamComment comment, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(comment);
            if (!_comments.TryGetValue(number, out var list))
                _comments[number] = list = [];
            var id = comment.ReplyToId is null ? $"note-{++_nextId}" : $"{comment.ReplyToId}-reply";
            UpstreamComment posted = new()
            {
                Id = id,
                Author = "codeybox",
                Body = comment.Body,
                FilePath = comment.FilePath,
                Line = comment.Line,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            list.Add(posted);
            return Task.FromResult<UpstreamComment?>(posted);
        }

        public Task<IReadOnlyList<UpstreamWebhookSubscription>?> ListWebhookSubscriptionsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<UpstreamWebhookSubscription>?>(_hooks.Values.ToList());

        public Task<UpstreamWebhookSubscription?> CreateWebhookSubscriptionAsync(
            NewUpstreamWebhookSubscription subscription, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(subscription);
            UpstreamWebhookSubscription created = new()
            {
                Id = $"hook-{++_nextId}",
                Scope = subscription.Scope,
                Events = subscription.Events.ToList(),
                TargetUrl = subscription.TargetUrl,
            };
            _hooks[created.Id] = created;
            return Task.FromResult<UpstreamWebhookSubscription?>(created);
        }

        public Task<bool?> DeleteWebhookSubscriptionAsync(string id, CancellationToken ct = default)
            => Task.FromResult<bool?>(_hooks.Remove(id));

        public Task<UpstreamRepositoryMetadata?> GetRepositoryMetadataAsync(CancellationToken ct = default)
            => Task.FromResult<UpstreamRepositoryMetadata?>(new UpstreamRepositoryMetadata
            {
                DefaultBranch = "main",
                Visibility = UpstreamRepositoryVisibility.Private,
                BranchProtections = [new UpstreamBranchProtection("main", requiredApprovals, true)],
            });
    }

    /// <summary>
    /// Azure-shaped in-memory forge: required-reviewer votes plus a
    /// minimum-approver policy, build validations deliberately unexposed
    /// (checks unsupported), service-hook subscriptions with native scopes,
    /// plain comments, repo metadata with branch policy.
    /// </summary>
    private sealed class AzureStyleUpstreamRemote(string requiredReviewer, int minApprovers) : IUpstreamRemote
    {
        private readonly Dictionary<int, Dictionary<string, UpstreamReviewVerdict>> _votes = new();
        private readonly Dictionary<int, List<UpstreamComment>> _comments = new();
        private readonly Dictionary<string, UpstreamWebhookSubscription> _hooks = new();
        private int _nextId;

        public string Name => "azure-fake";

        public Task<UpstreamPushResult> PushAsync(string repositoryId, string branch, CancellationToken ct = default)
            => Task.FromResult(new UpstreamPushResult(true, null));

        public Task<UpstreamCompletionOutcome> CompleteAsync(UpstreamCompletionRequest request, CancellationToken ct = default)
            => Task.FromResult(new UpstreamCompletionOutcome { BranchPushed = true });

        public Task<bool> TryMergeUpstreamBranchAsync(string targetBranch, string sourceBranch, CancellationToken ct = default)
            => Task.FromResult(true);

        public void Vote(int pr, string reviewer, UpstreamReviewVerdict verdict)
        {
            if (!_votes.TryGetValue(pr, out var votes))
                _votes[pr] = votes = new Dictionary<string, UpstreamReviewVerdict>(StringComparer.OrdinalIgnoreCase);
            votes[reviewer] = verdict;
        }

        public Task<UpstreamReviewState?> GetReviewStateAsync(int number, CancellationToken ct = default)
        {
            _votes.TryGetValue(number, out var votes);
            votes ??= new Dictionary<string, UpstreamReviewVerdict>(StringComparer.OrdinalIgnoreCase);
            var approvals = votes.Count(v => v.Value == UpstreamReviewVerdict.Approved);
            var blocked = votes.Any(v => v.Value == UpstreamReviewVerdict.ChangesRequested);
            var requiredOk = votes.TryGetValue(requiredReviewer, out var requiredVote)
                && requiredVote == UpstreamReviewVerdict.Approved;
            var met = !blocked && requiredOk && approvals >= minApprovers;
            UpstreamReviewState state = new()
            {
                Reviews = votes.Select(v => new UpstreamReview
                {
                    Reviewer = v.Key,
                    Verdict = v.Value,
                    SubmittedAt = DateTimeOffset.UtcNow,
                }).ToList(),
                RequiredReviewers = met ? [] : [requiredReviewer],
                RequiredApprovalCount = minApprovers,
                RequirementsMet = met,
            };
            return Task.FromResult<UpstreamReviewState?>(state);
        }

        public Task<IReadOnlyList<UpstreamComment>?> ListCommentsAsync(int number, CancellationToken ct = default)
        {
            _comments.TryGetValue(number, out var list);
            return Task.FromResult<IReadOnlyList<UpstreamComment>?>(list ?? []);
        }

        public Task<UpstreamComment?> PostCommentAsync(int number, NewUpstreamComment comment, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(comment);
            if (!_comments.TryGetValue(number, out var list))
                _comments[number] = list = [];
            UpstreamComment posted = new()
            {
                Id = $"comment-{++_nextId}",
                Author = "codeybox",
                Body = comment.Body,
                FilePath = comment.FilePath,
                Line = comment.Line,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            list.Add(posted);
            return Task.FromResult<UpstreamComment?>(posted);
        }

        public Task<IReadOnlyList<UpstreamWebhookSubscription>?> ListWebhookSubscriptionsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<UpstreamWebhookSubscription>?>(_hooks.Values.ToList());

        public Task<UpstreamWebhookSubscription?> CreateWebhookSubscriptionAsync(
            NewUpstreamWebhookSubscription subscription, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(subscription);
            UpstreamWebhookSubscription created = new()
            {
                Id = $"sub-{++_nextId}",
                Scope = subscription.Scope,
                Events = subscription.Events.ToList(),
                TargetUrl = subscription.TargetUrl,
            };
            _hooks[created.Id] = created;
            return Task.FromResult<UpstreamWebhookSubscription?>(created);
        }

        public Task<bool?> DeleteWebhookSubscriptionAsync(string id, CancellationToken ct = default)
            => Task.FromResult<bool?>(_hooks.Remove(id));

        public Task<UpstreamRepositoryMetadata?> GetRepositoryMetadataAsync(CancellationToken ct = default)
            => Task.FromResult<UpstreamRepositoryMetadata?>(new UpstreamRepositoryMetadata
            {
                DefaultBranch = "main",
                Visibility = UpstreamRepositoryVisibility.Private,
                BranchProtections = [new UpstreamBranchProtection("main", minApprovers, true)],
            });
    }
}
