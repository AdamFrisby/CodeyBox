using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox.Process;
using CodeyBox.Upstream;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end coverage for the PipelineRunner → IWebhookDispatcher wiring of
/// the usage / usage-total blocks: the cost-capture tests verify that rows are
/// written, but a separate plausible-regression — forgetting (or breaking) the
/// <c>Usage = iterUsage?.Iteration</c> assignments on the WebhookEvents emitted
/// from PipelineRunner.cs:1665 and PipelineRunner.cs:4005 — would slip past
/// those, the pure aggregator tests, and the HTTP-surface tests. This file
/// pins the production call sites: a real pipeline run with a recording cost
/// store and a capturing webhook dispatcher must dispatch work_item.done with
/// Usage and UsageTotal populated, and work_item.audit_iteration must carry
/// the same on the iteration boundary.
/// </summary>
[Collection("Pipeline integration")]
public sealed class PipelineRunnerUsagePublishTests : IDisposable
{
    private readonly string _workspace;

    public PipelineRunnerUsagePublishTests()
        => _workspace = Directory.CreateTempSubdirectory("codeybox-usage-publish-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task SuccessfulRun_DoneWebhook_CarriesUsageAndUsageTotal()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var costStore = new PipelineRunnerCostCaptureTests.RecordingCostStore();
        var webhooks = new CapturingWebhookDispatcher();
        using var tp = BuildPipeline(_workspace, seed, costStore, webhooks);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("usage-emit.txt", "x\n"));

        var item = MakeItem("feature/usage-emit");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var done = webhooks.Events.LastOrDefault(e => e.Event == "work_item.done");
        Assert.NotNull(done);
        // The fake extractor reports tokens; the calculator estimates USD. The
        // exact values don't matter here — we are pinning that the WebhookEvent
        // dispatch site populated Usage / UsageTotal at all.
        Assert.NotNull(done!.Usage);
        Assert.NotNull(done.UsageTotal);
        Assert.True(done.Usage!.TokensInput > 0,
            "Usage should reflect at least the work-phase fake extractor's input tokens");
        Assert.True(done.UsageTotal!.TokensInput >= done.Usage.TokensInput,
            "UsageTotal must aggregate ≥ the latest iteration");
    }

    [Fact]
    public async Task ReworkRun_AuditIterationWebhook_CarriesUsageDeltaPerIteration()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var costStore = new PipelineRunnerCostCaptureTests.RecordingCostStore();
        var webhooks = new CapturingWebhookDispatcher();
        using var tp = BuildPipeline(_workspace, seed, costStore, webhooks,
            auditors: [new OnceFailingAuditor()], maxAuditIterations: 2);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("rework-emit-1.txt", "work\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("rework-emit-2.txt", "rework\n"));

        var item = MakeItem("feature/rework-emit");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        // Each audit iteration emits one work_item.audit_iteration event with
        // its own usage delta (running totals up to that point). Both must
        // have Usage and UsageTotal set.
        var auditEvents = webhooks.Events.Where(e => e.Event == "work_item.audit_iteration").ToList();
        Assert.Equal(2, auditEvents.Count);
        foreach (var e in auditEvents)
        {
            Assert.NotNull(e.Usage);
            Assert.NotNull(e.UsageTotal);
            Assert.True(e.UsageTotal!.TokensInput >= e.Usage!.TokensInput);
        }
        // Cumulative totals are non-decreasing across iterations.
        Assert.True(auditEvents[1].UsageTotal!.TokensInput >= auditEvents[0].UsageTotal!.TokensInput);
    }

    [Fact]
    public async Task NoCostExtractor_DoneWebhook_CarriesElapsedFallbackUsage()
    {
        // Mirrors a deployment where IAgentCostExtractor is not registered for
        // the work agent. The pipeline still writes an elapsed-only row so agent
        // activity is visible in webhooks even when token counts are unavailable.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var costStore = new PipelineRunnerCostCaptureTests.RecordingCostStore();
        var webhooks = new CapturingWebhookDispatcher();
        using var tp = BuildPipeline(_workspace, seed, costStore, webhooks, registerExtractor: false);

        tp.Agent.BeforeWorkAsync = async (_, _, ct) => await Task.Delay(25, ct);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("no-extractor-emit.txt", "x\n"));

        var item = MakeItem("feature/no-extractor-emit");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var done = webhooks.Events.LastOrDefault(e => e.Event == "work_item.done");
        Assert.NotNull(done);
        Assert.NotNull(done!.Usage);
        Assert.NotNull(done.UsageTotal);
        Assert.Equal(0, done.Usage!.TokensInput);
        Assert.Equal(0, done.Usage.TokensOutput);
        Assert.Equal(0, done.Usage.CostUsd);
        Assert.True(done.Usage.ElapsedMs > 0);
        Assert.Equal(done.Usage.ElapsedMs, done.UsageTotal!.ElapsedMs);

        var workRow = Assert.Single(costStore.Recorded, r => r.Phase == "work");
        Assert.False(workRow.HasExtractedTokenUsage);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static WorkItem MakeItem(string branch) => new()
    {
        Id = new WorkItemId(Guid.NewGuid()),
        ProjectId = new ProjectId("test-project"),
        Title = "Usage publish test",
        Prompt = "write a file",
        State = WorkItemState.Queued,
        WorkBranch = branch,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        WorkTimeout = TimeSpan.FromMinutes(5),
        MergeTimeout = TimeSpan.FromMinutes(5),
    };

    private static TestPipeline BuildPipeline(
        string workspace,
        string seedRepoUrl,
        IWorkItemCostStore costStore,
        IWebhookDispatcher webhooks,
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
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);

        IReadOnlyDictionary<AgentKind, IAgentCostExtractor>? extractors = null;
        if (registerExtractor)
            extractors = new Dictionary<AgentKind, IAgentCostExtractor>
            {
                [AgentKind.Claude] = new FakeCostExtractor(),
            };

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, upstreamFactory, composer,
            store,
            webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            timingStore: null,
            costStore: costStore,
            costExtractors: extractors,
            costCalculator: calculator,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);

        return new TestPipeline(pipeline, store, agent, gitHost, gitRoot);
    }

    private sealed class FakeCostExtractor : IAgentCostExtractor
    {
        public AgentKind Kind => AgentKind.Claude;
        public AgentCostSnapshot? TryExtract(string? stdout, string? stderr)
            => new(InputTokens: 1000, CachedInputTokens: 100, OutputTokens: 200, ModelId: "fake-model");
        public ModelRateConfig? DefaultPricing => null;
    }

    private sealed class OnceFailingAuditor : IAuditor
    {
        private int _calls;
        public string Name => "once-failing-usage";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct)
        {
            _calls++;
            if (_calls == 1)
                return Task.FromResult(new AuditResult(false, [
                    new AuditFinding(Name, AuditSeverity.Error, "force rework", "iteration 1 always fails"),
                ]));
            return Task.FromResult(new AuditResult(true, []));
        }
    }
}
