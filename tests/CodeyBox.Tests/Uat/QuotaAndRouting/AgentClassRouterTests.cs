using System.Collections.Concurrent;
using System.Threading.Channels;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests.Uat.QuotaAndRouting;

/// <summary>
/// UAT coverage for <c>Agent class router - Chooses an agent/model using score, quota, and observed-failure gates</c>.
/// Plan anchor: docs/uat/00-plan.md#agent-class-router---chooses-an-agentmodel-using-score-quota-and-observed-failure-gates
/// </summary>
public sealed class AgentClassRouterTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-uat-router-failures-{Guid.NewGuid():N}.db");
    private readonly string _workDbPath = Path.Combine(Path.GetTempPath(), $"codeybox-uat-router-items-{Guid.NewGuid():N}.db");
    private readonly SqliteQuotaFailureStore _failures;
    private readonly SqliteWorkItemStore _items;

    public AgentClassRouterTests()
    {
        _failures = new SqliteQuotaFailureStore(_dbPath);
        _items = new SqliteWorkItemStore(_workDbPath);
    }

    public void Dispose()
    {
        _items.Dispose();
        _failures.Dispose();
        File.Delete(_dbPath);
        File.Delete(_workDbPath);
    }

    [Fact]
    public async Task WorkItemAgentClassId_FiltersByScoreAppliesTodModifierAndSelectsHighestViableMember()
    {
        var peakTime = new FixedTimeProvider(new DateTimeOffset(2026, 5, 11, 15, 0, 0, TimeSpan.Zero));
        var router = BuildRouter(
            members:
            [
                Subscription(AgentKind.Claude, score: 100),
                Subscription(AgentKind.Codex, score: 100),
                Subscription(AgentKind.Gemini, score: 94),
            ],
            probes:
            [
                new StaticQuotaProbe(AgentKind.Claude, 80),
                new StaticQuotaProbe(AgentKind.Codex, 80),
                new StaticQuotaProbe(AgentKind.Gemini, 80),
            ],
            options: new QuotaRouterOptions { MinQuotaPct = 10 },
            timeProvider: peakTime,
            todModifiers:
            [
                new ParsedTodModifier(
                    AgentKind.Claude,
                    -1,
                    [new ParsedTimeWindow(
                        new HashSet<DayOfWeek> { DayOfWeek.Monday },
                        TimeSpan.FromHours(14),
                        TimeSpan.FromHours(22))]),
            ]);

        var decision = await router.ResolveAsync(Item(agentClassId: "frontier", minModelScore: 95), null, CancellationToken.None);

        Assert.Equal(AgentKind.Codex, decision.Chosen!.Agent);
        Assert.False(decision.ShouldWait);
    }

    [Fact]
    public async Task ProjectDefaultAgentClass_RoutesItemWithoutPerItemOverride()
    {
        var router = BuildRouter(
            members: [Subscription(AgentKind.Claude, score: 100)],
            probes: [new StaticQuotaProbe(AgentKind.Claude, 80)],
            options: new QuotaRouterOptions { MinQuotaPct = 10 });
        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = "https://example.invalid/repo.git",
            DefaultAgentClass = "frontier",
        };

        var decision = await router.ResolveAsync(Item(agentClassId: null), project, CancellationToken.None);

        Assert.Equal(AgentKind.Claude, decision.Chosen!.Agent);
    }

    [Fact]
    public async Task PayPerApiMember_IsTreatedAsFullQuotaAndCanBeSelectedWhenRankedHighEnough()
    {
        var router = BuildRouter(
            members:
            [
                Subscription(AgentKind.Claude, score: 95),
                new AgentMembership
                {
                    Agent = AgentKind.Codex,
                    Billing = AgentBilling.PayPerApi,
                    QualityScore = 100,
                },
            ],
            probes: [new StaticQuotaProbe(AgentKind.Claude, 80)],
            options: new QuotaRouterOptions { MinQuotaPct = 10 });

        var decision = await router.ResolveAsync(Item("frontier"), null, CancellationToken.None);

        Assert.Equal(AgentKind.Codex, decision.Chosen!.Agent);
        Assert.Equal(AgentBilling.PayPerApi, decision.Chosen.Billing);
        Assert.Contains("100.0% available", decision.Reason);
    }

    [Fact]
    public async Task UnknownClassId_FallsThroughToDirectAgentPickWithDiagnosticReason()
    {
        var router = BuildRouter(
            members: [Subscription(AgentKind.Claude, score: 100)],
            probes: [new StaticQuotaProbe(AgentKind.Claude, 80)],
            options: new QuotaRouterOptions { MinQuotaPct = 10 });

        var decision = await router.ResolveAsync(Item("missing-class"), null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.False(decision.ShouldWait);
        Assert.Contains("unknown agent class", decision.Reason);
    }

    [Fact]
    public async Task AllMembersBelowMinModelScore_ReturnsNoEligibleMembers()
    {
        var router = BuildRouter(
            members: [Subscription(AgentKind.Claude, score: 80), Subscription(AgentKind.Codex, score: 85)],
            probes: [new StaticQuotaProbe(AgentKind.Claude, 80), new StaticQuotaProbe(AgentKind.Codex, 80)],
            options: new QuotaRouterOptions { MinQuotaPct = 10 });

        var decision = await router.ResolveAsync(Item(agentClassId: "frontier", minModelScore: 95), null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.False(decision.ShouldWait);
        Assert.True(decision.NoEligibleMembers);
        Assert.Contains("ROUTING_NO_ELIGIBLE", decision.Reason);
    }

    [Fact]
    public async Task UnknownQuotaWithObservedFailures_BlocksSameAgentAndModel()
    {
        var now = new DateTimeOffset(2026, 5, 13, 10, 0, 0, TimeSpan.Zero);
        await _failures.RecordAsync(
            AgentKind.Claude,
            "claude-opus-4-7",
            QuotaFailureKind.LimitReached,
            now.AddMinutes(-2));
        var router = BuildRouter(
            members:
            [
                Subscription(AgentKind.Claude, score: 100, modelId: "claude-opus-4-7"),
            ],
            probes: [new StaticQuotaProbe(AgentKind.Claude, -1)],
            options: new QuotaRouterOptions
            {
                MinQuotaPct = 10,
                UnknownPolicy = QuotaUnknownPolicy.UseObservedFailures,
                ObservedFailureWindow = TimeSpan.FromMinutes(10),
            },
            timeProvider: new FixedTimeProvider(now),
            failures: _failures);

        var decision = await router.ResolveAsync(Item("frontier"), null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
        Assert.Contains("below the effective quota floor", decision.Reason);
    }

    [Fact]
    public async Task UnknownQuotaWithProjectObservedFailure_BlocksSameAgentAndModelAcrossProjects()
    {
        var now = new DateTimeOffset(2026, 5, 13, 10, 0, 0, TimeSpan.Zero);
        await _failures.RecordForProjectAsync(
            AgentKind.Claude,
            "claude-opus-4-7",
            new ProjectId("project-a"),
            QuotaFailureKind.LimitReached,
            now.AddMinutes(-2));
        var router = BuildRouter(
            members:
            [
                Subscription(AgentKind.Claude, score: 100, modelId: "claude-opus-4-7"),
            ],
            probes: [new StaticQuotaProbe(AgentKind.Claude, -1)],
            options: new QuotaRouterOptions
            {
                MinQuotaPct = 10,
                UnknownPolicy = QuotaUnknownPolicy.UseObservedFailures,
                ObservedFailureWindow = TimeSpan.FromMinutes(10),
            },
            timeProvider: new FixedTimeProvider(now),
            failures: _failures);

        var sameProject = await router.ResolveAsync(Item("frontier", projectId: "project-a"), null, CancellationToken.None);
        var otherProject = await router.ResolveAsync(Item("frontier", projectId: "project-b"), null, CancellationToken.None);

        Assert.Null(sameProject.Chosen);
        Assert.True(sameProject.ShouldWait);
        Assert.Null(otherProject.Chosen);
        Assert.True(otherProject.ShouldWait);
    }

    [Fact]
    public async Task AllSubscriptionMembersExhausted_ReenqueuesItemAfterConfiguredRecheckInterval()
    {
        var interval = TimeSpan.FromMilliseconds(50);
        var router = BuildRouter(
            members: [Subscription(AgentKind.Claude, score: 100), Subscription(AgentKind.Codex, score: 99)],
            probes: [new StaticQuotaProbe(AgentKind.Claude, 2), new StaticQuotaProbe(AgentKind.Codex, 3)],
            options: new QuotaRouterOptions
            {
                MinQuotaPct = 10,
                QuotaRecheckInterval = interval,
            });
        var item = Item("frontier") with { State = WorkItemState.Queued };
        await _items.CreateAsync(item);

        var queue = new RecordingTaskQueue();
        var pipeline = new RecordingPipelineRunner();
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var service = new OrchestratorService(
            queue,
            _items,
            pipeline,
            registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            router);

        await service.StartAsync(CancellationToken.None);
        try
        {
            var reenqueuedId = await queue.SecondEnqueue.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stored = await _items.GetAsync(item.Id);

            Assert.Equal(item.Id, reenqueuedId);
            Assert.Equal(WorkItemState.Queued, stored!.State);
            Assert.Equal(0, pipeline.RunCount);
            Assert.Equal([item.Id, item.Id], queue.EnqueuedIds.Take(2).ToArray());
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void InvalidTimeWindowConfig_StartupValidationRejectsConfiguration()
    {
        using var factory = new ValidationTestFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:AgentScoreModifiers:ByTimeOfDay:0:Agent"] = "claude",
            ["CodeyBox:AgentScoreModifiers:ByTimeOfDay:0:Modifier"] = "-1",
            ["CodeyBox:AgentScoreModifiers:ByTimeOfDay:0:Windows:0:Days:0"] = "Funday",
            ["CodeyBox:AgentScoreModifiers:ByTimeOfDay:0:Windows:0:StartUtc"] = "14:00",
            ["CodeyBox:AgentScoreModifiers:ByTimeOfDay:0:Windows:0:EndUtc"] = "22:00",
        });

        var ex = Assert.Throws<InvalidOperationException>(
            () => factory.Services.GetRequiredService<CodeyBox.Orchestrator.AgentClassRouter>());

        Assert.Contains("unknown day code", ex.Message);
    }

    [Fact]
    public void GeminiHighScoreWithoutHighReasoning_StartupValidationRejectsUnsafeConfig()
    {
        using var factory = new ValidationTestFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:AgentClasses:0:Id"] = "frontier",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "gemini",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "95",
        });

        var ex = Assert.Throws<InvalidOperationException>(
            () => factory.Services.GetRequiredService<CodeyBox.Orchestrator.AgentClassRouter>());

        Assert.Contains("ReasoningMode=\"high\"", ex.Message);
    }

    private static CodeyBox.Orchestrator.AgentClassRouter BuildRouter(
        AgentMembership[] members,
        IAgentQuotaProbe[] probes,
        QuotaRouterOptions options,
        TimeProvider? timeProvider = null,
        IReadOnlyList<ParsedTodModifier>? todModifiers = null,
        IQuotaFailureStore? failures = null)
    {
        var agentClass = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members = members,
        };

        return new CodeyBox.Orchestrator.AgentClassRouter(
            [agentClass],
            probes,
            options,
            NullLogger<CodeyBox.Orchestrator.AgentClassRouter>.Instance,
            timeProvider,
            todModifiers,
            failures);
    }

    private static AgentMembership Subscription(AgentKind kind, int score, string? modelId = null) => new()
    {
        Agent = kind,
        Billing = AgentBilling.Subscription,
        ModelId = modelId,
        QualityScore = score,
    };

    private static WorkItem Item(string? agentClassId, int minModelScore = 0, string projectId = "test-project") => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId(projectId),
        Title = "Agent router UAT",
        Prompt = "route this",
        AgentClassId = agentClassId,
        MinModelScore = minModelScore,
    };

    private sealed class StaticQuotaProbe : IAgentQuotaProbe
    {
        private readonly AgentQuotaSnapshot _snapshot;

        public StaticQuotaProbe(AgentKind kind, double availablePct)
            : this(kind, new AgentQuotaSnapshot { AvailablePct = availablePct })
        {
        }

        private StaticQuotaProbe(AgentKind kind, AgentQuotaSnapshot snapshot)
        {
            Kind = kind;
            _snapshot = snapshot;
        }

        public AgentKind Kind { get; }

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
            => Task.FromResult(_snapshot);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class RecordingTaskQueue : ITaskQueue
    {
        private readonly Channel<WorkItemId> _channel = Channel.CreateUnbounded<WorkItemId>();
        private readonly ConcurrentQueue<WorkItemId> _enqueued = new();
        private int _enqueueCount;

        public TaskCompletionSource<WorkItemId> SecondEnqueue { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WorkItemId[] EnqueuedIds => _enqueued.ToArray();

        public int Count => _channel.Reader.Count;

        public async ValueTask EnqueueAsync(WorkItemId id, CancellationToken ct = default)
        {
            var count = Interlocked.Increment(ref _enqueueCount);
            _enqueued.Enqueue(id);
            if (count == 2)
                SecondEnqueue.TrySetResult(id);

            await _channel.Writer.WriteAsync(id, ct);
        }

        public async ValueTask<WorkItemId?> DequeueAsync(CancellationToken ct = default)
        {
            try
            {
                return await _channel.Reader.ReadAsync(ct);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }
    }

    private sealed class RecordingPipelineRunner : IPipelineRunner
    {
        private int _runCount;

        public int RunCount => Volatile.Read(ref _runCount);

        public Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            Interlocked.Increment(ref _runCount);
            return Task.CompletedTask;
        }
    }
}
