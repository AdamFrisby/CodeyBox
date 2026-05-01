using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Audit-loop integration tests using a scripted auditor.
///   - audit passes first iteration → straight to merge → Done
///   - audit fails then passes after rework → Done
///   - audit fails max iterations → AuditFailed (terminal)
///   - rework agent makes no changes → fail fast (Failed)
///   - no auditors registered → audit phase is a no-op
/// </summary>
public sealed class AuditPipelineIntegrationTests : IDisposable
{
    private readonly string _workspace;
    public AuditPipelineIntegrationTests() => _workspace = Directory.CreateTempSubdirectory("codeybox-audit-").FullName;
    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    [Fact]
    public async Task AuditPasses_FirstIteration_ReachesDone()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new ScriptedAuditor([new AuditOutcome(true, [])]);
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [auditor]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
    }

    [Fact]
    public async Task AuditFailsThenPassesAfterRework_ReachesDone()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new ScriptedAuditor(
        [
            new AuditOutcome(false, [new AuditFinding("Lint", AuditSeverity.Error, "needs fix", "x")]),
            new AuditOutcome(true, []),
        ]);
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [auditor]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v2-after-rework"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
    }

    [Fact]
    public async Task AuditFailsAllIterations_ReachesAuditFailed()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new ScriptedAuditor(
        [
            new AuditOutcome(false, [new AuditFinding("Lint", AuditSeverity.Error, "still broken", "x")]),
            new AuditOutcome(false, [new AuditFinding("Lint", AuditSeverity.Error, "still broken", "x")]),
            new AuditOutcome(false, [new AuditFinding("Lint", AuditSeverity.Error, "still broken", "x")]),
        ]);
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [auditor], maxAuditIterations: 3);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v2"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v3"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Contains("did not pass after 3 iterations", final.LastError);
    }

    [Fact]
    public async Task ReworkProducesNoChanges_FailsFast()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new ScriptedAuditor(
        [
            new AuditOutcome(false, [new AuditFinding("Lint", AuditSeverity.Error, "fix me", "x")]),
            new AuditOutcome(true, []),
        ]);
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [auditor]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "same-content"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "same-content"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("no changes", final.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoAuditorsRegistered_SkipsPhaseEntirely()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed); // no auditors
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "one"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
    }

    private sealed record AuditOutcome(bool Passed, IReadOnlyList<AuditFinding> Findings);

    private sealed class ScriptedAuditor : IAuditor
    {
        private readonly Queue<AuditOutcome> _plan;
        public ScriptedAuditor(IEnumerable<AuditOutcome> plan) { _plan = new Queue<AuditOutcome>(plan); }
        public string Name => "Scripted";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            if (_plan.Count == 0) throw new InvalidOperationException("no plan entries left");
            var outcome = _plan.Dequeue();
            return Task.FromResult(new AuditResult(outcome.Passed, outcome.Findings));
        }
    }

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "audit test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = "feature/x",
        PushUpstream = false,
    };
}
