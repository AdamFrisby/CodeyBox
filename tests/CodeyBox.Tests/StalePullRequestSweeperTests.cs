using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="StalePullRequestSweeper"/> — the periodic poller that
/// detects CodeyBox-authored PRs left unmergeable by base-branch motion and
/// fires <c>upstream.pr_stale_base</c> webhook events.
///
/// <para>Bug context: after a work item ships to <c>Done</c> and the PR opens,
/// CodeyBox loses ownership of the item. If main moves and produces a conflict
/// before the PR auto-merges, the PR can sit indefinitely without a signal.
/// These tests verify the sweeper detects the conflict, fires exactly one
/// event per <c>(projectId, prNumber, headSha)</c> identity, and reacts to
/// new head SHAs (operator-pushed rebase attempts) by re-firing.</para>
/// </summary>
public sealed class StalePullRequestSweeperTests
{
    private static Project BuildGitHubProject(string id = "test-project") =>
        new()
        {
            Id = new ProjectId(id),
            DisplayName = id,
            RepositoryUrl = "https://github.com/example/repo",
            Upstream = new ProjectUpstream
            {
                Kind = "github",
                GitHubOwner = "example",
                GitHubRepository = "repo",
                TokenEnvVar = "FAKE_TOKEN",
            },
        };

    private static Project BuildNoopProject(string id = "noop-project") =>
        new()
        {
            Id = new ProjectId(id),
            DisplayName = id,
            RepositoryUrl = "https://example.invalid/repo.git",
            Upstream = ProjectUpstream.Noop,
        };

    private static StalePullRequestSweeperOptions DefaultOpts => new()
    {
        Enabled = true,
        CheckInterval = TimeSpan.FromSeconds(30),
        BranchPrefix = "codeybox/",
    };

    [Fact]
    public async Task Sweep_PrWithMergeConflict_FiresWebhookEventOnce()
    {
        var project = BuildGitHubProject();
        var dirtyPr = new UpstreamPullRequest
        {
            Number = 112,
            Url = "https://github.com/example/repo/pull/112",
            HeadBranch = "codeybox/b6e61d94",
            HeadSha = "sha-original",
            BaseBranch = "main",
            HasMergeConflict = true,
        };
        var factory = new ScriptedUpstreamFactory(new ScriptedUpstreamRemote(new[] { dirtyPr }));
        var webhooks = new CapturingWebhookDispatcher();
        var sweeper = BuildSweeper(project, factory, webhooks);

        await sweeper.RunSweepAsync(CancellationToken.None);

        var evt = Assert.Single(webhooks.Events);
        Assert.Equal("upstream.pr_stale_base", evt.Event);
        Assert.Equal(project.Id, evt.Project?.Id);
        var details = Assert.IsType<StalePullRequestDetails>(evt.Details);
        Assert.Equal(112, details.PullRequestNumber);
        Assert.Equal("https://github.com/example/repo/pull/112", details.PullRequestUrl);
        Assert.Equal("codeybox/b6e61d94", details.HeadBranch);
        Assert.Equal("sha-original", details.HeadSha);
        Assert.Equal("main", details.BaseBranch);
        Assert.Equal(project.Id.Value, details.ProjectId);
    }

    [Fact]
    public async Task Sweep_RepeatedDetectionOfSameIdentity_DoesNotRefire()
    {
        // Idempotency requirement #4 in the bug spec: repeated triggers on the
        // same PR must not spawn duplicate events. The dedup key is
        // (projectId, prNumber, headSha) — staleness on the same tip is the
        // same event, not a new one.
        var project = BuildGitHubProject();
        var dirtyPr = new UpstreamPullRequest
        {
            Number = 200,
            Url = "https://github.com/example/repo/pull/200",
            HeadBranch = "codeybox/abc",
            HeadSha = "tip-1",
            BaseBranch = "main",
            HasMergeConflict = true,
        };
        var remote = new ScriptedUpstreamRemote(new[] { dirtyPr });
        var factory = new ScriptedUpstreamFactory(remote);
        var webhooks = new CapturingWebhookDispatcher();
        var sweeper = BuildSweeper(project, factory, webhooks);

        await sweeper.RunSweepAsync(CancellationToken.None);
        await sweeper.RunSweepAsync(CancellationToken.None);
        await sweeper.RunSweepAsync(CancellationToken.None);

        Assert.Single(webhooks.Events);
        // List call should still happen each tick (so we keep checking).
        Assert.Equal(3, remote.ListCallCount);
    }

