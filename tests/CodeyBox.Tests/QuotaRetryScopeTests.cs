using System.Reflection;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Regression coverage for per-agent scoping of the quota auto-retry budget:
/// exhaustion of one agent must not consume another agent's budget, an
/// operator retry must restore a usable budget, and the terminal failure
/// message must name the agent whose quota was actually exhausted.
/// </summary>
public sealed class QuotaRetryScopeTests : IDisposable
{
    private static readonly ProjectId TestProjectId = new("test-project");
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-quota-scope-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    /// <summary>
    /// An item that accrued attempts up to the cap against agent A (claude)
    /// must still run when re-routed to agent B (codex) with available quota:
    /// the inherited count resets instead of failing the item.
    /// </summary>
    [Fact]
    public async Task RerouteToHealthyPeer_ResetsInheritedAttemptsAndRuns()
    {
        var probes = new MutablePeerProbes(claude: 100.0, codex: 0.0);
        using var fixture = BuildSchedulerWithPeers(probes);

        // Accrue the budget against claude through the production path: the
        // first sweep stamps claude's scope and consumes one attempt.
        var item = ParkedItem() with { QuotaRetryAttempts = 2 };
        await fixture.Store.CreateAsync(item);
        await RunPeriodicSweepAsync(fixture.Scheduler);

        var afterFirstRun = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(afterFirstRun);
        Assert.Equal(WorkItemState.Queued, afterFirstRun!.State);
        Assert.Equal(3, afterFirstRun.QuotaRetryAttempts);
        Assert.NotNull(afterFirstRun.QuotaRetryScope);

        // The run on claude quota-fails again and re-parks, preserving the
        // claude-scoped budget at the cap.
        var reparked = afterFirstRun.With(
            WorkItemState.WaitingForQuotaReset,
            "quota exhausted on claude",
            failureKind: "quota",
            quotaResetAt: DateTimeOffset.UtcNow.AddHours(-1)) with
        {
            NextQuotaRetryAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
        Assert.True(await fixture.Store.TryUpdateIfStateAsync(reparked, WorkItemState.Queued));
        var parked = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(parked);
        Assert.Equal(3, parked!.QuotaRetryAttempts);
        Assert.Equal(afterFirstRun.QuotaRetryScope, parked.QuotaRetryScope);

        // Claude drains while codex refills: the re-route must grant a full
        // budget against codex rather than failing on claude's count.
        probes.UpdateClaude(0.0);
        probes.UpdateCodex(100.0);
        await RunPeriodicSweepAsync(fixture.Scheduler);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.Queued, stored!.State);
        Assert.Equal(1, stored.QuotaRetryAttempts);
        Assert.NotEqual(parked.QuotaRetryScope, stored.QuotaRetryScope);
    }

    /// <summary>
    /// The terminal cap failure must name the agent whose quota was actually
    /// exhausted (the recorded scope owner), not the agent the item was last
    /// routed to — e.g. after a fallback rewrite stamps a healthy peer.
    /// </summary>
    [Fact]
    public async Task TerminalCapFailure_NamesExhaustedAgentNotLastRoutedPeer()
    {
        var probes = new MutablePeerProbes(claude: 100.0, codex: 0.0);
        using var fixture = BuildSchedulerWithPeers(probes);

        var claudeScope = QuotaRetryScope.ForAdmission(
            new QuotaRetryAdmissionPoolKey("claude", AgentKind.Claude, null));
        var item = ParkedItem() with
        {
            QuotaRetryAttempts = 3,
            QuotaRetryScope = claudeScope,
            // Simulates a fallback rewrite: the item is now stamped with the
            // healthy peer even though the budget was accrued on claude.
            Agent = AgentKind.Codex,
        };
        await fixture.Store.CreateAsync(item);

        await RunPeriodicSweepAsync(fixture.Scheduler);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.Failed, stored!.State);
        Assert.Equal("quota", stored.FailureKind);
        Assert.Equal(3, stored.QuotaRetryAttempts);
        Assert.Contains("for agent 'claude'", stored.LastError);
        Assert.Contains("operator retry required", stored.LastError);
        Assert.DoesNotContain("codex", stored.LastError, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Same-bucket exhaustion still enforces the cap: when the item remains
    /// routable only on the scope owner, reaching the cap fails it.
    /// </summary>
    [Fact]
    public async Task SameBucketAtCap_StillFails()
    {
        var probes = new MutablePeerProbes(claude: 100.0, codex: 0.0);
        using var fixture = BuildSchedulerWithPeers(probes);

        var claudeScope = QuotaRetryScope.ForAdmission(
            new QuotaRetryAdmissionPoolKey("claude", AgentKind.Claude, null));
        var item = ParkedItem() with
        {
            QuotaRetryAttempts = 3,
            QuotaRetryScope = claudeScope,
        };
        await fixture.Store.CreateAsync(item);

        await RunPeriodicSweepAsync(fixture.Scheduler);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.Failed, stored!.State);
        Assert.Equal(3, stored.QuotaRetryAttempts);
        Assert.Equal(claudeScope, stored.QuotaRetryScope);
    }

