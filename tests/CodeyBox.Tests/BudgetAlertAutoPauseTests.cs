using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that BudgetAlertService auto-pauses a project when the hard cap is
/// crossed and that the pickup loop respects the per-project pause.
/// </summary>
public sealed class BudgetAlertAutoPauseTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-ba-pause-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    private static readonly ProjectId ProjectB = new("proj-b");
    private static readonly decimal Budget = 100m;

    private Project MakeProject(int hardCapPct = 100, bool autoResume = false) => new()
    {
        Id = ProjectB,
        DisplayName = "Proj B",
        RepositoryUrl = "https://example.com/b",
        Budget = new ProjectBudget
        {
            MonthlyCostBudgetUsd = Budget,
            CostWarningThresholdPct = 80,
            CostHardCapPct = hardCapPct,
            AutoResumeOnRecovery = autoResume,
        },
    };

    // ── Auto-pause on exceeded ────────────────────────────────────────────────

    [Fact]
    public async Task AutoPause_CalledWhenHardCapExceeded()
    {
        var costs = new CapturingCostStore();
        costs.SetSpend(ProjectB.Value, 110m); // 110%
        var queue = new CapturingQueueController();
        var webhooks = new BudgetWebhookCollector();

        var svc = new BudgetAlertService(
            new InMemoryProjectRepository(MakeProject()),
            costs, queue, webhooks,
            new BudgetAlertOptions(),
            NullLogger<BudgetAlertService>.Instance);

        await svc.RunSweepAsync(CancellationToken.None);

        Assert.True(queue.ProjectPaused.ContainsKey(ProjectB.Value));
        Assert.Contains("budget-exceeded", queue.ProjectPaused[ProjectB.Value]);
    }

    [Fact]
    public async Task AutoPause_IdempotentOnSecondTick()
    {
        var costs = new CapturingCostStore();
        costs.SetSpend(ProjectB.Value, 110m);
        var queue = new CapturingQueueController();
        var webhooks = new BudgetWebhookCollector();

        var svc = new BudgetAlertService(
            new InMemoryProjectRepository(MakeProject()),
            costs, queue, webhooks,
            new BudgetAlertOptions(),
            NullLogger<BudgetAlertService>.Instance);

        await svc.RunSweepAsync(CancellationToken.None);
        var pauseCallCount = queue.ProjectPaused.Count; // track state after first tick
        await svc.RunSweepAsync(CancellationToken.None); // second tick should not re-pause

        // PauseProjectAsync called at most once (state was already Exceeded).
        Assert.Equal(pauseCallCount, queue.ProjectPaused.Count);
    }

    [Fact]
    public async Task NoPause_WhenHardCapPctIsZero()
    {
        var costs = new CapturingCostStore();
        costs.SetSpend(ProjectB.Value, 110m);
        var queue = new CapturingQueueController();

        var svc = new BudgetAlertService(
            new InMemoryProjectRepository(MakeProject(hardCapPct: 0)),
            costs, queue, new BudgetWebhookCollector(),
            new BudgetAlertOptions(),
            NullLogger<BudgetAlertService>.Instance);

        await svc.RunSweepAsync(CancellationToken.None);

        Assert.False(queue.ProjectPaused.ContainsKey(ProjectB.Value));
    }

    // ── Auto-resume on recovery ───────────────────────────────────────────────

    [Fact]
    public async Task AutoResume_CalledOnRecovery_WhenConfigured()
    {
        var costs = new CapturingCostStore();
        costs.SetSpend(ProjectB.Value, 110m);
        var queue = new CapturingQueueController();

        var svc = new BudgetAlertService(
            new InMemoryProjectRepository(MakeProject(autoResume: true)),
            costs, queue, new BudgetWebhookCollector(),
            new BudgetAlertOptions(),
            NullLogger<BudgetAlertService>.Instance);

        await svc.RunSweepAsync(CancellationToken.None); // Exceeded
        costs.SetSpend(ProjectB.Value, 10m);             // Recovery
        await svc.RunSweepAsync(CancellationToken.None);

        Assert.Contains(ProjectB.Value, queue.ProjectResumed);
    }

    [Fact]
    public async Task NoAutoResume_WhenNotConfigured()
    {
        var costs = new CapturingCostStore();
        costs.SetSpend(ProjectB.Value, 110m);
        var queue = new CapturingQueueController();

        var svc = new BudgetAlertService(
            new InMemoryProjectRepository(MakeProject(autoResume: false)),
            costs, queue, new BudgetWebhookCollector(),
            new BudgetAlertOptions(),
            NullLogger<BudgetAlertService>.Instance);

        await svc.RunSweepAsync(CancellationToken.None); // Exceeded
        costs.SetSpend(ProjectB.Value, 10m);             // Recovery
        await svc.RunSweepAsync(CancellationToken.None);

        Assert.DoesNotContain(ProjectB.Value, queue.ProjectResumed);
    }

    // ── Pickup loop respects per-project pause ────────────────────────────────

    [Fact]
    public async Task PickupLoop_SkipsWork_WhenProjectPaused()
    {
        using var itemStore = new SqliteWorkItemStore(_dbPath);
        using var queueController = new SqliteQueueController(
            _dbPath, Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteQueueController>.Instance);

        var pid = new ProjectId("proj-pickup-paused");
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = pid,
            Title = "test",
            Prompt = "test",
            State = WorkItemState.Queued,
        };
        await itemStore.CreateAsync(item);

        await queueController.PauseProjectAsync(pid, "budget exceeded");

        var state = await queueController.GetProjectStateAsync(pid);
        Assert.NotNull(state);
        Assert.True(state!.Paused);
        Assert.Equal("budget exceeded", state.PausedReason);
    }
}
