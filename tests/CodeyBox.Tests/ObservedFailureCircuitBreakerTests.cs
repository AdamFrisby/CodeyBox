using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
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
    public async Task ProjectScopedQuotaFailure_DoesNotBlockDifferentProject()
    {
        var now = DateTimeOffset.UtcNow;
        await QuotaFailureDetector.RecordIfQuotaFailureAsync(
            _failures,
            AgentKind.Claude,
            "claude-opus-4-7",
            summary: "agent exited 1",
            stderr: "API Error: 401",
            now,
            TimeSpan.FromMinutes(30),
            CancellationToken.None,
            projectId: new ProjectId("proj-a"));

        Assert.True(await _failures.HasRecentForProjectAsync(
            AgentKind.Claude,
            "claude-opus-4-7",
            new ProjectId("proj-a"),
            TimeSpan.FromMinutes(10),
            now));
        Assert.False(await _failures.HasRecentForProjectAsync(
            AgentKind.Claude,
            "claude-opus-4-7",
            new ProjectId("proj-b"),
            TimeSpan.FromMinutes(10),
            now));

        var decision = await BuildRouter().ResolveAsync(Item(projectId: "proj-b"), null, CancellationToken.None);

        Assert.Equal(AgentKind.Claude, decision.Chosen!.Agent);
    }

    [Fact]
    public async Task ModelSpecificFailure_DoesNotBlockDifferentModel()
    {
        await _failures.RecordAsync(AgentKind.Claude, "claude-opus-4-7", QuotaFailureKind.LimitReached, DateTimeOffset.UtcNow);

        Assert.False(await _failures.HasRecentAsync(
            AgentKind.Claude,
            "claude-sonnet-4-6",
            TimeSpan.FromMinutes(10),
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ModelSpecificFailure_DoesNotBlockDefaultModel()
    {
        await _failures.RecordAsync(AgentKind.Claude, "claude-opus-4-7", QuotaFailureKind.LimitReached, DateTimeOffset.UtcNow);

        Assert.False(await _failures.HasRecentAsync(
            AgentKind.Claude,
            modelId: null,
            TimeSpan.FromMinutes(10),
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task DefaultModelFailure_BlocksOnlyDefaultModel()
    {
        await _failures.RecordAsync(AgentKind.Claude, modelId: null, QuotaFailureKind.LimitReached, DateTimeOffset.UtcNow);

        Assert.True(await _failures.HasRecentAsync(
            AgentKind.Claude,
            modelId: null,
            TimeSpan.FromMinutes(10),
            DateTimeOffset.UtcNow));
        Assert.False(await _failures.HasRecentAsync(
            AgentKind.Claude,
            "claude-opus-4-7",
            TimeSpan.FromMinutes(10),
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void PipelineRunner_UsesConcreteRunnerDefaultForObservedFailureKey()
    {
        Assert.Equal(
            "claude-opus-4-7",
            PipelineRunner.ResolveObservedModelId(new ClaudeAgentRunner(), modelId: null));
        Assert.Equal(
            "gpt-5.5",
            PipelineRunner.ResolveObservedModelId(new CodexAgentRunner(), modelId: null));
        Assert.Equal(
            "claude-sonnet-4-6",
            PipelineRunner.ResolveObservedModelId(new ClaudeAgentRunner(), "claude-sonnet-4-6"));
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

    [Fact]
    public async Task RecordIfQuotaFailure_RequiresAgentExitedOne()
    {
        var now = DateTimeOffset.UtcNow;
        await QuotaFailureDetector.RecordIfQuotaFailureAsync(
            _failures,
            AgentKind.Claude,
            "claude-opus-4-7",
            summary: "agent exited 2",
            stderr: "API Error: 401",
            now,
            TimeSpan.FromMinutes(30),
            CancellationToken.None);

        Assert.False(await _failures.HasRecentAsync(
            AgentKind.Claude,
            "claude-opus-4-7",
            TimeSpan.FromMinutes(10),
            now));

        await QuotaFailureDetector.RecordIfQuotaFailureAsync(
            _failures,
            AgentKind.Claude,
            "claude-opus-4-7",
            summary: "agent exited 1",
            stderr: "API Error: 401",
            now,
            TimeSpan.FromMinutes(30),
            CancellationToken.None);

        Assert.True(await _failures.HasRecentAsync(
            AgentKind.Claude,
            "claude-opus-4-7",
            TimeSpan.FromMinutes(10),
            now));
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

    private static WorkItem Item(string projectId = "proj") => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId(projectId),
        Title = "t",
        Prompt = "p",
        AgentClassId = "frontier",
    };
}
