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
public sealed class WaitingForQuotaResetTests : IDisposable
{
    private readonly string _workspace;

    public WaitingForQuotaResetTests() =>
        _workspace = Directory.CreateTempSubdirectory("codeybox-waiting-").FullName;

    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

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

        // Background task — give it a moment to complete the retry call.
        await Task.Delay(150);

        var refetched = await store.GetAsync(parked.Id);
        Assert.NotNull(refetched);
        Assert.Equal(WorkItemState.Queued, refetched!.State);
    }

    [Fact]
    public async Task QuotaRetryScheduler_WaitingForQuotaResetWakeupWorksWhenAutoRetryDisabled()
    {
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
        var scheduler = new QuotaRetryScheduler(
            store,
            retrier,
            new OrchestratorOptions(),
            NullLogger<QuotaRetryScheduler>.Instance,
            router,
            projects,
            timeProvider: time);

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

        await scheduler.NotifyQuotaFailureAsync(parked);

        var timersField = typeof(QuotaRetryScheduler).GetField(
            "_targetedTimers",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var timers = (System.Collections.IDictionary)timersField!.GetValue(scheduler)!;
        Assert.True(timers.Contains(parked.Id));

        var fired = typeof(QuotaRetryScheduler).GetMethod(
            "OnTargetedTimerFired",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        fired.Invoke(scheduler, [parked.Id]);
        await Task.Delay(150);

        var refetched = await store.GetAsync(parked.Id);
        Assert.Equal(WorkItemState.Queued, refetched!.State);
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
