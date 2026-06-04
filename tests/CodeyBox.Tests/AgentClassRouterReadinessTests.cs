using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class AgentClassRouterReadinessTests
{
    private static readonly AgentKind Claude = AgentKind.Claude;
    private static readonly AgentKind Codex = AgentKind.Codex;

    [Fact]
    public async Task CheckReadiness_NoClassConfigured_IsNotApplicable()
    {
        var router = BuildRouter([Class(Member(Claude))], [new FakeProbe(Claude, 100)]);
        var readiness = await router.CheckReadinessAsync(
            Item() with { AgentClassId = null },
            project: null,
            new FixedCapacity(),
            CancellationToken.None);

        Assert.Equal(AgentRoutingReadinessState.NotApplicable, readiness.State);
    }

    [Fact]
    public async Task CheckReadiness_UsesClassEligibilityAndReturnsChosenAgent()
    {
        var router = BuildRouter(
            [Class(Member(Claude, score: 100), Member(Codex, score: 80, capabilities: ["sensitive"]))],
            [new FakeProbe(Claude, 100), new FakeProbe(Codex, 100)]);

        var readiness = await router.CheckReadinessAsync(
            Item(required: "sensitive"),
            project: null,
            new FixedCapacity(),
            CancellationToken.None);

        Assert.Equal(AgentRoutingReadinessState.Available, readiness.State);
        Assert.Equal(Codex, readiness.Agent);
    }

    [Fact]
    public async Task CheckReadiness_NoEligibleMembers_IsUnavailable()
    {
        var router = BuildRouter(
            [Class(Member(Claude, capabilities: ["general"]))],
            [new FakeProbe(Claude, 100)]);

        var readiness = await router.CheckReadinessAsync(
            Item(required: "sensitive"),
            project: null,
            new FixedCapacity(),
            CancellationToken.None);

        Assert.Equal(AgentRoutingReadinessState.Unavailable, readiness.State);
        Assert.Contains("ROUTING_NO_ELIGIBLE", readiness.Reason);
    }

    [Fact]
    public async Task CheckReadiness_RespectsCapacityGate()
    {
        var router = BuildRouter([Class(Member(Claude))], [new FakeProbe(Claude, 100)]);

        var readiness = await router.CheckReadinessAsync(
            Item(),
            project: null,
            new FixedCapacity(hasCapacity: false),
            CancellationToken.None);

        Assert.Equal(AgentRoutingReadinessState.Unavailable, readiness.State);
    }

    [Fact]
    public async Task CheckReadiness_RespectsAvailabilityGate()
    {
        var router = BuildRouter(
            [Class(Member(Claude))],
            [new FakeProbe(Claude, 100)],
            availability: new FixedAvailability(available: false));

        var readiness = await router.CheckReadinessAsync(
            Item(),
            project: null,
            new FixedCapacity(),
            CancellationToken.None);

        Assert.Equal(AgentRoutingReadinessState.Unavailable, readiness.State);
        Assert.Contains("smoke gate", readiness.Reason);
    }

    [Fact]
    public async Task CheckReadiness_RespectsQuotaAndBudgetGates()
    {
        var quotaBlocked = BuildRouter([Class(Member(Claude))], [new FakeProbe(Claude, 1)]);
        var budgetBlocked = BuildRouter(
            [Class(Member(Claude))],
            [new FakeProbe(Claude, 100)],
            budgetProvider: new FixedBudget(availablePct: 1));

        var quotaReadiness = await quotaBlocked.CheckReadinessAsync(
            Item(),
            project: null,
            new FixedCapacity(),
            CancellationToken.None);
        var budgetReadiness = await budgetBlocked.CheckReadinessAsync(
            Item(),
            project: null,
            new FixedCapacity(),
            CancellationToken.None);

        Assert.Equal(AgentRoutingReadinessState.Unavailable, quotaReadiness.State);
        Assert.Equal(AgentRoutingReadinessState.Unavailable, budgetReadiness.State);
    }

    [Fact]
    public async Task CheckReadiness_DoesNotConsumeQuotaRetryAdmission()
    {
        var time = new ManualTimeProvider();
        var failures = new InMemoryQuotaFailureStore();
        await failures.RecordAsync(
            Claude,
            modelId: null,
            QuotaFailureKind.LimitReached,
            time.GetUtcNow(),
            CancellationToken.None);
        var router = BuildRouter(
            [Class(Member(Claude))],
            [new FakeProbe(Claude, 100)],
            timeProvider: time,
            quotaFailures: failures);
        var item = Item();

        var retryDecision = await router.ResolveQuotaRetryAsync(item, project: null, CancellationToken.None);
        var readiness = await router.CheckReadinessAsync(
            item,
            project: null,
            new FixedCapacity(),
            CancellationToken.None);
        var dispatchDecision = await router.ResolveAsync(item, project: null, CancellationToken.None);
        var blockedAfterAdmissionConsumed = await router.ResolveAsync(item, project: null, CancellationToken.None);

        Assert.False(retryDecision.ShouldWait);
        Assert.Equal(AgentRoutingReadinessState.Available, readiness.State);
        Assert.Equal(Claude, dispatchDecision.Chosen?.Agent);
        Assert.True(blockedAfterAdmissionConsumed.ShouldWait);
        Assert.Null(blockedAfterAdmissionConsumed.Chosen);
    }

    private static AgentClassRouter BuildRouter(
        IReadOnlyList<AgentClass> classes,
        IEnumerable<IAgentQuotaProbe> probes,
        IAgentEffectiveAvailabilityReader? availability = null,
        IAgentBudgetProvider? budgetProvider = null,
        TimeProvider? timeProvider = null,
        IQuotaFailureStore? quotaFailures = null)
        => new(
            classes,
            probes,
            new QuotaRouterOptions { MinQuotaPct = 10.0, QuotaRecheckInterval = TimeSpan.FromMinutes(5) },
            NullLogger<AgentClassRouter>.Instance,
            timeProvider: timeProvider,
            quotaFailures: quotaFailures,
            budgetProvider: budgetProvider,
            dispatchAvailability: availability is null ? null : new AgentDispatchAvailability(availability));

    private static AgentClass Class(params AgentMembership[] members) => new()
    {
        Id = "frontier",
        DisplayName = "Frontier",
        Members = members,
    };

    private static AgentMembership Member(
        AgentKind agent,
        int score = 100,
        string[]? capabilities = null) => new()
        {
            Agent = agent,
            Billing = AgentBilling.Subscription,
            QualityScore = score,
            Capabilities = capabilities ?? [],
        };

    private static WorkItem Item(params string[] required) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "t",
        Prompt = "p",
        AgentClassId = "frontier",
        RequiredCapabilities = required,
    };

    private sealed class FixedCapacity : IAgentCapacitySnapshot
    {
        private readonly bool _hasCapacity;

        public FixedCapacity(bool hasCapacity = true) => _hasCapacity = hasCapacity;

        public bool HasCapacity(AgentKind agent) => _hasCapacity;
    }

    private sealed class FixedAvailability : IAgentAvailabilityRegistry, IAgentEffectiveAvailabilityReader
    {
        private readonly bool _available;

        public FixedAvailability(bool available) => _available = available;

        public AgentAvailability GetAvailability(AgentKind kind) =>
            new(_available, _available ? null : "unavailable", null);

        public AgentAvailability GetAvailabilityWithoutSmokeGateExclusions(AgentKind kind) =>
            GetAvailability(kind);

        public AvailabilityTransition RecordRunOutcome(AgentKind kind, bool success, TimeSpan duration) =>
            new(false, !_available, _available ? null : "unavailable");

        public IReadOnlyList<AgentAvailabilitySnapshot> Snapshot() => [];
    }

    private sealed class FixedBudget : IAgentBudgetProvider
    {
        private readonly double _availablePct;

        public FixedBudget(double availablePct) => _availablePct = availablePct;

        public Task<AgentQuotaSnapshot?> GetBudgetSnapshotAsync(
            AgentKind agent,
            string? modelId,
            CancellationToken ct = default) =>
            Task.FromResult<AgentQuotaSnapshot?>(new AgentQuotaSnapshot
            {
                AvailablePct = _availablePct,
            });

        public Task<IReadOnlyList<AgentBudgetUsageView>> SummariseAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AgentBudgetUsageView>>([]);
    }

    private sealed class InMemoryQuotaFailureStore : IQuotaFailureStore
    {
        private readonly List<QuotaFailureObservation> _observations = [];

        public Task RecordAsync(
            AgentKind agent,
            string? modelId,
            QuotaFailureKind kind,
            DateTimeOffset observedAt,
            CancellationToken ct = default)
        {
            _observations.Add(new QuotaFailureObservation(agent, modelId, kind, observedAt));
            return Task.CompletedTask;
        }

        public Task RecordForProjectAsync(
            AgentKind agent,
            string? modelId,
            ProjectId projectId,
            QuotaFailureKind kind,
            DateTimeOffset observedAt,
            CancellationToken ct = default)
        {
            _observations.Add(new QuotaFailureObservation(agent, modelId, kind, observedAt, projectId));
            return Task.CompletedTask;
        }

        public async Task<bool> HasRecentAsync(
            AgentKind agent,
            string? modelId,
            TimeSpan window,
            DateTimeOffset now,
            CancellationToken ct = default) =>
            await GetMostRecentAsync(agent, modelId, window, now, ct) is not null;

        public Task<DateTimeOffset?> GetMostRecentAsync(
            AgentKind agent,
            string? modelId,
            TimeSpan window,
            DateTimeOffset now,
            CancellationToken ct = default)
        {
            var latest = _observations
                .Where(o => o.Agent == agent
                    && string.Equals(o.ModelId, modelId, StringComparison.Ordinal)
                    && o.ObservedAt <= now
                    && now - o.ObservedAt <= window)
                .Select(o => (DateTimeOffset?)o.ObservedAt)
                .Max();
            return Task.FromResult(latest);
        }

        public Task<IReadOnlyList<QuotaFailureObservation>> ListRecentAsync(
            TimeSpan window,
            DateTimeOffset now,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<QuotaFailureObservation>>(
                _observations
                    .Where(o => o.ObservedAt <= now && now - o.ObservedAt <= window)
                    .ToList());

        public Task PruneOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
        {
            _observations.RemoveAll(o => o.ObservedAt < cutoff);
            return Task.CompletedTask;
        }
    }
}
