using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>
/// Guards for the durable agent-turn resume quota contract: a resume must
/// never burn its bounded dispatch budget on a pinned agent the quota router
/// already knows cannot run.
/// <list type="number">
/// <item>Pinned agent exhausted → the resume is deferred (quota park, attempt
/// uncounted) or rerouted to an eligible class member.</item>
/// <item>A dispatch that ends in a provider rate-limit/quota classification
/// before the agent produced output must not count toward the limit.</item>
/// <item>When the limit is hit, the item restarts as a normal fresh work turn
/// (branch preserved) instead of Failed, if any eligible agent exists.</item>
/// </list>
/// Each test drives the real <see cref="PipelineRunner"/> through two
/// pickups: the first parks a genuine durable checkpoint via back-to-back
/// quota failures, the second exercises the guard under test.
/// </summary>
[Collection("Pipeline integration")]
public sealed class DurableResumeQuotaGuardTests : IDisposable
{
    private const string ClassId = "test-class";
    private static readonly AgentResult QuotaFailure =
        new(false, "agent exited 1", "", "rate_limit_exceeded reset after 1h");

    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-resume-quota-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    private sealed class MutableQuotaProbe(AgentKind kind, double availablePct) : IAgentQuotaProbe
    {
        public AgentKind Kind { get; } = kind;
        public double AvailablePct { get; set; } = availablePct;

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
            => Task.FromResult(new AgentQuotaSnapshot { AvailablePct = AvailablePct });
    }

    private sealed class Setup
    {
        public TestPipeline Pipeline { get; init; } = null!;
        public AgentClassRouter Router { get; init; } = null!;
        public ScriptedAgent Claude { get; init; } = null!;
        public ScriptedAgent Codex { get; init; } = null!;
        public MutableQuotaProbe ClaudeProbe { get; init; } = null!;
        public MutableQuotaProbe CodexProbe { get; init; } = null!;
    }

