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
        TestTempArtifacts.DeleteSqliteDatabase(_dbPath);
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

    private AgentClassRouter Build(QuotaUnknownPolicy policy)
    {
        var cls = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members =
            [
                new AgentMembership
                {
                    Agent = AgentKind.Claude,
                    Billing = AgentBilling.Subscription,
                    ModelId = "claude-opus-4-7",
                    QualityScore = 100,
                },
            ],
        };

        return new AgentClassRouter(
            [cls],
            [new FakeProbe(AgentKind.Claude, -1)],
            new QuotaRouterOptions { MinQuotaPct = 10, UnknownPolicy = policy },
            NullLogger<AgentClassRouter>.Instance,
            quotaFailures: _failures);
    }

    private static WorkItem Item() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "t",
        Prompt = "p",
        AgentClassId = "frontier",
    };
}
