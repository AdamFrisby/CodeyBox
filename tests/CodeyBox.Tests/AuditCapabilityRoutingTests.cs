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
/// Capability-based audit agent selection. With the operator tagging
/// agents <c>audit</c> in their class config, the audit phase must:
///
/// <list type="bullet">
///   <item>Route across ALL tagged members with quota-aware fallback (codex
///         exhausted → claude takes over; both available → either is fine).</item>
///   <item>NEVER pick a non-tagged member for auditing, even when it is the
///         only one with quota.</item>
///   <item>Honour <c>Project.Audit.AuditAgent</c> as the preferred primary
///         WHEN that agent is itself audit-capable; demote with a warning
///         when it is not.</item>
///   <item>Preserve legacy behaviour when no member of the class carries
///         the tag (backward compat).</item>
/// </list>
///
/// Mirrors the MOTIVATION from the work item: tagging both CLAUDE and CODEX
/// audit-capable removes the single-agent bottleneck that stalls audits
/// during quota crunches. GEMINI deliberately stays untagged.
/// </summary>
[Collection("Pipeline integration")]
public sealed class AuditCapabilityRoutingTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-audit-capability-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    // ── AC: codex exhausted, claude (audit-capable) takes over ──────────────

    [Fact]
    public async Task CodexExhausted_ClaudeAuditCapable_AuditRoutesToClaude()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        using var fix = BuildFixture(seed, auditor,
            members: [
                (AgentKind.Codex, IsAuditCapable: true,  QualityScore: 100),
                (AgentKind.Claude, IsAuditCapable: true, QualityScore: 90),
                (AgentKind.Gemini, IsAuditCapable: false, QualityScore: 95),
            ],
            quotas: new()
            {
                [AgentKind.Codex]  = 1.0,    // exhausted
                [AgentKind.Claude] = 80.0,   // healthy
                [AgentKind.Gemini] = 80.0,   // healthy but not audit-capable
            },
            preferredAuditAgent: AgentKind.Codex);

        fix.Codex!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));
        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        // The exhausted preferred (codex) is bypassed and the chain walk
        // picks the next audit-capable member (claude). Gemini is healthier
        // by quality score but NOT audit-capable, so it stays out of the pool.
        Assert.Equal([AgentKind.Claude], auditor.Invocations);
    }

    // ── AC: gemini is NEVER picked for audit even when only it has quota ────

    [Fact]
    public async Task OnlyGeminiHasQuota_GeminiNotAuditCapable_AuditSkipped()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        using var fix = BuildFixture(seed, auditor,
            members: [
                (AgentKind.Codex, IsAuditCapable: true,  QualityScore: 100),
                (AgentKind.Claude, IsAuditCapable: true, QualityScore: 90),
                (AgentKind.Gemini, IsAuditCapable: false, QualityScore: 95),
            ],
            quotas: new()
            {
                [AgentKind.Codex]  = 1.0,    // exhausted
                [AgentKind.Claude] = 2.0,    // exhausted
                [AgentKind.Gemini] = 80.0,   // healthy — but excluded from audit pool
            },
            preferredAuditAgent: AgentKind.Codex);

        fix.Codex!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));
        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        // Auditor never runs — gemini has quota but is excluded by the
        // audit-capability gate. Item still completes (legacy "skip auditor
        // rather than park" behaviour preserved).
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Empty(auditor.Invocations);
    }

    // ── AC: with both audit-capable members healthy, audit runs on preferred ──

    [Fact]
    public async Task BothAuditCapableHealthy_PreferredPrimaryRuns()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        using var fix = BuildFixture(seed, auditor,
            members: [
                (AgentKind.Codex, IsAuditCapable: true,  QualityScore: 100),
                (AgentKind.Claude, IsAuditCapable: true, QualityScore: 90),
            ],
            quotas: new()
            {
                [AgentKind.Codex]  = 80.0,
                [AgentKind.Claude] = 80.0,
            },
            preferredAuditAgent: AgentKind.Codex);

        fix.Codex!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));
        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([AgentKind.Codex], auditor.Invocations);
    }

    // ── AC: AuditAgent set to non-capable agent → demoted, pool runs ────────

    [Fact]
    public async Task AuditAgentSetToNonCapableAgent_DemotedToPool()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        // Operator misconfigured AuditAgent = Gemini, but Gemini is NOT
        // audit-capable in this class. The router must demote the preference
        // and pick an audit-capable member (claude or codex) instead.
        using var fix = BuildFixture(seed, auditor,
            members: [
                (AgentKind.Codex, IsAuditCapable: true,  QualityScore: 100),
                (AgentKind.Claude, IsAuditCapable: true, QualityScore: 90),
                (AgentKind.Gemini, IsAuditCapable: false, QualityScore: 95),
            ],
            quotas: new()
            {
                [AgentKind.Codex]  = 80.0,
                [AgentKind.Claude] = 80.0,
                [AgentKind.Gemini] = 80.0,
            },
            preferredAuditAgent: AgentKind.Gemini);

        fix.Codex!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));
        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        // Audit must NOT run on gemini — only audit-capable members are
        // eligible. Codex (highest QS audit-capable) takes over.
        Assert.Single(auditor.Invocations);
        Assert.DoesNotContain(AgentKind.Gemini, auditor.Invocations);
        Assert.Contains(auditor.Invocations[0], new[] { AgentKind.Codex, AgentKind.Claude });
    }

    // ── AC: no preferred AuditAgent + work agent not audit-capable → pool walk ──

    [Fact]
    public async Task NoPreferredAuditAgent_WorkAgentNotAuditCapable_WalksPool()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        // No AuditAgent set, work agent is gemini (not audit-capable). The
        // router must not fall through to workRunner (gemini); it must walk
        // the chain for an audit-capable substitute.
        using var fix = BuildFixture(seed, auditor,
            members: [
                (AgentKind.Gemini, IsAuditCapable: false, QualityScore: 100),
                (AgentKind.Claude, IsAuditCapable: true,  QualityScore: 90),
                (AgentKind.Codex, IsAuditCapable: true,   QualityScore: 80),
            ],
            quotas: new()
            {
                [AgentKind.Gemini] = 80.0,
                [AgentKind.Claude] = 80.0,
                [AgentKind.Codex]  = 80.0,
            },
            preferredAuditAgent: null,
            defaultAgent: AgentKind.Gemini);

        fix.Gemini!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));
        var item = NewItem(AgentKind.Gemini);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        // Auditor runs on the highest-QS audit-capable member (claude).
        Assert.Equal([AgentKind.Claude], auditor.Invocations);
    }

    // ── Backward compat: no member tagged → legacy unfiltered routing ───────

    [Fact]
    public async Task NoMemberTagged_LegacyRoutingPreserved()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        // NONE of the members carry the audit tag → the opt-in pool is
        // inactive and the audit phase behaves as before this feature.
        // Gemini (preferred) should run even though it isn't tagged.
        using var fix = BuildFixture(seed, auditor,
            members: [
                (AgentKind.Codex, IsAuditCapable: false,  QualityScore: 100),
                (AgentKind.Gemini, IsAuditCapable: false, QualityScore: 95),
            ],
            quotas: new()
            {
                [AgentKind.Codex]  = 80.0,
                [AgentKind.Gemini] = 80.0,
            },
            preferredAuditAgent: AgentKind.Gemini);

        fix.Codex!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));
        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        // Legacy behaviour: AuditAgent = Gemini runs even though it isn't
        // tagged audit-capable, because no member in the class opted in.
        Assert.Equal([AgentKind.Gemini], auditor.Invocations);
    }

    // ── Hot-reload: tagging a non-tagged class member flips the pool active ─

    [Fact]
    public void RouterCapabilityPool_HotReloadActivatesAndDeactivates()
    {
        // Start with NO tagged member: pool is null (legacy).
        var probes = new List<IAgentQuotaProbe>
        {
            new ConfigurableProbe(AgentKind.Claude, 80.0),
            new ConfigurableProbe(AgentKind.Codex, 80.0),
        };
        var router = new AgentClassRouter(
            [new AgentClass
            {
                Id = "frontier",
                DisplayName = "Frontier",
                Members =
                [
                    new AgentMembership
                    {
                        Agent = AgentKind.Claude,
                        Billing = AgentBilling.Subscription,
                        QualityScore = 100,
                    },
                    new AgentMembership
                    {
                        Agent = AgentKind.Codex,
                        Billing = AgentBilling.Subscription,
                        QualityScore = 90,
                    },
                ],
            }],
            probes,
            new QuotaRouterOptions(),
            NullLogger<AgentClassRouter>.Instance);

        Assert.Null(router.GetCapabilityPool("frontier", WellKnownCapabilities.Audit));

        // Hot-reload: tag claude as audit-capable.
        router.ApplyConfigReload(
            [new AgentClass
            {
                Id = "frontier",
                DisplayName = "Frontier",
                Members =
                [
                    new AgentMembership
                    {
                        Agent = AgentKind.Claude,
                        Billing = AgentBilling.Subscription,
                        QualityScore = 100,
                        Capabilities = [WellKnownCapabilities.Audit],
                    },
                    new AgentMembership
                    {
                        Agent = AgentKind.Codex,
                        Billing = AgentBilling.Subscription,
                        QualityScore = 90,
                    },
                ],
            }],
            []);

        var pool = router.GetCapabilityPool("frontier", WellKnownCapabilities.Audit);
        Assert.NotNull(pool);
        Assert.Single(pool);
        Assert.Contains(AgentKind.Claude, pool);
        Assert.DoesNotContain(AgentKind.Codex, pool);

        // Hot-reload back to untagged: pool goes inactive again.
        router.ApplyConfigReload(
            [new AgentClass
            {
                Id = "frontier",
                DisplayName = "Frontier",
                Members =
                [
                    new AgentMembership
                    {
                        Agent = AgentKind.Claude,
                        Billing = AgentBilling.Subscription,
                        QualityScore = 100,
                    },
                ],
            }],
            []);
        Assert.Null(router.GetCapabilityPool("frontier", WellKnownCapabilities.Audit));
    }

    [Fact]
    public void RouterCapabilityPool_CaseInsensitiveAndTrimmed()
    {
        var router = new AgentClassRouter(
            [new AgentClass
            {
                Id = "frontier",
                DisplayName = "Frontier",
                Members =
                [
                    new AgentMembership
                    {
                        Agent = AgentKind.Claude,
                        Billing = AgentBilling.Subscription,
                        QualityScore = 100,
                        Capabilities = ["AUDIT"],   // upper-case
                    },
                    new AgentMembership
                    {
                        Agent = AgentKind.Codex,
                        Billing = AgentBilling.Subscription,
                        QualityScore = 90,
                        Capabilities = ["Audit"],   // mixed case
                    },
                ],
            }],
            [],
            new QuotaRouterOptions(),
            NullLogger<AgentClassRouter>.Instance);

        var pool = router.GetCapabilityPool("frontier", "audit");
        Assert.NotNull(pool);
        Assert.Equal(2, pool.Count);
        Assert.Contains(AgentKind.Claude, pool);
        Assert.Contains(AgentKind.Codex, pool);
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private RoutingFixture BuildFixture(
        string seedRepoUrl,
        RecordingLlmAuditor auditor,
        IReadOnlyList<(AgentKind Agent, bool IsAuditCapable, int QualityScore)> members,
        Dictionary<AgentKind, double> quotas,
        AgentKind? preferredAuditAgent,
        AgentKind? defaultAgent = null)
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

        var agents = members.Select(m => new ScriptableAgent(m.Agent)).ToList();
        var codex = agents.FirstOrDefault(a => a.Kind == AgentKind.Codex);
        var gemini = agents.FirstOrDefault(a => a.Kind == AgentKind.Gemini);
        var claude = agents.FirstOrDefault(a => a.Kind == AgentKind.Claude);
        var registry = new AgentRegistry([.. agents]);

        var frontier = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members = members
                .Select(m => new AgentMembership
                {
                    Agent = m.Agent,
                    Billing = AgentBilling.Subscription,
                    QualityScore = m.QualityScore,
                    Capabilities = m.IsAuditCapable ? [WellKnownCapabilities.Audit] : [],
                })
                .ToList(),
        };

        var probes = members
            .Select(m => (IAgentQuotaProbe)new ConfigurableProbe(
                m.Agent,
                quotas.GetValueOrDefault(m.Agent, 80.0)))
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
            DefaultAgent = defaultAgent ?? AgentKind.Codex,
            DefaultAgentClass = "frontier",
            Audit = new ProjectAudit
            {
                MaxIterations = 1,
                AuditTypes = ["scripted"],
                AuditAgent = preferredAuditAgent,
                MaxLlmAuditorParallelism = 1,
            },
        };

        var projects = new InMemoryProjectRepository(project);
        var fallbackHistory = new InMemoryAgentFallbackHistoryStore();

        var pipeline = new PipelineRunner(
            sandboxes,
            gitHost,
            registry,
            new PermissiveCredentialProvider(),
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

        return new RoutingFixture(pipeline, store, webhooks, codex, gemini, claude);
    }

    private static WorkItem NewItem(AgentKind agent) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "audit-capability routing test",
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

    private sealed class PermissiveCredentialProvider : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
            => Task.FromResult<AgentCredential?>(new AgentCredential(
                agent,
                EnvironmentVariables: new Dictionary<string, string>(),
                Files: new Dictionary<string, string>()));
    }

    private sealed record RoutingFixture(
        PipelineRunner Pipeline,
        SqliteWorkItemStore Store,
        CapturingWebhookDispatcher Webhooks,
        ScriptableAgent? Codex,
        ScriptableAgent? Gemini,
        ScriptableAgent? Claude) : IDisposable
    {
        public void Dispose() => Store.Dispose();
    }
}
