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

    private static AgentClassRouter BuildRouter(
        IReadOnlyList<AgentClass> classes,
        IEnumerable<IAgentQuotaProbe> probes,
        IAgentAvailabilityRegistry? availability = null,
        IAgentBudgetProvider? budgetProvider = null)
        => new(
            classes,
            probes,
            new QuotaRouterOptions { MinQuotaPct = 10.0, QuotaRecheckInterval = TimeSpan.FromMinutes(5) },
            NullLogger<AgentClassRouter>.Instance,
            availability: availability,
            budgetProvider: budgetProvider);

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

    private sealed class FixedAvailability : IAgentAvailabilityRegistry
    {
        private readonly bool _available;

        public FixedAvailability(bool available) => _available = available;

        public AgentAvailability GetAvailability(AgentKind kind) =>
            new(_available, _available ? null : "unavailable", null);

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
}
