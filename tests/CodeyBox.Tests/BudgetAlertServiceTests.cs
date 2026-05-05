using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for BudgetAlertService edge-trigger semantics.
/// Uses a programmable cost store and webhook collector to verify that
/// events fire exactly once on threshold crossings and not on repeat ticks.
/// </summary>
public sealed class BudgetAlertServiceTests
{
    private static readonly ProjectId ProjectA = new("proj-a");
    private static readonly decimal Budget = 500m;

    private readonly CapturingCostStore _costs = new();
    private readonly BudgetWebhookCollector _webhooks = new();
    private readonly CapturingQueueController _queue = new();

    private BudgetAlertService MakeService(Project project) =>
        new(
            new InMemoryProjectRepository(project),
            _costs,
            _queue,
            _webhooks,
            new BudgetAlertOptions { CheckInterval = TimeSpan.FromMinutes(5) },
            NullLogger<BudgetAlertService>.Instance);

    private Project MakeProject(int warningPct = 80, int hardCapPct = 100) =>
        new()
        {
            Id = ProjectA,
            DisplayName = "Proj A",
            RepositoryUrl = "https://example.com/a",
            Budget = new ProjectBudget
            {
                MonthlyCostBudgetUsd = Budget,
                CostWarningThresholdPct = warningPct,
                CostHardCapPct = hardCapPct,
            },
        };

    // ── No event on Ok state ─────────────────────────────────────────────────

    [Fact]
    public async Task NoEvent_WhenBelowWarning()
    {
        _costs.SetSpend(ProjectA.Value, 100m); // 20%
        var svc = MakeService(MakeProject());
        await svc.RunSweepAsync(CancellationToken.None);

        Assert.Empty(_webhooks.Published);
        Assert.False(_queue.ProjectPaused.ContainsKey(ProjectA.Value));
    }

    // ── Warning fires once on crossing ────────────────────────────────────────

    [Fact]
    public async Task WarningFires_OnCrossWarningThreshold()
    {
        _costs.SetSpend(ProjectA.Value, 410m); // 82% > 80% warning
        var svc = MakeService(MakeProject());
        await svc.RunSweepAsync(CancellationToken.None);

        Assert.Single(_webhooks.Published, e => e.Event == "project.budget_warning");
    }

    [Fact]
    public async Task WarningDoesNotRepeat_OnSecondTick()
    {
        _costs.SetSpend(ProjectA.Value, 410m);
        var svc = MakeService(MakeProject());
        await svc.RunSweepAsync(CancellationToken.None);
        await svc.RunSweepAsync(CancellationToken.None); // second tick, still in Warning

        Assert.Single(_webhooks.Published, e => e.Event == "project.budget_warning");
    }

    // ── Exceeded fires on crossing hard cap ───────────────────────────────────

    [Fact]
    public async Task ExceededFires_WhenAtHardCap()
    {
        _costs.SetSpend(ProjectA.Value, 510m); // 102%
        var svc = MakeService(MakeProject());
        await svc.RunSweepAsync(CancellationToken.None);

        // Both warning and exceeded fire when jumping Ok→Exceeded.
        Assert.Contains(_webhooks.Published, e => e.Event == "project.budget_warning");
        Assert.Contains(_webhooks.Published, e => e.Event == "project.budget_exceeded");
    }

    [Fact]
    public async Task ExceededDoesNotRepeat_OnSecondTick()
    {
        _costs.SetSpend(ProjectA.Value, 510m);
        var svc = MakeService(MakeProject());
        await svc.RunSweepAsync(CancellationToken.None);
        _webhooks.Published.Clear();

        await svc.RunSweepAsync(CancellationToken.None); // second tick, still Exceeded

        Assert.Empty(_webhooks.Published);
    }

    // ── Warning→Exceeded: only exceeded fires, not a second warning ──────────

    [Fact]
    public async Task OnlyExceededFires_WhenCrossingFromWarningToExceeded()
    {
        _costs.SetSpend(ProjectA.Value, 410m); // Warning
        var svc = MakeService(MakeProject());
        await svc.RunSweepAsync(CancellationToken.None);
        _webhooks.Published.Clear();

        _costs.SetSpend(ProjectA.Value, 510m); // Exceeded
        await svc.RunSweepAsync(CancellationToken.None);

        Assert.DoesNotContain(_webhooks.Published, e => e.Event == "project.budget_warning");
        Assert.Single(_webhooks.Published, e => e.Event == "project.budget_exceeded");
    }

    // ── Recovery fires when dropping below warning threshold ─────────────────