    [Fact]
    public async Task Sweep_PrHeadShaChanges_RefiresEvent()
    {
        // Operator pushes a partial rebase that still conflicts. The dedup
        // identity changes (new head sha), so a fresh event fires — that's
        // the signal "your most recent rebase attempt didn't resolve it".
        var project = BuildGitHubProject();
        var firstTip = new UpstreamPullRequest
        {
            Number = 5,
            Url = "https://github.com/example/repo/pull/5",
            HeadBranch = "codeybox/x",
            HeadSha = "tip-1",
            BaseBranch = "main",
            HasMergeConflict = true,
        };
        var secondTip = firstTip with { HeadSha = "tip-2" };
        var remote = new ScriptedUpstreamRemote(
            new[] { firstTip },
            new[] { secondTip });
        var factory = new ScriptedUpstreamFactory(remote);
        var webhooks = new CapturingWebhookDispatcher();
        var sweeper = BuildSweeper(project, factory, webhooks);

        await sweeper.RunSweepAsync(CancellationToken.None);
        await sweeper.RunSweepAsync(CancellationToken.None);

        Assert.Equal(2, webhooks.Events.Count);
        Assert.Equal("tip-1", ((StalePullRequestDetails)webhooks.Events[0].Details!).HeadSha);
        Assert.Equal("tip-2", ((StalePullRequestDetails)webhooks.Events[1].Details!).HeadSha);
    }

    [Fact]
    public async Task Sweep_PrWithoutConflict_DoesNotFireEvent()
    {
        // Mergeable PRs are ignored entirely — the sweeper only signals on
        // genuinely-stuck PRs that need operator intervention.
        var project = BuildGitHubProject();
        var cleanPr = new UpstreamPullRequest
        {
            Number = 1,
            Url = "https://github.com/example/repo/pull/1",
            HeadBranch = "codeybox/clean",
            HeadSha = "tip",
            BaseBranch = "main",
            HasMergeConflict = false,
        };
        var factory = new ScriptedUpstreamFactory(new ScriptedUpstreamRemote(new[] { cleanPr }));
        var webhooks = new CapturingWebhookDispatcher();
        var sweeper = BuildSweeper(project, factory, webhooks);

        await sweeper.RunSweepAsync(CancellationToken.None);

        Assert.Empty(webhooks.Events);
    }

    [Fact]
    public async Task Sweep_PrConflictResolvesAfterFiring_ThenReturnsConflicted_ReFires()
    {
        // Lifecycle: stale → operator rebases (clean) → main moves again (stale).
        // The dedup table should clear the stale identity on the "clean" tick
        // so the next stale observation fires a fresh event. Otherwise an
        // operator who resolves and re-breaks loses the second alert.
        var project = BuildGitHubProject();
        var dirtyV1 = new UpstreamPullRequest
        {
            Number = 9,
            Url = "https://github.com/example/repo/pull/9",
            HeadBranch = "codeybox/x",
            HeadSha = "tip-1",
            BaseBranch = "main",
            HasMergeConflict = true,
        };
        // After operator rebase: the PR's head sha changed AND it's clean.
        var clean = dirtyV1 with { HeadSha = "tip-2", HasMergeConflict = false };
        // Then main moves and creates a new conflict on the rebased tip.
        var dirtyV2 = clean with { HasMergeConflict = true };
        var remote = new ScriptedUpstreamRemote(
            new[] { dirtyV1 },
            new[] { clean },
            new[] { dirtyV2 });
        var factory = new ScriptedUpstreamFactory(remote);
        var webhooks = new CapturingWebhookDispatcher();
        var sweeper = BuildSweeper(project, factory, webhooks);

        await sweeper.RunSweepAsync(CancellationToken.None);
        await sweeper.RunSweepAsync(CancellationToken.None);
        await sweeper.RunSweepAsync(CancellationToken.None);

        // Two events: dirty(tip-1) and dirty(tip-2). The clean middle tick is
        // silent on the webhook bus.
        Assert.Equal(2, webhooks.Events.Count);
        Assert.Equal("tip-1", ((StalePullRequestDetails)webhooks.Events[0].Details!).HeadSha);
        Assert.Equal("tip-2", ((StalePullRequestDetails)webhooks.Events[1].Details!).HeadSha);
    }

