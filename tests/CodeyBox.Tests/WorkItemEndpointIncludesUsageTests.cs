using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that GET /workitems/{id} returns the <c>usage</c> + <c>usageTotal</c>
/// blocks once cost rows have been recorded, and that items with no cost rows
/// (e.g. an agent without a registered IAgentCostExtractor) report null usage.
/// </summary>
public sealed class WorkItemEndpointIncludesUsageTests : IClassFixture<CostsApiFactory>
{
    private readonly CostsApiFactory _factory;

    public WorkItemEndpointIncludesUsageTests(CostsApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Get_ItemWithCostRows_IncludesUsageAndUsageTotal()
    {
        var item = NewItem();
        await _factory.Store.CreateAsync(item);

        await _factory.CostStore.RecordAsync(NewCost(item.Id, "work", null, 5000, 500, 0.10, 4));
        await _factory.CostStore.RecordAsync(NewCost(item.Id, "audit", 1, 2000, 100, 0.04, 2));
        await _factory.CostStore.RecordAsync(NewCost(item.Id, "rework", 2, 8000, 900, 0.20, 6));
        await _factory.CostStore.RecordAsync(NewCost(item.Id, "audit", 2, 1500, 80, 0.03, 1.5));

        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/workitems/{item.Id}");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var usage = body.GetProperty("usage");
        Assert.Equal(2, usage.GetProperty("iteration").GetInt32());
        Assert.Equal(8000 + 1500, usage.GetProperty("tokensInput").GetInt32());
        Assert.Equal(900 + 80, usage.GetProperty("tokensOutput").GetInt32());
        Assert.Equal(0, usage.GetProperty("tokensReasoning").GetInt32());
        Assert.Equal(0.23, usage.GetProperty("costUsd").GetDouble(), precision: 4);

        var total = body.GetProperty("usageTotal");
        Assert.Equal(5000 + 2000 + 8000 + 1500, total.GetProperty("tokensInput").GetInt32());
        Assert.Equal(500 + 100 + 900 + 80, total.GetProperty("tokensOutput").GetInt32());
        Assert.Equal(0.37, total.GetProperty("costUsd").GetDouble(), precision: 4);
    }

    [Fact]
    public async Task Get_ItemWithNoCostRows_UsageIsNull()
    {
        // Mirrors the "agent has no IAgentCostExtractor" scenario — no rows are
        // ever recorded, so the API reports usage as unknown.
        var item = NewItem();
        await _factory.Store.CreateAsync(item);

        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/workitems/{item.Id}");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        // The default ASP.NET serializer keeps the property but as JSON null;
        // either absent or null is acceptable per the surface contract.
        if (body.TryGetProperty("usage", out var usage))
            Assert.Equal(JsonValueKind.Null, usage.ValueKind);
        if (body.TryGetProperty("usageTotal", out var total))
            Assert.Equal(JsonValueKind.Null, total.ValueKind);
    }

    private static WorkItem NewItem() => new()
    {
        Id = new WorkItemId(Guid.NewGuid()),
        ProjectId = new ProjectId("test-project"),
        Title = "Usage test",
        Prompt = "test",
        State = WorkItemState.Done,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        WorkTimeout = TimeSpan.FromHours(1),
        MergeTimeout = TimeSpan.FromMinutes(30),
    };

    private static WorkItemCost NewCost(
        WorkItemId workItemId,
        string phase,
        int? iteration,
        int input,
        int output,
        double usd,
        double elapsedSeconds)
    {
        var ended = DateTimeOffset.UtcNow;
        return new WorkItemCost
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkItemId = workItemId.ToString(),
            Phase = phase,
            Iteration = iteration,
            AgentKind = "claude",
            ModelId = "claude-opus-4-7",
            InputTokens = input,
            CachedInputTokens = 0,
            OutputTokens = output,
            EstimatedUsd = usd,
            StartedAt = ended.AddSeconds(-elapsedSeconds),
            EndedAt = ended,
        };
    }
}
