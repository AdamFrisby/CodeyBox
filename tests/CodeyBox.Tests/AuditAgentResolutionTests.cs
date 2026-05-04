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
/// Tests for the audit-agent resolution hierarchy implemented in
/// <c>PipelineRunner.ResolveAuditAgentRunnerAsync</c>. The method is exercised
/// indirectly by running a full audit iteration and asserting which agent kind
/// the context-capturing auditor observed.
///
/// Precedence under test:
///   PerAuditorAgent[name]  >  AuditAgent  >  work agent
///
/// Fallback cases:
///   - Unregistered audit agent → falls back to work agent
///   - No credentials for audit agent → falls back to work agent
/// </summary>
public sealed class AuditAgentResolutionTests : IDisposable
{
    private readonly string _workspace;

    public AuditAgentResolutionTests() =>
        _workspace = Directory.CreateTempSubdirectory("codeybox-audit-res-").FullName;

    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    // ── No override → work agent passes through ──────────────────────────────

    [Fact]
    public async Task NoAuditAgent_LlmAuditorSeesWorkAgent()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var llmAuditor = new ContextCapturingAuditor("llm", AuditCapabilities.AgentCredentials | AuditCapabilities.Network);

        using var tp = BuildCrossReviewPipeline(seed, [llmAuditor],
            workAgent: AgentKind.Claude,
            auditAgent: null,            // no override
            credentialsForGemini: false);

        await RunItemAsync(tp);

        Assert.Equal(AgentKind.Claude, llmAuditor.ObservedRunnerKind);
    }

    // ── AuditAgent override ──────────────────────────────────────────────────

    [Fact]
    public async Task AuditAgentSet_LlmAuditorSeesAuditAgent()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var llmAuditor = new ContextCapturingAuditor("security:llm-review", AuditCapabilities.AgentCredentials | AuditCapabilities.Network);

        using var tp = BuildCrossReviewPipeline(seed, [llmAuditor],
            workAgent: AgentKind.Claude,
            auditAgent: AgentKind.Gemini,
            credentialsForGemini: true);

        await RunItemAsync(tp);

        Assert.Equal(AgentKind.Gemini, llmAuditor.ObservedRunnerKind);
    }

    // ── PerAuditorAgent takes precedence over AuditAgent ────────────────────

    [Fact]
    public async Task PerAuditorAgent_TakesPrecedenceOverAuditAgent()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var secAuditor = new ContextCapturingAuditor("security:llm-review", AuditCapabilities.AgentCredentials | AuditCapabilities.Network);
        var compAuditor = new ContextCapturingAuditor("completeness:llm-review", AuditCapabilities.AgentCredentials | AuditCapabilities.Network);

        // AuditAgent = Gemini (applies to completeness), but security is
        // overridden back to Claude via PerAuditorAgent.
        using var tp = BuildCrossReviewPipeline(seed, [secAuditor, compAuditor],
            workAgent: AgentKind.Claude,
            auditAgent: AgentKind.Gemini,
            credentialsForGemini: true,
            perAuditorAgent: new Dictionary<string, AgentKind>
            {
                ["security:llm-review"] = AgentKind.Claude,
            });

        await RunItemAsync(tp);

        Assert.Equal(AgentKind.Claude, secAuditor.ObservedRunnerKind);    // per-auditor wins
        Assert.Equal(AgentKind.Gemini, compAuditor.ObservedRunnerKind);   // falls to AuditAgent
    }

    // ── Tool auditors are never affected ────────────────────────────────────

    [Fact]
    public async Task ToolAuditors_NeverReceiveAuditRunner()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var toolAuditor = new ContextCapturingAuditor("format-check", AuditCapabilities.None);

        using var tp = BuildCrossReviewPipeline(seed, [toolAuditor],
            workAgent: AgentKind.Claude,
            auditAgent: AgentKind.Gemini,
            credentialsForGemini: true);

        await RunItemAsync(tp);

        // Tool auditor always sees the work runner injected as sentinel;
        // the key assertion: AuditCapabilities.None path never triggers
        // cross-review, and the AuditRunner in the ctx carries work agent.
        Assert.Equal(AgentKind.Claude, toolAuditor.ObservedRunnerKind);
    }

    // ── Unregistered audit agent falls back to work agent ───────────────────

    [Fact]
    public async Task UnregisteredAuditAgent_FallsBackToWorkAgent()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var llmAuditor = new ContextCapturingAuditor("review", AuditCapabilities.AgentCredentials | AuditCapabilities.Network);

        // AuditAgent = Codex, but Codex runner is NOT registered — only Claude.
        using var tp = BuildCrossReviewPipeline(seed, [llmAuditor],
            workAgent: AgentKind.Claude,
            auditAgent: AgentKind.Codex,    // not in registry
            credentialsForGemini: false,
            registerGemini: false,
            registerCodex: false);          // Codex deliberately absent

        await RunItemAsync(tp);

        Assert.Equal(AgentKind.Claude, llmAuditor.ObservedRunnerKind);
    }

    // ── Missing credentials falls back to work agent ─────────────────────────

    [Fact]
    public async Task MissingAuditAgentCredentials_FallsBackToWorkAgent()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var llmAuditor = new ContextCapturingAuditor("review", AuditCapabilities.AgentCredentials | AuditCapabilities.Network);

        // Gemini IS registered but its credentials are null (unset env var).
        using var tp = BuildCrossReviewPipeline(seed, [llmAuditor],
            workAgent: AgentKind.Claude,
            auditAgent: AgentKind.Gemini,
            credentialsForGemini: false);   // null credentials → fallback

        await RunItemAsync(tp);

        Assert.Equal(AgentKind.Claude, llmAuditor.ObservedRunnerKind);
    }

    // ── All combinations: both PerAuditorAgent AND missing creds fallback ────

    [Fact]
    public async Task PerAuditorAgent_MissingCredentials_FallsBackToWorkAgent()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var llmAuditor = new ContextCapturingAuditor("security:llm-review", AuditCapabilities.AgentCredentials | AuditCapabilities.Network);

        using var tp = BuildCrossReviewPipeline(seed, [llmAuditor],
            workAgent: AgentKind.Claude,
            auditAgent: null,
            credentialsForGemini: false,
            perAuditorAgent: new Dictionary<string, AgentKind>
            {
                ["security:llm-review"] = AgentKind.Gemini,  // per-auditor → Gemini
            });

        // Gemini credentials are null → falls back to Claude.
        await RunItemAsync(tp);

        Assert.Equal(AgentKind.Claude, llmAuditor.ObservedRunnerKind);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private TestPipelineWithCapture BuildCrossReviewPipeline(
        string seedRepoUrl,
        IReadOnlyList<IAuditor> auditors,
        AgentKind workAgent,
        AgentKind? auditAgent,
        bool credentialsForGemini,
        bool registerGemini = true,
        bool registerCodex = false,
        IReadOnlyDictionary<string, AgentKind>? perAuditorAgent = null)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();

        var claudeAgent = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = workAgent };
        var runners = new List<IAgentRunner> { claudeAgent };
        if (registerGemini) runners.Add(new PassthroughRunner(AgentKind.Gemini));
        if (registerCodex) runners.Add(new PassthroughRunner(AgentKind.Codex));
        var registry = new AgentRegistry(runners);

        var auditTypes = auditors.Count > 0 ? new[] { "scripted" } : Array.Empty<string>();
        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = workAgent,
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
        var webhooks = new CapturingWebhookDispatcher();

        var credentials = new SelectiveCredentialProvider(
            credentialsForGemini ? AgentKind.Gemini : null);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, credentials, prs,
            projects, new TestUpstreamFactory(), composer,
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance);

        return new TestPipelineWithCapture(pipeline, store, claudeAgent, webhooks);
    }

    private static async Task RunItemAsync(TestPipelineWithCapture tp)
    {
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "cross-review test",
            Prompt = "do thing",
            BaseBranch = "main",
            WorkBranch = "feature/x",
            PushUpstream = false,
        };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);
    }
}

