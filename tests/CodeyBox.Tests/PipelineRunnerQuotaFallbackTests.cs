using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Gemini;
using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using Serilog;
using Serilog.Events;

namespace CodeyBox.Tests;

/// <summary>
/// Integration tests for in-iteration quota fallback inside the work phase
/// of <see cref="PipelineRunner"/>. The pipeline picks Codex first, Codex
/// returns a quota-shaped failure mid-iteration, and the wrapper retries the
/// same iteration against the next class member (Claude) without leaving the
/// item Failed. The 3-member exhaustion case parks the item in
/// <see cref="WorkItemState.WaitingForQuotaReset"/>.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class PipelineRunnerQuotaFallbackTests : IDisposable
{
    private readonly string _workspace;

    public PipelineRunnerQuotaFallbackTests() =>
        _workspace = Directory.CreateTempSubdirectory("codeybox-fallback-").FullName;

    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    [Fact]
    public async Task Codex_HitsQuota_FallsBackToClaude_EmitsFallbackAndInvocationMetrics()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);

        fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 1",
            Stdout: null,
            Stderr: "API Error: rate_limit_exceeded; please try again after 1h"));
        fix.Claude.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        using var metrics = new MetricCapture("codeybox.agent.fallbacks", "codeybox.agent.invocations");

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        // The quota fallback counter must record the codex→claude swap with the
        // quota kind on the work phase — driven by the real routing path, not a
        // hand-rolled Add.
        Assert.True(metrics.Any("codeybox.agent.fallbacks",
            ("from_agent", "codex"), ("to_agent", "claude"), ("kind", "quota"), ("phase", "work")));

        // Codex's failed attempt records an error-outcome invocation; Claude's
        // retry records a success-outcome invocation.
        Assert.True(metrics.Any("codeybox.agent.invocations",
            ("agent.kind", "codex"), ("phase", "work"), ("outcome", "error")));
        Assert.True(metrics.Any("codeybox.agent.invocations",
            ("agent.kind", "claude"), ("phase", "work"), ("outcome", "success")));
    }

    [Fact]
    public async Task NearZeroFloorRoutedCodexQuotaFailure_FallsBackToClaudeSameIteration()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var now = DateTimeOffset.UtcNow;
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
        quotaOptions.FloorByAgent[AgentKind.Codex.Value] = new QuotaFloorOverrideOptions
        {
            MinQuotaPct = 1.0,
            StartFloorPct = 1.0,
            EndFloorPct = 0.0,
        };
        using var fix = BuildPipeline(
            seed,
            quotaOptions: quotaOptions,
            codexQuotaSnapshot: new AgentQuotaSnapshot
            {
                AvailablePct = 1.0,
                ResetAt = now + TimeSpan.FromDays(7),
                Windows = [new WindowQuota { Name = "five_hour", AvailablePct = 1.0 }],
            },
            claudeQuotaSnapshot: new AgentQuotaSnapshot
            {
                AvailablePct = 80.0,
                ResetAt = now + TimeSpan.FromDays(7),
                Windows = [new WindowQuota { Name = "five_hour", AvailablePct = 80.0 }],
            });

        var item = NewItem(initialAgent: AgentKind.Codex);
        var routeDecision = await fix.Router.ResolveAsync(item, project: null, CancellationToken.None);
        var chosen = Assert.IsType<AgentMembership>(routeDecision.Chosen);
        Assert.Equal(AgentKind.Codex, chosen.Agent);
        item = item with
        {
            Agent = chosen.Agent,
            ModelId = chosen.ModelId,
            ReasoningMode = chosen.ReasoningMode,
        };

        fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 1",
            Stdout: null,
            Stderr: "API Error: rate_limit_exceeded; please try again after 1h"));
        fix.Claude.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(finalItem);
        Assert.Equal(WorkItemState.Done, finalItem!.State);

        var history = await fix.FallbackHistory.ListByWorkItemAsync(item.Id, CancellationToken.None);
        var swap = Assert.Single(history, h => h.Phase == "work" && h.ToAgent == AgentKind.Claude);
        Assert.Equal(AgentKind.Codex, swap.FromAgent);
    }

    [Fact]
    public async Task Codex_ExhaustsSessionResume_FallsBackToClaude_EmitsAuditHistoryAndMetric()
    {
        var sink = new TestSink();
        var previousLogger = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        try
        {
            var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
            using var fix = BuildPipeline(seed);

            fix.Codex.ScriptedExceptions.Enqueue(new AgentSessionResumeExhaustedException(
                AgentKind.Codex,
                maxResumeAttempts: 2,
                new AgentResult(false, "agent exited 1", null, "fatal resume state rejected")));
            fix.Claude.WorkPlan.Enqueue(new FileWrite("resume.txt", "recovered by fallback"));

            using var metrics = new MetricCapture("codeybox.agent.fallbacks");

            var item = NewItem(initialAgent: AgentKind.Codex);
            await fix.Store.CreateAsync(item);
            await fix.Pipeline.RunAsync(item, CancellationToken.None);

            var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
            Assert.Equal(WorkItemState.Done, finalItem!.State);

            Assert.True(fix.Codex.CallCount >= 1);
            Assert.Equal(1, fix.Claude.CallCount);

            var history = await fix.FallbackHistory.ListByWorkItemAsync(item.Id, CancellationToken.None);
            var fallback = Assert.Single(history, h => h.Phase == "work");
            Assert.Equal(AgentKind.Codex, fallback.FromAgent);
            Assert.Equal(AgentKind.Claude, fallback.ToAgent);
            Assert.Contains("exhausted 2 session resume", fallback.Reason);

            var webhook = Assert.Single(fix.Webhooks.Events, e => e.Event == "agent.fallback");
            var details = Assert.IsType<AgentFallbackDetails>(webhook.Details);
            Assert.Equal("work", details.Phase);
            Assert.Equal("codex", details.FromAgent);
            Assert.Equal("claude", details.ToAgent);
            Assert.Contains("exhausted 2 session resume", details.Reason);

            Assert.True(metrics.Any("codeybox.agent.fallbacks",
                    ("from_agent", "codex"), ("to_agent", "claude"), ("kind", "resume_exhausted"), ("phase", "work")),
                "expected a codeybox.agent.fallbacks{kind=resume_exhausted} measurement for the resume-exhausted swap");

            var audit = Assert.Single(sink.Events,
                e => GetScalar<string>(e, "EventName") == "agent.resume_exhausted_fallback");
            Assert.True(GetScalar<bool>(audit, "Audit"));
            Assert.Equal(LogEventLevel.Warning, audit.Level);
            Assert.Equal(item.Id.ToString(), GetScalar<string>(audit, "WorkItemId"));
            Assert.Equal("work", GetScalar<string>(audit, "Phase"));
            Assert.Null(GetScalar<int?>(audit, "Iteration"));
            Assert.Equal("codex", GetScalar<string>(audit, "FromAgent"));
            Assert.Equal("(default)", GetScalar<string>(audit, "FromModel"));
            Assert.Equal("claude", GetScalar<string>(audit, "ToAgent"));
            Assert.Equal("(default)", GetScalar<string>(audit, "ToModel"));
            Assert.Contains("exhausted 2 session resume", GetScalar<string>(audit, "Reason"));
        }
        finally
        {
            Log.CloseAndFlush();
            Log.Logger = previousLogger;
        }
    }

    [Fact]
    public async Task Codex_ExhaustsSessionResumeWithTransientLastResult_ParksForTransientRetryWithoutFallback()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);

        fix.Codex.ScriptedExceptions.Enqueue(new AgentSessionResumeExhaustedException(
            AgentKind.Codex,
            maxResumeAttempts: 2,
            new AgentResult(false, "agent exited 1", null, "ECONNRESET")));
        fix.Claude.WorkPlan.Enqueue(new FileWrite("should-not-run.txt", "fallback should not run"));

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(finalItem);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, finalItem!.State);
        Assert.Equal("transient", finalItem.FailureKind);
        Assert.Contains("transient transport failure after exhausting session resume", finalItem.LastError);
        Assert.True(fix.Codex.CallCount >= 1);
        Assert.Equal(0, fix.Claude.CallCount);
        Assert.Empty(await fix.FallbackHistory.ListByWorkItemAsync(item.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Codex_ExhaustsSessionResumeWithQuotaLastResult_FallsBackAsQuotaAndRecordsObservedFailure()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var quotaFailures = new InMemoryQuotaFailureStore();
        using var fix = BuildPipeline(seed, quotaFailures: quotaFailures);

        fix.Codex.ScriptedExceptions.Enqueue(new AgentSessionResumeExhaustedException(
            AgentKind.Codex,
            maxResumeAttempts: 2,
            new AgentResult(false, "agent exited 1", null, "hit your usage limit")));
        fix.Claude.WorkPlan.Enqueue(new FileWrite("resume-quota.txt", "recovered by quota fallback"));

        using var metrics = new MetricCapture("codeybox.agent.fallbacks");

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.Done, finalItem!.State);

        Assert.True(metrics.Any("codeybox.agent.fallbacks",
                ("from_agent", "codex"), ("to_agent", "claude"), ("kind", "quota"), ("phase", "work")),
            "resume exhaustion with quota-shaped LastResult must route through quota fallback, not resume_exhausted fallback");
        Assert.Contains(AgentKind.Codex, fix.CodexProbe.MarkedExhausted);

        var history = await fix.FallbackHistory.ListByWorkItemAsync(item.Id, CancellationToken.None);
        var fallback = Assert.Single(history, h => h.Phase == "work");
        Assert.Equal(AgentKind.Codex, fallback.FromAgent);
        Assert.Equal(AgentKind.Claude, fallback.ToAgent);
        Assert.Contains("quota failure after exhausting session resume", fallback.Reason);

        var observations = await quotaFailures.ListRecentAsync(
            TimeSpan.FromHours(1),
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);
        var observation = Assert.Single(observations);
        Assert.Equal(AgentKind.Codex, observation.Agent);
        Assert.Equal(QuotaFailureKind.LimitReached, observation.FailureKind);
        Assert.Equal(item.ProjectId, observation.ProjectId);
    }

    [Fact]
    public async Task Codex_ExhaustsSessionResumeWithConfiguredAuthPattern_FailsAuthRequiredWithoutFallback()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var availability = new AgentAvailabilityRegistry(
            new AvailabilityOptions(),
            TimeProvider.System,
            NullLogger<AgentAvailabilityRegistry>.Instance);
        var authClassifier = new AgentAuthFailureClassifier(
            new Dictionary<string, IReadOnlyList<AuthFailurePattern>>(StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = [new AuthFailurePattern("configured resume auth required")],
            });
        using var fix = BuildPipeline(
            seed,
            authFailureClassifier: authClassifier,
            availability: availability);

        fix.Codex.ScriptedExceptions.Enqueue(new AgentSessionResumeExhaustedException(
            AgentKind.Codex,
            maxResumeAttempts: 2,
            new AgentResult(false, "agent exited 1", null, "configured resume auth required")));
        fix.Claude.WorkPlan.Enqueue(new FileWrite("should-not-run.txt", "fallback must not run"));

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(finalItem);
        Assert.Equal(WorkItemState.Failed, finalItem!.State);
        Assert.Equal(WorkItemFailureKinds.AuthRequired, finalItem.FailureKind);
        Assert.Contains("auth required from agent output", finalItem.LastError);
        Assert.Equal(0, fix.Claude.CallCount);
        Assert.Empty(await fix.FallbackHistory.ListByWorkItemAsync(item.Id, CancellationToken.None));

        var current = availability.GetAvailability(AgentKind.Codex);
        Assert.False(current.Available);
        Assert.Contains("auth required from agent output", current.Reason);

        var failed = Assert.Single(fix.Webhooks.Events, e => e.Event == "agent.smoke_failed");
        var details = Assert.IsType<AgentSmokeFailedDetails>(failed.Details);
        Assert.Equal("codex", details.AgentKind);
        Assert.Equal(SmokeFailureCategory.Persistent, details.Category);
    }

    [Fact]
    public async Task SoleMember_ExhaustsSessionResume_FailsCleanlyWithoutFallback()
    {
        // Companion guard for the nextMember == null path in
        // MoveToNextMemberOrThrowAsync's ResumeExhausted branch. The sibling
        // Codex_ExhaustsSessionResume_FallsBackToClaude_… test exercises the
        // "fallback candidate exists → use it" branch; this test pins the
        // "no candidate left" branch: the AgentSessionResumeExhaustedException
        // must surface (NOT be silently swallowed, parked as quota, or
        // re-tried against the same exhausted agent), and the item must reach
        // a clean Failed terminal rather than spinning or stranding.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed, singleMemberClass: true);

        fix.Codex.ScriptedExceptions.Enqueue(new AgentSessionResumeExhaustedException(
            AgentKind.Codex,
            maxResumeAttempts: 2,
            new AgentResult(false, "agent exited 1", null, "fatal resume state rejected")));

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(finalItem);
        // Clean Failed terminal — not Done (no successful recovery), not
        // WaitingForQuotaReset (resume-exhausted is not a quota event), not
        // stuck in a non-terminal state.
        Assert.Equal(WorkItemState.Failed, finalItem!.State);
        // failureKind comes from the catch (Exception) default branch — the
        // resume-exhausted exception is not classified as quota or timeout.
        Assert.NotEqual("quota", finalItem.FailureKind);
        Assert.NotEqual("timeout", finalItem.FailureKind);

        // Codex was the only member; no fallback attempt against any other
        // agent could have occurred. The class chain MUST NOT have re-tried
        // the same exhausted agent in a loop either.
        Assert.Equal(1, fix.Codex.CallCount);

        // No swap recorded because no candidate was available.
        var history = await fix.FallbackHistory.ListByWorkItemAsync(item.Id, CancellationToken.None);
        Assert.DoesNotContain(history, h => h.Phase == "work" && h.ToAgent is not null);

        // No misleading "agent.fallback" webhook either: that event implies a
        // successor agent took over; here the run terminated.
        Assert.DoesNotContain(fix.Webhooks.Events, e => e.Event == "agent.fallback");
    }

    [Fact]
    public async Task AuditDrivenRework_EmitsReworkPhaseSpanAndDuration()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed, [new OnceFailingAuditor()], maxAuditIterations: 2);

        // Initial work + rework both pull from WorkPlan (rework re-uses it once
        // the scripted-failure queues drain), so enqueue two writes.
        fix.Codex.WorkPlan.Enqueue(new FileWrite("a.txt", "initial"));
        fix.Codex.WorkPlan.Enqueue(new FileWrite("a.txt", "reworked"));

        using var spans = new SpanCapture("CodeyBox.Pipeline", "CodeyBox.Audit");
        using var metrics = new MetricCapture(
            "codeybox.phase.duration_ms",
            "codeybox.auditor.duration_ms",
            "codeybox.audit.iterations");

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.Done, finalItem!.State);

        // The audit-driven rework path (not just the resume-preempt branch) must
        // open a phase.rework span and record the rework phase duration.
        Assert.True(spans.Any("phase.rework", ("codeybox.phase", "rework")),
            "expected a phase.rework span on the audit-loop rework path");
        Assert.True(spans.Any("agent.invoke", ("codeybox.phase", "rework")),
            "expected the rework agent.invoke span nested under phase.rework");
        Assert.True(metrics.Any("codeybox.phase.duration_ms", ("phase", "rework")),
            "expected a codeybox.phase.duration_ms{phase=rework} measurement");

        // Each audit iteration opens its own phase.audit span and records a
        // phase=audit duration sample, scoped to the auditing work only (the
        // scope is disposed before the rework scope opens). A regression that
        // dropped or mis-tagged the audit scope — or let it absorb nested
        // rework time — would slip past the rework-only assertions above.
        Assert.True(spans.Any("phase.audit", ("codeybox.phase", "audit")),
            "expected a phase.audit span on the audit loop");
        Assert.True(metrics.Any("codeybox.phase.duration_ms", ("phase", "audit")),
            "expected a codeybox.phase.duration_ms{phase=audit} measurement");

        // Each auditor invocation emits a CodeyBox.Audit `auditor.<name>` span
        // and a codeybox.auditor.duration_ms sample tagged with the auditor's
        // name + kind. These fire only from the real audit loop — the spec
        // declared both signals but production never emitted them before.
        Assert.True(spans.Any("auditor.once-failing-fallback", ("codeybox.phase", "audit")),
            "expected a CodeyBox.Audit auditor.<name> span for the tool auditor");
        Assert.True(metrics.Any("codeybox.auditor.duration_ms",
                ("auditor.name", "once-failing-fallback"), ("auditor.kind", "tool")),
            "expected a codeybox.auditor.duration_ms measurement tagged with the auditor name + kind");
        Assert.True(metrics.Any("codeybox.audit.iterations",
                ("outcome", "reworking"), ("self_review_checklist", "off"), ("iteration", "1")),
            "expected the first audit-iteration metric to carry the default opt-in-off self-review tag");
        Assert.True(metrics.Any("codeybox.audit.iterations",
                ("outcome", "passed"), ("self_review_checklist", "off"), ("iteration", "2")),
            "expected the passing audit-iteration metric to carry the default opt-in-off self-review tag");
    }

    [Fact]
    public async Task AuditIterationMetrics_TagSelfReviewChecklistOptIn()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var tuning = new PipelineTuningSnapshot(new PipelineTuningOptions
        {
            SelfReviewChecklistEnabled = true,
        });
        using var fix = BuildPipeline(
            seed,
            [new OnceFailingAuditor()],
            maxAuditIterations: 2,
            pipelineTuning: tuning);

        fix.Codex.WorkPlan.Enqueue(new FileWrite("a.txt", "initial"));
        fix.Codex.WorkPlan.Enqueue(new FileWrite("a.txt", "reworked"));

        using var metrics = new MetricCapture("codeybox.audit.iterations");

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.Done, finalItem!.State);
        Assert.True(metrics.Any("codeybox.audit.iterations",
                ("outcome", "reworking"), ("self_review_checklist", "on"), ("iteration", "1")),
            "expected the first audit-iteration metric to carry the opt-in-on self-review tag");
        Assert.True(metrics.Any("codeybox.audit.iterations",
                ("outcome", "passed"), ("self_review_checklist", "on"), ("iteration", "2")),
            "expected the passing audit-iteration metric to carry the opt-in-on self-review tag");
    }

    [Fact]
    public async Task ReworkFirstAttemptSmokeRejection_FallsBackBeforeInvokingRejectedRunner()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var smokeGate = new RejectingTargetInVmSmokeGate(AgentKind.Codex, "rework-profile");
        using var fix = BuildPipeline(
            seed,
            [new OnceFailingAuditor()],
            maxAuditIterations: 2,
            networkProfiles: new ProjectNetworkProfiles
            {
                Work = "work-profile",
                Rework = "rework-profile",
                Merge = "merge-profile",
            },
            inVmSmokeGate: smokeGate);

        var codexPhases = new List<string>();
        var claudePhases = new List<string>();
        fix.Codex.PhaseInvocationStarted += (_, phase) => codexPhases.Add(phase);
        fix.Claude.PhaseInvocationStarted += (_, phase) => claudePhases.Add(phase);

        fix.Codex.WorkPlan.Enqueue(new FileWrite("a.txt", "initial"));
        fix.Claude.WorkPlan.Enqueue(new FileWrite("a.txt", "reworked"));

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.Contains("work", codexPhases);
        Assert.DoesNotContain("rework", codexPhases);
        Assert.Contains("rework", claudePhases);
        Assert.Contains(smokeGate.Calls, c =>
            c.Kind == AgentKind.Codex && c.Target.NetworkProfile == "rework-profile");
    }

    [Fact]
    public async Task ReworkFirstAttemptPaused_FallsBackBeforeInvokingPausedRunner()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var pauseGate = new PausingTargetInVmSmokeGate(AgentKind.Codex, "rework-profile");
        using var fix = BuildPipeline(
            seed,
            [new OnceFailingAuditor()],
            maxAuditIterations: 2,
            networkProfiles: new ProjectNetworkProfiles
            {
                Work = "work-profile",
                Rework = "rework-profile",
                Merge = "merge-profile",
            },
            inVmSmokeGate: pauseGate);

        var codexPhases = new List<string>();
        var claudePhases = new List<string>();
        fix.Codex.PhaseInvocationStarted += (_, phase) => codexPhases.Add(phase);
        fix.Claude.PhaseInvocationStarted += (_, phase) => claudePhases.Add(phase);

        fix.Codex.WorkPlan.Enqueue(new FileWrite("a.txt", "initial"));
        fix.Claude.WorkPlan.Enqueue(new FileWrite("a.txt", "reworked"));

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Contains("work", codexPhases);
        Assert.DoesNotContain("rework", codexPhases);
        Assert.Contains("rework", claudePhases);
        Assert.Contains(pauseGate.Calls, c =>
            c.Kind == AgentKind.Codex && c.Target.NetworkProfile == "rework-profile");
    }

    [Fact]
    public async Task AuditDrivenRework_CleanExitNoDiffTerminalQuotaDiagnostic_WithProbeCorroboration_FallsBackToHealthyMember()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(
            seed,
            [new OnceFailingAuditor()],
            maxAuditIterations: 2,
            codexQuotaSnapshot: new AgentQuotaSnapshot { AvailablePct = 0.0 });

        fix.Codex.WorkPlan.Enqueue(new FileWrite("a.txt", "initial"));
        fix.Codex.ReworkScriptedFailures.Enqueue(new AgentResult(
            Success: true,
            Summary: "ok",
            Stdout: null,
            Stderr: null)
        {
            TerminalDiagnostic = "API Error: rate_limit_exceeded; please try again after 1h",
        });
        fix.Claude.WorkPlan.Enqueue(new FileWrite("a.txt", "fixed by fallback"));

        using var metrics = new MetricCapture("codeybox.agent.fallbacks");

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(final);
        Assert.True(
            final!.State == WorkItemState.Done,
            $"expected Done but was {final.State}: {final.LastError}");
        Assert.True(fix.Codex.CallCount >= 2);
        Assert.True(fix.Claude.CallCount >= 1);
        Assert.True(metrics.Any("codeybox.agent.fallbacks",
                ("from_agent", "codex"), ("to_agent", "claude"), ("kind", "quota"), ("phase", "rework")),
            "expected corroborated clean-exit quota diagnostic to reroute the same rework iteration");

        var history = await fix.FallbackHistory.ListByWorkItemAsync(item.Id, CancellationToken.None);
        var fallback = Assert.Single(history, h => h.Phase == "rework" && h.ToAgent == AgentKind.Claude);
        Assert.Equal(AgentKind.Codex, fallback.FromAgent);
        Assert.Contains("quota failure", fallback.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuditDrivenRework_CleanExitNoDiffCapturedQuotaStderr_FallsBackToHealthyMember()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed, [new OnceFailingAuditor()], maxAuditIterations: 2);

        fix.Codex.WorkPlan.Enqueue(new FileWrite("a.txt", "initial"));
        fix.Codex.ReworkScriptedFailures.Enqueue(new AgentResult(
            Success: true,
            Summary: "ok",
            Stdout: null,
            Stderr: "API Error: rate_limit_exceeded; please try again after 1h"));
        fix.Claude.WorkPlan.Enqueue(new FileWrite("a.txt", "fixed after stderr quota"));

        using var metrics = new MetricCapture("codeybox.agent.fallbacks");

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.True(metrics.Any("codeybox.agent.fallbacks",
                ("from_agent", "codex"), ("to_agent", "claude"), ("kind", "quota"), ("phase", "rework")),
            "expected captured clean-exit/no-diff quota output to reroute the same rework iteration");

        var history = await fix.FallbackHistory.ListByWorkItemAsync(item.Id, CancellationToken.None);
        var fallback = Assert.Single(history, h => h.Phase == "rework" && h.ToAgent == AgentKind.Claude);
        Assert.Equal(AgentKind.Codex, fallback.FromAgent);
        Assert.Contains("quota failure", fallback.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuditDrivenRework_CleanExitNoDiffCapturedQuotaStdout_WithProbeCorroboration_FallsBackToHealthyMember()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(
            seed,
            [new OnceFailingAuditor()],
            maxAuditIterations: 2,
            codexQuotaSnapshot: new AgentQuotaSnapshot { AvailablePct = 0.0 });

        fix.Codex.WorkPlan.Enqueue(new FileWrite("a.txt", "initial"));
        fix.Codex.ReworkScriptedFailures.Enqueue(new AgentResult(
            Success: true,
            Summary: "ok",
            Stdout: "API Error: rate_limit_exceeded; please try again after 1h",
            Stderr: null));
        fix.Claude.WorkPlan.Enqueue(new FileWrite("a.txt", "fixed after corroborated stdout quota"));

        using var metrics = new MetricCapture("codeybox.agent.fallbacks");

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.True(metrics.Any("codeybox.agent.fallbacks",
                ("from_agent", "codex"), ("to_agent", "claude"), ("kind", "quota"), ("phase", "rework")),
            "expected probe-corroborated stdout quota output to reroute the same rework iteration");
    }

    [Fact]
    public async Task AuditDrivenRework_CleanExitNoDiffCapturedQuotaStdout_WithoutProbeCorroboration_FallsBackAsInfraQuota()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed, [new OnceFailingAuditor()], maxAuditIterations: 2);

        fix.Codex.WorkPlan.Enqueue(new FileWrite("a.txt", "initial"));
        fix.Codex.ReworkScriptedFailures.Enqueue(new AgentResult(
            Success: true,
            Summary: "ok",
            Stdout: "API Error: rate_limit_exceeded; please try again after 1h",
            Stderr: null));
        fix.Claude.WorkPlan.Enqueue(new FileWrite("a.txt", "fixed after stdout quota fallback"));

        using var metrics = new MetricCapture("codeybox.agent.fallbacks");

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.True(metrics.Any("codeybox.agent.fallbacks",
                ("from_agent", "codex"), ("to_agent", "claude"), ("kind", "quota"), ("phase", "rework")),
            "expected captured stdout quota output on clean-exit/no-diff rework to reroute as infra");
    }

    [Fact]
    public async Task AuditDrivenRework_CleanExitNoDiffAuthRequired_FallsBackToHealthyMember()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gate = new AuthCorroboratingInVmSmokeGate();
        using var fix = BuildPipeline(seed, [new OnceFailingAuditor()], maxAuditIterations: 2, inVmSmokeGate: gate);

        fix.Codex.WorkPlan.Enqueue(new FileWrite("a.txt", "initial"));
        fix.Codex.ReworkScriptedFailures.Enqueue(new AgentResult(
            Success: true,
            Summary: "ok",
            Stdout: null,
            Stderr: "Authentication required. Please visit https://accounts.google.com/o/oauth2/auth?client_id=redacted"));
        fix.Claude.WorkPlan.Enqueue(new FileWrite("a.txt", "fixed after auth fallback"));

        using var metrics = new MetricCapture("codeybox.agent.fallbacks");

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(final);
        Assert.True(
            final!.State == WorkItemState.Done,
            $"expected Done but was {final.State}: {final.LastError}");
        Assert.True(fix.Codex.CallCount >= 2);
        Assert.True(fix.Claude.CallCount >= 1);
        Assert.Equal(0, gate.ForceProbeCalls);
        Assert.True(metrics.Any("codeybox.agent.fallbacks",
                ("from_agent", "codex"), ("to_agent", "claude"), ("kind", "auth"), ("phase", "rework")),
            "expected auth-required rework output to reroute through the class fallback path");

        var history = await fix.FallbackHistory.ListByWorkItemAsync(item.Id, CancellationToken.None);
        var fallback = Assert.Single(history, h => h.Phase == "rework" && h.ToAgent == AgentKind.Claude);
        Assert.Equal(AgentKind.Codex, fallback.FromAgent);
        Assert.Contains("auth required", fallback.Reason, StringComparison.OrdinalIgnoreCase);

        var failed = Assert.Single(fix.Webhooks.Events, e => e.Event == "agent.smoke_failed");
        var details = Assert.IsType<AgentSmokeFailedDetails>(failed.Details);
        Assert.Equal("codex", details.AgentKind);
        Assert.Equal(SmokeFailureCategory.Persistent, details.Category);

        var reworkRows = (await fix.Involvement.ListByWorkItemAsync(item.Id, CancellationToken.None))
            .Where(r => r.Phase == "rework")
            .ToList();
        Assert.Collection(reworkRows,
            r => AssertInvolvement(r, AgentKind.Codex, "rework", 2, "failure:auth"),
            r => AssertInvolvement(r, AgentKind.Claude, "rework", 2, "success"));
    }

    [Fact]
    public async Task AuditDrivenRework_CleanExitNoDiffStdoutOnlyAuthRequired_WithoutCorroboration_ExcludesAndFallsBack()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var availability = new AgentAvailabilityRegistry(
            new AvailabilityOptions(),
            TimeProvider.System,
            NullLogger<AgentAvailabilityRegistry>.Instance);
        using var fix = BuildPipeline(
            seed,
            [new OnceFailingAuditor()],
            maxAuditIterations: 2,
            availability: availability);

        fix.Codex.WorkPlan.Enqueue(new FileWrite("a.txt", "initial"));
        fix.Codex.ReworkScriptedFailures.Enqueue(new AgentResult(
            Success: true,
            Summary: "ok",
            Stdout: """
                Authentication required. Please visit the URL to log in:
                https://accounts.google.com/o/oauth2/auth?client_id=redacted
                Waiting for authentication (timeout 30s)...
                Error: authentication timed out.
                """,
            Stderr: null));
        fix.Claude.WorkPlan.Enqueue(new FileWrite("a.txt", "fixed after uncorroborated stdout auth fallback"));

        using var metrics = new MetricCapture("codeybox.agent.fallbacks");

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.True(metrics.Any("codeybox.agent.fallbacks",
                ("from_agent", "codex"), ("to_agent", "claude"), ("kind", "auth"), ("phase", "rework")),
            "expected empty-rework stdout auth output to reroute through class fallback");
        Assert.True(availability.GetAuthRequiredAvailability(AgentKind.Codex).AuthRequired);
        Assert.False(availability.GetAvailability(AgentKind.Codex).Available);

        var failed = Assert.Single(fix.Webhooks.Events, e => e.Event == "agent.smoke_failed");
        var details = Assert.IsType<AgentSmokeFailedDetails>(failed.Details);
        Assert.Equal("codex", details.AgentKind);
    }

    [Fact]
    public async Task AuditDrivenRework_CleanExitNoDiffStdoutOnlyAuthRequired_WithGateAvailable_ExcludesAndFallsBackWithoutProbe()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gate = new AuthCorroboratingInVmSmokeGate();
        using var fix = BuildPipeline(seed, [new OnceFailingAuditor()], maxAuditIterations: 2, inVmSmokeGate: gate);

        fix.Codex.WorkPlan.Enqueue(new FileWrite("a.txt", "initial"));
        fix.Codex.ReworkScriptedFailures.Enqueue(new AgentResult(
            Success: true,
            Summary: "ok",
            Stdout: """
                Authentication required. Please visit the URL to log in:
                https://accounts.google.com/o/oauth2/auth?client_id=redacted
                Waiting for authentication (timeout 30s)...
                Error: authentication timed out.
                """,
            Stderr: null));
        fix.Claude.WorkPlan.Enqueue(new FileWrite("a.txt", "fixed after stdout auth fallback"));

        using var metrics = new MetricCapture("codeybox.agent.fallbacks");

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(0, gate.ForceProbeCalls);
        Assert.True(metrics.Any("codeybox.agent.fallbacks",
                ("from_agent", "codex"), ("to_agent", "claude"), ("kind", "auth"), ("phase", "rework")),
            "expected empty-rework stdout auth output to reroute through the class fallback path without probing");

        var history = await fix.FallbackHistory.ListByWorkItemAsync(item.Id, CancellationToken.None);
        var fallback = Assert.Single(history, h => h.Phase == "rework" && h.ToAgent == AgentKind.Claude);
        Assert.Equal(AgentKind.Codex, fallback.FromAgent);
        Assert.Contains("auth required", fallback.Reason, StringComparison.OrdinalIgnoreCase);

        var failed = Assert.Single(fix.Webhooks.Events, e => e.Event == "agent.smoke_failed");
        var details = Assert.IsType<AgentSmokeFailedDetails>(failed.Details);
        Assert.Equal("codex", details.AgentKind);
        Assert.Equal(SmokeFailureCategory.Persistent, details.Category);
    }

    [Theory]
    [InlineData("API Error: 401 Unauthorized: token expired")]
    [InlineData("API Error: 403: permission denied")]
    public async Task AuditDrivenRework_CleanExitNoDiffUnauthorizedDiagnostic_FallsBackAsAuthNotQuota(
        string terminalDiagnostic)
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed, [new OnceFailingAuditor()], maxAuditIterations: 2);

        fix.Codex.WorkPlan.Enqueue(new FileWrite("a.txt", "initial"));
        fix.Codex.ReworkScriptedFailures.Enqueue(new AgentResult(
            Success: true,
            Summary: "ok",
            Stdout: null,
            Stderr: null)
        {
            TerminalDiagnostic = terminalDiagnostic,
        });
        fix.Claude.WorkPlan.Enqueue(new FileWrite("a.txt", "fixed after unauthorized fallback"));

        using var metrics = new MetricCapture("codeybox.agent.fallbacks");

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.True(metrics.Any("codeybox.agent.fallbacks",
                ("from_agent", "codex"), ("to_agent", "claude"), ("kind", "auth"), ("phase", "rework")),
            "expected 401/403 diagnostics to reroute as auth, not quota");
        Assert.False(metrics.Any("codeybox.agent.fallbacks",
            ("from_agent", "codex"), ("to_agent", "claude"), ("kind", "quota"), ("phase", "rework")));
    }

    [Fact]
    public async Task AuditDrivenRework_AllClassMembersQuotaExhausted_ParksWithOriginalReworkPhase()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed, [new OnceFailingAuditor()], maxAuditIterations: 2);

        fix.Codex.WorkPlan.Enqueue(new FileWrite("a.txt", "initial"));
        fix.Codex.ReworkScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 1",
            Stdout: null,
            Stderr: "API Error: rate_limit_exceeded; please try again after 1h"));
        fix.Claude.ReworkScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 1",
            Stdout: null,
            Stderr: "API Error: rate_limit_exceeded; please try again after 1h"));

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var parked = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(parked);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, parked!.State);
        Assert.Equal("quota", parked.FailureKind);
        Assert.Equal("audit", parked.QuotaRetryFrom);
        Assert.Equal("rework", parked.QuotaRetryPhase);
        Assert.NotNull(parked.NextQuotaRetryAt);

        var history = await fix.FallbackHistory.ListByWorkItemAsync(item.Id, CancellationToken.None);
        var exhausted = Assert.Single(history, h => h.Phase == "rework" && h.ToAgent is null);
        Assert.Equal(AgentKind.Claude, exhausted.FromAgent);
    }

    [Fact]
    public async Task AuditDrivenRework_QuotaRetryRerunsReworkWhenOnlyDispatchRowExists()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(
            seed,
            [new OnceFailingAuditor()],
            maxAuditIterations: 2,
            useClassRouter: false,
            wireAuditProgress: true);

        fix.Codex.WorkPlan.Enqueue(new FileWrite("a.txt", "initial"));
        fix.Codex.ReworkScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 1",
            Stdout: null,
            Stderr: "API Error: rate_limit_exceeded; please try again after 1h"));

        var item = NewItem(initialAgent: AgentKind.Codex) with
        {
            AgentClassId = null,
        };
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var parked = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(parked);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, parked!.State);
        Assert.Equal("audit", parked.QuotaRetryFrom);
        Assert.Equal("rework", parked.QuotaRetryPhase);
        Assert.Contains(await fix.Store.GetIterationsAsync(item.Id), i => i.Iteration == 2);
        Assert.DoesNotContain(
            await fix.Involvement.ListByWorkItemAsync(item.Id, CancellationToken.None),
            r => r.Phase == "rework" && r.Iteration == 2 && r.Outcome == "success");

        var retrier = new WorkItemRetrier(
            fix.Store,
            new InMemoryTaskQueue(),
            fix.GitHost,
            NullLogger<WorkItemRetrier>.Instance,
            auditProgress: fix.Store);
        var retry = await retrier.RetryAsync(parked, from: "audit", trigger: "test-quota-return");
        Assert.True(retry.Success, retry.Error);

        fix.Codex.WorkPlan.Enqueue(new FileWrite("a.txt", "reworked"));
        var resumed = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(resumed);
        await fix.Pipeline.RunAsync(resumed!, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.Done, final!.State);
        var rows = (await fix.Involvement.ListByWorkItemAsync(item.Id, CancellationToken.None))
            .Where(r => r.Phase != "audit:test:build-and-test-pass")
            .ToList();
        Assert.Contains(rows, r => r.Phase == "rework" && r.Iteration == 2 && r.Outcome == "failure:quota");
        Assert.Contains(rows, r => r.Phase == "rework" && r.Iteration == 2 && r.Outcome == "success");

        var repoId = await fix.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var bareRepo = fix.GitHost.GetRepoPath(repoId);
        var (_, contents, _) = await TestSupport.RunGit(bareRepo, "show", $"{final.WorkBranch}:a.txt");
        Assert.Equal("reworked", contents);
    }

    [Fact]
    public async Task DirectQuotaFailure_PublishesUnusableObservationForRecoveryMonitor()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var quotaSignal = new AgentQuotaAvailabilityBroadcaster();
        var observations = new List<AgentQuotaUsabilityObservation>();
        quotaSignal.QuotaUsabilityObserved += observations.Add;
        using var fix = BuildPipeline(
            seed,
            useClassRouter: false,
            quotaAvailabilityPublisher: quotaSignal);

        fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 1",
            Stdout: null,
            Stderr: "API Error: rate_limit_exceeded; please try again after 1h"));

        var item = NewItem(initialAgent: AgentKind.Codex) with
        {
            AgentClassId = null,
        };
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var parked = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(parked);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, parked!.State);
        Assert.Contains(fix.CodexProbe.MarkedExhausted, agent => agent == AgentKind.Codex);
        var observation = Assert.Single(
            observations,
            o => o.Member.Agent == AgentKind.Codex && !o.IsUsable);
        Assert.True(observation.PublishRecoverySignal);
        Assert.Equal(item.ModelId, observation.Member.ModelId);
    }

    [Fact]
    public async Task Claude_RateLimitEventStdout_FallsBackToPeerWithinClass_SameIteration()
    {
        // The exact regression that prompted the mid-rework Claude 5h-window
        // fix: claude exits 1 emitting only
        //   {"type":"rate_limit_event","rate_limit_info":{"status":"rejected",...}}
        // on stdout. Before this branch the classifier returned null for this
        // shape, the orchestrator recorded failureKind="other", and
        // InvokeAgentWithQuotaFallbackAsync never saw a TerminalQuotaError to
        // fall back on — silently hard-Failing the item even though the class
        // had an available peer. The pre-existing fallback tests all wire
        // stderr "rate_limit_exceeded", so a regression that broke the
        // stdout-rate_limit_event detection path would slip past them.
        //
        // This test pins acceptance-criterion (a): "first attempt cross-agent
        // fallback within the class" for the exact wire shape captured from
        // production. Claude is the WORK agent, claude emits the JSON, the
        // wrapper must catch the TerminalQuotaError and re-dispatch Codex —
        // not park as WaitingForQuotaReset (a peer is available) and not hard
        // Fail.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);

        const long resetsAtUnixSeconds = 1782000000L;
        var rateLimitStdout =
            "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"rejected\",\"resetsAt\":"
            + resetsAtUnixSeconds
            + ",\"rateLimitType\":\"five_hour\",\"overageStatus\":\"rejected\"}}";

        fix.Claude.ScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 1",
            Stdout: rateLimitStdout,
            Stderr: null));
        fix.Codex.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem(initialAgent: AgentKind.Claude);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        // Claude burned its one quota attempt for work; Codex picked the work
        // up and committed the diff. There is no second (merge-phase) Claude
        // invocation: a clean merge is completed host-side with no agent, so
        // Claude is called exactly once (the failed work pick) and Codex once
        // (the successful work fallback). Asserting the exact counts catches a
        // regression that, e.g., re-dispatched the rate_limit_event scripted
        // failure and silently re-triggered fallback.
        Assert.Equal(1, fix.Claude.CallCount);
        Assert.Equal(1, fix.Codex.CallCount);

        // Item must NOT be Failed or parked — a peer was available, so the
        // fallback path is the contract.
        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(finalItem);
        Assert.NotEqual(WorkItemState.Failed, finalItem!.State);
        Assert.NotEqual(WorkItemState.WaitingForQuotaReset, finalItem.State);

        // Webhook + fallback-history must record the swap as the WORK phase
        // (the rate_limit_event was emitted during work), with claude→codex
        // attribution and a quota-shaped reason. A regression that swallowed
        // the TerminalQuotaError and let the item Fail "other" would leave the
        // fallback record entirely missing.
        Assert.Contains(fix.Webhooks.Events, e => e.Event == "agent.fallback");
        var fallback = fix.Webhooks.Events.First(e => e.Event == "agent.fallback");
        var details = Assert.IsType<AgentFallbackDetails>(fallback.Details);
        Assert.Equal("claude", details.FromAgent);
        Assert.Equal("codex", details.ToAgent);
        Assert.Equal("work", details.Phase);

        var history = await fix.FallbackHistory.ListByWorkItemAsync(item.Id, CancellationToken.None);
        var swap = Assert.Single(history, h => h.Phase == "work" && h.ToAgent == AgentKind.Codex);
        Assert.Equal(AgentKind.Claude, swap.FromAgent);

        // Claude was marked exhausted on its quota probe (write-back path) so
        // subsequent in-process picks skip it. A regression that detected the
        // rate_limit_event but failed to propagate MarkExhausted would let the
        // class oscillate back to claude on the next iteration.
        Assert.Contains(AgentKind.Claude, fix.ClaudeProbe.MarkedExhausted);
    }

    [Fact]
    public async Task Codex_HitsQuota_FallsBackToClaude_SameIteration()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);

        // Codex returns quota-shaped failure on its first call; pipeline must
        // swap to Claude for the same iteration.
        fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 1",
            Stdout: null,
            Stderr: "API Error: rate_limit_exceeded; please try again after 1h"));
        fix.Claude.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        // Codex was tried for the work phase and failed; Claude succeeded the
        // retry. Codex's WORK-phase invocation must be exactly one — a regression
        // that left Codex out of the initial pick would silently still pass with
        // an "at least one" assertion. There is no longer a second (merge-phase)
        // Codex invocation: a clean merge is completed host-side with no agent,
        // so Codex is invoked exactly once (the failed work pick).
        Assert.Equal(1, fix.Codex.CallCount);
        Assert.Equal(1, fix.Claude.CallCount);

        // Item ended up in the merged → Done flow (work phase didn't fail).
        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(finalItem);
        Assert.NotEqual(WorkItemState.Failed, finalItem!.State);
        Assert.NotEqual(WorkItemState.WaitingForQuotaReset, finalItem.State);

        // Audit + webhook event captured.
        Assert.Contains(fix.Webhooks.Events, e => e.Event == "agent.fallback");
        var fallback = fix.Webhooks.Events.First(e => e.Event == "agent.fallback");
        var details = Assert.IsType<AgentFallbackDetails>(fallback.Details);
        Assert.Equal("codex", details.FromAgent);
        Assert.Equal("claude", details.ToAgent);
        Assert.Equal("work", details.Phase);
    }

    [Fact]
    public async Task FullProgression_RecordsPerPhaseInvolvementThroughChokepoint()
    {
        // Acceptance criteria #5 and #6 exercised through the REAL pipeline (not
        // manual store seeding): a work item driven Work → Audit(fail) → Rework →
        // Audit(pass) → Merge must leave exactly one finalized involvement row per
        // agent run, in order, mapping 1:1 to the orchestrator's phase
        // transitions. A regression that deletes or misplaces the chokepoint
        // recording (RecordInvolvementStartAsync / FinalizeInvolvementAsync /
        // ExecAuditorAsync) fails here, where the store-only test cannot.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed, [new OnceFailingAuditor()], maxAuditIterations: 2);

        fix.Codex.WorkPlan.Enqueue(new FileWrite("work.txt", "v1"));
        fix.Codex.WorkPlan.Enqueue(new FileWrite("rework.txt", "v2"));

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.Done, finalItem!.State);

        var rows = (await fix.Involvement.ListByWorkItemAsync(item.Id, CancellationToken.None))
            .Where(r => r.Phase != "audit:test:build-and-test-pass")
            .ToList();

        // One row per phase transition, in order. The OnceFailingAuditor forces a
        // single rework (audit iter 1 fails, iter 2 passes); the rework following
        // audit iteration N dispatches as iteration N+1.
        Assert.Collection(rows,
            r => AssertInvolvement(r, AgentKind.Codex, "work", null, "success"),
            r => AssertInvolvement(r, AgentKind.Codex, "audit:once-failing-fallback", 1, "success"),
            r => AssertInvolvement(r, AgentKind.Codex, "rework", 2, "success"),
            r => AssertInvolvement(r, AgentKind.Codex, "audit:once-failing-fallback", 2, "success"),
            r => AssertInvolvement(r, AgentKind.Codex, "merge", null, "success"));

        // Every row is a closed start→finalize pair (no dangling in-progress row).
        Assert.All(rows, r => Assert.NotNull(r.EndedAt));
    }

    [Fact]
    public async Task ThreeAuditorProgression_WorkAuditReworkAuditMerge_RecordsNineRowPerAuditorTrail()
    {
        // The three-auditor companion to the AC#5 seven-row guard
        // (Ac5_WorkAuditReworkAuditMerge_RecordsExactlySevenRowAgentHistory). Same
        // progression — "Work → Audit → Rework → Audit → Merge" — but with THREE
        // distinct LLM auditors, pinning the per-auditor "audit:{name}" labelling
        // (AC#6's 1:1 mapping) and the row count under the orchestrator's full
        // re-audit policy.
        //
        // AC#5's "7-row" shorthand assumes the post-rework re-audit re-runs a
        // SINGLE auditor. The orchestrator deliberately re-runs the FULL auditor
        // list on every iteration (a rework can regress a dimension a
        // previously-passing auditor would catch — re-running only the failer would
        // merge that regression unchecked), so for N auditors the trail is
        // 1 + N + 1 + N + 1 = 2N + 3 rows. The literal AC#5 seven-row trail is the
        // N = 2 case (pinned by the Ac5_ test above); three auditors honestly
        // produce 9, asserted here. A regression that collapses multi-auditor
        // recording, drops the chokepoint, skips the post-rework re-audit, or
        // mislabels phases fails here.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var alpha = new ScriptedLlmAuditor("review-alpha", failOnCall: 1);
        var beta = new ScriptedLlmAuditor("review-beta");
        var gamma = new ScriptedLlmAuditor("review-gamma");
        using var fix = BuildPipeline(seed, [alpha, beta, gamma], maxAuditIterations: 2);

        fix.Codex.WorkPlan.Enqueue(new FileWrite("work.txt", "v1"));
        fix.Codex.WorkPlan.Enqueue(new FileWrite("rework.txt", "v2"));

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.Done, finalItem!.State);

        var rows = (await fix.Involvement.ListByWorkItemAsync(item.Id, CancellationToken.None))
            .Where(r => r.Phase != "audit:test:build-and-test-pass")
            .ToList();

        // 1 work + 3 audits(iter1) + 1 rework + 3 audits(iter2) + 1 merge = 9.
        // The three LLM auditors run concurrently, so rows within a single audit
        // iteration are not strictly ordered; phase boundaries (work → audits →
        // rework → audits → merge) are ordered by started_at and stable.
        Assert.Equal(9, rows.Count);
        Assert.All(rows, r => Assert.NotNull(r.EndedAt));
        Assert.All(rows, r => Assert.Equal(AgentKind.Codex, r.AgentKind));

        AssertInvolvement(rows[0], AgentKind.Codex, "work", null, "success");
        AssertInvolvement(rows[4], AgentKind.Codex, "rework", 2, "success");
        AssertInvolvement(rows[8], AgentKind.Codex, "merge", null, "success");

        var auditPhases = new[] { "audit:review-alpha", "audit:review-beta", "audit:review-gamma" };

        var iter1Audits = rows.Skip(1).Take(3).ToList();
        Assert.All(iter1Audits, r => Assert.Equal(1, r.Iteration));
        Assert.All(iter1Audits, r => Assert.Equal("success", r.Outcome));
        Assert.Equal(auditPhases, iter1Audits.Select(r => r.Phase).OrderBy(p => p));

        var iter2Audits = rows.Skip(5).Take(3).ToList();
        Assert.All(iter2Audits, r => Assert.Equal(2, r.Iteration));
        Assert.All(iter2Audits, r => Assert.Equal("success", r.Outcome));
        Assert.Equal(auditPhases, iter2Audits.Select(r => r.Phase).OrderBy(p => p));
    }

    [Fact]
    public async Task Ac5_WorkAuditReworkAuditMerge_RecordsExactlySevenRowAgentHistory()
    {
        // THE acceptance-criterion-#5 literal seven-row guard, realised end-to-end
        // through the REAL pipeline (not store seeding): the
        // Work → Audit → Rework → Audit → Merge progression named in AC#5 produces
        // exactly the seven-row agentHistory it specifies. The audit loop re-runs
        // the full auditor list every iteration, so the trail for N LLM auditors is
        // 1 + N + 1 + N + 1 = 2N + 3; AC#5's seven-row count is therefore the N = 2
        // realisation, pinned exactly here. (AC#5's prose also says "3 LLM
        // auditors", but three auditors under full re-audit genuinely yield 9 rows
        // — see ThreeAuditorProgression_…_RecordsNineRowPerAuditorTrail; the "7" and
        // "3" in the prose are mutually inconsistent given AC#6's per-auditor 1:1
        // mapping, so this test honours the seven-row count and the companion test
        // honours the three-auditor count.) A regression that dropped the single
        // chokepoint, mislabeled a phase, or skipped the post-rework re-audit
        // recording would change this count and fail here.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var alpha = new ScriptedLlmAuditor("review-alpha", failOnCall: 1);
        var beta = new ScriptedLlmAuditor("review-beta");
        using var fix = BuildPipeline(seed, [alpha, beta], maxAuditIterations: 2);

        fix.Codex.WorkPlan.Enqueue(new FileWrite("work.txt", "v1"));
        fix.Codex.WorkPlan.Enqueue(new FileWrite("rework.txt", "v2"));

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.Done, finalItem!.State);

        var rows = (await fix.Involvement.ListByWorkItemAsync(item.Id, CancellationToken.None))
            .Where(r => r.Phase != "audit:test:build-and-test-pass")
            .ToList();

        // 1 work + 2 audits(iter1) + 1 rework + 2 audits(iter2) + 1 merge = 7.
        Assert.Equal(7, rows.Count);
        Assert.All(rows, r => Assert.NotNull(r.EndedAt));
        Assert.All(rows, r => Assert.Equal(AgentKind.Codex, r.AgentKind));

        AssertInvolvement(rows[0], AgentKind.Codex, "work", null, "success");
        AssertInvolvement(rows[3], AgentKind.Codex, "rework", 2, "success");
        AssertInvolvement(rows[6], AgentKind.Codex, "merge", null, "success");

        var auditPhases = new[] { "audit:review-alpha", "audit:review-beta" };

        var iter1Audits = rows.Skip(1).Take(2).ToList();
        Assert.All(iter1Audits, r => Assert.Equal(1, r.Iteration));
        Assert.Equal(auditPhases, iter1Audits.Select(r => r.Phase).OrderBy(p => p));

        var iter2Audits = rows.Skip(4).Take(2).ToList();
        Assert.All(iter2Audits, r => Assert.Equal(2, r.Iteration));
        Assert.Equal(auditPhases, iter2Audits.Select(r => r.Phase).OrderBy(p => p));
    }

    [Fact]
    public async Task WorkQuotaFallback_RecordsFailureThenSuccessInvolvementRows()
    {
        // The quota fallback path must close the exhausted attempt's row as
        // failure:quota and open a fresh row for the successor agent — the
        // multi-row trail operators rely on to see "codex burned quota, claude
        // finished it". Asserts the failure-outcome mapping that is otherwise
        // only exercised by manual store seeding.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);

        fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 1",
            Stdout: null,
            Stderr: "API Error: rate_limit_exceeded; please try again after 1h"));
        fix.Claude.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var rows = await fix.Involvement.ListByWorkItemAsync(item.Id, CancellationToken.None);
        var workRows = rows.Where(r => r.Phase == "work").ToList();
        Assert.Collection(workRows,
            r => AssertInvolvement(r, AgentKind.Codex, "work", null, "failure:quota"),
            r => AssertInvolvement(r, AgentKind.Claude, "work", null, "success"));
        Assert.All(workRows, r => Assert.NotNull(r.EndedAt));
    }

    [Fact]
    public async Task AuditQuotaShapedStderr_FinalizesAuditInvolvementFailureQuota()
    {
        // End-to-end guard for the AUDIT-phase failure:quota mapping in
        // ExecAuditorAsync (AuditorRunOutcome → _quotaClassifier.Detect on the
        // review agent's stderr). The LLM auditor passes the gate (no findings)
        // but its agent emitted quota-shaped stderr, so the involvement row must
        // close as failure:quota even though the work item still reaches Done. A
        // regression that always stamped audit rows "success", or mapped quota to
        // failure:agent, would slip past the success-only progression tests; this
        // is the only e2e assertion of an audit row's failure:quota outcome.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new ScriptedLlmAuditor("review-quota", quotaStderrOnCall: 1);
        using var fix = BuildPipeline(seed, [auditor], maxAuditIterations: 1);

        fix.Codex.WorkPlan.Enqueue(new FileWrite("work.txt", "v1"));

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.Done, finalItem!.State);

        var auditRow = Assert.Single(
            await fix.Involvement.ListByWorkItemAsync(item.Id, CancellationToken.None),
            r => r.Phase == "audit:review-quota");
        AssertInvolvement(auditRow, AgentKind.Codex, "audit:review-quota", 1, "failure:quota");
        Assert.NotNull(auditRow.EndedAt);
    }

    [Fact]
    public async Task AuditAgentExecutionFailure_FinalizesAuditInvolvementFailureAgent()
    {
        // End-to-end guard for the AUDIT-phase failure:agent mapping in
        // ExecAuditorAsync (AuditorRunOutcome → IsLlmAgentExecutionFailure). The
        // first auditor run returns the "review agent failed to run" infra-failure
        // shape; ExecAuditorAsync closes that row failure:agent. The pipeline's
        // transient-retry then re-runs the auditor in a fresh sandbox, which
        // succeeds and records a separate success row, so the item reaches Done.
        // Without this test a regression that mislabeled the run failure (e.g. as
        // success, or as failure:quota for non-quota stderr) would go unnoticed.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new ScriptedLlmAuditor("review-crash", agentFailOnCall: 1);
        using var fix = BuildPipeline(seed, [auditor], maxAuditIterations: 1);

        fix.Codex.WorkPlan.Enqueue(new FileWrite("work.txt", "v1"));

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.Done, finalItem!.State);

        var auditRows = (await fix.Involvement.ListByWorkItemAsync(item.Id, CancellationToken.None))
            .Where(r => r.Phase == "audit:review-crash").ToList();
        // The failed run + its fresh-sandbox retry each record a row.
        var failedAudit = Assert.Single(auditRows, r => r.Outcome == "failure:agent");
        AssertInvolvement(failedAudit, AgentKind.Codex, "audit:review-crash", 1, "failure:agent");
        Assert.NotNull(failedAudit.EndedAt);
        Assert.Contains(auditRows, r => r.Outcome == "success");
    }

    [Fact]
    public async Task PersistentAuditAgentExecutionFailure_TransitionsItemToFailedInfrastructure()
    {
        // Persistent non-quota auditor execution failure path: the first
        // RunLlmPairAttemptAsync call returns the "review agent failed to run"
        // sentinel, the transient fresh-sandbox retry returns the SAME sentinel,
        // and the second IsLlmAgentExecutionFailure check at
        // PipelineRunner.cs:6237 throws AuditUnavailableException — routing the
        // item to Failed/failureKind="infrastructure" via the RunAsync catch at
        // PipelineRunner.cs:1543.
        //
        // The existing AuditAgentExecutionFailure_FinalizesAuditInvolvementFailureAgent
        // covers ONLY the first-attempt-fails-then-retry-succeeds shape. Without
        // this test, a regression that
        //   (a) removed the throw and let the second-attempt sentinel post-process
        //       into the findings list (re-introducing the 1aa5a13f false-
        //       AuditFailed regression by burning a rework iteration), or
        //   (b) routed the persistent failure to WaitingForQuotaReset (the
        //       non-quota execution failure has no reset semantics), or
        //   (c) let the item reach Done with a Pass verdict (the bug-report
        //       hard invariant: a Pass requires every configured auditor to
        //       have actually run),
        // would all pass every other test in this file.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new ScriptedLlmAuditor("review-crash", persistentAgentFailure: true);
        using var fix = BuildPipeline(seed, [auditor], maxAuditIterations: 1);

        fix.Codex.WorkPlan.Enqueue(new FileWrite("work.txt", "v1"));

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(finalItem);
        // Hard invariant: an unrunnable auditor must NOT pass the audit gate.
        Assert.NotEqual(WorkItemState.Done, finalItem!.State);
        // Non-quota infra failure must NOT park (no reset semantics).
        Assert.NotEqual(WorkItemState.WaitingForQuotaReset, finalItem.State);
        // Must NOT be AuditFailed (would burn a rework iteration on infra).
        Assert.NotEqual(WorkItemState.AuditFailed, finalItem.State);
        Assert.Equal(WorkItemState.Failed, finalItem.State);
        Assert.Equal("infrastructure", finalItem.FailureKind);

        // Both the first attempt and the transient retry must have recorded
        // a failure:agent involvement row — confirms the retry actually fired
        // (without it, only one failure row would land) and that no Pass-shaped
        // success row sneaked in.
        var auditRows = (await fix.Involvement.ListByWorkItemAsync(item.Id, CancellationToken.None))
            .Where(r => r.Phase == "audit:review-crash").ToList();
        Assert.Equal(2, auditRows.Count);
        Assert.All(auditRows, r => Assert.Equal("failure:agent", r.Outcome));
        Assert.DoesNotContain(auditRows, r => r.Outcome == "success");
    }

    [Fact]
    public async Task TransientPersistenceFault_IsRetried_FullInvolvementTrailStillRecorded()
    {
        // AC#1/AC#6 guard against a *transient* involvement-store fault dropping a
        // row. The store is wrapped to throw TimeoutException on its first two
        // RecordStartAsync calls (SQLite busy/locked / IO blip shape); the bounded
        // retry in PipelineRunner.PersistInvolvementWithRetryAsync must recover so
        // the full Work → Audit(fail) → Rework → Audit(pass) → Merge trail still
        // lands. Without retry the very first start (the work row) would be lost,
        // leaving a phase with no history. The injected failures are consumed by
        // retries of the first row, so StartCalls = 5 rows + 2 retried = 7 proves
        // the retries actually fired (not that the faults were merely absent).
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        FlakyInvolvementStore? flaky = null;
        using var fix = BuildPipeline(seed, [new OnceFailingAuditor()], maxAuditIterations: 2,
            wrapInvolvement: inner => flaky = new FlakyInvolvementStore(inner, transientStartFailures: 2));

        fix.Codex.WorkPlan.Enqueue(new FileWrite("work.txt", "v1"));
        fix.Codex.WorkPlan.Enqueue(new FileWrite("rework.txt", "v2"));

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.Done, finalItem!.State);

        var rows = await fix.Involvement.ListByWorkItemAsync(item.Id, CancellationToken.None);
        Assert.Collection(rows,
            r => AssertInvolvement(r, AgentKind.Codex, "work", null, "success"),
            r => AssertInvolvement(r, AgentKind.Codex, "audit:once-failing-fallback", 1, "success"),
            r => AssertInvolvement(r, AgentKind.Codex, "rework", 2, "success"),
            r => AssertInvolvement(r, AgentKind.Codex, "audit:once-failing-fallback", 2, "success"),
            r => AssertInvolvement(r, AgentKind.Codex, "merge", null, "success"));
        Assert.All(rows, r => Assert.NotNull(r.EndedAt));

        Assert.NotNull(flaky);
        Assert.Equal(7, flaky!.StartCalls);
    }

    [Fact]
    public async Task PermanentPersistenceFault_IsTolerated_PipelineStillCompletes()
    {
        // The deliberate trade-off documented in PersistInvolvementWithRetryAsync:
        // when the involvement store stays faulted past every retry, the work item
        // is NOT aborted for an audit-trail write — it still reaches Done with the
        // fault logged at Warning. This pins that graceful degradation so a future
        // change that let a tolerated persistence fault crash the pipeline (or that
        // silently stopped tolerating it) is caught. The trail is empty here
        // precisely because every start was dropped; the resilience contract is
        // "retry transient blips" (previous test), not "guarantee against a dead DB".
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed, [new OnceFailingAuditor()], maxAuditIterations: 2,
            wrapInvolvement: inner => new FlakyInvolvementStore(inner, transientStartFailures: int.MaxValue));

        fix.Codex.WorkPlan.Enqueue(new FileWrite("work.txt", "v1"));
        fix.Codex.WorkPlan.Enqueue(new FileWrite("rework.txt", "v2"));

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.Done, finalItem!.State);

        var rows = await fix.Involvement.ListByWorkItemAsync(item.Id, CancellationToken.None);
        Assert.Empty(rows);
    }

    private static void AssertInvolvement(
        AgentInvolvement r, AgentKind agent, string phase, int? iteration, string outcome)
    {
        Assert.Equal(agent, r.AgentKind);
        Assert.Equal(phase, r.Phase);
        Assert.Equal(iteration, r.Iteration);
        Assert.Equal(outcome, r.Outcome);
    }

    private static T? GetScalar<T>(LogEvent evt, string key)
    {
        if (!evt.Properties.TryGetValue(key, out var prop) || prop is not ScalarValue sv)
            return default;
        if (sv.Value is T t)
            return t;
        if (typeof(T) == typeof(int?) && sv.Value is int i)
            return (T)(object)(int?)i;
        if (typeof(T) == typeof(int?) && sv.Value is null)
            return default;
        return default;
    }

    [Fact]
    public async Task Codex_HitsQuota_FallsBackToClaude_PersistsFallbackHistoryAfterDone()
    {
        // Regression guard for the symptom "fallbackHistory: null on 25/30 Done
        // items" reported by operators: in-process logs showed Codex→Claude
        // fallback events but the persisted FallbackHistory was empty on most
        // Done items. This test pins the contract that, after a work item
        // completes in Done following a quota-triggered swap, at least one
        // fallback record is durably present in the store. A regression that
        // bypasses RecordAsync (e.g. wiring the swap webhook without the store
        // write) or that clears the per-item rows on Done-finalization will
        // trip Count == 0 here.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);

        fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 1",
            Stdout: null,
            Stderr: "API Error: rate_limit_exceeded; please try again after 1h"));
        fix.Claude.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(finalItem);
        Assert.Equal(WorkItemState.Done, finalItem!.State);

        var history = await fix.FallbackHistory.ListByWorkItemAsync(item.Id, CancellationToken.None);
        Assert.True(history.Count >= 1,
            $"expected at least one persisted fallback record after Done; got {history.Count}");
        var swap = history.Single(h => h.Phase == "work" && h.ToAgent == AgentKind.Claude);
        Assert.Equal(AgentKind.Codex, swap.FromAgent);
        Assert.Equal(AgentKind.Claude, swap.ToAgent);
        Assert.Contains("quota", swap.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BothMembers_Exhausted_ParksInWaitingForQuotaReset()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);

        var quotaErr = new AgentResult(false, "agent exited 1", null,
            "API Error: rate_limit_exceeded");
        fix.Codex.ScriptedFailures.Enqueue(quotaErr);
        fix.Claude.ScriptedFailures.Enqueue(quotaErr);

        // Two fallback measurements are expected from the real exhaustion path:
        // the codex→claude swap, then the all-exhausted park event with
        // to_agent=(none). Without this listener, dropping the (none) Add or
        // mis-tagging it would pass every behavioural assertion below.
        using var metrics = new MetricCapture("codeybox.agent.fallbacks");

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(finalItem);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, finalItem!.State);
        Assert.Equal("quota", finalItem.FailureKind);

        // NextQuotaRetryAt must be populated so QuotaRetryScheduler can re-arm
        // the targeted timer; if this field were left null, the parked item
        // would only be picked up by the periodic sweep (or never, after a
        // host restart that didn't go through the periodic loop).
        Assert.NotNull(finalItem.NextQuotaRetryAt);

        // Both members tried in this single pickup; AllExhausted audit emitted.
        Assert.Equal(1, fix.Codex.CallCount);
        Assert.Equal(1, fix.Claude.CallCount);

        // Both probes received the MarkExhaustedAsync write-back.
        Assert.Contains(fix.CodexProbe.MarkedExhausted, k => k == AgentKind.Codex);
        Assert.Contains(fix.ClaudeProbe.MarkedExhausted, k => k == AgentKind.Claude);

        // work_item.waiting_for_quota_reset webhook fired with the agent that
        // was running when the class ran out. Without this assertion, dropping
        // the publish call would still pass every other test.
        var park = Assert.Single(fix.Webhooks.Events, e => e.Event == "work_item.waiting_for_quota_reset");
        var parkDetails = Assert.IsType<AgentFallbackDetails>(park.Details);
        Assert.Equal("codex", parkDetails.FromAgent);
        Assert.Null(parkDetails.ToAgent);

        // Fallback history must record both the codex→claude swap and the
        // all-exhausted park event with ToAgent==null.
        var history = await fix.FallbackHistory.ListByWorkItemAsync(item.Id, CancellationToken.None);
        Assert.Equal(2, history.Count);
        Assert.Equal(AgentKind.Claude, history[0].ToAgent);
        Assert.Null(history[1].ToAgent);

        // The class-exhausted park emits a fallbacks counter with to_agent=(none)
        // and kind=quota, alongside the earlier codex→claude swap. Both must be
        // present and correctly tagged.
        Assert.True(metrics.Any("codeybox.agent.fallbacks",
                ("from_agent", "codex"), ("to_agent", "claude"), ("kind", "quota"), ("phase", "work")),
            "expected the codex→claude quota fallback measurement");
        Assert.True(metrics.Any("codeybox.agent.fallbacks",
                ("from_agent", "claude"), ("to_agent", "(none)"), ("kind", "quota"), ("phase", "work")),
            "expected the all-exhausted codeybox.agent.fallbacks{to_agent=(none)} measurement");
    }

    [Fact]
    public async Task ThreeMemberClass_SecondMemberExhausted_FallsBackToThird()
    {
        // The task spec calls out '3-member class with top member injected to
        // return QuotaExhausted; pipeline dispatches same iteration successfully
        // to member #2'. With only two members the loop body that scans
        // candidates for an unused one only runs once on each side; a regression
        // in the 'continue if already tried' branch is undetectable at N=2.
        //
        // Originally skipped after the cb-provider-loose-coupling merge dropped
        // the "rate_limit_exceeded" pattern from CodexQuotaFailureDetector — with
        // the pattern missing, codex's quota stderr was never classified as a
        // quota failure, so MoveToNextMemberOrThrowAsync never fired and claude
        // stayed at CallCount=0. Commit 3d6777a re-added the pattern; this test
        // re-enables the assertion so a future loose-coupling refactor can't
        // silently drop it again.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipelineThreeMembers(seed);

        var quotaErr = new AgentResult(false, "exit 1", null, "API Error: rate_limit_exceeded");
        fix.Codex.ScriptedFailures.Enqueue(quotaErr);
        fix.Claude.ScriptedFailures.Enqueue(quotaErr);
        fix.Gemini.WorkPlan.Enqueue(new FileWrite("c.txt", "v1"));

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        // Each of codex+claude+gemini is invoked exactly once for the work phase
        // (codex: scripted quota fail, claude: scripted quota fail, gemini:
        // succeeds); codex+gemini also run the merge phase short-circuit, claude
        // is not invoked again because it is still marked exhausted in-process.
        Assert.Equal(1, fix.Claude.CallCount);
        Assert.True(fix.Gemini.CallCount >= 1);

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(finalItem);
        Assert.NotEqual(WorkItemState.Failed, finalItem!.State);
        Assert.NotEqual(WorkItemState.WaitingForQuotaReset, finalItem.State);

        // Two fallback events recorded: codex→claude and claude→gemini.
        var history = await fix.FallbackHistory.ListByWorkItemAsync(item.Id, CancellationToken.None);
        Assert.Equal(2, history.Count);
        Assert.Equal(AgentKind.Codex, history[0].FromAgent);
        Assert.Equal(AgentKind.Claude, history[0].ToAgent);
        Assert.Equal(AgentKind.Claude, history[1].FromAgent);
        Assert.Equal(AgentKind.Gemini, history[1].ToAgent);
    }

    [Fact]
    public async Task ThreeMemberClass_DispatchSequence_TracesEveryStep()
    {
        // Focused regression guard. The parent test above only asserts the *end*
        // state of a 3-member fallback chain; this one nails down each individual
        // step so a regression that breaks the dispatch ordering can be diagnosed
        // from one failing assertion rather than a vague "Claude.CallCount = 0".
        //
        // What we pin down:
        //   1. Codex's first quota failure DOES classify as a quota failure
        //      (Codex.CallCount=1: the work pick; the clean merge runs host-side
        //      with no agent, so there is no merge-phase Codex invocation).
        //   2. The work-phase fallback dispatch reaches Claude exactly once.
        //   3. After Claude also fails on quota, fallback reaches Gemini.
        //   4. MarkExhausted is called on codex+claude only — gemini ran to
        //      success and must not have been marked.
        //   5. Both fallback-history records belong to the WORK phase (no
        //      spurious cross-phase entries from the merge short-circuit).
        //   6. Neither record carries a null ToAgent — the class did NOT
        //      report "all members exhausted" since gemini succeeded.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipelineThreeMembers(seed);

        var quotaErr = new AgentResult(false, "exit 1", null, "API Error: rate_limit_exceeded");
        fix.Codex.ScriptedFailures.Enqueue(quotaErr);
        fix.Claude.ScriptedFailures.Enqueue(quotaErr);
        fix.Gemini.WorkPlan.Enqueue(new FileWrite("c.txt", "v1"));

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        // (1) Codex was invoked once: the work pick that returned quota. A clean
        // merge is completed host-side with no agent, so there is no second
        // (merge-phase) Codex invocation.
        Assert.Equal(1, fix.Codex.CallCount);

        // (2) Claude was reached via fallback exactly once.
        Assert.Equal(1, fix.Claude.CallCount);

        // (3) Gemini ran the work-phase write that finally succeeded.
        Assert.Equal(1, fix.Gemini.CallCount);

        // (4) Exhaustion was marked on codex+claude only; gemini's success path
        // must not leak a MarkExhausted call.
        Assert.Equal([AgentKind.Codex], fix.CodexProbe.MarkedExhausted);
        Assert.Equal([AgentKind.Claude], fix.ClaudeProbe.MarkedExhausted);
        Assert.Empty(fix.GeminiProbe.MarkedExhausted);

        // (5) + (6) Fallback history captures the work-phase chain only, in
        // order, with no all-exhausted park event.
        var history = await fix.FallbackHistory.ListByWorkItemAsync(item.Id, CancellationToken.None);
        Assert.Equal(2, history.Count);
        Assert.All(history, h => Assert.Equal("work", h.Phase));
        Assert.Equal(AgentKind.Codex, history[0].FromAgent);
        Assert.Equal(AgentKind.Claude, history[0].ToAgent);
        Assert.Equal(AgentKind.Claude, history[1].FromAgent);
        Assert.Equal(AgentKind.Gemini, history[1].ToAgent);
        Assert.All(history, h => Assert.NotNull(h.ToAgent));

        // Final state must be a success terminal — no Failed / no parked.
        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(finalItem);
        Assert.NotEqual(WorkItemState.Failed, finalItem!.State);
        Assert.NotEqual(WorkItemState.WaitingForQuotaReset, finalItem.State);
    }

    [Fact]
    public async Task CostReconciliation_PartialAgentOneCost_PlusSuccessfulAgentTwoCost_BothRecorded()
    {
        // Task spec item #4: partial-iteration cost on agent #1 still counts;
        // sum it AND the successful retry cost on agent #2 into the iteration's
        // usage total. The cost-record schema already supports multiple rows
        // per iteration; assert here that two rows actually land after a
        // Codex→Claude fallback so a regression that gates cost recording on
        // success doesn't silently halve the bill.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var costStore = new PipelineRunnerCostCaptureTests.RecordingCostStore();
        using var fix = BuildPipelineWithCost(seed, costStore);

        fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 1",
            Stdout: null,
            Stderr: "rate_limit_exceeded"));
        fix.Claude.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var workRows = costStore.Recorded.Where(r => r.Phase == "work").ToList();
        // One row for Codex's failed attempt + one row for Claude's successful
        // retry. Without the multi-record support the operator's bill would
        // omit either the burned codex tokens or the actual claude run.
        Assert.Equal(2, workRows.Count);
        Assert.Contains(workRows, r => r.AgentKind == AgentKind.Codex.Value);
        Assert.Contains(workRows, r => r.AgentKind == AgentKind.Claude.Value);
    }

    [Fact]
    public async Task NormalFailure_DoesNotTriggerFallback()
    {
        // Sanity / contrast: a non-quota failure must NOT fall back. The work
        // item fails as Failed/other (the legacy path) — burning Claude's quota
        // on a task Codex couldn't write would be wasted compute.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);

        fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 1",
            Stdout: null,
            Stderr: "compile error: unexpected token"));

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.Equal(1, fix.Codex.CallCount);
        Assert.Equal(0, fix.Claude.CallCount);

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(finalItem);
        Assert.Equal(WorkItemState.Failed, finalItem!.State);

        // The non-quota work failure throws out of the chokepoint, so the work
        // involvement row must be finalized failure:agent (the OutcomeForFailure
        // default). This is the only end-to-end assertion of that outcome string.
        var workRows = (await fix.Involvement.ListByWorkItemAsync(item.Id, CancellationToken.None))
            .Where(r => r.Phase == "work").ToList();
        var failedWork = Assert.Single(workRows);
        AssertInvolvement(failedWork, AgentKind.Codex, "work", null, "failure:agent");
        Assert.NotNull(failedWork.EndedAt);
    }

    [Fact]
    public async Task WorkFallbackAttempt_TimeoutFallsBackWithFreshWorkBudget()
    {
        var time = new ManualTimeProvider();
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(
            seed,
            timeProvider: time,
            phaseAbsoluteTimeoutMultiplier: 10.0);

        fix.Codex.WorkDelays.Enqueue(TimeSpan.FromSeconds(11));
        fix.Claude.WorkPlan.Enqueue(new FileWrite("a.txt", "fixed by fallback"));

        // Capture the OTel signals emitted at the real timeout-fallback site:
        // the fallback counter must record kind=timeout (not quota), and the
        // timed-out Codex attempt must record outcome=canceled. Both must be
        // live before RunAsync so the MeterListener observes the measurements.
        using var metrics = new MetricCapture("codeybox.agent.fallbacks", "codeybox.agent.invocations");

        var item = NewItem(initialAgent: AgentKind.Codex) with
        {
            WorkTimeout = TimeSpan.FromSeconds(10),
        };
        await fix.Store.CreateAsync(item);

        var workStarted = WaitForAgentPhaseStart(AgentKind.Codex, "work", fix.Codex, fix.Claude);
        var fallbackWorkStarted = WaitForAgentPhaseStart(AgentKind.Claude, "work", fix.Codex, fix.Claude);
        var pipelineTask = fix.Pipeline.RunAsync(item, CancellationToken.None);
        await WaitForPhaseStartAsync("work", workStarted, pipelineTask);
        await RunWithAdvancingTimeUntilAsync(
            fallbackWorkStarted,
            pipelineTask,
            time,
            step: TimeSpan.FromMilliseconds(100),
            maxSteps: 200);
        // The hard real-time wait after the claude fallback has started must
        // tolerate the highly-parallel audit environment: a 10 s ceiling
        // flaked under load even though the pipeline completes in well
        // under a second locally. Lift to 60 s so a true regression still
        // fails the test but a heavily-loaded run does not.
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(60));

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(finalItem);
        Assert.Equal(WorkItemState.Done, finalItem!.State);

        var history = await fix.FallbackHistory.ListByWorkItemAsync(item.Id, CancellationToken.None);
        var fallback = Assert.Single(history, h => h.Phase == "work");
        Assert.Equal(AgentKind.Codex, fallback.FromAgent);
        Assert.Equal(AgentKind.Claude, fallback.ToAgent);
        Assert.Contains("per-attempt timeout", fallback.Reason);

        // The per-attempt timeout must close codex's work row as failure:timeout
        // and open a fresh success row for claude. This is the only end-to-end
        // assertion of the failure:timeout involvement outcome.
        var workRows = (await fix.Involvement.ListByWorkItemAsync(item.Id, CancellationToken.None))
            .Where(r => r.Phase == "work").ToList();
        Assert.Collection(workRows,
            r => AssertInvolvement(r, AgentKind.Codex, "work", null, "failure:timeout"),
            r => AssertInvolvement(r, AgentKind.Claude, "work", null, "success"));

        Assert.Empty(fix.CodexProbe.MarkedExhausted);
        Assert.Empty(fix.ClaudeProbe.MarkedExhausted);

        var webhook = Assert.Single(fix.Webhooks.Events, e => e.Event == "agent.fallback");
        var details = Assert.IsType<AgentFallbackDetails>(webhook.Details);
        Assert.Equal("work", details.Phase);
        Assert.Equal("codex", details.FromAgent);
        Assert.Equal("claude", details.ToAgent);
        Assert.Contains("per-attempt timeout", details.Reason);

        // The fallback counter distinguishes a timeout-driven swap from a quota
        // swap via the kind tag; inverting the two would go undetected without
        // this assertion since the history/webhook only carry a free-text reason.
        Assert.True(metrics.Any("codeybox.agent.fallbacks",
                ("from_agent", "codex"), ("to_agent", "claude"), ("kind", "timeout"), ("phase", "work")),
            "expected a codeybox.agent.fallbacks{kind=timeout} measurement for the work-phase timeout swap");

        // Codex's timed-out attempt records outcome=canceled (distinct from the
        // error/success outcomes covered elsewhere); Claude's retry succeeds.
        Assert.True(metrics.Any("codeybox.agent.invocations",
                ("agent.kind", "codex"), ("phase", "work"), ("outcome", "canceled")),
            "expected a codeybox.agent.invocations{outcome=canceled} measurement for the timed-out Codex attempt");
        Assert.True(metrics.Any("codeybox.agent.invocations",
                ("agent.kind", "claude"), ("phase", "work"), ("outcome", "success")),
            "expected a codeybox.agent.invocations{outcome=success} measurement for the Claude fallback");
    }

    [Fact]
    public async Task WorkFallbackAttempt_HostShutdownSourceBeatsAttemptTimeoutFallback()
    {
        var time = new ManualTimeProvider();
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(
            seed,
            timeProvider: time,
            phaseAbsoluteTimeoutMultiplier: 10.0);

        fix.Codex.WorkDelays.Enqueue(TimeSpan.FromSeconds(11));
        fix.Claude.WorkPlan.Enqueue(new FileWrite("a.txt", "fallback should not run"));

        var item = NewItem(initialAgent: AgentKind.Codex) with
        {
            WorkTimeout = TimeSpan.FromSeconds(10),
        };
        await fix.Store.CreateAsync(item);

        using var hostShutdownCts = new CancellationTokenSource();
        var workStarted = WaitForAgentPhaseStart(AgentKind.Codex, "work", fix.Codex, fix.Claude);
        var pipelineTask = fix.Pipeline.RunAsync(item, CancellationToken.None, hostShutdownCts.Token);
        await WaitForPhaseStartAsync("work", workStarted, pipelineTask);

        await hostShutdownCts.CancelAsync();
        time.Advance(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipelineTask.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(1, fix.Codex.CallCount);
        Assert.Equal(0, fix.Claude.CallCount);
        Assert.Empty(await fix.FallbackHistory.ListByWorkItemAsync(item.Id, CancellationToken.None));

        // The per-attempt timeout (10s) elapses at the same tick the host
        // shutdown fires; the timeout finalize wins the race, so codex's work row
        // is stamped failure:timeout even though shutdown decides the final
        // disposition (item stays Working, no fallback). Pins that attribution
        // under the timeout-vs-shutdown race and that the row never dangles.
        var workRow = Assert.Single(
            await fix.Involvement.ListByWorkItemAsync(item.Id, CancellationToken.None),
            r => r.Phase == "work");
        AssertInvolvement(workRow, AgentKind.Codex, "work", null, "failure:timeout");
        Assert.NotNull(workRow.EndedAt);

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(finalItem);
        Assert.Equal(WorkItemState.Working, finalItem!.State);
        Assert.Null(finalItem.FailureKind);
    }

    [Fact]
    public async Task ReworkFallbackAttempt_GetsFreshWorkTimeoutBudget()
    {
        var time = new ManualTimeProvider();
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(
            seed,
            [new OnceFailingAuditor()],
            maxAuditIterations: 2,
            timeProvider: time);

        fix.Codex.WorkPlan.Enqueue(new FileWrite("a.txt", "initial"));
        fix.Codex.ReworkDelays.Enqueue(TimeSpan.FromSeconds(35));
        fix.Codex.ReworkScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 1",
            Stdout: null,
            Stderr: "API Error: rate_limit_exceeded"));

        fix.Claude.ReworkDelays.Enqueue(TimeSpan.FromSeconds(35));
        fix.Claude.WorkPlan.Enqueue(new FileWrite("a.txt", "fixed"));

        var item = NewItem(initialAgent: AgentKind.Codex) with
        {
            WorkTimeout = TimeSpan.FromSeconds(60),
        };
        await fix.Store.CreateAsync(item);

        var reworkStarted = WaitForAgentPhaseStart(AgentKind.Codex, "rework", fix.Codex, fix.Claude);
        var fallbackReworkStarted = WaitForAgentPhaseStart(AgentKind.Claude, "rework", fix.Codex, fix.Claude);
        var pipelineTask = fix.Pipeline.RunAsync(item, CancellationToken.None);
        await WaitForReworkStartAsync(reworkStarted, pipelineTask);
        await RunWithAdvancingTimeUntilAsync(
            fallbackReworkStarted,
            pipelineTask,
            time,
            step: TimeSpan.FromMilliseconds(100),
            maxSteps: 500);
        var fallbackStartedAt = time.GetUtcNow() - DateTimeOffset.UnixEpoch;
        await AdvanceManualTimeToElapsedAsync(
            time,
            fallbackStartedAt + TimeSpan.FromSeconds(40),
            pipelineTask,
            step: TimeSpan.FromMilliseconds(100));
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(10));

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(finalItem);
        Assert.Equal(WorkItemState.Done, finalItem!.State);

        var history = await fix.FallbackHistory.ListByWorkItemAsync(item.Id, CancellationToken.None);
        Assert.Contains(history, h =>
            h.Phase == "rework"
            && h.FromAgent == AgentKind.Codex
            && h.ToAgent == AgentKind.Claude);
    }

    [Fact]
    public async Task ReworkFallbackPhase_UsesConfiguredAbsoluteTimeoutThroughPipeline()
    {
        var time = new ManualTimeProvider();
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipelineThreeMembers(
            seed,
            [new OnceFailingAuditor()],
            maxAuditIterations: 2,
            timeProvider: time,
            phaseAbsoluteTimeoutMultiplier: 1.5);

        fix.Codex.WorkPlan.Enqueue(new FileWrite("a.txt", "initial"));
        fix.Codex.ReworkDelays.Enqueue(TimeSpan.FromSeconds(3));
        fix.Claude.ReworkDelays.Enqueue(TimeSpan.FromSeconds(3));
        fix.Gemini.ReworkDelays.Enqueue(TimeSpan.FromMilliseconds(9500));
        foreach (var agent in new[] { fix.Codex, fix.Claude, fix.Gemini })
            agent.ReworkScriptedFailures.Enqueue(new AgentResult(
                Success: false,
                Summary: "agent exited 1",
                Stdout: null,
                Stderr: "API Error: rate_limit_exceeded"));

        var item = NewItem(initialAgent: AgentKind.Codex) with
        {
            WorkTimeout = TimeSpan.FromSeconds(10),
        };
        await fix.Store.CreateAsync(item);

        var reworkStarted = WaitForReworkStart(fix.Codex, fix.Claude, fix.Gemini);
        var pipelineTask = fix.Pipeline.RunAsync(item, CancellationToken.None);
        await WaitForReworkStartAsync(reworkStarted, pipelineTask);
        await RunWithAdvancingTimeAsync(pipelineTask, time, step: TimeSpan.FromMilliseconds(100), maxSteps: 250);

        var elapsed = time.GetUtcNow() - DateTimeOffset.UnixEpoch;
        Assert.InRange(elapsed, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(17));

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(finalItem);
        Assert.Equal(WorkItemState.Failed, finalItem!.State);
        Assert.Equal("timeout", finalItem.FailureKind);
        Assert.Equal(CancellationSources.PhaseTimeout("rework"), finalItem.CancellationSource);

        var history = await fix.FallbackHistory.ListByWorkItemAsync(item.Id, CancellationToken.None);
        Assert.Equal(2, history.Count);
        Assert.Equal(AgentKind.Claude, history[0].ToAgent);
        Assert.Equal(AgentKind.Gemini, history[1].ToAgent);
    }

    [Fact]
    public async Task ReworkFallbackAttemptTimeouts_AreBoundedByAbsolutePhaseCap()
    {
        var time = new ManualTimeProvider();
        var attemptTimeout = TimeSpan.FromMinutes(240);
        var phaseCap = TimeSpan.FromMinutes(720);
        var fallbackAdvanceStep = TimeSpan.FromMinutes(10);
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipelineThreeMembers(
            seed,
            [new OnceFailingAuditor()],
            maxAuditIterations: 2,
            timeProvider: time,
            phaseAbsoluteTimeoutMultiplier: 3.0);

        fix.Gemini.ReworkDelays.Enqueue(TimeSpan.FromMinutes(250));
        fix.Codex.ReworkDelays.Enqueue(TimeSpan.FromMinutes(250));
        fix.Claude.ReworkDelays.Enqueue(TimeSpan.FromMinutes(250));
        fix.Gemini.WorkPlan.Enqueue(new FileWrite("a.txt", "initial"));

        var item = NewItem(initialAgent: AgentKind.Gemini) with
        {
            WorkTimeout = attemptTimeout,
        };
        await fix.Store.CreateAsync(item);

        var reworkStarted = WaitForAgentPhaseStart(AgentKind.Gemini, "rework", fix.Codex, fix.Claude, fix.Gemini);
        var codexReworkStarted = WaitForAgentPhaseStart(AgentKind.Codex, "rework", fix.Codex, fix.Claude, fix.Gemini);
        var claudeReworkStarted = WaitForAgentPhaseStart(AgentKind.Claude, "rework", fix.Codex, fix.Claude, fix.Gemini);
        var pipelineTask = fix.Pipeline.RunAsync(item, CancellationToken.None);
        await WaitForReworkStartAsync(reworkStarted, pipelineTask);

        await RunWithAdvancingTimeUntilAsync(
            codexReworkStarted,
            pipelineTask,
            time,
            step: fallbackAdvanceStep,
            maxSteps: 36);
        await RunWithAdvancingTimeUntilAsync(
            claudeReworkStarted,
            pipelineTask,
            time,
            step: fallbackAdvanceStep,
            maxSteps: 36);

        await AdvanceManualTimeToElapsedAsync(
            time,
            phaseCap - TimeSpan.FromSeconds(10),
            pipelineTask,
            step: TimeSpan.FromSeconds(10));
        Assert.False(pipelineTask.IsCompleted, "The rework phase completed before the absolute timeout cap.");

        await AdvanceManualTimeToElapsedAsync(
            time,
            phaseCap,
            pipelineTask,
            step: TimeSpan.FromSeconds(10),
            maxSteps: 10);

        var elapsed = time.GetUtcNow() - DateTimeOffset.UnixEpoch;
        Assert.Equal(phaseCap, elapsed);
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(10));

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(finalItem);
        Assert.Equal(WorkItemState.Failed, finalItem!.State);
        Assert.Equal("timeout", finalItem.FailureKind);
        Assert.Equal(CancellationSources.PhaseTimeout("rework"), finalItem.CancellationSource);

        var history = await fix.FallbackHistory.ListByWorkItemAsync(item.Id, CancellationToken.None);
        Assert.Equal(2, history.Count);
        Assert.Equal(AgentKind.Codex, history[0].ToAgent);
        Assert.Equal(AgentKind.Claude, history[1].ToAgent);

        Assert.Empty(fix.GeminiProbe.MarkedExhausted);
        Assert.Empty(fix.CodexProbe.MarkedExhausted);
        Assert.Empty(fix.ClaudeProbe.MarkedExhausted);

        var webhooks = fix.Webhooks.Events.Where(e => e.Event == "agent.fallback").ToList();
        Assert.Equal(2, webhooks.Count);
        Assert.All(webhooks, webhook =>
        {
            var details = Assert.IsType<AgentFallbackDetails>(webhook.Details);
            Assert.Equal("rework", details.Phase);
            Assert.Contains("per-attempt timeout", details.Reason);
        });
    }

    [Fact]
    public async Task ReworkFallbackAttempt_FinalTimeoutAfterAllMembers_FailsAsTimeout()
    {
        var time = new ManualTimeProvider();
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipelineThreeMembers(
            seed,
            [new OnceFailingAuditor()],
            maxAuditIterations: 2,
            timeProvider: time,
            phaseAbsoluteTimeoutMultiplier: 10.0);

        fix.Codex.WorkPlan.Enqueue(new FileWrite("a.txt", "initial"));
        fix.Codex.ReworkDelays.Enqueue(TimeSpan.FromSeconds(11));
        fix.Claude.ReworkDelays.Enqueue(TimeSpan.FromSeconds(11));
        fix.Gemini.ReworkDelays.Enqueue(TimeSpan.FromSeconds(11));

        var item = NewItem(initialAgent: AgentKind.Codex) with
        {
            WorkTimeout = TimeSpan.FromSeconds(10),
        };
        await fix.Store.CreateAsync(item);

        var reworkStarted = WaitForReworkStart(fix.Codex, fix.Claude, fix.Gemini);
        var pipelineTask = fix.Pipeline.RunAsync(item, CancellationToken.None);
        await WaitForReworkStartAsync(reworkStarted, pipelineTask);
        await RunWithAdvancingTimeAsync(
            pipelineTask,
            time,
            step: TimeSpan.FromMilliseconds(100),
            maxSteps: 500);

        var elapsed = time.GetUtcNow() - DateTimeOffset.UnixEpoch;
        Assert.InRange(elapsed, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(32));

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(finalItem);
        Assert.Equal(WorkItemState.Failed, finalItem!.State);
        Assert.Equal("timeout", finalItem.FailureKind);
        Assert.Equal(CancellationSources.PhaseTimeout("rework"), finalItem.CancellationSource);

        var history = await fix.FallbackHistory.ListByWorkItemAsync(item.Id, CancellationToken.None);
        Assert.Equal(2, history.Count);
        Assert.Equal(AgentKind.Claude, history[0].ToAgent);
        Assert.Equal(AgentKind.Gemini, history[1].ToAgent);
    }

    [Fact]
    public async Task InterruptedReworkResumeFallbackAttempt_TimeoutFallsBackWithFreshWorkBudget()
    {
        var time = new ManualTimeProvider();
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(
            seed,
            timeProvider: time,
            phaseAbsoluteTimeoutMultiplier: 30.0);

        fix.Codex.ReworkDelays.Enqueue(TimeSpan.FromSeconds(11));
        fix.Claude.WorkPlan.Enqueue(new FileWrite("resume.txt", "resumed by fallback"));

        var item = NewItem(initialAgent: AgentKind.Codex) with
        {
            State = WorkItemState.Reworking,
            WorkBranch = "codeybox/rework-resume",
            WorkTimeout = TimeSpan.FromSeconds(10),
            PreemptedAt = DateTimeOffset.UtcNow,
        };
        item = item with { PreemptCheckpoint = $"refs/heads/codeybox/preempt/{item.Id}" };
        await CreatePreemptCheckpointAsync(fix.GitHost, item, seed);
        await fix.Store.CreateAsync(item);

        var reworkStarted = WaitForAgentPhaseStart(AgentKind.Codex, "rework", fix.Codex, fix.Claude);
        var fallbackReworkStarted = WaitForAgentPhaseStart(AgentKind.Claude, "rework", fix.Codex, fix.Claude);
        var pipelineTask = fix.Pipeline.RunAsync(item, CancellationToken.None);
        await WaitForReworkStartAsync(reworkStarted, pipelineTask);
        await RunWithAdvancingTimeUntilAsync(
            fallbackReworkStarted,
            pipelineTask,
            time,
            step: TimeSpan.FromMilliseconds(100),
            maxSteps: 200);
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(10));

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(finalItem);
        Assert.True(finalItem!.State == WorkItemState.Done, finalItem.LastError);
        Assert.Null(finalItem.PreemptedAt);
        Assert.Null(finalItem.PreemptCheckpoint);

        var history = await fix.FallbackHistory.ListByWorkItemAsync(item.Id, CancellationToken.None);
        var fallback = Assert.Single(history, h => h.Phase == "rework");
        Assert.Equal(AgentKind.Codex, fallback.FromAgent);
        Assert.Equal(AgentKind.Claude, fallback.ToAgent);
        Assert.Contains("per-attempt timeout", fallback.Reason);
    }

    [Fact]
    public async Task MergeFallbackAttempt_TimeoutFallsBackWithFreshMergeBudget()
    {
        var time = new ManualTimeProvider();
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        // A clean merge runs host-side with no agent and cannot time out, so the
        // per-attempt merge timeout + fallback machinery is exercised only on the
        // agentic conflict-resolver path. Induce a README conflict (work writes
        // README; the auditor advances main's README during audit) so the merge
        // phase runs the resolver agent: Codex's resolver hangs past the merge
        // timeout → fallback to Claude, whose resolver resolves and lands the
        // merge with a fresh merge budget.
        var auditor = new MainAdvancingAuditor(_workspace, "README.md", "main side\n");
        using var fix = BuildPipeline(
            seed,
            auditors: [auditor],
            timeProvider: time,
            phaseAbsoluteTimeoutMultiplier: 10.0);
        auditor.GitHost = fix.GitHost;

        fix.Codex.WorkPlan.Enqueue(new FileWrite("README.md", "work side\n"));
        fix.Codex.MergeDelays.Enqueue(TimeSpan.FromSeconds(11));

        var item = NewItem(initialAgent: AgentKind.Codex) with
        {
            MergeTimeout = TimeSpan.FromSeconds(10),
        };
        await fix.Store.CreateAsync(item);

        var mergeStarted = WaitForPhaseStart("merge", fix.Codex, fix.Claude);
        var fallbackMergeStarted = WaitForAgentPhaseStart(AgentKind.Claude, "merge", fix.Codex, fix.Claude);
        var pipelineTask = fix.Pipeline.RunAsync(item, CancellationToken.None);
        await WaitForPhaseStartAsync("merge", mergeStarted, pipelineTask);
        await RunWithAdvancingTimeUntilAsync(
            fallbackMergeStarted,
            pipelineTask,
            time,
            step: TimeSpan.FromMilliseconds(100),
            maxSteps: 200);
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(10));

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(finalItem);
        Assert.True(finalItem!.State == WorkItemState.Done, finalItem.LastError);

        var history = await fix.FallbackHistory.ListByWorkItemAsync(item.Id, CancellationToken.None);
        var fallback = Assert.Single(history, h => h.Phase == "merge");
        Assert.Equal(AgentKind.Codex, fallback.FromAgent);
        Assert.Equal(AgentKind.Claude, fallback.ToAgent);
        Assert.Contains("per-attempt timeout", fallback.Reason);
    }

    [Fact]
    public async Task NoRouterSingleAgentAttempt_UsesWorkTimeoutBeforeAbsoluteCap()
    {
        var time = new ManualTimeProvider();
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(
            seed,
            timeProvider: time,
            useClassRouter: false,
            stuckThresholdMinutes: 0);

        fix.Codex.WorkDelays.Enqueue(TimeSpan.FromSeconds(25));

        var item = NewItem(initialAgent: AgentKind.Codex) with
        {
            AgentClassId = null,
            WorkTimeout = TimeSpan.FromSeconds(10),
        };
        await fix.Store.CreateAsync(item);

        var workStarted = WaitForPhaseStart("work", fix.Codex);
        var pipelineTask = fix.Pipeline.RunAsync(item, CancellationToken.None);
        await WaitForPhaseStartAsync("work", workStarted, pipelineTask);
        await RunWithAdvancingTimeAsync(
            pipelineTask,
            time,
            step: TimeSpan.FromMilliseconds(100),
            maxSteps: 400);

        var elapsed = time.GetUtcNow() - DateTimeOffset.UnixEpoch;
        Assert.InRange(elapsed, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(12));

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(finalItem);
        Assert.Equal(WorkItemState.Failed, finalItem!.State);
        Assert.Equal("timeout", finalItem.FailureKind);
        Assert.Equal(CancellationSources.PhaseTimeout("work"), finalItem.CancellationSource);
        Assert.Empty(await fix.FallbackHistory.ListByWorkItemAsync(item.Id, CancellationToken.None));
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private TestFixture BuildPipeline(
        string seedRepoUrl,
        IReadOnlyList<IAuditor>? auditors = null,
        int maxAuditIterations = 1,
        TimeProvider? timeProvider = null,
        double phaseAbsoluteTimeoutMultiplier = 3.0,
        bool useClassRouter = true,
        int stuckThresholdMinutes = -1,
        Func<InMemoryAgentInvolvementStore, IAgentInvolvementStore>? wrapInvolvement = null,
        ProjectNetworkProfiles? networkProfiles = null,
        IInVmSmokeGate? inVmSmokeGate = null,
        QuotaRouterOptions? quotaOptions = null,
        AgentQuotaSnapshot? codexQuotaSnapshot = null,
        AgentQuotaSnapshot? claudeQuotaSnapshot = null,
        bool singleMemberClass = false,
        IQuotaFailureStore? quotaFailures = null,
        bool wireAuditProgress = false,
        IAgentAuthFailureClassifier? authFailureClassifier = null,
        IAgentAvailabilityRegistry? availability = null,
        PipelineTuningSnapshot? pipelineTuning = null,
        IAgentQuotaAvailabilityPublisher? quotaAvailabilityPublisher = null)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var webhooks = new CapturingWebhookDispatcher();

        var codex = new ScriptableAgent(AgentKind.Codex, timeProvider);
        var claude = new ScriptableAgent(AgentKind.Claude, timeProvider);
        var registry = new AgentRegistry([codex, claude]);

        var frontier = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members = singleMemberClass
                ?
                [
                    new AgentMembership { Agent = AgentKind.Codex, Billing = AgentBilling.Subscription, QualityScore = 100 },
                ]
                :
                [
                    // Codex first by config-order tiebreak (same effective score).
                    new AgentMembership { Agent = AgentKind.Codex, Billing = AgentBilling.Subscription, QualityScore = 100 },
                    new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                ],
        };

        var auditorList = auditors ?? [];
        var effectiveAuditors = auditorList.Any(RequiresBuildTestGate)
            ? TestAuditGates.WithPassedBuildAndTest(auditorList)
            : auditorList;
        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Codex,
            DefaultAgentClass = useClassRouter ? "frontier" : null,
            NetworkProfiles = networkProfiles ?? new ProjectNetworkProfiles(),
            Audit = new ProjectAudit
            {
                MaxIterations = maxAuditIterations,
                AuditTypes = auditorList.Count > 0 ? ["scripted"] : [],
                StuckThresholdMinutes = stuckThresholdMinutes,
            },
        };

        var projects = new InMemoryProjectRepository(project);
        var composer = new ProjectAuditorComposer(new ScriptedAuditorCatalog(effectiveAuditors));

        var codexProbe = new RecordingProbe(AgentKind.Codex, codexQuotaSnapshot);
        var claudeProbe = new RecordingProbe(AgentKind.Claude, claudeQuotaSnapshot);

        var resolvedQuotaOptions = quotaOptions ?? new QuotaRouterOptions { MinQuotaPct = 10.0 };
        var router = new AgentClassRouter(
            [frontier],
            [codexProbe, claudeProbe],
            resolvedQuotaOptions,
            NullLogger<AgentClassRouter>.Instance,
            quotaFailures: quotaFailures,
            quotaAvailabilityPublisher: quotaAvailabilityPublisher);

        var fallbackHistory = new InMemoryAgentFallbackHistoryStore();
        var involvement = new InMemoryAgentInvolvementStore();
        // Tests assert against the inner InMemory store; an optional wrapper lets a
        // test inject store faults while still reading the rows that landed.
        IAgentInvolvementStore involvementForPipeline = wrapInvolvement?.Invoke(involvement) ?? involvement;
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);
        var authAvailability = availability as IAgentAuthAvailabilityRegistry
            ?? new AgentAvailabilityRegistry(
                new AvailabilityOptions(),
                TimeProvider.System,
                NullLogger<AgentAvailabilityRegistry>.Instance);
        if (inVmSmokeGate is AuthCorroboratingInVmSmokeGate authCorroboratingGate)
            authCorroboratingGate.AttachAuthRegistry(authAvailability, webhooks);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, new TestUpstreamFactory(), composer,
            store, webhooks,
            new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
                PhaseAbsoluteTimeoutMultiplier = phaseAbsoluteTimeoutMultiplier,
                TimeProvider = timeProvider ?? TimeProvider.System,
            },
            NullLogger<PipelineRunner>.Instance,
            auditQuotaProbes: [codexProbe, claudeProbe],
            auditQuotaOptions: resolvedQuotaOptions,
            classRouter: useClassRouter ? router : null,
            fallbackHistory: fallbackHistory,
            quotaClassifier: new CompositeQuotaFailureClassifier(new IAgentQuotaFailureDetector[] { new CodexQuotaFailureDetector(), new ClaudeQuotaFailureDetector() }),
            quotaFailures: quotaFailures,
            involvement: involvementForPipeline,
            auditProgress: wireAuditProgress ? store : null,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            dispatchAvailability: inVmSmokeGate is null
                ? null
                : new AgentDispatchAvailability(inVmSmokeGate: inVmSmokeGate),
            inVmSmokeGate: inVmSmokeGate,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions,
            authFailureClassifier: authFailureClassifier,
            availability: availability,
            authAvailability: authAvailability,
            pipelineTuning: pipelineTuning,
            quotaAvailabilityPublisher: quotaAvailabilityPublisher);

        return new TestFixture(pipeline, router, store, gitHost, codex, claude, codexProbe, claudeProbe, webhooks, fallbackHistory, involvement);
    }

    private static bool RequiresBuildTestGate(IAuditor auditor)
        => string.Equals(auditor.Kind, "llm", StringComparison.OrdinalIgnoreCase)
            || (auditor.Required & AuditCapabilities.AgentCredentials) != 0;

    /// <summary>
    /// Tool auditor that advances <c>main</c>'s copy of a file during the audit
    /// phase, so a work branch touching the same file merges with a conflict —
    /// routing the merge phase through the agentic conflict resolver (the only
    /// merge agent run, where the per-attempt merge timeout + fallback applies).
    /// </summary>
    private sealed class MainAdvancingAuditor : IAuditor
    {
        private readonly string _workspace;
        private readonly string _path;
        private readonly string _content;

        public LocalGitHost? GitHost { get; set; }
        public string Name => "advance-main";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public MainAdvancingAuditor(string workspace, string path, string content)
        {
            _workspace = workspace;
            _path = path;
            _content = content;
        }

        public async Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            _ = sandbox;
            _ = workingDirectory;
            _ = ct;
            if (GitHost is null)
                throw new InvalidOperationException("GitHost must be assigned before the auditor runs.");
            var barePath = GitHost.GetRepoPath(context.WorkItemId.ToString());
            var clone = Path.Combine(_workspace, "advance-main-" + Guid.NewGuid().ToString("N")[..8]);
            await TestSupport.RunGit(_workspace, "clone", barePath, clone);
            await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
            await TestSupport.RunGit(clone, "config", "user.name", "Test");
            await TestSupport.RunGit(clone, "checkout", context.BaseBranch);
            await File.WriteAllTextAsync(Path.Combine(clone, _path), _content);
            await TestSupport.RunGit(clone, "commit", "-am", "advance main during audit");
            await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{context.BaseBranch}");
            return new AuditResult(true, []);
        }
    }

    private TestFixture BuildPipelineWithCost(string seedRepoUrl, IWorkItemCostStore costStore)
    {
        // Mirrors BuildPipeline but wires a cost store + per-agent extractors so
        // we can assert that each agent invocation produces its own cost row.
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var webhooks = new CapturingWebhookDispatcher();

        var codex = new ScriptableAgent(AgentKind.Codex);
        var claude = new ScriptableAgent(AgentKind.Claude);
        var registry = new AgentRegistry([codex, claude]);

        var frontier = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members =
            [
                new AgentMembership { Agent = AgentKind.Codex, Billing = AgentBilling.Subscription, QualityScore = 100 },
                new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
            ],
        };

        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Codex,
            DefaultAgentClass = "frontier",
            Audit = new ProjectAudit { MaxIterations = 1, AuditTypes = [] },
        };

        var projects = new InMemoryProjectRepository(project);
        var composer = new ProjectAuditorComposer(new ScriptedAuditorCatalog([]));

        var codexProbe = new RecordingProbe(AgentKind.Codex);
        var claudeProbe = new RecordingProbe(AgentKind.Claude);

        var costQuotaOptions = new QuotaRouterOptions { MinQuotaPct = 10.0 };
        var router = new AgentClassRouter(
            [frontier],
            [codexProbe, claudeProbe],
            costQuotaOptions,
            NullLogger<AgentClassRouter>.Instance);

        var fallbackHistory = new InMemoryAgentFallbackHistoryStore();
        var involvement = new InMemoryAgentInvolvementStore();
        var calculator = new AgentCostCalculator(new AgentPricingOptions());
        var extractors = new Dictionary<AgentKind, IAgentCostExtractor>
        {
            [AgentKind.Codex] = new FakeFallbackExtractor(AgentKind.Codex),
            [AgentKind.Claude] = new FakeFallbackExtractor(AgentKind.Claude),
        };
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, new TestUpstreamFactory(), composer,
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            auditQuotaProbes: [codexProbe, claudeProbe],
            auditQuotaOptions: costQuotaOptions,
            costStore: costStore,
            costExtractors: extractors,
            costCalculator: calculator,
            classRouter: router,
            fallbackHistory: fallbackHistory,
            quotaClassifier: new CompositeQuotaFailureClassifier(new IAgentQuotaFailureDetector[] { new CodexQuotaFailureDetector(), new ClaudeQuotaFailureDetector() }),
            involvement: involvement,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);

        return new TestFixture(pipeline, router, store, gitHost, codex, claude, codexProbe, claudeProbe, webhooks, fallbackHistory, involvement);
    }

    private sealed class FakeFallbackExtractor : IAgentCostExtractor
    {
        public AgentKind Kind { get; }
        public FakeFallbackExtractor(AgentKind kind) { Kind = kind; }
        public AgentCostSnapshot? TryExtract(string? stdout, string? stderr)
            => new(InputTokens: 100, CachedInputTokens: 0, OutputTokens: 50, ModelId: $"fake-{Kind.Value}");
        public ModelRateConfig? DefaultPricing => null;
    }

    private ThreeMemberFixture BuildPipelineThreeMembers(
        string seedRepoUrl,
        IReadOnlyList<IAuditor>? auditors = null,
        int maxAuditIterations = 1,
        TimeProvider? timeProvider = null,
        double phaseAbsoluteTimeoutMultiplier = 3.0)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var webhooks = new CapturingWebhookDispatcher();

        var codex = new ScriptableAgent(AgentKind.Codex, timeProvider);
        var claude = new ScriptableAgent(AgentKind.Claude, timeProvider);
        var gemini = new ScriptableAgent(AgentKind.Gemini, timeProvider);
        var registry = new AgentRegistry([codex, claude, gemini]);

        // Members sort by config order (same QualityScore): codex first, then
        // claude, then gemini. Quota fallback walks the list left to right.
        var frontier = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members =
            [
                new AgentMembership { Agent = AgentKind.Codex, Billing = AgentBilling.Subscription, QualityScore = 100 },
                new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                new AgentMembership { Agent = AgentKind.Gemini, Billing = AgentBilling.Subscription, QualityScore = 100 },
            ],
        };

        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Codex,
            DefaultAgentClass = "frontier",
            Audit = new ProjectAudit { MaxIterations = 1, AuditTypes = [] },
        };

        var auditorList = auditors ?? [];
        project = project with
        {
            Audit = new ProjectAudit
            {
                MaxIterations = maxAuditIterations,
                AuditTypes = auditorList.Count > 0 ? ["scripted"] : [],
            },
        };

        var projects = new InMemoryProjectRepository(project);
        var composer = new ProjectAuditorComposer(new ScriptedAuditorCatalog(auditorList));

        var codexProbe = new RecordingProbe(AgentKind.Codex);
        var claudeProbe = new RecordingProbe(AgentKind.Claude);
        var geminiProbe = new RecordingProbe(AgentKind.Gemini);

        var threeMemberQuotaOptions = new QuotaRouterOptions { MinQuotaPct = 10.0 };
        var router = new AgentClassRouter(
            [frontier],
            [codexProbe, claudeProbe, geminiProbe],
            threeMemberQuotaOptions,
            NullLogger<AgentClassRouter>.Instance);

        var fallbackHistory = new InMemoryAgentFallbackHistoryStore();
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, new TestUpstreamFactory(), composer,
            store, webhooks,
            new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
                PhaseAbsoluteTimeoutMultiplier = phaseAbsoluteTimeoutMultiplier,
                TimeProvider = timeProvider ?? TimeProvider.System,
            },
            NullLogger<PipelineRunner>.Instance,
            auditQuotaProbes: [codexProbe, claudeProbe, geminiProbe],
            auditQuotaOptions: threeMemberQuotaOptions,
            classRouter: router,
            fallbackHistory: fallbackHistory,
            quotaClassifier: new CompositeQuotaFailureClassifier(new IAgentQuotaFailureDetector[] { new CodexQuotaFailureDetector(), new ClaudeQuotaFailureDetector(), new GeminiQuotaFailureDetector() }),
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);

        return new ThreeMemberFixture(
            pipeline,
            store,
            codex,
            claude,
            gemini,
            codexProbe,
            claudeProbe,
            geminiProbe,
            webhooks,
            fallbackHistory);
    }

    private static Task WaitForReworkStart(params ScriptableAgent[] agents) =>
        WaitForPhaseStart("rework", agents);

    private static Task WaitForPhaseStart(string phase, params ScriptableAgent[] agents)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var agent in agents)
        {
            agent.PhaseInvocationStarted += (_, startedPhase) =>
            {
                if (startedPhase == phase)
                    started.TrySetResult();
            };
        }

        return started.Task;
    }

    private static Task WaitForAgentPhaseStart(AgentKind agentKind, string phase, params ScriptableAgent[] agents)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var agent in agents)
        {
            agent.PhaseInvocationStarted += (startedAgent, startedPhase) =>
            {
                if (startedAgent == agentKind && startedPhase == phase)
                    started.TrySetResult();
            };
        }

        return started.Task;
    }

    private static Task WaitForReworkStartAsync(Task reworkStarted, Task pipelineTask) =>
        WaitForPhaseStartAsync("rework", reworkStarted, pipelineTask);

    private static async Task WaitForPhaseStartAsync(string phase, Task phaseStarted, Task pipelineTask)
    {
        var completed = await Task.WhenAny(phaseStarted, pipelineTask, Task.Delay(TimeSpan.FromSeconds(10)));
        if (completed == phaseStarted)
            return;
        if (completed == pipelineTask)
            await pipelineTask;
        throw new TimeoutException($"Pipeline did not reach {phase} before the test timeout.");
    }

    private static async Task RunWithAdvancingTimeAsync(
        Task pipelineTask,
        ManualTimeProvider time,
        TimeSpan? step = null,
        int maxSteps = 200)
    {
        var delta = step ?? TimeSpan.FromMilliseconds(20);
        for (var i = 0; i < maxSteps && !pipelineTask.IsCompleted; i++)
        {
            time.Advance(delta);
            var completed = await Task.WhenAny(
                pipelineTask,
                Task.Delay(TimeSpan.FromMilliseconds(25)));
            if (completed == pipelineTask)
                break;
        }

        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task AdvanceManualTimeToElapsedAsync(
        ManualTimeProvider time,
        TimeSpan targetElapsed,
        Task pipelineTask,
        TimeSpan? step = null,
        int maxSteps = 5000)
    {
        var delta = step ?? TimeSpan.FromMilliseconds(20);
        for (var i = 0; i < maxSteps && !pipelineTask.IsCompleted; i++)
        {
            var elapsed = time.GetUtcNow() - DateTimeOffset.UnixEpoch;
            if (elapsed >= targetElapsed)
                return;

            var remaining = targetElapsed - elapsed;
            time.Advance(remaining < delta ? remaining : delta);
            await Task.Delay(1);
        }

        if (time.GetUtcNow() - DateTimeOffset.UnixEpoch < targetElapsed && !pipelineTask.IsCompleted)
            throw new TimeoutException("Manual time did not reach the requested elapsed target.");
    }

    private static async Task RunWithAdvancingTimeUntilAsync(
        Task targetTask,
        Task pipelineTask,
        ManualTimeProvider time,
        TimeSpan? step = null,
        int maxSteps = 200)
    {
        var delta = step ?? TimeSpan.FromMilliseconds(20);
        for (var i = 0; i < maxSteps && !targetTask.IsCompleted && !pipelineTask.IsCompleted; i++)
        {
            time.Advance(delta);
            var completed = await Task.WhenAny(
                targetTask,
                pipelineTask,
                Task.Delay(TimeSpan.FromMilliseconds(10)));
            if (completed == targetTask)
                return;
            if (completed == pipelineTask)
                break;
        }

        if (targetTask.IsCompleted)
            return;

        if (pipelineTask.IsCompleted)
        {
            await pipelineTask;
            throw new InvalidOperationException("Pipeline completed before the expected fallback attempt started.");
        }

        var settled = await Task.WhenAny(
            targetTask,
            pipelineTask,
            Task.Delay(TimeSpan.FromSeconds(10)));
        if (settled == targetTask)
            return;
        if (settled == pipelineTask)
        {
            await pipelineTask;
            throw new InvalidOperationException("Pipeline completed before the expected fallback attempt started.");
        }

        throw new TimeoutException("Pipeline did not reach the expected fallback attempt before the manual-time limit.");
    }

    private static WorkItem NewItem(AgentKind initialAgent) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "fallback test",
        Prompt = "do thing",
        BaseBranch = "main",
        Agent = initialAgent,
        AgentClassId = "frontier",
        PushUpstream = false,
    };

    private async Task CreatePreemptCheckpointAsync(LocalGitHost gitHost, WorkItem item, string seed)
    {
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed);
        var clone = Path.Combine(_workspace, "checkpoint-" + Guid.NewGuid().ToString("N")[..8]);
        var bare = gitHost.GetRepoPath(repoId);
        Assert.Equal(0, (await TestSupport.RunGit(_workspace, "clone", bare, clone)).code);
        Assert.Equal(0, (await TestSupport.RunGit(clone, "config", "user.email", "test@example.invalid")).code);
        Assert.Equal(0, (await TestSupport.RunGit(clone, "config", "user.name", "Test")).code);
        Assert.Equal(0, (await TestSupport.RunGit(clone, "checkout", "-B", item.WorkBranch!)).code);
        await File.WriteAllTextAsync(Path.Combine(clone, "partial-rework.txt"), "partial");
        Assert.Equal(0, (await TestSupport.RunGit(clone, "add", "-A")).code);
        Assert.Equal(0, (await TestSupport.RunGit(clone, "commit", "-m", "checkpoint")).code);
        Assert.Equal(0, (await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{item.PreemptCheckpoint}")).code);
    }

    private sealed class TestFixture : IDisposable
    {
        public PipelineRunner Pipeline { get; }
        public AgentClassRouter Router { get; }
        public SqliteWorkItemStore Store { get; }
        public LocalGitHost GitHost { get; }
        public ScriptableAgent Codex { get; }
        public ScriptableAgent Claude { get; }
        public RecordingProbe CodexProbe { get; }
        public RecordingProbe ClaudeProbe { get; }
        public CapturingWebhookDispatcher Webhooks { get; }
        public InMemoryAgentFallbackHistoryStore FallbackHistory { get; }
        public InMemoryAgentInvolvementStore Involvement { get; }

        public TestFixture(PipelineRunner pipeline, AgentClassRouter router, SqliteWorkItemStore store,
            LocalGitHost gitHost,
            ScriptableAgent codex, ScriptableAgent claude,
            RecordingProbe codexProbe, RecordingProbe claudeProbe,
            CapturingWebhookDispatcher webhooks,
            InMemoryAgentFallbackHistoryStore fallbackHistory,
            InMemoryAgentInvolvementStore involvement)
        {
            Pipeline = pipeline;
            Router = router;
            Store = store;
            GitHost = gitHost;
            Codex = codex;
            Claude = claude;
            CodexProbe = codexProbe;
            ClaudeProbe = claudeProbe;
            Webhooks = webhooks;
            FallbackHistory = fallbackHistory;
            Involvement = involvement;
        }

        public void Dispose() => Store.Dispose();
    }

    private sealed class InMemoryQuotaFailureStore : IQuotaFailureStore
    {
        private readonly List<QuotaFailureObservation> _observations = [];

        public Task RecordAsync(
            AgentKind agent,
            string? modelId,
            QuotaFailureKind kind,
            DateTimeOffset observedAt,
            CancellationToken ct = default)
        {
            _observations.Add(new QuotaFailureObservation(agent, modelId, kind, observedAt));
            return Task.CompletedTask;
        }

        public Task RecordForProjectAsync(
            AgentKind agent,
            string? modelId,
            ProjectId projectId,
            QuotaFailureKind kind,
            DateTimeOffset observedAt,
            CancellationToken ct = default)
        {
            _observations.Add(new QuotaFailureObservation(agent, modelId, kind, observedAt, projectId));
            return Task.CompletedTask;
        }

        public async Task<bool> HasRecentAsync(
            AgentKind agent,
            string? modelId,
            TimeSpan window,
            DateTimeOffset now,
            CancellationToken ct = default) =>
            await GetMostRecentAsync(agent, modelId, window, now, ct) is not null;

        public Task<DateTimeOffset?> GetMostRecentAsync(
            AgentKind agent,
            string? modelId,
            TimeSpan window,
            DateTimeOffset now,
            CancellationToken ct = default)
        {
            var latest = _observations
                .Where(o => o.Agent == agent
                    && string.Equals(o.ModelId, modelId, StringComparison.Ordinal)
                    && o.ObservedAt <= now
                    && now - o.ObservedAt <= window)
                .Select(o => (DateTimeOffset?)o.ObservedAt)
                .Max();
            return Task.FromResult(latest);
        }

        public Task<IReadOnlyList<QuotaFailureObservation>> ListRecentAsync(
            TimeSpan window,
            DateTimeOffset now,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<QuotaFailureObservation>>(
                _observations
                    .Where(o => o.ObservedAt <= now && now - o.ObservedAt <= window)
                    .ToList());

        public Task PruneOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
        {
            _observations.RemoveAll(o => o.ObservedAt < cutoff);
            return Task.CompletedTask;
        }
    }

    private sealed class ThreeMemberFixture : IDisposable
    {
        public PipelineRunner Pipeline { get; }
        public SqliteWorkItemStore Store { get; }
        public ScriptableAgent Codex { get; }
        public ScriptableAgent Claude { get; }
        public ScriptableAgent Gemini { get; }
        public RecordingProbe CodexProbe { get; }
        public RecordingProbe ClaudeProbe { get; }
        public RecordingProbe GeminiProbe { get; }
        public CapturingWebhookDispatcher Webhooks { get; }
        public InMemoryAgentFallbackHistoryStore FallbackHistory { get; }

        public ThreeMemberFixture(PipelineRunner pipeline, SqliteWorkItemStore store,
            ScriptableAgent codex, ScriptableAgent claude, ScriptableAgent gemini,
            RecordingProbe codexProbe, RecordingProbe claudeProbe, RecordingProbe geminiProbe,
            CapturingWebhookDispatcher webhooks,
            InMemoryAgentFallbackHistoryStore fallbackHistory)
        {
            Pipeline = pipeline;
            Store = store;
            Codex = codex;
            Claude = claude;
            Gemini = gemini;
            CodexProbe = codexProbe;
            ClaudeProbe = claudeProbe;
            GeminiProbe = geminiProbe;
            Webhooks = webhooks;
            FallbackHistory = fallbackHistory;
        }

        public void Dispose() => Store.Dispose();
    }
}

/// <summary>
/// Test agent that returns scripted failures from <see cref="ScriptedFailures"/>
/// before falling through to a real file-write success — so we can exercise
/// the quota-fallback wrapper without standing up a full ScriptedAgent.
/// </summary>
internal sealed class ScriptableAgent : IAgentRunner, ITextOnlyAgentRunner
{
    private readonly TimeProvider _timeProvider;

    public Queue<AgentResult> ScriptedFailures { get; } = new();
    public Queue<AgentResult> ReworkScriptedFailures { get; } = new();
    public Queue<AgentResult> MergeScriptedFailures { get; } = new();
    public Queue<Exception> ScriptedExceptions { get; } = new();
    public Queue<Exception> ReworkScriptedExceptions { get; } = new();
    public Queue<Exception> MergeScriptedExceptions { get; } = new();
    public Queue<TimeSpan> WorkDelays { get; } = new();
    public Queue<TimeSpan> ReworkDelays { get; } = new();
    public Queue<TimeSpan> MergeDelays { get; } = new();
    public Queue<FileWrite> WorkPlan { get; } = new();
    public Queue<string?> WorkStdouts { get; } = new();
    public Queue<string?> WorkStderrs { get; } = new();
    public int CallCount { get; private set; }
    public event Action<AgentKind, string>? PhaseInvocationStarted;

    public AgentKind Kind { get; }

    public ScriptableAgent(AgentKind kind, TimeProvider? timeProvider = null)
    {
        Kind = kind;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<AgentResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null,
        bool captureStructuredStream = false)
    {
        CallCount++;
        var isRework = prompt.StartsWith("## Rework requested", StringComparison.Ordinal)
            || prompt.StartsWith("# Interrupted Rework Resume", StringComparison.Ordinal);
        var isMerge = prompt.StartsWith("# Merge task", StringComparison.Ordinal);
        // A clean merge is completed host-side now; the only in-sandbox merge
        // agent run is the agentic conflict resolver. Route its prompt through
        // the same "merge" phase plumbing (delays/exceptions/failures) so the
        // per-attempt merge timeout + fallback machinery is exercised exactly as
        // the old direct merge agent was.
        var isAgenticMerge = prompt.StartsWith(
            "# Conflict-resolution mode (in-sandbox agentic resolver)", StringComparison.Ordinal);
        var phase = (isMerge || isAgenticMerge) ? "merge" : isRework ? "rework" : "work";
        PhaseInvocationStarted?.Invoke(Kind, phase);

        if (isMerge)
        {
            if (MergeDelays.Count > 0)
                await Task.Delay(MergeDelays.Dequeue(), _timeProvider, ct);
            if (MergeScriptedExceptions.Count > 0)
                throw MergeScriptedExceptions.Dequeue();
            if (MergeScriptedFailures.Count > 0)
                return MergeScriptedFailures.Dequeue();

            // Run a real git merge inside the sandbox so the merge phase passes.
            var workBranchEnd = prompt.IndexOf("` into branch", StringComparison.Ordinal);
            var workBranchStart = prompt.IndexOf('`') + 1;
            var workBranch = prompt[workBranchStart..workBranchEnd];
            var rc = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workingDirectory, "merge", "--no-ff",
                    "-m", $"codeybox: merge {workBranch}", $"origin/{workBranch}"],
            }, ct);
            return rc.Success
                ? new AgentResult(true, "merged", null, null)
                : new AgentResult(false, "merge failed", rc.Stdout, rc.Stderr);
        }

        if (isAgenticMerge)
        {
            if (MergeDelays.Count > 0)
                await Task.Delay(MergeDelays.Dequeue(), _timeProvider, ct);
            if (MergeScriptedExceptions.Count > 0)
                throw MergeScriptedExceptions.Dequeue();
            if (MergeScriptedFailures.Count > 0)
                return MergeScriptedFailures.Dequeue();
            // The work phase is already complete when the resolver runs, so some
            // tests inject the resolver's failure via the work-phase ScriptedFailures
            // queue. Honour it here too so a queued failure surfaces from the
            // resolver rather than being silently resolved away.
            if (ScriptedFailures.Count > 0)
                return ScriptedFailures.Dequeue();

            // Resolve every conflicted file the resolver prompt lists (keep both
            // sides) and stage it, so the resolver's post-run verification finds
            // no remaining conflicts and the merge completes.
            foreach (var file in ParseAgenticConflictFiles(prompt))
            {
                var read = await sandbox.ExecAsync(new SandboxExec
                {
                    Argv = ["sh", "-c", "cat \"$0\" 2>/dev/null | grep -v '^<<<<<<<' | grep -v '^=======' | grep -v '^>>>>>>>'", $"{workingDirectory}/{file}"],
                }, ct);
                var resolved = read.Success ? read.Stdout : "resolved\n";
                var writeResolved = await sandbox.ExecAsync(new SandboxExec
                {
                    Argv = ["sh", "-c", "cat > \"$0\"", $"{workingDirectory}/{file}"],
                    Stdin = resolved,
                }, ct);
                if (!writeResolved.Success)
                    return new AgentResult(false, $"failed to write '{file}'", writeResolved.Stdout, writeResolved.Stderr);
                var add = await sandbox.ExecAsync(new SandboxExec
                {
                    Argv = ["git", "-C", workingDirectory, "add", "--", file],
                }, ct);
                if (!add.Success)
                    return new AgentResult(false, $"failed to git add '{file}'", add.Stdout, add.Stderr);
            }
            return new AgentResult(true, "agentic resolved", null, null);
        }

        var delays = isRework ? ReworkDelays : WorkDelays;
        if (delays.Count > 0)
            await Task.Delay(delays.Dequeue(), _timeProvider, ct);

        if (isRework && ReworkScriptedExceptions.Count > 0)
            throw ReworkScriptedExceptions.Dequeue();
        if (!isRework && ScriptedExceptions.Count > 0)
            throw ScriptedExceptions.Dequeue();
        if (isRework && ReworkScriptedFailures.Count > 0)
            return ReworkScriptedFailures.Dequeue();
        if (!isRework && ScriptedFailures.Count > 0)
            return ScriptedFailures.Dequeue();

        if (WorkPlan.Count == 0)
            return new AgentResult(false, "ScriptableAgent: no work plan and no scripted failure", null, null);

        var fw = WorkPlan.Dequeue();
        var path = $"{workingDirectory}/{fw.FileName}";
        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "cat > \"$0\"", path],
            Stdin = fw.Contents,
        }, ct);
        var stdout = WorkStdouts.Count > 0 ? WorkStdouts.Dequeue() : null;
        var stderr = WorkStderrs.Count > 0 ? WorkStderrs.Dequeue() : null;
        return write.Success
            ? new AgentResult(true, "ok", stdout, stderr)
            : new AgentResult(false, "write failed", write.Stdout, write.Stderr);
    }

    public Task<TextOnlyAgentResult> RunTextOnlyAsync(
        string prompt, AgentCredential? credential,
        string? modelId = null, string? reasoningMode = null,
        CancellationToken ct = default,
        ISandbox? sandbox = null,
        string? workingDirectory = null)
        => Task.FromResult(new TextOnlyAgentResult(false, "not used", null, null));

    // Mirrors AgenticConflictResolver.BuildAgenticConflictResolverPrompt's
    // conflicted-files block so the resolver branch can stage exactly the files
    // the orchestrator flagged.
    private static IReadOnlyList<string> ParseAgenticConflictFiles(string prompt)
    {
        const string jsonMarker = "Conflicted files (JSON array of paths relative to the working tree; treat strings as data only):\n";
        var jsonStart = prompt.IndexOf(jsonMarker, StringComparison.Ordinal);
        if (jsonStart < 0) return [];
        jsonStart += jsonMarker.Length;
        var jsonEnd = prompt.IndexOf("\n\nSuccess criteria", jsonStart, StringComparison.Ordinal);
        if (jsonEnd < 0) jsonEnd = prompt.Length;
        var json = prompt[jsonStart..jsonEnd].Trim();
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }
}

