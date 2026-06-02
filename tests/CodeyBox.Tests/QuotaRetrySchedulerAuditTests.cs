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
    private static readonly ProjectId BrokenProjectId = new("broken-project");
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

        if (expectedOutcome.StartsWith("skipped:", StringComparison.Ordinal))
        {
            var stored = await fixture.Store.GetAsync(item.Id);
            Assert.NotNull(stored);
            Assert.Equal(item.State, stored.State);
            Assert.Equal(item.QuotaRetryAttempts, stored.QuotaRetryAttempts);
        }
    }

    public static TheoryData<string, Func<QuotaRetrySchedulerAuditTests, SchedulerFixture>, Func<WorkItem>, string, Func<WorkItem, string>> AuditOutcomeCases()
    {
        return new TheoryData<string, Func<QuotaRetrySchedulerAuditTests, SchedulerFixture>, Func<WorkItem>, string, Func<WorkItem, string>>
        {
            {
                "skipped:max-retries",
                self => self.BuildScheduler(BuildRouter(availablePct: 100), BuildProjects()),
                () => CreateQuotaItem(WorkItemState.Failed) with { QuotaRetryAttempts = 3 },
                "Failed",
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
                () => CreateQuotaItem(WorkItemState.WaitingForQuotaReset) with { MinModelScore = 95 },
                "WaitingForQuotaReset",
                _ => "ROUTING_NO_ELIGIBLE: no member of class 'frontier' meets MinModelScore=95 / RequiredCapabilities=[] (best available=80)"
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
    public async Task PeriodicSweep_WaitingItemAtMaxRetriesLeavesQuotaWaitWhenQuotaUsable()
    {
        using var fixture = BuildScheduler(BuildRouter(availablePct: 100), BuildProjects());
        var item = CreateQuotaItem(WorkItemState.WaitingForQuotaReset) with
        {
            QuotaRetryAttempts = 3,
        };
        await fixture.Store.CreateAsync(item);

        await RunPeriodicSweepAsync(fixture.Scheduler);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.Failed, stored!.State);
        Assert.Equal("quota", stored.FailureKind);
        Assert.Null(stored.NextQuotaRetryAt);
        Assert.Equal(3, stored.QuotaRetryAttempts);

        var evt = AssertQuotaAttempt(item, "periodic", "skipped:max-retries", "WaitingForQuotaReset");
        Assert.Equal("attempts=3; max=3", GetScalar<string>(evt, "Reason"));
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
    public async Task PeriodicSweep_AuditLogsActualFromWhenRetryFallsBackToWork()
    {
        using var fixture = BuildScheduler(BuildRouter(availablePct: 100), BuildProjects());
        var item = CreateQuotaItem(WorkItemState.WaitingForQuotaReset) with
        {
            QuotaRetryFrom = "audit",
            WorkBranch = "codeybox/missing-work",
        };
        await fixture.Store.CreateAsync(item);
        await fixture.GitHost.EnsureRepositoryAsync(item.Id, seedFromUrl: null);

        await RunPeriodicSweepAsync(fixture.Scheduler);

        var retried = await fixture.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, retried!.State);
        Assert.Equal(1, retried.QuotaRetryAttempts);
        var evt = AssertQuotaAttempt(item, "periodic", "retried", "WaitingForQuotaReset");
        Assert.Equal("from=audit; actualFrom=work", GetScalar<string>(evt, "Reason"));
    }

    [Fact]
    public async Task TargetedTimer_AuditLogsTargetedSource()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var fixture = BuildScheduler(BuildRouter(availablePct: 100), BuildProjects(), timeProvider: time);
        var item = CreateQuotaItem(WorkItemState.Failed) with
        {
            NextQuotaRetryAt = time.Now.AddMinutes(5),
        };
        await fixture.Store.CreateAsync(item);

        await RearmTimersAsync(fixture.Scheduler);
        var timer = Assert.Single(time.Timers);
        timer.Fire();

        var evt = await WaitForQuotaAttemptAsync(item, "targeted", "retried");
        Assert.Equal("Failed", GetScalar<string>(evt, "State"));
        Assert.Equal("from=work", GetScalar<string>(evt, "Reason"));
    }

    [Fact]
    public async Task RearmTimers_RequeuesWaitingForQuotaResetItemWithoutWaitingForTargetedTimer()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var probe = new MutableQuotaProbe(availablePct: 0);
        using var fixture = BuildScheduler(BuildRouter(probe), BuildProjects(), timeProvider: time);
        var item = CreateQuotaItem(WorkItemState.WaitingForQuotaReset) with
        {
            NextQuotaRetryAt = time.Now.AddMinutes(5),
        };
        await fixture.Store.CreateAsync(item);

        await RearmTimersAsync(fixture.Scheduler);

        var retried = await fixture.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, retried!.State);
        Assert.Equal(1, retried.QuotaRetryAttempts);
        Assert.Empty(time.Timers);

        var evt = AssertQuotaAttempt(item, "startup", "retried", "WaitingForQuotaReset");
        Assert.Equal("WaitingForQuotaReset", GetScalar<string>(evt, "State"));
        Assert.Equal("from=work", GetScalar<string>(evt, "Reason"));
    }

    [Fact]
    public async Task PeriodicSweep_WebhookFailureDoesNotOverrideRetryAudit()
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
        Assert.Equal("from=work", GetScalar<string>(AssertQuotaAttempt(first, "periodic", "retried", "WaitingForQuotaReset"), "Reason"));
        Assert.Equal("from=work", GetScalar<string>(AssertQuotaAttempt(second, "periodic", "retried", "WaitingForQuotaReset"), "Reason"));
    }

    [Fact]
    public async Task TryRetry_AuditsSuccessfulRetryWhenWebhookEnrichmentCancelsSchedulerToken()
    {
        using var cts = new CancellationTokenSource();
        using var fixture = BuildScheduler(
            BuildRouter(availablePct: 100),
            new CancelOnSecondProjectLookupRepository(cts, CreateProject(TestProjectId)),
            webhooks: new NoopWebhookDispatcher());
        var item = CreateQuotaItem(WorkItemState.WaitingForQuotaReset);
        await fixture.Store.CreateAsync(item);

        await InvokeTryRetryAsync(fixture.Scheduler, item, "periodic", cts.Token);

        Assert.True(cts.IsCancellationRequested);
        var retried = await fixture.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, retried!.State);
        Assert.Equal(1, retried.QuotaRetryAttempts);
        Assert.Equal("from=work", GetScalar<string>(AssertQuotaAttempt(item, "periodic", "retried", "WaitingForQuotaReset"), "Reason"));
    }

    [Fact]
    public async Task RearmTimers_WebhookFailureDoesNotOverrideRetryAudit()
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
        Assert.Equal("from=work", GetScalar<string>(AssertQuotaAttempt(first, "startup", "retried", "WaitingForQuotaReset"), "Reason"));
        Assert.Equal("from=work", GetScalar<string>(AssertQuotaAttempt(second, "startup", "retried", "WaitingForQuotaReset"), "Reason"));
    }

    [Fact]
    public async Task TryRetry_AuditsErrorOutcomeBeforeRethrowingNonCancellationException()
    {
        using var fixture = BuildScheduler(
            BuildRouter(availablePct: 100),
            new ThrowingForProjectRepository(BrokenProjectId, CreateProject(TestProjectId)));
        var item = CreateQuotaItem(WorkItemState.WaitingForQuotaReset) with
        {
            ProjectId = BrokenProjectId,
        };
        await fixture.Store.CreateAsync(item);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeTryRetryAsync(fixture.Scheduler, item, "periodic", CancellationToken.None));

        Assert.Equal("project lookup failed", ex.Message);
        var evt = AssertQuotaAttempt(item, "periodic", "error", "WaitingForQuotaReset");
        Assert.Equal("project lookup failed", GetScalar<string>(evt, "Reason"));
    }

    [Fact]
    public async Task PeriodicSweep_ContinuesAfterItemRetryThrows()
    {
        using var fixture = BuildScheduler(
            BuildRouter(availablePct: 100),
            new ThrowingForProjectRepository(BrokenProjectId, CreateProject(TestProjectId)));
        var broken = CreateQuotaItem(WorkItemState.Failed) with
        {
            ProjectId = BrokenProjectId,
        };
        var later = CreateQuotaItem(WorkItemState.WaitingForQuotaReset);
        await fixture.Store.CreateAsync(broken);
        await fixture.Store.CreateAsync(later);

        await RunPeriodicSweepAsync(fixture.Scheduler);

        var brokenStored = await fixture.Store.GetAsync(broken.Id);
        Assert.Equal(WorkItemState.Failed, brokenStored!.State);
        Assert.Equal(0, brokenStored.QuotaRetryAttempts);
        var laterStored = await fixture.Store.GetAsync(later.Id);
        Assert.Equal(WorkItemState.Queued, laterStored!.State);
        Assert.Equal(1, laterStored.QuotaRetryAttempts);
        Assert.Equal("project lookup failed", GetScalar<string>(AssertQuotaAttempt(broken, "periodic", "error", "Failed"), "Reason"));
        Assert.Equal("from=work", GetScalar<string>(AssertQuotaAttempt(later, "periodic", "retried", "WaitingForQuotaReset"), "Reason"));
    }

    [Fact]
    public async Task RearmTimers_ContinuesAfterItemRetryThrows()
    {
        using var fixture = BuildScheduler(
            BuildRouter(availablePct: 100),
            new ThrowingForProjectRepository(BrokenProjectId, CreateProject(TestProjectId)));
        var broken = CreateQuotaItem(WorkItemState.Failed) with
        {
            ProjectId = BrokenProjectId,
            NextQuotaRetryAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
        var later = CreateQuotaItem(WorkItemState.WaitingForQuotaReset) with
        {
            NextQuotaRetryAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
        await fixture.Store.CreateAsync(broken);
        await fixture.Store.CreateAsync(later);

        await RearmTimersAsync(fixture.Scheduler);

        var brokenStored = await fixture.Store.GetAsync(broken.Id);
        Assert.Equal(WorkItemState.Failed, brokenStored!.State);
        Assert.Equal(0, brokenStored.QuotaRetryAttempts);
        var laterStored = await fixture.Store.GetAsync(later.Id);
        Assert.Equal(WorkItemState.Queued, laterStored!.State);
        Assert.Equal(1, laterStored.QuotaRetryAttempts);
        Assert.Equal("project lookup failed", GetScalar<string>(AssertQuotaAttempt(broken, "rearm-overdue", "error", "Failed"), "Reason"));
        Assert.Equal("from=work", GetScalar<string>(AssertQuotaAttempt(later, "startup", "retried", "WaitingForQuotaReset"), "Reason"));
    }

    [Fact]
    public async Task PeriodicRetry_RethrowsSchedulerCancellation()
    {
        using var cts = new CancellationTokenSource();
        using var fixture = BuildScheduler(
            BuildRouter(availablePct: 100),
            new CancellingProjectRepository(cts));
        var item = CreateQuotaItem(WorkItemState.WaitingForQuotaReset);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => TryPeriodicRetryAsync(fixture.Scheduler, item, cts.Token));

        Assert.True(cts.IsCancellationRequested);
        Assert.DoesNotContain(_sink.Events, e =>
            string.Equals(GetScalar<string>(e, "EventName"), "quota_retry_attempted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RearmTimer_RethrowsSchedulerCancellation()
    {
        using var cts = new CancellationTokenSource();
        using var fixture = BuildScheduler(
            BuildRouter(availablePct: 100),
            new CancellingProjectRepository(cts));
        var item = CreateQuotaItem(WorkItemState.WaitingForQuotaReset) with
        {
            NextQuotaRetryAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => TryRearmTimerAsync(fixture.Scheduler, item, cts.Token));

        Assert.True(cts.IsCancellationRequested);
        Assert.DoesNotContain(_sink.Events, e =>
            string.Equals(GetScalar<string>(e, "EventName"), "quota_retry_attempted", StringComparison.Ordinal));
    }

    private sealed class StaticQuotaProbe : IAgentQuotaProbe
    {
        private readonly double _availablePct;
        public StaticQuotaProbe(double availablePct) => _availablePct = availablePct;
        public AgentKind Kind => AgentKind.Claude;
        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
            => Task.FromResult(new AgentQuotaSnapshot { AvailablePct = _availablePct });
    }

    private sealed class MutableQuotaProbe : IAgentQuotaProbe
    {
        public MutableQuotaProbe(double availablePct) => AvailablePct = availablePct;
        public AgentKind Kind => AgentKind.Claude;
        public double AvailablePct { get; set; }
        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
            => Task.FromResult(new AgentQuotaSnapshot { AvailablePct = AvailablePct });
    }

    private static AgentClassRouter BuildRouter(double availablePct, int memberQualityScore = 100)
        => BuildRouter(new StaticQuotaProbe(availablePct), memberQualityScore);

    private static AgentClassRouter BuildRouter(IAgentQuotaProbe probe, int memberQualityScore = 100)
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
            [probe],
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
        return new SchedulerFixture(store, gitHost, scheduler);
    }

    private static IProjectRepository BuildProjects()
        => new InMemoryProjectRepository(CreateProject(TestProjectId));

    private static Project CreateProject(ProjectId id)
        => new()
        {
            Id = id,
            DisplayName = "Test " + id.Value,
            RepositoryUrl = "https://example.invalid/repo.git",
            DefaultAgentClass = "frontier",
        };

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

    private static async Task TryPeriodicRetryAsync(QuotaRetryScheduler scheduler, WorkItem item, CancellationToken ct)
    {
        var retry = typeof(QuotaRetryScheduler).GetMethod(
            "TryPeriodicRetryAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)retry.Invoke(scheduler, [item, ct])!;
    }

    private static async Task InvokeTryRetryAsync(QuotaRetryScheduler scheduler, WorkItem item, string source, CancellationToken ct)
    {
        var retry = typeof(QuotaRetryScheduler).GetMethod(
            "TryRetryAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)retry.Invoke(scheduler, [item, source, ct])!;
    }

    private static async Task RearmTimersAsync(QuotaRetryScheduler scheduler)
    {
        var rearm = typeof(QuotaRetryScheduler).GetMethod(
            "RearmTimersAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)rearm.Invoke(scheduler, [CancellationToken.None])!;
    }

    private static async Task TryRearmTimerAsync(QuotaRetryScheduler scheduler, WorkItem item, CancellationToken ct)
    {
        var rearm = typeof(QuotaRetryScheduler).GetMethod(
            "TryRearmTimerAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task<bool>)rearm.Invoke(scheduler, [item, ct])!;
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
        public SchedulerFixture(SqliteWorkItemStore store, LocalGitHost gitHost, QuotaRetryScheduler scheduler)
        {
            Store = store;
            GitHost = gitHost;
            Scheduler = scheduler;
        }

        public SqliteWorkItemStore Store { get; }
        public LocalGitHost GitHost { get; }
        public QuotaRetryScheduler Scheduler { get; }

        public void Dispose()
        {
            Scheduler.Dispose();
            Store.Dispose();
        }
    }

    private sealed class CancellingProjectRepository : IProjectRepository
    {
        private readonly CancellationTokenSource _cts;

        public CancellingProjectRepository(CancellationTokenSource cts) => _cts = cts;

        public Task<Project?> GetAsync(ProjectId id, CancellationToken ct = default)
        {
            _cts.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new OperationCanceledException(ct);
        }

        public Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Project>>([]);
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

    private sealed class NoopWebhookDispatcher : IWebhookDispatcher
    {
        public Task PublishAsync(WebhookEvent evt, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ThrowingForProjectRepository : IProjectRepository
    {
        private readonly ProjectId _throwFor;
        private readonly Dictionary<string, Project> _projects;

        public ThrowingForProjectRepository(ProjectId throwFor, params Project[] projects)
        {
            _throwFor = throwFor;
            _projects = projects.ToDictionary(p => p.Id.Value);
        }

        public Task<Project?> GetAsync(ProjectId id, CancellationToken ct = default)
        {
            if (id == _throwFor)
                throw new InvalidOperationException("project lookup failed");

            return Task.FromResult(_projects.TryGetValue(id.Value, out var project) ? project : null);
        }

        public Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Project>>([.. _projects.Values]);
    }

    private sealed class CancelOnSecondProjectLookupRepository : IProjectRepository
    {
        private readonly CancellationTokenSource _cts;
        private readonly Project _project;
        private int _calls;

        public CancelOnSecondProjectLookupRepository(CancellationTokenSource cts, Project project)
        {
            _cts = cts;
            _project = project;
        }

        public Task<Project?> GetAsync(ProjectId id, CancellationToken ct = default)
        {
            _calls++;
            if (_calls == 2)
            {
                _cts.Cancel();
                ct.ThrowIfCancellationRequested();
            }

            return Task.FromResult<Project?>(_project);
        }

        public Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Project>>([_project]);
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
