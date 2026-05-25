using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class QuotaUnknownPolicyTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-quota-policy-{Guid.NewGuid():N}.db");
    private readonly SqliteQuotaFailureStore _failures;

    public QuotaUnknownPolicyTests() => _failures = new SqliteQuotaFailureStore(_dbPath);

    public void Dispose()
    {
        _failures.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task FailOpen_AllowsUnknownQuota()
    {
        var decision = await Build(QuotaUnknownPolicy.FailOpen).ResolveAsync(Item(), null, CancellationToken.None);
        Assert.Equal(AgentKind.Claude, decision.Chosen!.Agent);
    }

    [Fact]
    public async Task FailCautious_BlocksUnknownQuota()
    {
        var decision = await Build(QuotaUnknownPolicy.FailCautious).ResolveAsync(Item(), null, CancellationToken.None);
        Assert.True(decision.ShouldWait);
        Assert.Null(decision.Chosen);
    }

    [Fact]
    public async Task UseObservedFailures_AllowsUnknownWhenNoRecentFailure()
    {
        var decision = await Build(QuotaUnknownPolicy.UseObservedFailures).ResolveAsync(Item(), null, CancellationToken.None);
        Assert.Equal(AgentKind.Claude, decision.Chosen!.Agent);
    }

    [Fact]
    public async Task UseObservedFailures_BlocksUnknownAfterRecentQuotaFailure()
    {
        await _failures.RecordAsync(AgentKind.Claude, "claude-opus-4-7", QuotaFailureKind.LimitReached, DateTimeOffset.UtcNow);

        var decision = await Build(QuotaUnknownPolicy.UseObservedFailures).ResolveAsync(Item(), null, CancellationToken.None);
        Assert.True(decision.ShouldWait);
        Assert.Null(decision.Chosen);
    }

    [Fact]
    public async Task FailOpen_BlocksUnknownQuotaWhenReservedHeadroomIsPending()
    {
        var (router, manager) = BuildWithManager(
            QuotaUnknownPolicy.FailOpen,
            new FixedHeadroomEstimator(10.0));
        var reservation = await manager.TryReserveAsync(new QuotaHeadroomGateRequest(
            new ProjectId("proj"),
            ClaudeMember(),
            AvailablePct: 25,
            ResetAt: null,
            AuditOnRefusal: false));
        Assert.True(reservation.Allow, reservation.Reason);
        var lease = Assert.IsAssignableFrom<IQuotaReservationLease>(reservation.Reservation);

        var decision = await router.ResolveAsync(Item(), null, CancellationToken.None);

        Assert.True(decision.ShouldWait);
        Assert.Null(decision.Chosen);
        await lease.ReleaseAsync(quotaMayHaveBeenConsumed: false);
    }

    private AgentClassRouter Build(QuotaUnknownPolicy policy) => BuildWithManager(policy).Router;

    private (AgentClassRouter Router, IQuotaHeadroomManager Manager) BuildWithManager(
        QuotaUnknownPolicy policy,
        IQuotaHeadroomEstimator? estimator = null)
    {
        var cls = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members =
            [
                ClaudeMember(),
            ],
        };
        var probes = new IAgentQuotaProbe[] { new FakeProbe(AgentKind.Claude, -1) };
        var opts = new QuotaRouterOptions { MinQuotaPct = 10, UnknownPolicy = policy };
        var manager = new InProcessQuotaHeadroomManager(
            estimator,
            probes,
            opts,
            NullLogger<InProcessQuotaHeadroomManager>.Instance,
            quotaFailures: _failures);

        var router = new AgentClassRouter(
            [cls],
            probes,
            opts,
            NullLogger<AgentClassRouter>.Instance,
            quotaFailures: _failures,
            headroomManager: manager);
        return (router, manager);
    }

    private static AgentMembership ClaudeMember() => new()
    {
        Agent = AgentKind.Claude,
        Billing = AgentBilling.Subscription,
        ModelId = "claude-opus-4-7",
        QualityScore = 100,
    };

    private static WorkItem Item() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "t",
        Prompt = "p",
        AgentClassId = "frontier",
    };
}
