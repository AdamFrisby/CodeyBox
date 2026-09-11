using CodeyBox.Audit.Llm;
using CodeyBox.Audit.Shell;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end guards for the quota-park corroboration safety net: a quota
/// park must require corroboration rather than a substring match alone.
///
/// A failed agent run whose captured output contains quota markers in
/// agent-authored or provider-quoted text must NOT park the item in
/// <see cref="WorkItemState.WaitingForQuotaReset"/> while the agent's quota
/// probe reports a known healthy reading — that classification is always a
/// misattribution. A genuine provider 429 with an unknown or exhausted probe
/// reading must still park, with the parsed reset preserved.
///
/// These drive the REAL <see cref="Orchestrator.PipelineRunner"/> — not the
/// detector in isolation — on both the work path and the LLM-auditor dispatch
/// path, which both produced false parks in production.
/// </summary>
[Collection("Pipeline integration")]
public sealed class AntigravityQuotaProbeCorroborationTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-quota-corrob-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task WorkPhase_QuotaMarkerWithHealthyProbe_DoesNotParkWaitingForQuotaReset()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Antigravity };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            agentOverride: agent,
            auditQuotaProbes: [new FixedQuotaProbe(AgentKind.Antigravity, availablePct: 96.0)]);

        // The exact production false-park shape: the run really failed with a
        // plain "agent exited 1", and the quota markers in the captured output
        // are agent-authored prose (the agent was working on quota code), while
        // the probe reads a known healthy 96%. Parking this as quota-exhausted
        // contradicts a fresh, known reading and is always wrong.
        agent.WorkResults.Enqueue(new AgentResult(
            false,
            "agent exited 1",
            "the antigravity quota detector's marker set caught the status token RESOURCE_EXHAUSTED and the phrase quota exceeded",
            "agent exited 1"));

        var item = NewItem() with { Agent = AgentKind.Antigravity };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.NotEqual(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.NotEqual("quota", final.FailureKind);
    }

    [Fact]
    public async Task WorkPhase_QuotedProviderErrorWithHealthyProbe_DoesNotParkWaitingForQuotaReset()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Antigravity };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            agentOverride: agent,
            auditQuotaProbes: [new FixedQuotaProbe(AgentKind.Antigravity, availablePct: 96.0)]);

        // Scoping alone cannot catch this shape: the agent quotes (or forges)
        // a provider error envelope inside its own output, so the detector
        // still fires on the structured stream message. The probe safety net
        // must veto the park: no provider-surface (stderr) evidence exists and
        // the live probe reads a known healthy 96%.
        agent.WorkResults.Enqueue(new AgentResult(
            false,
            "agent exited 1",
            """
            working on the quota retry logic...
            {"type":"result","status":"error","error":"quota exceeded"}
            done.
            """,
            "agent exited 1"));

        var item = NewItem() with { Agent = AgentKind.Antigravity };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.NotEqual(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.NotEqual("quota", final.FailureKind);
    }

    [Fact]
    public async Task WorkPhase_QuotedProviderErrorWithExhaustedProbe_StillParks()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Antigravity };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            agentOverride: agent,
            auditQuotaProbes: [new FixedQuotaProbe(AgentKind.Antigravity, availablePct: 0.0)]);

        // Same quotable stream-only evidence, but the probe corroborates the
        // failure (known exhausted): the veto must not blind genuine
        // quota blocks that surface only in the captured stream.
        agent.WorkResults.Enqueue(new AgentResult(
            false,
            "agent exited 1",
            """
            {"type":"result","status":"error","error":"quota exceeded"}
            """,
            "agent exited 1"));

        var item = NewItem() with { Agent = AgentKind.Antigravity };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.Equal("quota", final.FailureKind);
        Assert.NotNull(final.NextQuotaRetryAt);
    }

    [Fact]
    public async Task WorkPhase_GenuineProvider429WithExhaustedProbe_StillParksWithReset()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Antigravity };
        var resetAt = DateTimeOffset.UtcNow.AddMinutes(8).AddSeconds(14);
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            agentOverride: agent,
            auditQuotaProbes: [new FixedQuotaProbe(AgentKind.Antigravity, availablePct: 0.0, resetAt: resetAt)]);

        // Genuine provider 429 on the failure surface with a corroborating
        // (exhausted) probe reading must still park — the safety net must not
        // weaken real 429 detection, including hidden consumer-tier blocks.
        var before = DateTimeOffset.UtcNow;
        agent.WorkResults.Enqueue(new AgentResult(
            false,
            "agent exited 1",
            null,
            "RESOURCE_EXHAUSTED (code 429): Individual quota reached (Resets in 8m14s)"));

        var item = NewItem() with { Agent = AgentKind.Antigravity };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);
        var after = DateTimeOffset.UtcNow;

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.Equal("quota", final.FailureKind);
        Assert.NotNull(final.NextQuotaRetryAt);
        // The parsed relative reset window (≈ now + 8m14s = 494s) is preserved,
        // not replaced by a coarse default backoff.
        Assert.InRange(
            final.NextQuotaRetryAt!.Value,
            before.AddSeconds(494),
            after.AddSeconds(494).AddMinutes(1));
    }

    [Fact]
    public async Task LlmAuditor_QuotaMarkerWithHealthyProbe_DoesNotParkWaitingForQuotaReset()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Antigravity };
        agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        // The auditor's agent run fails with a quota marker in its captured
        // output while the probe reports a known healthy reading: the audit
        // failure must surface as a transient execution failure, never as a
        // quota park of the whole work item.
        agent.AuditAgentResults.Enqueue(new AgentResult(
            false,
            "agent exited 1",
            "reviewing the RESOURCE_EXHAUSTED quota exceeded handling in this diff",
            "agent exited 1"));
        var auditor = new LlmReviewAuditor(new LlmReviewAuditorOptions
        {
            Name = "security:llm-review",
            Agent = agent,
            ReviewFocus = "security review",
            FrameTemplate = "{{reviewFocus}}\n{{resultFile}}",
        });
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: TestAuditGates.WithPassedBuildAndTest(auditor),
            maxAuditIterations: 1,
            agentOverride: agent,
            auditQuotaProbes: [new FixedQuotaProbe(AgentKind.Antigravity, availablePct: 96.0)],
            requiredBuildVerifier: new TestRequiredBuildVerifier(
                RequiredBuildProbeResult.Applies,
                RequiredBuildVerificationResult.Passed(0, "ok")));

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
        WorkBranch = "feature/quota-corrob",
        PushUpstream = false,
    };

    private sealed class FixedQuotaProbe(AgentKind kind, double availablePct, DateTimeOffset? resetAt = null)
        : IAgentQuotaProbe
    {
        public AgentKind Kind { get; } = kind;

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct) =>
            Task.FromResult(new AgentQuotaSnapshot
            {
                AvailablePct = availablePct,
                ResetAt = resetAt,
            });
    }
}
