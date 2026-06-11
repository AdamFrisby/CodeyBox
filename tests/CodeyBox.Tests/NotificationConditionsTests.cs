using CodeyBox.Core;
using CodeyBox.Notifications;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

public sealed class NotificationConditionsTests
{
    private static IAgentQuotaGate QuotaGate(double minQuotaPct)
        => QuotaGate(new QuotaRouterOptions { MinQuotaPct = minQuotaPct });

    private static IAgentQuotaGate QuotaGate(QuotaRouterOptions options)
        => new QuotaGateAvailability(new QuotaGatePolicy(options));

    private static OrchestratorStallCondition CreateStallCondition(OrchestratorProgressClock clock, int thresholdMinutes)
    {
        var opts = new NotificationsOptions
        {
            Rules =
            [
                new NotificationRuleOptions
                {
                    Condition = "orchestrator_stall",
                    StallThresholdMinutes = thresholdMinutes,
                },
            ],
        };
        var monitor = new StaticOptionsMonitor<NotificationsOptions>(opts);
        return new OrchestratorStallCondition(clock, monitor);
    }

    // ── OrchestratorStallCondition ─────────────────────────────────────────

    [Fact]
    public async Task OrchestratorStall_NotStalledWhenNoTransitions()
    {
        var clock = new OrchestratorProgressClock();
        var condition = CreateStallCondition(clock, 10);
        Assert.False(await condition.EvaluateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task OrchestratorStall_NotStalledWhenRecentTransition()
    {
        var clock = new OrchestratorProgressClock();
        clock.Stamp(DateTimeOffset.UtcNow);
        var condition = CreateStallCondition(clock, 10);
        Assert.False(await condition.EvaluateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task OrchestratorStall_StalledWhenThresholdExceeded()
    {
        var clock = new OrchestratorProgressClock();
        clock.Stamp(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20));
        var condition = CreateStallCondition(clock, 10);
        Assert.True(await condition.EvaluateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task OrchestratorStall_TrueExactlyAtThreshold()
    {
        var clock = new OrchestratorProgressClock();
        clock.Stamp(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10));
        var condition = CreateStallCondition(clock, 10);
        Assert.True(await condition.EvaluateAsync(CancellationToken.None));
    }

    [Fact]
    public void OrchestratorProgressClock_IsMonotonic()
    {
        var clock = new OrchestratorProgressClock();
        var t1 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 1, 1, 11, 0, 0, TimeSpan.Zero); // earlier

        clock.Stamp(t1);
        Assert.Equal(t1, clock.LastTransition);

        // Stamp an earlier timestamp — monotonic guard should keep t1.
        clock.Stamp(t2);
        Assert.Equal(t1, clock.LastTransition);

        // Stamp a later timestamp — should advance.
        var t3 = new DateTimeOffset(2026, 1, 1, 13, 0, 0, TimeSpan.Zero);
        clock.Stamp(t3);
        Assert.Equal(t3, clock.LastTransition);
    }

    // ── SandboxLeakReapedCondition ─────────────────────────────────────────

    [Fact]
    public async Task SandboxLeakReaped_FiresOnNewLeak()
    {
        var sink = new LeakDetectionSink();
        var condition = new SandboxLeakReapedCondition(sink);

        // Initial state: no leaks.
        Assert.False(await condition.EvaluateAsync(CancellationToken.None));

        // Leak detected.
        sink.Increment();
        Assert.True(await condition.EvaluateAsync(CancellationToken.None));

        // Already consumed the edge.
        Assert.False(await condition.EvaluateAsync(CancellationToken.None));

        // Another leak.
        sink.Increment();
        Assert.True(await condition.EvaluateAsync(CancellationToken.None));
    }

    // ── QueueEmptyCondition ────────────────────────────────────────────────

    [Fact]
    public async Task QueueEmpty_TrueWhenNoWorkingItems()
    {
        var store = new StubWorkItemStore(workingCount: 0);
        var condition = new QueueEmptyCondition(store);
        Assert.True(await condition.EvaluateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task QueueEmpty_FalseWhenItemsWorking()
    {
        var store = new StubWorkItemStore(workingCount: 3);
        var condition = new QueueEmptyCondition(store);
        Assert.False(await condition.EvaluateAsync(CancellationToken.None));
    }

    // ── WorkItemPermanentlyFailedCondition ─────────────────────────────────

    [Fact]
    public async Task WorkItemPermanentlyFailed_FiresWhenCountIncreases()
    {
        var store = new StubWorkItemStore(failedCount: 0, abandonedCount: 0);
        var condition = new WorkItemPermanentlyFailedCondition(store);

        // Prime: no edge yet.
        Assert.False(await condition.EvaluateAsync(CancellationToken.None));

        store.FailedCount = 1;
        Assert.True(await condition.EvaluateAsync(CancellationToken.None));

        // Already fired.
        Assert.False(await condition.EvaluateAsync(CancellationToken.None));

        store.AbandonedCount = 1;
        Assert.True(await condition.EvaluateAsync(CancellationToken.None));
    }

    // ── AllQuotasExhaustedCondition ────────────────────────────────────────

    [Fact]
    public async Task AllQuotasExhausted_NoProbes_ReturnsFalse()
    {
        var registry = new StubAgentRegistry();
        var condition = new AllQuotasExhaustedCondition(
            [], QuotaGate(10),
            registry,
            NullLogger<AllQuotasExhaustedCondition>.Instance);
        Assert.False(await condition.EvaluateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AllQuotasExhausted_AllProbesBelowThreshold_ReturnsTrue()
    {
        var probes = new IAgentQuotaProbe[]
        {
            new StubQuotaProbe(new AgentKind("claude"), 5),
            new StubQuotaProbe(new AgentKind("codex"), 3),
        };
        var registry = new StubAgentRegistry(probes[0].Kind, probes[1].Kind);
        var condition = new AllQuotasExhaustedCondition(
            probes, QuotaGate(10),
            registry,
            NullLogger<AllQuotasExhaustedCondition>.Instance);
        Assert.True(await condition.EvaluateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AllQuotasExhausted_OneProbeAboveThreshold_ReturnsFalse()
    {
        var probes = new IAgentQuotaProbe[]
        {
            new StubQuotaProbe(new AgentKind("claude"), 5),
            new StubQuotaProbe(new AgentKind("codex"), 15),
        };
        var registry = new StubAgentRegistry(probes[0].Kind, probes[1].Kind);
        var condition = new AllQuotasExhaustedCondition(
            probes, QuotaGate(10),
            registry,
            NullLogger<AllQuotasExhaustedCondition>.Instance);
        Assert.False(await condition.EvaluateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AllQuotasExhausted_PerAgentFloorAllowsBurnAgent_ReturnsFalse()
    {
        var probes = new IAgentQuotaProbe[]
        {
            new StubQuotaProbe(new AgentKind("claude"), 5),
            new StubQuotaProbe(new AgentKind("codex"), 5),
        };
        var options = new QuotaRouterOptions { MinQuotaPct = 10 };
        options.FloorByAgent["codex"] = new QuotaFloorOverrideOptions { MinQuotaPct = 1 };
        var registry = new StubAgentRegistry(probes[0].Kind, probes[1].Kind);
        var condition = new AllQuotasExhaustedCondition(
            probes, QuotaGate(options),
            registry,
            NullLogger<AllQuotasExhaustedCondition>.Instance);

        Assert.False(await condition.EvaluateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AllQuotasExhausted_ProbeNotInRegistry_Excluded()
    {
        var probes = new IAgentQuotaProbe[]
        {
            new StubQuotaProbe(new AgentKind("claude"), 5),
            new StubQuotaProbe(new AgentKind("codex"), 3),
        };
        // Registry only contains claude — codex is not available.
        var registry = new StubAgentRegistry(probes[0].Kind);
        var condition = new AllQuotasExhaustedCondition(
            probes, QuotaGate(10),
            registry,
            NullLogger<AllQuotasExhaustedCondition>.Instance);
        // Only claude is considered, and it's below threshold.
        Assert.True(await condition.EvaluateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AllQuotasExhausted_AvailablePctNegative_ShortCircuitsFalse()
    {
        var probes = new IAgentQuotaProbe[]
        {
            new StubQuotaProbe(new AgentKind("claude"), -1),
            new StubQuotaProbe(new AgentKind("codex"), 3),
        };
        var registry = new StubAgentRegistry(probes[0].Kind, probes[1].Kind);
        var condition = new AllQuotasExhaustedCondition(
            probes, QuotaGate(10),
            registry,
            NullLogger<AllQuotasExhaustedCondition>.Instance);
        // claude probe returns -1 (unknown) — short-circuits to false.
        Assert.False(await condition.EvaluateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AllQuotasExhausted_ProbeThrows_TreatedAsBelowThreshold()
    {
        var probes = new IAgentQuotaProbe[]
        {
            new StubQuotaProbe(new AgentKind("claude"), 5),
            new ThrowingQuotaProbe(new AgentKind("codex")),
        };
        var registry = new StubAgentRegistry(probes[0].Kind, probes[1].Kind);
        var condition = new AllQuotasExhaustedCondition(
            probes, QuotaGate(10),
            registry,
            NullLogger<AllQuotasExhaustedCondition>.Instance);
        Assert.True(await condition.EvaluateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AllQuotasExhausted_ExactlyAtThreshold_ReturnsFalse()
    {
        var probes = new IAgentQuotaProbe[]
        {
            new StubQuotaProbe(new AgentKind("claude"), 10),
            new StubQuotaProbe(new AgentKind("codex"), 5),
        };
        var registry = new StubAgentRegistry(probes[0].Kind, probes[1].Kind);
        var condition = new AllQuotasExhaustedCondition(
            probes, QuotaGate(10),
            registry,
            NullLogger<AllQuotasExhaustedCondition>.Instance);
        // claude is at exactly 10 (>= minQuotaPct), so returns false.
        Assert.False(await condition.EvaluateAsync(CancellationToken.None));
    }

    // ── OrchestratorStallCondition hot-reload ───────────────────────────────

    [Fact]
    public async Task OrchestratorStall_UsesCurrentThresholdFromMonitor()
    {
        var clock = new OrchestratorProgressClock();
        clock.Stamp(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10));
        var opts = new NotificationsOptions
        {
            Rules =
            [
                new NotificationRuleOptions
                {
                    Condition = "orchestrator_stall",
                    StallThresholdMinutes = 20,
                },
            ],
        };
        var monitor = new StaticOptionsMonitor<NotificationsOptions>(opts);
        var condition = new OrchestratorStallCondition(clock, monitor);

        // 10 min elapsed < 20 min threshold → not stalled.
        Assert.False(await condition.EvaluateAsync(CancellationToken.None));

        // Hot-reload: change threshold to 5 minutes.
        opts.Rules[0].StallThresholdMinutes = 5;
        monitor.Set(opts);

        // 10 min elapsed >= 5 min threshold → stalled.
        Assert.True(await condition.EvaluateAsync(CancellationToken.None));
    }

    // ── Builder tests ──────────────────────────────────────────────────────

    [Fact]
    public void QueueEmptyNotificationBuilder_Build_ReturnsCorrectNotification()
    {
        var timestamp = new DateTimeOffset(2026, 5, 30, 12, 0, 0, TimeSpan.Zero);
        var builder = new QueueEmptyNotificationBuilder();
        var notification = builder.Build(timestamp);

        Assert.Equal("queue_empty", notification.ConditionId);
        Assert.Equal("Queue is empty", notification.Title);
        Assert.Contains("No active work items", notification.Summary);
        Assert.Contains("orchestrator is idle", notification.Body);
        Assert.Equal(NotificationSeverity.Information, notification.Severity);
        Assert.Equal(timestamp, notification.Timestamp);
    }

    [Fact]
    public void WorkItemPermanentlyFailedNotificationBuilder_Build_ReturnsCorrectNotification()
    {
        var timestamp = new DateTimeOffset(2026, 5, 30, 12, 0, 0, TimeSpan.Zero);
        var builder = new WorkItemPermanentlyFailedNotificationBuilder();
        var notification = builder.Build(timestamp);

        Assert.Equal("work_item_permanently_failed", notification.ConditionId);
        Assert.Contains("permanently failed", notification.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("terminal failure", notification.Summary);
        Assert.Contains("not be retried", notification.Body);
        Assert.Equal(NotificationSeverity.Warning, notification.Severity);
        Assert.Equal(timestamp, notification.Timestamp);
    }

    [Fact]
    public void SandboxLeakReapedNotificationBuilder_Build_ReturnsCorrectNotification()
    {
        var timestamp = new DateTimeOffset(2026, 5, 30, 12, 0, 0, TimeSpan.Zero);
        var builder = new SandboxLeakReapedNotificationBuilder();
        var notification = builder.Build(timestamp);

        Assert.Equal("sandbox_leak_reaped", notification.ConditionId);
        Assert.Contains("leak", notification.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("leaked", notification.Summary);
        Assert.Contains("orphaned", notification.Body);
        Assert.Contains("GET /sandboxes/leaked", notification.Body);
        Assert.Equal(NotificationSeverity.Warning, notification.Severity);
        Assert.Equal(timestamp, notification.Timestamp);
    }

    [Fact]
    public void OrchestratorStallNotificationBuilder_Build_ReturnsCorrectNotification()
    {
        var timestamp = new DateTimeOffset(2026, 5, 30, 12, 0, 0, TimeSpan.Zero);
        var opts = new NotificationsOptions
        {
            Rules =
            [
                new NotificationRuleOptions
                {
                    Condition = "orchestrator_stall",
                    StallThresholdMinutes = 30,
                },
            ],
        };
        var monitor = new StaticOptionsMonitor<NotificationsOptions>(opts);
        var builder = new OrchestratorStallNotificationBuilder(monitor);
        var notification = builder.Build(timestamp);

        Assert.Equal("orchestrator_stall", notification.ConditionId);
        Assert.Contains("stalled", notification.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("30", notification.Title);
        Assert.Contains("not made any state transitions", notification.Summary);
        Assert.Contains("30", notification.Body);
        Assert.Equal(NotificationSeverity.Critical, notification.Severity);
        Assert.Equal(timestamp, notification.Timestamp);
        Assert.True(notification.Fields!.ContainsKey("stallThresholdMinutes"));
        Assert.Equal("30", notification.Fields!["stallThresholdMinutes"]);
    }

    [Fact]
    public void OrchestratorStallNotificationBuilder_HotReloadPicksUpNewThreshold()
    {
        var timestamp = new DateTimeOffset(2026, 5, 30, 12, 0, 0, TimeSpan.Zero);
        var opts = new NotificationsOptions
        {
            Rules =
            [
                new NotificationRuleOptions
                {
                    Condition = "orchestrator_stall",
                    StallThresholdMinutes = 10,
                },
            ],
        };
        var monitor = new StaticOptionsMonitor<NotificationsOptions>(opts);
        var builder = new OrchestratorStallNotificationBuilder(monitor);

        var n1 = builder.Build(timestamp);
        Assert.Contains("10", n1.Title);

        opts.Rules[0].StallThresholdMinutes = 45;
        monitor.Set(opts);

        var n2 = builder.Build(timestamp);
        Assert.Contains("45", n2.Title);
    }

    [Fact]
    public void AllQuotasExhaustedNotificationBuilder_Build_ReturnsCorrectNotification()
    {
        var timestamp = new DateTimeOffset(2026, 5, 30, 12, 0, 0, TimeSpan.Zero);
        var probes = new IAgentQuotaProbe[]
        {
            new StubQuotaProbe(new AgentKind("claude"), 5),
            new StubQuotaProbe(new AgentKind("codex"), 3),
        };
        var builder = new AllQuotasExhaustedNotificationBuilder(probes, 10);
        var notification = builder.Build(timestamp);

        Assert.Equal("all_quotas_exhausted", notification.ConditionId);
        Assert.Contains("quota gate", notification.Title);
        Assert.Contains("claude", notification.Summary);
        Assert.Contains("codex", notification.Summary);
        Assert.Contains("effective gate policy", notification.Body);
        Assert.Contains("per-agent", notification.Body);
        Assert.Equal(NotificationSeverity.Critical, notification.Severity);
        Assert.Equal(timestamp, notification.Timestamp);
        Assert.True(notification.Fields!.ContainsKey("globalMinQuotaPct"));
        Assert.Equal("10", notification.Fields!["globalMinQuotaPct"]);
        Assert.True(notification.Fields!.ContainsKey("gate"));
        Assert.Equal("effective", notification.Fields!["gate"]);
        Assert.True(notification.Fields!.ContainsKey("agents"));
        Assert.Contains("claude", notification.Fields!["agents"]);
        Assert.Contains("codex", notification.Fields!["agents"]);
    }

    [Fact]
    public void AllQuotasExhaustedNotificationBuilder_EmptyProbes_DoesNotThrow()
    {
        var timestamp = new DateTimeOffset(2026, 5, 30, 12, 0, 0, TimeSpan.Zero);
        var builder = new AllQuotasExhaustedNotificationBuilder([], 10);
        var notification = builder.Build(timestamp);

        Assert.Equal("all_quotas_exhausted", notification.ConditionId);
        Assert.Equal(string.Empty, notification.Fields!["agents"]);
    }
}

// ── Test doubles ───────────────────────────────────────────────────────────

public sealed class StubWorkItemStore : IWorkItemStore
{
    public int WorkingCount { get; set; }
    public int FailedCount { get; set; }
    public int AbandonedCount { get; set; }

    public StubWorkItemStore(int workingCount = 0, int failedCount = 0, int abandonedCount = 0)
    {
        WorkingCount = workingCount;
        FailedCount = failedCount;
        AbandonedCount = abandonedCount;
    }

    public Task<int> CountByStateAsync(WorkItemState state, CancellationToken ct)
        => Task.FromResult(state switch
        {
            WorkItemState.Working => WorkingCount,
            WorkItemState.Failed => FailedCount,
            WorkItemState.AbandonedAfterRecoveryAttempts => AbandonedCount,
            _ => 0,
        });

    // Remaining IWorkItemStore members — throw / return default.
    public Task CreateAsync(WorkItem item, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpdateAsync(WorkItem item, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<bool> TryUpdateIfStateAsync(WorkItem item, WorkItemState onlyIfState, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<PriorityUpdateResult> UpdatePriorityAsync(WorkItemId id, int priority, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AuditBudgetUpdateResult> UpdateAuditBudgetAsync(WorkItemId id, int? auditMaxIterations, string? auditComplexity, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default) => throw new NotImplementedException();
    public IAsyncEnumerable<WorkItem> ListAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public IAsyncEnumerable<WorkItem> ListByStateAsync(WorkItemState state, CancellationToken ct = default) => throw new NotImplementedException();
    public Task ReorderAsync(IReadOnlyList<WorkItemId> orderedIds, CancellationToken ct = default) => throw new NotImplementedException();
    public IAsyncEnumerable<WorkItem> ListDispatchEligibleByPriorityAsync(IReadOnlySet<WorkItemId> skipIds, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<int> CountStartedInWindowAsync(ProjectId projectId, DateTimeOffset since, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<int> CountInFlightAsync(ProjectId projectId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<WorkItem?> GetByExternalIdAsync(ProjectId projectId, string externalId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<WorkItem?> GetByNamespacedExternalIdAsync(ProjectId projectId, string @namespace, string externalId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<WorkItem?> ReplaceExternalIdsAsync(WorkItemId id, IReadOnlyDictionary<string, string> externalIds, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<(string ProjectId, int State, int Count, string MaxUpdatedAt)>> GetFleetStateCountsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<(string ProjectId, int State)>> GetFleetRecentOutcomesAsync(int perProject = 5, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyDictionary<string, bool>> GetFleetPauseStatesAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public IAsyncEnumerable<WorkItem> ListByReplaySourceAsync(WorkItemId sourceId, CancellationToken ct = default) => throw new NotImplementedException();
    public IAsyncEnumerable<WorkItem> ListSuspendedAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlySet<string>> GetActiveBaselineImageRefsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<(WorkItemId Id, string Title, WorkItemState State)>> ListWorkItemsForBaselineAsync(string baselineImageRef, CancellationToken ct = default) => throw new NotImplementedException();
    public Task OrphanReplaysAsync(WorkItemId sourceId, CancellationToken ct = default) => throw new NotImplementedException();
    public IAsyncEnumerable<WorkItem> ListByReleaseAsync(ReleaseId releaseId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<PromptReplaceResult> TryReplacePromptAsync(WorkItemId id, string newPrompt, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
    public Task RecordIterationDispatchAsync(WorkItemId workItemId, int iteration, int promptRevisionAtDispatch, DateTimeOffset dispatchedAt, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<WorkItemIteration>> GetIterationsAsync(WorkItemId workItemId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<DependsOnUpdateResult> UpdateDependsOnAsync(WorkItemId id, IReadOnlyList<WorkItemId> dependsOn, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
}

public sealed class StubQuotaProbe : IAgentQuotaProbe
{
    public AgentKind Kind { get; }
    private readonly double _availablePct;

    public StubQuotaProbe(AgentKind kind, double availablePct)
    {
        Kind = kind;
        _availablePct = availablePct;
    }

    public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
        => Task.FromResult(new AgentQuotaSnapshot { AvailablePct = _availablePct });
}

public sealed class ThrowingQuotaProbe : IAgentQuotaProbe
{
    public AgentKind Kind { get; }

    public ThrowingQuotaProbe(AgentKind kind)
    {
        Kind = kind;
    }

    public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
        => throw new InvalidOperationException("Probe failed");
}

public sealed class StubAgentRegistry : IAgentRegistry
{
    private readonly HashSet<AgentKind> _available;

    public StubAgentRegistry(params AgentKind[] available)
    {
        _available = new HashSet<AgentKind>(available);
    }

    public bool TryGet(AgentKind kind, out IAgentRunner runner)
    {
        runner = null!;
        return _available.Contains(kind);
    }

    public IReadOnlyCollection<AgentKind> Available => _available;
}