    [Fact]
    public async Task Sweep_NoopProject_IsSkipped()
    {
        // The sweeper only knows how to enumerate PRs on github-upstream
        // projects today; noop/git-generic projects don't have a PR concept.
        var project = BuildNoopProject();
        var factory = new ThrowingUpstreamFactory();
        var webhooks = new CapturingWebhookDispatcher();
        var sweeper = new StalePullRequestSweeper(
            new InMemoryProjectRepository(project),
            factory,
            webhooks,
            DefaultOpts,
            NullLogger<StalePullRequestSweeper>.Instance);

        await sweeper.RunSweepAsync(CancellationToken.None);

        Assert.Empty(webhooks.Events);
        Assert.False(factory.CreateCalled);
    }

    [Fact]
    public async Task Sweep_MultipleProjects_EachSweptIndependently()
    {
        // Cross-project bleed test: two github projects each have their own
        // stale PR; both must produce one event each.
        var p1 = BuildGitHubProject("project-a");
        var p2 = BuildGitHubProject("project-b");
        var dirty1 = new UpstreamPullRequest
        {
            Number = 1,
            Url = "u1",
            HeadBranch = "codeybox/a",
            HeadSha = "a-1",
            BaseBranch = "main",
            HasMergeConflict = true,
        };
        var dirty2 = new UpstreamPullRequest
        {
            Number = 2,
            Url = "u2",
            HeadBranch = "codeybox/b",
            HeadSha = "b-1",
            BaseBranch = "main",
            HasMergeConflict = true,
        };
        var factory = new PerProjectScriptedUpstreamFactory(new Dictionary<string, ScriptedUpstreamRemote>
        {
            [p1.Id.Value] = new ScriptedUpstreamRemote(new[] { dirty1 }),
            [p2.Id.Value] = new ScriptedUpstreamRemote(new[] { dirty2 }),
        });
        var webhooks = new CapturingWebhookDispatcher();
        var sweeper = new StalePullRequestSweeper(
            new InMemoryProjectRepository(p1, p2),
            factory,
            webhooks,
            DefaultOpts,
            NullLogger<StalePullRequestSweeper>.Instance);

        await sweeper.RunSweepAsync(CancellationToken.None);

        Assert.Equal(2, webhooks.Events.Count);
        var projectIds = webhooks.Events
            .Select(e => ((StalePullRequestDetails)e.Details!).ProjectId)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(new[] { "project-a", "project-b" }, projectIds);
    }

    [Fact]
    public async Task Sweep_UpstreamThrows_DoesNotBubbleAndContinuesNextProject()
    {
        // Resilience: a single project's GitHub API call should not break the
        // sweep for every other project. Forge outages happen.
        var failing = BuildGitHubProject("failing");
        var healthy = BuildGitHubProject("healthy");
        var dirty = new UpstreamPullRequest
        {
            Number = 7,
            Url = "u",
            HeadBranch = "codeybox/h",
            HeadSha = "tip",
            BaseBranch = "main",
            HasMergeConflict = true,
        };
        var factory = new PerProjectScriptedUpstreamFactory(new Dictionary<string, ScriptedUpstreamRemote>
        {
            [failing.Id.Value] = new ScriptedUpstreamRemote(throwOnList: new InvalidOperationException("github 502")),
            [healthy.Id.Value] = new ScriptedUpstreamRemote(new[] { dirty }),
        });
        var webhooks = new CapturingWebhookDispatcher();
        var sweeper = new StalePullRequestSweeper(
            new InMemoryProjectRepository(failing, healthy),
            factory,
            webhooks,
            DefaultOpts,
            NullLogger<StalePullRequestSweeper>.Instance);

        await sweeper.RunSweepAsync(CancellationToken.None);

        var evt = Assert.Single(webhooks.Events);
        var details = Assert.IsType<StalePullRequestDetails>(evt.Details);
        Assert.Equal("healthy", details.ProjectId);
    }

