using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
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
        BuildPipeline(IAgentRunner agent, bool enabled = true, string? repoUrl = null)
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
        var scheduler = new QuotaRetryScheduler(store, retrier, opts, NullLogger<QuotaRetryScheduler>.Instance, router, projects, null, webhooks, _time);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, new TestUpstreamFactory(), new ProjectAuditorComposer(new ScriptedAuditorCatalog([])),
            store, webhooks, new PipelineOptions { SandboxImageReference = "ignored" },
            NullLogger<PipelineRunner>.Instance,
            retryScheduler: scheduler);

        return (pipeline, store, scheduler, webhooks);
    }

    [Fact]
    public void QuotaFailureDetector_DetectsResetTime()
    {
        var stdout = "rate_limit_exceeded reset after 1h";
        var detection = QuotaFailureDetector.Detect(null, stdout);
        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
        Assert.NotNull(detection.ResetAt);
        var diff = detection.ResetAt!.Value - DateTimeOffset.UtcNow;
        Assert.True(diff.TotalMinutes > 59 && diff.TotalMinutes < 61);

        var stderr = "RESOURCE_EXHAUSTED reset after 20h31m6s";
        detection = QuotaFailureDetector.Detect(stderr, null);
        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
        Assert.NotNull(detection.ResetAt);
        diff = detection.ResetAt!.Value - DateTimeOffset.UtcNow;
        Assert.True(diff.TotalHours > 20 && diff.TotalHours < 21);
    }

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

        // Targeted timer runs in background Task.Run, so wait a bit
        await Task.Delay(100);

        var retried = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, retried!.State);
        Assert.Equal(1, retried.QuotaRetryAttempts);
        Assert.Contains(webhooks.Events, e => e.Event == "work_item.auto_retry");
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
    }
}
