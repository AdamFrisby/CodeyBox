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
                [AgentKind.Codex] = 80.0,   // healthy peer
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

    // ── HARD INVARIANT: a smoke-benched audit-capable work runner must NOT
    // be selected for auditing. The no-preferred-audit-agent shortcut in
    // ResolveAuditAgentRunnerAsync (PipelineRunner.cs:~6802) now routes the
    // work runner through EnsureAgentSmokeAvailableAsync before trusting it,
    // and on smoke rejection falls through to SelectFromAuditCapablePoolAsync
    // for a healthy peer. Without this gate, a work runner whose CLI is
    // benched (exit-127 / auth drift) could still be dispatched as auditor,
    // and a happens-to-return-cleanly run would silently produce a Pass
    // verdict on an agent the production smoke gate had already rejected.
    //
    // The sibling quota-exhausted shortcut is covered by
    // NoPreferredAuditAgent_WorkAgentAuditCapableButQuotaExhausted_SpillsToPeer
    // above; this test pins the smoke-gate branch explicitly so a regression
    // that ignores the smoke verdict cannot pass both tests.
    [Fact]
    public async Task NoPreferredAuditAgent_WorkAgentAuditCapableButSmokeRejected_SpillsToPeer()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        // Both Claude (the work runner) and Codex are audit-capable AND both
        // have healthy quota. The only thing wrong with Claude is that the
        // in-VM smoke gate benches it FOR THE AUDIT-PHASE sandbox target —
        // exactly the shape a real CLI outage produces against the audit
        // profile while the work profile is fine. With no AuditAgent
        // configured, the resolver enters the no-preferred-kind branch; the
        // work runner's audit-capable shortcut MUST gate on the smoke
        // verdict and fall through to the audit pool walk, which then picks
        // Codex.
        //
        // The work-phase smoke target uses NetworkProfiles.Work, and the
        // audit-phase target uses NetworkProfiles.AuditAgent — distinct
        // strings let the gate bench only the audit-phase view of Claude
        // without breaking the work phase. The same pattern keeps the
        // OrderedFallbackCandidatesAsync pool walk (also targeting the audit
        // profile) from surfacing Claude.
        const string workProfile = "work";
        const string auditProfile = "audit";
        var smokeGate = new BenchKindByNetworkProfileSmokeGate(AgentKind.Claude, auditProfile);
        using var fix = BuildFixture(seed, auditor,
            members: [
                (AgentKind.Claude, IsAuditCapable: true, QualityScore: 100),
                (AgentKind.Codex,  IsAuditCapable: true, QualityScore: 90),
            ],
            quotas: new()
            {
                // Both healthy on quota — only the smoke gate excludes Claude.
                // If quota were the lever, this test would collapse into the
                // sibling quota-exhaustion test above; keeping both healthy is
                // what isolates the smoke-gate branch.
                [AgentKind.Claude] = 80.0,
                [AgentKind.Codex] = 80.0,
            },
            preferredAuditAgent: null,
            defaultAgent: AgentKind.Claude,
            inVmSmokeGate: smokeGate,
            networkProfiles: new ProjectNetworkProfiles
            {
                Work = workProfile,
                AuditAgent = auditProfile,
            });

        fix.Claude!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));
        var item = NewItem(AgentKind.Claude);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        // Auditor must NOT run on the smoke-benched work runner; it must
        // spill to the healthy audit-capable peer. Asserting the exact pick
        // (not "anything but Claude") catches a regression where the
        // shortcut skips the smoke gate and dispatches the auditor on the
        // benched Claude runner.
        Assert.Equal([AgentKind.Codex], auditor.Invocations);
        Assert.DoesNotContain(AgentKind.Claude, auditor.Invocations);
    }

    // ── HARD INVARIANT: a cached-exhausted audit-capable work runner must
    // NOT be selected for auditing — even when its live smoke + quota probes
    // currently look healthy. The router's in-process exhaustion cache is
    // set by mid-iteration spills (AgentClassRouter.MarkExhausted); a fast-
    // path that ignores it can re-dispatch against the very bucket the
    // spill was meant to avoid, since the live probe lags behind the cache.
    // Without this gate a Pass verdict can emerge on a quota-exhausted
    // member that the rest of the pipeline is treating as out-of-rotation.
    [Fact]
    public async Task NoPreferredAuditAgent_WorkAgentAuditCapableButCachedExhausted_SpillsToPeer()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        // Both Claude (the work runner) and Codex are audit-capable AND both
        // have healthy quota by the live probe. The only thing wrong with
        // Claude is that the router cache says it's exhausted — the shape
        // a recent MarkExhausted leaves behind. The fast path that returns
        // the work runner after smoke + EvaluateAuditCandidateQuotaAsync
        // must also consult AgentClassRouter.IsExhausted; otherwise the
        // live healthy probe wins and the auditor runs on the cached-out
        // bucket. With the gate in place, the resolver spills to Codex.
        using var fix = BuildFixture(seed, auditor,
            members: [
                (AgentKind.Claude, IsAuditCapable: true, QualityScore: 100),
                (AgentKind.Codex,  IsAuditCapable: true, QualityScore: 90),
            ],
            quotas: new()
            {
                [AgentKind.Claude] = 80.0,   // healthy live probe
                [AgentKind.Codex] = 80.0,    // healthy peer
            },
            preferredAuditAgent: null,
            defaultAgent: AgentKind.Claude);
        fix.MarkExhausted(AgentKind.Claude);

        fix.Claude!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));
        var item = NewItem(AgentKind.Claude);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        // Audit MUST spill past the cached-exhausted work runner onto the
        // healthy peer. Asserting the exact pick catches a regression where
        // a missing IsExhausted check lets the live probe re-select Claude.
        Assert.Equal([AgentKind.Codex], auditor.Invocations);
        Assert.DoesNotContain(AgentKind.Claude, auditor.Invocations);
    }

    // ── HARD INVARIANT: a cached-exhausted preferred audit agent must NOT
    // be selected for auditing — same shape as the work-runner fast path,
    // but for the explicit Audit.AuditAgent override. The preferred fast
    // path runs smoke + EvaluateAuditCandidateQuotaAsync, both of which
    // consult live state; neither catches a member that was marked
    // exhausted by a mid-iteration spill an instant earlier. The resolver
    // must also consult AgentClassRouter.IsExhausted before returning, or
    // a Pass verdict can emerge against the very bucket the spill avoided.
    [Fact]
    public async Task PreferredAuditAgent_CachedExhausted_SpillsToHealthyPeer()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        using var fix = BuildFixture(seed, auditor,
            members: [
                (AgentKind.Codex,  IsAuditCapable: true, QualityScore: 100),
                (AgentKind.Claude, IsAuditCapable: true, QualityScore: 90),
            ],
            quotas: new()
            {
                [AgentKind.Codex] = 80.0,    // healthy live probe
                [AgentKind.Claude] = 80.0,   // healthy peer
            },
            preferredAuditAgent: AgentKind.Codex,
            defaultAgent: AgentKind.Codex);
        fix.MarkExhausted(AgentKind.Codex);

        fix.Codex!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));
        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        // Audit MUST spill past the cached-exhausted preferred agent onto
        // the healthy peer. Pinning the exact pick catches a regression
        // where the live probe re-selects Codex despite the cache verdict.
        Assert.Equal([AgentKind.Claude], auditor.Invocations);
        Assert.DoesNotContain(AgentKind.Codex, auditor.Invocations);
    }

    // ── HARD INVARIANT: when the preferred audit agent is registered but
    // has NO credentials, the fallback MUST gate the work runner through
    // the same smoke + quota checks the no-preferred branch applies — NOT
    // hand the auditor straight to the work runner with only a pause check.
    // The unregistered-preferred branch is covered by
    // PreferredAuditAgent_NotRegistered_FallbackGatesWorkRunner_AndSpillsToHealthyPeer;
    // this test pins the SIBLING branch where the preferred agent IS
    // registered but its credential resolves to null. Both branches share
    // FallbackToWorkRunnerOrSpillToAuditPoolAsync, so a regression that
    // bypasses the helper in the missing-credentials branch would slip
    // past the unregistered-branch test alone — exactly the coverage gap
    // the tests:meaningfulness-review finding called out.
    [Fact]
    public async Task PreferredAuditAgent_MissingCredentials_FallbackGatesWorkRunner_AndSpillsToHealthyPeer()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        using var fix = BuildFixture(seed, auditor,
            members: [
                (AgentKind.Claude, IsAuditCapable: true, QualityScore: 100),
                (AgentKind.Codex,  IsAuditCapable: true, QualityScore: 90),
                (AgentKind.Gemini, IsAuditCapable: true, QualityScore: 80),
            ],
            quotas: new()
            {
                [AgentKind.Claude] = 1.0,    // exhausted (work runner)
                [AgentKind.Codex] = 80.0,    // healthy peer
                [AgentKind.Gemini] = 80.0,   // preferred (no creds → fallback)
            },
            // Gemini is the preferred audit agent and IS in the registry,
            // but its credential resolves to null (operator forgot to set
            // CODEYBOX_GEMINI_API_KEY). The resolver hits the missing-
            // credentials branch and routes through
            // FallbackToWorkRunnerOrSpillToAuditPoolAsync; without gating
            // it would hand the auditor straight to the quota-exhausted
            // work runner (Claude).
            preferredAuditAgent: AgentKind.Gemini,
            defaultAgent: AgentKind.Claude,
            credentials: new MissingCredentialsForKind(AgentKind.Gemini));

        fix.Claude!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));
        var item = NewItem(AgentKind.Claude);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        // Audit MUST run on Codex (the healthy peer), NEVER on the quota-
        // exhausted Claude work runner. A regression that drops the gate
        // would dispatch the auditor on Claude — which may happen to return
        // cleanly and produce a Pass verdict against an exhausted bucket.
        Assert.Equal([AgentKind.Codex], auditor.Invocations);
        Assert.DoesNotContain(AgentKind.Claude, auditor.Invocations);
    }

    // ── HARD INVARIANT: when the operator NAMED a preferred audit agent
    // that isn't registered, the fallback MUST gate the work runner through
    // the same smoke + quota checks the no-preferred branch applies — NOT
    // hand the auditor straight to the work runner with only a pause check.
    // Before the fix at PipelineRunner.cs:~6827 the unregistered-preferred
    // branch routed to WorkRunnerForAuditUnlessPaused (operator-pause only)
    // even when the work runner was already smoke-rejected or quota-
    // exhausted, so the auditor could dispatch against an unroutable bucket
    // and produce a Pass against an effectively skipped review. The same
    // shape applies to the missing-credentials-preferred branch (~line 6851),
    // which now shares the same gated fallback helper. Below pins the
    // quota-exhausted case for the unregistered-preferred branch; an
    // analogous regression in the missing-credentials path would also have
    // to bypass the helper.
    [Fact]
    public async Task PreferredAuditAgent_NotRegistered_FallbackGatesWorkRunner_AndSpillsToHealthyPeer()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        using var fix = BuildFixture(seed, auditor,
            members: [
                (AgentKind.Claude, IsAuditCapable: true, QualityScore: 100),
                (AgentKind.Codex,  IsAuditCapable: true, QualityScore: 90),
            ],
            quotas: new()
            {
                [AgentKind.Claude] = 1.0,    // exhausted (work runner)
                [AgentKind.Codex] = 80.0,    // healthy peer
            },
            // Cursor is not present in members → not in the registry →
            // ResolveAuditAgentRunnerAsync hits the unregistered-preferred
            // branch and (pre-fix) returned the work runner unchecked.
            // With the fix it must gate the work runner on smoke + quota
            // and spill to the audit pool on rejection.
            preferredAuditAgent: AgentKind.Cursor,
            defaultAgent: AgentKind.Claude);

        fix.Claude!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));
        var item = NewItem(AgentKind.Claude);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        // Audit MUST run on the healthy peer (Codex), NEVER on the quota-
        // exhausted work runner (Claude). Asserting the exact pick catches
        // a regression that drops the gating helper and lets the work
        // runner audit against an exhausted bucket.
        Assert.Equal([AgentKind.Codex], auditor.Invocations);
        Assert.DoesNotContain(AgentKind.Claude, auditor.Invocations);
    }

    // Sibling pin to PreferredAuditAgent_NotRegistered_…: when the work
    // runner itself is quota-exhausted AND the whole spill pool is also
    // exhausted, the unregistered-preferred fallback must PARK the item
    // for quota reset rather than silently dispatching against the
    // exhausted work runner (pre-fix behaviour) — the hard invariant being
    // defended is that a Pass verdict never emerges while a configured
    // auditor's spill-to-peer pool was entirely quota-blocked.
    [Fact]
    public async Task PreferredAuditAgent_NotRegistered_AllAuditPoolExhausted_ParksForQuotaReset()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        using var fix = BuildFixture(seed, auditor,
            members: [
                (AgentKind.Claude, IsAuditCapable: true, QualityScore: 100),
                (AgentKind.Codex,  IsAuditCapable: true, QualityScore: 90),
            ],
            quotas: new()
            {
                [AgentKind.Claude] = 1.0,    // exhausted (work runner)
                [AgentKind.Codex] = 1.0,     // exhausted peer
            },
            preferredAuditAgent: AgentKind.Cursor,
            defaultAgent: AgentKind.Claude);

        fix.Claude!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));
        var item = NewItem(AgentKind.Claude);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        // Whole pool exhausted → park for quota reset; the auditor must
        // NEVER have run. A regression that bypasses the gate would
        // instead dispatch the auditor against the quota-exhausted work
        // runner — either silently passing (the 094bb05 hole this fix
        // descends from) or terminally failing with the wrong shape.
        Assert.Equal(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.NotEqual(WorkItemState.Done, final.State);
        Assert.Empty(auditor.Invocations);
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
        AgentKind? defaultAgent = null,
        IInVmSmokeGate? inVmSmokeGate = null,
        ProjectNetworkProfiles? networkProfiles = null,
        ICredentialProvider? credentials = null)
        => BuildFixture(
            seedRepoUrl,
            auditors: [auditor],
            members,
            quotas,
            preferredAuditAgent,
            defaultAgent: defaultAgent,
            perAuditorAgent: null,
            maxLlmAuditorParallelism: 1,
            inVmSmokeGate: inVmSmokeGate,
            networkProfiles: networkProfiles,
            credentials: credentials);

    private RoutingFixture BuildFixture(
        string seedRepoUrl,
        IReadOnlyList<IAuditor> auditors,
        IReadOnlyList<(AgentKind Agent, bool IsAuditCapable, int QualityScore)> members,
        Dictionary<AgentKind, double> quotas,
        AgentKind? preferredAuditAgent,
        AgentKind? defaultAgent = null,
        IReadOnlyDictionary<string, AgentKind>? perAuditorAgent = null,
        int maxLlmAuditorParallelism = 1,
        IInVmSmokeGate? inVmSmokeGate = null,
        ProjectNetworkProfiles? networkProfiles = null,
        ICredentialProvider? credentials = null)
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

        // Wire the smoke gate into both the router (so OrderedFallbackCandidatesAsync
        // filters smoke-rejected members from its candidate list) AND the pipeline
        // (so EnsureAgentSmokeAvailableAsync gates the work-runner-audit-capable
        // shortcut). Without both, the pool walk and the shortcut would drift in
        // smoke verdicts and the test would not exercise the real production
        // wiring.
        var dispatchAvailability = inVmSmokeGate is null
            ? null
            : new AgentDispatchAvailability(inVmSmokeGate: inVmSmokeGate);

        var router = new AgentClassRouter(
            [frontier],
            probes,
            new QuotaRouterOptions { MinQuotaPct = 10.0 },
            NullLogger<AgentClassRouter>.Instance,
            dispatchAvailability: dispatchAvailability);

        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = defaultAgent ?? AgentKind.Codex,
            DefaultAgentClass = "frontier",
            NetworkProfiles = networkProfiles ?? new ProjectNetworkProfiles(),
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
            credentials ?? new PermissiveCredentialProvider(),
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
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            dispatchAvailability: dispatchAvailability);

        return new RoutingFixture(pipeline, store, webhooks, codex, gemini, claude, router, frontier);
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

    /// <summary>
    /// Credential provider that returns a non-null bundle for every kind
    /// EXCEPT the configured "missing" kind, which always resolves to null
    /// (operator forgot to wire the credential env var). Pins the audit
    /// resolver's missing-credentials preferred-audit branch.
    /// </summary>
    private sealed class MissingCredentialsForKind : ICredentialProvider
    {
        private readonly AgentKind _missing;
        public MissingCredentialsForKind(AgentKind missing) => _missing = missing;

        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
            => Task.FromResult<AgentCredential?>(
                agent == _missing
                    ? null
                    : new AgentCredential(
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
        ScriptableAgent? Claude,
        AgentClassRouter Router,
        AgentClass Class) : IDisposable
    {
        public void Dispose() => Store.Dispose();

        /// <summary>
        /// Mark a configured class member exhausted in the router's in-process
        /// cache so subsequent fast paths see it as quota-out before any live
        /// probe runs. Used to pin the IsExhausted gate that the audit
        /// resolver applies before trusting an apparently-healthy preferred
        /// or audit-capable work runner.
        /// </summary>
        public void MarkExhausted(AgentKind kind, TimeSpan? ttl = null)
        {
            var member = Class.Members.First(m => m.Agent == kind);
            Router.MarkExhausted(
                member,
                ttl ?? TimeSpan.FromHours(1),
                resetAt: DateTimeOffset.UtcNow.AddHours(1));
        }
    }

    /// <summary>
    /// In-VM smoke gate stub that benches a single agent kind ONLY when the
    /// probe target's <see cref="InVmSmokeSandboxTarget.NetworkProfile"/>
    /// matches the configured "bench" profile string. Used by the smoke-
    /// rejected work-runner shortcut test to simulate a CLI outage that
    /// affects the audit-phase sandbox profile but leaves the work-phase
    /// profile healthy — without this distinction the work phase would fail
    /// on smoke before the audit phase ever ran. Production wiring runs the
    /// gate inside both the router (which filters smoke-rejected members
    /// from its candidate list) and the pipeline (which gates the audit-
    /// capable work-runner shortcut), so the test wires it into both via
    /// <see cref="AgentDispatchAvailability"/>.
    /// </summary>
    private sealed class BenchKindByNetworkProfileSmokeGate : IInVmSmokeGate
    {
        private readonly AgentKind _kind;
        private readonly string _benchProfile;

        public BenchKindByNetworkProfileSmokeGate(AgentKind kind, string benchProfile)
        {
            _kind = kind;
            _benchProfile = benchProfile;
        }

        public bool Enabled => true;

        private bool ShouldBench(AgentKind kind, InVmSmokeSandboxTarget target) =>
            kind == _kind
            && string.Equals(target.NetworkProfile, _benchProfile, StringComparison.Ordinal);

        public Task<AgentAvailability> EnsureAvailableAsync(
            AgentKind kind,
            InVmSmokeSandboxTarget target,
            CancellationToken ct)
            => Task.FromResult(ShouldBench(kind, target)
                ? new AgentAvailability(false, "in-VM smoke: bench (test)", null, AgentAvailabilityCause.SmokeGate)
                : new AgentAvailability(true, null, null));

        public Task ProbeAllAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ProbeAllAsync(InVmSmokeSandboxTarget target, CancellationToken ct) => Task.CompletedTask;
        public Task<AgentAvailability?> ForceProbeAsync(AgentKind kind, CancellationToken ct)
            => Task.FromResult<AgentAvailability?>(new AgentAvailability(true, null, null));
    }
}
