using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Threshold state for a project's monthly cost budget. Ok = below warning,
/// Warning = at or above warning threshold but below hard cap, Exceeded = at or
/// above hard cap.
/// </summary>
public enum BudgetThresholdState { Ok, Warning, Exceeded }

/// <summary>
/// Snapshot returned by a single budget sweep tick, exposed for testing.
/// </summary>
public sealed record ProjectBudgetSnapshot(
    ProjectId ProjectId,
    decimal CurrentSpendUsd,
    decimal BudgetUsd,
    double Pct,
    BudgetThresholdState ThresholdState,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd);

/// <summary>
/// Options bound from <c>CodeyBox:BudgetAlerts</c>.
/// </summary>
public sealed class BudgetAlertOptions
{
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Periodic background service that evaluates monthly cost budgets for all
/// projects and fires webhook events + auto-pauses on threshold crossings.
///
/// <para>Edge-trigger semantics: each event fires exactly once when the project's
/// spend crosses a threshold boundary. Successive ticks above the threshold do not
/// re-fire. Events fire again only after the spend drops back below the warning
/// threshold (recovery) and rises again.</para>
///
/// <para>On restart: in-memory state is empty so the first tick re-evaluates every
/// project from scratch and re-fires any currently-applicable events. Webhook
/// receivers must be idempotent (de-dupe by projectId + thresholdState).</para>
///
/// <para>Startup safety: if <c>work_item_costs</c> doesn't exist yet (cost-reporting
/// migration hasn't run), the service logs a warning and skips without crashing.</para>
/// </summary>
public sealed class BudgetAlertService : BackgroundService
{
    private readonly IProjectRepository _projects;
    private readonly IWorkItemCostStore _costStore;
    private readonly IQueueController _queueController;
    private readonly IWebhookDispatcher _webhooks;
    private readonly BudgetAlertOptions _opts;
    private readonly ILogger<BudgetAlertService> _log;

    // In-memory edge-trigger state. Empty at startup (treated as Ok for upward crossings).
    private readonly Dictionary<string, BudgetThresholdState> _previousState = [];

