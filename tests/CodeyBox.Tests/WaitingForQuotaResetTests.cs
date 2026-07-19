using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using Xunit;

namespace CodeyBox.Tests;

/// <summary>
/// Coverage for the safety nets that keep WaitingForQuotaReset items isolated
/// from the regular dispatch / recovery / in-flight code paths until the
/// quota retry scheduler decides to re-enqueue them. These cases all guard
/// silent regressions where a small one-line change (deleting a branch or
/// flipping a literal) would cause a parked item to be re-dispatched and
/// immediately re-fail with the same quota error.
/// </summary>
[Collection("Background service timing")]
public sealed class WaitingForQuotaResetTests : IDisposable
{
    private readonly string _workspace;

    public WaitingForQuotaResetTests() =>
        _workspace = Directory.CreateTempSubdirectory("codeybox-waiting-").FullName;

    public void Dispose() { CodeyBox.Tests.TestTempArtifacts.DeleteDirectory(_workspace); }

    [Fact]
    public void WorkItemWith_TransitionToWaitingForQuotaReset_PreservesQuotaFields()
    {
        // The retry scheduler re-arms targeted timers across host restart from
        // QuotaResetAt + NextQuotaRetryAt; both must survive the .With() call
        // when the next state is also a quota-shaped state.
        var resetAt = DateTimeOffset.UtcNow.AddHours(1);
        var nextRetryAt = DateTimeOffset.UtcNow.AddMinutes(30);
        var initial = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p1"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Working,
            QuotaResetAt = resetAt,
            NextQuotaRetryAt = nextRetryAt,
            FailureKind = null,
        };

        var transitioned = initial.With(
            WorkItemState.WaitingForQuotaReset, "all members exhausted",
            failureKind: "quota", quotaResetAt: resetAt);