    [Fact]
    public async Task RecoveryFires_WhenDropsBelowWarning()
    {
        _costs.SetSpend(ProjectA.Value, 410m); // Warning
        var svc = MakeService(MakeProject());
        await svc.RunSweepAsync(CancellationToken.None);
        _webhooks.Published.Clear();

        _costs.SetSpend(ProjectA.Value, 100m); // back to Ok
        await svc.RunSweepAsync(CancellationToken.None);

        Assert.Single(_webhooks.Published, e => e.Event == "project.budget_recovered");
    }

    [Fact]
    public async Task NoRecovery_WhenExceededDropsToWarning()
    {
        _costs.SetSpend(ProjectA.Value, 510m); // Exceeded
        var svc = MakeService(MakeProject());
        await svc.RunSweepAsync(CancellationToken.None);
        _webhooks.Published.Clear();

        _costs.SetSpend(ProjectA.Value, 410m); // back to Warning (not yet Ok)
        await svc.RunSweepAsync(CancellationToken.None);

        Assert.Empty(_webhooks.Published);
    }

    // ── Budget=0 is a no-op ───────────────────────────────────────────────────

    [Fact]
    public async Task NoBudget_NoEvents()
    {
        _costs.SetSpend(ProjectA.Value, 9999m);
        var project = new Project
        {
            Id = ProjectA,
            DisplayName = "Proj A",
            RepositoryUrl = "https://example.com/a",
            Budget = new ProjectBudget { MonthlyCostBudgetUsd = 0 },
        };
        var svc = MakeService(project);
        await svc.RunSweepAsync(CancellationToken.None);

        Assert.Empty(_webhooks.Published);
    }

    // ── CostWarningThresholdPct=0 disables warning event ─────────────────────

    [Fact]
    public async Task WarningDisabled_WhenThresholdPctIsZero()
    {
        _costs.SetSpend(ProjectA.Value, 410m);
        var svc = MakeService(MakeProject(warningPct: 0, hardCapPct: 100));
        await svc.RunSweepAsync(CancellationToken.None);

        Assert.DoesNotContain(_webhooks.Published, e => e.Event == "project.budget_warning");
    }

    // ── Previous-state tracking ────────────────────────────────────────────────

    [Fact]
    public async Task PreviousState_StartsNull_ThenTracked()
    {
        _costs.SetSpend(ProjectA.Value, 0m);
        var svc = MakeService(MakeProject());
        Assert.Null(svc.GetPreviousState(ProjectA.Value));

        await svc.RunSweepAsync(CancellationToken.None);
        Assert.Equal(BudgetThresholdState.Ok, svc.GetPreviousState(ProjectA.Value));
    }
}

// ── Test doubles ──────────────────────────────────────────────────────────────

internal sealed class CapturingCostStore : IWorkItemCostStore
{
    private readonly Dictionary<string, decimal> _spend = new();

    public void SetSpend(string projectId, decimal spend) => _spend[projectId] = spend;

    public Task<decimal> SumEstimatedUsdAsync(
        string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        Task.FromResult(_spend.TryGetValue(projectId, out var s) ? s : 0m);

    public Task RecordAsync(WorkItemCost cost, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<WorkItemCost>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WorkItemCost>>([]);
    public Task<IReadOnlyList<WorkItemCost>> GetByProjectAsync(string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WorkItemCost>>([]);
    public Task DeleteByWorkItemAsync(string workItemId, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class BudgetWebhookCollector : IWebhookDispatcher
{
    public List<WebhookEvent> Published { get; } = [];

    public Task PublishAsync(WebhookEvent evt, CancellationToken ct)
    {
        Published.Add(evt);
        return Task.CompletedTask;
    }
}

internal sealed class CapturingQueueController : IQueueController
{
    public QueueState State => QueueState.Running;
    public DateTimeOffset? PausedAt => null;
    public string? PausedReason => null;

    public Dictionary<string, string> ProjectPaused { get; } = new();
    public HashSet<string> ProjectResumed { get; } = [];
    public int PauseProjectCallCount { get; private set; }

    public Task PauseAsync(string reason, CancellationToken ct = default) => Task.CompletedTask;
    public Task ResumeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task PauseProjectAsync(ProjectId projectId, string reason, CancellationToken ct = default)
    {
        PauseProjectCallCount++;
        ProjectPaused[projectId.Value] = reason;
        return Task.CompletedTask;
    }

    public Task ResumeProjectAsync(ProjectId projectId, CancellationToken ct = default)
    {
        ProjectResumed.Add(projectId.Value);
        ProjectPaused.Remove(projectId.Value);
        return Task.CompletedTask;
    }

    public Task<ProjectQueueState?> GetProjectStateAsync(ProjectId projectId, CancellationToken ct = default)
    {
        if (!ProjectPaused.ContainsKey(projectId.Value))
            return Task.FromResult<ProjectQueueState?>(null);
        return Task.FromResult<ProjectQueueState?>(
            new ProjectQueueState(projectId, true, DateTimeOffset.UtcNow, ProjectPaused[projectId.Value]));
    }
}
