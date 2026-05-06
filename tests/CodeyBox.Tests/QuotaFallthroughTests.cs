using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using CodeyBox.Upstream;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the audit-agent quota fallthrough path:
/// when the configured audit agent is below the quota threshold, the
/// pipeline falls through to the work agent. The
/// <c>audit.cross_review_active</c> event must NOT be emitted (visible via
/// <c>AuditIterationDetails.AuditAgentKind == null</c>), and the auditor
/// should observe the work agent kind in its context.
/// </summary>
public sealed class QuotaFallthroughTests : IDisposable
{
    private readonly string _workspace;

    public QuotaFallthroughTests() =>
        _workspace = Directory.CreateTempSubdirectory("codeybox-quota-").FullName;

    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    [Fact]
    public async Task AuditAgentExhausted_FallsThroughToWorkAgent()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var llmAuditor = new ContextCapturingAuditor("security:llm-review",
            AuditCapabilities.AgentCredentials | AuditCapabilities.Network);

        using var tp = BuildPipelineWithQuota(seed, [llmAuditor],
            auditAgent: AgentKind.Gemini,
            geminiQuotaPct: 2.0);  // well below 10 % threshold

        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        // Auditor ran with work agent, not Gemini.
        Assert.Equal(AgentKind.Claude, llmAuditor.ObservedRunnerKind);
    }

    [Fact]
    public async Task AuditAgentExhausted_CrossReviewActiveNotEmitted()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var llmAuditor = new ContextCapturingAuditor("security:llm-review",
            AuditCapabilities.AgentCredentials | AuditCapabilities.Network);

        using var tp = BuildPipelineWithQuota(seed, [llmAuditor],
            auditAgent: AgentKind.Gemini,
            geminiQuotaPct: 1.0);  // exhausted

        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        // AuditAgentKind must be null — cross-review was not active because
        // Gemini fell through to Claude.
        var iterEvent = tp.Webhooks.Events.SingleOrDefault(e => e.Event == "work_item.audit_iteration");
        Assert.NotNull(iterEvent);
        var details = Assert.IsType<AuditIterationDetails>(iterEvent.Details);
        Assert.Null(details.AuditAgentKind);
    }

    [Fact]
    public async Task AuditAgentAvailable_CrossReviewActiveEmitted()
    {
        // Sanity / contrast: when Gemini IS above the threshold, cross-review
        // fires and AuditAgentKind is populated.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var llmAuditor = new ContextCapturingAuditor("security:llm-review",
            AuditCapabilities.AgentCredentials | AuditCapabilities.Network);

        using var tp = BuildPipelineWithQuota(seed, [llmAuditor],
            auditAgent: AgentKind.Gemini,
            geminiQuotaPct: 80.0);  // well above threshold

        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var iterEvent = tp.Webhooks.Events.SingleOrDefault(e => e.Event == "work_item.audit_iteration");
        Assert.NotNull(iterEvent);
        var details = Assert.IsType<AuditIterationDetails>(iterEvent.Details);
        Assert.Equal("gemini", details.AuditAgentKind);
        Assert.Equal(AgentKind.Gemini, llmAuditor.ObservedRunnerKind);
    }

    [Fact]
    public async Task NoQuotaProbesWired_AuditAgentUsedWithoutGating()
    {
        // When no quota probes are injected, the audit-agent quota gate is not active.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var llmAuditor = new ContextCapturingAuditor("security:llm-review",
            AuditCapabilities.AgentCredentials | AuditCapabilities.Network);

        // Build pipeline WITHOUT injecting quota probes.
        using var tp = BuildPipelineWithoutQuota(seed, [llmAuditor], auditAgent: AgentKind.Gemini);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        // No quota gate → Gemini is used as configured.
        Assert.Equal(AgentKind.Gemini, llmAuditor.ObservedRunnerKind);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private QuotaTestPipeline BuildPipelineWithQuota(
        string seedRepoUrl,
        IReadOnlyList<IAuditor> auditors,
        AgentKind auditAgent,
        double geminiQuotaPct)
    {
        return Build(seedRepoUrl, auditors, auditAgent,
            auditProbes: [new FakeProbe(auditAgent, geminiQuotaPct)],
            quotaOptions: new QuotaRouterOptions { MinQuotaPct = 10.0 });
    }

    private QuotaTestPipeline BuildPipelineWithoutQuota(
        string seedRepoUrl,
        IReadOnlyList<IAuditor> auditors,
        AgentKind auditAgent)
        => Build(seedRepoUrl, auditors, auditAgent, auditProbes: null, quotaOptions: null);

    private QuotaTestPipeline Build(
        string seedRepoUrl,
        IReadOnlyList<IAuditor> auditors,
        AgentKind auditAgent,
        IEnumerable<IAgentQuotaProbe>? auditProbes,
        QuotaRouterOptions? quotaOptions)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var webhooks = new CapturingWebhookDispatcher();

        var claudeAgent = new ScriptedAgent([MergeStrategy.RealMerge]);
        var geminiRunner = new PassthroughRunner(AgentKind.Gemini);
        var registry = new AgentRegistry([claudeAgent, geminiRunner]);

        var auditTypes = auditors.Count > 0 ? new[] { "scripted" } : Array.Empty<string>();
        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            Audit = new ProjectAudit
            {
                MaxIterations = 3,
                AuditTypes = auditTypes,
                AuditAgent = auditAgent,
            },
        };

        var projects = new InMemoryProjectRepository(project);
        var presetCatalog = new ScriptedAuditorCatalog([.. auditors]);
        var composer = new ProjectAuditorComposer(presetCatalog);
        // Gemini always has credentials so we reach the quota check.
        var credentials = new SelectiveCredentialProvider(AgentKind.Gemini);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, credentials, prs,
            projects, new TestUpstreamFactory(), composer,
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            smokeGate: null,
            suggestions: null,
            auditQuotaProbes: auditProbes,
            auditQuotaOptions: quotaOptions);

        return new QuotaTestPipeline(pipeline, store, claudeAgent, webhooks);
    }

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "quota test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = "feature/x",
        PushUpstream = false,
    };
}

internal sealed class QuotaTestPipeline : IDisposable
{
    public PipelineRunner Pipeline { get; }
    public SqliteWorkItemStore Store { get; }
    public ScriptedAgent Agent { get; }
    public CapturingWebhookDispatcher Webhooks { get; }

    public QuotaTestPipeline(PipelineRunner pipeline, SqliteWorkItemStore store,
        ScriptedAgent agent, CapturingWebhookDispatcher webhooks)
    {
        Pipeline = pipeline;
        Store = store;
        Agent = agent;
        Webhooks = webhooks;
    }

    public void Dispose() => Store.Dispose();
}
