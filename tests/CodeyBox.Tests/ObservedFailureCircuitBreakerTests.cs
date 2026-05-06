using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class ObservedFailureCircuitBreakerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-quota-breaker-{Guid.NewGuid():N}.db");
    private readonly SqliteQuotaFailureStore _failures;

    public ObservedFailureCircuitBreakerTests() => _failures = new SqliteQuotaFailureStore(_dbPath);

    public void Dispose()
    {
        _failures.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task RecentQuotaFailure_SkipsSameAgentAndModel()
    {
        await _failures.RecordAsync(AgentKind.Claude, "claude-opus-4-7", QuotaFailureKind.LimitReached, DateTimeOffset.UtcNow);

        var decision = await BuildRouter().ResolveAsync(Item(), null, CancellationToken.None);

        Assert.Equal(AgentKind.Codex, decision.Chosen!.Agent);
    }

    [Fact]
    public async Task FailureOutsideWindow_DoesNotBlockPickup()
    {
        await _failures.RecordAsync(
            AgentKind.Claude,
            "claude-opus-4-7",
            QuotaFailureKind.LimitReached,
            DateTimeOffset.UtcNow.AddMinutes(-11));

        var decision = await BuildRouter().ResolveAsync(Item(), null, CancellationToken.None);

        Assert.Equal(AgentKind.Claude, decision.Chosen!.Agent);
    }

    [Theory]
    [InlineData("You have hit your usage limit", QuotaFailureKind.LimitReached)]
    [InlineData("error: rate_limit_exceeded", QuotaFailureKind.RateLimitExceeded)]
    [InlineData("API Error: 401 unauthorized", QuotaFailureKind.Unauthorized)]
    public void Detector_MatchesDocumentedQuotaPatternsOnly(string stderr, QuotaFailureKind expected)
    {
        Assert.Equal(expected, QuotaFailureDetector.Detect(stderr));
        Assert.Null(QuotaFailureDetector.Detect("ordinary model error"));
    }

    private AgentClassRouter BuildRouter()
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
                new AgentMembership
                {
                    Agent = AgentKind.Codex,
                    Billing = AgentBilling.Subscription,
                    ModelId = "codex-5.5",
                    QualityScore = 99,
                },
            ],
        };

        return new AgentClassRouter(
            [cls],
            [new FakeProbe(AgentKind.Claude, 80), new FakeProbe(AgentKind.Codex, 80)],
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
        AgentClassId = "frontier",
    };
}
