using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Tests;

namespace CodeyBox.Tests.Uat.PipelineAndWorkerLifecycle;

/// <summary>
/// UAT coverage for <c>Work item pipeline state machine - Runs Work, Audit, Merge, and UpstreamPush phases in order</c>.
/// Plan anchor: docs/uat/00-plan.md#work-item-pipeline-state-machine---runs-work-audit-merge-and-upstreampush-phases-in-order
/// </summary>
[Collection("Pipeline integration")]
public sealed class PipelineStateMachineTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-uat-pipeline-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    [Fact]
    public async Task QueuedItem_RunsWorkAuditMergeAndUpstreamInOrder()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var upstreamFactory = new CapturingUpstreamFactory();
        using var context = PipelineLifecycleUatHelpers.BuildPipeline(
            _workspace,
            seed,
            auditors: [new PassingAuditor()],
            upstream: new ProjectUpstream { Kind = "uat-upstream" },
            upstreamFactory: upstreamFactory);
        context.Agent.WorkPlan.Enqueue(new FileWrite("result.txt", "pipeline completed\n"));
        var item = PipelineLifecycleUatHelpers.NewItem("feature/full-pipeline") with
        {
            PushUpstream = true,
        };
        await context.Store.CreateAsync(item);

        await context.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await context.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.False(string.IsNullOrWhiteSpace(final.MergeSha));
        var lifecycleEvents = context.Webhooks.Events
            .Select(e => e.Event)
            .Where(e => e != "work_item.audit_iteration"
                && !e.StartsWith("iteration.", StringComparison.Ordinal)
                && !e.StartsWith("audit.", StringComparison.Ordinal)
                && !e.StartsWith("merge.", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(
            [
                "work_item.working",
                "work_item.work_complete",
                "work_item.auditing",
                "work_item.audit_passed",
                "work_item.merging",
                "work_item.merged",
                "work_item.upstream_pushing",
                "work_item.pull_request_opened",
                "work_item.done",
            ],
            lifecycleEvents);
        var request = Assert.Single(upstreamFactory.Remote.Requests);
        Assert.Equal(final.MergeSha, request.MergeSha);
    }

    [Fact]
    public async Task NoAuditorsAndPushUpstreamFalse_SkipsAuditAndUpstreamButStillMerges()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var upstreamFactory = new CapturingUpstreamFactory();
        using var context = PipelineLifecycleUatHelpers.BuildPipeline(
            _workspace,
            seed,
            upstream: new ProjectUpstream { Kind = "uat-upstream" },
            upstreamFactory: upstreamFactory);
        context.Agent.WorkPlan.Enqueue(new FileWrite("local-only.txt", "merged locally\n"));
        var item = PipelineLifecycleUatHelpers.NewItem("feature/local-only") with
        {
            PushUpstream = false,
        };
        await context.Store.CreateAsync(item);

        await context.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await context.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.DoesNotContain(context.Webhooks.Events, e => e.Event == "work_item.auditing");
        Assert.DoesNotContain(context.Webhooks.Events, e => e.Event == "work_item.upstream_pushing");
        Assert.Empty(upstreamFactory.Remote.Requests);

        var barePath = Path.Combine(context.GitRoot, item.Id + ".git");
        var (_, blob, _) = await TestSupport.RunGit(barePath, "show", "main:local-only.txt");
        Assert.Equal("merged locally\n", blob);
    }

    [Fact]
    public async Task ResumeFromMerged_UsesStoredMergeShaWhenCompletingUpstream()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var upstreamFactory = new CapturingUpstreamFactory();
        using var context = PipelineLifecycleUatHelpers.BuildPipeline(
            _workspace,
            seed,
            upstream: new ProjectUpstream { Kind = "uat-upstream" },
            upstreamFactory: upstreamFactory);
        var storedMergeSha = new string('a', 40);
        var item = PipelineLifecycleUatHelpers.NewItem("feature/resume-upstream", WorkItemState.Merged) with
        {
            PushUpstream = true,
            MergeSha = storedMergeSha,
        };
        await context.Store.CreateAsync(item);

        await context.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await context.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        var request = Assert.Single(upstreamFactory.Remote.Requests);
        Assert.Equal(item.Id, request.WorkItemId);
        Assert.Equal(["work_item.upstream_pushing", "work_item.pull_request_opened", "work_item.done"],
            context.Webhooks.Events.Select(e => e.Event).ToArray());
    }

    [Fact]
    public async Task AuditorBlocksPastMaxIterations_WithReworkProgress_ParksForOperator()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new ScriptedUatAuditor(
        [
            new AuditResult(false, [new AuditFinding("uat", AuditSeverity.Error, "still broken", "fix required")]),
            new AuditResult(false, [new AuditFinding("uat", AuditSeverity.Error, "still broken", "fix required")]),
        ]);
        using var context = PipelineLifecycleUatHelpers.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            maxAuditIterations: 2);
        context.Agent.WorkPlan.Enqueue(new FileWrite("audit.txt", "first attempt\n"));
        context.Agent.WorkPlan.Enqueue(new FileWrite("audit.txt", "second attempt\n"));
        var item = PipelineLifecycleUatHelpers.NewItem("feature/audit-fails");
        await context.Store.CreateAsync(item);

        await context.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await context.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, final!.State);
        Assert.Contains("parked for operator review", final.LastError);
        Assert.DoesNotContain(context.Webhooks.Events, e => e.Event == "work_item.audit_failed");
        var escalation = Assert.Single(context.Webhooks.Events, e => e.Event == "work_item.needs_operator_input");
        var details = Assert.IsType<AuditMaxIterationsEscalationDetails>(escalation.Details);
        Assert.Contains("work_branch_tip_changed", details.ProgressSignals);

        var barePath = Path.Combine(context.GitRoot, item.Id + ".git");
        var (_, blob, _) = await TestSupport.RunGit(barePath, "show", $"{final.WorkBranch}:audit.txt");
        Assert.Equal("second attempt\n", blob);
    }
}
