using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;

namespace CodeyBox.Tests;

/// <summary>
/// Covers peer-reroute recovery for items parked in
/// <see cref="WorkItemState.WaitingForQuotaReset"/> against a class member whose
/// quota was exhausted. Companion to the same-agent infra-restore path: when
/// the parking agent stays down (or gets paused) but a class peer becomes
/// routable, the item must be re-dispatched onto the peer without operator
/// intervention.
/// </summary>
public sealed class QuotaRetrySchedulerPeerRerouteTests : IDisposable
{
    private static readonly ProjectId TestProjectId = new("test-project");
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-quota-peer-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    /// <summary>
    /// Acceptance: an item parked WaitingForQuotaReset against a class whose
    /// only-then-eligible member is exhausted is auto-requeued onto a peer the
    /// moment that peer becomes available — no manual retry, no restart.
    /// </summary>
    [Fact]
    public async Task PeriodicSweep_PeerAvailable_RequeuesOntoPeer()
    {
        var probes = new MutablePeerProbes(claude: 0.0, codex: 100.0);
        using var fixture = BuildSchedulerWithPeers(probes);

        var item = ParkedItem();
        await fixture.Store.CreateAsync(item);

        await RunPeriodicSweepAsync(fixture.Scheduler);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.Queued, stored!.State);
        Assert.Equal(1, stored.QuotaRetryAttempts);
    }

    /// <summary>
    /// Acceptance: pausing the parking agent does not strand the item when a
    /// peer is available. The pause signal triggers the class-availability
    /// wake-up sweep, which evaluates against the FULL class and dispatches on
    /// the available peer.
    /// </summary>
    [Fact]
    public async Task PauseSignal_PausingParkingAgent_RequeuesOntoPeer()
    {
        var probes = new MutablePeerProbes(claude: 0.0, codex: 100.0);
        var pauseSignal = new FakeAgentPauseSignal();
        using var fixture = BuildSchedulerWithPeers(probes, pauseSignal: pauseSignal);

        var item = ParkedItem();
        await fixture.Store.CreateAsync(item);

        // Operator pauses the parking agent. The peer (codex) is wide open;
        // the sweep must route the item to the peer rather than waiting on
        // the now-paused agent.
        pauseSignal.FireAgentPauseChanged();
        await WaitForWakeUpSweepAsync(fixture.Scheduler);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.Queued, stored!.State);
        Assert.Equal(1, stored.QuotaRetryAttempts);
    }

    /// <summary>
    /// Acceptance: a peer quota reset (signalled via
    /// <see cref="IAgentQuotaAvailabilitySignal"/>) wakes the sweep and routes
    /// the parked item onto the freshly-available peer.
    /// </summary>
    [Fact]
    public async Task QuotaSignal_PeerBecomesAvailable_RequeuesOntoPeer()
    {
        var probes = new MutablePeerProbes(claude: 0.0, codex: 0.0);
        var quotaSignal = new FakeAgentQuotaAvailabilitySignal();
        using var fixture = BuildSchedulerWithPeers(probes, quotaSignal: quotaSignal);

        var item = ParkedItem();
        await fixture.Store.CreateAsync(item);

        // Peer refills its quota; the signal fires to wake the sweep.
        probes.UpdateCodex(100.0);
        quotaSignal.FireQuotaUsableThresholdCrossed();
        await WaitForWakeUpSweepAsync(fixture.Scheduler);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.Queued, stored!.State);
    }

    /// <summary>
    /// Acceptance: the real quota-probe transition path wakes parked items.
    /// A known below-floor probe records "unusable"; the next above-floor
    /// reading publishes through <see cref="AgentQuotaAvailabilityBroadcaster"/>
    /// and the scheduler reuses its wake-up sweep without waiting for the
    /// periodic interval or the stale NextQuotaRetryAt.
    /// </summary>
    [Fact]
    public async Task RouterQuotaTransition_ExhaustedToAvailable_RequeuesPromptly()
    {
        var probes = new MutablePeerProbes(claude: 0.0, codex: 0.0);
        var quotaBroadcaster = new AgentQuotaAvailabilityBroadcaster();
        using var fixture = BuildSchedulerWithPeers(
            probes,
            quotaSignal: quotaBroadcaster,
            quotaPublisher: quotaBroadcaster);
        var item = ParkedItem() with
        {
            NextQuotaRetryAt = DateTimeOffset.UtcNow.AddDays(5),
        };
        await fixture.Store.CreateAsync(item);

        var probeItem = item with { Id = WorkItemId.New(), State = WorkItemState.Queued };
        var denied = await fixture.Router!.ResolveAsync(probeItem, Project(), CancellationToken.None);
        Assert.True(denied.ShouldWait);

        probes.UpdateCodex(100.0);
        var allowed = await fixture.Router.ResolveAsync(probeItem with { Id = WorkItemId.New() }, Project(), CancellationToken.None);
        Assert.NotNull(allowed.Chosen);
        await WaitForWakeUpSweepAsync(fixture.Scheduler);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.Queued, stored!.State);
        Assert.Equal(1, stored.QuotaRetryAttempts);
    }

    /// <summary>
    /// Acceptance: a WaitingForQuotaReset item never has a null forward retry
    /// trigger. When neither the router nor the failing-agent reset can
    /// produce a wake time, the scheduler falls back to the periodic-check
    /// interval so the targeted timer arms and the item is re-evaluated even
    /// if nothing else signals.
    /// </summary>
    [Fact]
    public async Task NotifyQuotaFailure_NoResetSources_AnchorsToPeriodicInterval()
    {
        var time = new InertTimeProvider(new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero));
        var router = new NullResetRouter();
        using var fixture = BuildScheduler(router, time);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = TestProjectId,
            Title = "parked",
            Prompt = "p",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            AgentClassId = "frontier",
            NextQuotaRetryAt = null,
            QuotaResetAt = null,
        };
        await fixture.Store.CreateAsync(item);

        await fixture.Scheduler.NotifyQuotaFailureAsync(item);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.NotNull(stored!.NextQuotaRetryAt);
        Assert.Equal(time.GetUtcNow().AddHours(1), stored.NextQuotaRetryAt);
    }

    private SchedulerFixture BuildSchedulerWithPeers(
        MutablePeerProbes probes,
        TimeProvider? timeProvider = null,
        IAgentPauseSignal? pauseSignal = null,
        IAgentQuotaAvailabilitySignal? quotaSignal = null,
        IEnumerable<IAgentQuotaProbe>? probeOverride = null,
        IAgentQuotaAvailabilityPublisher? quotaPublisher = null)
    {
        var router = new AgentClassRouter(
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
                        new AgentMembership
                        {
                            Agent = AgentKind.Codex,
                            Billing = AgentBilling.Subscription,
                            QualityScore = 90,
                        },
                    ],
                },
            ],
            probeOverride ?? probes.AsProbes(),
            new QuotaRouterOptions { MinQuotaPct = 10 },
            NullLogger<AgentClassRouter>.Instance,
            quotaAvailabilityPublisher: quotaPublisher);
        return BuildScheduler(router, timeProvider, pauseSignal, quotaSignal, router);
    }

    private SchedulerFixture BuildScheduler(
        IQuotaRetryRouter? router,
        TimeProvider? timeProvider = null,
        IAgentPauseSignal? pauseSignal = null,
        IAgentQuotaAvailabilitySignal? quotaSignal = null,
        AgentClassRouter? classRouter = null)
    {
        var dbPath = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new SqliteWorkItemStore(dbPath);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")) },
            NullLogger<LocalGitHost>.Instance);
        var retrier = new WorkItemRetrier(store, new InMemoryTaskQueue(), gitHost, NullLogger<WorkItemRetrier>.Instance);
        var time = timeProvider ?? new InertTimeProvider(DateTimeOffset.UtcNow);
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = TestProjectId,
            DisplayName = "Test",
            RepositoryUrl = "https://example.invalid/repo.git",
            DefaultAgentClass = "frontier",
        });
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
            queueController: null,
            webhooks: null,
            time,
            baselineResolver: null,
            autoRetryOptionsAccessor: null,
            quotaAvailabilitySignal: quotaSignal,
            pauseSignal: pauseSignal);
        return new SchedulerFixture(store, scheduler, classRouter);
    }

    private static Project Project() => new()
    {
        Id = TestProjectId,
        DisplayName = "Test",
        RepositoryUrl = "https://example.invalid/repo.git",
        DefaultAgentClass = "frontier",
    };

    private static WorkItem ParkedItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = TestProjectId,
        Title = "parked",
        Prompt = "p",
        State = WorkItemState.WaitingForQuotaReset,
        FailureKind = "quota",
        AgentClassId = "frontier",
        NextQuotaRetryAt = DateTimeOffset.UtcNow.AddHours(-1),
    };

    private static async Task RunPeriodicSweepAsync(QuotaRetryScheduler scheduler)
    {
        var sweep = typeof(QuotaRetryScheduler).GetMethod(
            "RunPeriodicSweepAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)sweep.Invoke(scheduler, [CancellationToken.None])!;
    }

    private static async Task WaitForWakeUpSweepAsync(QuotaRetryScheduler scheduler)
    {
        var taskField = typeof(QuotaRetryScheduler).GetField(
            "_wakeUpSweepTask",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (taskField.GetValue(scheduler) is Task sweep)
            {
                await sweep;
                return;
            }
            await Task.Delay(10);
        }

        Assert.Fail("wake-up sweep task never started within the test deadline");
    }

    public sealed class SchedulerFixture : IDisposable
    {
        public SchedulerFixture(SqliteWorkItemStore store, QuotaRetryScheduler scheduler, AgentClassRouter? router = null)
        {
            Store = store;
            Scheduler = scheduler;
            Router = router;
        }

        public SqliteWorkItemStore Store { get; }
        public QuotaRetryScheduler Scheduler { get; }
        public AgentClassRouter? Router { get; }

        public void Dispose()
        {
            Scheduler.Dispose();
            Store.Dispose();
        }
    }

    // The router keys probes by AgentKind, so multi-kind tests need one probe
    // per kind. This helper bundles the two-probe construction behind a
    // single mutable state holder.
    private sealed class MutablePeerProbes
    {
        public double ClaudePct { get; private set; }
        public double CodexPct { get; private set; }

        public MutablePeerProbes(double claude, double codex)
        {
            ClaudePct = claude;
            CodexPct = codex;
        }

        public void UpdateClaude(double pct) => ClaudePct = pct;
        public void UpdateCodex(double pct) => CodexPct = pct;

        public IEnumerable<IAgentQuotaProbe> AsProbes() =>
            [
                new BoundProbe(AgentKind.Claude, () => ClaudePct),
                new BoundProbe(AgentKind.Codex, () => CodexPct),
            ];

        private sealed class BoundProbe : IAgentQuotaProbe
        {
            private readonly Func<double> _read;
            public BoundProbe(AgentKind kind, Func<double> read) { Kind = kind; _read = read; }
            public AgentKind Kind { get; }
            public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
                => Task.FromResult(new AgentQuotaSnapshot { AvailablePct = _read() });
        }
    }

    private sealed class NullResetRouter : IQuotaRetryRouter
    {
        public Task<QuotaRetryRoutingDecision> ResolveQuotaRetryAsync(
            WorkItem item, Project? project, CancellationToken ct, string? requiredCapability = null)
            => Task.FromResult(new QuotaRetryRoutingDecision(
                ShouldWait: true, NoEligibleMembers: false, Reason: "still gated"));

        public Task<DateTimeOffset?> ComputeEarliestExhaustedResetAsync(
            WorkItem item, Project? project, CancellationToken ct, string? requiredCapability = null)
            => Task.FromResult<DateTimeOffset?>(null);
    }

    private sealed class FakeAgentPauseSignal : IAgentPauseSignal
    {
        public event Action? AgentPauseChanged;
        public void FireAgentPauseChanged() => AgentPauseChanged?.Invoke();
    }

    private sealed class FakeAgentQuotaAvailabilitySignal : IAgentQuotaAvailabilitySignal
    {
        public event Action? QuotaUsableThresholdCrossed;
        public void FireQuotaUsableThresholdCrossed() => QuotaUsableThresholdCrossed?.Invoke();
    }

    private sealed class InertTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public InertTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
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
