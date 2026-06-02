using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using CodeyBox.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Unit-tests for <c>PipelineRunner.BuildAgenticConflictCandidatesAsync</c>,
/// the candidate-ordering core that decides which agent the in-VM agentic
/// conflict resolver tries first, second, etc. when a pickup-rebase or merge
/// phase hits a conflict.
///
/// <para>
/// The deleted <c>RebaseResolverAgentRoutingTests</c> class covered this
/// indirectly by exercising the full pipeline; these tests are the
/// finer-grained direct replacements pinned by the rework audit. They
/// reach into PipelineRunner via <c>internal</c> visibility (set by
/// <c>InternalsVisibleTo("CodeyBox.Tests")</c>) so each branch of
/// <c>BuildAgenticConflictCandidatesAsync</c> can be probed without standing
/// up the full Work → Audit → Merge state machine.
/// </para>
/// </summary>
public sealed class BuildAgenticConflictCandidatesTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-build-candidates-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task PrimaryAndClassChain_AssembledInRouterOrder()
    {
        // Class chain: Claude (primary) → Codex → Gemini.
        // OrderedFallbackCandidates returns Codex then Gemini (Claude is the
        // primary so the router-driven walk just appends the rest of the
        // class). The resulting candidate list MUST be [Claude, Codex, Gemini].
        var primary = new FakeAgentRunner(AgentKind.Claude);
        var codex = new FakeAgentRunner(AgentKind.Codex);
        var gemini = new FakeAgentRunner(AgentKind.Gemini);

        var fixture = BuildFixture(
            runners: [primary, codex, gemini],
            members: [
                new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                new AgentMembership { Agent = AgentKind.Codex, Billing = AgentBilling.Subscription, QualityScore = 90 },
                new AgentMembership { Agent = AgentKind.Gemini, Billing = AgentBilling.Subscription, QualityScore = 80 },
            ]);

        var item = NewItem(AgentKind.Claude);
        await fixture.Store.CreateAsync(item);

        var candidates = await fixture.Pipeline.BuildAgenticConflictCandidatesAsync(item, fixture.Project, primary, CancellationToken.None);

        Assert.Equal(3, candidates.Count);
        Assert.Equal(AgentKind.Claude, candidates[0].Runner.Kind);
        Assert.Equal(AgentKind.Codex, candidates[1].Runner.Kind);
        Assert.Equal(AgentKind.Gemini, candidates[2].Runner.Kind);
    }

    [Fact]
    public async Task ClassChain_CrossKindCandidates_HaveModelIdAndReasoningModeCleared()
    {
        // The item carries claude-specific ModelId / ReasoningMode. The
        // class-chain fallback candidates must NOT inherit them — those strings
        // are agent-specific (e.g. 'claude-opus-4-7' is not a valid Codex model).
        // Only the primary candidate keeps the item's values.
        var primary = new FakeAgentRunner(AgentKind.Claude);
        var codex = new FakeAgentRunner(AgentKind.Codex);

        var fixture = BuildFixture(
            runners: [primary, codex],
            members: [
                new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                new AgentMembership { Agent = AgentKind.Codex, Billing = AgentBilling.Subscription, QualityScore = 90 },
            ]);

        var item = NewItem(AgentKind.Claude) with
        {
            ModelId = "claude-opus-4-7",
            ReasoningMode = "high",
        };
        await fixture.Store.CreateAsync(item);

        var candidates = await fixture.Pipeline.BuildAgenticConflictCandidatesAsync(item, fixture.Project, primary, CancellationToken.None);

        Assert.Equal(2, candidates.Count);
        // Primary keeps the item's values.
        Assert.Equal(AgentKind.Claude, candidates[0].Runner.Kind);
        Assert.Equal("claude-opus-4-7", candidates[0].ModelId);
        Assert.Equal("high", candidates[0].ReasoningMode);
        // Cross-kind fallback must NOT carry claude-shaped ModelId / ReasoningMode.
        Assert.Equal(AgentKind.Codex, candidates[1].Runner.Kind);
        Assert.Null(candidates[1].ModelId);
        Assert.Null(candidates[1].ReasoningMode);
    }

    [Fact]
    public async Task AllCandidatesBudgetExhausted_ThrowsAgentUnavailable()
    {
        // Primary AND every class-chain fallback have exhausted local budgets
        // (all 5% < MinQuotaPct 10%). The candidate list is empty after the
        // gate so BuildAgenticConflictCandidatesAsync must throw
        // AgentUnavailableException with the per-candidate skip reasons
        // threaded through.
        //
        // This is the post-rework equivalent of the deleted
        // AllAgentsMissingTextOnlyCredentials_FailsWithAgentUnavailable —
        // the gate has moved from "text-only credential availability" (the
        // old text-only resolver path) to "local operator-spend budget" (the
        // current MIN gate), and the exception type/contract is unchanged.
        var primary = new FakeAgentRunner(AgentKind.Claude);
        var codex = new FakeAgentRunner(AgentKind.Codex);
        var gemini = new FakeAgentRunner(AgentKind.Gemini);

        var budgets = new StubBudgetProvider
        {
            [AgentKind.Claude] = 5.0,
            [AgentKind.Codex] = 5.0,
            [AgentKind.Gemini] = 5.0,
        };

        var fixture = BuildFixture(
            runners: [primary, codex, gemini],
            members: [
                new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                new AgentMembership { Agent = AgentKind.Codex, Billing = AgentBilling.Subscription, QualityScore = 90 },
                new AgentMembership { Agent = AgentKind.Gemini, Billing = AgentBilling.Subscription, QualityScore = 80 },
            ],
            budgetProvider: budgets);

        var item = NewItem(AgentKind.Claude);
        await fixture.Store.CreateAsync(item);

        var ex = await Assert.ThrowsAsync<AgentUnavailableException>(() =>
            fixture.Pipeline.BuildAgenticConflictCandidatesAsync(
                item, fixture.Project, primary, CancellationToken.None));

        // Throw type pinned: AgentUnavailableException (NOT
        // MergeConflictResolutionFailedException — the catch in
        // RebaseCheckedOutBranchWithScopeFenceAsync re-raises this type
        // specifically so the work item parks as failureKind=agent_unavailable
        // instead of merge-conflict-resolution-failed).
        Assert.Contains("no agent has viable credentials", ex.Message, StringComparison.Ordinal);
        // Per-candidate skip reasons must be threaded through the throw.
        Assert.Contains("claude:", ex.Message, StringComparison.Ordinal);
        Assert.Contains("codex:", ex.Message, StringComparison.Ordinal);
        Assert.Contains("gemini:", ex.Message, StringComparison.Ordinal);
        // CandidateReasons property exposes the skip-reason concat for upstream logging.
        Assert.Contains("claude:", ex.CandidateReasons, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryBudgetExhausted_ButClassFallbackAvailable_PrimarySkipped()
    {
        // Primary has exhausted budget (5% < 10%) but Codex fallback is
        // healthy. The candidate list contains ONLY Codex — the primary is
        // gated out. This is the defense-in-depth path the budget gate was
        // added to close: don't waste an exhausted subscription on conflict
        // resolution when a healthy fallback exists.
        var primary = new FakeAgentRunner(AgentKind.Claude);
        var codex = new FakeAgentRunner(AgentKind.Codex);

        var budgets = new StubBudgetProvider
        {
            [AgentKind.Claude] = 5.0,   // exhausted
            [AgentKind.Codex] = 80.0,   // healthy
        };

        var fixture = BuildFixture(
            runners: [primary, codex],
            members: [
                new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                new AgentMembership { Agent = AgentKind.Codex, Billing = AgentBilling.Subscription, QualityScore = 90 },
            ],
            budgetProvider: budgets);

        var item = NewItem(AgentKind.Claude);
        await fixture.Store.CreateAsync(item);

        var candidates = await fixture.Pipeline.BuildAgenticConflictCandidatesAsync(
            item, fixture.Project, primary, CancellationToken.None);

        Assert.Single(candidates);
        Assert.Equal(AgentKind.Codex, candidates[0].Runner.Kind);
    }

    [Fact]
    public async Task AtCapCandidates_DeprioritizedButRetainedInOrder()
    {
        // Class chain: Claude (primary) → Codex → Gemini.
        // Codex is at cap (running=2, cap=2). The candidate list should be
        // [Claude, Gemini, Codex] — Codex pushed to the back, primary
        // retains its first slot, Gemini retains relative order before Codex
        // within the "not at cap" bucket.
        var primary = new FakeAgentRunner(AgentKind.Claude);
        var codex = new FakeAgentRunner(AgentKind.Codex);
        var gemini = new FakeAgentRunner(AgentKind.Gemini);

        var counters = new StubAgentRunningCounters
        {
            { AgentKind.Codex, 2 },
        };
        var concurrency = new AgentConcurrencyOptions
        {
            Members =
            {
                [AgentKind.Codex.Value] = new AgentConcurrencyEntry { MaxConcurrent = 2 },
            },
        };

        var fixture = BuildFixture(
            runners: [primary, codex, gemini],
            members: [
                new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                new AgentMembership { Agent = AgentKind.Codex, Billing = AgentBilling.Subscription, QualityScore = 90 },
                new AgentMembership { Agent = AgentKind.Gemini, Billing = AgentBilling.Subscription, QualityScore = 80 },
            ],
            runningCounters: counters,
            agentConcurrency: concurrency);

        var item = NewItem(AgentKind.Claude);
        await fixture.Store.CreateAsync(item);

        var candidates = await fixture.Pipeline.BuildAgenticConflictCandidatesAsync(item, fixture.Project, primary, CancellationToken.None);

        Assert.Equal(3, candidates.Count);
        Assert.Equal(AgentKind.Claude, candidates[0].Runner.Kind);
        Assert.Equal(AgentKind.Gemini, candidates[1].Runner.Kind);
        Assert.Equal(AgentKind.Codex, candidates[2].Runner.Kind);
    }

    [Fact]
    public async Task PrimaryAtCap_StillDeprioritizedRelativeToHealthyClassChain()
    {
        // Primary Claude is at cap; Codex is healthy. The cap-bucket sort
        // pushes Claude to the back so Codex (healthy class chain) is tried
        // FIRST. This prevents the resolver from competing with in-flight
        // work-phase Claude work and 429-ing.
        var primary = new FakeAgentRunner(AgentKind.Claude);
        var codex = new FakeAgentRunner(AgentKind.Codex);

        var counters = new StubAgentRunningCounters
        {
            { AgentKind.Claude, 1 },
        };
        var concurrency = new AgentConcurrencyOptions
        {
            Members =
            {
                [AgentKind.Claude.Value] = new AgentConcurrencyEntry { MaxConcurrent = 1 },
            },
        };

        var fixture = BuildFixture(
            runners: [primary, codex],
            members: [
                new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                new AgentMembership { Agent = AgentKind.Codex, Billing = AgentBilling.Subscription, QualityScore = 90 },
            ],
            runningCounters: counters,
            agentConcurrency: concurrency);

        var item = NewItem(AgentKind.Claude);
        await fixture.Store.CreateAsync(item);

        var candidates = await fixture.Pipeline.BuildAgenticConflictCandidatesAsync(item, fixture.Project, primary, CancellationToken.None);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(AgentKind.Codex, candidates[0].Runner.Kind);
        Assert.Equal(AgentKind.Claude, candidates[1].Runner.Kind);
    }

    [Fact]
    public async Task AllAtCap_OrderPreserved_PrimaryFirst()
    {
        // When EVERYTHING is at cap the primary still leads — the cap-bucket
        // sort preserves index order within each bucket. Confirms the
        // permissive escape hatch: rather than parking with agent_unavailable,
        // the resolver proceeds and accepts a possible 429.
        var primary = new FakeAgentRunner(AgentKind.Claude);
        var codex = new FakeAgentRunner(AgentKind.Codex);

        var counters = new StubAgentRunningCounters
        {
            { AgentKind.Claude, 1 },
            { AgentKind.Codex, 1 },
        };
        var concurrency = new AgentConcurrencyOptions
        {
            Members =
            {
                [AgentKind.Claude.Value] = new AgentConcurrencyEntry { MaxConcurrent = 1 },
                [AgentKind.Codex.Value] = new AgentConcurrencyEntry { MaxConcurrent = 1 },
            },
        };

        var fixture = BuildFixture(
            runners: [primary, codex],
            members: [
                new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                new AgentMembership { Agent = AgentKind.Codex, Billing = AgentBilling.Subscription, QualityScore = 90 },
            ],
            runningCounters: counters,
            agentConcurrency: concurrency);

        var item = NewItem(AgentKind.Claude);
        await fixture.Store.CreateAsync(item);

        var candidates = await fixture.Pipeline.BuildAgenticConflictCandidatesAsync(item, fixture.Project, primary, CancellationToken.None);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(AgentKind.Claude, candidates[0].Runner.Kind);
        Assert.Equal(AgentKind.Codex, candidates[1].Runner.Kind);
    }

    [Fact]
    public async Task NoCapConfigured_RunningCountersIgnored()
    {
        // Sanity: when no cap is configured, the IsAtAgentCap helper returns
        // false even if running counters say "many in flight". Otherwise
        // wiring counters without a cap config would silently change the
        // sort order.
        var primary = new FakeAgentRunner(AgentKind.Claude);
        var codex = new FakeAgentRunner(AgentKind.Codex);

        var counters = new StubAgentRunningCounters
        {
            { AgentKind.Claude, 99 },
            { AgentKind.Codex, 99 },
        };
        // Empty Members → no cap configured → IsAtAgentCap returns false.
        var concurrency = new AgentConcurrencyOptions();

        var fixture = BuildFixture(
            runners: [primary, codex],
            members: [
                new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                new AgentMembership { Agent = AgentKind.Codex, Billing = AgentBilling.Subscription, QualityScore = 90 },
            ],
            runningCounters: counters,
            agentConcurrency: concurrency);

        var item = NewItem(AgentKind.Claude);
        await fixture.Store.CreateAsync(item);

        var candidates = await fixture.Pipeline.BuildAgenticConflictCandidatesAsync(item, fixture.Project, primary, CancellationToken.None);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(AgentKind.Claude, candidates[0].Runner.Kind);
        Assert.Equal(AgentKind.Codex, candidates[1].Runner.Kind);
    }

    [Fact]
    public async Task LocalBudgetExhausted_ClassMemberSkipped()
    {
        // Codex has an exhausted local budget (5% < MinQuotaPct 10%). It must
        // NOT appear in the candidate list — same local-budget MIN gate the
        // audit-agent resolver uses, so a conflict resolver never dispatches
        // to an account already over its operator-configured spend cap.
        var primary = new FakeAgentRunner(AgentKind.Claude);
        var codex = new FakeAgentRunner(AgentKind.Codex);
        var gemini = new FakeAgentRunner(AgentKind.Gemini);

        var budgets = new StubBudgetProvider
        {
            [AgentKind.Codex] = 5.0,   // exhausted, below MinQuotaPct=10
            [AgentKind.Gemini] = 80.0, // healthy
        };

        var fixture = BuildFixture(
            runners: [primary, codex, gemini],
            members: [
                new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                new AgentMembership { Agent = AgentKind.Codex, Billing = AgentBilling.Subscription, QualityScore = 90 },
                new AgentMembership { Agent = AgentKind.Gemini, Billing = AgentBilling.Subscription, QualityScore = 80 },
            ],
            budgetProvider: budgets);

        var item = NewItem(AgentKind.Claude);
        await fixture.Store.CreateAsync(item);

        var candidates = await fixture.Pipeline.BuildAgenticConflictCandidatesAsync(item, fixture.Project, primary, CancellationToken.None);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(AgentKind.Claude, candidates[0].Runner.Kind);
        Assert.Equal(AgentKind.Gemini, candidates[1].Runner.Kind);
        // Codex must NOT be in the list.
        Assert.DoesNotContain(candidates, c => c.Runner.Kind == AgentKind.Codex);
    }

    [Fact]
    public async Task LocalBudgetProviderThrows_FailsClosedAndSkipsCandidate()
    {
        // Budget provider throws for Codex → ReadCandidateBudgetAsync returns
        // (FailedClosed=true). Codex must be dropped from the candidate list
        // — a provider error during the budget check must NOT silently route
        // to a candidate whose budget cannot be verified.
        var primary = new FakeAgentRunner(AgentKind.Claude);
        var codex = new FakeAgentRunner(AgentKind.Codex);
        var gemini = new FakeAgentRunner(AgentKind.Gemini);

        var budgets = new ThrowingBudgetProvider
        {
            ThrowFor = AgentKind.Codex,
            Available = { [AgentKind.Gemini] = 80.0 },
        };

        var fixture = BuildFixture(
            runners: [primary, codex, gemini],
            members: [
                new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                new AgentMembership { Agent = AgentKind.Codex, Billing = AgentBilling.Subscription, QualityScore = 90 },
                new AgentMembership { Agent = AgentKind.Gemini, Billing = AgentBilling.Subscription, QualityScore = 80 },
            ],
            budgetProvider: budgets);

        var item = NewItem(AgentKind.Claude);
        await fixture.Store.CreateAsync(item);

        var candidates = await fixture.Pipeline.BuildAgenticConflictCandidatesAsync(item, fixture.Project, primary, CancellationToken.None);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(AgentKind.Claude, candidates[0].Runner.Kind);
        Assert.Equal(AgentKind.Gemini, candidates[1].Runner.Kind);
        Assert.DoesNotContain(candidates, c => c.Runner.Kind == AgentKind.Codex);
    }

    [Fact]
    public async Task RebaseSmokeGateUsesRebaseSandboxProfileFallback_AndRejectsBenchedResolver()
    {
        var primary = new FakeAgentRunner(AgentKind.Claude);
        var smokeGate = new RejectingTargetInVmSmokeGate(AgentKind.Claude, "audit-agent-profile");

        var fixture = BuildFixture(
            runners: [primary],
            members:
            [
                new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
            ],
            networkProfiles: new ProjectNetworkProfiles
            {
                Work = null,
                AuditAgent = "audit-agent-profile",
                AuditTool = "audit-tool-profile",
                Merge = "merge-profile",
            },
            inVmSmokeGate: smokeGate);

        var item = NewItem(AgentKind.Claude) with { BaselineImageRef = "cb-baseline-pin" };
        await fixture.Store.CreateAsync(item);

        var ex = await Assert.ThrowsAsync<AgentUnavailableException>(() =>
            fixture.Pipeline.BuildAgenticConflictCandidatesAsync(
                item, fixture.Project, primary, CancellationToken.None));

        Assert.Contains("smoke gate", ex.CandidateReasons, StringComparison.Ordinal);
        var call = Assert.Single(smokeGate.Calls);
        Assert.Equal(AgentKind.Claude, call.Kind);
        Assert.Equal("audit-agent-profile", call.Target.NetworkProfile);
        Assert.Equal(SandboxProfileFlavor.Headless, call.Target.Flavor);
        Assert.Equal("cb-baseline-pin", call.Target.BaselineRef);
    }

    // ── Fixture and helpers ────────────────────────────────────────────────

    private Fixture BuildFixture(
        IReadOnlyList<IAgentRunner> runners,
        IReadOnlyList<AgentMembership> members,
        IAgentRunningCounters? runningCounters = null,
        AgentConcurrencyOptions? agentConcurrency = null,
        IAgentBudgetProvider? budgetProvider = null,
        ProjectNetworkProfiles? networkProfiles = null,
        IInVmSmokeGate? inVmSmokeGate = null)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var registry = new AgentRegistry(runners);

        var agentClass = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members = members,
        };
        var router = new AgentClassRouter(
            [agentClass],
            probes: [],
            new QuotaRouterOptions { MinQuotaPct = 10.0 },
            NullLogger<AgentClassRouter>.Instance);

        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = "/nonexistent",
            DefaultBaseBranch = "main",
            DefaultAgent = members[0].Agent,
            DefaultAgentClass = "frontier",
            NetworkProfiles = networkProfiles ?? new ProjectNetworkProfiles(),
            Audit = new ProjectAudit { MaxIterations = 1 },
        };
        var projects = new InMemoryProjectRepository(project);

        var pipeline = new PipelineRunner(
            sandboxes,
            gitHost,
            registry,
            new PermissiveCredentialProvider(),
            prs,
            projects,
            new TestUpstreamFactory(),
            new ProjectAuditorComposer(new ScriptedAuditorCatalog([])),
            store,
            new NullWebhookDispatcher(),
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            classRouter: router,
            agentRunningCounters: runningCounters,
            agentConcurrency: agentConcurrency,
            auditQuotaOptions: new QuotaRouterOptions { MinQuotaPct = 10.0 },
            budgetProvider: budgetProvider,
            inVmSmokeGate: inVmSmokeGate);

        return new Fixture(pipeline, store, project);
    }

    private static WorkItem NewItem(AgentKind agent)
    {
        var id = WorkItemId.New();
        return new WorkItem
        {
            Id = id,
            ProjectId = new ProjectId("test-project"),
            Title = "test",
            Prompt = "do thing",
            BaseBranch = "main",
            WorkBranch = $"codeybox/{id.ToString()[..8]}",
            Agent = agent,
            AgentClassId = "frontier",
            PushUpstream = false,
        };
    }

    private sealed record Fixture(PipelineRunner Pipeline, SqliteWorkItemStore Store, Project Project) : IDisposable
    {
        public void Dispose() => Store.Dispose();
    }

    private sealed class PermissiveCredentialProvider : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
            => Task.FromResult<AgentCredential?>(null);
    }

    private sealed class FakeAgentRunner : IAgentRunner
    {
        public FakeAgentRunner(AgentKind kind) { Kind = kind; }
        public AgentKind Kind { get; }
        public Task<AgentResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false)
            => Task.FromResult(new AgentResult(true, "ok", null, null));
    }

    private sealed class StubAgentRunningCounters
        : Dictionary<AgentKind, int>, IAgentRunningCounters
    {
        public int GetRunning(AgentKind agent) => TryGetValue(agent, out var n) ? n : 0;
        public IReadOnlyDictionary<AgentKind, int> Snapshot()
            => new Dictionary<AgentKind, int>(this);
    }

    private sealed class StubBudgetProvider : Dictionary<AgentKind, double>, IAgentBudgetProvider
    {
        public Task<AgentQuotaSnapshot?> GetBudgetSnapshotAsync(
            AgentKind agent, string? modelId, CancellationToken ct = default)
        {
            if (!TryGetValue(agent, out var pct))
                return Task.FromResult<AgentQuotaSnapshot?>(null);
            return Task.FromResult<AgentQuotaSnapshot?>(
                new AgentQuotaSnapshot { AvailablePct = pct });
        }
        public Task<IReadOnlyList<AgentBudgetUsageView>> SummariseAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentBudgetUsageView>>([]);
    }

    private sealed class ThrowingBudgetProvider : IAgentBudgetProvider
    {
        public AgentKind ThrowFor { get; set; }
        public Dictionary<AgentKind, double> Available { get; } = new();
        public Task<AgentQuotaSnapshot?> GetBudgetSnapshotAsync(
            AgentKind agent, string? modelId, CancellationToken ct = default)
        {
            if (agent.Value == ThrowFor.Value)
                throw new InvalidOperationException("budget provider exploded");
            if (!Available.TryGetValue(agent, out var pct))
                return Task.FromResult<AgentQuotaSnapshot?>(null);
            return Task.FromResult<AgentQuotaSnapshot?>(
                new AgentQuotaSnapshot { AvailablePct = pct });
        }
        public Task<IReadOnlyList<AgentBudgetUsageView>> SummariseAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentBudgetUsageView>>([]);
    }

}
