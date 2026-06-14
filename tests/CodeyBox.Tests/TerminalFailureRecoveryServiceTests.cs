using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Events;

namespace CodeyBox.Tests;

[Collection("GlobalSerilog")]
public sealed class TerminalFailureRecoveryServiceTests : IDisposable
{
    private static readonly ProjectId TestProjectId = new("test-project");
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-tfr-").FullName;
    private readonly TestSink _sink = new();

    public TerminalFailureRecoveryServiceTests()
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
    public async Task Transient_failure_first_seen_arms_backoff_and_does_NOT_retry_immediately()
    {
        var fixture = BuildFixture();
        var item = CreateFailedItem(failureKind: "infrastructure");
        await fixture.Store.CreateAsync(item);

        await fixture.RunSweepAsync();

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.Failed, stored!.State);
        Assert.Equal(0, stored.TerminalRetryAttempts);
        Assert.NotNull(stored.NextTerminalRetryAt);
        AssertEvent(item, action: "scheduled", failureClass: "Transient");
    }

    [Fact]
    public async Task Transient_failure_at_or_past_scheduled_time_retries_and_increments_counter()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var fixture = BuildFixture(time: time);
        // Pre-arm the schedule in the past so the next sweep takes the retry
        // branch instead of the arming branch.
        var item = CreateFailedItem(failureKind: "infrastructure") with
        {
            NextTerminalRetryAt = time.GetUtcNow().AddMinutes(-1),
        };
        await fixture.Store.CreateAsync(item);

        await fixture.RunSweepAsync();

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        // RetryAsync re-queues the item from Failed; resumeState should be Queued.
        Assert.Equal(WorkItemState.Queued, stored!.State);
        Assert.Equal(1, stored.TerminalRetryAttempts);
        Assert.Null(stored.NextTerminalRetryAt);
        AssertEvent(item, action: "retried", failureClass: "Transient");
    }

    [Fact]
    public async Task Transient_failure_at_cap_dead_letters_to_NeedsOperatorInput()
    {
        var fixture = BuildFixture();
        var item = CreateFailedItem(failureKind: "infrastructure") with
        {
            TerminalRetryAttempts = 3, // matches default MaxAutoRetriesPerWorkItem
        };
        await fixture.Store.CreateAsync(item);

        await fixture.RunSweepAsync();

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.NeedsOperatorInput, stored!.State);
        Assert.Contains("reached max attempts", stored.LastError ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Null(stored.NextTerminalRetryAt);
        AssertEvent(item, action: "dead-lettered", failureClass: "Transient");
    }

    [Fact]
    public async Task Deterministic_failure_is_not_retried_and_is_logged_as_parked()
    {
        var fixture = BuildFixture();
        var item = CreateFailedItem(failureKind: "build");
        await fixture.Store.CreateAsync(item);

        await fixture.RunSweepAsync();

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.Failed, stored!.State);
        Assert.Equal(0, stored.TerminalRetryAttempts);
        Assert.Null(stored.NextTerminalRetryAt);
        AssertEvent(item, action: "parked", failureClass: "Deterministic");
    }

    [Fact]
    public async Task AuditFailed_is_swept_and_parked_as_Deterministic()
    {
        var fixture = BuildFixture();
        var item = CreateFailedItem(failureKind: null) with { State = WorkItemState.AuditFailed };
        await fixture.Store.CreateAsync(item);

        await fixture.RunSweepAsync();

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.AuditFailed, stored!.State);
        AssertEvent(item, action: "parked", failureClass: "Deterministic");
    }

    [Fact]
    public async Task Quota_failures_are_delegated_to_quota_retry_scheduler_no_state_change()
    {
        var fixture = BuildFixture();
        var item = CreateFailedItem(failureKind: "quota");
        await fixture.Store.CreateAsync(item);

        await fixture.RunSweepAsync();

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.Failed, stored!.State);
        Assert.Equal(0, stored.TerminalRetryAttempts);
        Assert.Null(stored.NextTerminalRetryAt);
        AssertEvent(item, action: "delegated", failureClass: "PolicyQuota");
    }

    [Fact]
    public async Task Unknown_failure_is_parked_fail_closed_default()
    {
        var fixture = BuildFixture();
        var item = CreateFailedItem(failureKind: "other");
        await fixture.Store.CreateAsync(item);

        await fixture.RunSweepAsync();

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.Failed, stored!.State);
        AssertEvent(item, action: "parked", failureClass: "Unknown");
    }

    [Fact]
    public async Task Sweep_does_not_touch_non_terminal_states()
    {
        var fixture = BuildFixture();
        var working = CreateFailedItem(failureKind: "infrastructure") with
        {
            State = WorkItemState.Working,
            FailureKind = null,
        };
        await fixture.Store.CreateAsync(working);

        await fixture.RunSweepAsync();

        var stored = await fixture.Store.GetAsync(working.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.Working, stored!.State);
        // No audit event should have been emitted for this row.
        Assert.DoesNotContain(_sink.Events, e =>
            string.Equals(GetScalar<string>(e, "WorkItemId"), working.Id.ToString(), StringComparison.Ordinal)
            && string.Equals(GetScalar<string>(e, "EventName"), "work_item.terminal_failure_classified", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Exponential_backoff_grows_with_attempts_and_is_capped_at_MaxBackoff()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var fixture = BuildFixture(time: time, opts: new TerminalFailureRecoveryOptions
        {
            Enabled = true,
            BaseBackoff = TimeSpan.FromMinutes(1),
            MaxBackoff = TimeSpan.FromMinutes(10),
            JitterFraction = 0, // deterministic
            MaxAutoRetriesPerWorkItem = 10,
        });

        // attemptsSoFar=0: delay should be 1 min
        var item0 = CreateFailedItem(failureKind: "infrastructure");
        await fixture.Store.CreateAsync(item0);
        await fixture.RunSweepAsync();
        var stored0 = await fixture.Store.GetAsync(item0.Id);
        var delay0 = stored0!.NextTerminalRetryAt!.Value - time.GetUtcNow();
        Assert.Equal(TimeSpan.FromMinutes(1), delay0);

        // attemptsSoFar=2: delay should be base * 4 = 4 min
        var item2 = CreateFailedItem(failureKind: "infrastructure") with { TerminalRetryAttempts = 2 };
        await fixture.Store.CreateAsync(item2);
        await fixture.RunSweepAsync();
        var stored2 = await fixture.Store.GetAsync(item2.Id);
        var delay2 = stored2!.NextTerminalRetryAt!.Value - time.GetUtcNow();
        Assert.Equal(TimeSpan.FromMinutes(4), delay2);

        // attemptsSoFar=5: delay should be base * 32 = 32 min, capped at 10 min
        var item5 = CreateFailedItem(failureKind: "infrastructure") with { TerminalRetryAttempts = 5 };
        await fixture.Store.CreateAsync(item5);
        await fixture.RunSweepAsync();
        var stored5 = await fixture.Store.GetAsync(item5.Id);
        var delay5 = stored5!.NextTerminalRetryAt!.Value - time.GetUtcNow();
        Assert.Equal(TimeSpan.FromMinutes(10), delay5);
    }

    [Fact]
    public async Task Manual_retry_path_clears_TerminalRetryAttempts_and_NextTerminalRetryAt()
    {
        // The WorkItemRetrier is the ground truth for retry semantics.
        // The recovery service depends on it leaving counters in a sane
        // state so the operator can manually intervene after a dead-letter.
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var fixture = BuildFixture(time: time);
        var item = CreateFailedItem(failureKind: "infrastructure") with
        {
            TerminalRetryAttempts = 2,
            NextTerminalRetryAt = time.GetUtcNow().AddMinutes(15),
        };
        await fixture.Store.CreateAsync(item);

        var (success, error, _, _, _) = await fixture.Retrier.RetryAsync(item, from: "work", trigger: "manual");
        Assert.True(success, error);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.Queued, stored!.State);
        Assert.Equal(0, stored.TerminalRetryAttempts);
        Assert.Null(stored.NextTerminalRetryAt);
    }

    [Fact]
    public async Task Terminal_failure_recovery_trigger_does_not_bump_quota_counter()
    {
        // Guards a wiring footgun: WorkItemRetrier previously assumed any
        // non-"manual" trigger was a quota auto-retry. Adding a new trigger
        // without distinguishing it would over-count quota retries.
        var fixture = BuildFixture();
        var item = CreateFailedItem(failureKind: "infrastructure") with
        {
            QuotaRetryAttempts = 0,
            TerminalRetryAttempts = 0,
        };
        await fixture.Store.CreateAsync(item);

        var (success, _, _, _, _) = await fixture.Retrier.RetryAsync(
            item, from: "work", trigger: "terminal-failure-recovery");
        Assert.True(success);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(0, stored!.QuotaRetryAttempts);
    }

    private RecoveryFixture BuildFixture(
        TerminalFailureRecoveryOptions? opts = null,
        MutableTimeProvider? time = null)
    {
        var dbPath = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new SqliteWorkItemStore(dbPath);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")) },
            NullLogger<LocalGitHost>.Instance);
        var retrier = new WorkItemRetrier(store, new InMemoryTaskQueue(), gitHost, NullLogger<WorkItemRetrier>.Instance);
        var classifier = new DefaultTerminalFailureClassifier();
        var effectiveOpts = opts ?? new TerminalFailureRecoveryOptions
        {
            Enabled = true,
            BaseBackoff = TimeSpan.FromMinutes(1),
            MaxBackoff = TimeSpan.FromMinutes(30),
            JitterFraction = 0,
            MaxAutoRetriesPerWorkItem = 3,
            PeriodicCheckInterval = TimeSpan.FromMinutes(1),
        };
        var service = new TerminalFailureRecoveryService(
            store,
            retrier,
            classifier,
            optionsAccessor: () => effectiveOpts,
            log: NullLogger<TerminalFailureRecoveryService>.Instance,
            timeProvider: time,
            jitter: _ => 500); // dead-center: no jitter offset
        return new RecoveryFixture(store, retrier, service, effectiveOpts);
    }

    private static WorkItem CreateFailedItem(string? failureKind)
        => new()
        {
            Id = WorkItemId.New(),
            ProjectId = TestProjectId,
            Title = "test",
            Prompt = "p",
            State = WorkItemState.Failed,
            FailureKind = failureKind,
            LastError = "synthetic failure",
        };

    private LogEvent AssertEvent(WorkItem item, string action, string failureClass)
    {
        var evt = Assert.Single(_sink.Events, e =>
            string.Equals(GetScalar<string>(e, "EventName"), "work_item.terminal_failure_classified", StringComparison.Ordinal)
            && string.Equals(GetScalar<string>(e, "WorkItemId"), item.Id.ToString(), StringComparison.Ordinal)
            && string.Equals(GetScalar<string>(e, "Action"), action, StringComparison.Ordinal)
            && string.Equals(GetScalar<string>(e, "FailureClass"), failureClass, StringComparison.Ordinal));
        return evt;
    }

    private static T? GetScalar<T>(LogEvent evt, string key)
    {
        if (!evt.Properties.TryGetValue(key, out var prop) || prop is not ScalarValue sv)
            return default;
        return sv.Value is T t ? t : default;
    }

    private sealed record RecoveryFixture(
        SqliteWorkItemStore Store,
        WorkItemRetrier Retrier,
        TerminalFailureRecoveryService Service,
        TerminalFailureRecoveryOptions Options)
    {
        public async Task RunSweepAsync()
        {
            var sweep = typeof(TerminalFailureRecoveryService).GetMethod(
                "RunPeriodicSweepAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            await (Task)sweep.Invoke(Service, [Options, CancellationToken.None])!;
        }
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public MutableTimeProvider(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) { _now = _now.Add(delta); }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => new InertTimer();
    }

    private sealed class InertTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
