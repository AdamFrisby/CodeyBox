using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that on restart (fresh in-memory state), BudgetAlertService
/// re-evaluates all projects and re-fires events for any that are currently
/// above threshold. Webhook receivers must be idempotent and handle replay.
/// </summary>
public sealed class RestartReevaluatesTests
{
    private static readonly ProjectId ProjectC = new("proj-c");

    private static Project MakeProject() => new()
    {
        Id = ProjectC,
        DisplayName = "Proj C",
        RepositoryUrl = "https://example.com/c",
        Budget = new ProjectBudget
        {
            MonthlyCostBudgetUsd = 100m,
            CostWarningThresholdPct = 80,
            CostHardCapPct = 100,
        },
    };

    [Fact]
    public async Task Restart_RefiresWarning_WhenProjectAlreadyInWarning()
    {
        var costs = new CapturingCostStore();
        costs.SetSpend(ProjectC.Value, 85m); // 85% — in Warning

        // First "orchestrator instance" runs one tick.
        var webhooks1 = new BudgetWebhookCollector();
        var svc1 = new BudgetAlertService(
            new InMemoryProjectRepository(MakeProject()),
            costs, new CapturingQueueController(), webhooks1,
            new BudgetAlertOptions(), NullLogger<BudgetAlertService>.Instance);
        await svc1.RunSweepAsync(CancellationToken.None);
        Assert.Single(webhooks1.Published, e => e.Event == "project.budget_warning");

        // Second "orchestrator instance" (restart) — fresh in-memory state.
        var webhooks2 = new BudgetWebhookCollector();
        var svc2 = new BudgetAlertService(
            new InMemoryProjectRepository(MakeProject()),
            costs, new CapturingQueueController(), webhooks2,
            new BudgetAlertOptions(), NullLogger<BudgetAlertService>.Instance);
        await svc2.RunSweepAsync(CancellationToken.None);

        // Re-fires the warning on the first tick after restart.
        Assert.Single(webhooks2.Published, e => e.Event == "project.budget_warning");
    }

    [Fact]
    public async Task Restart_RefiresExceededAndAutoPauses_WhenProjectAlreadyExceeded()
    {
        var costs = new CapturingCostStore();
        costs.SetSpend(ProjectC.Value, 110m); // 110% — Exceeded

        var queue1 = new CapturingQueueController();
        var svc1 = new BudgetAlertService(
            new InMemoryProjectRepository(MakeProject()),
            costs, queue1, new BudgetWebhookCollector(),
            new BudgetAlertOptions(), NullLogger<BudgetAlertService>.Instance);
        await svc1.RunSweepAsync(CancellationToken.None);
        Assert.True(queue1.ProjectPaused.ContainsKey(ProjectC.Value));

        // Restart: fresh service, fresh queue state tracker.
        var queue2 = new CapturingQueueController();
        var webhooks2 = new BudgetWebhookCollector();
        var svc2 = new BudgetAlertService(
            new InMemoryProjectRepository(MakeProject()),
            costs, queue2, webhooks2,
            new BudgetAlertOptions(), NullLogger<BudgetAlertService>.Instance);
        await svc2.RunSweepAsync(CancellationToken.None);

        Assert.Contains(webhooks2.Published, e => e.Event == "project.budget_exceeded");
        // Auto-pause is idempotent in SQLite; the CapturingQueueController here
        // records it again, which is expected behavior (PauseProjectAsync is idempotent).
        Assert.True(queue2.ProjectPaused.ContainsKey(ProjectC.Value));
    }

    [Fact]
    public async Task Restart_NoEvents_WhenProjectBelowThreshold()
    {
        var costs = new CapturingCostStore();
        costs.SetSpend(ProjectC.Value, 10m); // 10% — Ok

        var webhooks = new BudgetWebhookCollector();
        var svc = new BudgetAlertService(
            new InMemoryProjectRepository(MakeProject()),
            costs, new CapturingQueueController(), webhooks,
            new BudgetAlertOptions(), NullLogger<BudgetAlertService>.Instance);
        await svc.RunSweepAsync(CancellationToken.None);

        Assert.Empty(webhooks.Published);
    }
}
