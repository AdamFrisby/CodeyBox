using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Events;

namespace CodeyBox.Tests;

[Collection("GlobalSerilog")]
public sealed class QuotaRetrySchedulerAuditTests : IDisposable
{
    private static readonly ProjectId TestProjectId = new("test-project");
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-quota-audit-").FullName;
    private readonly TestSink _sink = new();

    public QuotaRetrySchedulerAuditTests()
    {
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    public void Dispose()
    {
        Log.CloseAndFlush();
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task PeriodicSweep_AuditLogsEveryWalkedQuotaStateItem()
    {
        using var fixture = BuildScheduler(BuildRouter(availablePct: 0), BuildProjects());
        var failed = CreateQuotaItem(WorkItemState.Failed);
        var waiting = CreateQuotaItem(WorkItemState.WaitingForQuotaReset);
        await fixture.Store.CreateAsync(failed);
        await fixture.Store.CreateAsync(waiting);

        await RunPeriodicSweepAsync(fixture.Scheduler);

        AssertQuotaAttempt(failed, "periodic", "skipped:quota-still-gated", "Failed");
        AssertQuotaAttempt(waiting, "periodic", "skipped:quota-still-gated", "WaitingForQuotaReset");
    }

    [Theory]
    [MemberData(nameof(AuditOutcomeCases))]
    public async Task TryRetry_AuditLogsOutcomeForSkippedAndFailedBranches(
        string expectedOutcome,
        Func<QuotaRetrySchedulerAuditTests, SchedulerFixture> buildFixture,
        Func<WorkItem> buildItem,
        string expectedState,
        Func<WorkItem, string> expectedReason)
    {
        using var fixture = buildFixture(this);
        var item = buildItem();
        await fixture.Store.CreateAsync(item);

        await RunPeriodicSweepAsync(fixture.Scheduler);

        var evt = AssertQuotaAttempt(item, "periodic", expectedOutcome, expectedState);
        Assert.Equal(expectedReason(item), GetScalar<string>(evt, "Reason"));
    }

    public static TheoryData<string, Func<QuotaRetrySchedulerAuditTests, SchedulerFixture>, Func<WorkItem>, string, Func<WorkItem, string>> AuditOutcomeCases()
    {
        return new TheoryData<string, Func<QuotaRetrySchedulerAuditTests, SchedulerFixture>, Func<WorkItem>, string, Func<WorkItem, string>>
        {
            {
                "skipped:max-retries",
                self => self.BuildScheduler(BuildRouter(availablePct: 100), BuildProjects()),
                () => CreateQuotaItem(WorkItemState.WaitingForQuotaReset) with { QuotaRetryAttempts = 3 },
                "WaitingForQuotaReset",
                _ => "attempts=3; max=3"
            },
            {
                "skipped:global-queue-paused",
                self => self.BuildScheduler(BuildRouter(availablePct: 100), BuildProjects(), new FakeQueueController(QueueState.Paused)),
                () => CreateQuotaItem(WorkItemState.WaitingForQuotaReset),
                "WaitingForQuotaReset",
                _ => ""
            },
            {
                "skipped:project-queue-paused",
                self => self.BuildScheduler(BuildRouter(availablePct: 100), BuildProjects(), new FakeQueueController(QueueState.Running, projectPaused: true)),
                () => CreateQuotaItem(WorkItemState.WaitingForQuotaReset),
                "WaitingForQuotaReset",
                _ => "projectId=test-project"
            },
            {
                "skipped:project-repository-unavailable",
                self => self.BuildScheduler(BuildRouter(availablePct: 100), projects: null),
                () => CreateQuotaItem(WorkItemState.WaitingForQuotaReset),
                "WaitingForQuotaReset",
                _ => ""
            },
            {
                "skipped:project-not-found",
                self => self.BuildScheduler(BuildRouter(availablePct: 100), new InMemoryProjectRepository()),
                () => CreateQuotaItem(WorkItemState.WaitingForQuotaReset),
                "WaitingForQuotaReset",
                _ => "projectId=test-project"
            },
            {
                "skipped:router-unavailable",
                self => self.BuildScheduler(router: null, BuildProjects()),
                () => CreateQuotaItem(WorkItemState.WaitingForQuotaReset),
                "WaitingForQuotaReset",
                _ => ""
            },
            {
                "skipped:no-eligible-members",
                self => self.BuildScheduler(BuildRouter(availablePct: 100, memberQualityScore: 80), BuildProjects()),
                () => CreateQuotaItem(WorkItemState.WaitingForQuotaReset),
                "WaitingForQuotaReset",
                _ => "ROUTING_NO_ELIGIBLE: no member of class 'frontier' meets MinModelScore=95 (best available=80)"
            },
            {
                "retry-failed",
                self => self.BuildScheduler(BuildRouter(availablePct: 100), BuildProjects()),
                () => CreateQuotaItem(WorkItemState.WaitingForQuotaReset) with { QuotaRetryFrom = "audit" },
                "WaitingForQuotaReset",
                item => $"cannot retry from 'audit': bare repo for work item {item.Id} no longer exists"
            },
        };
    }

    [Fact]
    public async Task RearmTimers_ImmediatelyRetriesOverdueFailedQuotaItemAndAuditsSuccess()
    {
        using var fixture = BuildScheduler(BuildRouter(availablePct: 100), BuildProjects());
        var item = CreateQuotaItem(WorkItemState.Failed) with
        {
            NextQuotaRetryAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
        await fixture.Store.CreateAsync(item);

        await RearmTimersAsync(fixture.Scheduler);

        var retried = await fixture.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, retried!.State);
        Assert.Equal(1, retried.QuotaRetryAttempts);
        var evt = AssertQuotaAttempt(item, "rearm-overdue", "retried", "Failed");
        Assert.Equal("from=work", GetScalar<string>(evt, "Reason"));
    }

    [Fact]
    public async Task TargetedTimer_AuditLogsTargetedSource()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var fixture = BuildScheduler(BuildRouter(availablePct: 100), BuildProjects(), timeProvider: time);
        var item = CreateQuotaItem(WorkItemState.WaitingForQuotaReset) with
        {
            NextQuotaRetryAt = time.Now.AddMinutes(5),
        };
        await fixture.Store.CreateAsync(item);

        await RearmTimersAsync(fixture.Scheduler);
        var timer = Assert.Single(time.Timers);
        timer.Fire();

        var evt = await WaitForQuotaAttemptAsync(item, "targeted", "retried");
        Assert.Equal("WaitingForQuotaReset", GetScalar<string>(evt, "State"));
        Assert.Equal("from=work", GetScalar<string>(evt, "Reason"));
    }

    [Fact]
    public async Task PeriodicSweep_ContinuesAfterRetryExceptionAndAuditsError()
    {
        using var fixture = BuildScheduler(
            BuildRouter(availablePct: 100),
            BuildProjects(),
            webhooks: new ThrowingWebhookDispatcher());
        var first = CreateQuotaItem(WorkItemState.WaitingForQuotaReset);
        var second = CreateQuotaItem(WorkItemState.WaitingForQuotaReset);
        await fixture.Store.CreateAsync(first);
        await fixture.Store.CreateAsync(second);

        await RunPeriodicSweepAsync(fixture.Scheduler);

        Assert.Equal(WorkItemState.Queued, (await fixture.Store.GetAsync(first.Id))!.State);
        Assert.Equal(WorkItemState.Queued, (await fixture.Store.GetAsync(second.Id))!.State);
        Assert.Equal("webhook failed", GetScalar<string>(AssertQuotaAttempt(first, "periodic", "error", "WaitingForQuotaReset"), "Reason"));
        Assert.Equal("webhook failed", GetScalar<string>(AssertQuotaAttempt(second, "periodic", "error", "WaitingForQuotaReset"), "Reason"));
    }

    [Fact]
    public async Task RearmTimers_ContinuesAfterOverdueRetryExceptionAndAuditsError()
    {
        using var fixture = BuildScheduler(
            BuildRouter(availablePct: 100),
            BuildProjects(),
            webhooks: new ThrowingWebhookDispatcher());
        var first = CreateQuotaItem(WorkItemState.WaitingForQuotaReset) with
        {
            NextQuotaRetryAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
        var second = CreateQuotaItem(WorkItemState.WaitingForQuotaReset) with
        {
            NextQuotaRetryAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
        await fixture.Store.CreateAsync(first);
        await fixture.Store.CreateAsync(second);

        await RearmTimersAsync(fixture.Scheduler);

        Assert.Equal(WorkItemState.Queued, (await fixture.Store.GetAsync(first.Id))!.State);
        Assert.Equal(WorkItemState.Queued, (await fixture.Store.GetAsync(second.Id))!.State);
        Assert.Equal("webhook failed", GetScalar<string>(AssertQuotaAttempt(first, "rearm-overdue", "error", "WaitingForQuotaReset"), "Reason"));
        Assert.Equal("webhook failed", GetScalar<string>(AssertQuotaAttempt(second, "rearm-overdue", "error", "WaitingForQuotaReset"), "Reason"));
    }

    private sealed class StaticQuotaProbe : IAgentQuotaProbe
    {
        private readonly double _availablePct;
        public StaticQuotaProbe(double availablePct) => _availablePct = availablePct;
        public AgentKind Kind => AgentKind.Claude;
        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
            => Task.FromResult(new AgentQuotaSnapshot { AvailablePct = _availablePct });
    }

    private static AgentClassRouter BuildRouter(double availablePct, int memberQualityScore = 100)
        => new(
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
                            QualityScore = memberQualityScore,
                        },
                    ],
                },
            ],
            [new StaticQuotaProbe(availablePct)],
            new QuotaRouterOptions { MinQuotaPct = 10 },
            NullLogger<AgentClassRouter>.Instance);

    private SchedulerFixture BuildScheduler(
        AgentClassRouter? router,
        IProjectRepository? projects,
        IQueueController? queueController = null,
        IWebhookDispatcher? webhooks = null,
        TimeProvider? timeProvider = null)
    {
        var dbPath = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new SqliteWorkItemStore(dbPath);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")) },
            NullLogger<LocalGitHost>.Instance);
        var retrier = new WorkItemRetrier(store, new InMemoryTaskQueue(), gitHost, NullLogger<WorkItemRetrier>.Instance);
        var time = timeProvider ?? new InertTimeProvider(DateTimeOffset.UtcNow);
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
            queueController,
            webhooks,
            time);
        return new SchedulerFixture(store, scheduler);
    }

    private static IProjectRepository BuildProjects()
        => new InMemoryProjectRepository(new Project
        {
            Id = TestProjectId,
            DisplayName = "Test",
            RepositoryUrl = "https://example.invalid/repo.git",
            DefaultAgentClass = "frontier",
        });

    private static WorkItem CreateQuotaItem(WorkItemState state)
        => new()
        {
            Id = WorkItemId.New(),
            ProjectId = TestProjectId,
            Title = "parked",
            Prompt = "p",
            State = state,
            FailureKind = "quota",
            AgentClassId = "frontier",
            NextQuotaRetryAt = DateTimeOffset.UtcNow.AddHours(-2),
        };

    private static async Task RunPeriodicSweepAsync(QuotaRetryScheduler scheduler)
    {
        var sweep = typeof(QuotaRetryScheduler).GetMethod(
            "RunPeriodicSweepAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)sweep.Invoke(scheduler, [CancellationToken.None])!;
    }

    private static async Task RearmTimersAsync(QuotaRetryScheduler scheduler)
    {
        var rearm = typeof(QuotaRetryScheduler).GetMethod(
            "RearmTimersAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)rearm.Invoke(scheduler, [CancellationToken.None])!;
    }

    private LogEvent AssertQuotaAttempt(WorkItem item, string source, string outcome, string state)
    {
        var evt = Assert.Single(_sink.Events, e =>
            string.Equals(GetScalar<string>(e, "EventName"), "quota_retry_attempted", StringComparison.Ordinal)
            && string.Equals(GetScalar<string>(e, "WorkItemId"), item.Id.ToString(), StringComparison.Ordinal)
            && string.Equals(GetScalar<string>(e, "Source"), source, StringComparison.Ordinal)
            && string.Equals(GetScalar<string>(e, "Outcome"), outcome, StringComparison.Ordinal));
        Assert.Equal(state, GetScalar<string>(evt, "State"));
        return evt;
    }

    private async Task<LogEvent> WaitForQuotaAttemptAsync(WorkItem item, string source, string outcome)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var match = _sink.Events.SingleOrDefault(e =>
                string.Equals(GetScalar<string>(e, "EventName"), "quota_retry_attempted", StringComparison.Ordinal)
                && string.Equals(GetScalar<string>(e, "WorkItemId"), item.Id.ToString(), StringComparison.Ordinal)
                && string.Equals(GetScalar<string>(e, "Source"), source, StringComparison.Ordinal)
                && string.Equals(GetScalar<string>(e, "Outcome"), outcome, StringComparison.Ordinal));
            if (match is not null)
                return match;

            await Task.Delay(25);
        }

        return AssertQuotaAttempt(item, source, outcome, item.State.ToString());
    }

    public sealed class SchedulerFixture : IDisposable
    {
        public SchedulerFixture(SqliteWorkItemStore store, QuotaRetryScheduler scheduler)
        {
            Store = store;
            Scheduler = scheduler;
        }

        public SqliteWorkItemStore Store { get; }
        public QuotaRetryScheduler Scheduler { get; }

        public void Dispose()
        {
            Scheduler.Dispose();
            Store.Dispose();
        }
    }

    private sealed class FakeQueueController : IQueueController
    {
        private readonly bool _projectPaused;

        public FakeQueueController(QueueState state, bool projectPaused = false)
        {
            State = state;
            _projectPaused = projectPaused;
        }

        public QueueState State { get; }
        public DateTimeOffset? PausedAt => null;
        public string? PausedReason => null;
        public Task PauseAsync(string reason, CancellationToken ct) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task PauseProjectAsync(ProjectId projectId, string reason, CancellationToken ct) => Task.CompletedTask;
        public Task ResumeProjectAsync(ProjectId projectId, CancellationToken ct) => Task.CompletedTask;
        public Task<ProjectQueueState?> GetProjectStateAsync(ProjectId projectId, CancellationToken ct)
            => Task.FromResult<ProjectQueueState?>(new ProjectQueueState(projectId, _projectPaused, null, null));
        public Task<IReadOnlyDictionary<string, bool>> GetFleetPauseStatesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, bool>>(new Dictionary<string, bool>());
    }

    private sealed class ThrowingWebhookDispatcher : IWebhookDispatcher
    {
        public Task PublishAsync(WebhookEvent evt, CancellationToken ct)
            => throw new InvalidOperationException("webhook failed");
    }

    private sealed class InertTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public InertTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => new InertTimer();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];

        public ManualTimeProvider(DateTimeOffset now) => Now = now;

        public DateTimeOffset Now { get; }

        public IReadOnlyList<ManualTimer> Timers => _timers;

        public override DateTimeOffset GetUtcNow() => Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime);
            _timers.Add(timer);
            return timer;
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime)
        {
            _callback = callback;
            _state = state;
            DueTime = dueTime;
        }

        public TimeSpan DueTime { get; }

        public void Fire() => _callback(_state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InertTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static T? GetScalar<T>(LogEvent evt, string key)
    {
        if (!evt.Properties.TryGetValue(key, out var prop) || prop is not ScalarValue sv)
            return default;
        return sv.Value is T t ? t : default;
    }
}
