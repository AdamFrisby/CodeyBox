using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="AgentClassRouter"/>.
/// Uses a programmable fake probe to verify member preference, threshold
/// gate, wait-for-subscription behaviour, and PayPerApi fallthrough.
/// </summary>
public sealed class AgentClassRouterTests
{
    private static readonly AgentKind Claude = AgentKind.Claude;
    private static readonly AgentKind Codex = AgentKind.Codex;

    private static AgentClassRouter BuildRouter(
        IEnumerable<AgentClass> catalog,
        IEnumerable<IAgentQuotaProbe> probes,
        double minQuotaPct = 10.0)
    {
        var opts = new QuotaRouterOptions { MinQuotaPct = minQuotaPct, QuotaRecheckInterval = TimeSpan.FromMinutes(5) };
        return new AgentClassRouter(catalog.ToList(), probes, opts, NullLogger<AgentClassRouter>.Instance);
    }

    private static WorkItem MakeItem(string? classId = null) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "t",
        Prompt = "p",
        AgentClassId = classId,
    };

    private static Project MakeProject(string? defaultClass = null) => new()
    {
        Id = new ProjectId("proj"),
        DisplayName = "Test",
        RepositoryUrl = "https://git.example.com/repo",
        DefaultAgentClass = defaultClass,
    };

    private static AgentClass FrontierClass(params AgentMembership[] members) => new()
    {
        Id = "frontier",
        DisplayName = "Frontier",
        Members = members,
    };

    private static AgentMembership Sub(AgentKind kind) =>
        new() { Agent = kind, Billing = AgentBilling.Subscription, QualityScore = 100 };

    private static AgentMembership Api(AgentKind kind) =>
        new() { Agent = kind, Billing = AgentBilling.PayPerApi, QualityScore = 100 };

    // ── No class configured ──────────────────────────────────────────────────

    [Fact]
    public async Task NoClassId_ReturnsNoChosen_NoWait()
    {
        var router = BuildRouter([], []);
        var decision = await router.ResolveAsync(MakeItem(classId: null), MakeProject(), CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.False(decision.ShouldWait);
    }

    [Fact]
    public async Task NoClassId_ButProjectDefault_PicksFromClass()
    {
        var cls = FrontierClass(Sub(Claude));
        var router = BuildRouter([cls], [new FakeProbe(Claude, 50.0)]);
        var decision = await router.ResolveAsync(MakeItem(classId: null), MakeProject("frontier"), CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Claude, decision.Chosen!.Agent);
        Assert.False(decision.ShouldWait);
    }

    // ── Member preference ────────────────────────────────────────────────────

    [Fact]
    public async Task FirstMember_AvailablePct_HighEnough_Chosen()
    {
        var cls = FrontierClass(Sub(Claude), Sub(Codex));
        var router = BuildRouter([cls], [new FakeProbe(Claude, 50.0), new FakeProbe(Codex, 50.0)]);
        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(Claude, decision.Chosen!.Agent);
        Assert.False(decision.ShouldWait);
    }

    [Fact]
    public async Task FirstMember_Exhausted_FallsBackToSecond()
    {
        var cls = FrontierClass(Sub(Claude), Sub(Codex));
        var router = BuildRouter([cls], [new FakeProbe(Claude, 5.0), new FakeProbe(Codex, 60.0)]);
        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(Codex, decision.Chosen!.Agent);
        Assert.False(decision.ShouldWait);
    }

    [Fact]
    public async Task UnknownAvailablePct_TreatedAsAvailable_FailOpen()
    {
        var cls = FrontierClass(Sub(Claude));
        var router = BuildRouter([cls], [new FakeProbe(Claude, -1.0)]);
        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(Claude, decision.Chosen!.Agent);
        Assert.False(decision.ShouldWait);
    }

    // ── Threshold gate ───────────────────────────────────────────────────────

    [Fact]
    public async Task ExactlyAtThreshold_Chosen()
    {
        var cls = FrontierClass(Sub(Claude));
        var router = BuildRouter([cls], [new FakeProbe(Claude, 10.0)], minQuotaPct: 10.0);
        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(Claude, decision.Chosen!.Agent);
    }

    [Fact]
    public async Task JustBelowThreshold_Skipped()
    {
        var cls = FrontierClass(Sub(Claude), Sub(Codex));
        var router = BuildRouter([cls], [new FakeProbe(Claude, 9.9), new FakeProbe(Codex, 15.0)], minQuotaPct: 10.0);
        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(Codex, decision.Chosen!.Agent);
    }

    // ── Wait for subscription ─────────────────────────────────────────────────

    [Fact]
    public async Task AllSubscriptionMembersExhausted_ShouldWait()
    {
        var cls = FrontierClass(Sub(Claude), Sub(Codex));
        var router = BuildRouter([cls], [new FakeProbe(Claude, 2.0), new FakeProbe(Codex, 3.0)]);
        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
        Assert.True(decision.SuggestedRecheckIn > TimeSpan.Zero);
    }

    // ── PayPerApi fallthrough ─────────────────────────────────────────────────

    [Fact]
    public async Task PayPerApiMember_NeverWaits_AlwaysFires()
    {
        var cls = FrontierClass(Api(Claude));
        var router = BuildRouter([cls], []);   // no probe needed for PayPerApi
        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(Claude, decision.Chosen!.Agent);
        Assert.Equal(AgentBilling.PayPerApi, decision.Chosen.Billing);
        Assert.False(decision.ShouldWait);
    }

    [Fact]
    public async Task SubscriptionExhausted_FallsBackToPayPerApi()
    {
        var cls = FrontierClass(Sub(Claude), Api(Codex));
        var router = BuildRouter([cls], [new FakeProbe(Claude, 1.0)]);
        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(Codex, decision.Chosen!.Agent);
        Assert.Equal(AgentBilling.PayPerApi, decision.Chosen.Billing);
        Assert.False(decision.ShouldWait);
    }

    // ── No probe registered → unknown policy ────────────────────────────────

    [Fact]
    public async Task NoProbeRegistered_ForSubscriptionMember_TreatedAsAvailable()
    {
        var cls = FrontierClass(Sub(Claude));
        var router = BuildRouter([cls], []);   // no Claude probe registered
        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(Claude, decision.Chosen!.Agent);
        Assert.False(decision.ShouldWait);
    }

    // ── Unknown class id ──────────────────────────────────────────────────────

    [Fact]
    public async Task UnknownClassId_FallsThrough_NoChosen()
    {
        var router = BuildRouter([], []);
        var decision = await router.ResolveAsync(MakeItem("does-not-exist"), null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.False(decision.ShouldWait);
    }
}

/// <summary>Fake probe that always returns a fixed AvailablePct.</summary>
internal sealed class FakeProbe : IAgentQuotaProbe
{
    private readonly AgentQuotaSnapshot _snapshot;

    public FakeProbe(AgentKind kind, double availablePct)
        : this(kind, new AgentQuotaSnapshot { AvailablePct = availablePct })
    {
    }

    public FakeProbe(AgentKind kind, AgentQuotaSnapshot snapshot)
    {
        Kind = kind;
        _snapshot = snapshot;
    }

    public AgentKind Kind { get; }

    public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
        => Task.FromResult(_snapshot);
}
