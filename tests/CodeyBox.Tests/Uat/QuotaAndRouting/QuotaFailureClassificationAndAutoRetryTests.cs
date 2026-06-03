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
using CodeyBox.Tests;
using CodeyBox.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests.Uat.QuotaAndRouting;

/// <summary>
/// UAT coverage for <c>Quota failure classification and auto-retry - Persists quota failures and retries after reset</c>.
/// Plan anchor: docs/uat/00-plan.md#quota-failure-classification-and-auto-retry---persists-quota-failures-and-retries-after-reset
/// </summary>
public sealed class QuotaFailureClassificationAndAutoRetryTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-uat-quota-retry-").FullName;
    private readonly ControlledTimeProvider _time = new(new DateTimeOffset(2026, 5, 13, 10, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    [Theory]
    [InlineData(nameof(AgentKind.Codex), "You hit your usage limit. Try again after 5m.", QuotaFailureKind.LimitReached)]
    [InlineData(nameof(AgentKind.Claude), "rate_limit_exceeded reset after 1h", QuotaFailureKind.RateLimitExceeded)]
    [InlineData(nameof(AgentKind.Gemini), "RESOURCE_EXHAUSTED reset after 20m", QuotaFailureKind.RateLimitExceeded)]
    public void DetectorClassifiesQuotaTextFromAgentOutput(string agentName, string stderr, QuotaFailureKind expected)
    {
        var detection = BuildClassifier().Detect(ResolveAgent(agentName), stderr, stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(expected, detection!.Kind);
    }

    [Fact]
    public void Claude401_IsNotClassifiedAsQuotaEvent_EvenViaUatClassifier()
    {
        // Shared-OAuth refresh race (see commit message): 401 from Claude must
        // not trip the observed-failure breaker, otherwise a single race burns
        // 10 minutes of Claude availability per occurrence.
        Assert.Null(BuildClassifier().Detect(AgentKind.Claude, "API Error: 401 unauthorized", stdout: null));
    }

    [Theory]
    [InlineData(nameof(AgentKind.Codex), """{"msg":{"type":"error","message":"You hit your usage limit. Try again after 5m17s."}}""")]
    [InlineData(nameof(AgentKind.Claude), """{"type":"result","subtype":"error","is_error":true,"result":"Error: rate_limit_exceeded retry after 30m"}""")]
    [InlineData(nameof(AgentKind.Gemini), """{"type":"result","status":"error","error":{"message":"[API Error: You have exhausted your capacity on this model. Your quota will reset after 21h41m24s.]"}}""")]
    public void StructuredStreamJsonWrappedErrorsAreExtracted(string agentName, string stdout)
    {
        var detection = BuildClassifier().Detect(ResolveAgent(agentName), stderr: null, stdout);

        Assert.NotNull(detection);
        Assert.NotNull(detection!.ResetAt);
    }

    [Fact]
    public void ResetDuration_IsDetectedFromQuotaMessage()
    {
        var detection = BuildClassifier().Detect(AgentKind.Gemini,
            stderr: "RESOURCE_EXHAUSTED reset after 20h31m6s", stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
        Assert.NotNull(detection.ResetAt);
    }

    [Fact]
    public void MalformedStreamJson_IsIgnoredAndUnstructuredOutputIsStillScanned()
    {
        var stdout = """
            {this is not valid json}
            {"type":"result","status":"success","result":"ok"}
            quota exceeded retry after 10m
            """;

        var detection = BuildClassifier().Detect(AgentKind.Gemini, stderr: null, stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
    }

    private static IQuotaFailureClassifier BuildClassifier() =>
        new CompositeQuotaFailureClassifier(
        [
            new ClaudeQuotaFailureDetector(),
            new CodexQuotaFailureDetector(),
            new GeminiQuotaFailureDetector(),
        ]);

    private static AgentKind ResolveAgent(string name) => name switch
    {
        nameof(AgentKind.Claude) => AgentKind.Claude,
        nameof(AgentKind.Codex) => AgentKind.Codex,
        nameof(AgentKind.Gemini) => AgentKind.Gemini,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Fact]
    public async Task PipelinePersistsQuotaResetAndNextRetryAfterAgentFailure()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var context = BuildPipelineContext(
            projectRepositoryUrl: seed,
            agent: new QuotaFailingAgent(stderr: "rate_limit_exceeded reset after 1h"));
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Quota persistence UAT",
            Prompt = "do work",
            Agent = AgentKind.Claude,
            BaseBranch = "main",
            WorkBranch = "feature/quota-persistence",
            PushUpstream = false,
        };
        await context.Store.CreateAsync(item);

        await context.Pipeline.RunAsync(item, CancellationToken.None);

        var failed = await context.Store.GetAsync(item.Id);
        Assert.NotNull(failed);
        Assert.Equal(WorkItemState.Failed, failed!.State);
        Assert.Equal("quota", failed.FailureKind);
        Assert.NotNull(failed.QuotaResetAt);
        Assert.NotNull(failed.NextQuotaRetryAt);
        Assert.True(failed.NextQuotaRetryAt >= failed.QuotaResetAt);
    }

    [Fact]
    public async Task NotifyQuotaFailure_PersistsNextRetryAtWithClockDriftMargin()
    {
        using var context = BuildRetryContext();
        var resetAt = _time.GetUtcNow().AddMinutes(30);
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Quota targeted retry UAT",
            Prompt = "do work",
            State = WorkItemState.Failed,
            FailureKind = "quota",
            QuotaResetAt = resetAt,
            AgentClassId = "frontier",
        };
        await context.Store.CreateAsync(item);

        await context.Scheduler.NotifyQuotaFailureAsync(item);

        var stored = await context.Store.GetAsync(item.Id);
        Assert.Equal(resetAt.Add(TimeSpan.FromMinutes(2)), stored!.NextQuotaRetryAt);
        Assert.True(_time.Timers.ContainsKey(item.Id));
    }

    [Fact]
    public async Task SchedulerStartup_RearmsTargetedTimersFromFailedQuotaItems()
    {
        using var context = BuildRetryContext();
        var item = FailedQuotaItem() with { NextQuotaRetryAt = _time.GetUtcNow().AddHours(1) };
        await context.Store.CreateAsync(item);

        await InvokePrivateAsync(context.Scheduler, "RearmTimersAsync", CancellationToken.None);

        Assert.True(_time.Timers.ContainsKey(item.Id));
    }

    [Fact]
    public async Task AutoRetryEnabled_PeriodicSweepRetriesEligibleQuotaFailedItemAndEmitsWebhook()
    {
        using var context = BuildRetryContext();
        var item = FailedQuotaItem();
        await context.Store.CreateAsync(item);

        await InvokePrivateAsync(context.Scheduler, "RunPeriodicSweepAsync", CancellationToken.None);

        var retried = await context.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, retried!.State);
        Assert.Equal(1, retried.QuotaRetryAttempts);
        Assert.Contains(context.Webhooks.Events, e => e.Event == "work_item.auto_retry");
    }

    [Fact]
    public async Task QueuePaused_AutoRetrySkipsWithoutChangingItemState()
    {
        using var context = BuildRetryContext(queueController: new QueueControllerStub(globalPaused: true));
        var item = FailedQuotaItem();
        await context.Store.CreateAsync(item);

        await InvokePrivateAsync(context.Scheduler, "RunPeriodicSweepAsync", CancellationToken.None);

        var stored = await context.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, stored!.State);
        Assert.Equal(0, stored.QuotaRetryAttempts);
    }

    [Fact]
    public async Task ProjectPaused_AutoRetrySkipsWithoutChangingItemState()
    {
        var queueController = new QueueControllerStub(projectPaused: true);
        using var context = BuildRetryContext(queueController: queueController);
        var item = FailedQuotaItem();
        await context.Store.CreateAsync(item);

        await InvokePrivateAsync(context.Scheduler, "RunPeriodicSweepAsync", CancellationToken.None);

        var stored = await context.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, stored!.State);
        Assert.Equal(0, stored.QuotaRetryAttempts);
        Assert.Contains(item.ProjectId, queueController.ProjectStateChecks);
    }

    [Fact]
    public async Task MaxAutoRetriesReached_SchedulerLeavesItemFailed()
    {
        using var context = BuildRetryContext();
        var item = FailedQuotaItem() with { QuotaRetryAttempts = 3 };
        await context.Store.CreateAsync(item);

        await InvokePrivateAsync(context.Scheduler, "RunPeriodicSweepAsync", CancellationToken.None);

        var stored = await context.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, stored!.State);
        Assert.Equal(3, stored.QuotaRetryAttempts);
    }

    [Fact]
    public async Task RouterStillGatesItem_RetryIsSkippedForLaterSweep()
    {
        using var context = BuildRetryContext(routerQuotaPct: 0);
        var item = FailedQuotaItem();
        await context.Store.CreateAsync(item);

        await InvokePrivateAsync(context.Scheduler, "RunPeriodicSweepAsync", CancellationToken.None);

        var stored = await context.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, stored!.State);
        Assert.Equal(0, stored.QuotaRetryAttempts);
    }

    private RetryContext BuildRetryContext(
        double routerQuotaPct = 100,
        IQueueController? queueController = null)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var dbPath = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var store = new SqliteWorkItemStore(dbPath);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var queue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(store, queue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = "https://example.invalid/repo.git",
            DefaultAgent = AgentKind.Claude,
            DefaultAgentClass = "frontier",
        });
        var router = new CodeyBox.Orchestrator.AgentClassRouter(
            [
                new AgentClass
                {
                    Id = "frontier",
                    DisplayName = "Frontier",
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
            [new StaticQuotaProbe(AgentKind.Claude, routerQuotaPct)],
            new QuotaRouterOptions { MinQuotaPct = 10, QuotaRecheckInterval = TimeSpan.FromMinutes(5) },
            NullLogger<CodeyBox.Orchestrator.AgentClassRouter>.Instance,
            _time);
        var webhooks = new CapturingWebhookDispatcher();
        var scheduler = new QuotaRetryScheduler(
            store,
            retrier,
            new OrchestratorOptions
            {
                AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
                {
                    Enabled = true,
                    PeriodicCheckInterval = TimeSpan.FromHours(1),
                    ClockDriftSafetyMargin = TimeSpan.FromMinutes(2),
                    MaxAutoRetriesPerWorkItem = 3,
                },
            },
            NullLogger<QuotaRetryScheduler>.Instance,
            router,
            projects,
            queueController,
            webhooks,
            _time);

        return new RetryContext(store, scheduler, webhooks);
    }

    private PipelineContext BuildPipelineContext(string projectRepositoryUrl, IAgentRunner agent)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var dbPath = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var store = new SqliteWorkItemStore(dbPath);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var webhooks = new CapturingWebhookDispatcher();
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = projectRepositoryUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
        });
        var retryScheduler = BuildRetrySchedulerForStore(store, gitHost);
        var pipeline = new PipelineRunner(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance),
            gitHost,
            new AgentRegistry([agent]),
            new StaticCredentialProvider(),
            new InMemoryPullRequestService(),
            projects,
            new TestUpstreamFactory(),
            new ProjectAuditorComposer(new ScriptedAuditorCatalog([])),
            store,
            webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            retryScheduler: retryScheduler,
            quotaClassifier: BuildClassifier(),
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);

        return new PipelineContext(pipeline, store);
    }

    private QuotaRetryScheduler BuildRetrySchedulerForStore(SqliteWorkItemStore store, LocalGitHost gitHost)
    {
        var queue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(store, queue, gitHost, NullLogger<WorkItemRetrier>.Instance);

        return new QuotaRetryScheduler(
            store,
            retrier,
            new OrchestratorOptions
            {
                AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
                {
                    Enabled = true,
                    PeriodicCheckInterval = TimeSpan.FromHours(1),
                    ClockDriftSafetyMargin = TimeSpan.FromMinutes(2),
                    MaxAutoRetriesPerWorkItem = 3,
                },
            },
            NullLogger<QuotaRetryScheduler>.Instance,
            timeProvider: _time);
    }

    private WorkItem FailedQuotaItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "Quota retry UAT",
        Prompt = "do work",
        State = WorkItemState.Failed,
        FailureKind = "quota",
        AgentClassId = "frontier",
    };

    private static async Task InvokePrivateAsync(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(
            methodName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        await (Task)method!.Invoke(target, args)!;
    }

    private sealed class QuotaFailingAgent : IAgentRunner
    {
        private readonly string? _stdout;
        private readonly string? _stderr;

        public QuotaFailingAgent(string? stderr = null, string? stdout = null)
        {
            _stderr = stderr;
            _stdout = stdout;
        }

        public AgentKind Kind => AgentKind.Claude;

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
            => Task.FromResult(new AgentResult(false, "agent exited 1", _stdout, _stderr));
    }

    private sealed class StaticQuotaProbe : IAgentQuotaProbe
    {
        private readonly AgentQuotaSnapshot _snapshot;

        public StaticQuotaProbe(AgentKind kind, double availablePct)
        {
            Kind = kind;
            _snapshot = new AgentQuotaSnapshot { AvailablePct = availablePct };
        }

        public AgentKind Kind { get; }

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
            => Task.FromResult(_snapshot);
    }

    private sealed class ControlledTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public ControlledTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public Dictionary<WorkItemId, RecordingTimer> Timers { get; } = [];

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new RecordingTimer(callback, state, dueTime);
            if (state is WorkItemId id)
                Timers[id] = timer;
            return timer;
        }
    }

    private sealed class RecordingTimer : ITimer
    {
        public RecordingTimer(TimerCallback callback, object? state, TimeSpan dueTime)
        {
            Callback = callback;
            State = state;
            DueTime = dueTime;
        }

        public TimerCallback Callback { get; }
        public object? State { get; }
        public TimeSpan DueTime { get; }

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class QueueControllerStub : IQueueController
    {
        private readonly bool _globalPaused;
        private readonly bool _projectPaused;

        public QueueControllerStub(bool globalPaused = false, bool projectPaused = false)
        {
            _globalPaused = globalPaused;
            _projectPaused = projectPaused;
        }

        public QueueState State => _globalPaused ? QueueState.Paused : QueueState.Running;
        public List<ProjectId> ProjectStateChecks { get; } = [];
        public DateTimeOffset? PausedAt => null;
        public string? PausedReason => null;
        public Task PauseAsync(string reason, CancellationToken ct) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task PauseProjectAsync(ProjectId projectId, string reason, CancellationToken ct) => Task.CompletedTask;
        public Task ResumeProjectAsync(ProjectId projectId, CancellationToken ct) => Task.CompletedTask;
        public Task<ProjectQueueState?> GetProjectStateAsync(ProjectId projectId, CancellationToken ct)
        {
            ProjectStateChecks.Add(projectId);
            return Task.FromResult<ProjectQueueState?>(new ProjectQueueState(projectId, _projectPaused, null, null));
        }
        public Task<IReadOnlyDictionary<string, bool>> GetFleetPauseStatesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, bool>>(new Dictionary<string, bool>());
    }

    private sealed record RetryContext(
        SqliteWorkItemStore Store,
        QuotaRetryScheduler Scheduler,
        CapturingWebhookDispatcher Webhooks) : IDisposable
    {
        public void Dispose() => Store.Dispose();
    }

    private sealed record PipelineContext(
        PipelineRunner Pipeline,
        SqliteWorkItemStore Store) : IDisposable
    {
        public void Dispose() => Store.Dispose();
    }
}
