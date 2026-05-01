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
/// Tests for the missing-audit-agent-credentials fallback. When a project
/// specifies <c>AuditAgent=gemini</c> but the credential provider returns
/// null for Gemini (simulating an unset <c>CODEYBOX_GEMINI_API_KEY</c>), the
/// pipeline must fall back to the work agent without crashing.
///
/// The spec says this should also warn at startup. Here we test both paths:
///   1. Per-pickup fallback: the audit iteration runs with the work agent.
///   2. No crash: the work item transitions to Done (not Failed).
/// </summary>
public sealed class MissingAuditAgentCredentialTests : IDisposable
{
    private readonly string _workspace;

    public MissingAuditAgentCredentialTests() =>
        _workspace = Directory.CreateTempSubdirectory("codeybox-cred-").FullName;

    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    // ── Per-pickup: fallback with no credentials ──────────────────────────────

    [Fact]
    public async Task MissingGeminiCredential_FallsBackToWorkAgent_NoCrash()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var llmAuditor = new ContextCapturingAuditor("security:llm-review",
            AuditCapabilities.AgentCredentials | AuditCapabilities.Network);

        // Gemini IS registered but has no credentials (null).
        using var tp = BuildPipeline(seed, [llmAuditor],
            auditAgent: AgentKind.Gemini,
            credentialsForGemini: false);   // null → triggers fallback

        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        // Must complete successfully, not crash.
        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        // Auditor ran with the work agent (Claude), not Gemini.
        Assert.Equal(AgentKind.Claude, llmAuditor.ObservedRunnerKind);
    }

    [Fact]
    public async Task MissingGeminiCredential_AuditAgentKind_IsNullInWebhook()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var llmAuditor = new ContextCapturingAuditor("security:llm-review",
            AuditCapabilities.AgentCredentials | AuditCapabilities.Network);

        using var tp = BuildPipeline(seed, [llmAuditor],
            auditAgent: AgentKind.Gemini,
            credentialsForGemini: false);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        // Cross-review was not active (fell through) so AuditAgentKind is null.
        var iterEvent = tp.Webhooks.Events.SingleOrDefault(e => e.Event == "work_item.audit_iteration");
        Assert.NotNull(iterEvent);
        var details = Assert.IsType<AuditIterationDetails>(iterEvent.Details);
        Assert.Null(details.AuditAgentKind);
    }

    [Fact]
    public async Task MissingGeminiCredential_PerAuditorAgent_FallsBackToWorkAgent()
    {
        // Fallback also applies when the missing-credential agent is specified
        // via PerAuditorAgent rather than the project-level AuditAgent.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var secAuditor = new ContextCapturingAuditor("security:llm-review",
            AuditCapabilities.AgentCredentials | AuditCapabilities.Network);

        using var tp = BuildPipeline(seed, [secAuditor],
            auditAgent: null,
            credentialsForGemini: false,    // Gemini has no credentials
            perAuditorAgent: new Dictionary<string, AgentKind>
            {
                ["security:llm-review"] = AgentKind.Gemini,
            });

        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(AgentKind.Claude, secAuditor.ObservedRunnerKind);
    }

    [Fact]
    public async Task GeminiCredentialPresent_CrossReviewProceedsNormally()
    {
        // Contrast test: credentials present → Gemini is used, no fallback.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var llmAuditor = new ContextCapturingAuditor("security:llm-review",
            AuditCapabilities.AgentCredentials | AuditCapabilities.Network);

        using var tp = BuildPipeline(seed, [llmAuditor],
            auditAgent: AgentKind.Gemini,
            credentialsForGemini: true);    // credentials present

        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.Equal(AgentKind.Gemini, llmAuditor.ObservedRunnerKind);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private CredTestPipeline BuildPipeline(
        string seedRepoUrl,
        IReadOnlyList<IAuditor> auditors,
        AgentKind? auditAgent,
        bool credentialsForGemini,
        IReadOnlyDictionary<string, AgentKind>? perAuditorAgent = null)
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
                PerAuditorAgent = perAuditorAgent ?? new Dictionary<string, AgentKind>(),
            },
        };

        var projects = new InMemoryProjectRepository(project);
        var presetCatalog = new ScriptedAuditorCatalog([.. auditors]);
        var composer = new ProjectAuditorComposer(presetCatalog);
        var credentials = new SelectiveCredentialProvider(
            credentialsForGemini ? AgentKind.Gemini : null);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, credentials, prs,
            projects, new TestUpstreamFactory(), composer,
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance);

        return new CredTestPipeline(pipeline, store, claudeAgent, webhooks);
    }

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "cred test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = "feature/x",
        PushUpstream = false,
    };
}

internal sealed class CredTestPipeline : IDisposable
{
    public PipelineRunner Pipeline { get; }
    public SqliteWorkItemStore Store { get; }
    public ScriptedAgent Agent { get; }
    public CapturingWebhookDispatcher Webhooks { get; }

    public CredTestPipeline(PipelineRunner pipeline, SqliteWorkItemStore store,
        ScriptedAgent agent, CapturingWebhookDispatcher webhooks)
    {
        Pipeline = pipeline;
        Store = store;
        Agent = agent;
        Webhooks = webhooks;
    }

    public void Dispose() => Store.Dispose();
}
