using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Cursor;
using CodeyBox.Agents.Gemini;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Pipeline-level wiring tests for the fast-fail circuit breaker. The unit
/// tests in <see cref="AgentAvailabilityRegistryTests"/> only cover the
/// registry math; they do NOT exercise the two PipelineRunner call sites that
/// actually feed the registry (work-phase finish at PipelineRunner.cs:1542
/// and merge-phase finish at PipelineRunner.cs:3385). The cb-216a2230 bug
/// report's acceptance criterion 4 explicitly asks for an end-to-end test
/// ("stub a runner that succeeds on smoke but fails on every real call. After
/// 3 fast-fails the agent is excluded"), so this file dispatches work items
/// through <see cref="PipelineRunner.RunAsync"/> with a real registry wired
/// in. A regression that drops either RecordRunOutcome call site (or wires
/// the wrong stopwatch into the duration argument) would silently bring the
/// cascade back; these tests are the trap for that.
/// </summary>
[Collection("Pipeline integration")]
public sealed class PipelineRunnerAvailabilityWiringTests : IDisposable
{
    private readonly string _workspace;

    public PipelineRunnerAvailabilityWiringTests() =>
        _workspace = Directory.CreateTempSubdirectory("codeybox-availwiring-").FullName;

    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    [Theory]
    [InlineData("agent exited 127", "env: 'codex': No such file or directory")]
    [InlineData("agent exited 1", "bwrap: execvp codex: No such file or directory")]
    [InlineData("failed to materialise codex auth: exit 1", "permission denied")]
    public async Task InfrastructureWorkFailures_DoNotFeedFastFailBreaker(string summary, string stderr)
    {
        // Exit 127 / missing binary and runner prerequisite materialisation
        // failures are sandbox/provisioning defects. They should fail the item
        // and emit an infra audit signal, but they must not increment the
        // agent fast-fail counter or bench the agent.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);

        for (var i = 0; i < 3; i++)
        {
            fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
                Success: false,
                Summary: summary,
                Stdout: null,
                Stderr: stderr));
        }

        for (var i = 0; i < 3; i++)
        {
            var item = NewItem(AgentKind.Codex);
            await fix.Store.CreateAsync(item);
            await fix.Pipeline.RunAsync(item, CancellationToken.None);
            // Each item terminates in Failed (non-quota failure on the single
            // configured agent — useClassRouter=false so there is no fallback
            // target to consume the slot).
            var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
            Assert.Equal(WorkItemState.Failed, final!.State);
        }

        var availability = fix.Registry.GetAvailability(AgentKind.Codex);
        Assert.True(availability.Available);

        var snap = fix.Registry.Snapshot().SingleOrDefault(s => s.Agent == AgentKind.Codex);
        Assert.True(snap is null || snap.ConsecutiveFastFails == 0);
        Assert.True(snap is null || snap.LastFastFailAt is null);
        Assert.DoesNotContain(fix.Webhooks.Events, e => e.Event == "agent.smoke_failed");
    }

    [Fact]
    public async Task TransientNetworkWorkFailures_DoNotFeedFastFailBreaker()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);

        for (var i = 0; i < 3; i++)
        {
            fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
                Success: false,
                Summary: "agent transport failed",
                Stdout: null,
                Stderr: "request timed out while reading agent stream"));
        }

        for (var i = 0; i < 3; i++)
        {
            var item = NewItem(AgentKind.Codex);
            await fix.Store.CreateAsync(item);
            await fix.Pipeline.RunAsync(item, CancellationToken.None);
            var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
            Assert.Equal(WorkItemState.WaitingForTransientRetry, final!.State);
            Assert.Equal("transient", final.FailureKind);
        }

        var availability = fix.Registry.GetAvailability(AgentKind.Codex);
        Assert.True(availability.Available);

        var snap = fix.Registry.Snapshot().SingleOrDefault(s => s.Agent == AgentKind.Codex);
        Assert.True(snap is null || snap.ConsecutiveFastFails == 0);
        Assert.True(snap is null || snap.LastFastFailAt is null);
        Assert.DoesNotContain(fix.Webhooks.Events, e => e.Event == "agent.smoke_failed");
    }

    [Fact]
    public async Task InfrastructureFailure_PreservesExistingFastFailCount_AndStillTripsOnNextGenuineFail()
    {
        // Inverse of the zero-counter case above: prove the infra filter is
        // genuinely a NO-OP on the registry, not a "record-as-success" reset.
        // Seed two genuine fast-fails (counter=2, threshold=3). Run one infra
        // failure. Then run one more genuine fast-fail and verify the breaker
        // trips — proving the prior counter survived intact.
        //
        // A regression that recorded infra as success would zero the counter
        // and the breaker would never trip on the third real crash. A
        // regression that recorded infra as a slow non-zero run would bump
        // the counter but reset LastFastFailAt and break the time-window
        // logic; this test pins the counter directly.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);

        // Seed two genuine fast-fails directly into the registry.
        for (var i = 0; i < 2; i++)
            fix.Registry.RecordRunOutcome(
                AgentKind.Codex,
                success: false,
                duration: TimeSpan.FromMilliseconds(500));

        var beforeSnap = fix.Registry.Snapshot().Single(s => s.Agent == AgentKind.Codex);
        Assert.Equal(2, beforeSnap.ConsecutiveFastFails);
        Assert.True(fix.Registry.GetAvailability(AgentKind.Codex).Available);

        // Run an infra-shaped failure through the pipeline.
        fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 127",
            Stdout: null,
            Stderr: "env: 'codex': No such file or directory"));
        var infraItem = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(infraItem);
        await fix.Pipeline.RunAsync(infraItem, CancellationToken.None);

        // Counter is unchanged — the filter was a no-op, not a reset.
        var midSnap = fix.Registry.Snapshot().Single(s => s.Agent == AgentKind.Codex);
        Assert.Equal(2, midSnap.ConsecutiveFastFails);
        Assert.True(fix.Registry.GetAvailability(AgentKind.Codex).Available);
        Assert.DoesNotContain(fix.Webhooks.Events, e => e.Event == "agent.smoke_failed");

        // One more genuine fast-fail must trip the breaker (2 + 1 == threshold).
        fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 2",
            Stdout: null,
            Stderr: "panic: fatal agent runtime crash"));
        var crashItem = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(crashItem);
        await fix.Pipeline.RunAsync(crashItem, CancellationToken.None);

        var availability = fix.Registry.GetAvailability(AgentKind.Codex);
        Assert.False(availability.Available);
        Assert.Contains("fast-fail circuit breaker", availability.Reason);
        var transition = Assert.Single(fix.Webhooks.Events, e => e.Event == "agent.smoke_failed");
        var details = Assert.IsType<AgentSmokeFailedDetails>(transition.Details);
        Assert.Equal("codex", details.AgentKind);
    }

    [Fact]
    public async Task ThreeConsecutiveFastAgentCrashes_ExcludeAgent_AndPublishOneTransitionWebhook()
    {
        // Stand up an availability registry with the production defaults and
        // wire it into the pipeline. A crash-shaped non-quota, non-infra
        // sub-threshold failure must still trip the breaker after 3 separate
        // work items.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);

        for (var i = 0; i < 3; i++)
        {
            fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
                Success: false,
                Summary: "agent exited 2",
                Stdout: null,
                Stderr: "panic: fatal agent runtime crash"));
        }

        for (var i = 0; i < 3; i++)
        {
            var item = NewItem(AgentKind.Codex);
            await fix.Store.CreateAsync(item);
            await fix.Pipeline.RunAsync(item, CancellationToken.None);
            var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
            Assert.Equal(WorkItemState.Failed, final!.State);
        }

        var availability = fix.Registry.GetAvailability(AgentKind.Codex);
        Assert.False(availability.Available);
        Assert.Contains("fast-fail circuit breaker", availability.Reason);

        var transitionEvents = fix.Webhooks.Events
            .Where(e => e.Event == "agent.smoke_failed")
            .ToList();
        Assert.Single(transitionEvents);
        var details = Assert.IsType<AgentSmokeFailedDetails>(transitionEvents[0].Details);
        Assert.Equal("codex", details.AgentKind);
        Assert.Contains("fast-fail circuit breaker", details.Reason);
        // The work-phase fast-fail call site hard-codes Category=Persistent
        // (PipelineRunner.cs ~2185): the binary launched, exited non-zero fast,
        // and did so repeatedly — retrying without operator intervention will
        // keep failing. A regression that forgot to set Category, or copied
        // the wrong constant, would silently default back to Unknown and the
        // operator-alert routing on persistent failures would not fire.
        Assert.Equal(SmokeFailureCategory.Persistent, details.Category);
    }

    [Fact]
    public async Task SingleFastFail_DoesNotExclude_AndPublishesNoTransitionWebhook()
    {
        // Inverse-contrast: one fast-fail must NOT exclude the agent (the
        // breaker threshold is 3). Without this guard a regression that
        // shortens the threshold would silently page the operator on every
        // transient agent error.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);

        fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 2",
            Stdout: null,
            Stderr: "panic: fatal agent runtime crash"));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.True(fix.Registry.GetAvailability(AgentKind.Codex).Available);
        Assert.DoesNotContain(fix.Webhooks.Events, e => e.Event == "agent.smoke_failed");

        // The registry must still have RECORDED the fast-fail (counter=1) —
        // we infer that via the snapshot, since a regression that disabled
        // RecordRunOutcome entirely would leave the counter at 0 and the
        // breaker would never trip on item 3.
        var snap = fix.Registry.Snapshot().Single(s => s.Agent == AgentKind.Codex);
        Assert.Equal(1, snap.ConsecutiveFastFails);
        Assert.NotNull(snap.LastFastFailAt);
    }

    // ── No-changes breaker (silent-failure backstop) ─────────────────────────
    // The fast-fail breaker above only counts NON-ZERO exits. A silently-broken
    // agent — auth collapse, capability collapse, or a failure mode whose
    // signature we don't recognise yet — exits 0 but leaves the working tree
    // unchanged. These tests pin the wiring that catches that pattern: the
    // pipeline must call IAgentAvailabilityRegistry.RecordNoChangesOutcome at
    // the "Agent produced no changes to commit" throw site, fire an
    // agent.smoke_failed webhook on the trip transition, and call
    // RecordChangesProduced on the success path so an isolated no-change does
    // not trip the breaker.

    [Fact]
    public async Task ThreeConsecutiveDistinctNoChanges_ExcludeAgent_AndPublishOneTransitionWebhook()
    {
        // Headline: a clean-exit agent that produces no diff on 3 distinct
        // work items in a row is excluded by the no-changes breaker. Exactly
        // one agent.smoke_failed webhook fires (the trip transition).
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);

        for (var i = 0; i < 3; i++)
        {
            fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
                Success: true, Summary: "ok", Stdout: null, Stderr: null));
        }

        for (var i = 0; i < 3; i++)
        {
            var item = NewItem(AgentKind.Codex);
            await fix.Store.CreateAsync(item);
            await fix.Pipeline.RunAsync(item, CancellationToken.None);
            var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
            Assert.Equal(WorkItemState.Failed, final!.State);
        }

        var availability = fix.Registry.GetAvailability(AgentKind.Codex);
        Assert.False(availability.Available);
        Assert.Contains("no-changes circuit breaker", availability.Reason);

        var transitionEvents = fix.Webhooks.Events
            .Where(e => e.Event == "agent.smoke_failed")
            .ToList();
        Assert.Single(transitionEvents);
        var details = Assert.IsType<AgentSmokeFailedDetails>(transitionEvents[0].Details);
        Assert.Equal("codex", details.AgentKind);
        Assert.Contains("no-changes circuit breaker", details.Reason);
        // Persistent: a silent-failure agent will keep producing empty diffs
        // until the operator intervenes — Unknown / Transient would mis-route
        // the alert as recoverable noise.
        Assert.Equal(SmokeFailureCategory.Persistent, details.Category);
    }

    [Fact]
    public async Task SingleNoChanges_DoesNotExcludeAgent_AndPublishesNoTransitionWebhook()
    {
        // One no-changes outcome must not fire the alert (threshold is 3).
        // Without this guard a regression that wired the trip predicate as
        // `>= 1` would page the operator on every empty diff.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);

        fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
            Success: true, Summary: "ok", Stdout: null, Stderr: null));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.True(fix.Registry.GetAvailability(AgentKind.Codex).Available);
        Assert.DoesNotContain(fix.Webhooks.Events, e => e.Event == "agent.smoke_failed");

        var snap = fix.Registry.Snapshot().Single(s => s.Agent == AgentKind.Codex);
        Assert.Equal(1, snap.ConsecutiveNoChanges);
        Assert.NotNull(snap.LastNoChangesAt);
        // Orthogonal to fast-fails: a clean-exit no-change must NOT inflate
        // the fast-fail counter or it would race ahead of its own breaker.
        Assert.Equal(0, snap.ConsecutiveFastFails);
    }

    [Fact]
    public async Task NoChangesInterleavedWithRealCommit_ResetsBreakerFromPipeline()
    {
        // The success-path wiring (RecordChangesProduced after HEAD advances)
        // must reset the no-changes streak. Sequence: no-change × 2, then a
        // real-work item that commits → counter back to 0 → next no-change
        // is only 1, well below threshold. A regression that omitted the
        // RecordChangesProduced call would leave the counter at 2 and the
        // next no-change would trip the breaker.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);

        // Queue and dispatch in three batches because ScriptedFailures takes
        // precedence over WorkPlan in the scripted runner — interleaving by
        // enqueueing the WorkPlan entry between the failure entries would
        // still dequeue all ScriptedFailures first.

        // Batch 1: two no-change runs (streak → 2).
        for (var i = 0; i < 2; i++)
        {
            fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
                Success: true, Summary: "ok", Stdout: null, Stderr: null));
            var item = NewItem(AgentKind.Codex);
            await fix.Store.CreateAsync(item);
            await fix.Pipeline.RunAsync(item, CancellationToken.None);
        }

        // Batch 2: one real-work run (streak → 0).
        fix.Codex.WorkPlan.Enqueue(new FileWrite("ok.txt", "v1"));
        var success = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(success);
        await fix.Pipeline.RunAsync(success, CancellationToken.None);

        // Batch 3: two more no-change runs (streak → 2).
        for (var i = 0; i < 2; i++)
        {
            fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
                Success: true, Summary: "ok", Stdout: null, Stderr: null));
            var item = NewItem(AgentKind.Codex);
            await fix.Store.CreateAsync(item);
            await fix.Pipeline.RunAsync(item, CancellationToken.None);
        }

        // After the interleaved sequence the trailing streak is 2, still
        // under the threshold of 3 — proving the success-path reset wired.
        Assert.True(fix.Registry.GetAvailability(AgentKind.Codex).Available);
        Assert.DoesNotContain(fix.Webhooks.Events, e => e.Event == "agent.smoke_failed");
        var snap = fix.Registry.Snapshot().Single(s => s.Agent == AgentKind.Codex);
        Assert.Equal(2, snap.ConsecutiveNoChanges);
    }

    [Fact]
    public async Task NoChangesBreakerTripped_ResetEndpointPathRestoresAvailability()
    {
        // Recovery contract: the breaker is never permanent. After the
        // operator hits /admin/agent/{name}/reset (which delegates to
        // IAgentAvailabilityReset.Reset → AgentAvailabilityRegistry.Reset),
        // the agent is routable again and the streak counter starts fresh.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);

        for (var i = 0; i < 3; i++)
        {
            fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
                Success: true, Summary: "ok", Stdout: null, Stderr: null));
        }

        for (var i = 0; i < 3; i++)
        {
            var item = NewItem(AgentKind.Codex);
            await fix.Store.CreateAsync(item);
            await fix.Pipeline.RunAsync(item, CancellationToken.None);
        }

        Assert.False(fix.Registry.GetAvailability(AgentKind.Codex).Available);

        fix.Registry.Reset(AgentKind.Codex);

        Assert.True(fix.Registry.GetAvailability(AgentKind.Codex).Available);
        var snap = fix.Registry.Snapshot().SingleOrDefault(s => s.Agent == AgentKind.Codex);
        Assert.True(snap is null || snap.ConsecutiveNoChanges == 0);
    }

    [Fact]
    public async Task SuccessfulNoDiffRun_WithCapturedStdoutAuthPrompt_FailsItemWithoutGlobalBench_WhenNotCorroborated()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);
        var transcript = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Auth", "agy-login-prompt.redacted.txt"));

        fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
            Success: true,
            Summary: "ok",
            Stdout: transcript,
            Stderr: null));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("auth required from agent output", final.LastError);
        Assert.Contains("not globally benched", final.LastError);
        Assert.Equal("infrastructure", final.FailureKind);

        var availability = fix.Registry.GetAvailability(AgentKind.Codex);
        Assert.True(availability.Available);

        Assert.DoesNotContain(fix.Webhooks.Events, e => e.Event == "agent.smoke_failed");
    }

    [Fact]
    public async Task FailedWorkRun_WithAuthLoginPrompt_ExcludesAgent_AndPublishesPersistentAlert()
    {
        // The work-phase fix scans BOTH the Success=true (exit 0, no diff) and
        // Success=false (non-zero exit) outputs for a login prompt. Without
        // this case, a regression that only kept the Success=true call would
        // still pass the pre-existing test but silently regress the more
        // common failure shape: a nonzero exit whose stderr/stdout contains
        // the captured login transcript.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);
        var transcript = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Auth", "agy-login-prompt.redacted.txt"));

        fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 1",
            Stdout: null,
            Stderr: transcript));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("auth required from agent output", final.LastError);
        Assert.Equal("infrastructure", final.FailureKind);

        var availability = fix.Registry.GetAvailability(AgentKind.Codex);
        Assert.False(availability.Available);
        Assert.Contains("auth required from agent output", availability.Reason);

        var failed = Assert.Single(fix.Webhooks.Events, e => e.Event == "agent.smoke_failed");
        var details = Assert.IsType<AgentSmokeFailedDetails>(failed.Details);
        Assert.Equal("codex", details.AgentKind);
        Assert.Equal(SmokeFailureCategory.Persistent, details.Category);
    }

    [Fact]
    public async Task FailedWorkRun_WithAuthLoginPromptAnd401_IsAuthRequired_NotQuotaParked()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);

        fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 1",
            Stdout: """
                Authentication required. Please visit the URL to log in:
                Waiting for authentication (timeout 30s)...
                Error: authentication timed out.
                """,
            Stderr: "API Error: 401 Unauthorized"));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal("infrastructure", final.FailureKind);
        Assert.Null(final.QuotaRetryFrom);
        Assert.Contains("auth required from agent output", final.LastError);

        var availability = fix.Registry.GetAvailability(AgentKind.Codex);
        Assert.False(availability.Available);
        Assert.Contains("auth required from agent output", availability.Reason);
    }

    [Fact]
    public async Task AuthLoginPrompt_SurvivesSmokeDisabled_GateBenchesAgentForNextItem()
    {
        // Master smoke switch OFF. The non-smoke exclusion source
        // (SmokeExclusionSource.AuthRequired) MUST still hold the agent
        // benched — if the auth source were tracked as InVmSmoke/HostSmoke,
        // AgentDispatchAvailability.GetAvailabilityWithoutSmokeGateExclusions
        // would silently ignore it and route the next item to the same
        // unauthenticated CLI. Pin the regression so a future refactor that
        // re-classifies AuthRequired as a smoke source fails this test.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var smokeOptions = new SmokeOptionsSnapshot(new SmokeOptions { Enabled = false });
        using var fix = BuildPipeline(seed, smokeOptions: smokeOptions);
        var transcript = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Auth", "agy-login-prompt.redacted.txt"));

        fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
            Success: true,
            Summary: "ok",
            Stdout: null,
            Stderr: transcript));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        // Even with smoke disabled the dispatch-availability view must
        // report the agent as unavailable.
        var dispatch = new AgentDispatchAvailability(fix.Registry, inVmSmokeGate: null, smokeOptions: smokeOptions);
        var verdict = dispatch.GetAvailability(AgentKind.Codex);
        Assert.NotNull(verdict);
        Assert.False(verdict!.Available);
        Assert.Contains("auth required from agent output", verdict.Reason);
    }

    [Fact]
    public async Task AuthLoginPrompt_PublishesAlertEvenWhenAgentAlreadyExcludedByOtherSource()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var smokeOptions = new SmokeOptionsSnapshot(new SmokeOptions { Enabled = false });
        using var fix = BuildPipeline(seed, smokeOptions: smokeOptions);
        fix.Registry.MarkSmokeResult(
            AgentKind.Codex,
            new AgentSmokeResult(false, "host probe already failed", TimeSpan.Zero, SmokeFailureCategory.Persistent),
            SmokeExclusionSource.HostSmoke);

        var transcript = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Auth", "agy-login-prompt.redacted.txt"));
        fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
            Success: true,
            Summary: "ok",
            Stdout: null,
            Stderr: transcript));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var failed = Assert.Single(fix.Webhooks.Events, e => e.Event == "agent.smoke_failed");
        var details = Assert.IsType<AgentSmokeFailedDetails>(failed.Details);
        Assert.Equal("codex", details.AgentKind);
        Assert.Equal(SmokeFailureCategory.Persistent, details.Category);
        Assert.Contains("auth required from agent output", details.Reason);

        var availability = fix.Registry.GetAvailability(AgentKind.Codex);
        Assert.False(availability.Available);
        Assert.Contains("host probe already failed", availability.Reason);
        Assert.Contains("auth required from agent output", availability.Reason);
    }

    [Fact]
    public async Task MergePhaseDirectAgentRun_WithStderrAuthLoginPrompt_ExcludesAgent_AndPublishesPersistentAlert()
    {
        // The merge-phase fix call site is decoupled from the work-phase call
        // site: a regression that drops EITHER would leave half the outage
        // unmitigated. Pin the merge path independently so a removed/wrong-
        // phase-name detector call there can't pass off the work-phase test
        // as full coverage.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);
        var transcript = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Auth", "agy-login-prompt.redacted.txt"));

        // Work phase succeeds; merge phase returns exit 0 with a captured
        // login prompt — the silent-broken-agent shape from the antigravity
        // outage, but inside the merge call site.
        fix.Codex.WorkPlan.Enqueue(new FileWrite("ok.txt", "v1"));
        fix.Codex.MergeScriptedFailures.Enqueue(new AgentResult(
            Success: true,
            Summary: "ok",
            Stdout: null,
            Stderr: transcript));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("auth required from agent output", final.LastError);
        Assert.Contains("merge", final.LastError);
        Assert.Equal("infrastructure", final.FailureKind);

        var availability = fix.Registry.GetAvailability(AgentKind.Codex);
        Assert.False(availability.Available);
        Assert.Contains("auth required from agent output", availability.Reason);

        var failed = Assert.Single(fix.Webhooks.Events, e => e.Event == "agent.smoke_failed");
        var details = Assert.IsType<AgentSmokeFailedDetails>(failed.Details);
        Assert.Equal("codex", details.AgentKind);
        Assert.Equal(SmokeFailureCategory.Persistent, details.Category);
        // Pin the phase tag so a future refactor that copies the work-phase
        // call into merge without updating the label is caught — operator
        // dashboards need merge vs. work attribution.
        Assert.Contains("merge", details.Reason);
    }

    [Fact]
    public async Task MergePhaseAgenticResolver_WithAuthLoginPrompt_ExcludesAgent_AndPublishesPersistentAlert()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);
        var transcript = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Auth", "agy-login-prompt.redacted.txt"));

        fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
            Success: true,
            Summary: "ok",
            Stdout: null,
            Stderr: transcript));

        var item = NewItem(AgentKind.Codex) with
        {
            State = WorkItemState.AuditPassed,
            WorkBranch = "feature/merge-resolver-auth-" + Guid.NewGuid().ToString("N")[..8],
        };
        var repoId = await fix.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = fix.GitHost.GetRepoPath(repoId);
        await CommitWorkBranchAsync(barePath, item.WorkBranch!, "README.md", "work side\n", "work readme");
        await CommitToSeedAsync(seed, "README.md", "main side\n", "main readme");

        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal("infrastructure", final.FailureKind);
        Assert.Contains("auth required from agent output", final.LastError);
        Assert.Contains("merge-resolver", final.LastError);

        var availability = fix.Registry.GetAvailability(AgentKind.Codex);
        Assert.False(availability.Available);
        Assert.Contains("auth required from agent output", availability.Reason);

        var failed = Assert.Single(fix.Webhooks.Events, e => e.Event == "agent.smoke_failed");
        var details = Assert.IsType<AgentSmokeFailedDetails>(failed.Details);
        Assert.Equal("codex", details.AgentKind);
        Assert.Equal(SmokeFailureCategory.Persistent, details.Category);
        Assert.Contains("merge-resolver", details.Reason);
    }

    [Fact]
    public async Task SuccessfulNoDiffRun_WithoutAuthLoginPrompt_RemainsNormalNoChangesFailure()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);

        fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
            Success: true,
            Summary: "ok",
            Stdout: "No repository changes were necessary.",
            Stderr: null));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("Agent produced no changes to commit", final.LastError);
        Assert.True(fix.Registry.GetAvailability(AgentKind.Codex).Available);
        Assert.DoesNotContain(fix.Webhooks.Events, e => e.Event == "agent.smoke_failed");
    }

    [Fact]
    public async Task StdoutOnlyAuthFragment_WithoutTrustedTranscript_RemainsNormalNoChangesFailure()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);

        fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
            Success: true,
            Summary: "ok",
            Stdout: "Please visit the URL to log in: https://accounts.google.com/o/oauth2/auth?client_id=redacted",
            Stderr: null));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("Agent produced no changes to commit", final.LastError);

        var availability = fix.Registry.GetAvailability(AgentKind.Codex);
        Assert.True(availability.Available);

        Assert.DoesNotContain(fix.Webhooks.Events, e => e.Event == "agent.smoke_failed");
    }

    [Fact]
    public async Task StdoutOnlyAuthPrompt_RequiresInVmCorroboration_ToExcludeAgentAndAlert()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gate = new AuthCorroboratingInVmSmokeGate();
        using var fix = BuildPipeline(seed, inVmSmokeGate: gate);

        fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
            Success: true,
            Summary: "ok",
            Stdout: """
                Authentication required. Please visit the URL to log in:
                https://accounts.google.com/o/oauth2/auth?client_id=redacted
                Waiting for authentication (timeout 30s)...
                Error: authentication timed out.
                """,
            Stderr: null));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal("infrastructure", final.FailureKind);
        Assert.Contains("auth required from agent output", final.LastError);
        Assert.Equal(1, gate.ForceProbeCalls);

        var availability = fix.Registry.GetAvailability(AgentKind.Codex);
        Assert.False(availability.Available);
        Assert.Contains("auth required from agent output", availability.Reason);

        var failed = Assert.Single(fix.Webhooks.Events, e => e.Event == "agent.smoke_failed");
        var details = Assert.IsType<AgentSmokeFailedDetails>(failed.Details);
        Assert.Equal("codex", details.AgentKind);
        Assert.Equal(SmokeFailureCategory.Persistent, details.Category);
    }

    [Fact]
    public async Task SuccessfulWorkRun_ResetsFastFailCounterFromPipeline()
    {
        // Pin the contract that a SUCCESSFUL run also feeds the registry — a
        // future change that conditionally skipped RecordRunOutcome on success
        // would silently let slow-failure → fast-fail → fast-fail → fast-fail
        // exclude an agent that was actually recovering.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);

        fix.Codex.ScriptedFailures.Enqueue(new AgentResult(false, "agent exited 2", null,
            "panic: fatal agent runtime crash"));
        fix.Codex.ScriptedFailures.Enqueue(new AgentResult(false, "agent exited 2", null,
            "panic: fatal agent runtime crash"));
        fix.Codex.WorkPlan.Enqueue(new FileWrite("ok.txt", "v1"));

        // Two items fail fast, third succeeds end-to-end.
        for (var i = 0; i < 2; i++)
        {
            var failing = NewItem(AgentKind.Codex);
            await fix.Store.CreateAsync(failing);
            await fix.Pipeline.RunAsync(failing, CancellationToken.None);
        }
        var succeeding = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(succeeding);
        await fix.Pipeline.RunAsync(succeeding, CancellationToken.None);

        // After the successful run the counter must be 0 — proving the
        // success-side wiring fires.
        var snap = fix.Registry.Snapshot().Single(s => s.Agent == AgentKind.Codex);
        Assert.Equal(0, snap.ConsecutiveFastFails);
        Assert.True(fix.Registry.GetAvailability(AgentKind.Codex).Available);
    }

    [Fact]
    public async Task MergePhaseFastFail_ExcludesAgent_AndPublishesWebhookWithMergeContext()
    {
        // Pin the second RecordRunOutcome call site in PipelineRunner —
        // RunAgentMergePhaseAsync, which fires *after* the work phase has
        // already reset the fast-fail counter to 0 on a successful work-phase
        // exit. A regression that deletes the merge-phase block (or wires it
        // to the wrong stopwatch/event name) would leave the counter at 0
        // because the work-phase reset masks the merge-phase fast-fail.
        //
        // Build a fixture with MaxConsecutiveFastFails=1 so a single merge-
        // phase fast-fail trips the breaker. The work phase succeeds (counter
        // ← 0); then the merge phase returns a non-quota, sub-threshold
        // failure (counter ← 1 = threshold, exclusion + webhook fire).
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed, maxConsecutiveFastFails: 1);

        fix.Codex.WorkPlan.Enqueue(new FileWrite("ok.txt", "v1"));
        fix.Codex.MergeScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 2",
            Stdout: null,
            Stderr: "panic: fatal merge agent runtime crash"));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var availability = fix.Registry.GetAvailability(AgentKind.Codex);
        Assert.False(availability.Available);
        Assert.Contains("fast-fail circuit breaker", availability.Reason);

        // The merge-phase block attaches both WorkItem and Project to the
        // event (the work-phase block does the same). Verify presence — a
        // regression that switched the call site to a payload-less variant
        // would silently drop these.
        var transitions = fix.Webhooks.Events
            .Where(e => e.Event == "agent.smoke_failed")
            .ToList();
        var transition = Assert.Single(transitions);
        var details = Assert.IsType<AgentSmokeFailedDetails>(transition.Details);
        Assert.Equal("codex", details.AgentKind);
        Assert.Contains("fast-fail circuit breaker", details.Reason);
        // Same Category=Persistent pin as the work-phase site above. The two
        // call sites (PipelineRunner.cs ~2185 work-phase, ~5105 merge-phase)
        // each hard-code the constant; drift between them or a copy-paste
        // omission would let only one path raise the persistent alert.
        Assert.Equal(SmokeFailureCategory.Persistent, details.Category);
        Assert.NotNull(transition.WorkItem);
        Assert.Equal(item.Id, transition.WorkItem.Id);
        Assert.NotNull(transition.Project);
        Assert.Equal("test-project", transition.Project.Id.Value);
    }

    [Fact]
    public async Task MergePhaseInfrastructureFailure_DoesNotFeedFastFailBreaker()
    {
        // Pin the merge-phase branch of the infra filter separately: the work
        // phase succeeds, then merge sees a missing-binary-shaped failure. Even
        // with a threshold of 1, this must not bench the agent.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed, maxConsecutiveFastFails: 1);

        fix.Codex.WorkPlan.Enqueue(new FileWrite("ok.txt", "v1"));
        fix.Codex.MergeScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 127",
            Stdout: null,
            Stderr: "env: 'codex': No such file or directory"));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.True(fix.Registry.GetAvailability(AgentKind.Codex).Available);

        var snap = fix.Registry.Snapshot().Single(s => s.Agent == AgentKind.Codex);
        Assert.Equal(0, snap.ConsecutiveFastFails);
        Assert.DoesNotContain(fix.Webhooks.Events, e => e.Event == "agent.smoke_failed");
    }

    [Fact]
    public async Task ConflictMergeResolverInfrastructureFailure_UsesLastAttemptClassification_NotAggregateSummary()
    {
        // Host-conflict merges run through AgenticConflictResolver, whose
        // public Summary is an aggregate trail. Availability classification
        // must use the raw last failed attempt: the aggregate summary does not
        // start with "agent exited 127", so classifying it directly would turn
        // this missing-binary failure into a normal fast fail and bench an
        // otherwise healthy agent.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var codex = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Codex };
        var claude = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Claude };

        codex.AgenticConflictResults.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 2",
            Stdout: null,
            Stderr: "panic: ordinary resolver crash"));
        claude.AgenticConflictResults.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 127",
            Stdout: null,
            Stderr: "env: 'claude': No such file or directory"));

        using var fix = BuildConflictMergePipeline(seed, [codex, claude], maxConsecutiveFastFails: 1);

        var item = NewConflictMergeItem(AgentKind.Codex) with
        {
            State = WorkItemState.WorkComplete,
            // Skip the third-line conflict-rework fallback so this test only
            // observes the merge resolver's availability classification.
            ConflictReworkAttempts = 1,
        };
        var repoId = await fix.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await CommitToBareBranchAsync(
            fix.GitHost.GetRepoPath(repoId),
            item.WorkBranch!,
            "README.md",
            "work branch change\n",
            "work readme");
        await CommitToSeedAsync(seed, "README.md", "main branch change\n", "main readme");

        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.MergeConflictResolutionFailed, final!.State);
        Assert.Single(codex.AgenticConflictInvocations);
        Assert.Single(claude.AgenticConflictInvocations);

        Assert.True(fix.Registry.GetAvailability(AgentKind.Codex).Available);
        Assert.True(fix.Registry.GetAvailability(AgentKind.Claude).Available);
        Assert.DoesNotContain(fix.Webhooks.Events, e => e.Event == "agent.smoke_failed");
        Assert.DoesNotContain(fix.Registry.Snapshot(), s => s.ConsecutiveFastFails != 0);
    }

    [Fact]
    public async Task DirectAgentPickup_InVmGate_Exit127_FailsItemBeforeRunner_AndPublishesWebhook()
    {
        // The tests:meaningfulness-review Error: wire a REAL InVmSmokeProber into
        // PipelineRunner and prove the work-item gate (PipelineRunner.cs ~331)
        // actually short-circuits a direct-agent pickup (no AgentClass) when the
        // agent CLI exits 127 on `agent --version` inside the sandbox. A
        // regression that dropped this block, tied it back to
        // SkipCredentialSmokeTest, or wired it incorrectly would let the runner
        // be invoked and the exit-127 cascade reach first dispatch.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var cursorAgent = new ScriptableAgent(AgentKind.Cursor);
        var registry = new AgentRegistry([cursorAgent]);

        var availability = new AgentAvailabilityRegistry(
            new AvailabilityOptions(), TimeProvider.System,
            NullLogger<AgentAvailabilityRegistry>.Instance);

        // Scripted sandbox the prober clones: `agent --version` returns 127
        // (binary missing from PATH), everything else passes.
        var probeProvider = new ScriptedSandboxProvider(exec =>
            exec.Argv.Count >= 2 && exec.Argv[1] == "--version"
                ? new SandboxExecResult(127, "", "bash: agent: command not found")
                : new SandboxExecResult(0, "", ""));
        var cursorCred = new AgentCredential(
            AgentKind.Cursor,
            new Dictionary<string, string> { ["CODEYBOX_CURSOR_AUTH_JSON"] = "{\"token\":\"t\"}" },
            new Dictionary<string, string>());
        var baselineResolver = new StubBaselineResolver("base-A");
        var prober = new InVmSmokeProber(
            probeProvider,
            baselineResolver,
            baselineResolver,
            new ConstantCredentialProvider(cursorCred),
            [new CursorInVmSmokeProbe()],
            availability,
            new InVmSmokeCache(TimeSpan.FromMinutes(60)),
            new NullWebhookDispatcher(),
            new InVmSmokeOptions { Enabled = true, ImageReference = "img", NetworkProfile = "work-profile", SweepIntervalSeconds = 0 },
            NullLogger<InVmSmokeProber>.Instance);

        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var webhooks = new CapturingWebhookDispatcher();

        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = seed,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Cursor,
            NetworkProfiles = new ProjectNetworkProfiles { Work = "work-profile" },
            // No DefaultAgentClass — this is the direct-agent path the gate must
            // still cover. SkipCredentialSmokeTest stays false; even if it were
            // true the in-VM gate is now decoupled from it.
            Audit = new ProjectAudit { MaxIterations = 1, AuditTypes = [] },
        };
        var projects = new InMemoryProjectRepository(project);
        var composer = new ProjectAuditorComposer(new ScriptedAuditorCatalog([]));
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, new TestUpstreamFactory(), composer,
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            availability: availability,
            authAvailability: availability,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            dispatchAvailability: new AgentDispatchAvailability(availability, prober),
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "in-vm gate",
            Prompt = "do thing",
            BaseBranch = "main",
            Agent = AgentKind.Cursor,
            PushUpstream = false,
        };
        await store.CreateAsync(item);
        await pipeline.RunAsync(item, CancellationToken.None);

        // The gate fired: the item failed without ever invoking the agent runner.
        var final = await store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("in-VM smoke gate", final.LastError);
        Assert.Contains("agent binary not runnable", final.LastError);
        Assert.Equal(0, cursorAgent.CallCount);

        // The prober benched cursor on the exit-127 version step.
        Assert.Equal(1, probeProvider.CreateCount);
        Assert.False(availability.GetAvailability(AgentKind.Cursor).Available);

        // PipelineRunner published the agent.smoke_failed transition for the item.
        var failed = Assert.Single(webhooks.Events, e => e.Event == "agent.smoke_failed");
        var details = Assert.IsType<AgentSmokeFailedDetails>(failed.Details);
        Assert.Equal("cursor", details.AgentKind);
    }

    [Fact]
    public async Task DirectAgentPickup_PausedVerdict_ParksWithoutSmokeFailure()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var pauseGate = new PausingTargetInVmSmokeGate(AgentKind.Codex);
        using var fix = BuildPipeline(seed, inVmSmokeGate: pauseGate);

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.WaitingForAgentResume, final!.State);
        Assert.Equal("work", final.AgentPauseRetryFrom);
        Assert.Null(final.QuotaRetryFrom);
        Assert.Contains("waiting: agent paused", final.LastError);
        Assert.Equal(0, fix.Codex.CallCount);
        Assert.DoesNotContain(fix.Webhooks.Events, e => e.Event == "agent.smoke_failed");
    }

    [Fact]
    public async Task MergePhase_PausedDirectAgent_ParksFromMerge()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var pauseGate = new PausingTargetInVmSmokeGate(AgentKind.Codex);
        using var fix = BuildPipeline(seed, inVmSmokeGate: pauseGate);

        var item = NewItem(AgentKind.Codex) with
        {
            State = WorkItemState.AuditPassed,
            WorkBranch = "feature/merge-paused-" + Guid.NewGuid().ToString("N")[..8],
        };
        var repoId = await fix.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await CommitWorkBranchAsync(fix.GitHost.GetRepoPath(repoId), item.WorkBranch!);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.WaitingForAgentResume, final!.State);
        Assert.Equal("merge", final.AgentPauseRetryFrom);
        Assert.Null(final.QuotaRetryFrom);
        Assert.Contains("waiting: agent paused", final.LastError);
        Assert.Equal(0, fix.Codex.CallCount);
    }

    [Fact]
    public async Task DirectAgentPickup_SmokeDisabledGlobally_SkipsInVmGate_AndInvokesRunner()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var cursorAgent = new ScriptableAgent(AgentKind.Cursor);
        var registry = new AgentRegistry([cursorAgent]);
        var availability = new AgentAvailabilityRegistry(
            new AvailabilityOptions(), TimeProvider.System,
            NullLogger<AgentAvailabilityRegistry>.Instance);
        availability.MarkSmokeResult(
            AgentKind.Cursor,
            new AgentSmokeResult(false, "transient: try later", TimeSpan.Zero, SmokeFailureCategory.Transient),
            SmokeExclusionSource.InVmSmoke);
        var smokeOptions = new SmokeOptionsSnapshot(new SmokeOptions { Enabled = false });
        var gate = new RejectingInVmSmokeGate();

        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var webhooks = new CapturingWebhookDispatcher();

        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = seed,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Cursor,
            NetworkProfiles = new ProjectNetworkProfiles { Work = "work-profile" },
            Audit = new ProjectAudit { MaxIterations = 1, AuditTypes = [] },
        };
        var projects = new InMemoryProjectRepository(project);
        var composer = new ProjectAuditorComposer(new ScriptedAuditorCatalog([]));
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, new TestUpstreamFactory(), composer,
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            availability: availability,
            authAvailability: availability,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            dispatchAvailability: new AgentDispatchAvailability(availability, gate, smokeOptions),
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "smoke disabled",
            Prompt = "do thing",
            BaseBranch = "main",
            Agent = AgentKind.Cursor,
            PushUpstream = false,
        };
        await store.CreateAsync(item);
        await pipeline.RunAsync(item, CancellationToken.None);

        var final = await store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.DoesNotContain("in-VM smoke gate", final.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.True(cursorAgent.CallCount > 0);
        Assert.Equal(0, gate.EnsureCalls);
    }

    [Fact]
    public async Task DirectAgentPickup_SmokeDisabledGlobally_StillHonorsFastFailBreaker()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var smokeOptions = new SmokeOptionsSnapshot(new SmokeOptions { Enabled = false });
        var gate = new RejectingInVmSmokeGate();
        using var fix = BuildPipeline(seed, smokeOptions: smokeOptions, inVmSmokeGate: gate);
        for (var i = 0; i < 3; i++)
            fix.Registry.RecordRunOutcome(
                AgentKind.Codex,
                success: false,
                duration: TimeSpan.FromMilliseconds(500));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("fast-fail circuit breaker", final.LastError);
        Assert.Equal(0, gate.EnsureCalls);
        Assert.Equal(0, fix.Codex.CallCount);
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private TestFixture BuildPipeline(
        string seedRepoUrl,
        int maxConsecutiveFastFails = 3,
        SmokeOptionsSnapshot? smokeOptions = null,
        IInVmSmokeGate? inVmSmokeGate = null)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var webhooks = new CapturingWebhookDispatcher();

        // Single-agent setup: no class router, no fallback target. A
        // non-quota fast-fail terminates the item as Failed, and the registry
        // sees the outcome via PipelineRunner's RecordRunOutcome call.
        var codex = new ScriptableAgent(AgentKind.Codex);
        var registry = new AgentRegistry([codex]);

        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Codex,
            Audit = new ProjectAudit
            {
                MaxIterations = 1,
                AuditTypes = [],
            },
        };
        var projects = new InMemoryProjectRepository(project);
        var composer = new ProjectAuditorComposer(new ScriptedAuditorCatalog([]));

        var availability = new AgentAvailabilityRegistry(
            new AvailabilityOptions
            {
                FastFailThresholdSeconds = 10,
                MaxConsecutiveFastFails = maxConsecutiveFastFails,
            },
            TimeProvider.System,
            NullLogger<AgentAvailabilityRegistry>.Instance);
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, new TestUpstreamFactory(), composer,
            store, webhooks,
            new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
            },
            NullLogger<PipelineRunner>.Instance,
            quotaClassifier: new CompositeQuotaFailureClassifier(new IAgentQuotaFailureDetector[]
            {
                new ClaudeQuotaFailureDetector(),
                new CodexQuotaFailureDetector(),
                new GeminiQuotaFailureDetector(),
            }),
            availability: availability,
            authAvailability: availability,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            dispatchAvailability: new AgentDispatchAvailability(availability, inVmSmokeGate, smokeOptions),
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);

        return new TestFixture(pipeline, store, codex, webhooks, availability, gitHost);
    }

    private ConflictMergeFixture BuildConflictMergePipeline(
        string seedRepoUrl,
        IReadOnlyList<IAgentRunner> agents,
        int maxConsecutiveFastFails)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var webhooks = new CapturingWebhookDispatcher();
        var registry = new AgentRegistry(agents);

        var memberships = agents
            .Select((agent, index) => new AgentMembership
            {
                Agent = agent.Kind,
                Billing = AgentBilling.Subscription,
                QualityScore = 100 - index,
            })
            .ToList();
        var agentClass = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members = memberships,
        };
        var router = new AgentClassRouter(
            [agentClass],
            memberships.Select(m => new RecordingProbe(m.Agent)).ToArray(),
            new QuotaRouterOptions { MinQuotaPct = 10.0 },
            NullLogger<AgentClassRouter>.Instance);

        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = agents[0].Kind,
            DefaultAgentClass = "frontier",
            Audit = new ProjectAudit { MaxIterations = 1, AuditTypes = [] },
        };
        var projects = new InMemoryProjectRepository(project);
        var composer = new ProjectAuditorComposer(new ScriptedAuditorCatalog([]));
        var availability = new AgentAvailabilityRegistry(
            new AvailabilityOptions
            {
                FastFailThresholdSeconds = 10,
                MaxConsecutiveFastFails = maxConsecutiveFastFails,
            },
            TimeProvider.System,
            NullLogger<AgentAvailabilityRegistry>.Instance);
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, new TestUpstreamFactory(), composer,
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            classRouter: router,
            quotaClassifier: new CompositeQuotaFailureClassifier(new IAgentQuotaFailureDetector[]
            {
                new ClaudeQuotaFailureDetector(),
                new CodexQuotaFailureDetector(),
                new GeminiQuotaFailureDetector(),
            }),
            availability: availability,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);

        return new ConflictMergeFixture(pipeline, store, gitHost, webhooks, availability);
    }

    private sealed class RejectingInVmSmokeGate : IInVmSmokeGate
    {
        public int EnsureCalls { get; private set; }
        public bool Enabled => true;

        public Task<AgentAvailability> EnsureAvailableAsync(
            AgentKind kind,
            InVmSmokeSandboxTarget target,
            CancellationToken ct)
        {
            EnsureCalls++;
            return Task.FromResult(new AgentAvailability(false, "transient: try later", null));
        }

        public Task ProbeAllAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ProbeAllAsync(InVmSmokeSandboxTarget target, CancellationToken ct) => Task.CompletedTask;

        public Task<AgentAvailability?> ForceProbeAsync(AgentKind kind, CancellationToken ct) =>
            Task.FromResult<AgentAvailability?>(new AgentAvailability(false, "transient: try later", null));
    }

    private sealed class AuthCorroboratingInVmSmokeGate : IInVmSmokeGate
    {
        public int EnsureCalls { get; private set; }
        public int ForceProbeCalls { get; private set; }
        public bool Enabled => true;

        public Task<AgentAvailability> EnsureAvailableAsync(
            AgentKind kind,
            InVmSmokeSandboxTarget target,
            CancellationToken ct)
        {
            EnsureCalls++;
            return Task.FromResult(new AgentAvailability(true, null, null));
        }

        public Task ProbeAllAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ProbeAllAsync(InVmSmokeSandboxTarget target, CancellationToken ct) => Task.CompletedTask;

        public Task<AgentAvailability?> ForceProbeAsync(AgentKind kind, CancellationToken ct)
        {
            ForceProbeCalls++;
            return Task.FromResult<AgentAvailability?>(
                new AgentAvailability(false, "smoke probe failed [persistent]: credential login required", null));
        }
    }

    private static WorkItem NewItem(AgentKind initialAgent) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "availability wiring",
        Prompt = "do thing",
        BaseBranch = "main",
        Agent = initialAgent,
        PushUpstream = false,
    };

    private static WorkItem NewConflictMergeItem(AgentKind initialAgent)
    {
        var id = WorkItemId.New();
        return new WorkItem
        {
            Id = id,
            ProjectId = new ProjectId("test-project"),
            Title = "availability wiring conflict merge",
            Prompt = "do thing",
            BaseBranch = "main",
            WorkBranch = $"codeybox/{id.ToString()[..8]}",
            Agent = initialAgent,
            AgentClassId = "frontier",
            PushUpstream = false,
        };
    }

    private async Task CommitToBareBranchAsync(
        string barePath,
        string branch,
        string fileName,
        string contents,
        string subject)
    {
        var clone = Path.Combine(_workspace, "clone-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", barePath, clone);
        try
        {
            await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
            await TestSupport.RunGit(clone, "config", "user.name", "Test");
            await TestSupport.RunGit(clone, "checkout", "-B", branch, "origin/main");
            await File.WriteAllTextAsync(Path.Combine(clone, fileName), contents);
            await TestSupport.RunGit(clone, "add", fileName);
            await TestSupport.RunGit(clone, "commit", "-m", $"{subject}\n\n{CodeyBoxTrailers.CoAuthoredBy}");
            await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{branch}");
        }
        finally
        {
            try { Directory.Delete(clone, recursive: true); } catch { }
        }
    }

    private static async Task CommitToSeedAsync(string repoPath, string path, string content, string message)
    {
        await TestSupport.RunGit(repoPath, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(repoPath, "config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(repoPath, path), content);
        await TestSupport.RunGit(repoPath, "add", path);
        await TestSupport.RunGit(repoPath, "commit", "-m", message);
    }

    private static async Task CommitWorkBranchAsync(
        string bareRepoPath,
        string workBranch,
        string path = "merge-phase.txt",
        string contents = "ready\n",
        string subject = "work already audited")
    {
        var clone = Path.Combine(Path.GetTempPath(), "codeybox-merge-pause-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(clone);
        try
        {
            await TestSupport.RunGit(clone, "clone", bareRepoPath, ".");
            await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
            await TestSupport.RunGit(clone, "config", "user.name", "Test");
            await TestSupport.RunGit(clone, "checkout", "-b", workBranch, "origin/main");
            await File.WriteAllTextAsync(Path.Combine(clone, path), contents);
            await TestSupport.RunGit(clone, "add", path);
            await TestSupport.RunGit(clone, "commit", "-m", subject);
            await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{workBranch}");
        }
        finally
        {
            try { Directory.Delete(clone, recursive: true); } catch { }
        }
    }

    private sealed class TestFixture : IDisposable
    {
        public PipelineRunner Pipeline { get; }
        public SqliteWorkItemStore Store { get; }
        public ScriptableAgent Codex { get; }
        public CapturingWebhookDispatcher Webhooks { get; }
        public AgentAvailabilityRegistry Registry { get; }
        public LocalGitHost GitHost { get; }

        public TestFixture(
            PipelineRunner pipeline,
            SqliteWorkItemStore store,
            ScriptableAgent codex,
            CapturingWebhookDispatcher webhooks,
            AgentAvailabilityRegistry registry,
            LocalGitHost gitHost)
        {
            Pipeline = pipeline;
            Store = store;
            Codex = codex;
            Webhooks = webhooks;
            Registry = registry;
            GitHost = gitHost;
        }

        public void Dispose() => Store.Dispose();
    }

    private sealed record ConflictMergeFixture(
        PipelineRunner Pipeline,
        SqliteWorkItemStore Store,
        LocalGitHost GitHost,
        CapturingWebhookDispatcher Webhooks,
        AgentAvailabilityRegistry Registry) : IDisposable
    {
        public void Dispose() => Store.Dispose();
    }
}