    [Fact]
    public async Task Sweep_EnabledFalse_ExecuteAsyncReturnsImmediatelyWithoutTouchingUpstream()
    {
        // Enabled=false is the operator's kill switch — ExecuteAsync must
        // return before scheduling any work. Without a test on this branch a
        // regression that swapped the negation (or moved the gate to a point
        // after factory.Create) would silently keep hammering GitHub even
        // when an operator explicitly disabled the sweep.
        var project = BuildGitHubProject();
        var factory = new ThrowingUpstreamFactory();
        var webhooks = new CapturingWebhookDispatcher();
        var disabled = new StalePullRequestSweeperOptions
        {
            Enabled = false,
            CheckInterval = TimeSpan.FromSeconds(30),
            BranchPrefix = "codeybox/",
        };
        var sweeper = new StalePullRequestSweeper(
            new InMemoryProjectRepository(project),
            factory,
            webhooks,
            disabled,
            NullLogger<StalePullRequestSweeper>.Instance);

        // StartAsync drives ExecuteAsync. With Enabled=false the body returns
        // immediately (no 5-s stagger delay, no PeriodicTimer). StopAsync
        // would otherwise wait for that initial Task.Delay if the gate were
        // broken — so a hang here is itself a regression signal.
        await sweeper.StartAsync(CancellationToken.None);
        await sweeper.StopAsync(CancellationToken.None);

        Assert.False(factory.CreateCalled);
        Assert.Empty(webhooks.Events);
    }

    [Fact]
    public async Task Sweep_DetectionLatencyWithinSlaAtDefaultInterval()
    {
        // SLA acceptance criterion #1: detection within 5 minutes when main
        // advances. With the default 60-s polling interval, a conflict
        // introduced just after a tick is detected on the very next tick
        // — well inside the 5-minute window. Test models this by driving
        // the sweep with a virtual time provider and confirming the detection
        // happens on the first sweep that observes the conflicting state.
        var project = BuildGitHubProject();
        var dirty = new UpstreamPullRequest
        {
            Number = 42,
            Url = "u",
            HeadBranch = "codeybox/x",
            HeadSha = "tip",
            BaseBranch = "main",
            HasMergeConflict = true,
        };
        var remote = new ScriptedUpstreamRemote(
            firstResponse: Array.Empty<UpstreamPullRequest>(),
            laterResponses: new[] { new[] { dirty } });
        var factory = new ScriptedUpstreamFactory(remote);
        var webhooks = new CapturingWebhookDispatcher();
        var time = new AdvancingTimeProvider(start: DateTimeOffset.Parse("2026-05-28T00:00:00Z"));
        var sweeper = new StalePullRequestSweeper(
            new InMemoryProjectRepository(project),
            factory,
            webhooks,
            DefaultOpts,
            NullLogger<StalePullRequestSweeper>.Instance,
            time);

        // First tick — PR not stale yet, no event.
        await sweeper.RunSweepAsync(CancellationToken.None);
        Assert.Empty(webhooks.Events);

        // ~60s later, main moved and the PR has gone stale.
        time.Advance(TimeSpan.FromSeconds(60));
        await sweeper.RunSweepAsync(CancellationToken.None);

        var evt = Assert.Single(webhooks.Events);
        var details = Assert.IsType<StalePullRequestDetails>(evt.Details);
        // FirstDetectedAt should reflect the sweep tick that first observed
        // the staleness — used by downstream trackers to compute "how long
        // has this PR been orphaned".
        Assert.Equal(
            DateTimeOffset.Parse("2026-05-28T00:01:00Z"),
            details.FirstDetectedAt);
        // Detection-to-staleness latency: 0 (same tick that observed the
        // dirty state fired the event). Whatever delay there is between the
        // GitHub push and our tick is bounded by CheckInterval = 60s ≪ 5 min.
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static StalePullRequestSweeper BuildSweeper(
        Project project,
        IUpstreamRemoteFactory factory,
        IWebhookDispatcher webhooks) =>
        new(
            new InMemoryProjectRepository(project),
            factory,
            webhooks,
            DefaultOpts,
            NullLogger<StalePullRequestSweeper>.Instance);
}

/// <summary>
/// Scripted IUpstreamRemote used by sweeper tests. Returns successive PR
/// snapshots in sequence; once exhausted, returns the last snapshot
/// indefinitely (mimics a steady-state where the forge keeps reporting
/// the same set).
/// </summary>
internal sealed class ScriptedUpstreamRemote : IUpstreamRemote
{
    private readonly Queue<IReadOnlyList<UpstreamPullRequest>> _responses = new();
    private IReadOnlyList<UpstreamPullRequest> _last;
    private readonly Exception? _throwOnList;
    public int ListCallCount { get; private set; }