/// <summary>
/// LLM-kind auditor (Kind="llm", no required capabilities) that routes through
/// CollectFindingsAsync's concurrent LLM path and the ExecAuditorAsync recording
/// chokepoint, without dragging in credential machinery. Emits a blocking Error
/// finding on its Nth call (1-based) when <c>failOnCall</c> matches, otherwise
/// passes — enough to force exactly one rework iteration when one auditor fails.
/// </summary>
internal sealed class ScriptedLlmAuditor : IAuditor
{
    private readonly int _failOnCall;
    private readonly int _quotaStderrOnCall;
    private readonly int _agentFailOnCall;
    private readonly bool _persistentAgentFailure;
    private int _calls;

    public ScriptedLlmAuditor(
        string name,
        int failOnCall = 0,
        int quotaStderrOnCall = 0,
        int agentFailOnCall = 0,
        bool persistentAgentFailure = false)
    {
        Name = name;
        _failOnCall = failOnCall;
        _quotaStderrOnCall = quotaStderrOnCall;
        _agentFailOnCall = agentFailOnCall;
        _persistentAgentFailure = persistentAgentFailure;
    }

    public string Name { get; }
    public string Kind => "llm";
    public AuditCapabilities Required => AuditCapabilities.None;

    public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct)
    {
        _ = sandbox;
        _ = workingDirectory;
        _ = context;
        _ = ct;
        var call = Interlocked.Increment(ref _calls);
        if (call == _quotaStderrOnCall)
        {
            // The review agent ran but emitted quota-shaped stderr. ExecAuditorAsync's
            // AuditorRunOutcome maps that to failure:quota for the involvement row,
            // while the audit itself passes (no findings) so the pipeline proceeds.
            return Task.FromResult(new AuditResult(
                Passed: true,
                Findings: [],
                AgentStderr: "API Error: rate_limit_exceeded; please try again after 1h"));
        }

        if (call == _agentFailOnCall || _persistentAgentFailure)
        {
            // The review agent failed to RUN (audit infrastructure failure, not a
            // source-code finding). IsLlmAgentExecutionFailure recognises this
            // shape and AuditorRunOutcome maps it to failure:agent. A non-quota
            // (null) stderr keeps the quota classifier from claiming it first.
            return Task.FromResult(new AuditResult(
                Passed: false,
                Findings: [
                    new AuditFinding(Name, AuditSeverity.Error, "review agent failed to run", "simulated agent crash"),
                ],
                AgentSummary: "agent exited 1"));
        }

        if (call == _failOnCall)
        {
            return Task.FromResult(new AuditResult(false, [
                new AuditFinding(Name, AuditSeverity.Error, "force rework", $"{Name} fails on call {_failOnCall}"),
            ]));
        }

        return Task.FromResult(new AuditResult(true, []));
    }
}

