using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox.Process;
using CodeyBox.Upstream;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that PipelineRunner writes cost rows via IWorkItemCostStore when
/// an IAgentCostExtractor returns a snapshot, and does not throw or write rows
/// when no extractor is registered or the extractor returns null.
/// </summary>
[Collection("Pipeline integration")]
public sealed class PipelineRunnerCostCaptureTests : IDisposable
{
    private readonly string _workspace;

    public PipelineRunnerCostCaptureTests()
        => _workspace = Directory.CreateTempSubdirectory("codeybox-cost-pipeline-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task SuccessfulRun_WritesWorkPhaseCostRow()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var costStore = new RecordingCostStore();
        using var tp = BuildPipelineWithCosts(_workspace, seed, costStore);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("cost-test.txt", "cost\n"));

        var item = NewItem("feature/cost-work");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var workRows = costStore.Recorded.Where(r => r.Phase == "work").ToList();
        Assert.Single(workRows);
    }

    [Fact]
    public async Task SuccessfulRun_WritesMergePhaseCostRow()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var costStore = new RecordingCostStore();
        using var tp = BuildPipelineWithCosts(_workspace, seed, costStore);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("merge-cost.txt", "merge\n"));

        var item = NewItem("feature/cost-merge");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var mergeRows = costStore.Recorded.Where(r => r.Phase == "merge").ToList();
        Assert.Single(mergeRows);
        Assert.Equal(2, costStore.Recorded.Count);
    }

    [Fact]
    public async Task MissingExtractor_NoCostRow()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var costStore = new RecordingCostStore();
        using var tp = BuildPipelineWithCosts(_workspace, seed, costStore, registerExtractor: false);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("no-extractor.txt", "x\n"));

        var item = NewItem("feature/no-extractor");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.Empty(costStore.Recorded);
    }

    [Fact]
    public async Task ToolAuditors_DoNotProduceCostRows()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var costStore = new RecordingCostStore();
        // Configure a tool-only auditor (no AgentCredentials) that always passes.
        using var tp = BuildPipelineWithCosts(_workspace, seed, costStore,
            auditors: [new PassingToolAuditor()], maxAuditIterations: 1);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("audit-tool.txt", "a\n"));

        var item = NewItem("feature/tool-audit");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        // Tool auditors (AuditCapabilities.None, no LLM) must not produce cost rows.
        var auditRows = costStore.Recorded.Where(r => r.Phase == "audit").ToList();
        Assert.Empty(auditRows);
    }

    [Fact]
    public async Task ReworkPhase_WritesReworkCostRow()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var costStore = new RecordingCostStore();
        // Auditor that fails on iteration 1, passes on iteration 2 → triggers one rework.
        using var tp = BuildPipelineWithCosts(_workspace, seed, costStore,
            auditors: [new OnceFailingAuditor()], maxAuditIterations: 2);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("cost-rework-work.txt", "work\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("cost-rework-rework.txt", "rework\n"));

        var item = NewItem("feature/cost-rework");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var reworkRows = costStore.Recorded.Where(r => r.Phase == "rework").ToList();
        Assert.Single(reworkRows);
        Assert.Equal(1, reworkRows[0].Iteration);
    }

    private static WorkItem NewItem(string branch) => new()
    {
        Id = new WorkItemId(Guid.NewGuid()),
        ProjectId = new ProjectId("test-project"),
        Title = "Cost capture test",
        Prompt = "write a file",
        State = WorkItemState.Queued,
        WorkBranch = branch,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        WorkTimeout = TimeSpan.FromMinutes(5),
        MergeTimeout = TimeSpan.FromMinutes(5),
    };

    [Fact]
    public async Task LlmAuditor_ProducesAuditPhaseCostRow()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var costStore = new RecordingCostStore();
        using var tp = BuildPipelineWithCosts(_workspace, seed, costStore,
            auditors: [new FakeLlmAuditor()], maxAuditIterations: 1);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("llm-audit.txt", "a\n"));

        var item = NewItem("feature/llm-audit");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var auditRows = costStore.Recorded.Where(r => r.Phase == "audit").ToList();
        Assert.Single(auditRows);
    }

    [Fact]
    public async Task CostStoreFailure_DoesNotAbortPipeline()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var throwingStore = new ThrowingCostStore();
        using var tp = BuildPipelineWithCosts(_workspace, seed, throwingStore);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("fail-soft.txt", "x\n"));

        var item = NewItem("feature/fail-soft");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static TestPipeline BuildPipelineWithCosts(
        string workspace,
        string seedRepoUrl,
        IWorkItemCostStore costStore,
        bool registerExtractor = true,
        IReadOnlyList<IAuditor>? auditors = null,
        int maxAuditIterations = 1)
    {
        var gitRoot = Path.Combine(workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var agent = new ScriptedAgent([MergeStrategy.RealMerge]);
        var registry = new AgentRegistry([agent]);

        var auditorList = auditors ?? [];
        var auditTypes = auditorList.Count > 0 ? new[] { "scripted" } : Array.Empty<string>();

        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            Audit = new ProjectAudit { MaxIterations = maxAuditIterations, AuditTypes = auditTypes },
        });

        var composer = new ProjectAuditorComposer(new ScriptedAuditorCatalog(auditorList));
        var upstreamFactory = new TestUpstreamFactory();
        var calculator = new AgentCostCalculator(new AgentPricingOptions());

        IReadOnlyDictionary<AgentKind, IAgentCostExtractor>? extractors = null;
        if (registerExtractor)
        {
            var fake = new FakeCostExtractor { Kind = AgentKind.Claude };
            extractors = new Dictionary<AgentKind, IAgentCostExtractor> { [AgentKind.Claude] = fake };
        }

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, upstreamFactory, composer,
            store,
            new NullWebhookDispatcher(),
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            timingStore: null,
            costStore: costStore,
            costExtractors: extractors,
            costCalculator: calculator);

        return new TestPipeline(pipeline, store, agent, gitHost, gitRoot);
    }

    // ── Scripted auditors ─────────────────────────────────────────────────────

    private sealed class PassingToolAuditor : IAuditor
    {
        public string Name => "passing-tool";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct)
            => Task.FromResult(new AuditResult(true, []));
    }

    private sealed class OnceFailingAuditor : IAuditor
    {
        private int _callCount;
        public string Name => "once-failing";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct)
        {
            _callCount++;
            if (_callCount == 1)
                return Task.FromResult(new AuditResult(false, [
                    new AuditFinding("once-failing", AuditSeverity.Error, "Test failure", "First audit always fails"),
                ]));
            return Task.FromResult(new AuditResult(true, []));
        }
    }

    // ── Fake extractor ────────────────────────────────────────────────────────

    private sealed class FakeCostExtractor : IAgentCostExtractor
    {
        public AgentKind Kind { get; init; }

        public AgentCostSnapshot? TryExtract(string? stdout, string? stderr)
            => new(InputTokens: 1000, CachedInputTokens: 100, OutputTokens: 200, ModelId: "fake-model");
    }

    // ── Recording cost store ──────────────────────────────────────────────────

    internal sealed class RecordingCostStore : IWorkItemCostStore
    {
        private readonly List<WorkItemCost> _recorded = [];

        public IReadOnlyList<WorkItemCost> Recorded
        {
            get { lock (_recorded) return [.. _recorded]; }
        }

        public Task RecordAsync(WorkItemCost cost, CancellationToken ct = default)
        {
            lock (_recorded) _recorded.Add(cost);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkItemCost>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkItemCost>>(
                Recorded.Where(r => r.WorkItemId == workItemId).ToList());

        public Task<IReadOnlyList<WorkItemCost>> GetByProjectAsync(
            string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkItemCost>>([]);

        public Task<IReadOnlyList<(string ProjectId, double TotalUsd)>> GetFleetCostSummaryAsync(
            DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(string, double)>>([]);

        public Task DeleteByWorkItemAsync(string workItemId, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    // ── Fake LLM auditor (needsCreds=true path) ───────────────────────────────

    private sealed class FakeLlmAuditor : IAuditor
    {
        public string Name => "fake-llm";
        public string Kind => "llm";
        public AuditCapabilities Required => AuditCapabilities.AgentCredentials;

        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct)
            => Task.FromResult(new AuditResult(true, [], RawOutput: "fake llm output"));
    }

    // ── Throwing cost store (fail-soft test) ──────────────────────────────────

    private sealed class ThrowingCostStore : IWorkItemCostStore
    {
        public Task RecordAsync(WorkItemCost cost, CancellationToken ct = default)
            => throw new InvalidOperationException("injected cost store failure");

        public Task<IReadOnlyList<WorkItemCost>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkItemCost>>([]);

        public Task<IReadOnlyList<WorkItemCost>> GetByProjectAsync(
            string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkItemCost>>([]);

        public Task<IReadOnlyList<(string ProjectId, double TotalUsd)>> GetFleetCostSummaryAsync(
            DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(string, double)>>([]);

        public Task DeleteByWorkItemAsync(string workItemId, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
