using System.Threading;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>
/// Delivery-reconciliation integration tests for sig-pr422-delivery-reconciliation.
///
/// These tests run the real <see cref="PipelineRunner"/> against the real
/// SQLite <see cref="IWorkItemStore"/> and assert on persisted work-item state
/// — never on mocked outcome objects. The only scripted seam is the forge
/// edge (<see cref="Pr422ScriptedUpstreamRemote"/>), which replays the outcome
/// shapes the GitHub remote can now produce (verified merge, unidentified or
/// unmerged PR, transport failure). The GitHub remote's own fake-HTTP tests
/// prove those shapes arise from the right wire conditions; these tests prove
/// the pipeline only reaches <see cref="WorkItemState.Done"/> — and only
/// releases dependents — on verified delivery.
/// </summary>
[Collection("Pipeline integration")]
public sealed class Pr422DeliveryReconciliationTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-pr422-").FullName;

    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    private static WorkItem NewItem(string workBranch, IReadOnlyList<WorkItemId>? dependsOn = null) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = workBranch,
        PushUpstream = true,
        DependsOn = dependsOn ?? [],
    };

    private const string ForgeMergeSha = "abc123def4567890abc123def4567890abc12345";

    [Fact]
    public async Task UnverifiedBranchPushOutcome_NeverReachesDone_AndHoldsDependents()
    {
        // Simulates the old bug shape end to end: the forge edge reports a
        // bare branch push with no PR identity and no merge proof (what every
        // 422 used to produce). The item must park with an actionable
        // diagnostic — never Done — and a dependent must stay gated.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var remote = new Pr422ScriptedUpstreamRemote("github")
        {
            OnComplete = _ => new UpstreamCompletionOutcome
            {
                BranchPushed = true,
                Notes = "PR creation skipped (422 — branch may already have an open PR)",
            },
        };

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            upstream: new ProjectUpstream { Kind = "github", AutoMerge = true, MergeMethod = "squash" },
            upstreamFactory: new Pr422UpstreamRemoteFactory(remote),
            mergeStrategy: [MergeStrategy.RealMerge]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("unverified.txt", "unverified\n"));

        var leader = NewItem("feature/pr422-unverified");
        var follower = NewItem("feature/pr422-follower", [leader.Id]);
        await tp.Store.CreateAsync(leader);
        await tp.Store.CreateAsync(follower);
        await tp.Pipeline.RunAsync(leader, CancellationToken.None);

        var final = await tp.Store.GetAsync(leader.Id);
        Assert.NotNull(final);
        Assert.NotEqual(WorkItemState.Done, final!.State);
        Assert.Equal(WorkItemState.Failed, final.State);
        Assert.NotNull(final.LastError);
        Assert.Contains("unverified", final.LastError);
        Assert.Null(final.MergedPrNumber);

        // Dependency semantics: a Failed parent is terminal but NOT
        // satisfying, so the follower can never be picked up for its own run.
        var states = new Dictionary<WorkItemId, WorkItemState>
        {
            [leader.Id] = final.State,
            [follower.Id] = (await tp.Store.GetAsync(follower.Id))!.State,
        };
        Assert.False(WorkItemDependencies.AreSatisfied(follower.DependsOn, states));
    }

    [Fact]
    public async Task VerifiedMerge_ReachesDone_WithFreshAudit_AndReleasesDependents()
    {
        // Happy path with teeth: an approving auditor runs in this delivery
        // run (fresh, applicable audit for the delivered content) and the
        // forge edge returns PR identity plus the authoritative merge sha.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingApprovingAuditor();
        // The audit_reports table references work_items(id), so the report
        // store must live on the same state DB file the pipeline uses.
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var auditStore = new SqliteAuditReportStore(stateDb);
        var remote = new Pr422ScriptedUpstreamRemote("github")
        {
            OnComplete = _ => new UpstreamCompletionOutcome
            {
                BranchPushed = true,
                PullRequestUrl = "https://example.invalid/owner/repo/pull/7",
                PullRequestNumber = 7,
                MergedSha = ForgeMergeSha,
            },
        };

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            auditors: [auditor],
            upstream: new ProjectUpstream { Kind = "github", AutoMerge = true, MergeMethod = "squash" },
            upstreamFactory: new Pr422UpstreamRemoteFactory(remote),
            mergeStrategy: [MergeStrategy.RealMerge],
            stateDbPathOverride: stateDb,
            auditReportStore: auditStore);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("verified.txt", "verified\n"));

        var leader = NewItem("feature/pr422-verified");
        var follower = NewItem("feature/pr422-verified-follower", [leader.Id]);
        await tp.Store.CreateAsync(leader);
        await tp.Store.CreateAsync(follower);
        await tp.Pipeline.RunAsync(leader, CancellationToken.None);

        var final = await tp.Store.GetAsync(leader.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(ForgeMergeSha, final.MergeSha);
        Assert.Equal(7, final.MergedPrNumber);

        // The audit ran in this delivery run against the delivered content —
        // delivery did not bypass the audit gates.
        Assert.True(auditor.Invocations > 0);
        var reports = await auditStore.GetByWorkItemAsync(leader.Id.ToString(), AuditTarget.Code, CancellationToken.None);
        Assert.NotEmpty(reports);

        // A Done parent satisfies the dependent's gate: dependents release
        // only on verified delivery.
        var states = new Dictionary<WorkItemId, WorkItemState>
        {
            [leader.Id] = final.State,
            [follower.Id] = (await tp.Store.GetAsync(follower.Id))!.State,
        };
        Assert.True(WorkItemDependencies.AreSatisfied(follower.DependsOn, states));
    }

    [Fact]
    public async Task AutoMergeFalse_OpenPr_RemainsDoneCompatible()
    {
        // Intentional AutoMerge=false operation: an identified open PR with no
        // merge yet is the configured completion result and stays Done.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var remote = new Pr422ScriptedUpstreamRemote("github")
        {
            OnComplete = _ => new UpstreamCompletionOutcome
            {
                BranchPushed = true,
                PullRequestUrl = "https://example.invalid/owner/repo/pull/9",
                PullRequestNumber = 9,
            },
        };

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            upstream: new ProjectUpstream { Kind = "github", AutoMerge = false, MergeMethod = "squash" },
            upstreamFactory: new Pr422UpstreamRemoteFactory(remote),
            mergeStrategy: [MergeStrategy.RealMerge]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("noautomerge.txt", "noautomerge\n"));

        var item = NewItem("feature/pr422-no-automerge");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Null(final.MergeSha);
        Assert.Equal(9, final.MergedPrNumber);
    }

    [Fact]
    public async Task GenericUpstream_BranchPush_RemainsDoneCompatible()
    {
        // The verified-merge requirement is GitHub-specific: a generic-git
        // upstream has no PR concept, so a bare branch push still completes.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var remote = new Pr422ScriptedUpstreamRemote("git-generic")
        {
            OnComplete = _ => new UpstreamCompletionOutcome { BranchPushed = true },
        };

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            upstream: new ProjectUpstream { Kind = "git-generic", AutoMerge = true, MergeMethod = "merge" },
            upstreamFactory: new Pr422UpstreamRemoteFactory(remote),
            mergeStrategy: [MergeStrategy.RealMerge]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("generic.txt", "generic\n"));

        var item = NewItem("feature/pr422-generic");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
    }

    [Fact]
    public async Task RestartWithPersistedPrIdentity_ReusesPrInsteadOfDuplicating()
    {
        // Process restart between create/merge/persist: the first attempt
        // opened PR #77 and persisted its number before going away. The
        // resumed run must continue against #77 (no duplicate create) and
        // only report Done with a verified merge sha.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var remote = new Pr422ScriptedUpstreamRemote("github")
        {
            OnComplete = request => new UpstreamCompletionOutcome
            {
                BranchPushed = true,
                PullRequestUrl = "https://example.invalid/owner/repo/pull/77",
                PullRequestNumber = 77,
                MergedSha = ForgeMergeSha,
            },
        };

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            upstream: new ProjectUpstream { Kind = "github", AutoMerge = true, MergeMethod = "squash" },
            upstreamFactory: new Pr422UpstreamRemoteFactory(remote),
            mergeStrategy: [MergeStrategy.RealMerge]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("restart.txt", "restart\n"));

        var item = NewItem("feature/pr422-restart");
        await tp.Store.CreateAsync(item);
        // Simulate the pre-crash evidence row: PR identity reached the
        // persistent store but the merge/persist/Done steps never ran.
        await tp.Store.UpdateAsync(item with { MergedPrNumber = 77, MergedPrUrl = "https://example.invalid/owner/repo/pull/77" });

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.NotEmpty(remote.Requests);
        Assert.All(remote.Requests, r => Assert.Equal(77, r.ExistingPullRequestNumber));
        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(ForgeMergeSha, final.MergeSha);
        Assert.Equal(77, final.MergedPrNumber);
    }

    [Fact]
    public async Task UnmergedOpenPrOutcome_ParksWithPrEvidencePersisted()
    {
        // A reconciled-but-unmerged open PR under AutoMerge=true: delivery is
        // unverified, so the item parks — but the PR identity is persisted as
        // reconciliation evidence for the operator and for restart recovery.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var remote = new Pr422ScriptedUpstreamRemote("github")
        {
            OnComplete = _ => new UpstreamCompletionOutcome
            {
                BranchPushed = true,
                PullRequestUrl = "https://example.invalid/owner/repo/pull/8",
                PullRequestNumber = 8,
                Notes = "PR reconciled but auto-merge produced no verified sha",
            },
        };

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            upstream: new ProjectUpstream { Kind = "github", AutoMerge = true, MergeMethod = "squash" },
            upstreamFactory: new Pr422UpstreamRemoteFactory(remote),
            mergeStrategy: [MergeStrategy.RealMerge]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("openpr.txt", "openpr\n"));

        var item = NewItem("feature/pr422-openparked");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.NotEqual(WorkItemState.Done, final!.State);
        Assert.Equal(8, final.MergedPrNumber);
        Assert.Equal("https://example.invalid/owner/repo/pull/8", final.MergedPrUrl);
        Assert.NotNull(final.LastError);
        Assert.Contains("#8", final.LastError);
    }

    [Fact]
    public async Task ThrowingRemote_ParksAfterBoundedAttemptsWithoutDone()
    {
        // Every attempt throws (e.g. 422 validation with no match, or a lost
        // response that never reconciles): the loop must stay bounded and the
        // item must park — never Done, never an uncontrolled retry loop.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var remote = new Pr422ScriptedUpstreamRemote("github")
        {
            OnComplete = _ => throw new InvalidOperationException(
                "GitHub POST /pulls returned 422 and no existing PR exactly matches head='feature/pr422-throwing' pushed revision"),
        };

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            upstream: new ProjectUpstream { Kind = "github", AutoMerge = true, MergeMethod = "squash" },
            upstreamFactory: new Pr422UpstreamRemoteFactory(remote),
            mergeStrategy: [MergeStrategy.RealMerge],
            pipelineOptions: new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
                UpstreamPushMaxAttempts = 2,
                UpstreamPushBackoff = TimeSpan.FromMilliseconds(1),
            });
        tp.Agent.WorkPlan.Enqueue(new FileWrite("throwing.txt", "throwing\n"));

        var item = NewItem("feature/pr422-throwing");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.NotEqual(WorkItemState.Done, final!.State);
        // Bounded: exactly the configured attempt budget was consumed.
        Assert.Equal(2, remote.Requests.Count);
        Assert.NotNull(final.LastError);
        Assert.Contains("2 attempts", final.LastError);
    }

    private sealed class RecordingApprovingAuditor : IAuditor
    {
        public string Name => "recording-approver";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public int Invocations;
        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Invocations);
            return Task.FromResult(new AuditResult(true, []));
        }
    }
}