    public ScriptedUpstreamRemote(params IReadOnlyList<UpstreamPullRequest>[] responses)
    {
        foreach (var r in responses) _responses.Enqueue(r);
        _last = responses.Length == 0 ? Array.Empty<UpstreamPullRequest>() : responses[^1];
    }

    public ScriptedUpstreamRemote(
        IReadOnlyList<UpstreamPullRequest> firstResponse,
        IReadOnlyList<UpstreamPullRequest>[] laterResponses)
    {
        _responses.Enqueue(firstResponse);
        foreach (var r in laterResponses) _responses.Enqueue(r);
        _last = laterResponses.Length == 0 ? firstResponse : laterResponses[^1];
    }

    public ScriptedUpstreamRemote(Exception throwOnList)
    {
        _throwOnList = throwOnList;
        _last = Array.Empty<UpstreamPullRequest>();
    }

    public string Name => "github";

    public Task<IReadOnlyList<UpstreamPullRequest>> ListOpenPullRequestsAsync(
        string branchPrefix, CancellationToken ct = default)
    {
        ListCallCount++;
        if (_throwOnList is not null) throw _throwOnList;
        var next = _responses.Count > 0 ? _responses.Dequeue() : _last;
        _last = next;
        return Task.FromResult(next);
    }

    public Task<UpstreamPushResult> PushAsync(string repositoryId, string branch, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<UpstreamCompletionOutcome> CompleteAsync(UpstreamCompletionRequest request, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<bool> TryMergeUpstreamBranchAsync(string targetBranch, string sourceBranch, CancellationToken ct = default)
        => throw new NotSupportedException();
}

internal sealed class ScriptedUpstreamFactory : IUpstreamRemoteFactory
{
    private readonly ScriptedUpstreamRemote _remote;
    public ScriptedUpstreamFactory(ScriptedUpstreamRemote remote) { _remote = remote; }
    public IUpstreamRemote Create(Project project) => _remote;
}

internal sealed class PerProjectScriptedUpstreamFactory : IUpstreamRemoteFactory
{
    private readonly IReadOnlyDictionary<string, ScriptedUpstreamRemote> _byProject;
    public PerProjectScriptedUpstreamFactory(IReadOnlyDictionary<string, ScriptedUpstreamRemote> byProject)
    {
        _byProject = byProject;
    }
    public IUpstreamRemote Create(Project project) =>
        _byProject.TryGetValue(project.Id.Value, out var r)
            ? r
            : throw new InvalidOperationException($"no scripted remote for project {project.Id.Value}");
}

internal sealed class ThrowingUpstreamFactory : IUpstreamRemoteFactory
{
    public bool CreateCalled { get; private set; }
    public IUpstreamRemote Create(Project project)
    {
        CreateCalled = true;
        throw new InvalidOperationException("factory should not be called for noop projects");
    }
}

/// <summary>
/// Advancing-clock TimeProvider used by the sweeper SLA test. The repo
/// already has an immutable <c>FakeTimeProvider</c> in router-test files; this
/// is the mutable variant the sweeper needs to drive elapsed time.
/// </summary>
internal sealed class AdvancingTimeProvider : TimeProvider
{
    private DateTimeOffset _now;
    public AdvancingTimeProvider(DateTimeOffset start) { _now = start; }
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
