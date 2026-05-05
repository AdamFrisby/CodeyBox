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
/// Integration tests for the cross-agent review feature: a project configured
/// with <c>Agent=claude, AuditAgent=gemini</c> should invoke Gemini for LLM
/// auditors while Claude handles the work phase. Tool auditors and the rework
/// phase are unaffected.
///
/// All fake runners record their invocations to allow targeted assertions.
/// </summary>
public sealed class CrossReviewIntegrationTests : IDisposable
{
    private readonly string _workspace;

    public CrossReviewIntegrationTests() =>
        _workspace = Directory.CreateTempSubdirectory("codeybox-crossrev-").FullName;

    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    // ── Core cross-review scenario ────────────────────────────────────────────

    [Fact]
    public async Task WorkUsesClaude_LlmAuditorsUseGemini_ToolAuditorsUnaffected()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var llmAuditor = new ContextCapturingAuditor("security:llm-review", AuditCapabilities.AgentCredentials | AuditCapabilities.Network);
        var toolAuditor = new ContextCapturingAuditor("format-check", AuditCapabilities.None);

        using var tp = BuildCrossReviewPipeline(seed,
            auditors: [llmAuditor, toolAuditor],
            auditAgent: AgentKind.Gemini);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        // Work phase used Claude (the work runner).
        Assert.Contains(tp.Recorders.Keys, k => k == AgentKind.Claude);
        Assert.True(tp.Recorders[AgentKind.Claude].WorkInvocations > 0, "Claude should have run the work phase");

        // LLM auditor got Gemini.
        Assert.Equal(AgentKind.Gemini, llmAuditor.ObservedRunnerKind);

        // Tool auditor's AuditRunner reflects the work runner sentinel (not Gemini),
        // proving the tool path was never cross-reviewed.
        Assert.NotEqual(AgentKind.Gemini, toolAuditor.ObservedRunnerKind);
    }

    // ── AuditAgentKind appears in the webhook payload ─────────────────────────

    [Fact]
    public async Task CrossReview_AuditAgentKind_AppearsInWebhookPayload()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var llmAuditor = new ContextCapturingAuditor("security:llm-review", AuditCapabilities.AgentCredentials | AuditCapabilities.Network);

        using var tp = BuildCrossReviewPipeline(seed, [llmAuditor], auditAgent: AgentKind.Gemini);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var iterationEvent = tp.Webhooks.Events
            .SingleOrDefault(e => e.Event == "work_item.audit_iteration");
        Assert.NotNull(iterationEvent);

        var details = Assert.IsType<AuditIterationDetails>(iterationEvent.Details);
        Assert.Equal("gemini", details.AuditAgentKind);
    }

    // ── No cross-review when AuditAgent not set: payload field is null ────────

    [Fact]
    public async Task NoCrossReview_AuditAgentKind_IsNullInWebhookPayload()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var llmAuditor = new ContextCapturingAuditor("security:llm-review", AuditCapabilities.AgentCredentials | AuditCapabilities.Network);

        using var tp = BuildCrossReviewPipeline(seed, [llmAuditor], auditAgent: null);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var iterationEvent = tp.Webhooks.Events
            .SingleOrDefault(e => e.Event == "work_item.audit_iteration");
        Assert.NotNull(iterationEvent);

        var details = Assert.IsType<AuditIterationDetails>(iterationEvent.Details);
        Assert.Null(details.AuditAgentKind);
    }

    // ── PerAuditorAgent routes specific auditors to different agents ──────────

    [Fact]
    public async Task PerAuditorAgent_RoutesSpecificAuditorToOverrideAgent()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var secAuditor = new ContextCapturingAuditor("security:llm-review", AuditCapabilities.AgentCredentials | AuditCapabilities.Network);
        var compAuditor = new ContextCapturingAuditor("completeness:llm-review", AuditCapabilities.AgentCredentials | AuditCapabilities.Network);

        using var tp = BuildCrossReviewPipeline(seed, [secAuditor, compAuditor],
            auditAgent: null,    // no project-level override
            perAuditorAgent: new Dictionary<string, AgentKind>
            {
                ["security:llm-review"] = AgentKind.Gemini,
                // completeness:llm-review falls through to work agent (Claude)
            });

        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.Equal(AgentKind.Gemini, secAuditor.ObservedRunnerKind);
        Assert.Equal(AgentKind.Claude, compAuditor.ObservedRunnerKind);
    }

    // ── Rework phase still uses the work agent ────────────────────────────────

    [Fact]
    public async Task ReworkPhase_AlwaysUsesWorkAgent()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        // First audit iteration fails → triggers rework → second iteration passes.
        var auditor = new ScriptedAuditor([
            new AuditOutcome(false, [new AuditFinding("Lint", AuditSeverity.Error, "fix me", "x")]),
            new AuditOutcome(true, []),
        ]);

        using var tp = BuildCrossReviewPipeline(seed, [auditor], auditAgent: AgentKind.Gemini);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v2-rework"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        // Rework (second work invocation) went to Claude, not Gemini.
        Assert.True(tp.Recorders[AgentKind.Claude].WorkInvocations >= 2,
            "Rework should also have used Claude");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private CrossReviewTestPipeline BuildCrossReviewPipeline(
        string seedRepoUrl,
        IReadOnlyList<IAuditor> auditors,
        AgentKind? auditAgent,
        IReadOnlyDictionary<string, AgentKind>? perAuditorAgent = null)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var webhooks = new CapturingWebhookDispatcher();

        // Claude (work agent) + Gemini (audit agent).
        var recorders = new Dictionary<AgentKind, RecordingAgent>();
        var claudeAgent = new ScriptedAgent([MergeStrategy.RealMerge]);
        var geminiRunner = new PassthroughRunner(AgentKind.Gemini);
        recorders[AgentKind.Claude] = new RecordingAgent(AgentKind.Claude, claudeAgent);
        var registry = new AgentRegistry([recorders[AgentKind.Claude], geminiRunner]);

        var auditTypes = auditors.Count > 0 ? new[] { "scripted" } : Array.Empty<string>();
        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            Audit = new ProjectAudit
            {
                MaxIterations = 3,
                AuditTypes = auditTypes,
                AuditAgent = auditAgent,
                PerAuditorAgent = perAuditorAgent ?? new Dictionary<string, AgentKind>(),
            },
        };

        var projects = new InMemoryProjectRepository(project);
        var presetCatalog = new ScriptedAuditorCatalog([.. auditors]);
        var composer = new ProjectAuditorComposer(presetCatalog);
        // Gemini has credentials; Claude uses the null provider (existing test convention).
        var credentials = new SelectiveCredentialProvider(AgentKind.Gemini);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, credentials, prs,
            projects, new TestUpstreamFactory(), composer,
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance);

        return new CrossReviewTestPipeline(pipeline, store, claudeAgent, recorders, webhooks);
    }

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "cross-review test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = "feature/x",
        PushUpstream = false,
    };

    private sealed record AuditOutcome(bool Passed, IReadOnlyList<AuditFinding> Findings);

    private sealed class ScriptedAuditor : IAuditor
    {
        private readonly Queue<AuditOutcome> _plan;
        public ScriptedAuditor(IEnumerable<AuditOutcome> plan) { _plan = new Queue<AuditOutcome>(plan); }
        public string Name => "ScriptedCrossReview";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            if (_plan.Count == 0) throw new InvalidOperationException("no plan entries left");
            var o = _plan.Dequeue();
            return Task.FromResult(new AuditResult(o.Passed, o.Findings));
        }
    }
}