/// <summary>
/// Auditor that captures the <see cref="AuditContext.AuditRunner"/> it was
/// called with and returns a passing result. The <see cref="Required"/>
/// capabilities are configurable so the test can exercise both the LLM
/// (credentialed) and tool (credential-free) paths.
/// </summary>
internal sealed class ContextCapturingAuditor : IAuditor
{
    public string Name { get; }
    public string Kind => "tool";
    public AuditCapabilities Required { get; }
    public AgentKind? ObservedRunnerKind { get; private set; }

    public ContextCapturingAuditor(string name, AuditCapabilities required)
    {
        Name = name;
        Required = required;
    }

    public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
    {
        ObservedRunnerKind = context.AuditRunner?.Kind;
        return Task.FromResult(new AuditResult(true, []));
    }
}

/// <summary>
/// Agent runner stub that always succeeds without doing any file operations.
/// Used for the Gemini/Codex stubs in resolution tests where the runner must
/// be registered but is never actually invoked (the auditor captures the kind
/// from context instead).
/// </summary>
internal sealed class PassthroughRunner : IAgentRunner
{
    public AgentKind Kind { get; }
    public PassthroughRunner(AgentKind kind) => Kind = kind;

    public Task<AgentResult> RunAsync(ISandbox sandbox, string workingDirectory, string prompt,
        AgentCredential? credential, string? modelId = null, string? reasoningMode = null, CancellationToken ct = default)
        => Task.FromResult(new AgentResult(true, "pass", null, null));
}

/// <summary>
/// Credential provider that returns a non-null (empty) credential for one
/// specific agent kind, and null for everything else.
/// </summary>
internal sealed class SelectiveCredentialProvider : ICredentialProvider
{
    private readonly AgentKind? _grantedKind;

    public SelectiveCredentialProvider(AgentKind? grantedKind) => _grantedKind = grantedKind;

    public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
    {
        if (_grantedKind is not null && agent == _grantedKind.Value)
            return Task.FromResult<AgentCredential?>(
                new AgentCredential(agent, new Dictionary<string, string>(), new Dictionary<string, string>()));
        return Task.FromResult<AgentCredential?>(null);
    }
}

internal sealed class TestPipelineWithCapture : IDisposable
{
    public PipelineRunner Pipeline { get; }
    public SqliteWorkItemStore Store { get; }
    public ScriptedAgent Agent { get; }
    public CapturingWebhookDispatcher Webhooks { get; }

    public TestPipelineWithCapture(PipelineRunner pipeline, SqliteWorkItemStore store, ScriptedAgent agent, CapturingWebhookDispatcher webhooks)
    {
        Pipeline = pipeline;
        Store = store;
        Agent = agent;
        Webhooks = webhooks;
    }

    public void Dispose() => Store.Dispose();
}
