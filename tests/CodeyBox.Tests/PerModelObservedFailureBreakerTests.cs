using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Per-(agent, model) observed-failure breaker: an observed quota failure on
/// gemini/gemini-3-flash-preview must reject that specific membership while
/// leaving other gemini models on the same agent routable. Regression for the
/// scenario where the cloudcode probe reported availablePct=100 because the
/// account-wide ceiling was fine, but the per-model rolling quota was walled.
/// </summary>
public sealed class PerModelObservedFailureBreakerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-permodel-breaker-{Guid.NewGuid():N}.db");
    private readonly SqliteQuotaFailureStore _failures;

    public PerModelObservedFailureBreakerTests() => _failures = new SqliteQuotaFailureStore(_dbPath);

    public void Dispose()
    {
        _failures.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task ObservedFailureOnSpecificModel_RejectsThatModel_RoutesAlternateModel()
    {
        // gemini-3-flash-preview observed walled 8 minutes ago; the per-agent
        // probe still reports 100% (vendor exposes only the daily ceiling).
        // The router must skip the walled membership and route to gemini-2.5-pro.
        var observedAt = DateTimeOffset.UtcNow.AddMinutes(-8);
        await _failures.RecordAsync(AgentKind.Gemini, "gemini-3-flash-preview", QuotaFailureKind.LimitReached, observedAt);

        var router = BuildRouter(perAgentAvailablePct: 100);
        var decision = await router.ResolveAsync(Item(), null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(AgentKind.Gemini, decision.Chosen!.Agent);
        Assert.Equal("gemini-2.5-pro", decision.Chosen.ModelId);
    }

    [Fact]
    public async Task ObservedFailureOnDifferentModel_DoesNotBlockSameAgentDifferentModel()
    {
        // Failure on gemini-3-flash-preview must not block gemini-2.5-pro;
        // they share an agent but each has its own rolling quota window.
        await _failures.RecordAsync(AgentKind.Gemini, "gemini-3-flash-preview", QuotaFailureKind.LimitReached, DateTimeOffset.UtcNow);

        var allowsOther = !await _failures.HasRecentAsync(
            AgentKind.Gemini, "gemini-2.5-pro", TimeSpan.FromMinutes(10), DateTimeOffset.UtcNow);
        Assert.True(allowsOther);
    }

    [Fact]
    public async Task GetMostRecentAsync_ReturnsLatestObservation_WithinWindow()
    {
        var older = DateTimeOffset.UtcNow.AddMinutes(-5);
        var newer = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _failures.RecordAsync(AgentKind.Gemini, "gemini-3-flash-preview", QuotaFailureKind.LimitReached, older);
        await _failures.RecordAsync(AgentKind.Gemini, "gemini-3-flash-preview", QuotaFailureKind.LimitReached, newer);

        var observed = await _failures.GetMostRecentAsync(
            AgentKind.Gemini, "gemini-3-flash-preview", TimeSpan.FromMinutes(10), DateTimeOffset.UtcNow);

        Assert.NotNull(observed);
        Assert.Equal(newer.UtcDateTime, observed!.Value.UtcDateTime, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetMostRecentAsync_OutsideWindow_ReturnsNull()
    {
        await _failures.RecordAsync(AgentKind.Gemini, "gemini-3-flash-preview", QuotaFailureKind.LimitReached, DateTimeOffset.UtcNow.AddMinutes(-30));

        var observed = await _failures.GetMostRecentAsync(
            AgentKind.Gemini, "gemini-3-flash-preview", TimeSpan.FromMinutes(10), DateTimeOffset.UtcNow);

        Assert.Null(observed);
    }

    [Fact]
    public void FormatObservedFailureReason_LogsAgentModelAndAge()
    {
        var member = new AgentMembership
        {
            Agent = AgentKind.Gemini,
            Billing = AgentBilling.Subscription,
            ModelId = "gemini-3-flash-preview",
            QualityScore = 100,
        };
        var now = DateTimeOffset.UtcNow;
        var observedAt = now.AddMinutes(-8);

        var reason = AgentClassRouter.FormatObservedFailureReason(member, observedAt, now);

        Assert.Contains("gemini/gemini-3-flash-preview", reason);
        Assert.Contains("observed quota failure", reason);
        Assert.Contains("8 minutes ago", reason);
    }

    [Fact]
    public void FormatObservedFailureReason_DistinctFromQuotaExhausted()
    {
        // Distinct rejection reason must not collide with probe-derived
        // "quota exhausted" or floor-based "below floor" rejection reasons.
        var member = new AgentMembership
        {
            Agent = AgentKind.Gemini,
            Billing = AgentBilling.Subscription,
            ModelId = "gemini-3-flash-preview",
            QualityScore = 100,
        };
        var reason = AgentClassRouter.FormatObservedFailureReason(member, DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow);
        Assert.DoesNotContain("quota exhausted", reason);
        Assert.DoesNotContain("below floor", reason);
    }

    [Fact]
    public async Task QuotaRouter_WouldAllow_RejectsWhenRecentFailureFlagSet()
    {
        // Integration through the existing static gate: WouldAllow honours
        // the per-(agent, model) recent-failure flag the router computes.
        await _failures.RecordAsync(AgentKind.Gemini, "gemini-3-flash-preview", QuotaFailureKind.LimitReached, DateTimeOffset.UtcNow);

        var hasRecent = await _failures.HasRecentAsync(
            AgentKind.Gemini, "gemini-3-flash-preview", TimeSpan.FromMinutes(10), DateTimeOffset.UtcNow);
        var opts = new QuotaRouterOptions { MinQuotaPct = 10 };

        Assert.False(QuotaRouter.WouldAllow(availablePct: 100, recentFailure: hasRecent, opts));

        // Same flag computed for a different model on the same agent must allow.
        var hasRecentOther = await _failures.HasRecentAsync(
            AgentKind.Gemini, "gemini-2.5-pro", TimeSpan.FromMinutes(10), DateTimeOffset.UtcNow);
        Assert.True(QuotaRouter.WouldAllow(availablePct: 100, recentFailure: hasRecentOther, opts));
    }

    private AgentClassRouter BuildRouter(double perAgentAvailablePct)
    {
        // Two gemini models in the class. The probe returns a per-agent
        // snapshot only — no PerModel data — to mirror the cloudcode probe's
        // vendor-imposed limitation that motivated this bug.
        var cls = new AgentClass
        {
            Id = "gemini-mix",
            DisplayName = "Gemini Mix",
            Members =
            [
                new AgentMembership
                {
                    Agent = AgentKind.Gemini,
                    Billing = AgentBilling.Subscription,
                    ModelId = "gemini-3-flash-preview",
                    QualityScore = 100,
                },
                new AgentMembership
                {
                    Agent = AgentKind.Gemini,
                    Billing = AgentBilling.Subscription,
                    ModelId = "gemini-2.5-pro",
                    QualityScore = 99,
                },
            ],
        };

        return new AgentClassRouter(
            [cls],
            [new FakeProbe(AgentKind.Gemini, perAgentAvailablePct)],
            new QuotaRouterOptions
            {
                MinQuotaPct = 10,
                ObservedFailureWindow = TimeSpan.FromMinutes(10),
            },
            NullLogger<AgentClassRouter>.Instance,
            quotaFailures: _failures);
    }

    private static WorkItem Item() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "t",
        Prompt = "p",
        AgentClassId = "gemini-mix",
    };
}