/// <summary>
/// Wraps a real runner and counts work-phase invocations (prompts that don't
/// start with "# Merge task"). Used to verify which agent handled which phase.
/// </summary>
internal sealed class RecordingAgent : IAgentRunner
{
    private readonly IAgentRunner _inner;
    public AgentKind Kind { get; }
    public int WorkInvocations { get; private set; }

    public RecordingAgent(AgentKind kind, IAgentRunner inner)
    {
        Kind = kind;
        _inner = inner;
    }

    public async Task<AgentResult> RunAsync(ISandbox sandbox, string workingDirectory, string prompt,
        AgentCredential? credential, string? modelId = null, string? reasoningMode = null, CancellationToken ct = default)
    {
        if (!prompt.StartsWith("# Merge task", StringComparison.Ordinal))
            WorkInvocations++;
        return await _inner.RunAsync(sandbox, workingDirectory, prompt, credential, modelId, reasoningMode, ct);
    }
}

internal sealed class CrossReviewTestPipeline : IDisposable
{
    public PipelineRunner Pipeline { get; }
    public SqliteWorkItemStore Store { get; }
    public ScriptedAgent Agent { get; }
    public IReadOnlyDictionary<AgentKind, RecordingAgent> Recorders { get; }
    public CapturingWebhookDispatcher Webhooks { get; }

    public CrossReviewTestPipeline(PipelineRunner pipeline, SqliteWorkItemStore store, ScriptedAgent agent,
        Dictionary<AgentKind, RecordingAgent> recorders, CapturingWebhookDispatcher webhooks)
    {
        Pipeline = pipeline;
        Store = store;
        Agent = agent;
        Recorders = recorders;
        Webhooks = webhooks;
    }

    public void Dispose() => Store.Dispose();
}