/// <summary>
/// Configurable-named upstream fake for delivery-reconciliation tests.
/// Unlike <see cref="RacingUpstreamRemote"/> (which models the 405 race), this
/// fake replays caller-supplied outcome shapes — including the unverified
/// shapes the fixed GitHub remote can no longer produce but the pipeline must
/// still refuse to complete on (defence in depth at the Done gate).
/// </summary>
internal sealed class Pr422ScriptedUpstreamRemote(string name) : IUpstreamRemote
{
    public string Name => name;
    public List<UpstreamCompletionRequest> Requests { get; } = new();
    public Func<UpstreamCompletionRequest, UpstreamCompletionOutcome>? OnComplete { get; set; }

    public Task<UpstreamPushResult> PushAsync(string repositoryId, string branch, CancellationToken ct = default)
        => Task.FromResult(new UpstreamPushResult(true, null));

    public Task<UpstreamCompletionOutcome> CompleteAsync(UpstreamCompletionRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(OnComplete?.Invoke(request) ?? new UpstreamCompletionOutcome { BranchPushed = true });
    }

    public Task<bool> TryMergeUpstreamBranchAsync(string targetBranch, string sourceBranch, CancellationToken ct = default)
        => Task.FromResult(true);
}

internal sealed class Pr422UpstreamRemoteFactory(Pr422ScriptedUpstreamRemote remote) : IUpstreamRemoteFactory
{
    public IUpstreamRemote Create(Project project) => remote;
}