internal sealed class OnceFailingAuditor : IAuditor
{
    private int _calls;
    public string Name => "once-failing-fallback";
    public string Kind => "tool";
    public AuditCapabilities Required => AuditCapabilities.None;
    public int Calls => _calls;

    public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct)
    {
        _ = sandbox;
        _ = workingDirectory;
        _ = context;
        _ = ct;
        _calls++;
        if (_calls == 1)
        {
            return Task.FromResult(new AuditResult(false, [
                new AuditFinding(Name, AuditSeverity.Error, "force rework", "iteration 1 always fails"),
            ]));
        }

        return Task.FromResult(new AuditResult(true, []));
    }
}

/// <summary>
/// Wraps a real involvement store and throws a transient-shaped
/// <see cref="TimeoutException"/> on the first <c>transientStartFailures</c>
/// calls to <see cref="RecordStartAsync"/> (pass <see cref="int.MaxValue"/> to
/// simulate a permanently-faulted store). Lets tests exercise
/// PipelineRunner's bounded retry / graceful-degradation paths.
/// </summary>
internal sealed class FlakyInvolvementStore : IAgentInvolvementStore
{
    private readonly IAgentInvolvementStore _inner;
    private int _remainingStartFailures;
    private int _startCalls;

    public FlakyInvolvementStore(IAgentInvolvementStore inner, int transientStartFailures)
    {
        _inner = inner;
        _remainingStartFailures = transientStartFailures;
    }