    /// <summary>
    /// An operator retry (trigger "manual") restores a usable quota budget:
    /// the counter resets to zero and the scope clears, so the retried item
    /// cannot immediately re-fail on an inherited cap. Non-operator triggers
    /// preserve the inherited count.
    /// </summary>
    [Fact]
    public async Task ManualRetry_ResetsQuotaBudget()
    {
        using var fixture = BuildSchedulerWithPeers(new MutablePeerProbes(100.0, 100.0));
        var claudeScope = QuotaRetryScope.ForAdmission(
            new QuotaRetryAdmissionPoolKey("claude", AgentKind.Claude, null));
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = TestProjectId,
            Title = "failed",
            Prompt = "p",
            State = WorkItemState.Failed,
            FailureKind = "quota",
            AgentClassId = "frontier",
            QuotaRetryAttempts = 3,
            QuotaRetryScope = claudeScope,
        };
        await fixture.Store.CreateAsync(item);

        var retrier = new WorkItemRetrier(
            fixture.Store, new InMemoryTaskQueue(), fixture.GitHost, NullLogger<WorkItemRetrier>.Instance);
        var (success, error, _, _, _) = await retrier.RetryAsync(item, from: "work", trigger: "manual");
        Assert.True(success, error);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.Queued, stored!.State);
        Assert.Equal(0, stored.QuotaRetryAttempts);
        Assert.Null(stored.QuotaRetryScope);
    }

    [Fact]
    public async Task NonManualRetry_PreservesQuotaBudget()
    {
        using var fixture = BuildSchedulerWithPeers(new MutablePeerProbes(100.0, 100.0));
        var claudeScope = QuotaRetryScope.ForAdmission(
            new QuotaRetryAdmissionPoolKey("claude", AgentKind.Claude, null));
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = TestProjectId,
            Title = "failed",
            Prompt = "p",
            State = WorkItemState.Failed,
            FailureKind = "quota",
            AgentClassId = "frontier",
            QuotaRetryAttempts = 2,
            QuotaRetryScope = claudeScope,
        };
        await fixture.Store.CreateAsync(item);

        var retrier = new WorkItemRetrier(
            fixture.Store, new InMemoryTaskQueue(), fixture.GitHost, NullLogger<WorkItemRetrier>.Instance);
        var (success, error, _, _, _) = await retrier.RetryAsync(
            item, from: "work", trigger: "terminal-failure-recovery");
        Assert.True(success, error);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(2, stored!.QuotaRetryAttempts);
    }

    private SchedulerFixture BuildSchedulerWithPeers(MutablePeerProbes probes)
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
            probes.AsProbes(),
            new QuotaRouterOptions { MinQuotaPct = 10 },
            NullLogger<AgentClassRouter>.Instance);
        var dbPath = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new SqliteWorkItemStore(dbPath);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")) },
            NullLogger<LocalGitHost>.Instance);
        var retrier = new WorkItemRetrier(store, new InMemoryTaskQueue(), gitHost, NullLogger<WorkItemRetrier>.Instance);
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
            new InertTimeProvider(DateTimeOffset.UtcNow),
            baselineResolver: null,
            autoRetryOptionsAccessor: null,
            quotaAvailabilitySignal: null,
            pauseSignal: null);
        return new SchedulerFixture(store, gitHost, scheduler);
    }

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

    public sealed class SchedulerFixture(SqliteWorkItemStore store, LocalGitHost gitHost, QuotaRetryScheduler scheduler) : IDisposable
    {
        public SqliteWorkItemStore Store { get; } = store;
        public LocalGitHost GitHost { get; } = gitHost;
        public QuotaRetryScheduler Scheduler { get; } = scheduler;

        public void Dispose()
        {
            Scheduler.Dispose();
            Store.Dispose();
        }
    }

    private sealed class MutablePeerProbes(double claude, double codex)
    {
        public double ClaudePct { get; private set; } = claude;
        public double CodexPct { get; private set; } = codex;

        public void UpdateClaude(double pct) => ClaudePct = pct;
        public void UpdateCodex(double pct) => CodexPct = pct;

        public IEnumerable<IAgentQuotaProbe> AsProbes() =>
        [
            new BoundProbe(AgentKind.Claude, () => ClaudePct),
            new BoundProbe(AgentKind.Codex, () => CodexPct),
        ];

        private sealed class BoundProbe(AgentKind kind, Func<double> read) : IAgentQuotaProbe
        {
            public AgentKind Kind { get; } = kind;
            public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
                => Task.FromResult(new AgentQuotaSnapshot { AvailablePct = read() });
        }
    }

    private sealed class InertTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;
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
