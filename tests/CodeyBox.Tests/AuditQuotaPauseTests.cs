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
    public async Task AuditLlmQuotaFailure_AllClassMembersExhausted_SkipsLlmAuditorAndContinues()
    {
        // Bug 779e7dc9 (warning-and-skip variant): when every member of the
        // work item's agent class is quota-exhausted for an LLM auditor, the
        // pipeline now skips that auditor for the iteration rather than
        // parking the whole work item. The remaining auditors still run and
        // the item ships with degraded (but non-fatal) audit signal.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new RoutingLlmAuditor("cheating:llm-review", _ => QuotaResult());
        using var fix = BuildFixture(seed, auditor, [AgentKind.Gemini]);
        fix.Gemini.WorkPlan.Enqueue(new FileWrite("work.txt", "done\n"));

        var item = NewItem(AgentKind.Gemini);
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.NotEqual(WorkItemState.WaitingForQuotaReset, final.State);
        Assert.DoesNotContain(fix.Webhooks.Events, e => e.Event == "work_item.waiting_for_quota_reset");
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
    public async Task AuditLlmQuotaFailure_NoLongerParksWhenChainExhausted()
    {
        // Bug 779e7dc9: previously, when the audit-side quota fallback
        // exhausted every class member, the pipeline parked the work item in
        // WaitingForQuotaReset. The preferred behaviour now is warning-and-skip
        // so non-LLM audit signal still completes. This test pins that —
        // even with a class of one (gemini-only) and a quota-failing auditor,
        // the item completes Done and the QuotaRetryScheduler is NOT armed.
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
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Null(final.NextQuotaRetryAt);
        Assert.DoesNotContain(fix.Webhooks.Events, e => e.Event == "work_item.waiting_for_quota_reset");
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
        Assert.Equal("audit", final.QuotaRetryFrom);
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
        Assert.Equal(AgentKind.Codex, final.Agent);
        Assert.Equal("audit", final.QuotaRetryFrom);
        Assert.Empty(auditor.Invocations);
    }

    private AuditQuotaFixture BuildFixture(
        string seedRepoUrl,
        RoutingLlmAuditor auditor,
        IReadOnlyList<AgentKind> classMembers,
        IQuotaFailureStore? quotaFailures = null,
        IAgentPauseController? pauses = null,
        AgentKind? auditAgent = null,
        IReadOnlyDictionary<AgentKind, IReadOnlyList<string>>? capabilities = null,
        IReadOnlySet<AgentKind>? missingCredentials = null)
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
        var probes = classMembers.Select(kind => new RecordingProbe(kind)).ToList();
        var dispatchAvailability = pauses is null
            ? null
            : new AgentDispatchAvailability(pauses: pauses);
        var router = new AgentClassRouter(
            [frontier],
            probes,
            new QuotaRouterOptions { MinQuotaPct = 10 },
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
            new ProjectAuditorComposer(new ScriptedAuditorCatalog([auditor])),
            store,
            webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            auditQuotaProbes: probes,
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

        return new AuditQuotaFixture(pipeline, scheduler, store, queue, webhooks, fallbackHistory, time, gemini, codex);
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
        SqliteWorkItemStore Store,
        InMemoryTaskQueue Queue,
        CapturingWebhookDispatcher Webhooks,
        InMemoryAgentFallbackHistoryStore FallbackHistory,
        ManualClock Time,
        ScriptableAgent Gemini,
        ScriptableAgent Codex) : IDisposable
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