        Assert.Equal(WorkItemState.WaitingForQuotaReset, transitioned.State);
        Assert.Equal("quota", transitioned.FailureKind);
        Assert.Equal(resetAt, transitioned.QuotaResetAt);
        // Critical: NextQuotaRetryAt must NOT be cleared. The retry scheduler
        // uses this field on restart to decide whether to re-arm a targeted
        // timer or rely on the periodic sweep.
        Assert.Equal(nextRetryAt, transitioned.NextQuotaRetryAt);
    }

    [Fact]
    public void WorkItemWith_TransitionToNonQuotaState_ClearsQuotaFields()
    {
        // Symmetric guard: transitioning back to Queued for a retry must clear
        // FailureKind / QuotaResetAt / NextQuotaRetryAt so the scheduler
        // doesn't see a stale "still parked" record on the next pickup.
        var initial = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p1"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.WaitingForQuotaReset,
            QuotaResetAt = DateTimeOffset.UtcNow,
            NextQuotaRetryAt = DateTimeOffset.UtcNow.AddMinutes(15),
            FailureKind = "quota",
        };

        var transitioned = initial.With(WorkItemState.Queued);

        Assert.Equal(WorkItemState.Queued, transitioned.State);
        Assert.Null(transitioned.FailureKind);
        Assert.Null(transitioned.QuotaResetAt);
        Assert.Null(transitioned.NextQuotaRetryAt);
    }

    [Fact]
    public async Task SqliteWorkItemStore_CountInFlight_ExcludesWaitingForQuotaReset()
    {
        // A WaitingForQuotaReset item must not count against the project's
        // concurrent in-flight cap — otherwise the cap could be permanently
        // saturated by items waiting on a quota window.
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);
        var pid = new ProjectId("p1");

        var parked = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = pid,
            Title = "parked",
            Prompt = "p",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            QuotaResetAt = DateTimeOffset.UtcNow.AddHours(1),
            NextQuotaRetryAt = DateTimeOffset.UtcNow.AddMinutes(10),
            StartedAt = DateTimeOffset.UtcNow,
        };
        await store.CreateAsync(parked);

        var inflight = await store.CountInFlightAsync(pid);
        Assert.Equal(0, inflight);

        // Sanity contrast: a Working item with the same StartedAt does count.
        var working = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = pid,
            Title = "working",
            Prompt = "p",
            State = WorkItemState.Working,
            StartedAt = DateTimeOffset.UtcNow,
        };
        await store.CreateAsync(working);

        Assert.Equal(1, await store.CountInFlightAsync(pid));
    }

    [Fact]
    public async Task QuotaRetryScheduler_PeriodicSweep_ReEnqueuesWaitingForQuotaResetItem()
    {
        // The other half of the spec test case "all members exhausted → item
        // moves to WaitingForQuotaReset; periodic probe re-enqueues": once a
        // class member becomes available again, the periodic sweep must lift
        // the parked item back to Queued so a worker can pick it up.
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);

        var time = new FixedClock(DateTimeOffset.UtcNow);
        var pid = new ProjectId("p1");
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = pid,
            DisplayName = "p1",
            RepositoryUrl = "http://fake",
            DefaultAgent = AgentKind.Claude,
            DefaultAgentClass = "test-class",
        });

        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var taskQueue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(store, taskQueue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var webhooks = new CapturingWebhookDispatcher();

        var classes = new List<AgentClass>
        {
            new AgentClass
            {
                Id = "test-class",
                DisplayName = "Test",
                Members =
                [
                    new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.PayPerApi, QualityScore = 100 },
                ],
            },
        };
        var router = new AgentClassRouter(classes, [new PayPerApiQuotaProbe()],
            new QuotaRouterOptions(), NullLogger<AgentClassRouter>.Instance, time);
        var opts = new OrchestratorOptions
        {
            AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
            {
                Enabled = true,
                PeriodicCheckInterval = TimeSpan.FromHours(1),
                MaxAutoRetriesPerWorkItem = 3,
            },
        };
        var scheduler = new QuotaRetryScheduler(store, retrier, opts,
            NullLogger<QuotaRetryScheduler>.Instance, router, projects, null, webhooks, time);

        var parked = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = pid,
            Title = "parked",
            Prompt = "p",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            AgentClassId = "test-class",
            QuotaResetAt = time.GetUtcNow().AddMinutes(-5), // already due
            NextQuotaRetryAt = time.GetUtcNow().AddMinutes(-1),
        };
        await store.CreateAsync(parked);

        // Trigger the periodic sweep via the same reflection hook the existing
        // tests use; the real loop calls this every PeriodicCheckInterval.
        var sweep = typeof(QuotaRetryScheduler).GetMethod(
            "RunPeriodicSweepAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)sweep.Invoke(scheduler, [CancellationToken.None])!;

        var refetched = await store.GetAsync(parked.Id);
        Assert.NotNull(refetched);
        Assert.Equal(WorkItemState.Queued, refetched!.State);
        Assert.Equal(1, refetched.QuotaRetryAttempts);
        Assert.Contains(webhooks.Events, e => e.Event == "work_item.auto_retry");
    }

    [Fact]
    public void OrchestratorService_StartupRecovery_DoesNotRecoverWaitingForQuotaReset()
    {
        // WaitingForQuotaReset must be a resting point on startup recovery —
        // the QuotaRetryScheduler is the sole owner. If TryBuildRecoveredState
        // returned non-null for this state, the recovery loop would burn a
        // RecoveryAttempt credit and re-enqueue the item, which would then
        // re-fail with the same quota error inside the pipeline.
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);
        var queue = new InMemoryTaskQueue();
        var registry = new CancellationRegistry(CancellationToken.None);
        var pipeline = new FakePipelineRunner(store);
        var svc = new OrchestratorService(queue, store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p1"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            QuotaResetAt = DateTimeOffset.UtcNow.AddHours(1),
            NextQuotaRetryAt = DateTimeOffset.UtcNow.AddMinutes(30),
            RecoveryAttempts = 0,
        };

        var recovered = svc.TryBuildRecoveredStateForTest(item);
        Assert.Null(recovered);
    }

    [Fact]
    public async Task OrchestratorService_Dispatch_SkipsWaitingForQuotaResetItem()
    {
        // Even if an over-eager test (or external caller) directly enqueues a
        // WaitingForQuotaReset item, the dispatch path must reject it without
        // running the pipeline. Without this gate, an enqueued item would be
        // re-run and immediately re-fail with the same quota error.
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);
        var queue = new InMemoryTaskQueue();
        var registry = new CancellationRegistry(CancellationToken.None);
        var pipeline = new FakePipelineRunner(store);
        var svc = new OrchestratorService(queue, store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p1"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            QuotaResetAt = DateTimeOffset.UtcNow.AddHours(1),
            NextQuotaRetryAt = DateTimeOffset.UtcNow.AddMinutes(30),
        };
        await store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);

        await svc.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        await svc.StopAsync(CancellationToken.None);

        // Pipeline must not have executed it.
        Assert.DoesNotContain(item.Id, pipeline.Executed);
        // State unchanged — the worker logged "skipping" and returned without
        // touching the row.
        var refetched = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, refetched!.State);
    }

    [Fact]
    public async Task QuotaRetryScheduler_PeriodicSweep_ReEnqueuesAuditParkedItem_WhenQuotaRecovers()
    {
        // Regression for stranded WaitingForQuotaReset rows parked at audit:
        // when the audit-phase fallback exhausts every class member, the item
        // parks with QuotaRetryFrom="audit". The periodic sweep must walk it
        // and resume at WorkComplete (the audit-phase resume slot) without
        // requiring operator intervention, once an eligible class member is
        // available again. Six items were observed stranded for ~4 days in
        // production because only manual /retry moved them out.
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);

        var time = new FixedClock(DateTimeOffset.UtcNow);
        var pid = new ProjectId("p1");
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = pid,
            DisplayName = "p1",
            RepositoryUrl = "http://fake",
            DefaultAgent = AgentKind.Claude,
            DefaultAgentClass = "test-class",
        });

        // Stub git host so the audit-phase resume path (which requires the
        // bare repo + work branch to exist) does not auto-fall back to
        // from=work. The point of the test is that QuotaRetryFrom="audit"
        // round-trips through the sweep without operator action.
        var gitHost = new StubResumeGitHost();
        var taskQueue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(store, taskQueue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var webhooks = new CapturingWebhookDispatcher();

        var classes = new List<AgentClass>
        {
            new AgentClass
            {
                Id = "test-class",
                DisplayName = "Test",
                Members =
                [
                    new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                ],
            },
        };
        // Probe reports 100% — quota has fully recovered.
        var router = new AgentClassRouter(classes, [new FakeProbe(AgentKind.Claude, 100)],
            new QuotaRouterOptions { MinQuotaPct = 10 }, NullLogger<AgentClassRouter>.Instance, time);
        // Simulate the production state: when the item parked, the work-phase
        // call site marked the failing member exhausted in the router's
        // in-process cache. The periodic sweep must walk past that suppression
        // entry when an external probe now says the agent is usable; otherwise
        // a recovered agent stays benched until the (potentially hours-long)
        // resetAt elapses and items never resume — the production symptom.
        router.MarkExhausted(
            classes[0].Members[0],
            TimeSpan.FromHours(5),
            resetAt: time.GetUtcNow().AddHours(5));
        var opts = new OrchestratorOptions
        {
            AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
            {
                Enabled = true,
                PeriodicCheckInterval = TimeSpan.FromMinutes(5),
                MaxAutoRetriesPerWorkItem = 3,
            },
        };
        var scheduler = new QuotaRetryScheduler(store, retrier, opts,
            NullLogger<QuotaRetryScheduler>.Instance, router, projects, null, webhooks, time);

        var workBranch = "codeybox/audit-parked";
        var parked = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = pid,
            Title = "audit parked",
            Prompt = "p",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            AgentClassId = "test-class",
            // Item parked at the audit phase, e.g. via TerminalQuotaError
            // thrown while routing the LLM auditor agent. The resume slot for
            // from="audit" is WorkComplete.
            QuotaRetryFrom = "audit",
            WorkBranch = workBranch,
            QuotaResetAt = time.GetUtcNow().AddMinutes(-5), // window already elapsed
            NextQuotaRetryAt = time.GetUtcNow().AddMinutes(-1),
            LastError = "All 1 eligible member(s) of class 'test-class' exhausted mid-audit",
        };
        await store.CreateAsync(parked);
        gitHost.MarkRepoAndBranchPresent(parked.Id, workBranch);

        var sweep = typeof(QuotaRetryScheduler).GetMethod(
            "RunPeriodicSweepAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)sweep.Invoke(scheduler, [CancellationToken.None])!;

        var refetched = await store.GetAsync(parked.Id);
        Assert.NotNull(refetched);
        // Audit-from items resume at WorkComplete so the pipeline re-enters
        // the audit phase rather than discarding prior work-phase commits.
        Assert.Equal(WorkItemState.WorkComplete, refetched!.State);
        Assert.Null(refetched.FailureKind);
        Assert.Null(refetched.QuotaResetAt);
        Assert.Null(refetched.NextQuotaRetryAt);
        Assert.Equal(1, refetched.QuotaRetryAttempts);
        Assert.Contains(webhooks.Events, e => e.Event == "work_item.auto_retry");
    }

    [Fact]
    public async Task QuotaRetryScheduler_TargetedTimer_FiresForWaitingForQuotaReset()
    {
        // Sanity that the timer path (not just the periodic sweep) treats
        // WaitingForQuotaReset as eligible. NotifyQuotaFailureAsync schedules
        // a timer; when it fires we expect the parked item to be retried.
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);

        var time = new FixedClock(DateTimeOffset.UtcNow);
        var pid = new ProjectId("p1");
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = pid,
            DisplayName = "p1",
            RepositoryUrl = "http://fake",
            DefaultAgent = AgentKind.Claude,
            DefaultAgentClass = "test-class",
        });

        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var taskQueue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(store, taskQueue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var webhooks = new CapturingWebhookDispatcher();

        var classes = new List<AgentClass>
        {
            new AgentClass
            {
                Id = "test-class",
                DisplayName = "Test",
                Members =
                [
                    new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.PayPerApi, QualityScore = 100 },
                ],
            },
        };
        var router = new AgentClassRouter(classes, [new PayPerApiQuotaProbe()],
            new QuotaRouterOptions(), NullLogger<AgentClassRouter>.Instance, time);
        var opts = new OrchestratorOptions
        {
            AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
            {
                Enabled = true,
                PeriodicCheckInterval = TimeSpan.FromHours(1),
                MaxAutoRetriesPerWorkItem = 3,
            },
        };
        var scheduler = new QuotaRetryScheduler(store, retrier, opts,
            NullLogger<QuotaRetryScheduler>.Instance, router, projects, null, webhooks, time);

        var parked = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = pid,
            Title = "parked",
            Prompt = "p",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            AgentClassId = "test-class",
            QuotaResetAt = time.GetUtcNow().AddMinutes(-5),
            NextQuotaRetryAt = time.GetUtcNow().AddMinutes(-1),
        };
        await store.CreateAsync(parked);

        var fired = typeof(QuotaRetryScheduler).GetMethod(
            "OnTargetedTimerFired",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        fired.Invoke(scheduler, [parked.Id]);

        var refetched = await WaitForStateAsync(store, parked.Id, WorkItemState.Queued);
        Assert.NotNull(refetched);
        Assert.Equal(WorkItemState.Queued, refetched!.State);
    }

    private static async Task<WorkItem?> WaitForStateAsync(
        IWorkItemStore store,
        WorkItemId id,
        WorkItemState expected,
        int attempts = 100)
    {
        WorkItem? current = null;
        for (var i = 0; i < attempts; i++)
        {
            current = await store.GetAsync(id);
            if (current?.State == expected)
                return current;
            await Task.Delay(25);
        }
        return current;
    }

    private sealed class FixedClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
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
