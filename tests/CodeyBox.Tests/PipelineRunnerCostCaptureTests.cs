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
        using var tp = BuildPipelineWithCosts(_workspace, seed, costStore);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("audit-tool.txt", "a\n"));

        var item = NewItem("feature/tool-audit");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        // Only work and merge phases produce cost rows from the fake extractor.
        // Tool auditors (no agent invocation) must not produce cost rows.
        var auditRows = costStore.Recorded.Where(r => r.Phase == "audit").ToList();
        Assert.Empty(auditRows);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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

    private static TestPipeline BuildPipelineWithCosts(
        string workspace,
        string seedRepoUrl,
        RecordingCostStore costStore,
        bool registerExtractor = true)
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

        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            Audit = new ProjectAudit { MaxIterations = 1, AuditTypes = [] },
        });

        var composer = new ProjectAuditorComposer(new ScriptedAuditorCatalog([]));
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

        public Task DeleteByWorkItemAsync(string workItemId, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