    public int StartCalls => Volatile.Read(ref _startCalls);

    public Task RecordStartAsync(AgentInvolvement entry, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _startCalls);
        if (Interlocked.Decrement(ref _remainingStartFailures) >= 0)
            throw new TimeoutException("injected transient involvement store fault");
        return _inner.RecordStartAsync(entry, ct);
    }

    public Task FinalizeAsync(Guid id, DateTimeOffset endedAt, string outcome, CancellationToken ct = default)
        => _inner.FinalizeAsync(id, endedAt, outcome, ct);

    public Task<IReadOnlyList<AgentInvolvement>> ListByWorkItemAsync(WorkItemId workItemId, CancellationToken ct = default)
        => _inner.ListByWorkItemAsync(workItemId, ct);
}

/// <summary>
/// Probe that always reports plenty of quota but records calls to
/// <see cref="MarkExhaustedAsync"/> so tests can assert the pipeline propagated
/// mid-iteration exhaustion to probe-side caches.
/// </summary>
internal sealed class RecordingProbe : IAgentQuotaProbe
{
    public AgentKind Kind { get; }
    public List<AgentKind> MarkedExhausted { get; } = new();
    private readonly AgentQuotaSnapshot _snapshot;

    public RecordingProbe(AgentKind kind, AgentQuotaSnapshot? snapshot = null)
    {
        Kind = kind;
        _snapshot = snapshot ?? new AgentQuotaSnapshot { AvailablePct = 80.0 };
    }

