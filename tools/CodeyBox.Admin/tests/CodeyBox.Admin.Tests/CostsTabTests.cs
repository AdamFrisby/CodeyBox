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
        long elapsedMs = 12_000,
        int invocationCount = 1,
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
                elapsedMs,
                invocationCount,
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
            ElapsedMs = elapsedMs,
            InvocationCount = invocationCount,
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
                ElapsedMs = elapsedMs,
                InvocationCount = invocationCount,
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
    public void WorkItemCosts_ZeroTokensWithElapsedTime_ShowsActivity()
    {
        var fake = new FakeApiClient([]);
        fake.CostsOverride[ItemId] = MakeCosts(
            inputTokens: 0,
            outputTokens: 0,
            cachedTokens: 0,
            estimatedUsd: 0,
            elapsedMs: 15_000,
            agentKinds: ["cursor"]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemCostsPage>(p => p.Add(x => x.Id, ItemId));

        Assert.Contains("Token counts unavailable", cut.Markup);
        Assert.Contains("15s", cut.Markup);
        Assert.Contains("cursor", cut.Markup);
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

    [Fact]
    public void WorkItemCosts_ShowsAgentInstanceBreakdown()
    {
        var fake = new FakeApiClient([]);
        var costs = MakeCosts(agentKinds: ["claude"]);
        costs.ByAgent[0].AgentInstanceId = "claude/acct-a";
        fake.CostsOverride[ItemId] = costs;
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemCostsPage>(p => p.Add(x => x.Id, ItemId));

        Assert.Contains("claude/acct-a", cut.Markup);
        Assert.Contains("costs-agent-table", cut.Markup);
    }
}
