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
                [AgentKind.Codex] = 1.0,    // exhausted
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
    public async Task OnlyGeminiHasQuota_GeminiNotAuditCapable_ParksForQuotaReset()
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
                [AgentKind.Codex] = 1.0,    // exhausted
                [AgentKind.Claude] = 2.0,    // exhausted
                [AgentKind.Gemini] = 80.0,   // healthy — but excluded from audit pool
            },
            preferredAuditAgent: AgentKind.Codex);

        fix.Codex!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));
        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        // Every audit-capable member is quota-exhausted and Gemini is
        // excluded from the audit pool by the capability gate. A Pass
        // verdict cannot emerge without the auditor having produced a
        // verdict, so the work item parks for quota reset rather than
        // silently completing with zero LLM review.
        Assert.Equal(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.NotEqual(WorkItemState.Done, final.State);
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
                [AgentKind.Codex] = 80.0,
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

    // ── AC: mid-iteration spill stays inside the audit-capable pool ─────────

    [Fact]
    public async Task MidIterationSpill_NonAuditCapableMember_StaysExcludedAndParksForQuotaReset()
    {
        // Resolve-time gate is exercised by the other tests; this pins the
        // mid-iteration spill gate in InvokeAgentWithQuotaFallbackAsync. Setup
        // is "Claude looks healthy at resolve time but quota-fails when it
        // actually runs the auditor"; the spill candidate list contains only
        // Gemini (healthy, non-audit-capable). The requireCapability filter
        // must drop Gemini, so the entire audit-capable pool is exhausted and
        // the work item parks for quota reset — silently skipping the auditor
        // and routing through to Done would let a Pass verdict emerge with
        // zero LLM review.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("cheating:llm-review",
            agent => agent == AgentKind.Claude ? QuotaAuditResult() : new AuditResult(true, []));
        using var fix = BuildFixture(seed, auditor,
            members: [
                (AgentKind.Claude, IsAuditCapable: true,  QualityScore: 100),
                (AgentKind.Gemini, IsAuditCapable: false, QualityScore: 90),
            ],
            quotas: new()
            {
                [AgentKind.Claude] = 80.0,   // healthy at resolve time
                [AgentKind.Gemini] = 80.0,   // healthy too, but NOT audit-capable
            },
            preferredAuditAgent: AgentKind.Claude);

        fix.Claude!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));
        var item = NewItem(AgentKind.Claude);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.NotEqual(WorkItemState.Done, final.State);
        // Claude ran first (preferred audit-capable) and quota-failed.
        // Gemini has quota but is NOT audit-capable — the mid-iter spill
        // filter must skip it, the pool is exhausted, the item parks.
        Assert.Equal([AgentKind.Claude], auditor.Invocations);
        Assert.DoesNotContain(AgentKind.Gemini, auditor.Invocations);
    }

    // ── HARD INVARIANT: when no preferred audit agent is configured and the
    // selected work runner is itself in the audit-capable pool, the resolver
    // must still gate it on quota before returning. An already-exhausted
    // work runner has to spill to another audit-capable peer (or park if
    // the whole pool is exhausted), not be returned blindly. This is the
    // resolve-time gate that audit:llm-review flagged: previously the
    // shortcut at PipelineRunner.cs:~6780 trusted the work runner solely on
    // the audit-capability tag + paused-state check, so an exhausted work
    // agent could still dispatch the auditor and reach a Pass verdict if
    // the CLI happened to return cleanly.
    [Fact]
    public async Task NoPreferredAuditAgent_WorkAgentAuditCapableButQuotaExhausted_SpillsToPeer()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        // Both Claude (the work runner) and Codex are audit-capable; Claude
        // is quota-exhausted at resolve time, Codex is healthy. With no
        // explicit AuditAgent set, the resolver previously returned the
        // work runner (Claude) directly without re-gating on quota — the
        // bug fixed here. The resolver must now spill to Codex.
        using var fix = BuildFixture(seed, auditor,
            members: [
                (AgentKind.Claude, IsAuditCapable: true, QualityScore: 100),
                (AgentKind.Codex,  IsAuditCapable: true, QualityScore: 90),
            ],
            quotas: new()
            {
                [AgentKind.Claude] = 1.0,    // exhausted (work runner)
                [AgentKind.Codex]  = 80.0,   // healthy peer
            },
            // No AuditAgent configured — forces the "no preferred kind"
            // branch of ResolveAuditAgentRunnerAsync, which is the path
            // that previously bypassed the quota gate for an audit-capable
            // work runner.
            preferredAuditAgent: null,
            defaultAgent: AgentKind.Claude);

        fix.Claude!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));
        var item = NewItem(AgentKind.Claude);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        // Audit must NOT run on the exhausted work runner; it must spill to
        // the healthy audit-capable peer. Asserting the exact pick (rather
        // than "anything but Claude") catches a future regression where the
        // shortcut returns Claude again and the auditor happens to succeed.
        Assert.Equal([AgentKind.Codex], auditor.Invocations);
        Assert.DoesNotContain(AgentKind.Claude, auditor.Invocations);
    }

    // ── HARD INVARIANT: a sibling auditor's non-quota exception must NOT
    // mask another auditor's mid-iteration AgentClassExhaustedException.
    // The settling logic in CollectFindingsAsync (PipelineRunner.cs:~6320)
    // waits for all parallel tasks to finish, then surfaces exhaustion
    // FIRST so the work item parks in WaitingForQuotaReset for a quota-
    // blocked audit; only when no auditor was exhaustion-blocked does a
    // sibling generic exception propagate. Without this priority, a
    // sibling InvalidOperationException would route the item to
    // failureKind=other (or worse, infrastructure) and the
    // QuotaRetryScheduler would never re-pick it up — the quota would
    // return and the item would stay terminally Failed.
    [Fact]
    public async Task ParallelAuditors_ExhaustionPrioritisedOverSiblingNonQuotaException_ParksForQuotaReset()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        // Auditor A returns a Claude-shaped quota failure on dispatch; the
        // single-member audit pool then exhausts in
        // InvokeAgentWithQuotaFallbackAsync → AgentClassExhaustedException.
        var quotaAuditor = new RecordingLlmAuditor(
            "cheating:llm-review",
            agent => agent == AgentKind.Claude ? QuotaAuditResult() : new AuditResult(true, []));
        // Auditor B throws a generic (non-quota) exception from inside its
        // RunAsync body — the same shape an unexpected fault would take.
        var throwingAuditor = new RecordingLlmAuditor(
            "security:llm-review",
            agent => throw new InvalidOperationException(
                "simulated non-quota auditor body fault (must NOT mask sibling exhaustion)"));

        using var fix = BuildFixture(seed,
            auditors: [quotaAuditor, throwingAuditor],
            members: [
                // Single-member audit pool. Both auditors land on Claude;
                // the quota-failing auditor's spill therefore exhausts the
                // whole pool, which is what raises AgentClassExhaustedException.
                (AgentKind.Claude, IsAuditCapable: true, QualityScore: 100),
                (AgentKind.Gemini, IsAuditCapable: false, QualityScore: 90),
            ],
            quotas: new()
            {
                [AgentKind.Claude] = 80.0,
                [AgentKind.Gemini] = 80.0,
            },
            preferredAuditAgent: AgentKind.Claude,
            // Parallel dispatch is what makes the settling order matter —
            // sequential settling would never see both exceptions land at
            // once.
            maxLlmAuditorParallelism: 2);

        fix.Claude!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));
        var item = NewItem(AgentKind.Claude);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        // The hard invariant: AgentClassExhaustedException wins over a sibling
        // non-quota exception, so the work item parks for quota reset rather
        // than landing terminally Failed on the sibling fault.
        Assert.Equal(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.NotEqual(WorkItemState.Failed, final.State);
    }

    // ── AC: per-auditor routing dispatches across distinct audit-capable members ─

    [Fact]
    public async Task PerAuditorAgent_RoutesAcrossDistinctAuditCapableMembers()
    {
        // "Concurrent across pool" no-bottleneck pin: with both Codex and
        // Claude audit-capable and healthy and a PerAuditorAgent map sending
        // each LLM auditor to a different audit-capable member, the audit
        // phase dispatches across both — proving the routing is not pinned to
        // one agent. Without this property the original audit-throughput
        // collapse (every audit serialised through one member) could regress
        // silently.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditorA = new RecordingLlmAuditor("cheating:llm-review");
        var auditorB = new RecordingLlmAuditor("security:llm-review");
        using var fix = BuildFixture(seed,
            auditors: [auditorA, auditorB],
            members: [
                (AgentKind.Codex,  IsAuditCapable: true,  QualityScore: 100),
                (AgentKind.Claude, IsAuditCapable: true,  QualityScore: 90),
                (AgentKind.Gemini, IsAuditCapable: false, QualityScore: 95),
            ],
            quotas: new()
            {
                [AgentKind.Codex] = 80.0,
                [AgentKind.Claude] = 80.0,
                [AgentKind.Gemini] = 80.0,
            },
            preferredAuditAgent: AgentKind.Codex,
            perAuditorAgent: new Dictionary<string, AgentKind>
            {
                ["cheating:llm-review"] = AgentKind.Codex,
                ["security:llm-review"] = AgentKind.Claude,
            },
            maxLlmAuditorParallelism: 2);

        fix.Codex!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));
        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        // Each auditor lands on its per-auditor pick — distinct members of
        // the audit pool — demonstrating that audits route across the pool
        // instead of serialising through one agent.
        Assert.Equal([AgentKind.Codex], auditorA.Invocations);
        Assert.Equal([AgentKind.Claude], auditorB.Invocations);
        // Gemini is healthier (by quality score) than Claude but stays out of
        // the audit pool entirely — neither auditor lands on it.
        Assert.DoesNotContain(AgentKind.Gemini, auditorA.Invocations);
        Assert.DoesNotContain(AgentKind.Gemini, auditorB.Invocations);
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
                [AgentKind.Codex] = 80.0,
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
        // eligible. SelectFromAuditCapablePoolAsync walks
        // OrderedFallbackCandidatesAsync, so the pick is deterministic: the
        // highest-quality audit-capable member, which is Codex (QS=100) over
        // Claude (QS=90). Asserting the exact pick (rather than a disjunction)
        // catches regressions that route to the wrong audit-capable member.
        Assert.Equal([AgentKind.Codex], auditor.Invocations);
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
                [AgentKind.Codex] = 80.0,
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
                [AgentKind.Codex] = 80.0,
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
        => BuildFixture(
            seedRepoUrl,
            auditors: [auditor],
            members,
            quotas,
            preferredAuditAgent,
            defaultAgent: defaultAgent,
            perAuditorAgent: null,
            maxLlmAuditorParallelism: 1);

    private RoutingFixture BuildFixture(
        string seedRepoUrl,
        IReadOnlyList<IAuditor> auditors,
        IReadOnlyList<(AgentKind Agent, bool IsAuditCapable, int QualityScore)> members,
        Dictionary<AgentKind, double> quotas,
        AgentKind? preferredAuditAgent,
        AgentKind? defaultAgent = null,
        IReadOnlyDictionary<string, AgentKind>? perAuditorAgent = null,
        int maxLlmAuditorParallelism = 1)
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
                PerAuditorAgent = perAuditorAgent is null
                    ? new Dictionary<string, AgentKind>()
                    : new Dictionary<string, AgentKind>(perAuditorAgent),
                MaxLlmAuditorParallelism = maxLlmAuditorParallelism,
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
            new ProjectAuditorComposer(new ScriptedAuditorCatalog(auditors)),
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
            ]),
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);

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
        private readonly Func<AgentKind, AuditResult>? _resultBuilder;

        public RecordingLlmAuditor(string name, Func<AgentKind, AuditResult>? resultBuilder = null)
        {
            Name = name;
            _resultBuilder = resultBuilder;
        }
        public string Name { get; }
        public string Kind => "llm";
        public AuditCapabilities Required => AuditCapabilities.AgentCredentials;
        public List<AgentKind> Invocations { get; } = [];

        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            // Throw rather than silently substitute a placeholder kind — a null
            // AuditRunner here would indicate the resolver returned no agent yet
            // the pipeline still dispatched, which would mask the real bug. The
            // earlier Claude-default could hide a regression behind a green test.
            Assert.NotNull(context.AuditRunner);
            var agent = context.AuditRunner!.Kind;
            Invocations.Add(agent);
            return Task.FromResult(_resultBuilder?.Invoke(agent) ?? new AuditResult(true, []));
        }
    }

    /// <summary>
    /// Quota-shaped LLM auditor result: <see cref="ThrowIfAuditorRunQuotaAsync"/>
    /// detects the stdout signature and throws <c>TerminalQuotaError</c>, which
    /// drives the mid-iteration spill path inside
    /// <c>InvokeAgentWithQuotaFallbackAsync</c>. Uses Claude's
    /// <c>rate_limit_exceeded</c> marker so the Claude detector classifies it.
    /// </summary>
    private static AuditResult QuotaAuditResult() => new(
        Passed: false,
        Findings: [new AuditFinding("cheating:llm-review", AuditSeverity.Error, "review agent failed to run", "quota")],
        AgentSummary: "agent exited 1",
        AgentStdout: "API Error: rate_limit_exceeded; please try again after 1h");

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
