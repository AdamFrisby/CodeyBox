using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using WorkItemCostsPage = CodeyBox.Admin.Web.Components.Pages.WorkItemCosts;

namespace CodeyBox.Admin.Tests;

public sealed class CostsTabTests : TestContext
{
    private const string ItemId = "aabbccdd-0000-0000-0000-000000000001";

    private static WorkItemCostsDto MakeCosts(
        int inputTokens = 12345,
        int outputTokens = 678,
        int cachedTokens = 500,
        double estimatedUsd = 0.168525,
        string[]? phases = null,
        string[]? agentKinds = null)
    {
        var byPhase = new Dictionary<string, JsonElement>();
        foreach (var phase in phases ?? ["work"])
        {
            byPhase[phase] = JsonSerializer.SerializeToElement(new
            {
                inputTokens,
                cachedInputTokens = cachedTokens,
                outputTokens,
                estimatedUsd,
            });
        }

        var byAgent = (agentKinds ?? []).Select(a => new AgentCostBreakdownDto
        {
            Agent = a,
            ModelId = $"{a}-model",
            InputTokens = inputTokens,
            CachedInputTokens = cachedTokens,
            OutputTokens = outputTokens,
            EstimatedUsd = estimatedUsd,
        }).ToList();

        return new WorkItemCostsDto
        {
            WorkItemId = ItemId,
            Totals = new CostTotalsDto
            {
                InputTokens = inputTokens,
                CachedInputTokens = cachedTokens,
                OutputTokens = outputTokens,
                EstimatedUsd = estimatedUsd,
            },
            ByPhase = byPhase,
            ByAgent = byAgent,
        };
    }

    [Fact]
    public void WorkItemCosts_ShowsSummaryWithUsd()
    {
        var fake = new FakeApiClient([]);
        fake.CostsOverride[ItemId] = MakeCosts(estimatedUsd: 0.168525);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemCostsPage>(p => p.Add(x => x.Id, ItemId));

        Assert.Contains("$", cut.Markup);
        Assert.Contains("12", cut.Markup);
    }

    [Fact]
    public void WorkItemCosts_ShowsPhaseTable()
    {
        var fake = new FakeApiClient([]);
        fake.CostsOverride[ItemId] = MakeCosts(phases: ["work", "audit"]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemCostsPage>(p => p.Add(x => x.Id, ItemId));

        Assert.Contains("costs-phase-table", cut.Markup);
        Assert.Contains("work", cut.Markup);
        Assert.Contains("audit", cut.Markup);
    }

    [Fact]
    public void WorkItemCosts_NotFound_ShowsErrorBanner()
    {
        var fake = new FakeApiClient([]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemCostsPage>(p => p.Add(x => x.Id, ItemId));

        Assert.Contains("not found", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkItemCosts_NoCostData_ShowsEmptyMessage()
    {
        var fake = new FakeApiClient([]);
        fake.CostsOverride[ItemId] = new WorkItemCostsDto
        {
            WorkItemId = ItemId,
            Totals = new CostTotalsDto(),
        };
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemCostsPage>(p => p.Add(x => x.Id, ItemId));

        Assert.Contains("No cost data", cut.Markup);
    }

    [Fact]
    public void WorkItemCosts_ShowsAgentBreakdown()
    {
        var fake = new FakeApiClient([]);
        fake.CostsOverride[ItemId] = MakeCosts(agentKinds: ["claude"]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemCostsPage>(p => p.Add(x => x.Id, ItemId));

        Assert.Contains("claude", cut.Markup);
        Assert.Contains("costs-agent-table", cut.Markup);
    }
}
