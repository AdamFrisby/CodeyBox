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
        var pauseCallCount = queue.PauseProjectCallCount; // 1 after the first tick
        await svc.RunSweepAsync(CancellationToken.None); // second tick — already Exceeded, no re-fire

        // Edge-trigger: PauseProjectAsync must be called exactly once regardless of
        // how many consecutive ticks remain above the hard cap.
        Assert.Equal(pauseCallCount, queue.PauseProjectCallCount);
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
            _dbPath, NullLogger<SqliteQueueController>.Instance);

        var pid = new ProjectId("proj-pickup-paused");
        var project = new Project
        {
            Id = pid,
            DisplayName = "Pickup Paused",
            RepositoryUrl = "https://example.com/paused",
        };
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = pid,
            Title = "test",
            Prompt = "test",
            State = WorkItemState.Queued,
        };
        await itemStore.CreateAsync(item);

        // Pause the project before the orchestrator picks up the item.
        await queueController.PauseProjectAsync(pid, "budget exceeded");

        var taskQueue = new InMemoryTaskQueue();
        await taskQueue.EnqueueAsync(item.Id, CancellationToken.None);

        // FakePipelineRunner records every item it executes — it must stay empty.
        var pipeline = new FakePipelineRunner(itemStore);
        var registry = new CancellationRegistry(CancellationToken.None);

        // OnWorkerSpawned fires right before Task.Run inside the dispatch loop,
        // i.e. after the item is dequeued and before RunItemAsync executes.
        var spawnedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var svc = new OrchestratorService(
            taskQueue, itemStore, pipeline, registry,
            new OrchestratorOptions
            {
                MaxConcurrentWorkers = 1,
                OnWorkerSpawned = () => spawnedTcs.TrySetResult(),
            },
            NullLogger<OrchestratorService>.Instance,
            projects: new InMemoryProjectRepository(project),
            queueController: queueController);

        using var cts = new CancellationTokenSource();
        _ = svc.StartAsync(cts.Token);

        // Wait until the dispatch loop picks up the item, then give RunItemAsync
        // time to complete the per-project pause check and return.
        await spawnedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);

        await cts.CancelAsync();
        await svc.StopAsync(CancellationToken.None);

        // The pipeline must never have been invoked for the paused project.
        Assert.Empty(pipeline.Executed);
    }
}