    public BudgetAlertService(
        IProjectRepository projects,
        IWorkItemCostStore costStore,
        IQueueController queueController,
        IWebhookDispatcher webhooks,
        BudgetAlertOptions opts,
        ILogger<BudgetAlertService> log)
    {
        _projects = projects;
        _costStore = costStore;
        _queueController = queueController;
        _webhooks = webhooks;
        _opts = opts;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minInterval = TimeSpan.FromSeconds(30);
        var checkInterval = _opts.CheckInterval < minInterval ? minInterval : _opts.CheckInterval;
        if (_opts.CheckInterval < minInterval)
            _log.LogWarning(
                "BudgetAlerts:CheckInterval {Configured} is below the 30-second minimum; using 30s to prevent I/O saturation",
                _opts.CheckInterval);

        // Stagger the first tick by 30 seconds so startup noise settles.
        try { await Task.Delay(minInterval, stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(checkInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "BudgetAlertService sweep failed; will retry next tick");
            }

            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Runs a single sweep across all projects with a configured cost budget.
    /// Exposed internally for unit testing.
    /// </summary>
    internal async Task RunSweepAsync(CancellationToken ct)
    {
        IReadOnlyList<Project> projects;
        try
        {
            projects = await _projects.ListAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "BudgetAlertService could not load project list; skipping sweep");
            return;
        }

        foreach (var project in projects)
        {
            if (project.Budget.MonthlyCostBudgetUsd <= 0) continue;
            await EvaluateProjectAsync(project, ct);
        }
    }

    private async Task EvaluateProjectAsync(Project project, CancellationToken ct)
    {
        var budget = project.Budget;
        var now = DateTimeOffset.UtcNow;
        var windowStart = now.AddDays(-30);

        decimal spendUsd;
        try
        {
            spendUsd = await _costStore.SumEstimatedUsdAsync(project.Id.Value, windowStart, now, ct);
        }
        catch (Exception ex)
        {
            // Table may not exist yet (cost-reporting not migrated). Log once and skip.
            if (IsTableMissingException(ex))
            {
                AuditLog.BudgetAlertServiceStartupSafe($"work_item_costs table not found for project '{project.Id.Value}'");
                return;
            }
            _log.LogWarning(ex, "BudgetAlertService could not query costs for project {ProjectId}; skipping", project.Id.Value);
            return;
        }

        var pct = (double)(spendUsd / budget.MonthlyCostBudgetUsd * 100m);
        var currentState = ComputeThresholdState(pct, budget);

        _previousState.TryGetValue(project.Id.Value, out var previousState);
        // Default previous = Ok (treated as if just starting clean).

        if (currentState == previousState)
        {
            _previousState[project.Id.Value] = currentState;
            return;
        }

        var snapshot = new ProjectBudgetSnapshot(
            project.Id, spendUsd, budget.MonthlyCostBudgetUsd, pct, currentState, windowStart, now);

        // Fire events for upward crossings.
        if (currentState > previousState)
        {
            if (currentState >= BudgetThresholdState.Warning &&
                previousState < BudgetThresholdState.Warning &&
                budget.CostWarningThresholdPct > 0)
            {
                AuditLog.BudgetAlertWarning(project.Id, spendUsd, budget.MonthlyCostBudgetUsd, pct);
                await FireWebhookAsync("project.budget_warning", project, snapshot, budget.CostWarningThresholdPct, ct);
            }

            if (currentState >= BudgetThresholdState.Exceeded &&
                previousState < BudgetThresholdState.Exceeded)
            {
                AuditLog.BudgetAlertExceeded(project.Id, spendUsd, budget.MonthlyCostBudgetUsd, pct);
                await FireWebhookAsync("project.budget_exceeded", project, snapshot, budget.CostHardCapPct, ct);

                if (budget.CostHardCapPct > 0)
                    await AutoPauseAsync(project, spendUsd, budget.MonthlyCostBudgetUsd, pct, ct);
            }
        }
        // Fire recovery when crossing back below the warning threshold.
        else if (currentState == BudgetThresholdState.Ok &&
                 previousState > BudgetThresholdState.Ok)
        {
            AuditLog.BudgetAlertRecovered(project.Id, spendUsd, budget.MonthlyCostBudgetUsd, pct);
            await FireWebhookAsync("project.budget_recovered", project, snapshot, budget.CostWarningThresholdPct, ct);

            if (budget.AutoResumeOnRecovery)
                await AutoResumeAsync(project, ct);
        }

        _previousState[project.Id.Value] = currentState;
    }

    private static BudgetThresholdState ComputeThresholdState(double pct, ProjectBudget budget)
    {
        if (budget.CostHardCapPct > 0 && pct >= budget.CostHardCapPct)
            return BudgetThresholdState.Exceeded;
        if (budget.CostWarningThresholdPct > 0 && pct >= budget.CostWarningThresholdPct)
            return BudgetThresholdState.Warning;
        return BudgetThresholdState.Ok;
    }

    private async Task FireWebhookAsync(
        string eventName,
        Project project,
        ProjectBudgetSnapshot snapshot,
        int thresholdPct,
        CancellationToken ct)
    {
        try
        {
            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = eventName,
                Project = project,
                Details = new ProjectBudgetEventDetails
                {
                    ProjectId = project.Id.Value,
                    CurrentSpendUsd = snapshot.CurrentSpendUsd,
                    BudgetUsd = snapshot.BudgetUsd,
                    Pct = snapshot.Pct,
                    ThresholdPct = thresholdPct,
                },
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "BudgetAlertService failed to publish {Event} for project {ProjectId}",
                eventName, project.Id.Value);
        }
    }

    private async Task AutoPauseAsync(
        Project project, decimal spendUsd, decimal budgetUsd, double pct, CancellationToken ct)
    {
        try
        {
            var reason = $"budget-exceeded: ${spendUsd:F4} of ${budgetUsd:F2} ({pct:F1}%)";
            await _queueController.PauseProjectAsync(project.Id, reason, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "BudgetAlertService could not auto-pause project {ProjectId}", project.Id.Value);
        }
    }

    private async Task AutoResumeAsync(Project project, CancellationToken ct)
    {
        try
        {
            await _queueController.ResumeProjectAsync(project.Id, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "BudgetAlertService could not auto-resume project {ProjectId}", project.Id.Value);
        }
    }

    private static bool IsTableMissingException(Exception ex) =>
        ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the current previous-state snapshot for a project (for testing).
    /// </summary>
    internal BudgetThresholdState? GetPreviousState(string projectId) =>
        _previousState.TryGetValue(projectId, out var s) ? s : null;
}
