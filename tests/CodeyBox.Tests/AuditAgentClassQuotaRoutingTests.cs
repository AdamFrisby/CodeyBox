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

    // ── Acceptance #3 (resolve-time path): every probe below floor → PARK ──

    [Fact]
    public async Task AllClassMembersExhausted_AtResolveTime_ParksWorkItemForQuotaReset()
    {
        // Resolve-time path. Every probed candidate (preferred audit agent +
        // every class member) reports available below MinQuotaPct, so the
        // audit gate cannot run an LLM verdict this iteration. The work item
        // PARKS in WaitingForQuotaReset rather than passing audit with an
        // incomplete review set; the QuotaRetryScheduler resumes the same
        // iteration once a class member's quota recovers. The earlier
        // warning-and-skip variant let a Pass verdict emerge with zero LLM
        // review which silently bypassed the gate — the bug this fix targets.
        // Mid-iteration coverage of the same invariant lives in
        // AuditQuotaPauseTests.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        using var fix = BuildFixture(seed, auditor,
            classMembers: [AgentKind.Gemini, AgentKind.Claude, AgentKind.Codex],
            quotas: new()
            {
                [AgentKind.Gemini] = 1.0,
                [AgentKind.Claude] = 2.0,
                [AgentKind.Codex] = 3.0,
            });
        fix.Codex!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, final!.State);
        // Auditor never ran — resolver threw AgentClassExhaustedException
        // before any auditor was dispatched into a sandbox.
        Assert.Empty(auditor.Invocations);
        Assert.Contains(fix.Webhooks.Events, e => e.Event == "work_item.waiting_for_quota_reset");
        // The fix guarantees the LastError never carries the old
        // "agent exited 1" string when the auditor cannot run for quota.
        Assert.DoesNotContain("agent exited 1", final.LastError ?? string.Empty);
    }

    // ── Resume after quota return: every auditor runs before a Pass verdict ─

    [Fact]
    public async Task AllClassMembersExhausted_AtResolveTime_AfterQuotaReturn_RetryDrivesAuditorToCompletion()
    {
        // Hard invariant on the audit-tag pool: a Pass verdict must NEVER
        // emerge while a configured auditor's spill-to-peer pool was entirely
        // quota-blocked. The companion to
        // AllClassMembersExhausted_AtResolveTime_ParksWorkItemForQuotaReset
        // proves the park; this test proves the OTHER half of the invariant:
        // when quota returns, the retry path actually drives the SAME audit
        // iteration to a completed verdict (auditor invoked, Done) rather
        // than carrying a stale skipped-auditor entry that would let the
        // iteration short-circuit to Pass without the auditor running.
        // The original AllClassMembersExhausted... test stops at the park;
        // a regression that parks correctly but then resumes to Done with no
        // auditor invocation would slip past it.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        using var fix = BuildFixture(seed, auditor,
            classMembers: [AgentKind.Gemini, AgentKind.Claude, AgentKind.Codex],
            quotas: new()
            {
                [AgentKind.Gemini] = 1.0,
                [AgentKind.Claude] = 2.0,
                [AgentKind.Codex] = 3.0,
            },
            wireRetrier: true);
        fix.Codex!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);

        // First pass: all probes report below MinQuotaPct → resolver throws
        // AgentClassExhaustedException → item parks in WaitingForQuotaReset
        // BEFORE any auditor is dispatched.
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var parked = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(parked);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, parked!.State);
        Assert.Empty(auditor.Invocations);

        // Quota returns: flip every class member's probe back above MinQuotaPct.
        // This mirrors the production "next quota probe reads fresh headroom"
        // sequence that QuotaRetryScheduler observes before scheduling retry.
        foreach (var probe in fix.Probes)
            probe.SetAvailable(80.0);

        // Drive the retry the same way QuotaRetryScheduler does on quota
        // return: WorkItemRetrier.RetryAsync transitions the parked item
        // back to a re-runnable pre-audit state and re-enqueues it for
        // pickup.
        Assert.NotNull(fix.Retrier);
        var (retrySuccess, retryError, _, _, _) = await fix.Retrier!.RetryAsync(
            parked, from: "audit", trigger: "test-quota-return", CancellationToken.None);
        Assert.True(retrySuccess, retryError);

        var resumed = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(resumed);
        // Resume target is the pre-audit state the worker pool re-picks up.
        Assert.Equal(WorkItemState.WorkComplete, resumed!.State);

        // Second pickup: mirror what the worker pool does on dequeue.
        // The audit phase must now actually run the configured auditor
        // before any Pass verdict — a stale skipped-auditor entry surviving
        // the park would let the iteration pass with zero auditor calls,
        // which is exactly the silent-skip regression this fix forbids.
        await fix.Pipeline.RunAsync(resumed, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        // Auditor MUST have run on the resumed iteration before the Pass
        // verdict — exactly one invocation, on a member that passed the gate
        // (gemini is preferred but at 1% on first pass; after the flip it's
        // healthy and runs the auditor; if the preferred path were skipped
        // the chain walk would still land on a class member). Either way
        // the count must be exactly one and the invocation must be a real
        // class member, not the empty list the park produced.
        Assert.Single(auditor.Invocations);
        Assert.Contains(auditor.Invocations[0],
            new[] { AgentKind.Gemini, AgentKind.Claude, AgentKind.Codex });
    }

    [Fact]
    public async Task PerAgentFloorOverride_AuditGateRejectsReservedPreferredAndAllowsBurnFallback()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        var reset = DateTimeOffset.UtcNow + TimeSpan.FromDays(7);
        var quotaOptions = new QuotaRouterOptions
        {
            MinQuotaPct = 10.0,
            StartFloorPct = 25.0,
            EndFloorPct = 3.0,
            RampWindow = TimeSpan.FromDays(7),
        };
        quotaOptions.FloorByAgent[AgentKind.Codex.Value] = new QuotaFloorOverrideOptions
        {
            MinQuotaPct = 1.0,
            StartFloorPct = 1.0,
            EndFloorPct = 0.0,
        };
        using var fix = BuildFixture(seed, auditor,
            classMembers: [AgentKind.Gemini, AgentKind.Codex],
            quotas: new() { [AgentKind.Gemini] = 20.0, [AgentKind.Codex] = 1.0 },
            quotaOptions: quotaOptions,
            quotaResetAt: reset);
        fix.Codex!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([AgentKind.Codex], auditor.Invocations);
    }

    [Fact]
    public async Task ExplicitAuditQuotaOptions_BuildAuditGateEvenWhenRouterHasDifferentPolicy()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        var reset = DateTimeOffset.UtcNow + TimeSpan.FromDays(7);
        var routerOptions = new QuotaRouterOptions
        {
            MinQuotaPct = 10.0,
            StartFloorPct = 25.0,
            EndFloorPct = 3.0,
            RampWindow = TimeSpan.FromDays(7),
        };
        var auditOptions = new QuotaRouterOptions
        {
            MinQuotaPct = 10.0,
            StartFloorPct = 25.0,
            EndFloorPct = 3.0,
            RampWindow = TimeSpan.FromDays(7),
        };
        auditOptions.FloorByAgent[AgentKind.Codex.Value] = new QuotaFloorOverrideOptions
        {
            MinQuotaPct = 1.0,
            StartFloorPct = 1.0,
            EndFloorPct = 0.0,
        };
        using var fix = BuildFixture(seed, auditor,
            classMembers: [AgentKind.Gemini, AgentKind.Codex],
            quotas: new() { [AgentKind.Gemini] = 20.0, [AgentKind.Codex] = 1.0 },
            routerQuotaOptions: routerOptions,
            auditQuotaOptions: auditOptions,
            quotaResetAt: reset);
        fix.Codex!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([AgentKind.Codex], auditor.Invocations);
    }

    [Fact]
    public async Task PreferredWindowBelowFloor_AuditFallsThroughToClassMember()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        var reset = DateTimeOffset.UtcNow + TimeSpan.FromDays(7);
        var quotaOptions = new QuotaRouterOptions
        {
            MinQuotaPct = 10.0,
            StartFloorPct = 25.0,
            EndFloorPct = 3.0,
            RampWindow = TimeSpan.FromDays(7),
            MinQuotaPctByWindow = new(StringComparer.OrdinalIgnoreCase)
            {
                ["five_hour"] = 25.0,
            },
        };
        using var fix = BuildFixture(seed, auditor,
            classMembers: [AgentKind.Gemini, AgentKind.Codex],
            quotas: new() { [AgentKind.Gemini] = 80.0, [AgentKind.Codex] = 80.0 },
            quotaOptions: quotaOptions,
            quotaResetAt: reset,
            quotaWindows: new()
            {
                [AgentKind.Gemini] = [new WindowQuota { Name = "five_hour", AvailablePct = 10.0, ResetAt = reset }],
                [AgentKind.Codex] = [new WindowQuota { Name = "five_hour", AvailablePct = 80.0, ResetAt = reset }],
            });
        fix.Codex!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([AgentKind.Codex], auditor.Invocations);
    }

    [Fact]
    public async Task PreferredBudgetWithSoonerResetBelowProviderRamp_AuditFallsThroughToClassMember()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        var now = DateTimeOffset.UtcNow;
        var providerReset = now + TimeSpan.FromDays(7);
        var budgetReset = now + TimeSpan.FromMinutes(1);
        var quotaOptions = new QuotaRouterOptions
        {
            MinQuotaPct = 10.0,
            StartFloorPct = 25.0,
            EndFloorPct = 3.0,
            RampWindow = TimeSpan.FromDays(7),
        };
        using var fix = BuildFixture(seed, auditor,
            classMembers: [AgentKind.Gemini, AgentKind.Codex],
            quotas: new() { [AgentKind.Gemini] = 80.0, [AgentKind.Codex] = 80.0 },
            quotaOptions: quotaOptions,
            quotaResetAt: providerReset,
            budgetProvider: new FakeBudgetProvider(
                new() { [AgentKind.Gemini] = 20.0 },
                resetAt: budgetReset));
        fix.Codex!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([AgentKind.Codex], auditor.Invocations);
    }

    [Fact]
    public async Task PreferredBudgetResetLaterThanProviderReset_AuditUsesProviderResetFloor()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        var now = DateTimeOffset.UtcNow;
        var providerReset = now + TimeSpan.FromMinutes(1);
        var budgetReset = now + TimeSpan.FromDays(7);
        var quotaOptions = new QuotaRouterOptions
        {
            MinQuotaPct = 10.0,
            StartFloorPct = 25.0,
            EndFloorPct = 3.0,
            RampWindow = TimeSpan.FromDays(7),
        };
        using var fix = BuildFixture(seed, auditor,
            classMembers: [AgentKind.Gemini, AgentKind.Codex],
            quotas: new() { [AgentKind.Gemini] = 80.0, [AgentKind.Codex] = 80.0 },
            quotaOptions: quotaOptions,
            quotaResetAt: providerReset,
            budgetProvider: new FakeBudgetProvider(
                new() { [AgentKind.Gemini] = 20.0 },
                resetAt: budgetReset));
        fix.Codex!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([AgentKind.Gemini], auditor.Invocations);
    }

    // ── Probe-throws + UnknownPolicy branches in EvaluateAuditCandidateQuotaAsync ─

    [Fact]
    public async Task PreferredProbeThrows_FailOpen_AuditorRunsOnPreferredAgent()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        using var fix = BuildFixture(seed, auditor,
            classMembers: [AgentKind.Gemini, AgentKind.Codex],
            throwingProbes: [AgentKind.Gemini],
            unknownPolicy: QuotaUnknownPolicy.FailOpen);
        fix.Codex!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        // FailOpen treats a throwing probe as "available", so the preferred
        // audit agent (gemini) is used — the chain walk is never entered.
        Assert.Equal([AgentKind.Gemini], auditor.Invocations);
    }

    [Fact]
    public async Task PreferredProbeThrows_FailCautious_AuditorFallsThroughToClassMember()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        using var fix = BuildFixture(seed, auditor,
            classMembers: [AgentKind.Gemini, AgentKind.Codex],
            throwingProbes: [AgentKind.Gemini],
            unknownPolicy: QuotaUnknownPolicy.FailCautious);
        fix.Codex!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        // FailCautious treats the throwing preferred probe as "rejected",
        // forcing fallback to the class chain — codex (the next available)
        // runs the auditor.
        Assert.Equal([AgentKind.Codex], auditor.Invocations);
    }

    [Fact]
    public async Task PreferredProbeUnknown_FailOpen_AuditorRunsOnPreferredAgent()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        using var fix = BuildFixture(seed, auditor,
            classMembers: [AgentKind.Gemini, AgentKind.Codex],
            quotas: new()
            {
                // Negative pct means "unknown" to ResolveMemberQuota.
                [AgentKind.Gemini] = -1.0,
                [AgentKind.Codex] = 80.0,
            },
            unknownPolicy: QuotaUnknownPolicy.FailOpen);
        fix.Codex!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        // FailOpen on an unknown probe value means the preferred agent is
        // accepted as available.
        Assert.Equal([AgentKind.Gemini], auditor.Invocations);
    }

    [Fact]
    public async Task PreferredProbeUnknown_FailCautious_AuditorFallsThroughToClassMember()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        using var fix = BuildFixture(seed, auditor,
            classMembers: [AgentKind.Gemini, AgentKind.Codex],
            quotas: new()
            {
                [AgentKind.Gemini] = -1.0,
                [AgentKind.Codex] = 80.0,
            },
            unknownPolicy: QuotaUnknownPolicy.FailCautious);
        fix.Codex!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        // FailCautious rejects the unknown preferred probe; chain falls
        // through to codex.
        Assert.Equal([AgentKind.Codex], auditor.Invocations);
    }

    // ── Local-budget MIN gate in EvaluateAuditCandidateQuotaAsync ────────────

    [Fact]
    public async Task PreferredBudgetExhausted_ProbeHealthy_AuditFallsThroughToClassMember()
    {
        // The preferred audit agent (gemini) has a healthy real probe (80%) but
        // its operator spend budget is exhausted (1% < MinQuotaPct). MIN(probe,
        // budget) must reject gemini, so the audit falls through to codex,
        // whose budget is unconfigured (null → ignored) and whose probe is
        // healthy.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        using var fix = BuildFixture(seed, auditor,
            classMembers: [AgentKind.Gemini, AgentKind.Codex],
            quotas: new() { [AgentKind.Gemini] = 80.0, [AgentKind.Codex] = 80.0 },
            budgetProvider: new FakeBudgetProvider(new() { [AgentKind.Gemini] = 1.0 }));
        fix.Codex!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([AgentKind.Codex], auditor.Invocations);
    }

    [Fact]
    public async Task PreferredBudgetAndProbeHealthy_AuditRunsOnPreferredAgent()
    {
        // Both the real probe and the local budget for gemini are above the
        // floor. MIN(80, 50) = 50 >= MinQuotaPct, so the preferred audit agent is
        // accepted — confirming the budget gate does not spuriously reject a
        // healthy budget (guards against an inverted MIN that would still allow
        // here but reject in the exhausted-budget test above).
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        using var fix = BuildFixture(seed, auditor,
            classMembers: [AgentKind.Gemini, AgentKind.Codex],
            quotas: new() { [AgentKind.Gemini] = 80.0, [AgentKind.Codex] = 80.0 },
            budgetProvider: new FakeBudgetProvider(new() { [AgentKind.Gemini] = 50.0 }));
        fix.Codex!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([AgentKind.Gemini], auditor.Invocations);
    }

    [Fact]
    public async Task PreferredProbeExhausted_BudgetHealthy_AuditFallsThroughToClassMember()
    {
        // Symmetric to PreferredBudgetExhausted_ProbeHealthy: the preferred audit
        // agent (gemini) has a HEALTHY budget (80%) but an EXHAUSTED real
        // probe (1%). MIN(probe 1, budget 80) = 1 <
        // MinQuotaPct must reject gemini. If the combination were MAX, or dropped
        // the probe, gemini would be wrongly accepted. The audit therefore falls
        // through to codex (healthy probe, unconfigured budget).
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        using var fix = BuildFixture(seed, auditor,
            classMembers: [AgentKind.Gemini, AgentKind.Codex],
            quotas: new() { [AgentKind.Gemini] = 1.0, [AgentKind.Codex] = 80.0 },
            budgetProvider: new FakeBudgetProvider(new() { [AgentKind.Gemini] = 80.0 }));
        fix.Codex!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([AgentKind.Codex], auditor.Invocations);
    }

    [Fact]
    public async Task BudgetProviderThrows_FailsClosed_AuditFallsThroughToClassMember()
    {
        // The budget provider throws for the preferred audit agent (gemini). We
        // cannot verify the spend cap, so the gate must fail closed (reject
        // gemini) rather than silently drop the budget constraint. The audit
        // falls through to codex, whose budget lookup succeeds (null → ignored).
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        using var fix = BuildFixture(seed, auditor,
            classMembers: [AgentKind.Gemini, AgentKind.Codex],
            quotas: new() { [AgentKind.Gemini] = 80.0, [AgentKind.Codex] = 80.0 },
            budgetProvider: new FakeBudgetProvider(new(), throwFor: [AgentKind.Gemini]));
        fix.Codex!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([AgentKind.Codex], auditor.Invocations);
    }

    [Fact]
    public async Task PreferredProbeThrows_HealthyBudget_FailCautious_FallsBackToBudgetAndRunsPreferred()
    {
        // The preferred audit agent's real probe throws (transient error) but its
        // operator budget is healthy (80%). The throw must be treated as unknown
        // (-1) and fall through to MIN(probe, budget) = budget, NOT short-circuit on
        // the FailCautious UnknownPolicy. So gemini runs despite FailCautious,
        // because the configured budget vouches for it. Without the MIN fallthrough
        // (the throw-path returning early) gemini would be rejected and the audit
        // would fall through to codex — a fail-closed bypass of a healthy budget.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        using var fix = BuildFixture(seed, auditor,
            classMembers: [AgentKind.Gemini, AgentKind.Codex],
            quotas: new() { [AgentKind.Codex] = 80.0 },
            throwingProbes: [AgentKind.Gemini],
            unknownPolicy: QuotaUnknownPolicy.FailCautious,
            budgetProvider: new FakeBudgetProvider(new() { [AgentKind.Gemini] = 80.0 }));
        fix.Codex!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([AgentKind.Gemini], auditor.Invocations);
    }

    [Fact]
    public async Task NoAuditProbeButHealthyBudget_AuditRunsOnPreferredAgent()
    {
        // No real audit probes are registered, but the preferred audit agent has
        // a healthy configured budget. The probe-less branch must treat the
        // budget's concrete available percentage as "available" and run the
        // preferred agent rather than reverting to a blanket probe-less allow that
        // ignores the configured spend cap.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        using var fix = BuildFixture(seed, auditor,
            classMembers: [AgentKind.Gemini, AgentKind.Codex],
            quotas: new() { [AgentKind.Gemini] = 80.0, [AgentKind.Codex] = 80.0 },
            budgetProvider: new FakeBudgetProvider(new() { [AgentKind.Gemini] = 80.0 }),
            registerAuditProbes: false);
        fix.Codex!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([AgentKind.Gemini], auditor.Invocations);
    }

    [Fact]
    public async Task NoAuditProbe_BudgetResetDoesNotDriveProviderRampFloor()
    {
        // With no real audit probe, a local budget is the only concrete gate.
        // Its reset timestamp is not a provider quota-window reset, so the gate
        // must fall back to MinQuotaPct instead of applying the early-window
        // StartFloorPct ramp. Budget 20% is healthy against MinQuotaPct=10 but
        // would be rejected against StartFloorPct=25 if the reset leaked through.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RecordingLlmAuditor("security:llm-review");
        var auditOptions = new QuotaRouterOptions
        {
            MinQuotaPct = 10.0,
            StartFloorPct = 25.0,
            EndFloorPct = 3.0,
            RampWindow = TimeSpan.FromDays(7),
        };
        using var fix = BuildFixture(seed, auditor,
            classMembers: [AgentKind.Gemini, AgentKind.Codex],
            quotas: new() { [AgentKind.Gemini] = 80.0, [AgentKind.Codex] = 80.0 },
            budgetProvider: new FakeBudgetProvider(
                new() { [AgentKind.Gemini] = 20.0 },
                resetAt: DateTimeOffset.UtcNow + TimeSpan.FromDays(7)),
            registerAuditProbes: false,
            auditQuotaOptions: auditOptions);
        fix.Codex!.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([AgentKind.Gemini], auditor.Invocations);
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private RoutingFixture BuildFixture(
        string seedRepoUrl,
        RecordingLlmAuditor auditor,
        IReadOnlyList<AgentKind> classMembers,
        Dictionary<AgentKind, double>? quotas = null,
        IReadOnlyList<AgentKind>? throwingProbes = null,
        QuotaUnknownPolicy unknownPolicy = QuotaUnknownPolicy.UseObservedFailures,
        IAgentBudgetProvider? budgetProvider = null,
        bool registerAuditProbes = true,
        QuotaRouterOptions? quotaOptions = null,
        QuotaRouterOptions? routerQuotaOptions = null,
        QuotaRouterOptions? auditQuotaOptions = null,
        DateTimeOffset? quotaResetAt = null,
        Dictionary<AgentKind, IReadOnlyList<WindowQuota>>? quotaWindows = null,
        bool wireRetrier = false)
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
        var codex = agents.FirstOrDefault(a => a.Kind == AgentKind.Codex);
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

        var throwSet = throwingProbes is null
            ? new HashSet<AgentKind>()
            : new HashSet<AgentKind>(throwingProbes);
        var configurableProbes = classMembers
            .Select(kind => new ConfigurableProbe(
                kind,
                quotas?.GetValueOrDefault(kind, 80.0) ?? 80.0,
                quotaResetAt,
                quotaWindows?.GetValueOrDefault(kind),
                shouldThrow: throwSet.Contains(kind)))
            .ToList();
        var probes = configurableProbes.Cast<IAgentQuotaProbe>().ToList();
        var effectiveRouterQuotaOptions = routerQuotaOptions ?? quotaOptions ?? new QuotaRouterOptions { MinQuotaPct = 10.0 };
        var effectiveAuditQuotaOptions = auditQuotaOptions ?? quotaOptions ?? effectiveRouterQuotaOptions;
        effectiveRouterQuotaOptions.UnknownPolicy = unknownPolicy;
        effectiveAuditQuotaOptions.UnknownPolicy = unknownPolicy;

        var router = new AgentClassRouter(
            [frontier],
            probes,
            effectiveRouterQuotaOptions,
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

        var queue = wireRetrier ? new InMemoryTaskQueue() : null;
        var retrier = wireRetrier
            ? new WorkItemRetrier(store, queue!, gitHost, NullLogger<WorkItemRetrier>.Instance)
            : null;

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
            auditQuotaProbes: registerAuditProbes ? probes : null,
            auditQuotaOptions: effectiveAuditQuotaOptions,
            classRouter: router,
            fallbackHistory: fallbackHistory,
            quotaClassifier: new CompositeQuotaFailureClassifier(
            [
                new ClaudeQuotaFailureDetector(),
                new CodexQuotaFailureDetector(),
                new GeminiQuotaFailureDetector(),
            ]),
            budgetProvider: budgetProvider,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);

        return new RoutingFixture(pipeline, store, webhooks, codex, router, configurableProbes, retrier);
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
        private readonly bool _shouldThrow;
        private readonly DateTimeOffset? _resetAt;
        private readonly IReadOnlyList<WindowQuota> _windows;
        private double _pct;
        public ConfigurableProbe(
            AgentKind kind,
            double initialPct,
            DateTimeOffset? resetAt = null,
            IReadOnlyList<WindowQuota>? windows = null,
            bool shouldThrow = false)
        {
            Kind = kind;
            _pct = initialPct;
            _resetAt = resetAt;
            _windows = windows ?? [];
            _shouldThrow = shouldThrow;
        }
        public AgentKind Kind { get; }
        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
        {
            if (_shouldThrow)
                throw new InvalidOperationException("probe failure (test)");
            return Task.FromResult(new AgentQuotaSnapshot
            {
                AvailablePct = _pct,
                ResetAt = _resetAt,
                Windows = _windows,
            });
        }
        public Task MarkExhaustedAsync(AgentMembership member, TimeSpan ttl, DateTimeOffset? resetAt = null, CancellationToken ct = default)
        {
            _pct = 0.0;
            return Task.CompletedTask;
        }
        // Lets the resume test flip the probe back to healthy after the initial
        // park — mirrors the production "quota recovers, next probe sees fresh
        // headroom" sequence.
        public void SetAvailable(double pct) => _pct = pct;
    }

    private sealed class FakeBudgetProvider : IAgentBudgetProvider
    {
        private readonly Dictionary<AgentKind, double> _pct;
        private readonly HashSet<AgentKind> _throw;
        private readonly DateTimeOffset? _resetAt;

        public FakeBudgetProvider(
            Dictionary<AgentKind, double> pct,
            IEnumerable<AgentKind>? throwFor = null,
            DateTimeOffset? resetAt = null)
        {
            _pct = pct;
            _throw = throwFor is null ? new() : new(throwFor);
            _resetAt = resetAt;
        }

        public Task<AgentQuotaSnapshot?> GetBudgetSnapshotAsync(AgentKind agent, string? modelId, CancellationToken ct = default)
        {
            if (_throw.Contains(agent))
                throw new InvalidOperationException("budget provider failure (test)");
            // null = no budget configured for this agent → router ignores the gate.
            return Task.FromResult(_pct.TryGetValue(agent, out var p)
                ? new AgentQuotaSnapshot { AvailablePct = p, ResetAt = _resetAt }
                : null);
        }

        public Task<IReadOnlyList<AgentBudgetUsageView>> SummariseAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentBudgetUsageView>>([]);
    }

    // Returns a non-null AgentCredential for every kind so the resolver's
    // credential gate does not short-circuit to workRunner — quota routing
    // is what these tests are actually exercising.
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
        AgentClassRouter Router,
        IReadOnlyList<ConfigurableProbe> Probes,
        WorkItemRetrier? Retrier) : IDisposable
    {
        public void Dispose() => Store.Dispose();
    }
}
