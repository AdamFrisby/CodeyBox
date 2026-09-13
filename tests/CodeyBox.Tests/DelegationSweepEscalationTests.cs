using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Events;
using Xunit;

namespace CodeyBox.Tests;

/// <summary>
/// Sweep-level coverage for the repeated-terminal-failure condition: a
/// deterministic item that keeps failing across retries escalates when the
/// condition is armed, stays parked when it is not, and never escalates a
/// second time. Pipeline-level audit-max coverage lives in
/// <see cref="DelegationAuditEscalationTests"/>.
///
/// Audit assertions use a dedicated Serilog logger pushed through
/// <see cref="CodeyBox.Core.AuditLog.PushScopedLogger"/> rather than the
/// process-global <c>Log.Logger</c>: a <c>WebApplicationFactory</c> host boot
/// running concurrently in a sibling collection rebuilds the global logger,
/// which previously rerouted the sweep's <c>terminal_failure_classified</c>
/// event off the test sink (and let foreign host events land in it). The
/// AsyncLocal scope flows into the awaited sweep and is immune to those
/// global swaps, so this class stays out of the GlobalSerilog collection.
/// </summary>
public sealed class DelegationSweepEscalationTests : IDisposable
{
    private static readonly ProjectId TestProjectId = new("test-project");
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-delseep-").FullName;
    private readonly TestSink _sink = new();
    private readonly Serilog.Core.Logger _auditLogger;
    private readonly IDisposable _auditScope;

    public DelegationSweepEscalationTests()
    {
        _auditLogger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(_sink)
            .CreateLogger();
        _auditScope = CodeyBox.Core.AuditLog.PushScopedLogger(_auditLogger);
    }

    public void Dispose()
    {
        _auditScope.Dispose();
        _auditLogger.Dispose();
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task RepeatedFailure_Armed_EscalatesAndRecordsFailureSignal()
    {
        var fixture = BuildFixture(escalation: new DelegationEscalationOptions { Enabled = true });
        // Two failure episodes: failed, retried, failed again.
        var item = FailedTwice("build broke");
        await fixture.Store.CreateAsync(item);

        await fixture.RunSweepAsync();

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.Delegating, stored!.State);
        Assert.True(stored.DelegationRequested);
        Assert.True(stored.AutoDelegationEscalated);
        Assert.Contains(DelegationTriggers.RepeatedTerminalFailure, stored.DelegationReason);
        // The failure signal rides along: error text, episode count, and the
        // classification audit log all stay on the record.
        Assert.Contains("build broke", stored.LastError);
        Assert.Equal(2, stored.TerminalFailureCount);
        AssertSweepAction(item, "escalated");
    }

    [Fact]
    public async Task RepeatedFailure_BelowThreshold_StaysParked()
    {
        var fixture = BuildFixture(escalation: new DelegationEscalationOptions { Enabled = true });
        var item = NewItem().With(WorkItemState.Failed, "build broke");
        await fixture.Store.CreateAsync(item);

        await fixture.RunSweepAsync();

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, stored!.State);
        Assert.False(stored.DelegationRequested);
    }

    [Fact]
    public async Task RepeatedFailure_MasterSwitchOff_StaysParked()
    {
        var fixture = BuildFixture(escalation: new DelegationEscalationOptions { Enabled = false });
        var item = FailedTwice("build broke");
        await fixture.Store.CreateAsync(item);

        await fixture.RunSweepAsync();

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, stored!.State);
        Assert.False(stored.DelegationRequested);
    }

    [Fact]
    public async Task RepeatedFailure_ConditionDisabled_StaysParked()
    {
        var fixture = BuildFixture(escalation: new DelegationEscalationOptions
        {
            Enabled = true,
            OnRepeatedTerminalFailure = false,
        });
        var item = FailedTwice("build broke");
        await fixture.Store.CreateAsync(item);

        await fixture.RunSweepAsync();

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, stored!.State);
    }

    [Fact]
    public async Task RepeatedFailure_AlreadyEscalated_DoesNotEscalateAgain()
    {
        var fixture = BuildFixture(escalation: new DelegationEscalationOptions { Enabled = true });
        var item = FailedTwice("build broke") with { AutoDelegationEscalated = true };
        await fixture.Store.CreateAsync(item);

        await fixture.RunSweepAsync();

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, stored!.State);
        Assert.False(stored.DelegationRequested);
    }

    [Fact]
    public async Task RepeatedFailure_TransientExhaustion_EscalatesInsteadOfDeadLettering()
    {
        var fixture = BuildFixture(escalation: new DelegationEscalationOptions { Enabled = true });
        // Transient budget burned: two failure episodes, attempts at the cap,
        // and a live schedule so this sweep takes the dead-letter branch.
        var item = FailedTwice("connection reset") with
        {
            FailureKind = WorkItemFailureKinds.Infrastructure,
            TerminalRetryAttempts = 3,
            NextTerminalRetryAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        await fixture.Store.CreateAsync(item);

        await fixture.RunSweepAsync();

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.Delegating, stored!.State);
        Assert.True(stored.AutoDelegationEscalated);
        Assert.Contains("reached max attempts", stored.LastError);
        Assert.Equal(3, stored.TerminalRetryAttempts);
        AssertSweepAction(item, "escalated");
    }

    // ── Fixture ──────────────────────────────────────────────────────────────

    private SweepFixture BuildFixture(DelegationEscalationOptions escalation)
    {
        var dbPath = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new SqliteWorkItemStore(dbPath);
        var queue = new InMemoryTaskQueue();
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")) },
            NullLogger<LocalGitHost>.Instance);
        var retrier = new WorkItemRetrier(store, queue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var escalationService = new DelegationEscalationService(
            store, queue, () => escalation);
        var recoveryOptions = new TerminalFailureRecoveryOptions
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
            new DefaultTerminalFailureClassifier(),
            optionsAccessor: () => recoveryOptions,
            log: NullLogger<TerminalFailureRecoveryService>.Instance,
            jitter: _ => 500,
            delegationEscalation: escalationService);
        return new SweepFixture(store, service, recoveryOptions);
    }

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = TestProjectId,
        Title = "sweep test",
        Prompt = "p",
        State = WorkItemState.Queued,
    };

    private static WorkItem FailedTwice(string error) =>
        NewItem().With(WorkItemState.Failed, error)
            .With(WorkItemState.Queued)
            .With(WorkItemState.Failed, error);

    private void AssertSweepAction(WorkItem item, string action)
    {
        var evt = Assert.Single(_sink.Events, e =>
            string.Equals(GetScalar<string>(e, "EventName"), "work_item.terminal_failure_classified", StringComparison.Ordinal)
            && string.Equals(GetScalar<string>(e, "WorkItemId"), item.Id.ToString(), StringComparison.Ordinal)
            && string.Equals(GetScalar<string>(e, "Action"), action, StringComparison.Ordinal));
        Assert.NotNull(evt);
    }

    private static T? GetScalar<T>(LogEvent evt, string key)
    {
        if (!evt.Properties.TryGetValue(key, out var prop) || prop is not ScalarValue sv)
            return default;
        return sv.Value is T t ? t : default;
    }

    private sealed record SweepFixture(
        SqliteWorkItemStore Store,
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
}
