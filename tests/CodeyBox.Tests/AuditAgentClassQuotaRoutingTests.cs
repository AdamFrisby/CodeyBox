using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Gemini;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;

namespace CodeyBox.Tests;

/// <summary>
/// Audit-phase agent routing must consult the same quota state the work-phase
/// router uses. Before bug 779e7dc9 the audit pipeline would pick the
/// configured audit agent without checking the class chain, hit quota mid-call,
/// and park the entire work item — even when another class member (codex)
/// was available and would have served fine. These tests pin the fix at the
/// router level: the audit pipeline now walks the class chain on quota
/// exhaustion before deciding whether to skip the auditor for the iteration.
/// </summary>
[Collection("Pipeline integration")]
public sealed class AuditAgentClassQuotaRoutingTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-audit-route-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    // ── Acceptance #1: gemini exhausted, codex OK → codex runs the auditor ─

    [Fact]
    public async Task GeminiExhausted_CodexAvailable_AuditRoutesToCodex()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        using var fix = BuildFixture(seed, auditor,
            classMembers: [AgentKind.Gemini, AgentKind.Codex],
            quotas: new() { [AgentKind.Gemini] = 1.0, [AgentKind.Codex] = 80.0 });
        fix.Codex!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([AgentKind.Codex], auditor.Invocations);
    }

    // ── Acceptance #2: gemini + claude exhausted, codex OK → codex runs ────

    [Fact]
    public async Task GeminiAndClaudeExhausted_CodexAvailable_AuditRoutesToCodex()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        using var fix = BuildFixture(seed, auditor,
            classMembers: [AgentKind.Gemini, AgentKind.Claude, AgentKind.Codex],
            quotas: new()
            {
                [AgentKind.Gemini] = 1.0,
                [AgentKind.Claude] = 2.0,
                [AgentKind.Codex] = 80.0,
            });
        fix.Codex!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([AgentKind.Codex], auditor.Invocations);
    }

    // Acceptance criterion #3 (all members exhausted → auditor skipped) is
    // covered by AuditQuotaPauseTests.AuditLlmQuotaFailure_AllClassMembersExhausted_SkipsLlmAuditorAndContinues
    // — it exercises the mid-iteration path via TerminalQuotaError + the
    // wrapper's AgentClassExhaustedException; the LLM task body catches the
    // exception and treats it as a skip rather than parking.

    // ── Harness ─────────────────────────────────────────────────────────────

    private RoutingFixture BuildFixture(
        string seedRepoUrl,
        RecordingLlmAuditor auditor,
        IReadOnlyList<AgentKind> classMembers,
        Dictionary<AgentKind, double>? quotas = null)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var webhooks = new CapturingWebhookDispatcher();

        var agents = classMembers.Select(k => new ScriptableAgent(k)).ToList();
        var gemini = agents.FirstOrDefault(a => a.Kind == AgentKind.Gemini);
        var codex = agents.FirstOrDefault(a => a.Kind == AgentKind.Codex);
        var claude = agents.FirstOrDefault(a => a.Kind == AgentKind.Claude);
        var registry = new AgentRegistry([.. agents]);

        var frontier = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members = classMembers
                .Select((kind, idx) => new AgentMembership
                {
                    Agent = kind,
                    Billing = AgentBilling.Subscription,
                    // Use descending QualityScore by config order so the router's
                    // tie-break still puts the first-listed member first.
                    QualityScore = 100 - idx,
                })
                .ToList(),
        };

        var probes = classMembers
            .Select(kind => (IAgentQuotaProbe)new ConfigurableProbe(
                kind,
                quotas?.GetValueOrDefault(kind, 80.0) ?? 80.0))
            .ToList();

        var router = new AgentClassRouter(
            [frontier],
            probes,
            new QuotaRouterOptions { MinQuotaPct = 10.0 },
            NullLogger<AgentClassRouter>.Instance);

        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Codex,
            DefaultAgentClass = "frontier",
            Audit = new ProjectAudit
            {
                MaxIterations = 1,
                AuditTypes = ["scripted"],
                // Configure gemini as the LLM auditor: the bug repro requires
                // the preferred audit agent to be exhausted so the router
                // falls through to the class chain.
                AuditAgent = AgentKind.Gemini,
                MaxLlmAuditorParallelism = 1,
            },
        };

        var projects = new InMemoryProjectRepository(project);
        var fallbackHistory = new InMemoryAgentFallbackHistoryStore();

        var pipeline = new PipelineRunner(
            sandboxes,
            gitHost,
            registry,
            new StaticCredentialProvider(),
            prs,
            projects,
            new TestUpstreamFactory(),
            new ProjectAuditorComposer(new ScriptedAuditorCatalog([auditor])),
            store,
            webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            auditQuotaProbes: probes,
            auditQuotaOptions: new QuotaRouterOptions { MinQuotaPct = 10.0 },
            classRouter: router,
            fallbackHistory: fallbackHistory,
            quotaClassifier: new CompositeQuotaFailureClassifier(
            [
                new ClaudeQuotaFailureDetector(),
                new CodexQuotaFailureDetector(),
                new GeminiQuotaFailureDetector(),
            ]));

        return new RoutingFixture(pipeline, store, webhooks, gemini, codex, claude);
    }

    private static WorkItem NewItem(AgentKind agent) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "audit routing test",
        Prompt = "do thing",
        BaseBranch = "main",
        Agent = agent,
        AgentClassId = "frontier",
        PushUpstream = false,
    };

    private sealed class RecordingLlmAuditor : IAuditor
    {
        public RecordingLlmAuditor(string name) { Name = name; }
        public string Name { get; }
        public string Kind => "llm";
        public AuditCapabilities Required => AuditCapabilities.AgentCredentials;
        public List<AgentKind> Invocations { get; } = [];

        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            var agent = context.AuditRunner?.Kind ?? AgentKind.Claude;
            Invocations.Add(agent);
            return Task.FromResult(new AuditResult(true, []));
        }
    }

    private sealed class ConfigurableProbe : IAgentQuotaProbe
    {
        private double _pct;
        public ConfigurableProbe(AgentKind kind, double initialPct) { Kind = kind; _pct = initialPct; }
        public AgentKind Kind { get; }
        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
            => Task.FromResult(new AgentQuotaSnapshot { AvailablePct = _pct });
        public Task MarkExhaustedAsync(AgentMembership member, TimeSpan ttl, DateTimeOffset? resetAt = null, CancellationToken ct = default)
        {
            _pct = 0.0;
            return Task.CompletedTask;
        }
    }

    private sealed record RoutingFixture(
        PipelineRunner Pipeline,
        SqliteWorkItemStore Store,
        CapturingWebhookDispatcher Webhooks,
        ScriptableAgent? Gemini,
        ScriptableAgent? Codex,
        ScriptableAgent? Claude) : IDisposable
    {
        public void Dispose() => Store.Dispose();
    }
}
