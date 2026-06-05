using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests.Uat.CostTelemetryAndStreams;

/// <summary>
/// UAT coverage for cost capture, cost endpoints, rolling-window cost math, and
/// budget alert transitions from the Cost, Telemetry, And Streams section.
/// Plan anchor:
/// docs/uat/00-plan.md#cost-capture-and-budget-enforcement---estimates-per-item-spend-and-enforces-project-caps
/// </summary>
public sealed class CostCaptureAndBudgetUatTests : IDisposable
{
    private readonly CostTelemetryWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task ProjectCosts_DefaultThirtyDayWindow_ExcludesOlderRowsAndGroupsFreshSpend()
    {
        var project = CostTelemetryFixtures.Project();
        using var factory = new CostTelemetryApiFactory(_workspace.NewDatabasePath(), _workspace.NewStreamRoot(), project);
        var freshItem = CostTelemetryFixtures.WorkItem();
        var oldItem = CostTelemetryFixtures.WorkItem();
        await factory.SeedWorkItemAsync(freshItem);
        await factory.SeedWorkItemAsync(oldItem);
        var now = DateTimeOffset.UtcNow;
        await factory.Costs.RecordAsync(CostTelemetryFixtures.Cost(freshItem.Id, "work", now.AddDays(-1), 0.75));
        await factory.Costs.RecordAsync(CostTelemetryFixtures.Cost(oldItem.Id, "merge", now.AddDays(-31), 99.0));

        var client = factory.CreateClient();
        var response = await client.GetAsync($"/projects/{project.Id.Value}/costs");

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0.75, json.GetProperty("totals").GetProperty("estimatedUsd").GetDouble(), precision: 5);
        Assert.Single(json.GetProperty("byWorkItem").EnumerateArray());
        Assert.Equal(freshItem.Id.ToString(), json.GetProperty("byWorkItem")[0].GetProperty("workItemId").GetString());
        Assert.Single(json.GetProperty("byAgent").EnumerateArray());
    }

    [Fact]
    public async Task WorkItemCosts_ReturnsPhaseBreakdownAndTokenTotals()
    {
        using var factory = new CostTelemetryApiFactory(
            _workspace.NewDatabasePath(),
            _workspace.NewStreamRoot(),
            CostTelemetryFixtures.Project());
        var item = CostTelemetryFixtures.WorkItem();
        await factory.SeedWorkItemAsync(item);
        var startedAt = DateTimeOffset.Parse("2026-05-14T01:00:00Z");
        await factory.Costs.RecordAsync(CostTelemetryFixtures.Cost(item.Id, "work", startedAt, 0.25));
        await factory.Costs.RecordAsync(CostTelemetryFixtures.Cost(item.Id, "merge", startedAt.AddMinutes(2), 0.50));

        var client = factory.CreateClient();
        var response = await client.GetAsync($"/workitems/{item.Id}/costs");

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(240, json.GetProperty("totals").GetProperty("inputTokens").GetInt64());
        Assert.Equal(40, json.GetProperty("totals").GetProperty("cachedInputTokens").GetInt64());
        Assert.Equal(60, json.GetProperty("totals").GetProperty("outputTokens").GetInt64());
        Assert.Equal(0.75, json.GetProperty("totals").GetProperty("estimatedUsd").GetDouble(), precision: 5);
        Assert.True(json.GetProperty("byPhase").TryGetProperty("work", out _));
        Assert.True(json.GetProperty("byPhase").TryGetProperty("merge", out _));
    }

    [Fact]
    public async Task BudgetAlerts_FireThresholdEventsAutoPauseAndAutoResumeOnRecovery()
    {
        var project = CostTelemetryFixtures.Project(new ProjectBudget
        {
            MonthlyCostBudgetUsd = 100m,
            CostWarningThresholdPct = 80,
            CostHardCapPct = 100,
            AutoResumeOnRecovery = true,
        });
        var costs = new FixedSpendCostStore();
        var queue = new RecordingQueueController();
        var webhooks = new CapturingWebhookDispatcher();
        var service = new BudgetAlertService(
            new InMemoryProjectRepository(project),
            costs,
            queue,
            webhooks,
            new BudgetAlertOptions(),
            NullLogger<BudgetAlertService>.Instance);

        costs.SetSpend(project.Id, 85m);
        await service.RunSweepAsync(CancellationToken.None);
        costs.SetSpend(project.Id, 125m);
        await service.RunSweepAsync(CancellationToken.None);
        costs.SetSpend(project.Id, 10m);
        await service.RunSweepAsync(CancellationToken.None);

        Assert.Contains(webhooks.Events, e => e.Event == "project.budget_warning");
        Assert.Contains(webhooks.Events, e => e.Event == "project.budget_exceeded");
        Assert.Contains(webhooks.Events, e => e.Event == "project.budget_recovered");
        Assert.False(queue.ProjectStates.ContainsKey(project.Id.Value));
    }

    [Fact]
    public async Task BudgetCapQueries_CountStartedAndInFlightRowsByProjectOnly()
    {
        using var store = new SqliteWorkItemStore(_workspace.NewDatabasePath());
        var now = DateTimeOffset.Parse("2026-05-14T02:00:00Z");
        var inScopeStarted = CostTelemetryFixtures.WorkItem(WorkItemState.Working) with { StartedAt = now.AddMinutes(-20) };
        var staleStarted = CostTelemetryFixtures.WorkItem(WorkItemState.Working) with { StartedAt = now.AddHours(-2) };
        var otherProject = CostTelemetryFixtures.WorkItem(WorkItemState.Working) with
        {
            ProjectId = new ProjectId("other-project"),
            StartedAt = now.AddMinutes(-10),
        };
        var queued = CostTelemetryFixtures.WorkItem(WorkItemState.Queued);
        await store.CreateAsync(inScopeStarted);
        await store.CreateAsync(staleStarted);
        await store.CreateAsync(otherProject);
        await store.CreateAsync(queued);

        var startedInHour = await store.CountStartedInWindowAsync(CostTelemetryFixtures.ProjectId, now.AddHours(-1));
        var inFlight = await store.CountInFlightAsync(CostTelemetryFixtures.ProjectId);

        Assert.Equal(1, startedInHour);
        Assert.Equal(2, inFlight);
    }
}
