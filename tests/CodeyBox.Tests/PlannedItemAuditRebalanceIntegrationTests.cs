using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end coverage for the planning loop's implementation-side rebalance and
/// its planned-vs-unplanned measurement, driven through the real audit loop.
///   - a PLANNED item's approach-reviewer error is demoted to advisory -> merges
///   - the SAME error on an UNPLANNED item still blocks -> AuditFailed
///   - the code-audit iteration + first-audit metrics carry the planned cohort tag
/// </summary>
[Collection("GlobalSerilog")]
public sealed class PlannedItemAuditRebalanceIntegrationTests : IDisposable
{
    private const string SamplePlan =
        "{\"approach\":\"add retries\",\"files\":[\"a.txt\"],\"testStrategy\":[\"t\"]," +
        "\"risks\":[\"r\"],\"satisfiesTask\":\"adds retries\"}";

    private readonly string _workspace;
    public PlannedItemAuditRebalanceIntegrationTests() =>
        _workspace = Directory.CreateTempSubdirectory("codeybox-plan-rebalance-").FullName;
    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    private static WorkItem BaseItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "rebalance test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = "feature/x",
        PushUpstream = false,
    };

    private static WorkItem PlannedItem() => BaseItem() with
    {
        PlanArtifact = SamplePlan,
        PlanGeneratedAt = DateTimeOffset.UtcNow,
        PlanReviewedAt = DateTimeOffset.UtcNow,
        // Must carry the current provenance prefix so HasReviewedPlanArtifact
        // treats this as a genuinely reviewed-and-approved plan.
        PlanReviewSummary = "auditor-loop/v1: approved by panel",
    };

    private static AuditFinding ArchError() =>
        new("architecture:llm-review", AuditSeverity.Error, "approach smell", "restructure this");

    [Fact]
    public async Task PlannedItem_ApproachReviewerError_IsDemotedToAdvisory_AndMerges()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var arch = new ScriptedAuditor([new AuditOutcome(false, [ArchError()])], name: "architecture:llm-review");
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [arch], maxAuditIterations: 1);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = PlannedItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        // Demoted -> zero blocking -> audit passes -> merge -> Done, with a single
        // audit iteration (no rework triggered by the architecture finding).
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([1], arch.SeenIterations);
    }

    [Fact]
    public async Task UnplannedItem_SameApproachReviewerError_StillBlocks()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var arch = new ScriptedAuditor([new AuditOutcome(false, [ArchError()])], name: "architecture:llm-review");
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [arch], maxAuditIterations: 1);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = BaseItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        // No plan -> no demotion -> the architecture error blocks and, at the
        // one-iteration cap, the item fails the audit.
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
    }

    [Fact]
    public async Task PlannedItem_EmitsAuditIterationAndFirstAuditMetrics_WithPlannedOnTag()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new ScriptedAuditor([new AuditOutcome(true, [])]);
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [auditor]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        using var metrics = new MetricCapture("codeybox.audit.iterations", "codeybox.audit.first_audit.outcome");
        var item = PlannedItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.True(metrics.Any("codeybox.audit.iterations", ("outcome", "passed"), ("planned", "on")));
        Assert.True(metrics.Any("codeybox.audit.first_audit.outcome", ("outcome", "passed"), ("planned", "on")));
    }

    [Fact]
    public async Task UnplannedItem_EmitsMetrics_WithPlannedOffTag()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new ScriptedAuditor([new AuditOutcome(true, [])]);
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [auditor]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        using var metrics = new MetricCapture("codeybox.audit.iterations", "codeybox.audit.first_audit.outcome");
        var item = BaseItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.True(metrics.Any("codeybox.audit.iterations", ("outcome", "passed"), ("planned", "off")));
        Assert.True(metrics.Any("codeybox.audit.first_audit.outcome", ("outcome", "passed"), ("planned", "off")));
    }

    private sealed record AuditOutcome(bool Passed, IReadOnlyList<AuditFinding> Findings);

    private sealed class ScriptedAuditor : IAuditor
    {
        private readonly Queue<AuditOutcome> _plan;
        public ScriptedAuditor(IEnumerable<AuditOutcome> plan, string name = "Scripted")
        {
            _plan = new Queue<AuditOutcome>(plan);
            Name = name;
        }
        public string Name { get; }
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public List<int> SeenIterations { get; } = [];
        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            if (_plan.Count == 0) throw new InvalidOperationException("no plan entries left");
            SeenIterations.Add(context.Iteration);
            var outcome = _plan.Dequeue();
            return Task.FromResult(new AuditResult(outcome.Passed, outcome.Findings));
        }
    }
}
