using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Projects;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end guard for the acceptance criterion: an antigravity
/// RESOURCE_EXHAUSTED (429) run that exits 0 and makes NO file changes (agy's
/// consumer-tier give-up shape) must park the work item in
/// <see cref="WorkItemState.WaitingForQuotaReset"/> — with the parsed relative
/// reset window — instead of terminal-failing as "produced no changes" and
/// dead-lettering. A genuine no-op (exit 0, no changes, NO quota marker) must
/// still terminal-fail, so the routing adds no false quota parks.
///
/// <para>This drives the REAL <see cref="Orchestrator.PipelineRunner"/> work
/// phase — not the detector in isolation — through the exact clean-exit /
/// empty-diff failure shape, closing the gap the detector-only unit tests
/// could not reach: the runner lifts agy's terminal 429 into
/// <see cref="AgentResult.TerminalDiagnostic"/> on an exit-0 run, and the
/// no-changes branch classifies it and routes it to a
/// <c>TerminalQuotaError</c> park.</para>
/// </summary>
[Collection("Pipeline integration")]
public sealed class AntigravityExitZeroQuotaParkTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-agy-park-").FullName;

    public void Dispose()
    {
        CodeyBox.Tests.TestTempArtifacts.DeleteDirectory(_workspace);
    }

    [Fact]
    public async Task WorkPhase_ExitZeroNoChangesWithTerminalQuota_ParksWaitingForQuotaResetWithPreciseWindow()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Antigravity };
        using var tp = TestSupport.BuildPipeline(_workspace, seed, agentOverride: agent);

        // agy exit 0, no file writes, but the runner lifted its terminal consumer
        // 429 into TerminalDiagnostic (its exit code and stderr never reflect the
        // block). This is the exact give-up shape that used to terminal-fail as
        // "produced no changes".
        agent.WorkResults.Enqueue(new AgentResult(true, "ok", null, null)
        {
            TerminalDiagnostic =
                "RESOURCE_EXHAUSTED (code 429): Individual quota reached (Resets in 8m14s)",
        });

        var item = NewItem() with { Agent = AgentKind.Antigravity };
        await tp.Store.CreateAsync(item);

        var before = DateTimeOffset.UtcNow;
        await tp.Pipeline.RunAsync(item, CancellationToken.None);
        var after = DateTimeOffset.UtcNow;

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        // Parked (not Failed, not dead-lettered) so the QuotaRetryScheduler resumes it.
        Assert.Equal(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.Equal("quota", final.FailureKind);
        Assert.NotNull(final.NextQuotaRetryAt);
        // resetAt ≈ now + 8m14s (494s) — the PRECISE parsed window, not a coarse
        // default backoff. Bracket by the wall-clock reads to stay flake-free.
        Assert.InRange(
            final.NextQuotaRetryAt!.Value,
            before.AddSeconds(494),
            after.AddSeconds(494));
    }

    [Fact]
    public async Task WorkPhase_ExitZeroNoChangesNoQuotaMarker_TerminalFailsWithoutParking()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Antigravity };
        using var tp = TestSupport.BuildPipeline(_workspace, seed, agentOverride: agent);

        // Genuine no-op: exit 0, no changes, and NO terminal quota diagnostic. The
        // no-changes branch must find no detection and fall through to the generic
        // "produced no changes" terminal failure — no false quota park.
        agent.WorkResults.Enqueue(new AgentResult(true, "ok", null, null));

        var item = NewItem() with { Agent = AgentKind.Antigravity };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.NotEqual(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.Equal(WorkItemState.Failed, final.State);
    }

    [Fact]
    public async Task WorkPhase_ExitZeroNoChangesWithQuotaButNoResetHint_ParksWithDefaultBackoff()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Antigravity };
        using var tp = TestSupport.BuildPipeline(_workspace, seed, agentOverride: agent);

        // Quota block WITHOUT a parseable reset duration. The relative-reset parser
        // yields null; the routing must still park (falling back to the default
        // backoff) rather than null-crash — acceptance item #1's "rather than
        // null-crashing" clause.
        agent.WorkResults.Enqueue(new AgentResult(true, "ok", null, null)
        {
            TerminalDiagnostic = "RESOURCE_EXHAUSTED (code 429): Individual quota reached",
        });

        var item = NewItem() with { Agent = AgentKind.Antigravity };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        // Parked, not Failed/dead-lettered, and with a non-null default retry time.
        Assert.Equal(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.Equal("quota", final.FailureKind);
        Assert.NotNull(final.NextQuotaRetryAt);
    }

    [Fact]
    public async Task WorkPhase_ExitZeroNoChangesWithUnauthorizedTerminal_DoesNotParkAsQuota()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Antigravity };
        using var tp = TestSupport.BuildPipeline(_workspace, seed, agentOverride: agent);

        // An auth block (401) lifted into TerminalDiagnostic classifies as
        // Unauthorized. Parking that as WaitingForQuotaReset would retry it forever
        // (an expired token never clears on a quota window), so the exit-0 routing
        // must NOT park it as quota — it falls through to the generic no-changes
        // terminal failure instead.
        agent.WorkResults.Enqueue(new AgentResult(true, "ok", null, null)
        {
            TerminalDiagnostic = "API Error: 401 Unauthorized: invalid credentials",
        });

        var item = NewItem() with { Agent = AgentKind.Antigravity };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.NotEqual(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.NotEqual("quota", final.FailureKind);
    }

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = "feature/agy-park",
        PushUpstream = false,
    };
}
