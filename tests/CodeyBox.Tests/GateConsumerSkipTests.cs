using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>
/// When the build gate fails, a <c>dotnet test --no-build</c> gate consumes
/// the compile gate's assemblies and can only surface a derived runner
/// refusal — so the pipeline must skip it with an explicit
/// "skipped: build failed" result and let the build gate's own Error drive a
/// normal rework turn. When the build passes, the consumer runs as today.
/// Regression test for the 2026-09-26 incident, where the test gate ran
/// against absent build outputs, vstest failed with
/// "The argument .../bin/.../CodeyBox.Tests.dll is invalid", and the item
/// failed TERMINALLY with failureKind=configuration instead of reworking the
/// real build error.
/// </summary>
[Collection("Pipeline integration")]
public sealed class GateConsumerSkipTests : IDisposable
{
    private readonly string _workspace;

    public GateConsumerSkipTests() =>
        _workspace = Directory.CreateTempSubdirectory("codeybox-gate-skip-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task BuildFails_NoBuildTestGateIsSkipped_ItemGoesToRework_NotFailed()
    {
        using var _ = TestSupport.AmbientGitConfigScope.Clear();
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var build = new ConsumingGateScriptedAuditor(
            "csharp:build-WaE",
            BuildTestGateEvidence.Build,
            BuildTestGateEvidence.None,
            [
                new GateOutcome(false, [new AuditFinding(
                    "csharp:build-WaE", AuditSeverity.Error,
                    "build failed", "error CS0000: boom")]),
                new GateOutcome(true, []),
            ]);
        var test = new ConsumingGateScriptedAuditor(
            "csharp:test-pass",
            BuildTestGateEvidence.Test,
            BuildTestGateEvidence.Build,
            [new GateOutcome(true, [])]);
        var involvement = new InMemoryAgentInvolvementStore();
        var captureStore = new CapturingGateReportStore();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [build, test],
            maxAuditIterations: 3,
            involvement: involvement,
            auditReportStore: captureStore);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "v1\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "v2\n"));

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "build fails, test gate skipped",
            Prompt = "change the repo",
            WorkBranch = "feature/gate-skip",
            BaseBranch = "main",
        };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);

        // The build ran both iterations; the --no-build consumer never ran
        // while its evidence was missing (iteration 1) and ran once the
        // build passed (iteration 2).
        Assert.Equal([1, 2], build.SeenIterations);
        Assert.Equal([2], test.SeenIterations);

        // A rework turn was driven by the build gate's own Error.
        var rows = await involvement.ListByWorkItemAsync(item.Id, CancellationToken.None);
        Assert.Contains(rows, r => string.Equals(r.Phase, "rework", StringComparison.Ordinal));
        var iterations = await tp.Store.GetIterationsAsync(item.Id, CancellationToken.None);
        Assert.Contains(iterations, i => i.Iteration == 2);

        // The skip was explicit, not a silent non-run: iteration 1 persists
        // a test-gate report carrying the "skipped: build failed" finding.
        var skippedReport = Assert.Single(
            captureStore.Reports,
            r => r.Iteration == 1 && string.Equals(r.AuditorName, "csharp:test-pass", StringComparison.Ordinal));
        Assert.Contains(
            skippedReport.Findings,
            f => f.Title.Contains("skipped: build failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildPasses_NoBuildTestGateRunsAsToday()
    {
        using var _ = TestSupport.AmbientGitConfigScope.Clear();
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var build = new ConsumingGateScriptedAuditor(
            "csharp:build-WaE",
            BuildTestGateEvidence.Build,
            BuildTestGateEvidence.None,
            [new GateOutcome(true, [])]);
        var test = new ConsumingGateScriptedAuditor(
            "csharp:test-pass",
            BuildTestGateEvidence.Test,
            BuildTestGateEvidence.Build,
            [new GateOutcome(true, [])]);
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [build, test],
            maxAuditIterations: 3);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "v1\n"));

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "build passes, test gate runs",
            Prompt = "change the repo",
            WorkBranch = "feature/gate-run",
            BaseBranch = "main",
        };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([1], build.SeenIterations);
        Assert.Equal([1], test.SeenIterations);
    }

    private sealed record GateOutcome(bool Passed, IReadOnlyList<AuditFinding> Findings);

    private sealed class CapturingGateReportStore : IAuditReportStore
    {
        public List<AuditReport> Reports { get; } = [];

        public Task CreateAsync(AuditReport report, CancellationToken ct = default)
        {
            Reports.Add(report);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditReport>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AuditReport>>(Reports.Where(r => r.WorkItemId == workItemId).ToList());

        public Task<string?> GetRawOutputAsync(string workItemId, AuditTarget target, int iteration, string auditorName, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
            => Task.FromResult(0);
    }

    /// <summary>
    /// Scripted BuildTestGate auditor that also stamps consumed gate
    /// evidence, standing in for a <c>dotnet test --no-build</c> gate that
    /// reuses the compile gate's assemblies.
    /// </summary>
    private sealed class ConsumingGateScriptedAuditor(
        string name,
        BuildTestGateEvidence produces,
        BuildTestGateEvidence consumes,
        IEnumerable<GateOutcome> plan) : IAuditor
    {
        private readonly Queue<GateOutcome> _plan = new(plan);

        public string Name { get; } = name;
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public AuditorRole Role => AuditorRole.BuildTestGate;
        public BuildTestGateEvidence BuildTestGateEvidence => produces;
        public BuildTestGateEvidence ConsumesGateEvidence => consumes;
        public List<int> SeenIterations { get; } = [];

        public Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
        {
            _ = sandbox;
            _ = workingDirectory;
            _ = ct;
            if (_plan.Count == 0)
                throw new InvalidOperationException($"no plan entries left for {Name}");
            SeenIterations.Add(context.Iteration);
            var outcome = _plan.Dequeue();
            return Task.FromResult(new AuditResult(outcome.Passed, outcome.Findings));
        }
    }
}
