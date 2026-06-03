using Microsoft.Extensions.Logging;
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
using CodeyBox.Webhooks;
using Xunit;

namespace CodeyBox.Tests;

public sealed class QuotaAutoRetryTests : IDisposable
{
    private readonly string _workspace;
    private readonly FakeTimeProvider _time;

    public QuotaAutoRetryTests()
    {
        _workspace = Directory.CreateTempSubdirectory("codeybox-quota-retry-").FullName;
        _time = new FakeTimeProvider(DateTimeOffset.UtcNow);
    }

    public void Dispose()
    { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    private sealed class FakeTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public FakeTimeProvider(DateTimeOffset now) => Now = now;
        public override DateTimeOffset GetUtcNow() => Now;

        // Simple timer that doesn't actually fire automatically,
        // we fire it manually in tests if needed.
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            return new FakeTimer(callback, state, dueTime);
        }
    }

    private sealed class FakeTimer : ITimer
    {
        public TimerCallback Callback { get; }
        public object? State { get; }
        public TimeSpan DueTime { get; }

        public FakeTimer(TimerCallback callback, object? state, TimeSpan dueTime)
        {
            Callback = callback;
            State = state;
            DueTime = dueTime;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class QuotaFailingAgent : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Claude;
        public int CallCount { get; private set; }
        public Task<AgentResult> RunAsync(ISandbox sandbox, string workingDirectory,
            string prompt, AgentCredential? credential, string? modelId = null,
            string? reasoningMode = null, CancellationToken ct = default, Action<string>? stdoutChunkCallback = null, bool captureStructuredStream = false)
        {
            CallCount++;
            return Task.FromResult(new AgentResult(false, "agent exited 1", "", "rate_limit_exceeded reset after 1h"));
        }
    }

    private (PipelineRunner pipeline, SqliteWorkItemStore store, QuotaRetryScheduler scheduler, CapturingWebhookDispatcher webhooks)
        BuildPipeline(
            IAgentRunner agent,
            bool enabled = true,
            string? repoUrl = null,
            Func<AutoRetryOnQuotaFailureOptions>? autoRetryOptionsAccessor = null)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var registry = new AgentRegistry([agent]);
        var webhooks = new CapturingWebhookDispatcher();

        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = repoUrl ?? "http://fake",
            DefaultAgent = AgentKind.Claude,
        });

        var opts = new OrchestratorOptions
        {
            AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
            {
                Enabled = enabled,
                PeriodicCheckInterval = TimeSpan.FromHours(1),
                ClockDriftSafetyMargin = TimeSpan.FromMinutes(2),
                MaxAutoRetriesPerWorkItem = 3
            }
        };

        var probes = new List<IAgentQuotaProbe> { new PayPerApiQuotaProbe() }; // PayPerApi always allows
        var classOptions = new List<AgentClass>
        {
            new AgentClass
            {
                Id = "test-class",
                DisplayName = "Test Class",
                Members = [
                    new AgentMembership { Agent = AgentKind.Claude, ModelId = "opus", Billing = AgentBilling.PayPerApi, QualityScore = 100 }
                ]
            }
        };
        var router = new AgentClassRouter(classOptions, probes, new QuotaRouterOptions(), NullLogger<AgentClassRouter>.Instance, _time);

        var taskQueue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(store, taskQueue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var scheduler = new QuotaRetryScheduler(
            store,
            retrier,
            opts,
            NullLogger<QuotaRetryScheduler>.Instance,
            router,
            projects,
            null,
            webhooks,
            _time,
            autoRetryOptionsAccessor: autoRetryOptionsAccessor);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, new TestUpstreamFactory(), new ProjectAuditorComposer(new ScriptedAuditorCatalog([])),
            store, webhooks, new PipelineOptions { SandboxImageReference = "ignored" },
            NullLogger<PipelineRunner>.Instance,
            retryScheduler: scheduler,
            quotaClassifier: BuildQuotaClassifier());

        return (pipeline, store, scheduler, webhooks);
    }

    private QuotaRetryScheduler BuildPayPerApiRetryScheduler(
        SqliteWorkItemStore store,
        string gitRoot,
        Func<AutoRetryOnQuotaFailureOptions>? autoRetryOptionsAccessor = null,
        AutoRetryOnQuotaFailureOptions? retryOptions = null,
        TimeProvider? timeProvider = null,
        ILogger<QuotaRetryScheduler>? logger = null)
    {
        var time = timeProvider ?? _time;
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var taskQueue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(store, taskQueue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = "http://fake",
            DefaultAgent = AgentKind.Claude,
            DefaultAgentClass = "test-class",
        });
        var opts = new OrchestratorOptions
        {
            AutoRetryOnQuotaFailure = retryOptions ?? new AutoRetryOnQuotaFailureOptions
            {
                Enabled = true,
                PeriodicCheckInterval = TimeSpan.FromHours(1),
                ClockDriftSafetyMargin = TimeSpan.FromMinutes(2),
                MaxAutoRetriesPerWorkItem = 3,
            },
        };
        var router = new AgentClassRouter(
            [
                new AgentClass
                {
                    Id = "test-class",
                    DisplayName = "Test Class",
                    Members =
                    [
                        new AgentMembership { Agent = AgentKind.Claude, ModelId = "opus", Billing = AgentBilling.PayPerApi, QualityScore = 100 },
                    ],
                },
            ],
            [new PayPerApiQuotaProbe()],
            new QuotaRouterOptions(),
            NullLogger<AgentClassRouter>.Instance,
            time);

        return new QuotaRetryScheduler(
            store,
            retrier,
            opts,
            logger ?? NullLogger<QuotaRetryScheduler>.Instance,
            router,
            projects,
            null,
            null,
            time,
            autoRetryOptionsAccessor: autoRetryOptionsAccessor);
    }

    [Fact]
    public void QuotaFailureClassifier_DetectsResetTime()
    {
        var classifier = BuildQuotaClassifier();

        var detection = classifier.Detect(AgentKind.Claude, stderr: null, stdout: "rate_limit_exceeded reset after 1h");
        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
        Assert.NotNull(detection.ResetAt);
        var diff = detection.ResetAt!.Value - DateTimeOffset.UtcNow;
        Assert.True(diff.TotalMinutes > 59 && diff.TotalMinutes < 61);

        detection = classifier.Detect(AgentKind.Gemini, stderr: "RESOURCE_EXHAUSTED reset after 20h31m6s", stdout: null);
        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
        Assert.NotNull(detection.ResetAt);
        diff = detection.ResetAt!.Value - DateTimeOffset.UtcNow;
        Assert.True(diff.TotalHours > 20 && diff.TotalHours < 21);
    }

    private static IQuotaFailureClassifier BuildQuotaClassifier() =>
        new CompositeQuotaFailureClassifier(
        [
            new ClaudeQuotaFailureDetector(),
            new CodexQuotaFailureDetector(),
            new GeminiQuotaFailureDetector(),
        ]);

    [Fact]
    public async Task Pipeline_CapturesQuotaFailure_AndNotifiesScheduler()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new QuotaFailingAgent();
        var (pipeline, store, scheduler, webhooks) = BuildPipeline(agent, repoUrl: seed);
        using var _ = store;

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "test",
            Prompt = "do thing",
            Agent = AgentKind.Claude,
        };

        await store.CreateAsync(item);

        await pipeline.RunAsync(item, CancellationToken.None);

        var failed = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, failed!.State);
        Assert.Equal("quota", failed.FailureKind);
        Assert.NotNull(failed.QuotaResetAt);
        Assert.NotNull(failed.NextQuotaRetryAt);
    }

    [Fact]
    public async Task Scheduler_PeriodicSweep_RetriesEligibleItems()
    {
        var agent = new QuotaFailingAgent();
        var (pipeline, store, scheduler, webhooks) = BuildPipeline(agent);
        using var _ = store;

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "test",
            Prompt = "do thing",
            State = WorkItemState.Failed,
            FailureKind = "quota",
            QuotaRetryAttempts = 0,
            AgentClassId = "test-class"
        };
        await store.CreateAsync(item);

        // Run sweep via private method using reflection
        var sweepMethod = typeof(QuotaRetryScheduler).GetMethod("RunPeriodicSweepAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)sweepMethod!.Invoke(scheduler, [CancellationToken.None])!;

        var retried = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, retried!.State);
        Assert.Equal(1, retried.QuotaRetryAttempts);
        Assert.Contains(webhooks.Events, e => e.Event == "work_item.auto_retry");
    }

    [Fact]
    public async Task Scheduler_PeriodicSweep_ProbesRecentlyFailedAgentWhenQuotaRecovered()
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var failuresDb = Path.Combine(_workspace, "quota-failures-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var store = new SqliteWorkItemStore(stateDb);
        using var _ = store;
        using var quotaFailures = new SqliteQuotaFailureStore(failuresDb);
        await quotaFailures.RecordAsync(
            AgentKind.Claude,
            modelId: null,
            QuotaFailureKind.LimitReached,
            _time.Now,
            CancellationToken.None);

        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var taskQueue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(store, taskQueue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var projectId = new ProjectId("test-project");
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = projectId,
            DisplayName = "Test",
            RepositoryUrl = "http://fake",
            DefaultAgentClass = "recent-failure-class",
        });
        var probe = new MutableProbe(AgentKind.Claude, availablePct: 80);
        var router = new AgentClassRouter(
            [
                new AgentClass
                {
                    Id = "recent-failure-class",
                    DisplayName = "Recent Failure",
                    Members =
                    [
                        new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                    ],
                },
            ],
            [probe],
            new QuotaRouterOptions
            {
                MinQuotaPct = 10,
                ObservedFailureWindow = TimeSpan.FromMinutes(10),
            },
            NullLogger<AgentClassRouter>.Instance,
            _time,
            quotaFailures: quotaFailures);
        var scheduler = new QuotaRetryScheduler(
            store,
            retrier,
            new OrchestratorOptions
            {
                AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
                {
                    Enabled = true,
                    PeriodicCheckInterval = TimeSpan.FromHours(1),
                    MaxAutoRetriesPerWorkItem = 3,
                },
            },
            NullLogger<QuotaRetryScheduler>.Instance,
            router,
            projects,
            null,
            null,
            _time);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = projectId,
            Title = "test",
            Prompt = "do thing",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            QuotaRetryAttempts = 0,
            AgentClassId = "recent-failure-class",
            NextQuotaRetryAt = _time.Now.AddHours(1),
        };
        await store.CreateAsync(item);

        var sweepMethod = typeof(QuotaRetryScheduler).GetMethod("RunPeriodicSweepAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)sweepMethod!.Invoke(scheduler, [CancellationToken.None])!;

        var retried = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, retried!.State);
        Assert.Equal(1, retried.QuotaRetryAttempts);
        Assert.Equal(1, probe.CallCount);
    }

    [Fact]
    public async Task Scheduler_RequeuedWaitingItemDispatchesThroughStaleQuotaState()
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var failuresDb = Path.Combine(_workspace, "quota-failures-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);
        using var quotaFailures = new SqliteQuotaFailureStore(failuresDb);
        var projectId = new ProjectId("test-project");
        await quotaFailures.RecordAsync(
            AgentKind.Claude,
            modelId: null,
            QuotaFailureKind.LimitReached,
            _time.Now,
            CancellationToken.None);

        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var taskQueue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(store, taskQueue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var project = new Project
        {
            Id = projectId,
            DisplayName = "Test",
            RepositoryUrl = "http://fake",
            DefaultAgentClass = "recent-failure-class",
        };
        var projects = new InMemoryProjectRepository(project);
        var member = new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 };
        var probe = new MutableProbe(AgentKind.Claude, availablePct: 80);
        var router = new AgentClassRouter(
            [
                new AgentClass
                {
                    Id = "recent-failure-class",
                    DisplayName = "Recent Failure",
                    Members = [member],
                },
            ],
            [probe],
            new QuotaRouterOptions
            {
                MinQuotaPct = 10,
                ObservedFailureWindow = TimeSpan.FromMinutes(10),
            },
            NullLogger<AgentClassRouter>.Instance,
            _time,
            quotaFailures: quotaFailures);
        router.MarkExhausted(member, TimeSpan.FromMinutes(30));

        var blockedWithoutRetryAdmission = await router.ResolveAsync(new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = projectId,
            Title = "blocked",
            Prompt = "p",
            AgentClassId = "recent-failure-class",
        }, project, CancellationToken.None);
        Assert.True(blockedWithoutRetryAdmission.ShouldWait);
        Assert.Null(blockedWithoutRetryAdmission.Chosen);
        Assert.Equal(0, probe.CallCount);

        using var scheduler = new QuotaRetryScheduler(
            store,
            retrier,
            new OrchestratorOptions
            {
                AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
                {
                    Enabled = true,
                    PeriodicCheckInterval = TimeSpan.FromHours(1),
                    MaxAutoRetriesPerWorkItem = 3,
                },
                MaxConcurrentWorkers = 1,
            },
            NullLogger<QuotaRetryScheduler>.Instance,
            router,
            projects,
            null,
            null,
            _time);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = projectId,
            Title = "test",
            Prompt = "do thing",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            QuotaRetryAttempts = 0,
            AgentClassId = "recent-failure-class",
            NextQuotaRetryAt = _time.Now.AddHours(1),
        };
        await store.CreateAsync(item);

        var sweepMethod = typeof(QuotaRetryScheduler).GetMethod(
            "RunPeriodicSweepAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)sweepMethod!.Invoke(scheduler, [CancellationToken.None])!;

        var requeued = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, requeued!.State);
        Assert.Equal(1, requeued.QuotaRetryAttempts);

        using var registry = new CancellationRegistry(CancellationToken.None);
        var tracking = new AgentTrackingPipeline(store);
        using var orchestrator = new OrchestratorService(
            taskQueue,
            store,
            tracking,
            registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            router,
            projects);

        await orchestrator.StartAsync(CancellationToken.None);
        try
        {
            await WaitForConditionAsync(
                () => tracking.LastAgent == AgentKind.Claude,
                TimeSpan.FromSeconds(5));
        }
        finally
        {
            await orchestrator.StopAsync(CancellationToken.None);
        }

        Assert.Equal(AgentKind.Claude, tracking.LastAgent);
        Assert.True(probe.CallCount >= 2);
    }

    [Fact]
    public async Task Scheduler_RespectsMaxRetries()
    {
        var agent = new QuotaFailingAgent();
        var (pipeline, store, scheduler, _) = BuildPipeline(agent);
        using var _ = store;

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "test",
            Prompt = "do thing",
            State = WorkItemState.Failed,
            FailureKind = "quota",
            QuotaRetryAttempts = 3, // Max is 3
            AgentClassId = "test-class"
        };
        await store.CreateAsync(item);

        var sweepMethod = typeof(QuotaRetryScheduler).GetMethod("RunPeriodicSweepAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)sweepMethod!.Invoke(scheduler, [CancellationToken.None])!;

        var notRetried = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, notRetried!.State);
        Assert.Equal(3, notRetried.QuotaRetryAttempts);
    }

    [Fact]
    public async Task Scheduler_UsesLiveEnabledAndMaxRetryOptions()
    {
        var liveOptions = new AutoRetryOnQuotaFailureOptions
        {
            Enabled = false,
            PeriodicCheckInterval = TimeSpan.FromHours(1),
            ClockDriftSafetyMargin = TimeSpan.FromMinutes(2),
            MaxAutoRetriesPerWorkItem = 3,
        };
        var agent = new QuotaFailingAgent();
        var (_, store, scheduler, _) = BuildPipeline(agent, autoRetryOptionsAccessor: () => liveOptions);
        using var _ = store;

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "test",
            Prompt = "do thing",
            State = WorkItemState.Failed,
            FailureKind = "quota",
            QuotaRetryAttempts = 0,
            AgentClassId = "test-class",
        };
        await store.CreateAsync(item);

        var sweepMethod = typeof(QuotaRetryScheduler).GetMethod("RunPeriodicSweepAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)sweepMethod!.Invoke(scheduler, [CancellationToken.None])!;

        var disabled = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, disabled!.State);
        Assert.Equal(0, disabled.QuotaRetryAttempts);

        liveOptions = liveOptions with { Enabled = true, MaxAutoRetriesPerWorkItem = 0 };
        await (Task)sweepMethod.Invoke(scheduler, [CancellationToken.None])!;

        var capped = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, capped!.State);
        Assert.Equal(0, capped.QuotaRetryAttempts);

        liveOptions = liveOptions with { MaxAutoRetriesPerWorkItem = 1 };
        await (Task)sweepMethod.Invoke(scheduler, [CancellationToken.None])!;

        var retried = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, retried!.State);
        Assert.Equal(1, retried.QuotaRetryAttempts);
    }

    [Fact]
    public async Task Scheduler_TargetedTimer_FiresRetry()
    {
        var agent = new QuotaFailingAgent();
        var (pipeline, store, scheduler, webhooks) = BuildPipeline(agent);
        using var _ = store;

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "test",
            Prompt = "do thing",
            State = WorkItemState.Failed,
            FailureKind = "quota",
            QuotaRetryAttempts = 0,
            AgentClassId = "test-class"
        };
        await store.CreateAsync(item);

        // Simulate timer firing
        var timerFiredMethod = typeof(QuotaRetryScheduler).GetMethod("OnTargetedTimerFired", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        timerFiredMethod!.Invoke(scheduler, [item.Id]);

        // Targeted timer runs in background Task.Run, so wait for state change
        var retried = await WaitForStateAsync(store, item.Id, WorkItemState.Queued, TimeSpan.FromSeconds(5));

        Assert.NotNull(retried);
        Assert.Equal(WorkItemState.Queued, retried!.State);
        Assert.Equal(1, retried.QuotaRetryAttempts);

        // The webhook publish happens AFTER the state transition in PerformRetryAsync,
        // so the state change can be visible before the webhook event lands in the
        // capturing dispatcher. Poll briefly for the event.
        await WaitForWebhookAsync(webhooks, "work_item.auto_retry", TimeSpan.FromSeconds(5));
        Assert.Contains(webhooks.Events, e => e.Event == "work_item.auto_retry");
    }

    private static async Task WaitForWebhookAsync(
        CapturingWebhookDispatcher webhooks, string eventName, TimeSpan timeout)
    {
        var start = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - start < timeout)
        {
            if (webhooks.Events.Any(e => e.Event == eventName)) return;
            await Task.Delay(50);
        }
    }

    private static async Task<WorkItem?> WaitForStateAsync(
        IWorkItemStore store, WorkItemId id, WorkItemState target, TimeSpan timeout)
    {
        var start = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - start < timeout)
        {
            var item = await store.GetAsync(id);
            if (item?.State == target) return item;
            await Task.Delay(50);
        }
        return await store.GetAsync(id);
    }

    [Fact]
    public async Task Scheduler_QueuePaused_SuppressesRetry()
    {
        // 1. Setup with a fake QueueController that is paused
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var store = new SqliteWorkItemStore(stateDb);
        using var _ = store;
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var taskQueue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(store, taskQueue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var projects = new InMemoryProjectRepository(new Project { Id = new ProjectId("test-project"), DisplayName = "Test", RepositoryUrl = "http://fake", DefaultAgent = AgentKind.Claude });
        var queueController = new FakeQueueController { State = QueueState.Paused };
        var opts = new OrchestratorOptions { AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions { Enabled = true, PeriodicCheckInterval = TimeSpan.FromHours(1), MaxAutoRetriesPerWorkItem = 3 } };
        var probes = new List<IAgentQuotaProbe> { new PayPerApiQuotaProbe() };
        var classOptions = new List<AgentClass> { new AgentClass { Id = "test-class", DisplayName = "Test", Members = [new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.PayPerApi, QualityScore = 100 }] } };
        var router = new AgentClassRouter(classOptions, probes, new QuotaRouterOptions(), NullLogger<AgentClassRouter>.Instance, _time);
        var scheduler = new QuotaRetryScheduler(store, retrier, opts, NullLogger<QuotaRetryScheduler>.Instance, router, projects, queueController, null, _time);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "test",
            Prompt = "do thing",
            State = WorkItemState.Failed,
            FailureKind = "quota",
            AgentClassId = "test-class"
        };
        await store.CreateAsync(item);

        var sweepMethod = typeof(QuotaRetryScheduler).GetMethod("RunPeriodicSweepAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)sweepMethod!.Invoke(scheduler, [CancellationToken.None])!;

        var notRetried = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, notRetried!.State);
    }

    private sealed class FakeQueueController : IQueueController
    {
        public QueueState State { get; set; } = QueueState.Running;
        public DateTimeOffset? PausedAt => null;
        public string? PausedReason => null;
        public Task PauseAsync(string reason, CancellationToken ct) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task PauseProjectAsync(ProjectId projectId, string reason, CancellationToken ct) => Task.CompletedTask;
        public Task ResumeProjectAsync(ProjectId projectId, CancellationToken ct) => Task.CompletedTask;
        public Task<ProjectQueueState?> GetProjectStateAsync(ProjectId projectId, CancellationToken ct) => Task.FromResult<ProjectQueueState?>(new ProjectQueueState(projectId, State == QueueState.Paused, null, null));
        public Task<IReadOnlyDictionary<string, bool>> GetFleetPauseStatesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<string, bool>>(new Dictionary<string, bool>());
    }

    [Fact]
    public async Task NotifyQuotaFailure_UsesMinResetAcrossExhaustedClassMembers()
    {
        // Build a router with three exhausted subscription members. The failing
        // agent (Gemini) has a 21h reset, but Claude refills in 5h. Park time
        // must be Claude's 5h reset + drift, NOT Gemini's 21h reset.
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var store = new SqliteWorkItemStore(stateDb);
        using var _ = store;
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var taskQueue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(store, taskQueue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var projects = new InMemoryProjectRepository(new Project { Id = new ProjectId("test-project"), DisplayName = "Test", RepositoryUrl = "http://fake", DefaultAgent = AgentKind.Claude });
        var opts = new OrchestratorOptions
        {
            AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
            {
                Enabled = true,
                PeriodicCheckInterval = TimeSpan.FromMinutes(5),
                ClockDriftSafetyMargin = TimeSpan.FromMinutes(2),
                MaxAutoRetriesPerWorkItem = 3,
            }
        };
        var now = _time.Now;
        var claudeReset = now.AddHours(5);
        var codexReset = now.AddHours(1);
        var geminiReset = now.AddHours(21);

        var classOptions = new List<AgentClass>
        {
            new AgentClass
            {
                Id = "codex-xhigh",
                DisplayName = "codex-xhigh",
                Members =
                [
                    new AgentMembership { Agent = AgentKind.Codex, Billing = AgentBilling.Subscription, QualityScore = 100 },
                    new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                    new AgentMembership { Agent = AgentKind.Gemini, Billing = AgentBilling.Subscription, QualityScore = 95, ReasoningMode = "high" },
                ]
            }
        };
        var probes = new List<IAgentQuotaProbe>
        {
            new StaticProbe(AgentKind.Codex, new AgentQuotaSnapshot { AvailablePct = 0, ResetAt = codexReset }),
            new StaticProbe(AgentKind.Claude, new AgentQuotaSnapshot { AvailablePct = 0, ResetAt = claudeReset }),
            new StaticProbe(AgentKind.Gemini, new AgentQuotaSnapshot { AvailablePct = 0, ResetAt = geminiReset }),
        };
        var router = new AgentClassRouter(classOptions, probes, new QuotaRouterOptions(), NullLogger<AgentClassRouter>.Instance, _time);
        var scheduler = new QuotaRetryScheduler(store, retrier, opts, NullLogger<QuotaRetryScheduler>.Instance, router, projects, null, null, _time);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "test",
            Prompt = "do thing",
            State = WorkItemState.Failed,
            FailureKind = "quota",
            AgentClassId = "codex-xhigh",
            Agent = AgentKind.Gemini,
            QuotaResetAt = geminiReset, // last-tried agent
        };
        await store.CreateAsync(item);

        await scheduler.NotifyQuotaFailureAsync(item);

        var updated = await store.GetAsync(item.Id);
        Assert.NotNull(updated!.NextQuotaRetryAt);
        // Earliest exhausted reset is codex's 1h, + 2 min drift margin.
        var expected = codexReset + TimeSpan.FromMinutes(2);
        Assert.Equal(expected, updated.NextQuotaRetryAt);
    }

    [Fact]
    public async Task NotifyQuotaFailure_UsesLiveClockDriftMargin()
    {
        var liveOptions = new AutoRetryOnQuotaFailureOptions
        {
            Enabled = true,
            PeriodicCheckInterval = TimeSpan.FromHours(1),
            ClockDriftSafetyMargin = TimeSpan.FromMinutes(9),
            MaxAutoRetriesPerWorkItem = 3,
        };
        var agent = new QuotaFailingAgent();
        var (_, store, scheduler, _) = BuildPipeline(agent, autoRetryOptionsAccessor: () => liveOptions);
        using var _ = store;

        var resetAt = _time.Now.AddHours(2);
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "test",
            Prompt = "do thing",
            State = WorkItemState.Failed,
            FailureKind = "quota",
            AgentClassId = "test-class",
            Agent = AgentKind.Claude,
            QuotaResetAt = resetAt,
        };
        await store.CreateAsync(item);

        await scheduler.NotifyQuotaFailureAsync(item);

        var updated = await store.GetAsync(item.Id);
        Assert.Equal(resetAt + TimeSpan.FromMinutes(9), updated!.NextQuotaRetryAt);
    }

    [Fact]
    public async Task NotifyQuotaFailure_FallsBackToStartupOptionsWhenLiveAccessorThrows()
    {
        var agent = new QuotaFailingAgent();
        var (_, store, scheduler, _) = BuildPipeline(
            agent,
            autoRetryOptionsAccessor: () => throw new InvalidOperationException("live options unavailable"));
        using var _ = store;

        var resetAt = _time.Now.AddHours(2);
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "test",
            Prompt = "do thing",
            State = WorkItemState.Failed,
            FailureKind = "quota",
            AgentClassId = "test-class",
            Agent = AgentKind.Claude,
            QuotaResetAt = resetAt,
        };
        await store.CreateAsync(item);

        await scheduler.NotifyQuotaFailureAsync(item);

        var updated = await store.GetAsync(item.Id);
        Assert.Equal(resetAt + TimeSpan.FromMinutes(2), updated!.NextQuotaRetryAt);
    }

    [Fact]
    public async Task PeriodicSweep_RetriesEvenWhenNextQuotaRetryAtIsInFuture()
    {
        // Item parked with NextQuotaRetryAt 20h out (gemini's daily). Periodic
        // sweep must still call the router; if the router says ok, retry.
        var agent = new QuotaFailingAgent();
        var (_, store, scheduler, webhooks) = BuildPipeline(agent);
        using var _ = store;

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "test",
            Prompt = "do thing",
            State = WorkItemState.Failed,
            FailureKind = "quota",
            QuotaRetryAttempts = 0,
            AgentClassId = "test-class",
            NextQuotaRetryAt = _time.Now.AddHours(20), // far-future
        };
        await store.CreateAsync(item);

        var sweepMethod = typeof(QuotaRetryScheduler).GetMethod("RunPeriodicSweepAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)sweepMethod!.Invoke(scheduler, [CancellationToken.None])!;

        var retried = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, retried!.State);
        Assert.Equal(1, retried.QuotaRetryAttempts);
        Assert.Contains(webhooks.Events, e => e.Event == "work_item.auto_retry");
    }

    private sealed class StaticProbe : IAgentQuotaProbe
    {
        private readonly AgentQuotaSnapshot _snapshot;
        public StaticProbe(AgentKind kind, AgentQuotaSnapshot snapshot)
        {
            Kind = kind;
            _snapshot = snapshot;
        }
        public AgentKind Kind { get; }
        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct) =>
            Task.FromResult(_snapshot);
    }

    private sealed class MutableProbe : IAgentQuotaProbe
    {
        public MutableProbe(AgentKind kind, double availablePct)
            : this(kind, new AgentQuotaSnapshot { AvailablePct = availablePct })
        {
        }

        public MutableProbe(AgentKind kind, AgentQuotaSnapshot snapshot)
        {
            Kind = kind;
            Snapshot = snapshot;
        }

        public AgentKind Kind { get; }
        public int CallCount { get; private set; }
        public AgentQuotaSnapshot Snapshot { get; set; }
        public double AvailablePct
        {
            get => Snapshot.AvailablePct;
            set => Snapshot = Snapshot with { AvailablePct = value };
        }
        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(Snapshot);
        }
    }

    [Fact]
    public async Task Scheduler_Rearm_LoadsTimersFromDb()
    {
        var agent = new QuotaFailingAgent();
        var (pipeline, store, scheduler, _) = BuildPipeline(agent);
        using var _ = store;

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "test",
            Prompt = "do thing",
            State = WorkItemState.Failed,
            FailureKind = "quota",
            NextQuotaRetryAt = _time.Now.AddHours(1)
        };
        await store.CreateAsync(item);

        var rearmMethod = typeof(QuotaRetryScheduler).GetMethod("RearmTimersAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)rearmMethod!.Invoke(scheduler, [CancellationToken.None])!;

        // Verify timer was created in _targetedTimers
        var timersField = typeof(QuotaRetryScheduler).GetField("_targetedTimers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var timers = (System.Collections.IDictionary)timersField!.GetValue(scheduler)!;
        Assert.True(timers.Contains(item.Id));

        var notYetDue = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, notYetDue!.State);
        Assert.Equal(0, notYetDue.QuotaRetryAttempts);
    }

    [Fact]
    public async Task Scheduler_StartupRearm_ImmediatelyRetriesOverdueWaitingForQuotaResetItem()
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "test",
            Prompt = "do thing",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            QuotaRetryAttempts = 0,
            AgentClassId = "test-class",
            NextQuotaRetryAt = _time.Now.AddDays(-1),
        };
        using (var seedStore = new SqliteWorkItemStore(stateDb))
        {
            await seedStore.CreateAsync(item);
        }

        using var store = new SqliteWorkItemStore(stateDb);
        using var scheduler = BuildPayPerApiRetryScheduler(store, gitRoot);

        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            var retried = await WaitForAttemptsAsync(store, item.Id, expectedAttempts: 1, TimeSpan.FromSeconds(10));
            Assert.Equal(WorkItemState.Queued, retried.State);
            Assert.Equal(1, retried.QuotaRetryAttempts);
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Scheduler_StartupRearm_ReevaluatesFutureWaitingForQuotaResetItem()
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "test",
            Prompt = "do thing",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            QuotaRetryAttempts = 0,
            AgentClassId = "test-class",
            NextQuotaRetryAt = _time.Now.AddHours(12),
        };
        using (var seedStore = new SqliteWorkItemStore(stateDb))
        {
            await seedStore.CreateAsync(item);
        }

        using var store = new SqliteWorkItemStore(stateDb);
        using var scheduler = BuildPayPerApiRetryScheduler(store, gitRoot);

        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            var retried = await WaitForAttemptsAsync(store, item.Id, expectedAttempts: 1, TimeSpan.FromSeconds(10));
            Assert.Equal(WorkItemState.Queued, retried.State);
            Assert.Equal(1, retried.QuotaRetryAttempts);
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Scheduler_StartupRearm_ReevaluatesWaitingForQuotaResetItemWithNoRetryTimestamp()
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "test",
            Prompt = "do thing",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            QuotaRetryAttempts = 0,
            AgentClassId = "test-class",
            NextQuotaRetryAt = null,
        };
        using (var seedStore = new SqliteWorkItemStore(stateDb))
        {
            await seedStore.CreateAsync(item);
        }

        using var store = new SqliteWorkItemStore(stateDb);
        using var scheduler = BuildPayPerApiRetryScheduler(store, gitRoot);

        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            var retried = await WaitForAttemptsAsync(store, item.Id, expectedAttempts: 1, TimeSpan.FromSeconds(10));
            Assert.Equal(WorkItemState.Queued, retried.State);
            Assert.Equal(1, retried.QuotaRetryAttempts);
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Scheduler_HotEnableAfterDisabledStartup_ReevaluatesWaitingForQuotaResetItem()
    {
        var liveOptions = new AutoRetryOnQuotaFailureOptions
        {
            Enabled = false,
            PeriodicCheckInterval = TimeSpan.FromHours(1),
            ClockDriftSafetyMargin = TimeSpan.FromMinutes(2),
            MaxAutoRetriesPerWorkItem = 3,
        };
        var agent = new QuotaFailingAgent();
        var (_, store, scheduler, _) = BuildPipeline(
            agent,
            enabled: false,
            autoRetryOptionsAccessor: () => liveOptions);
        using var _ = store;

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "test",
            Prompt = "do thing",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            QuotaRetryAttempts = 0,
            AgentClassId = "test-class",
            NextQuotaRetryAt = _time.Now.AddHours(12),
        };
        await store.CreateAsync(item);

        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(100);
            var stillParked = await store.GetAsync(item.Id);
            Assert.Equal(WorkItemState.WaitingForQuotaReset, stillParked!.State);
            Assert.Equal(0, stillParked.QuotaRetryAttempts);

            liveOptions = liveOptions with { Enabled = true };

            var retried = await WaitForAttemptsAsync(store, item.Id, expectedAttempts: 1, TimeSpan.FromSeconds(4));
            Assert.Equal(WorkItemState.Queued, retried.State);
            Assert.Equal(1, retried.QuotaRetryAttempts);
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Scheduler_ExecuteAsync_PeriodicTickReevaluatesWaitingForQuotaResetItem()
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        using var store = new SqliteWorkItemStore(stateDb);
        using var scheduler = BuildPayPerApiRetryScheduler(
            store,
            gitRoot,
            retryOptions: new AutoRetryOnQuotaFailureOptions
            {
                Enabled = true,
                PeriodicCheckInterval = TimeSpan.FromMilliseconds(50),
                ClockDriftSafetyMargin = TimeSpan.FromMinutes(2),
                MaxAutoRetriesPerWorkItem = 3,
            },
            timeProvider: TimeProvider.System);

        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(150);
            var item = new WorkItem
            {
                Id = WorkItemId.New(),
                ProjectId = new ProjectId("test-project"),
                Title = "test",
                Prompt = "do thing",
                State = WorkItemState.WaitingForQuotaReset,
                FailureKind = "quota",
                QuotaRetryAttempts = 0,
                AgentClassId = "test-class",
                NextQuotaRetryAt = DateTimeOffset.UtcNow.AddHours(12),
            };
            await store.CreateAsync(item);

            var retried = await WaitForAttemptsAsync(store, item.Id, expectedAttempts: 1, TimeSpan.FromSeconds(5));
            Assert.Equal(WorkItemState.Queued, retried.State);
            Assert.Equal(1, retried.QuotaRetryAttempts);
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Scheduler_ExecuteAsync_InvalidPeriodicIntervalUsesReloadPollFallback()
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var logger = new SignalLogger<QuotaRetryScheduler>("Re-armed or re-evaluated");

        using var store = new SqliteWorkItemStore(stateDb);
        using var scheduler = BuildPayPerApiRetryScheduler(
            store,
            gitRoot,
            retryOptions: new AutoRetryOnQuotaFailureOptions
            {
                Enabled = true,
                PeriodicCheckInterval = TimeSpan.Zero,
                ClockDriftSafetyMargin = TimeSpan.FromMinutes(2),
                MaxAutoRetriesPerWorkItem = 3,
            },
            timeProvider: TimeProvider.System,
            logger: logger);

        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            var rearmObserved = await Task.WhenAny(logger.Seen, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Same(logger.Seen, rearmObserved);

            var item = new WorkItem
            {
                Id = WorkItemId.New(),
                ProjectId = new ProjectId("test-project"),
                Title = "test",
                Prompt = "do thing",
                State = WorkItemState.WaitingForQuotaReset,
                FailureKind = "quota",
                QuotaRetryAttempts = 0,
                AgentClassId = "test-class",
                NextQuotaRetryAt = DateTimeOffset.UtcNow.AddHours(12),
            };
            await store.CreateAsync(item);

            await Task.Delay(200);
            var early = await store.GetAsync(item.Id);
            Assert.Equal(WorkItemState.WaitingForQuotaReset, early!.State);
            Assert.Equal(0, early.QuotaRetryAttempts);

            var retried = await WaitForAttemptsAsync(store, item.Id, expectedAttempts: 1, TimeSpan.FromSeconds(4));
            Assert.Equal(WorkItemState.Queued, retried.State);
            Assert.Equal(1, retried.QuotaRetryAttempts);
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Scheduler_PeriodicSweep_RequeuesWhenEligiblePeerIsAvailable()
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var store = new SqliteWorkItemStore(stateDb);
        using var _ = store;
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var taskQueue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(store, taskQueue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = "http://fake",
            DefaultAgentClass = "mixed-class",
        });
        var router = new AgentClassRouter(
            [
                new AgentClass
                {
                    Id = "mixed-class",
                    DisplayName = "Mixed",
                    Members =
                    [
                        new AgentMembership { Agent = AgentKind.Codex, Billing = AgentBilling.Subscription, QualityScore = 100 },
                        new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                    ],
                },
            ],
            [
                new StaticProbe(AgentKind.Codex, new AgentQuotaSnapshot { AvailablePct = 0 }),
                new StaticProbe(AgentKind.Claude, new AgentQuotaSnapshot { AvailablePct = 75 }),
            ],
            new QuotaRouterOptions { MinQuotaPct = 10 },
            NullLogger<AgentClassRouter>.Instance,
            _time);
        var scheduler = new QuotaRetryScheduler(
            store,
            retrier,
            new OrchestratorOptions
            {
                AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
                {
                    Enabled = true,
                    PeriodicCheckInterval = TimeSpan.FromMinutes(5),
                    MaxAutoRetriesPerWorkItem = 3,
                },
            },
            NullLogger<QuotaRetryScheduler>.Instance,
            router,
            projects,
            null,
            null,
            _time);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "test",
            Prompt = "do thing",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            QuotaRetryAttempts = 0,
            AgentClassId = "mixed-class",
            Agent = AgentKind.Codex,
            NextQuotaRetryAt = _time.Now.AddHours(20),
        };
        await store.CreateAsync(item);

        var sweepMethod = typeof(QuotaRetryScheduler).GetMethod("RunPeriodicSweepAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)sweepMethod!.Invoke(scheduler, [CancellationToken.None])!;

        var retried = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, retried!.State);
        Assert.Equal(1, retried.QuotaRetryAttempts);
    }

    [Fact]
    public async Task Scheduler_StartupRearm_RequeuesStillGatedWaitingItemAfterRestart()
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "test",
            Prompt = "do thing",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            QuotaRetryAttempts = 0,
            AgentClassId = "gated-class",
            NextQuotaRetryAt = _time.Now.AddHours(1),
        };
        using (var seedStore = new SqliteWorkItemStore(stateDb))
        {
            await seedStore.CreateAsync(item);
        }

        using var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var taskQueue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(store, taskQueue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = "http://fake",
            DefaultAgentClass = "gated-class",
        });
        var router = new AgentClassRouter(
            [
                new AgentClass
                {
                    Id = "gated-class",
                    DisplayName = "Gated",
                    Members =
                    [
                        new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                    ],
                },
            ],
            [new StaticProbe(AgentKind.Claude, new AgentQuotaSnapshot { AvailablePct = 0, ResetAt = _time.Now.AddHours(1) })],
            new QuotaRouterOptions { MinQuotaPct = 10 },
            NullLogger<AgentClassRouter>.Instance,
            _time);
        using var scheduler = new QuotaRetryScheduler(
            store,
            retrier,
            new OrchestratorOptions
            {
                AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
                {
                    Enabled = true,
                    PeriodicCheckInterval = TimeSpan.FromMinutes(5),
                    MaxAutoRetriesPerWorkItem = 3,
                },
            },
            NullLogger<QuotaRetryScheduler>.Instance,
            router,
            projects,
            null,
            null,
            _time);

        await scheduler.StartAsync(CancellationToken.None);

        try
        {
            var retried = await WaitForAttemptsAsync(store, item.Id, expectedAttempts: 1, TimeSpan.FromSeconds(10));
            Assert.Equal(WorkItemState.Queued, retried.State);
            Assert.Equal(1, retried.QuotaRetryAttempts);

            var timersField = typeof(QuotaRetryScheduler).GetField("_targetedTimers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var timers = (System.Collections.Concurrent.ConcurrentDictionary<WorkItemId, ITimer>)timersField!.GetValue(scheduler)!;
            Assert.False(timers.ContainsKey(item.Id));
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Scheduler_QuotaUsableSignal_RequeuesWaitingForQuotaResetItem()
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var store = new SqliteWorkItemStore(stateDb);
        using var _ = store;
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var taskQueue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(store, taskQueue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var projectId = new ProjectId("test-project");
        var project = new Project
        {
            Id = projectId,
            DisplayName = "Test",
            RepositoryUrl = "http://fake",
            DefaultAgentClass = "signal-class",
        };
        var projects = new InMemoryProjectRepository(project);
        var probe = new MutableProbe(AgentKind.Claude, availablePct: 0);
        var router = new AgentClassRouter(
            [
                new AgentClass
                {
                    Id = "signal-class",
                    DisplayName = "Signal",
                    Members =
                    [
                        new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                    ],
                },
            ],
            [probe],
            new QuotaRouterOptions { MinQuotaPct = 10 },
            NullLogger<AgentClassRouter>.Instance,
            _time);
        var scheduler = new QuotaRetryScheduler(
            store,
            retrier,
            new OrchestratorOptions
            {
                AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
                {
                    Enabled = true,
                    PeriodicCheckInterval = TimeSpan.FromHours(1),
                    MaxAutoRetriesPerWorkItem = 3,
                },
            },
            NullLogger<QuotaRetryScheduler>.Instance,
            router,
            projects,
            null,
            null,
            _time,
            quotaAvailabilitySignal: router);

        var parked = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = projectId,
            Title = "parked",
            Prompt = "do thing",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            QuotaRetryAttempts = 0,
            AgentClassId = "signal-class",
            NextQuotaRetryAt = _time.Now.AddHours(30),
        };
        await store.CreateAsync(parked);

        var probeItem = parked with { Id = WorkItemId.New(), State = WorkItemState.Queued };
        var firstDecision = await router.ResolveAsync(probeItem, project, CancellationToken.None);
        Assert.True(firstDecision.ShouldWait);

        probe.AvailablePct = 80;
        var secondDecision = await router.ResolveAsync(probeItem with { Id = WorkItemId.New() }, project, CancellationToken.None);
        Assert.NotNull(secondDecision.Chosen);

        var retried = await WaitForAttemptsAsync(store, parked.Id, expectedAttempts: 1, TimeSpan.FromSeconds(5));
        Assert.Equal(WorkItemState.Queued, retried.State);
        Assert.Equal(1, retried.QuotaRetryAttempts);
    }

    [Fact]
    public async Task Router_QuotaUsableSignal_SubscriberExceptionDoesNotBreakResolve()
    {
        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = "http://fake",
            DefaultAgentClass = "subscriber-class",
        };
        var probe = new MutableProbe(AgentKind.Claude, availablePct: 0);
        var router = new AgentClassRouter(
            [
                new AgentClass
                {
                    Id = "subscriber-class",
                    DisplayName = "Subscriber",
                    Members =
                    [
                        new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                    ],
                },
            ],
            [probe],
            new QuotaRouterOptions { MinQuotaPct = 10 },
            NullLogger<AgentClassRouter>.Instance,
            _time);
        router.QuotaUsableThresholdCrossed += () => throw new InvalidOperationException("subscriber failed");

        var probeItem = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = project.Id,
            Title = "probe",
            Prompt = "p",
            AgentClassId = "subscriber-class",
        };
        var firstDecision = await router.ResolveAsync(probeItem, project, CancellationToken.None);
        Assert.True(firstDecision.ShouldWait);

        probe.AvailablePct = 80;
        var secondDecision = await router.ResolveAsync(probeItem with { Id = WorkItemId.New() }, project, CancellationToken.None);

        Assert.NotNull(secondDecision.Chosen);
    }

    [Fact]
    public async Task Scheduler_QuotaUsableSignal_RequeuesWhenWindowFloorRecovers()
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var store = new SqliteWorkItemStore(stateDb);
        using var _ = store;
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var taskQueue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(store, taskQueue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var projectId = new ProjectId("test-project");
        var project = new Project
        {
            Id = projectId,
            DisplayName = "Test",
            RepositoryUrl = "http://fake",
            DefaultAgentClass = "window-signal-class",
        };
        var projects = new InMemoryProjectRepository(project);
        var probe = new MutableProbe(AgentKind.Claude, new AgentQuotaSnapshot
        {
            AvailablePct = 30,
            Windows =
            [
                new WindowQuota { Name = "five_hour", AvailablePct = 20 },
                new WindowQuota { Name = "seven_day", AvailablePct = 80 },
            ],
        });
        var router = new AgentClassRouter(
            [
                new AgentClass
                {
                    Id = "window-signal-class",
                    DisplayName = "Window Signal",
                    Members =
                    [
                        new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                    ],
                },
            ],
            [probe],
            new QuotaRouterOptions
            {
                MinQuotaPct = 10,
                StartFloorPct = 10,
                EndFloorPct = 10,
                MinQuotaPctByWindow = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["five_hour"] = 25,
                    ["seven_day"] = 10,
                },
            },
            NullLogger<AgentClassRouter>.Instance,
            _time);
        using var scheduler = new QuotaRetryScheduler(
            store,
            retrier,
            new OrchestratorOptions
            {
                AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
                {
                    Enabled = true,
                    PeriodicCheckInterval = TimeSpan.FromHours(1),
                    MaxAutoRetriesPerWorkItem = 3,
                },
            },
            NullLogger<QuotaRetryScheduler>.Instance,
            router,
            projects,
            null,
            null,
            _time,
            quotaAvailabilitySignal: router);

        var parked = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = projectId,
            Title = "parked",
            Prompt = "do thing",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            QuotaRetryAttempts = 0,
            AgentClassId = "window-signal-class",
            NextQuotaRetryAt = _time.Now.AddHours(30),
        };
        await store.CreateAsync(parked);

        var probeItem = parked with { Id = WorkItemId.New(), State = WorkItemState.Queued };
        var firstDecision = await router.ResolveAsync(probeItem, project, CancellationToken.None);
        Assert.True(firstDecision.ShouldWait);
        Assert.Null(firstDecision.Chosen);

        probe.Snapshot = new AgentQuotaSnapshot
        {
            AvailablePct = 30,
            Windows =
            [
                new WindowQuota { Name = "five_hour", AvailablePct = 30 },
                new WindowQuota { Name = "seven_day", AvailablePct = 80 },
            ],
        };
        var secondDecision = await router.ResolveAsync(probeItem with { Id = WorkItemId.New() }, project, CancellationToken.None);
        Assert.NotNull(secondDecision.Chosen);

        var retried = await WaitForAttemptsAsync(store, parked.Id, expectedAttempts: 1, TimeSpan.FromSeconds(5));
        Assert.Equal(WorkItemState.Queued, retried.State);
        Assert.Equal(1, retried.QuotaRetryAttempts);
    }

    [Fact]
    public async Task Scheduler_QuotaUsableSignal_DoesNotRequeueWhenAutoRetryDisabled()
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var store = new SqliteWorkItemStore(stateDb);
        using var _ = store;
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var taskQueue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(store, taskQueue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var projectId = new ProjectId("test-project");
        var project = new Project
        {
            Id = projectId,
            DisplayName = "Test",
            RepositoryUrl = "http://fake",
            DefaultAgentClass = "disabled-signal-class",
        };
        var projects = new InMemoryProjectRepository(project);
        var probe = new MutableProbe(AgentKind.Claude, availablePct: 0);
        var router = new AgentClassRouter(
            [
                new AgentClass
                {
                    Id = "disabled-signal-class",
                    DisplayName = "Disabled Signal",
                    Members =
                    [
                        new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                    ],
                },
            ],
            [probe],
            new QuotaRouterOptions { MinQuotaPct = 10 },
            NullLogger<AgentClassRouter>.Instance,
            _time);
        using var scheduler = new QuotaRetryScheduler(
            store,
            retrier,
            new OrchestratorOptions
            {
                AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
                {
                    Enabled = false,
                    PeriodicCheckInterval = TimeSpan.FromHours(1),
                    MaxAutoRetriesPerWorkItem = 3,
                },
            },
            NullLogger<QuotaRetryScheduler>.Instance,
            router,
            projects,
            null,
            null,
            _time,
            quotaAvailabilitySignal: router);

        var signalCount = 0;
        router.QuotaUsableThresholdCrossed += () => System.Threading.Interlocked.Increment(ref signalCount);

        var parked = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = projectId,
            Title = "parked",
            Prompt = "do thing",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            QuotaRetryAttempts = 0,
            AgentClassId = "disabled-signal-class",
            NextQuotaRetryAt = _time.Now.AddHours(30),
        };
        await store.CreateAsync(parked);

        var probeItem = parked with { Id = WorkItemId.New(), State = WorkItemState.Queued };
        var firstDecision = await router.ResolveAsync(probeItem, project, CancellationToken.None);
        Assert.True(firstDecision.ShouldWait);

        probe.AvailablePct = 80;
        var secondDecision = await router.ResolveAsync(probeItem with { Id = WorkItemId.New() }, project, CancellationToken.None);
        Assert.NotNull(secondDecision.Chosen);
        await WaitForConditionAsync(
            () => System.Threading.Volatile.Read(ref signalCount) > 0,
            TimeSpan.FromSeconds(2));

        await Task.Delay(250);
        var stillParked = await store.GetAsync(parked.Id);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, stillParked!.State);
        Assert.Equal(0, stillParked.QuotaRetryAttempts);
    }

    [Fact]
    public async Task Scheduler_Rearm_OverdueWaitingItemRetriesImmediatelyAndCancelsTimer()
    {
        var agent = new QuotaFailingAgent();
        var (_, store, scheduler, _) = BuildPipeline(agent);
        using var _ = store;

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "test",
            Prompt = "do thing",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            QuotaRetryAttempts = 0,
            AgentClassId = "test-class",
            NextQuotaRetryAt = _time.Now.AddHours(-130),
        };
        await store.CreateAsync(item);

        var rearmMethod = typeof(QuotaRetryScheduler).GetMethod("RearmTimersAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)rearmMethod!.Invoke(scheduler, [CancellationToken.None])!;

        var retried = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, retried!.State);
        Assert.Equal(1, retried.QuotaRetryAttempts);

        var timersField = typeof(QuotaRetryScheduler).GetField("_targetedTimers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var timers = (System.Collections.Concurrent.ConcurrentDictionary<WorkItemId, ITimer>)timersField!.GetValue(scheduler)!;
        Assert.False(timers.ContainsKey(item.Id));
    }

    private sealed class SignalLogger<T> : ILogger<T>
    {
        private readonly string _needle;
        private readonly TaskCompletionSource _seen = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SignalLogger(string needle) => _needle = needle;

        public Task Seen => _seen.Task;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (message.Contains(_needle, StringComparison.Ordinal))
                _seen.TrySetResult();
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private static async Task<WorkItem> WaitForAttemptsAsync(
        IWorkItemStore store,
        WorkItemId id,
        int expectedAttempts,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var item = await store.GetAsync(id);
            if (item is not null && item.QuotaRetryAttempts >= expectedAttempts)
                return item;
            await Task.Delay(25);
        }

        var latest = await store.GetAsync(id);
        throw new TimeoutException(
            $"Work item {id} did not reach quotaRetryAttempts={expectedAttempts}; latest={latest?.QuotaRetryAttempts}");
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(25);
        }

        throw new TimeoutException("Condition was not satisfied before the timeout elapsed");
    }
}