    private async Task<Setup> BuildAsync()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var claude = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Claude };
        var codex = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Codex };
        var claudeProbe = new MutableQuotaProbe(AgentKind.Claude, 80);
        var codexProbe = new MutableQuotaProbe(AgentKind.Codex, 80);
        var router = new AgentClassRouter(
            [new AgentClass
            {
                Id = ClassId,
                DisplayName = "Test",
                Members =
                [
                    new() { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                    new() { Agent = AgentKind.Codex, Billing = AgentBilling.Subscription, QualityScore = 100 },
                ],
            }],
            [claudeProbe, codexProbe],
            new QuotaRouterOptions { MinQuotaPct = 10.0 },
            NullLogger<AgentClassRouter>.Instance);
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seed,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            DefaultAgentClass = ClassId,
            Audit = new ProjectAudit { MaxIterations = 3 },
        });
        var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            agentOverride: claude,
            extraAgentRunners: [codex],
            classRouter: router,
            projectRepository: projects);
        return new Setup
        {
            Pipeline = tp,
            Router = router,
            Claude = claude,
            Codex = codex,
            ClaudeProbe = claudeProbe,
            CodexProbe = codexProbe,
        };
    }

    private static WorkItem NewItem(string workBranch) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = workBranch,
        PushUpstream = false,
        Agent = AgentKind.Claude,
        AgentClassId = ClassId,
    };

    private static AgentMembership Member(AgentClassRouter router, AgentKind kind) =>
        router.GetClassMembers(ClassId).First(m => m.Agent == kind);

    /// <summary>
    /// First pickup: both class members fail with a provider rate-limit, so
    /// the item parks in WaitingForQuotaReset holding a genuine durable
    /// checkpoint pinned to the last-failing member (codex).
    /// </summary>
    private static async Task<WorkItem> ParkCheckpointAsync(Setup s, WorkItem item)
    {
        s.Claude.WorkResults.Enqueue(QuotaFailure);
        s.Codex.WorkResults.Enqueue(QuotaFailure);
        await s.Pipeline.Store.CreateAsync(item);
        await s.Pipeline.Pipeline.RunAsync(item, CancellationToken.None);

        var parked = await s.Pipeline.Store.GetAsync(item.Id);
        Assert.NotNull(parked);
        Assert.True(
            parked!.State == WorkItemState.WaitingForQuotaReset,
            $"expected WaitingForQuotaReset but was {parked.State}: {parked.LastError}");
        Assert.NotNull(parked.AgentTurnResumeCheckpoint);
        Assert.Equal(AgentKind.Codex, parked.AgentTurnResumeCheckpoint!.Agent);
        Assert.Equal(0, parked.AgentTurnResumeCheckpoint.AttemptCount);
        return parked;
    }

    /// <summary>
    /// Mirrors the quota-retry scheduler's re-dispatch: a parked item
    /// re-enters the pipeline in its checkpoint's resume state with the
    /// recovery boundary intact.
    /// </summary>
    private static async Task<WorkItem> RedriveForResumeAsync(Setup s, WorkItem parked)
    {
        var redriven = parked with
        {
            State = parked.AgentTurnResumeCheckpoint!.ResumeState,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await s.Pipeline.Store.UpdateAsync(redriven);
        return (await s.Pipeline.Store.GetAsync(parked.Id))!;
    }

    [Fact]
    public async Task PinnedAgentExhaustedWithNoAlternative_ResumeDeferredWithoutCountingAttempt()
    {
        var s = await BuildAsync();
        var parked = await ParkCheckpointAsync(s, NewItem("feature/resume-defer"));

        // Both members stay quota-dead: probes read 0% and the pickup-1
        // in-process exhaustion verdicts are still fresh.
        s.ClaudeProbe.AvailablePct = 0;
        s.CodexProbe.AvailablePct = 0;
        var claudePrompts = s.Claude.WorkPrompts.Count;
        var codexPrompts = s.Codex.WorkPrompts.Count;

        var redrivenDefer = await RedriveForResumeAsync(s, parked);
        await s.Pipeline.Pipeline.RunAsync(redrivenDefer, CancellationToken.None);

        var final = await s.Pipeline.Store.GetAsync(parked.Id);
        Assert.NotNull(final);
        // Deferred to another quota wait — not Failed, checkpoint intact, and
        // the dispatch-limit counter unchanged because no dispatch ran.
        Assert.Equal(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.NotNull(final.AgentTurnResumeCheckpoint);
        Assert.Equal(0, final.AgentTurnResumeCheckpoint!.AttemptCount);
        Assert.Equal(claudePrompts, s.Claude.WorkPrompts.Count);
        Assert.Equal(codexPrompts, s.Codex.WorkPrompts.Count);
    }

    [Fact]
    public async Task PinnedAgentExhaustedWithHealthyAlternative_ResumeReroutedToFreshTurn()
    {
        var s = await BuildAsync();
        var parked = await ParkCheckpointAsync(s, NewItem("feature/resume-reroute"));

        // Codex stays exhausted; claude recovers (verdict cleared, probe healthy).
        s.Router.ClearExhaustion(Member(s.Router, AgentKind.Claude), out _);
        s.CodexProbe.AvailablePct = 0;
        s.Claude.WorkPlan.Enqueue(new FileWrite("rerouted.txt", "via claude\n"));

        var redrivenReroute = await RedriveForResumeAsync(s, parked);
        await s.Pipeline.Pipeline.RunAsync(redrivenReroute, CancellationToken.None);

        var final = await s.Pipeline.Store.GetAsync(parked.Id);
        Assert.NotNull(final);
        // Fresh turn on the eligible member: checkpoint discarded, item Done,
        // and the pinned agent never re-dispatched.
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(AgentKind.Claude, final.Agent);
        Assert.Null(final.AgentTurnResumeCheckpoint);
        Assert.Null(final.PreemptCheckpoint);
        Assert.Single(s.Codex.WorkPrompts);
        Assert.Equal(2, s.Claude.WorkPrompts.Count);

        var barePath = Path.Combine(s.Pipeline.GitRoot, parked.Id + ".git");
        var (_, blob, _) = await TestSupport.RunGit(barePath, "show", "main:rerouted.txt");
        Assert.Equal("via claude\n", blob);
    }

    [Fact]
    public async Task RateLimitedInstantFailure_DoesNotIncrementDispatchCounter()
    {
        var s = await BuildAsync();
        var parked = await ParkCheckpointAsync(s, NewItem("feature/resume-refund"));

        // Codex recovers; the resumed dispatch fails instantly on a provider
        // rate-limit with no output and no tree changes.
        s.Router.ClearExhaustion(Member(s.Router, AgentKind.Codex), out _);
        s.Codex.WorkResults.Enqueue(QuotaFailure);
        var codexPrompts = s.Codex.WorkPrompts.Count;

        var redrivenRefund = await RedriveForResumeAsync(s, parked);
        await s.Pipeline.Pipeline.RunAsync(redrivenRefund, CancellationToken.None);

        var final = await s.Pipeline.Store.GetAsync(parked.Id);
        Assert.NotNull(final);
        // The dispatch really ran (so this is not a deferral) but the phantom
        // attempt was refunded: still parked, checkpoint intact, counter at 0.
        Assert.Equal(codexPrompts + 1, s.Codex.WorkPrompts.Count);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.NotNull(final.AgentTurnResumeCheckpoint);
        Assert.Equal(0, final.AgentTurnResumeCheckpoint!.AttemptCount);
    }

    [Fact]
    public async Task RateLimitedFailureWithAgentOutput_CountsTowardDispatchLimit()
    {
        var s = await BuildAsync();
        var parked = await ParkCheckpointAsync(s, NewItem("feature/resume-counted"));

        // Same instant rate-limit, but the agent produced output before dying:
        // a real attempt ran, so the counter must advance.
        s.Router.ClearExhaustion(Member(s.Router, AgentKind.Codex), out _);
        s.Codex.WorkResults.Enqueue(
            new AgentResult(false, "agent exited 1", "partial log line", "rate_limit_exceeded reset after 1h"));

        var redrivenCounted = await RedriveForResumeAsync(s, parked);
        await s.Pipeline.Pipeline.RunAsync(redrivenCounted, CancellationToken.None);

        var final = await s.Pipeline.Store.GetAsync(parked.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.NotNull(final.AgentTurnResumeCheckpoint);
        Assert.Equal(1, final.AgentTurnResumeCheckpoint!.AttemptCount);
    }

    [Fact]
    public async Task RateLimitedFailureWithTreeChanges_CountsTowardDispatchLimit()
    {
        var s = await BuildAsync();
        var parked = await ParkCheckpointAsync(s, NewItem("feature/resume-dirty"));

        // Silent rate-limit, but the agent left uncommitted work behind: the
        // re-checkpoint captures a real partial turn, so the counter advances.
        // (Written through the sandbox handle: the working-directory path is
        // a sandbox path, not a host path.)
        s.Router.ClearExhaustion(Member(s.Router, AgentKind.Codex), out _);
        s.Codex.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            var written = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "cat > \"$0\"", $"{workingDirectory}/partial.txt"],
                Stdin = "partial\n",
            }, ct);
            if (!written.Success)
                throw new InvalidOperationException($"setup write failed: {written.Stderr}");
        };
        s.Codex.WorkResults.Enqueue(QuotaFailure);

        var redrivenDirty = await RedriveForResumeAsync(s, parked);
        await s.Pipeline.Pipeline.RunAsync(redrivenDirty, CancellationToken.None);

        var final = await s.Pipeline.Store.GetAsync(parked.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.NotNull(final.AgentTurnResumeCheckpoint);
        Assert.Equal(1, final.AgentTurnResumeCheckpoint!.AttemptCount);
    }

    [Fact]
    public async Task DispatchLimitReachedWithEligibleAlternative_RestartsFreshTurnInsteadOfFailing()
    {
        var s = await BuildAsync();
        var parked = await ParkCheckpointAsync(s, NewItem("feature/resume-limit-reroute"));

        // Simulate an exhausted lineage: bump the parked checkpoint to the
        // configured dispatch budget.
        var bumped = parked with
        {
            AgentTurnResumeCheckpoint = new AgentTurnResumeCheckpoint(
                parked.AgentTurnResumeCheckpoint!.Agent,
                parked.AgentTurnResumeCheckpoint.AgentInstanceRoute,
                parked.AgentTurnResumeCheckpoint.ModelId,
                parked.AgentTurnResumeCheckpoint.ReasoningMode,
                parked.AgentTurnResumeCheckpoint.NativeSessionId,
                parked.AgentTurnResumeCheckpoint.ResumeState,
                parked.AgentTurnResumeCheckpoint.Phase,
                parked.AgentTurnResumeCheckpoint.Iteration,
                parked.AgentTurnResumeCheckpoint.PromptRevision,
                parked.AgentTurnResumeCheckpoint.CreatedAt,
                attemptCount: SessionResumeOptions.MaxResumeAttempts),
        };
        await s.Pipeline.Store.UpdateAsync(bumped);

        // Codex stays exhausted; claude recovers.
        s.Router.ClearExhaustion(Member(s.Router, AgentKind.Claude), out _);
        s.CodexProbe.AvailablePct = 0;
        s.Claude.WorkPlan.Enqueue(new FileWrite("limit-reroute.txt", "fresh turn\n"));

        var redrivenLimit = await RedriveForResumeAsync(s, bumped);
        await s.Pipeline.Pipeline.RunAsync(redrivenLimit, CancellationToken.None);

        var final = await s.Pipeline.Store.GetAsync(parked.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(AgentKind.Claude, final.Agent);
        Assert.Null(final.AgentTurnResumeCheckpoint);
    }

    [Fact]
    public async Task DispatchLimitReachedWithNoAlternative_FailsInsteadOfParking()
    {
        var s = await BuildAsync();
        var parked = await ParkCheckpointAsync(s, NewItem("feature/resume-limit-fail"));

        var bumped = parked with
        {
            AgentTurnResumeCheckpoint = new AgentTurnResumeCheckpoint(
                parked.AgentTurnResumeCheckpoint!.Agent,
                parked.AgentTurnResumeCheckpoint.AgentInstanceRoute,
                parked.AgentTurnResumeCheckpoint.ModelId,
                parked.AgentTurnResumeCheckpoint.ReasoningMode,
                parked.AgentTurnResumeCheckpoint.NativeSessionId,
                parked.AgentTurnResumeCheckpoint.ResumeState,
                parked.AgentTurnResumeCheckpoint.Phase,
                parked.AgentTurnResumeCheckpoint.Iteration,
                parked.AgentTurnResumeCheckpoint.PromptRevision,
                parked.AgentTurnResumeCheckpoint.CreatedAt,
                attemptCount: SessionResumeOptions.MaxResumeAttempts),
        };
        await s.Pipeline.Store.UpdateAsync(bumped);

        // Nobody recovers: the limit-hit lineage has nowhere fresh to go.
        s.ClaudeProbe.AvailablePct = 0;
        s.CodexProbe.AvailablePct = 0;

        var redrivenLimitFail = await RedriveForResumeAsync(s, bumped);
        await s.Pipeline.Pipeline.RunAsync(redrivenLimitFail, CancellationToken.None);

        var final = await s.Pipeline.Store.GetAsync(parked.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("dispatch limit", final.LastError);
    }
}
