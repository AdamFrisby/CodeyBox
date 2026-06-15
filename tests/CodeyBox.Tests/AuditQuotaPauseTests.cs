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

[Collection("Pipeline integration")]
public sealed class AuditQuotaPauseTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-audit-quota-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task AuditLlmQuotaFailure_AllClassMembersExhausted_ParksWorkItemForQuotaReset()
    {
        // When every member of the work item's agent class is quota-exhausted
        // for an LLM auditor, the audit gate cannot complete this iteration.
        // A Pass verdict requires every configured auditor to have produced
        // a verdict, so the work item PARKS in WaitingForQuotaReset and the
        // QuotaRetryScheduler resumes the same iteration when quota returns —
        // silently skipping the auditor would let a Pass verdict emerge with
        // an incomplete review set (the original warning-and-skip bug).
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RoutingLlmAuditor("cheating:llm-review", _ => QuotaResult());
        using var fix = BuildFixture(seed, auditor, [AgentKind.Gemini]);
        fix.Gemini.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Gemini);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.NotEqual(WorkItemState.Done, final.State);
        Assert.Contains(fix.Webhooks.Events, e => e.Event == "work_item.waiting_for_quota_reset");
    }

    [Fact]
    public async Task AuditLlmQuotaFailure_FallsBackWithinAgentClassBeforeParking()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RoutingLlmAuditor("cheating:llm-review", invocation =>
            invocation.Agent == AgentKind.Gemini ? QuotaResult() : new AuditResult(true, []));
        using var fix = BuildFixture(seed, auditor, [AgentKind.Gemini, AgentKind.Codex]);
        fix.Gemini.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Gemini);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.DoesNotContain(fix.Webhooks.Events, e => e.Event == "work_item.waiting_for_quota_reset");
        Assert.Equal([AgentKind.Gemini, AgentKind.Codex], auditor.Invocations);

        var history = await fix.FallbackHistory.ListByWorkItemAsync(item.Id, CancellationToken.None);
        var fallback = Assert.Single(history);
        Assert.Equal("audit", fallback.Phase);
        Assert.Equal(1, fallback.Iteration);
        Assert.Equal(AgentKind.Gemini, fallback.FromAgent);
        Assert.Equal(AgentKind.Codex, fallback.ToAgent);
    }

    [Fact]
    public async Task AuditLlmQuotaFailure_FallbackSkipsCandidateWithRecentObservedFailure()
    {
        // Bug 779e7dc9 also added an observed-failure short-circuit to the
        // in-iteration fallback loop in InvokeAgentWithQuotaFallbackAsync:
        // candidates with a recent entry in IQuotaFailureStore.HasRecentAsync
        // are skipped before the next attempt runs. This test pre-seeds an
        // observation for claude, then makes the auditor fail on gemini so
        // the fallback loop iterates [gemini→already-tried, claude→observed-
        // failure skip, codex→picked]. Without the new branch, claude would
        // be invoked first and the auditor would burn an extra round-trip on
        // a known-bad agent.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var dbPath = Path.Combine(_workspace, $"quota-failures-{Guid.NewGuid():N}.db");
        using var quotaFailures = new SqliteQuotaFailureStore(dbPath);
        await quotaFailures.RecordAsync(
            AgentKind.Claude, modelId: null,
            QuotaFailureKind.LimitReached, DateTimeOffset.UtcNow);

        var auditor = new RoutingLlmAuditor("cheating:llm-review", invocation =>
            invocation.Agent == AgentKind.Gemini ? QuotaResult() : new AuditResult(true, []));
        using var fix = BuildFixture(seed, auditor,
            classMembers: [AgentKind.Gemini, AgentKind.Claude, AgentKind.Codex],
            quotaFailures: quotaFailures);
        fix.Gemini.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Gemini);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        // Auditor must NOT have been invoked on claude — the observed-failure
        // skip branch in InvokeAgentWithQuotaFallbackAsync filters it out
        // before the next attempt. Order must be exactly gemini→codex.
        Assert.Equal([AgentKind.Gemini, AgentKind.Codex], auditor.Invocations);
    }

    [Fact]
    public async Task AuditLlmQuotaFailure_ParksWhenAuditChainExhaustedOnFirstAttempt()
    {
        // When the audit-side quota fallback exhausts every class member on
        // the first attempt, the pipeline PARKS the work item in
        // WaitingForQuotaReset and arms the QuotaRetryScheduler. The earlier
        // warning-and-skip variant let a Pass verdict emerge with the LLM
        // auditor never having run, which violates the per-auditor
        // independent-gate contract — fixing that is the whole point of this
        // change. With a class of one (gemini-only) and a quota-failing
        // auditor, the only correct outcome is to park.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RoutingLlmAuditor("cheating:llm-review", _ =>
            _.CallNumber == 1 ? QuotaResult() : new AuditResult(true, []));
        using var fix = BuildFixture(seed, auditor, [AgentKind.Gemini]);
        fix.Gemini.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Gemini);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.NotNull(final.NextQuotaRetryAt);
        Assert.Contains(fix.Webhooks.Events, e => e.Event == "work_item.waiting_for_quota_reset");
    }

    [Fact]
    public async Task AuditRouting_SkipsPausedPreferredAuditorAndUsesClassFallback()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var pauseDb = Path.Combine(_workspace, $"audit-pauses-{Guid.NewGuid():N}.db");
        using var pauses = new SqliteAgentPauseController(
            pauseDb,
            NullLogger<SqliteAgentPauseController>.Instance);
        await pauses.PauseAsync(AgentKind.Codex, "provider outage", "test");

        var auditor = new RoutingLlmAuditor("cheating:llm-review", _ => new AuditResult(true, []));
        using var fix = BuildFixture(
            seed,
            auditor,
            [AgentKind.Gemini, AgentKind.Codex],
            pauses: pauses,
            auditAgent: AgentKind.Codex);
        fix.Gemini.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Gemini);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([AgentKind.Gemini], auditor.Invocations);
    }

    [Fact]
    public async Task AuditRouting_PausedPreferredAndQuotaBlockedFallback_ParksForQuotaReset()
    {
        // Mixed cause: the preferred audit agent (codex) is operator-paused
        // and the work agent (gemini) is below the quota floor. The auditor
        // cannot run, but the proximate cause is QUOTA, not the operator
        // pause — the pause only matters because we walked past codex while
        // looking for a non-exhausted member. The pipeline must park for
        // QUOTA reset (not agent resume) so the QuotaRetryScheduler resumes
        // the iteration when the gemini probe recovers; parking for agent
        // resume would idle the item until an operator manually unpaused
        // codex even though quota is the actual blocker.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var pauseDb = Path.Combine(_workspace, $"audit-pauses-{Guid.NewGuid():N}.db");
        using var pauses = new SqliteAgentPauseController(
            pauseDb,
            NullLogger<SqliteAgentPauseController>.Instance);
        await pauses.PauseAsync(AgentKind.Codex, "audit subscription reserved", "test");

        var auditor = new RoutingLlmAuditor("cheating:llm-review", _ => new AuditResult(true, []));
        using var fix = BuildFixture(
            seed,
            auditor,
            [AgentKind.Gemini, AgentKind.Codex],
            pauses: pauses,
            auditAgent: AgentKind.Codex,
            auditProbeAvailablePct: new Dictionary<AgentKind, double>
            {
                [AgentKind.Gemini] = 0.0,
                [AgentKind.Codex] = 80.0,
            });
        fix.Gemini.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Gemini);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.NotEqual(WorkItemState.WaitingForAgentResume, final.State);
        Assert.Empty(auditor.Invocations);
        Assert.DoesNotContain(fix.Webhooks.Events, e => e.Event == "work_item.waiting_for_agent_resume");
        Assert.Contains(fix.Webhooks.Events, e => e.Event == "work_item.waiting_for_quota_reset");
    }

    [Fact]
    public async Task AuditRouting_AllAuditCapableAgentsPaused_ParksForAgentResume()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var pauseDb = Path.Combine(_workspace, $"audit-pauses-{Guid.NewGuid():N}.db");
        using var pauses = new SqliteAgentPauseController(
            pauseDb,
            NullLogger<SqliteAgentPauseController>.Instance);
        await pauses.PauseAsync(AgentKind.Codex, "audit subscription reserved", "test");

        var auditor = new RoutingLlmAuditor("cheating:llm-review", _ => new AuditResult(true, []));
        using var fix = BuildFixture(
            seed,
            auditor,
            [AgentKind.Gemini, AgentKind.Codex],
            pauses: pauses,
            capabilities: new Dictionary<AgentKind, IReadOnlyList<string>>
            {
                [AgentKind.Codex] = [WellKnownCapabilities.Audit],
            });
        fix.Gemini.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Gemini);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WaitingForAgentResume, final!.State);
        Assert.Equal(AgentKind.Gemini, final.Agent);
        Assert.Equal(AgentKind.Codex, final.AgentPauseTarget);
        Assert.Equal("audit", final.AgentPauseRetryFrom);
        Assert.Null(final.QuotaRetryFrom);
        Assert.Empty(auditor.Invocations);
        Assert.Contains(fix.Webhooks.Events, e => e.Event == "work_item.waiting_for_agent_resume");
    }

    [Fact]
    public async Task AuditRouting_PausedCredentialedAuditMemberParksEvenWhenAnotherAuditMemberLacksCredentials()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var pauseDb = Path.Combine(_workspace, $"audit-pauses-{Guid.NewGuid():N}.db");
        using var pauses = new SqliteAgentPauseController(
            pauseDb,
            NullLogger<SqliteAgentPauseController>.Instance);
        await pauses.PauseAsync(AgentKind.Codex, "audit subscription reserved", "test");

        var auditor = new RoutingLlmAuditor("cheating:llm-review", _ => new AuditResult(true, []));
        using var fix = BuildFixture(
            seed,
            auditor,
            [AgentKind.Gemini, AgentKind.Codex, AgentKind.Claude],
            pauses: pauses,
            capabilities: new Dictionary<AgentKind, IReadOnlyList<string>>
            {
                [AgentKind.Codex] = [WellKnownCapabilities.Audit],
                [AgentKind.Claude] = [WellKnownCapabilities.Audit],
            },
            missingCredentials: new HashSet<AgentKind> { AgentKind.Claude });
        fix.Gemini.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Gemini);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WaitingForAgentResume, final!.State);
        Assert.Equal(AgentKind.Gemini, final.Agent);
        Assert.Equal(AgentKind.Codex, final.AgentPauseTarget);
        Assert.Equal("audit", final.AgentPauseRetryFrom);
        Assert.Null(final.QuotaRetryFrom);
        Assert.Empty(auditor.Invocations);
    }

    [Fact]
    public async Task AuditRouting_PausedAuditMemberIgnoredWhenItemEligibilityRejectsIt()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var pauseDb = Path.Combine(_workspace, $"audit-pauses-{Guid.NewGuid():N}.db");
        using var pauses = new SqliteAgentPauseController(
            pauseDb,
            NullLogger<SqliteAgentPauseController>.Instance);
        await pauses.PauseAsync(AgentKind.Claude, "paused but not eligible for this item", "test");

        var auditor = new RoutingLlmAuditor("cheating:llm-review", _ => new AuditResult(true, []));
        using var fix = BuildFixture(
            seed,
            auditor,
            [AgentKind.Codex, AgentKind.Claude],
            pauses: pauses,
            capabilities: new Dictionary<AgentKind, IReadOnlyList<string>>
            {
                [AgentKind.Codex] = [WellKnownCapabilities.Audit, "sensitive"],
                [AgentKind.Claude] = [WellKnownCapabilities.Audit],
            });

        var codexMember = fix.Router
            .GetClassMembers("frontier")
            .Single(m => m.Agent == AgentKind.Codex);
        fix.Router.MarkExhausted(codexMember, TimeSpan.FromHours(1));

        var itemId = WorkItemId.New();
        var item = NewItem(AgentKind.Gemini) with
        {
            Id = itemId,
            State = WorkItemState.WorkComplete,
            WorkBranch = $"codeybox/{itemId.ToString()[..8]}",
            RequiredCapabilities = ["sensitive"],
        };
        var repoId = await fix.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await CommitToBareBranchAsync(
            fix.GitHost.GetRepoPath(repoId),
            item.WorkBranch!,
            "work.txt",
            "work complete\n",
            "work commit");
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.NotEqual(WorkItemState.WaitingForAgentResume, final.State);
        Assert.Null(final.AgentPauseTarget);
        Assert.Equal("audit", final.QuotaRetryFrom);
        Assert.Empty(auditor.Invocations);
    }

    [Fact]
    public async Task AuditRouting_LegacyNoTagPausedAuditPool_ParksForAgentResume()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var pauseDb = Path.Combine(_workspace, $"audit-pauses-{Guid.NewGuid():N}.db");
        using var pauses = new SqliteAgentPauseController(
            pauseDb,
            NullLogger<SqliteAgentPauseController>.Instance);
        await pauses.PauseAsync(AgentKind.Gemini, "audit subscription reserved", "test");

        var auditor = new RoutingLlmAuditor("cheating:llm-review", _ => new AuditResult(true, []));
        using var fix = BuildFixture(
            seed,
            auditor,
            [AgentKind.Gemini],
            pauses: pauses);

        var itemId = WorkItemId.New();
        var item = NewItem(AgentKind.Gemini) with
        {
            Id = itemId,
            State = WorkItemState.WorkComplete,
            WorkBranch = $"codeybox/{itemId.ToString()[..8]}",
        };
        var repoId = await fix.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await CommitToBareBranchAsync(
            fix.GitHost.GetRepoPath(repoId),
            item.WorkBranch!,
            "work.txt",
            "work complete\n",
            "work commit");
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WaitingForAgentResume, final!.State);
        Assert.Equal(AgentKind.Gemini, final.AgentPauseTarget);
        Assert.Equal("audit", final.AgentPauseRetryFrom);
        Assert.Null(final.QuotaRetryFrom);
        Assert.Empty(auditor.Invocations);
    }

    [Fact]
    public async Task AuditPass_RequiresEveryConfiguredAuditorToHaveRun_WholePoolExhaustedDoesNotPass()
    {
        // Hard invariant: a Pass verdict must NEVER emerge while one or more
        // of the configured auditors did not run because its entire spill-to-
        // peer pool was quota-exhausted. The bug this fix targets had item
        // 286b7b44 silently pass with ALL 7 auditors skipped — a zero-review
        // audit being scored as a pass. With multiple LLM auditors configured
        // and every class member quota-failing each of them, the item must
        // PARK in WaitingForQuotaReset rather than completing Done. The
        // single-member class isolates the spill path: there is nowhere for
        // the wrapper to fall through to, so quota exhaustion is unambiguous.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var bugsAuditor = new RoutingLlmAuditor("bugs:llm-review", _ => QuotaResult());
        var securityAuditor = new RoutingLlmAuditor("security:llm-review", _ => QuotaResult());
        var cheatingAuditor = new RoutingLlmAuditor("cheating:llm-review", _ => QuotaResult());

        using var fix = BuildFixture(
            seed,
            [bugsAuditor, securityAuditor, cheatingAuditor],
            [AgentKind.Gemini]);
        fix.Gemini.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Gemini);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        // Pass verdict requires every configured auditor to have produced a
        // verdict — exhausted pools force a park, not a silent pass.
        Assert.NotEqual(WorkItemState.Done, final!.State);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, final.State);
        Assert.Contains(fix.Webhooks.Events, e => e.Event == "work_item.waiting_for_quota_reset");
        // The audit phase must not announce a pass when any auditor could
        // not run — there must be no audit.completed event marking Pass for
        // this iteration.
        Assert.DoesNotContain(
            fix.Webhooks.Events,
            e => e.Event == "audit.completed"
                 && e.Details is AuditCompletedDetails details
                 && string.Equals(details.Verdict, "pass", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AuditPass_QuotaReturnViaRetryResumesAndAllAuditorsRunBeforePass()
    {
        // Hard invariant: after the entire spill-to-peer pool exhausts and
        // parks the work item in WaitingForQuotaReset, the retry path
        // (QuotaRetryScheduler / WorkItemRetrier) must re-dispatch the item
        // and the resumed audit iteration must drive EVERY configured auditor
        // to a verdict before any Pass verdict is emitted. The first audit
        // iteration parked because every auditor's class-pool was exhausted;
        // the resumed iteration must not inherit a stale skipped-auditor
        // entry that would let the iteration pass with only the previously
        // successful sibling auditor having run.
        //
        // The auditor handler returns QuotaResult on its FIRST call (parking
        // attempt) and Pass on every subsequent call (post-quota-return).
        // Driving the same item through resume must therefore observe BOTH
        // auditors being invoked at least once more before Done.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var bugsAuditor = new RoutingLlmAuditor("bugs:llm-review", inv =>
            inv.CallNumber == 1 ? QuotaResult() : new AuditResult(true, []));
        var securityAuditor = new RoutingLlmAuditor("security:llm-review", inv =>
            inv.CallNumber == 1 ? QuotaResult() : new AuditResult(true, []));

        using var fix = BuildFixture(
            seed,
            [bugsAuditor, securityAuditor],
            [AgentKind.Gemini]);
        fix.Gemini.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Gemini);
        await fix.Store.CreateAsync(item);

        // First pass: the single-member class is exhausted for the parking
        // auditor → audit phase raises AgentClassExhaustedException → item
        // parks in WaitingForQuotaReset BEFORE any Pass verdict is emitted.
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var parked = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(parked);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, parked!.State);
        Assert.DoesNotContain(
            fix.Webhooks.Events,
            e => e.Event == "audit.completed"
                 && e.Details is AuditCompletedDetails details
                 && string.Equals(details.Verdict, "pass", StringComparison.OrdinalIgnoreCase));

        // Quota returns. Drive the retry path the same way QuotaRetryScheduler
        // does on the usable-threshold wakeup: WorkItemRetrier.RetryAsync
        // transitions the parked item back to WorkComplete (from=audit) and
        // re-enqueues it for pickup. This is the exact code path the
        // scheduler invokes on quota return.
        var (retrySuccess, retryError, _, _, _) = await fix.Retrier.RetryAsync(
            parked, from: "audit", trigger: "test-quota-return", CancellationToken.None);
        Assert.True(retrySuccess, retryError);

        // Advance the manual clock past the router's in-process exhaustion
        // TTL (default 1 h). Without this, the legacy no-tag class path's
        // new gating (cached-exhausted member → spill / park, never dispatch
        // on a cached-exhausted work runner) would correctly re-park the
        // resumed iteration. Production quota return both expires the TTL
        // AND clears the cache via a fresh probe write-back; here we just
        // age the cache so the next pickup observes Gemini as eligible.
        fix.Time.UtcNow = fix.Time.UtcNow.AddHours(2);

        var resumed = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(resumed);
        Assert.Equal(WorkItemState.WorkComplete, resumed!.State);

        // Second pickup: the worker pool would call pipeline.RunAsync on the
        // dequeued item — mirror that here. The audit phase must now run BOTH
        // auditors before Pass: a stale skipped-auditor entry surviving the
        // park would let the iteration pass with only the previously
        // successful sibling having run, which is exactly the regression
        // shape this fix forbids.
        await fix.Pipeline.RunAsync(resumed, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);

        // Every configured auditor must have produced a verdict on the
        // resumed iteration. The first call to each auditor was the
        // parking-attempt quota result; the resume must have driven each
        // auditor to a SECOND invocation (Pass) before the Done verdict.
        Assert.True(
            bugsAuditor.CallCount >= 2,
            $"bugs auditor must run on the resumed iteration before Pass; CallCount={bugsAuditor.CallCount}");
        Assert.True(
            securityAuditor.CallCount >= 2,
            $"security auditor must run on the resumed iteration before Pass; CallCount={securityAuditor.CallCount}");
    }

    [Fact]
    public async Task AuditPass_QuotaRetryAdmissionBypassesRecentObservedFailureBeforePass()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var dbPath = Path.Combine(_workspace, $"quota-failures-{Guid.NewGuid():N}.db");
        using var quotaFailures = new SqliteQuotaFailureStore(dbPath);
        var auditor = new RoutingLlmAuditor("bugs:llm-review", inv =>
            inv.CallNumber == 1 ? QuotaResult() : new AuditResult(true, []));

        using var fix = BuildFixture(
            seed,
            auditor,
            [AgentKind.Gemini],
            quotaFailures: quotaFailures);
        fix.Gemini.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Gemini);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var parked = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(parked);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, parked!.State);

        await quotaFailures.RecordAsync(
            AgentKind.Gemini,
            modelId: null,
            QuotaFailureKind.LimitReached,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        Assert.True(await quotaFailures.HasRecentAsync(
            AgentKind.Gemini,
            modelId: null,
            TimeSpan.FromHours(1),
            DateTimeOffset.UtcNow,
            CancellationToken.None));

        var retryDecision = await fix.Router.ResolveQuotaRetryAsync(
            parked,
            project: null,
            CancellationToken.None,
            WellKnownCapabilities.Audit);
        Assert.False(retryDecision.ShouldWait, retryDecision.Reason);
        Assert.False(retryDecision.NoEligibleMembers, retryDecision.Reason);

        var (retrySuccess, retryError, _, _, _) = await fix.Retrier.RetryAsync(
            parked, from: "audit", trigger: "test-quota-return", CancellationToken.None);
        Assert.True(retrySuccess, retryError);

        var resumed = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(resumed);
        Assert.Equal(WorkItemState.WorkComplete, resumed!.State);

        await fix.Pipeline.RunAsync(resumed, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([AgentKind.Gemini, AgentKind.Gemini], auditor.Invocations);
    }

    [Fact]
    public async Task AuditPass_QuotaRetryAdmissionBypassesStaleInProcessExhaustionBeforePass()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RoutingLlmAuditor("bugs:llm-review", inv =>
            inv.CallNumber == 1 ? QuotaResult() : new AuditResult(true, []));

        using var fix = BuildFixture(seed, auditor, [AgentKind.Gemini]);
        fix.Gemini.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Gemini);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var parked = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(parked);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, parked!.State);

        var member = Assert.Single(fix.Router.GetClassMembers("frontier"), m => m.Agent == AgentKind.Gemini);
        Assert.True(
            fix.Router.IsExhausted(member, fix.Time.UtcNow),
            "the retry admission must bypass the still-live in-process exhaustion cache");

        var retryDecision = await fix.Router.ResolveQuotaRetryAsync(
            parked,
            project: null,
            CancellationToken.None,
            WellKnownCapabilities.Audit);
        Assert.False(retryDecision.ShouldWait, retryDecision.Reason);
        Assert.False(retryDecision.NoEligibleMembers, retryDecision.Reason);

        var (retrySuccess, retryError, _, _, _) = await fix.Retrier.RetryAsync(
            parked, from: "audit", trigger: "test-quota-return", CancellationToken.None);
        Assert.True(retrySuccess, retryError);

        var resumed = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(resumed);
        Assert.Equal(WorkItemState.WorkComplete, resumed!.State);

        await fix.Pipeline.RunAsync(resumed, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([AgentKind.Gemini, AgentKind.Gemini], auditor.Invocations);
    }

    [Fact]
    public async Task AuditPass_QuotaRetryAdmissionBypassesRecentObservedFailureForEveryResolvedAuditor()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var dbPath = Path.Combine(_workspace, $"quota-failures-{Guid.NewGuid():N}.db");
        using var quotaFailures = new SqliteQuotaFailureStore(dbPath);
        var firstAuditor = new RoutingLlmAuditor("bugs:llm-review", inv =>
            inv.CallNumber == 1 ? QuotaResult() : new AuditResult(true, []));
        var secondAuditor = new RoutingLlmAuditor("security:llm-review", _ => new AuditResult(true, []));

        using var fix = BuildFixture(
            seed,
            [firstAuditor, secondAuditor],
            [AgentKind.Gemini],
            quotaFailures: quotaFailures);
        fix.Gemini.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Gemini);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var parked = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(parked);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, parked!.State);

        await quotaFailures.RecordAsync(
            AgentKind.Gemini,
            modelId: null,
            QuotaFailureKind.LimitReached,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        Assert.True(await quotaFailures.HasRecentAsync(
            AgentKind.Gemini,
            modelId: null,
            TimeSpan.FromHours(1),
            DateTimeOffset.UtcNow,
            CancellationToken.None));

        var retryDecision = await fix.Router.ResolveQuotaRetryAsync(
            parked,
            project: null,
            CancellationToken.None,
            WellKnownCapabilities.Audit);
        Assert.False(retryDecision.ShouldWait, retryDecision.Reason);
        Assert.False(retryDecision.NoEligibleMembers, retryDecision.Reason);

        var (retrySuccess, retryError, _, _, _) = await fix.Retrier.RetryAsync(
            parked, from: "audit", trigger: "test-quota-return", CancellationToken.None);
        Assert.True(retrySuccess, retryError);

        var resumed = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(resumed);
        Assert.Equal(WorkItemState.WorkComplete, resumed!.State);

        await fix.Pipeline.RunAsync(resumed, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([AgentKind.Gemini, AgentKind.Gemini], firstAuditor.Invocations);
        Assert.Equal([AgentKind.Gemini, AgentKind.Gemini], secondAuditor.Invocations);
    }

    [Fact]
    public async Task AuditPass_PartialAuditorPoolExhaustion_ParksRatherThanCountingAsBlockingFinding()
    {
        // Regression cover for the 1aa5a13f swing: an auditor that cannot
        // run due to quota exhaustion must NOT be counted as a code-quality
        // finding either (the over-correction direction). When one auditor's
        // pool is exhausted but a sibling auditor passes, the item parks for
        // quota reset rather than auditing as failed — and rework iteration
        // count is NOT incremented (it would burn the audit budget on a
        // transient infra failure). The single-iteration budget pins the
        // counter behaviour: a false rework increment would terminally fail
        // the item AFTER park, but the park outcome is the only acceptable
        // terminal-of-this-pickup state.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var exhaustedAuditor = new RoutingLlmAuditor("bugs:llm-review", _ => QuotaResult());
        var passingAuditor = new RoutingLlmAuditor("style:llm-review", _ => new AuditResult(true, []));

        using var fix = BuildFixture(
            seed,
            [exhaustedAuditor, passingAuditor],
            [AgentKind.Gemini]);
        fix.Gemini.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Gemini);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.NotEqual(WorkItemState.Done, final.State);
        // Exhaustion must NOT be classified as audit_failed — the 1aa5a13f
        // false-AuditFailed regression direction.
        Assert.NotEqual(WorkItemState.AuditFailed, final.State);
        Assert.DoesNotContain(fix.Webhooks.Events, e => e.Event == "work_item.audit_failed");
    }

    private AuditQuotaFixture BuildFixture(
        string seedRepoUrl,
        RoutingLlmAuditor auditor,
        IReadOnlyList<AgentKind> classMembers,
        IQuotaFailureStore? quotaFailures = null,
        IAgentPauseController? pauses = null,
        AgentKind? auditAgent = null,
        IReadOnlyDictionary<AgentKind, IReadOnlyList<string>>? capabilities = null,
        IReadOnlySet<AgentKind>? missingCredentials = null,
        IReadOnlyDictionary<AgentKind, double>? auditProbeAvailablePct = null)
        => BuildFixture(
            seedRepoUrl, [auditor], classMembers, quotaFailures, pauses, auditAgent,
            capabilities, missingCredentials, auditProbeAvailablePct);

    private AuditQuotaFixture BuildFixture(
        string seedRepoUrl,
        IReadOnlyList<RoutingLlmAuditor> auditors,
        IReadOnlyList<AgentKind> classMembers,
        IQuotaFailureStore? quotaFailures = null,
        IAgentPauseController? pauses = null,
        AgentKind? auditAgent = null,
        IReadOnlyDictionary<AgentKind, IReadOnlyList<string>>? capabilities = null,
        IReadOnlySet<AgentKind>? missingCredentials = null,
        IReadOnlyDictionary<AgentKind, double>? auditProbeAvailablePct = null)
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
        var queue = new InMemoryTaskQueue();
        var time = new ManualClock(DateTimeOffset.UtcNow);

        // Register a ScriptableAgent for every class member so the routing test
        // can exercise classes containing arbitrary agent kinds (the fallback
        // loop test seeds gemini + claude + codex).
        var agentSet = new HashSet<AgentKind>(classMembers);
        agentSet.Add(AgentKind.Gemini);
        agentSet.Add(AgentKind.Codex);
        var scriptedAgents = agentSet.ToDictionary(k => k, k => new ScriptableAgent(k));
        var gemini = scriptedAgents[AgentKind.Gemini];
        var codex = scriptedAgents[AgentKind.Codex];
        var registry = new AgentRegistry(scriptedAgents.Values.Cast<IAgentRunner>().ToArray());

        var frontier = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members = classMembers
                .Select(kind => new AgentMembership
                {
                    Agent = kind,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 100,
                    Capabilities = capabilities is not null && capabilities.TryGetValue(kind, out var caps)
                        ? caps.ToList()
                        : [],
                })
                .ToList(),
        };
        var routeProbes = classMembers.Select(kind => new RecordingProbe(kind)).ToList();
        var auditProbes = classMembers
            .Select(kind => new RecordingProbe(
                kind,
                new AgentQuotaSnapshot
                {
                    AvailablePct = auditProbeAvailablePct is not null
                        && auditProbeAvailablePct.TryGetValue(kind, out var pct)
                        ? pct
                        : 80.0,
                }))
            .ToList();
        var dispatchAvailability = pauses is null
            ? null
            : new AgentDispatchAvailability(pauses: pauses);
        var sharedQuotaOptions = new QuotaRouterOptions { MinQuotaPct = 10 };
        var router = new AgentClassRouter(
            [frontier],
            routeProbes,
            sharedQuotaOptions,
            NullLogger<AgentClassRouter>.Instance,
            time,
            dispatchAvailability: dispatchAvailability);

        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Gemini,
            DefaultAgentClass = "frontier",
            Audit = new ProjectAudit
            {
                MaxIterations = 1,
                AuditTypes = ["scripted"],
                MaxLlmAuditorParallelism = 1,
                AuditAgent = auditAgent,
            },
        };
        var projects = new InMemoryProjectRepository(project);
        var fallbackHistory = new InMemoryAgentFallbackHistoryStore();
        var opts = new OrchestratorOptions
        {
            AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
            {
                Enabled = true,
                PeriodicCheckInterval = TimeSpan.FromHours(1),
                MaxAutoRetriesPerWorkItem = 3,
            },
        };
        var retrier = new WorkItemRetrier(store, queue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var scheduler = new QuotaRetryScheduler(
            store,
            retrier,
            opts,
            NullLogger<QuotaRetryScheduler>.Instance,
            router,
            projects,
            null,
            webhooks,
            time);

        var pipeline = new PipelineRunner(
            sandboxes,
            gitHost,
            registry,
            new SelectiveCredentialProvider(missingCredentials),
            prs,
            projects,
            new TestUpstreamFactory(),
            new ProjectAuditorComposer(new ScriptedAuditorCatalog([.. auditors])),
            store,
            webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            auditQuotaProbes: auditProbes,
            auditQuotaOptions: sharedQuotaOptions,
            retryScheduler: scheduler,
            classRouter: router,
            fallbackHistory: fallbackHistory,
            quotaFailures: quotaFailures,
            quotaClassifier: new CompositeQuotaFailureClassifier(
            [
                new ClaudeQuotaFailureDetector(),
                new CodexQuotaFailureDetector(),
                new GeminiQuotaFailureDetector(),
            ]),
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            dispatchAvailability: dispatchAvailability,
            agentPauseController: pauses);

        return new AuditQuotaFixture(
            pipeline,
            scheduler,
            retrier,
            store,
            queue,
            webhooks,
            fallbackHistory,
            time,
            router,
            gemini,
            codex,
            gitHost);
    }

    private static WorkItem NewItem(AgentKind agent) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "audit quota test",
        Prompt = "do thing",
        BaseBranch = "main",
        Agent = agent,
        AgentClassId = "frontier",
        PushUpstream = false,
    };

    private static AuditResult QuotaResult() => new(
        false,
        [new AuditFinding("cheating:llm-review", AuditSeverity.Error, "review agent failed to run", "quota")],
        AgentSummary: "agent exited 1",
        AgentStdout: "RESOURCE_EXHAUSTED reset after 13m");

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!cts.IsCancellationRequested)
        {
            if (await condition())
                return;
            await Task.Delay(25, cts.Token);
        }
        throw new TimeoutException("condition was not met");
    }

    private async Task CommitToBareBranchAsync(
        string barePath,
        string branch,
        string fileName,
        string contents,
        string subject)
    {
        var clone = Path.Combine(_workspace, "audit-pause-clone-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", barePath, clone);
        await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(clone, "config", "user.name", "Test");
        await TestSupport.RunGit(clone, "checkout", "-B", branch, "origin/main");
        await File.WriteAllTextAsync(Path.Combine(clone, fileName), contents);
        await TestSupport.RunGit(clone, "add", fileName);
        await TestSupport.RunGit(clone, "commit", "-m", $"{subject}\n\n{CodeyBoxTrailers.CoAuthoredBy}");
        await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{branch}");
    }

    private sealed class RoutingLlmAuditor : IAuditor
    {
        private readonly Func<Invocation, AuditResult> _handler;

        public RoutingLlmAuditor(string name, Func<Invocation, AuditResult> handler)
        {
            Name = name;
            _handler = handler;
        }

        public string Name { get; }
        public string Kind => "llm";
        public AuditCapabilities Required => AuditCapabilities.AgentCredentials;
        public int CallCount { get; private set; }
        public List<AgentKind> Invocations { get; } = [];

        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            CallCount++;
            var agent = context.AuditRunner?.Kind ?? AgentKind.Claude;
            Invocations.Add(agent);
            return Task.FromResult(_handler(new Invocation(agent, CallCount)));
        }
    }

    private sealed record Invocation(AgentKind Agent, int CallNumber);

    private sealed record AuditQuotaFixture(
        PipelineRunner Pipeline,
        QuotaRetryScheduler Scheduler,
        WorkItemRetrier Retrier,
        SqliteWorkItemStore Store,
        InMemoryTaskQueue Queue,
        CapturingWebhookDispatcher Webhooks,
        InMemoryAgentFallbackHistoryStore FallbackHistory,
        ManualClock Time,
        AgentClassRouter Router,
        ScriptableAgent Gemini,
        ScriptableAgent Codex,
        LocalGitHost GitHost) : IDisposable
    {
        public void Dispose()
        {
            Scheduler.Dispose();
            Store.Dispose();
        }
    }

    private sealed class SelectiveCredentialProvider(IReadOnlySet<AgentKind>? missing) : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
        {
            if (missing is not null && missing.Contains(agent))
                return Task.FromResult<AgentCredential?>(null);

            return Task.FromResult<AgentCredential?>(new AgentCredential(
                    agent,
                    new Dictionary<string, string>(),
                    new Dictionary<string, string>()));
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        public ManualClock(DateTimeOffset utcNow) => UtcNow = utcNow;

        public DateTimeOffset UtcNow { get; set; }

        public override DateTimeOffset GetUtcNow() => UtcNow;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => new NoopTimer();

        private sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