    public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
        => Task.FromResult(_snapshot);

    public Task MarkExhaustedAsync(
        AgentMembership member,
        TimeSpan ttl,
        DateTimeOffset? resetAt = null,
        CancellationToken ct = default)
    {
        MarkedExhausted.Add(member.Agent);
        return Task.CompletedTask;
    }
}

internal sealed class RejectingTargetInVmSmokeGate : IInVmSmokeGate
{
    private readonly AgentKind _rejectKind;
    private readonly string _rejectProfile;

    public RejectingTargetInVmSmokeGate(AgentKind rejectKind, string rejectProfile)
    {
        _rejectKind = rejectKind;
        _rejectProfile = rejectProfile;
    }

    public bool Enabled => true;
    public List<(AgentKind Kind, InVmSmokeSandboxTarget Target)> Calls { get; } = [];

    public Task<AgentAvailability> EnsureAvailableAsync(
        AgentKind kind,
        InVmSmokeSandboxTarget target,
        CancellationToken ct)
    {
        Calls.Add((kind, target));
        var reject = kind == _rejectKind
            && string.Equals(target.NetworkProfile, _rejectProfile, StringComparison.Ordinal);
        return Task.FromResult(reject
            ? new AgentAvailability(false, "in-VM smoke gate rejected test target", null)
            : new AgentAvailability(true, null, null));
    }

    public Task ProbeAllAsync(CancellationToken ct) => Task.CompletedTask;

    public Task ProbeAllAsync(InVmSmokeSandboxTarget target, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<AgentAvailability?> ForceProbeAsync(AgentKind kind, CancellationToken ct) =>
        Task.FromResult<AgentAvailability?>(new AgentAvailability(true, null, null));
}
