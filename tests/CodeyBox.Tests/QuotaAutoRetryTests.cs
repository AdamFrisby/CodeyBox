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

[Collection("Background service timing")]
public sealed class QuotaAutoRetryTests : IDisposable
{
    private static readonly TimeSpan DispatchObservationTimeout = TimeSpan.FromSeconds(30);
    private readonly string _workspace;
    private readonly FakeTimeProvider _time;

    public QuotaAutoRetryTests()
    {
        _workspace = Directory.CreateTempSubdirectory("codeybox-quota-retry-").FullName;
        _time = new FakeTimeProvider(DateTimeOffset.UtcNow);
    }

    public void Dispose()
    { CodeyBox.Tests.TestTempArtifacts.DeleteDirectory(_workspace); }

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
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);
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
            retryScheduler: new WorkItemAutoRetryScheduler(scheduler, transient: null),
            quotaClassifier: BuildQuotaClassifier(),
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);

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

        // A quota rejection must never hard-Fail the work item: it parks as
        // WaitingForQuotaReset so QuotaRetryScheduler re-dispatches it once
        // the agent (or a class peer) is available again.
        var parked = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, parked!.State);
        Assert.Equal("quota", parked.FailureKind);
        Assert.NotNull(parked.QuotaResetAt);
        Assert.NotNull(parked.NextQuotaRetryAt);
    }

    [Fact]
    public async Task CompositeAutoRetryScheduler_NotifyQuotaFailure_DelegatesToQuotaScheduler()
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);
        using var quotaScheduler = BuildPayPerApiRetryScheduler(
            store,
            gitRoot,
            retryOptions: new AutoRetryOnQuotaFailureOptions
            {
                Enabled = true,
                ClockDriftSafetyMargin = TimeSpan.FromMinutes(2),
                PeriodicCheckInterval = TimeSpan.FromHours(1),
                MaxAutoRetriesPerWorkItem = 3,
            });
        IWorkItemAutoRetryScheduler scheduler = new WorkItemAutoRetryScheduler(quotaScheduler, transient: null);
        var resetAt = _time.Now.AddMinutes(13);
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "quota delegate",
            Prompt = "p",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            QuotaResetAt = resetAt,
            NextQuotaRetryAt = null,
        };
        await store.CreateAsync(item);

        await scheduler.NotifyQuotaFailureAsync(item);

        var stored = await store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(resetAt.AddMinutes(2), stored!.NextQuotaRetryAt);
    }

    [Fact]
    public async Task PeriodicSweep_MovesRecoveredButPausedQuotaItemToWaitingForAgentResume()
    {
        var stateDb = Path.Combine(_workspace, $"quota-paused-{Guid.NewGuid():N}.db");
        var pauseDb = Path.Combine(_workspace, $"quota-pauses-{Guid.NewGuid():N}.db");
        using var store = new SqliteWorkItemStore(stateDb);
        using var pauses = new SqliteAgentPauseController(
            pauseDb,
            NullLogger<SqliteAgentPauseController>.Instance,
            _time);
        await pauses.PauseAsync(AgentKind.Claude, "maintenance", "test");

        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var queue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(store, queue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = "http://fake",
            DefaultAgentClass = "test-class",
        });
        var router = new AgentClassRouter(
            [
                new AgentClass
                {
                    Id = "test-class",
                    DisplayName = "Test Class",
                    Members =
                    [
                        new AgentMembership
                        {
                            Agent = AgentKind.Claude,
                            Billing = AgentBilling.Subscription,
                            QualityScore = 100,
                        },
                    ],
                },
            ],
            [new FakeProbe(AgentKind.Claude, 100)],
            new QuotaRouterOptions { MinQuotaPct = 10 },
            NullLogger<AgentClassRouter>.Instance,
            _time,
            dispatchAvailability: new AgentDispatchAvailability(pauses: pauses));
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
            timeProvider: _time);
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "quota paused",
            Prompt = "p",
            AgentClassId = "test-class",
            State = WorkItemState.WaitingForQuotaReset,
            QuotaRetryFrom = "audit",
            NextQuotaRetryAt = _time.Now.AddMinutes(-1),
        };
        await store.CreateAsync(item);

        var sweepMethod = typeof(QuotaRetryScheduler).GetMethod(
            "RunPeriodicSweepAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)sweepMethod.Invoke(scheduler, [CancellationToken.None])!;

        var parked = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WaitingForAgentResume, parked!.State);
        Assert.Equal("audit", parked.AgentPauseRetryFrom);
        Assert.Null(parked.QuotaRetryFrom);
        Assert.Contains("waiting: agent paused", parked.LastError);
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
    public async Task Scheduler_StartupRearm_RequeuedItemDispatchesThroughStaleQuotaState()
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
            DefaultAgentClass = "startup-recent-failure-class",
        };
        var projects = new InMemoryProjectRepository(project);
        var member = new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 };
        var probe = new MutableProbe(AgentKind.Claude, availablePct: 80);
        var router = new AgentClassRouter(
            [
                new AgentClass
                {
                    Id = "startup-recent-failure-class",
                    DisplayName = "Startup Recent Failure",
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
            AgentClassId = "startup-recent-failure-class",
        }, project, CancellationToken.None);
        Assert.True(blockedWithoutRetryAdmission.ShouldWait);
        Assert.Null(blockedWithoutRetryAdmission.Chosen);
        Assert.Equal(0, probe.CallCount);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = projectId,
            Title = "test",
            Prompt = "do thing",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            QuotaRetryAttempts = 0,
            AgentClassId = "startup-recent-failure-class",
            NextQuotaRetryAt = _time.Now.AddHours(1),
        };
        await store.CreateAsync(item);

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

        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            var requeued = await WaitForAttemptsAsync(store, item.Id, expectedAttempts: 1, TimeSpan.FromSeconds(10));
            Assert.Equal(WorkItemState.Queued, requeued.State);

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
                await tracking.Ran.WaitAsync(DispatchObservationTimeout);
            }
            finally
            {
                await orchestrator.StopAsync(CancellationToken.None);
            }

            Assert.Equal(AgentKind.Claude, tracking.LastAgent);
            Assert.True(probe.CallCount >= 2);
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
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
            await tracking.Ran.WaitAsync(DispatchObservationTimeout);
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

    [Fact]
    public async Task PeriodicSweep_PagesPastStillExhaustedPriorityPrefix()
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var taskQueue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(store, taskQueue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var projectId = new ProjectId("periodic-backstop-project");
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = projectId,
            DisplayName = "Periodic backstop",
            RepositoryUrl = "http://fake",
            DefaultAgent = AgentKind.Claude,
        });
        var router = new AgentClassRouter(
            [],
            [
                new StaticProbe(AgentKind.Claude, new AgentQuotaSnapshot { AvailablePct = 0 }),
                new StaticProbe(AgentKind.Codex, new AgentQuotaSnapshot { AvailablePct = 80 }),
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
                    PeriodicCheckInterval = TimeSpan.FromHours(1),
                    MaxAutoRetriesPerWorkItem = 3,
                    MaxWaitingForQuotaResetSweepBatchSize = 2,
                },
            },
            NullLogger<QuotaRetryScheduler>.Instance,
            router,
            projects,
            timeProvider: _time);

        var firstExhausted = NewWaitingQuotaItem(projectId, "exhausted high 1", AgentKind.Claude, priority: 100, createdAt: _time.Now.AddMinutes(-3));
        var secondExhausted = NewWaitingQuotaItem(projectId, "exhausted high 2", AgentKind.Claude, priority: 90, createdAt: _time.Now.AddMinutes(-2));
        var recoveredLower = NewWaitingQuotaItem(projectId, "recovered low", AgentKind.Codex, priority: 1, createdAt: _time.Now.AddMinutes(-1));
        await store.CreateAsync(firstExhausted);
        await store.CreateAsync(secondExhausted);
        await store.CreateAsync(recoveredLower);

        var sweepMethod = typeof(QuotaRetryScheduler).GetMethod(
            "RunPeriodicSweepAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)sweepMethod!.Invoke(scheduler, [CancellationToken.None])!;

        var first = await store.GetAsync(firstExhausted.Id);
        var second = await store.GetAsync(secondExhausted.Id);
        var retried = await store.GetAsync(recoveredLower.Id);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, first!.State);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, second!.State);
        Assert.Equal(0, first.QuotaRetryAttempts);
        Assert.Equal(0, second.QuotaRetryAttempts);
        Assert.Equal(WorkItemState.Queued, retried!.State);
        Assert.Equal(1, retried.QuotaRetryAttempts);
    }

    [Fact]
    public async Task Scheduler_QuotaUsableSignal_RequeuesDirectDefaultAgentPastPriorityPrefix()
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var taskQueue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(store, taskQueue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var projectId = new ProjectId("default-agent-recovery-project");
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = projectId,
            DisplayName = "Default agent recovery",
            RepositoryUrl = "http://fake",
            DefaultAgent = AgentKind.Codex,
        });
        var quotaSignal = new AgentQuotaAvailabilityBroadcaster(
            NullLogger<AgentQuotaAvailabilityBroadcaster>.Instance);
        var codexMember = new AgentMembership
        {
            Agent = AgentKind.Codex,
            Billing = AgentBilling.Subscription,
            QualityScore = 100,
        };
        var router = new AgentClassRouter(
            [],
            [
                new StaticProbe(AgentKind.Claude, new AgentQuotaSnapshot { AvailablePct = 0 }),
                new StaticProbe(AgentKind.Codex, new AgentQuotaSnapshot { AvailablePct = 80 }),
            ],
            new QuotaRouterOptions { MinQuotaPct = 10 },
            NullLogger<AgentClassRouter>.Instance,
            _time,
            quotaAvailabilityPublisher: quotaSignal);
        using var scheduler = new QuotaRetryScheduler(
            store,
            retrier,
            new OrchestratorOptions
            {
                AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
                {
                    Enabled = true,
                    PeriodicCheckInterval = TimeSpan.FromHours(6),
                    MaxAutoRetriesPerWorkItem = 3,
                    MaxWaitingForQuotaResetSweepBatchSize = 2,
                },
            },
            NullLogger<QuotaRetryScheduler>.Instance,
            router,
            projects,
            null,
            null,
            _time,
            quotaAvailabilitySignal: quotaSignal);

        var firstOtherAgent = NewWaitingQuotaItem(projectId, "other high 1", AgentKind.Claude, priority: 100, createdAt: _time.Now.AddMinutes(-3));
        var secondOtherAgent = NewWaitingQuotaItem(projectId, "other high 2", AgentKind.Claude, priority: 90, createdAt: _time.Now.AddMinutes(-2));
        var defaultAgentItem = NewWaitingQuotaItem(projectId, "default codex low", agent: null, priority: 1, createdAt: _time.Now.AddMinutes(-1));
        await store.CreateAsync(firstOtherAgent);
        await store.CreateAsync(secondOtherAgent);
        await store.CreateAsync(defaultAgentItem);

        quotaSignal.RecordQuotaUsability(codexMember, isUsable: false, resetAt: _time.Now.AddDays(7));
        quotaSignal.RecordQuotaUsability(codexMember, isUsable: true);

        var retried = await WaitForAttemptsAsync(store, defaultAgentItem.Id, expectedAttempts: 1, TimeSpan.FromSeconds(5));
        Assert.Equal(WorkItemState.Queued, retried.State);

        var first = await store.GetAsync(firstOtherAgent.Id);
        var second = await store.GetAsync(secondOtherAgent.Id);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, first!.State);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, second!.State);
        Assert.Equal(0, first.QuotaRetryAttempts);
        Assert.Equal(0, second.QuotaRetryAttempts);
    }

    private WorkItem NewWaitingQuotaItem(
        ProjectId projectId,
        string title,
        AgentKind? agent,
        int priority,
        DateTimeOffset createdAt) =>
        new()
        {
            Id = WorkItemId.New(),
            ProjectId = projectId,
            Title = title,
            Prompt = "do thing",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            QuotaRetryAttempts = 0,
            Agent = agent,
            Priority = priority,
            CreatedAt = createdAt,
            NextQuotaRetryAt = _time.Now.AddDays(7),
        };

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

    private sealed class ManualQuotaAvailabilitySignal : IAgentQuotaAvailabilitySignal
    {
        public event Action? QuotaUsableThresholdCrossed;
        public event Action<AgentQuotaMemberKey>? QuotaMemberUsableThresholdCrossed;
        public void Fire() => QuotaUsableThresholdCrossed?.Invoke();
        public void Fire(AgentQuotaMemberKey member) => QuotaMemberUsableThresholdCrossed?.Invoke(member);
    }

    private sealed class SignalDuringFirstRetryRouter : IQuotaRetryRouter
    {
        private readonly Action _signalDuringFirstCall;
        private int _resolveCalls;

        public SignalDuringFirstRetryRouter(Action signalDuringFirstCall)
            => _signalDuringFirstCall = signalDuringFirstCall;

        public int ResolveCalls => Volatile.Read(ref _resolveCalls);

        public Task<QuotaRetryRoutingDecision> ResolveQuotaRetryAsync(
            WorkItem item,
            Project? project,
            CancellationToken ct,
            string? requiredCapability = null)
        {
            var call = Interlocked.Increment(ref _resolveCalls);
            if (call == 1)
            {
                _signalDuringFirstCall();
                return Task.FromResult(new QuotaRetryRoutingDecision(
                    ShouldWait: true,
                    NoEligibleMembers: false,
                    Reason: "first pass still gated",
                    WaitingForPausedAgent: false));
            }

            return Task.FromResult(new QuotaRetryRoutingDecision(
                ShouldWait: false,
                NoEligibleMembers: false,
                Reason: "second pass available",
                WaitingForPausedAgent: false));
        }

        public Task<DateTimeOffset?> ComputeEarliestExhaustedResetAsync(
            WorkItem item,
            Project? project,
            CancellationToken ct,
            string? requiredCapability = null)
            => Task.FromResult<DateTimeOffset?>(null);
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

    private sealed class CachingMutableProbe : IAgentQuotaProbe, IAgentQuotaCacheInvalidator
    {
        private AgentQuotaSnapshot? _cached;

        public CachingMutableProbe(AgentKind kind, double availablePct)
            : this(kind, new AgentQuotaSnapshot { AvailablePct = availablePct })
        {
        }

        public CachingMutableProbe(AgentKind kind, AgentQuotaSnapshot snapshot)
        {
            Kind = kind;
            Snapshot = snapshot;
        }

        public AgentKind Kind { get; }
        public int CallCount { get; private set; }
        public int Invalidations { get; private set; }
        public AgentQuotaSnapshot Snapshot { get; set; }
        public double AvailablePct
        {
            get => Snapshot.AvailablePct;
            set => Snapshot = Snapshot with { AvailablePct = value };
        }

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
        {
            if (_cached is null)
            {
                CallCount++;
                _cached = Snapshot;
            }

            return Task.FromResult(_cached);
        }

        public void InvalidateCache()
        {
            Invalidations++;
            _cached = null;
        }
    }

    private sealed class ThrowOnceThenMutableProbe : IAgentQuotaProbe
    {
        private bool _throwNext = true;

        public ThrowOnceThenMutableProbe(AgentKind kind, double availablePct)
        {
            Kind = kind;
            AvailablePct = availablePct;
        }

        public AgentKind Kind { get; }
        public int CallCount { get; private set; }
        public double AvailablePct { get; set; }

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
        {
            CallCount++;
            if (_throwNext)
            {
                _throwNext = false;
                throw new InvalidOperationException("probe unavailable");
            }

            return Task.FromResult(new AgentQuotaSnapshot { AvailablePct = AvailablePct });
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
            await tracking.Ran.WaitAsync(DispatchObservationTimeout);
        }
        finally
        {
            await orchestrator.StopAsync(CancellationToken.None);
        }

        Assert.Equal(AgentKind.Claude, tracking.LastAgent);
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
        var quotaSignal = new AgentQuotaAvailabilityBroadcaster(
            NullLogger<AgentQuotaAvailabilityBroadcaster>.Instance);
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
            _time,
            quotaAvailabilityPublisher: quotaSignal);
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
            quotaAvailabilitySignal: quotaSignal);

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
    public async Task Scheduler_AgentAvailabilityRecoverySignal_RequeuesWaitingForQuotaResetItem()
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var store = new SqliteWorkItemStore(stateDb);
        using var _ = store;
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var taskQueue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(store, taskQueue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var projectId = new ProjectId("availability-recovery-project");
        var project = new Project
        {
            Id = projectId,
            DisplayName = "Availability Recovery",
            RepositoryUrl = "http://fake",
            DefaultAgentClass = "availability-recovery-class",
        };
        var projects = new InMemoryProjectRepository(project);
        var availability = new AgentAvailabilityRegistry(
            new AvailabilityOptions(),
            _time,
            NullLogger<AgentAvailabilityRegistry>.Instance);
        var dispatchAvailability = new AgentDispatchAvailability(availability);
        var probe = new MutableProbe(AgentKind.Codex, availablePct: 80);
        var router = new AgentClassRouter(
            [
                new AgentClass
                {
                    Id = "availability-recovery-class",
                    DisplayName = "Availability Recovery",
                    Members =
                    [
                        new AgentMembership { Agent = AgentKind.Codex, Billing = AgentBilling.Subscription, QualityScore = 100 },
                    ],
                },
            ],
            [probe],
            new QuotaRouterOptions { MinQuotaPct = 10 },
            NullLogger<AgentClassRouter>.Instance,
            _time,
            dispatchAvailability: dispatchAvailability);
        using var scheduler = new QuotaRetryScheduler(
            store,
            retrier,
            new OrchestratorOptions
            {
                AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
                {
                    Enabled = true,
                    PeriodicCheckInterval = TimeSpan.FromHours(6),
                    MaxAutoRetriesPerWorkItem = 3,
                },
            },
            NullLogger<QuotaRetryScheduler>.Instance,
            router,
            projects,
            null,
            null,
            _time,
            agentAvailabilityRecoverySignal: availability);

        availability.MarkSmokeResult(
            AgentKind.Codex,
            new AgentSmokeResult(false, "quota probe exhausted", TimeSpan.Zero));

        var parked = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = projectId,
            Title = "parked",
            Prompt = "do thing",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            QuotaRetryAttempts = 0,
            AgentClassId = "availability-recovery-class",
            NextQuotaRetryAt = _time.Now.AddDays(7),
        };
        await store.CreateAsync(parked);

        availability.MarkSmokeResult(
            AgentKind.Codex,
            new AgentSmokeResult(true, null, TimeSpan.Zero));

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
        var quotaSignal = new AgentQuotaAvailabilityBroadcaster(
            NullLogger<AgentQuotaAvailabilityBroadcaster>.Instance);
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
            _time,
            quotaAvailabilityPublisher: quotaSignal);
        quotaSignal.QuotaUsableThresholdCrossed += () => throw new InvalidOperationException("subscriber failed");

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
        var quotaSignal = new AgentQuotaAvailabilityBroadcaster(
            NullLogger<AgentQuotaAvailabilityBroadcaster>.Instance);
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
            _time,
            quotaAvailabilityPublisher: quotaSignal);
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
            quotaAvailabilitySignal: quotaSignal);

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
    public async Task Scheduler_QuotaRecoveryProbeMonitor_RequeuesWaitingForQuotaResetItem()
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
            DefaultAgentClass = "probe-signal-class",
        };
        var projects = new InMemoryProjectRepository(project);
        var routerOptions = new QuotaRouterOptions
        {
            MinQuotaPct = 10,
            QuotaRecoveryProbeInterval = TimeSpan.FromMilliseconds(50),
        };
        var quotaSignal = new AgentQuotaAvailabilityBroadcaster(
            NullLogger<AgentQuotaAvailabilityBroadcaster>.Instance);
        var member = new AgentMembership
        {
            Agent = AgentKind.Codex,
            Billing = AgentBilling.Subscription,
            QualityScore = 100,
        };
        var probe = new CachingMutableProbe(AgentKind.Codex, availablePct: 0);
        var router = new AgentClassRouter(
            [
                new AgentClass
                {
                    Id = "probe-signal-class",
                    DisplayName = "Probe Signal",
                    Members = [member],
                },
            ],
            [probe],
            routerOptions,
            NullLogger<AgentClassRouter>.Instance,
            _time,
            quotaAvailabilityPublisher: quotaSignal);
        using var monitor = new AgentQuotaRecoveryProbeMonitor(
            quotaSignal,
            quotaSignal,
            [probe],
            new QuotaGateAvailability(new QuotaGatePolicy(routerOptions)),
            routerOptions,
            NullLogger<AgentQuotaRecoveryProbeMonitor>.Instance,
            _time,
            store,
            projects,
            router);
        using var scheduler = new QuotaRetryScheduler(
            store,
            retrier,
            new OrchestratorOptions
            {
                AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
                {
                    Enabled = true,
                    PeriodicCheckInterval = TimeSpan.FromHours(6),
                    MaxAutoRetriesPerWorkItem = 3,
                },
            },
            NullLogger<QuotaRetryScheduler>.Instance,
            router,
            projects,
            null,
            null,
            _time,
            quotaAvailabilitySignal: quotaSignal);

        var parked = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = projectId,
            Title = "parked",
            Prompt = "do thing",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            QuotaRetryAttempts = 0,
            AgentClassId = "probe-signal-class",
            NextQuotaRetryAt = _time.Now.AddDays(7),
        };
        await store.CreateAsync(parked);

        var signalCount = 0;
        quotaSignal.QuotaUsableThresholdCrossed += () => System.Threading.Interlocked.Increment(ref signalCount);

        router.MarkExhausted(member, TimeSpan.FromHours(6), _time.Now.AddDays(7));
        Assert.Equal(0, System.Threading.Volatile.Read(ref signalCount));

        var stillParked = await store.GetAsync(parked.Id);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, stillParked!.State);
        Assert.Equal(0, stillParked.QuotaRetryAttempts);

        await monitor.StartAsync(CancellationToken.None);
        try
        {
            await WaitForConditionAsync(() => probe.CallCount > 0, TimeSpan.FromSeconds(2));
            Assert.Equal(0, System.Threading.Volatile.Read(ref signalCount));

            stillParked = await store.GetAsync(parked.Id);
            Assert.Equal(WorkItemState.WaitingForQuotaReset, stillParked!.State);
            Assert.Equal(0, stillParked.QuotaRetryAttempts);

            probe.AvailablePct = 80;
            _time.Now = _time.Now.Add(routerOptions.QuotaRecoveryProbeInterval);
            var retried = await WaitForAttemptsAsync(store, parked.Id, expectedAttempts: 1, TimeSpan.FromSeconds(5));
            Assert.Equal(WorkItemState.Queued, retried.State);
            Assert.Equal(1, retried.QuotaRetryAttempts);
            Assert.Equal(1, System.Threading.Volatile.Read(ref signalCount));
            Assert.True(probe.Invalidations > 0);
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task QuotaRecoveryProbeMonitor_SubscriberFailureKeepsMemberTrackedForNextProbe()
    {
        var routerOptions = new QuotaRouterOptions
        {
            MinQuotaPct = 10,
            QuotaRecoveryProbeInterval = TimeSpan.FromMilliseconds(50),
        };
        var quotaSignal = new AgentQuotaAvailabilityBroadcaster(
            NullLogger<AgentQuotaAvailabilityBroadcaster>.Instance);
        var member = new AgentMembership
        {
            Agent = AgentKind.Codex,
            Billing = AgentBilling.Subscription,
            QualityScore = 100,
        };
        var probe = new CachingMutableProbe(AgentKind.Codex, availablePct: 0);
        using var monitor = new AgentQuotaRecoveryProbeMonitor(
            quotaSignal,
            quotaSignal,
            [probe],
            new QuotaGateAvailability(new QuotaGatePolicy(routerOptions)),
            routerOptions,
            NullLogger<AgentQuotaRecoveryProbeMonitor>.Instance,
            _time);
        var signalCount = 0;
        void ThrowingSubscriber()
        {
            System.Threading.Interlocked.Increment(ref signalCount);
            throw new InvalidOperationException("subscriber failed");
        }

        quotaSignal.QuotaUsableThresholdCrossed += ThrowingSubscriber;
        quotaSignal.RecordQuotaUsability(member, isUsable: false);
        probe.AvailablePct = 80;

        Assert.Equal(0, await monitor.ProbeTrackedMembersOnceAsync(CancellationToken.None));
        Assert.Equal(1, System.Threading.Volatile.Read(ref signalCount));

        quotaSignal.QuotaUsableThresholdCrossed -= ThrowingSubscriber;
        quotaSignal.QuotaUsableThresholdCrossed += () => System.Threading.Interlocked.Increment(ref signalCount);

        Assert.Equal(1, await monitor.ProbeTrackedMembersOnceAsync(CancellationToken.None));
        Assert.Equal(2, System.Threading.Volatile.Read(ref signalCount));
    }

    [Fact]
    public async Task QuotaRecoveryProbeMonitor_ProbeExceptionKeepsMemberTrackedForLaterRecovery()
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var store = new SqliteWorkItemStore(stateDb);
        using var _ = store;
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var taskQueue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(store, taskQueue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var projectId = new ProjectId("probe-exception-project");
        var project = new Project
        {
            Id = projectId,
            DisplayName = "Probe exception",
            RepositoryUrl = "http://fake",
            DefaultAgent = AgentKind.Codex,
        };
        var projects = new InMemoryProjectRepository(project);
        var routerOptions = new QuotaRouterOptions
        {
            MinQuotaPct = 10,
            QuotaRecoveryProbeInterval = TimeSpan.FromMilliseconds(50),
        };
        var quotaSignal = new AgentQuotaAvailabilityBroadcaster(
            NullLogger<AgentQuotaAvailabilityBroadcaster>.Instance);
        var member = new AgentMembership
        {
            Agent = AgentKind.Codex,
            Billing = AgentBilling.Subscription,
            QualityScore = 100,
        };
        var probe = new ThrowOnceThenMutableProbe(AgentKind.Codex, availablePct: 80);
        var router = new AgentClassRouter(
            [],
            [probe],
            routerOptions,
            NullLogger<AgentClassRouter>.Instance,
            _time,
            quotaAvailabilityPublisher: quotaSignal);
        using var monitor = new AgentQuotaRecoveryProbeMonitor(
            quotaSignal,
            quotaSignal,
            [probe],
            new QuotaGateAvailability(new QuotaGatePolicy(routerOptions)),
            routerOptions,
            NullLogger<AgentQuotaRecoveryProbeMonitor>.Instance,
            _time,
            store,
            projects,
            router);
        using var scheduler = new QuotaRetryScheduler(
            store,
            retrier,
            new OrchestratorOptions
            {
                AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
                {
                    Enabled = true,
                    PeriodicCheckInterval = TimeSpan.FromHours(6),
                    MaxAutoRetriesPerWorkItem = 3,
                },
            },
            NullLogger<QuotaRetryScheduler>.Instance,
            router,
            projects,
            null,
            null,
            _time,
            quotaAvailabilitySignal: quotaSignal);

        var parked = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = projectId,
            Title = "parked after probe exception",
            Prompt = "do thing",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            Agent = AgentKind.Codex,
            NextQuotaRetryAt = _time.Now.AddDays(7),
        };
        await store.CreateAsync(parked);

        quotaSignal.RecordQuotaUsability(member, isUsable: false, resetAt: _time.Now.AddDays(7));

        Assert.Equal(0, await monitor.ProbeTrackedMembersOnceAsync(CancellationToken.None));
        Assert.Equal(1, probe.CallCount);
        var stillParked = await store.GetAsync(parked.Id);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, stillParked!.State);
        Assert.Equal(0, stillParked.QuotaRetryAttempts);

        Assert.Equal(1, await monitor.ProbeTrackedMembersOnceAsync(CancellationToken.None));
        var retried = await WaitForAttemptsAsync(store, parked.Id, expectedAttempts: 1, TimeSpan.FromSeconds(5));
        Assert.Equal(WorkItemState.Queued, retried.State);
        Assert.True(probe.CallCount >= 2);
    }

    [Fact]
    public async Task QuotaRecoveryProbeMonitor_DirectAgentRecovery_RequeuesOnlyRecoveredAgent()
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var store = new SqliteWorkItemStore(stateDb);
        using var _ = store;
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var taskQueue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(store, taskQueue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var projectId = new ProjectId("direct-quota-project");
        var project = new Project
        {
            Id = projectId,
            DisplayName = "Direct quota",
            RepositoryUrl = "http://fake",
            DefaultAgent = AgentKind.Codex,
        };
        var projects = new InMemoryProjectRepository(project);
        var routerOptions = new QuotaRouterOptions
        {
            MinQuotaPct = 10,
            QuotaRecoveryProbeInterval = TimeSpan.FromMilliseconds(50),
        };
        var quotaSignal = new AgentQuotaAvailabilityBroadcaster(
            NullLogger<AgentQuotaAvailabilityBroadcaster>.Instance);
        var codexMember = new AgentMembership
        {
            Agent = AgentKind.Codex,
            Billing = AgentBilling.Subscription,
            QualityScore = 100,
        };
        var codexProbe = new CachingMutableProbe(AgentKind.Codex, availablePct: 0);
        var claudeProbe = new CachingMutableProbe(AgentKind.Claude, availablePct: 0);
        var router = new AgentClassRouter(
            [],
            [codexProbe, claudeProbe],
            routerOptions,
            NullLogger<AgentClassRouter>.Instance,
            _time,
            quotaAvailabilityPublisher: quotaSignal);
        using var monitor = new AgentQuotaRecoveryProbeMonitor(
            quotaSignal,
            quotaSignal,
            [codexProbe, claudeProbe],
            new QuotaGateAvailability(new QuotaGatePolicy(routerOptions)),
            routerOptions,
            NullLogger<AgentQuotaRecoveryProbeMonitor>.Instance,
            _time,
            store,
            projects,
            router);
        using var scheduler = new QuotaRetryScheduler(
            store,
            retrier,
            new OrchestratorOptions
            {
                AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
                {
                    Enabled = true,
                    PeriodicCheckInterval = TimeSpan.FromHours(6),
                    MaxAutoRetriesPerWorkItem = 3,
                    MaxWaitingForQuotaResetSweepBatchSize = 2,
                },
            },
            NullLogger<QuotaRetryScheduler>.Instance,
            router,
            projects,
            null,
            null,
            _time,
            quotaAvailabilitySignal: quotaSignal);

        var codexParked = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = projectId,
            Title = "codex parked",
            Prompt = "do thing",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            QuotaRetryAttempts = 0,
            Agent = AgentKind.Codex,
            Priority = 1,
            NextQuotaRetryAt = _time.Now.AddDays(7),
        };
        var firstClaudeParked = codexParked with
        {
            Id = WorkItemId.New(),
            Title = "claude parked 1",
            Agent = AgentKind.Claude,
            Priority = 100,
        };
        var secondClaudeParked = codexParked with
        {
            Id = WorkItemId.New(),
            Title = "claude parked 2",
            Agent = AgentKind.Claude,
            Priority = 90,
        };
        await store.CreateAsync(codexParked);
        await store.CreateAsync(firstClaudeParked);
        await store.CreateAsync(secondClaudeParked);

        quotaSignal.RecordQuotaUsability(codexMember, isUsable: false);
        Assert.Equal(0, await monitor.ProbeTrackedMembersOnceAsync(CancellationToken.None));

        var stillCodexParked = await store.GetAsync(codexParked.Id);
        var stillFirstClaudeParked = await store.GetAsync(firstClaudeParked.Id);
        var stillSecondClaudeParked = await store.GetAsync(secondClaudeParked.Id);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, stillCodexParked!.State);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, stillFirstClaudeParked!.State);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, stillSecondClaudeParked!.State);
        Assert.Equal(0, stillFirstClaudeParked.QuotaRetryAttempts);
        Assert.Equal(0, stillSecondClaudeParked.QuotaRetryAttempts);

        codexProbe.AvailablePct = 80;
        _time.Now = _time.Now.Add(routerOptions.QuotaRecoveryProbeInterval);
        Assert.Equal(1, await monitor.ProbeTrackedMembersOnceAsync(CancellationToken.None));

        var retried = await WaitForAttemptsAsync(store, codexParked.Id, expectedAttempts: 1, TimeSpan.FromSeconds(5));
        Assert.Equal(WorkItemState.Queued, retried.State);
        Assert.Equal(1, retried.QuotaRetryAttempts);

        stillFirstClaudeParked = await store.GetAsync(firstClaudeParked.Id);
        stillSecondClaudeParked = await store.GetAsync(secondClaudeParked.Id);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, stillFirstClaudeParked!.State);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, stillSecondClaudeParked!.State);
        Assert.Equal(0, stillFirstClaudeParked.QuotaRetryAttempts);
        Assert.Equal(0, stillSecondClaudeParked.QuotaRetryAttempts);
    }

    [Fact]
    public async Task QuotaRecoveryProbeMonitor_ReworkRetryRequiresAuditCapableRecoveredMember()
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var store = new SqliteWorkItemStore(stateDb);
        using var _ = store;
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var taskQueue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(store, taskQueue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var projectId = new ProjectId("audit-quota-project");
        var project = new Project
        {
            Id = projectId,
            DisplayName = "Audit quota",
            RepositoryUrl = "http://fake",
            DefaultAgentClass = "audit-quota-class",
        };
        var projects = new InMemoryProjectRepository(project);
        var routerOptions = new QuotaRouterOptions
        {
            MinQuotaPct = 10,
            QuotaRecoveryProbeInterval = TimeSpan.FromMilliseconds(50),
        };
        var quotaSignal = new AgentQuotaAvailabilityBroadcaster(
            NullLogger<AgentQuotaAvailabilityBroadcaster>.Instance);
        var codexMember = new AgentMembership
        {
            Agent = AgentKind.Codex,
            Billing = AgentBilling.Subscription,
            QualityScore = 100,
        };
        var claudeAuditMember = new AgentMembership
        {
            Agent = AgentKind.Claude,
            Billing = AgentBilling.Subscription,
            QualityScore = 90,
            Capabilities = [WellKnownCapabilities.Audit],
        };
        var codexProbe = new CachingMutableProbe(AgentKind.Codex, availablePct: 80);
        var claudeProbe = new CachingMutableProbe(AgentKind.Claude, availablePct: 80);
        var router = new AgentClassRouter(
            [
                new AgentClass
                {
                    Id = "audit-quota-class",
                    DisplayName = "Audit quota",
                    Members = [codexMember, claudeAuditMember],
                },
            ],
            [codexProbe, claudeProbe],
            routerOptions,
            NullLogger<AgentClassRouter>.Instance,
            _time,
            quotaAvailabilityPublisher: quotaSignal);
        using var monitor = new AgentQuotaRecoveryProbeMonitor(
            quotaSignal,
            quotaSignal,
            [codexProbe, claudeProbe],
            new QuotaGateAvailability(new QuotaGatePolicy(routerOptions)),
            routerOptions,
            NullLogger<AgentQuotaRecoveryProbeMonitor>.Instance,
            _time,
            store,
            projects,
            router);
        using var scheduler = new QuotaRetryScheduler(
            store,
            retrier,
            new OrchestratorOptions
            {
                AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
                {
                    Enabled = true,
                    PeriodicCheckInterval = TimeSpan.FromHours(6),
                    MaxAutoRetriesPerWorkItem = 3,
                },
            },
            NullLogger<QuotaRetryScheduler>.Instance,
            router,
            projects,
            null,
            null,
            _time,
            quotaAvailabilitySignal: quotaSignal);

        var workItemId = WorkItemId.New();
        var workBranch = "codeybox/rework-" + Guid.NewGuid().ToString("N")[..8];
        await gitHost.EnsureRepositoryAsync(workItemId, seedFromUrl: null, CancellationToken.None);
        await CommitToBareBranchAsync(gitHost.GetRepoPath(workItemId.ToString()), workBranch);

        var parked = new WorkItem
        {
            Id = workItemId,
            ProjectId = projectId,
            Title = "parked rework",
            Prompt = "do thing",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            AgentClassId = "audit-quota-class",
            WorkBranch = workBranch,
            QuotaRetryFrom = RetryFromPolicy.Rework,
            NextQuotaRetryAt = _time.Now.AddDays(7),
        };
        await store.CreateAsync(parked);

        quotaSignal.RecordQuotaUsability(codexMember, isUsable: false, resetAt: _time.Now.AddDays(7));
        quotaSignal.RecordQuotaUsability(claudeAuditMember, isUsable: false, resetAt: _time.Now.AddDays(7));

        Assert.Equal(1, await monitor.ProbeTrackedMembersOnceAsync(CancellationToken.None));
        Assert.Equal(0, codexProbe.CallCount);
        Assert.True(claudeProbe.CallCount > 0);

        var retried = await WaitForAttemptsAsync(store, parked.Id, expectedAttempts: 1, TimeSpan.FromSeconds(5));
        Assert.Equal(WorkItemState.WorkComplete, retried.State);
        Assert.Equal(1, retried.QuotaRetryAttempts);
    }

    [Fact]
    public async Task QuotaRecoveryProbeMonitor_NoEligibleParkedWork_DoesNotProbe()
    {
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);
        var projectId = new ProjectId("direct-no-work-project");
        var project = new Project
        {
            Id = projectId,
            DisplayName = "Direct no work",
            RepositoryUrl = "http://fake",
            DefaultAgent = AgentKind.Claude,
        };
        var projects = new InMemoryProjectRepository(project);
        var routerOptions = new QuotaRouterOptions { MinQuotaPct = 10 };
        var quotaSignal = new AgentQuotaAvailabilityBroadcaster(
            NullLogger<AgentQuotaAvailabilityBroadcaster>.Instance);
        var member = new AgentMembership
        {
            Agent = AgentKind.Codex,
            Billing = AgentBilling.Subscription,
            QualityScore = 100,
        };
        var probe = new CachingMutableProbe(AgentKind.Codex, availablePct: 80);
        var router = new AgentClassRouter(
            [],
            [probe],
            routerOptions,
            NullLogger<AgentClassRouter>.Instance,
            _time,
            quotaAvailabilityPublisher: quotaSignal);
        using var monitor = new AgentQuotaRecoveryProbeMonitor(
            quotaSignal,
            quotaSignal,
            [probe],
            new QuotaGateAvailability(new QuotaGatePolicy(routerOptions)),
            routerOptions,
            NullLogger<AgentQuotaRecoveryProbeMonitor>.Instance,
            _time,
            store,
            projects,
            router);

        await store.CreateAsync(new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = projectId,
            Title = "parked on claude",
            Prompt = "do thing",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            Agent = AgentKind.Claude,
            NextQuotaRetryAt = _time.Now.AddDays(7),
        });

        quotaSignal.RecordQuotaUsability(member, isUsable: false, resetAt: _time.Now.AddDays(7));

        Assert.Equal(0, await monitor.ProbeTrackedMembersOnceAsync(CancellationToken.None));
        Assert.Equal(0, probe.CallCount);
        Assert.Equal(0, probe.Invalidations);
    }

    [Fact]
    public async Task QuotaRecoveryProbeMonitor_DeniedProbeBacksOff()
    {
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);
        var projectId = new ProjectId("direct-backoff-project");
        var project = new Project
        {
            Id = projectId,
            DisplayName = "Direct backoff",
            RepositoryUrl = "http://fake",
            DefaultAgent = AgentKind.Codex,
        };
        var projects = new InMemoryProjectRepository(project);
        var routerOptions = new QuotaRouterOptions
        {
            MinQuotaPct = 10,
            QuotaRecoveryProbeInterval = TimeSpan.FromSeconds(5),
        };
        var quotaSignal = new AgentQuotaAvailabilityBroadcaster(
            NullLogger<AgentQuotaAvailabilityBroadcaster>.Instance);
        var member = new AgentMembership
        {
            Agent = AgentKind.Codex,
            Billing = AgentBilling.Subscription,
            QualityScore = 100,
        };
        var probe = new CachingMutableProbe(AgentKind.Codex, availablePct: 0);
        var router = new AgentClassRouter(
            [],
            [probe],
            routerOptions,
            NullLogger<AgentClassRouter>.Instance,
            _time,
            quotaAvailabilityPublisher: quotaSignal);
        using var monitor = new AgentQuotaRecoveryProbeMonitor(
            quotaSignal,
            quotaSignal,
            [probe],
            new QuotaGateAvailability(new QuotaGatePolicy(routerOptions)),
            routerOptions,
            NullLogger<AgentQuotaRecoveryProbeMonitor>.Instance,
            _time,
            store,
            projects,
            router);

        await store.CreateAsync(new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = projectId,
            Title = "parked on codex",
            Prompt = "do thing",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            Agent = AgentKind.Codex,
            NextQuotaRetryAt = _time.Now.AddDays(7),
        });

        quotaSignal.RecordQuotaUsability(member, isUsable: false, resetAt: _time.Now.AddDays(7));

        Assert.Equal(0, await monitor.ProbeTrackedMembersOnceAsync(CancellationToken.None));
        Assert.Equal(1, probe.CallCount);

        Assert.Equal(0, await monitor.ProbeTrackedMembersOnceAsync(CancellationToken.None));
        Assert.Equal(1, probe.CallCount);

        _time.Now = _time.Now.Add(routerOptions.QuotaRecoveryProbeInterval);
        Assert.Equal(0, await monitor.ProbeTrackedMembersOnceAsync(CancellationToken.None));
        Assert.Equal(2, probe.CallCount);
    }

    [Fact]
    public void QuotaRecoveryProbeMonitor_DuplicateProbeRegistrationFailsFast()
    {
        var quotaSignal = new AgentQuotaAvailabilityBroadcaster(
            NullLogger<AgentQuotaAvailabilityBroadcaster>.Instance);
        var options = new QuotaRouterOptions { MinQuotaPct = 10 };

        Assert.Throws<ArgumentException>(() => new AgentQuotaRecoveryProbeMonitor(
            quotaSignal,
            quotaSignal,
            [
                new MutableProbe(AgentKind.Codex, availablePct: 0),
                new MutableProbe(AgentKind.Codex, availablePct: 100),
            ],
            new QuotaGateAvailability(new QuotaGatePolicy(options)),
            options,
            NullLogger<AgentQuotaRecoveryProbeMonitor>.Instance,
            _time));
    }

    [Fact]
    public async Task QuotaRecoveryProbeMonitor_DelegatesUnknownSnapshotPolicyToGate()
    {
        var options = new QuotaRouterOptions
        {
            MinQuotaPct = 10,
            UnknownPolicy = QuotaUnknownPolicy.FailOpen,
        };
        var quotaSignal = new AgentQuotaAvailabilityBroadcaster(
            NullLogger<AgentQuotaAvailabilityBroadcaster>.Instance);
        var member = new AgentMembership
        {
            Agent = AgentKind.Codex,
            Billing = AgentBilling.Subscription,
            QualityScore = 100,
        };
        var probe = new MutableProbe(
            AgentKind.Codex,
            AgentQuotaSnapshot.UnknownSnapshot(QuotaUnknownReason.Transient, "test unknown"));
        using var monitor = new AgentQuotaRecoveryProbeMonitor(
            quotaSignal,
            quotaSignal,
            [probe],
            new QuotaGateAvailability(new QuotaGatePolicy(options)),
            options,
            NullLogger<AgentQuotaRecoveryProbeMonitor>.Instance,
            _time);
        var signalCount = 0;
        quotaSignal.QuotaUsableThresholdCrossed += () => Interlocked.Increment(ref signalCount);

        quotaSignal.RecordQuotaUsability(member, isUsable: false);

        Assert.Equal(1, await monitor.ProbeTrackedMembersOnceAsync(CancellationToken.None));
        Assert.Equal(1, Volatile.Read(ref signalCount));
    }

    [Fact]
    public async Task Scheduler_QuotaUsableSignalDuringActiveSweep_QueuesFollowUpSweep()
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var store = new SqliteWorkItemStore(stateDb);
        using var _ = store;
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var taskQueue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(store, taskQueue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var projectId = new ProjectId("test-project");
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = projectId,
            DisplayName = "Test",
            RepositoryUrl = "http://fake",
            DefaultAgentClass = "signal-race-class",
        });
        var quotaSignal = new ManualQuotaAvailabilitySignal();
        var router = new SignalDuringFirstRetryRouter(quotaSignal.Fire);
        using var scheduler = new QuotaRetryScheduler(
            store,
            retrier,
            new OrchestratorOptions
            {
                AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
                {
                    Enabled = true,
                    PeriodicCheckInterval = TimeSpan.FromHours(6),
                    MaxAutoRetriesPerWorkItem = 3,
                },
            },
            NullLogger<QuotaRetryScheduler>.Instance,
            router,
            projects,
            null,
            null,
            _time,
            quotaAvailabilitySignal: quotaSignal);
        var parked = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = projectId,
            Title = "parked",
            Prompt = "do thing",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            QuotaRetryAttempts = 0,
            AgentClassId = "signal-race-class",
            NextQuotaRetryAt = _time.Now.AddDays(7),
        };
        await store.CreateAsync(parked);

        quotaSignal.Fire();

        var retried = await WaitForAttemptsAsync(store, parked.Id, expectedAttempts: 1, TimeSpan.FromSeconds(5));
        Assert.Equal(WorkItemState.Queued, retried.State);
        Assert.True(router.ResolveCalls >= 2);
    }

    [Fact]
    public async Task Scheduler_QuotaUsableSignal_RequeuesWaitingItemsInPriorityOrder()
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var taskQueue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(store, taskQueue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var projectId = new ProjectId("priority-recovery-project");
        var project = new Project
        {
            Id = projectId,
            DisplayName = "Priority recovery",
            RepositoryUrl = "http://fake",
            DefaultAgentClass = "priority-recovery-class",
        };
        var projects = new InMemoryProjectRepository(project);
        var routerOptions = new QuotaRouterOptions { MinQuotaPct = 10 };
        var quotaSignal = new AgentQuotaAvailabilityBroadcaster(
            NullLogger<AgentQuotaAvailabilityBroadcaster>.Instance);
        var member = new AgentMembership
        {
            Agent = AgentKind.Codex,
            Billing = AgentBilling.Subscription,
            QualityScore = 100,
        };
        var probe = new MutableProbe(AgentKind.Codex, availablePct: 80);
        var router = new AgentClassRouter(
            [
                new AgentClass
                {
                    Id = "priority-recovery-class",
                    DisplayName = "Priority recovery",
                    Members = [member],
                },
            ],
            [probe],
            routerOptions,
            NullLogger<AgentClassRouter>.Instance,
            _time,
            quotaAvailabilityPublisher: quotaSignal);
        using var scheduler = new QuotaRetryScheduler(
            store,
            retrier,
            new OrchestratorOptions
            {
                AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
                {
                    Enabled = true,
                    PeriodicCheckInterval = TimeSpan.FromHours(6),
                    MaxAutoRetriesPerWorkItem = 3,
                },
            },
            NullLogger<QuotaRetryScheduler>.Instance,
            router,
            projects,
            null,
            null,
            _time,
            quotaAvailabilitySignal: quotaSignal);

        var low = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = projectId,
            Title = "low priority",
            Prompt = "do thing",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            AgentClassId = "priority-recovery-class",
            NextQuotaRetryAt = _time.Now.AddDays(7),
            CreatedAt = _time.Now,
            Priority = 1,
        };
        var high = low with
        {
            Id = WorkItemId.New(),
            Title = "high priority",
            CreatedAt = _time.Now.AddMinutes(1),
            Priority = 100,
        };
        await store.CreateAsync(low);
        await store.CreateAsync(high);

        quotaSignal.RecordQuotaUsability(member, isUsable: false, resetAt: _time.Now.AddDays(7));
        quotaSignal.RecordQuotaUsability(member, isUsable: true);

        var highRetried = await WaitForAttemptsAsync(store, high.Id, expectedAttempts: 1, TimeSpan.FromSeconds(5));
        var lowRetried = await WaitForAttemptsAsync(store, low.Id, expectedAttempts: 1, TimeSpan.FromSeconds(5));
        Assert.Equal(WorkItemState.Queued, highRetried.State);
        Assert.Equal(WorkItemState.Queued, lowRetried.State);

        Assert.Equal(high.Id, await taskQueue.DequeueAsync(CancellationToken.None));
        Assert.Equal(low.Id, await taskQueue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Router_QuotaRetryAdmissionIsConsumedWhenDispatchProbeDenies()
    {
        var failuresDb = Path.Combine(_workspace, "quota-failures-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var quotaFailures = new SqliteQuotaFailureStore(failuresDb);
        var projectId = new ProjectId("test-project");
        await quotaFailures.RecordAsync(
            AgentKind.Claude,
            modelId: null,
            QuotaFailureKind.LimitReached,
            _time.Now,
            CancellationToken.None);

        var project = new Project
        {
            Id = projectId,
            DisplayName = "Test",
            RepositoryUrl = "http://fake",
            DefaultAgentClass = "admission-denial-class",
        };
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = projectId,
            Title = "test",
            Prompt = "do thing",
            State = WorkItemState.Queued,
            AgentClassId = "admission-denial-class",
        };
        var probe = new MutableProbe(AgentKind.Claude, availablePct: 80);
        var router = new AgentClassRouter(
            [
                new AgentClass
                {
                    Id = "admission-denial-class",
                    DisplayName = "Admission Denial",
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

        var retryDecision = await router.ResolveQuotaRetryAsync(item, project, CancellationToken.None);
        Assert.False(retryDecision.ShouldWait);
        Assert.Equal(1, probe.CallCount);

        probe.AvailablePct = 0;
        var deniedDispatch = await router.ResolveAsync(item, project, CancellationToken.None);
        Assert.True(deniedDispatch.ShouldWait);
        Assert.Null(deniedDispatch.Chosen);
        Assert.Equal(2, probe.CallCount);

        probe.AvailablePct = 80;
        var blockedAfterAdmissionConsumed = await router.ResolveAsync(item, project, CancellationToken.None);
        Assert.True(blockedAfterAdmissionConsumed.ShouldWait);
        Assert.Null(blockedAfterAdmissionConsumed.Chosen);
        Assert.Equal(2, probe.CallCount);
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
        var quotaSignal = new AgentQuotaAvailabilityBroadcaster(
            NullLogger<AgentQuotaAvailabilityBroadcaster>.Instance);
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
            _time,
            quotaAvailabilityPublisher: quotaSignal);
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
            quotaAvailabilitySignal: quotaSignal);

        var signalCount = 0;
        quotaSignal.QuotaUsableThresholdCrossed += () => System.Threading.Interlocked.Increment(ref signalCount);

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
        if (latest is not null && latest.QuotaRetryAttempts >= expectedAttempts)
            return latest;

        throw new TimeoutException(
            $"Work item {id} did not reach quotaRetryAttempts={expectedAttempts}; latest={latest?.QuotaRetryAttempts}");
    }

    private async Task CommitToBareBranchAsync(string barePath, string branch)
    {
        var clone = Path.Combine(_workspace, "bare-edit-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", barePath, clone);
        await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(clone, "config", "user.name", "Test");
        await TestSupport.RunGit(clone, "checkout", "-B", branch);
        await File.WriteAllTextAsync(Path.Combine(clone, "work.txt"), "work complete\n");
        await TestSupport.RunGit(clone, "add", "work.txt");
        await TestSupport.RunGit(clone, "commit", "-m", $"work complete\n\n{CodeyBoxTrailers.CoAuthoredBy}");
        await TestSupport.RunGit(clone, "push", "origin", $"{branch}:{branch}");
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
