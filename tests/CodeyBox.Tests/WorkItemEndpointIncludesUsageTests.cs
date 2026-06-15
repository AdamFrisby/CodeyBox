using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;

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

        await _factory.CostStore.RecordAsync(NewCost(item.Id, "work", null, 5000, 500, 0.10, 4, cached: 250));
        await _factory.CostStore.RecordAsync(NewCost(item.Id, "audit", 1, 2000, 100, 0.04, 2));
        await _factory.CostStore.RecordAsync(NewCost(item.Id, "rework", 2, 8000, 900, 0.20, 6, cached: 750));
        await _factory.CostStore.RecordAsync(NewCost(item.Id, "audit", 2, 1500, 80, 0.03, 1.5));

        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/workitems/{item.Id}");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var usage = body.GetProperty("usage");
        Assert.Equal(2, usage.GetProperty("iteration").GetInt32());
        Assert.Equal(8000 + 750 + 1500, usage.GetProperty("tokensInput").GetInt32());
        Assert.Equal(900 + 80, usage.GetProperty("tokensOutput").GetInt32());
        Assert.Equal(0, usage.GetProperty("tokensReasoning").GetInt32());
        // End-to-end propagation of cached tokens (iter 2 = 750 from rework row).
        Assert.Equal(750, usage.GetProperty("tokensCached").GetInt32());
        Assert.Equal(0.23, usage.GetProperty("costUsd").GetDouble(), precision: 4);

        var total = body.GetProperty("usageTotal");
        Assert.Equal(5000 + 250 + 2000 + 8000 + 750 + 1500, total.GetProperty("tokensInput").GetInt32());
        Assert.Equal(500 + 100 + 900 + 80, total.GetProperty("tokensOutput").GetInt32());
        // Cumulative cached = 250 (work) + 750 (rework) = 1000.
        Assert.Equal(1000, total.GetProperty("tokensCached").GetInt32());
        Assert.Equal(0.37, total.GetProperty("costUsd").GetDouble(), precision: 4);
    }

    [Fact]
    public async Task Get_ItemWithNoCostRows_UsageIsOmitted()
    {
        // Mirrors the "agent has no IAgentCostExtractor" scenario — no rows are
        // ever recorded, so the API reports usage as unknown by omitting the
        // property entirely (DefaultIgnoreCondition=WhenWritingNull applied via
        // [JsonIgnore] on the DTO).
        var item = NewItem();
        await _factory.Store.CreateAsync(item);

        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/workitems/{item.Id}");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.TryGetProperty("usage", out _),
            "usage must be absent (not null) when no cost rows exist");
        Assert.False(body.TryGetProperty("usageTotal", out _),
            "usageTotal must be absent (not null) when no cost rows exist");
    }

    [Fact]
    public async Task GetAndList_WaitingForQuotaReset_IncludesQuotaRetryPhase()
    {
        var resetAt = DateTimeOffset.UtcNow.AddHours(1);
        var item = NewItem() with
        {
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            QuotaResetAt = resetAt,
            NextQuotaRetryAt = resetAt,
            QuotaRetryFrom = "audit",
            QuotaRetryPhase = "rework",
        };
        await _factory.Store.CreateAsync(item);

        var client = _factory.CreateClient();
        var getResp = await client.GetAsync($"/workitems/{item.Id}");
        getResp.EnsureSuccessStatusCode();

        var getBody = await getResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("audit", getBody.GetProperty("quotaRetryFrom").GetString());
        Assert.Equal("rework", getBody.GetProperty("quotaRetryPhase").GetString());

        var listResp = await client.GetAsync("/workitems");
        listResp.EnsureSuccessStatusCode();
        var listBody = await listResp.Content.ReadFromJsonAsync<JsonElement>();
        var row = Assert.Single(
            listBody.EnumerateArray(),
            candidate => candidate.GetProperty("id").GetString() == item.Id.ToString());
        Assert.Equal("audit", row.GetProperty("quotaRetryFrom").GetString());
        Assert.Equal("rework", row.GetProperty("quotaRetryPhase").GetString());
    }

    [Fact]
    public async Task List_ItemWithCostRows_IncludesUsageAndUsageTotal()
    {
        // Verifies that the LIST endpoint (GET /workitems) — not just GET /{id}
        // — surfaces usage. Two items, one with costs and one without: the
        // mapper must thread the right summary to the right item, not zero them
        // out across the list.
        var withCosts = NewItem();
        var withoutCosts = NewItem();
        await _factory.Store.CreateAsync(withCosts);
        await _factory.Store.CreateAsync(withoutCosts);

        await _factory.CostStore.RecordAsync(NewCost(withCosts.Id, "work", null, 1234, 56, 0.0123, 1.0, cached: 99));

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/workitems");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);

        JsonElement? withRow = null, withoutRow = null;
        foreach (var row in body.EnumerateArray())
        {
            var id = row.GetProperty("id").GetString();
            if (id == withCosts.Id.ToString()) withRow = row;
            else if (id == withoutCosts.Id.ToString()) withoutRow = row;
        }
        Assert.NotNull(withRow);
        Assert.NotNull(withoutRow);

        var usage = withRow!.Value.GetProperty("usage");
        Assert.Equal(1, usage.GetProperty("iteration").GetInt32());
        Assert.Equal(1234 + 99, usage.GetProperty("tokensInput").GetInt32());
        Assert.Equal(56, usage.GetProperty("tokensOutput").GetInt32());
        Assert.Equal(99, usage.GetProperty("tokensCached").GetInt32());

        var total = withRow.Value.GetProperty("usageTotal");
        Assert.Equal(1234 + 99, total.GetProperty("tokensInput").GetInt32());

        // The item with no costs must not have a usage/usageTotal block at all.
        Assert.False(withoutRow!.Value.TryGetProperty("usage", out _),
            "list entry for cost-less item must omit usage");
        Assert.False(withoutRow.Value.TryGetProperty("usageTotal", out _),
            "list entry for cost-less item must omit usageTotal");
    }

    [Fact]
    public async Task Get_ThrowingCostStore_ReturnsOkWithUsageOmitted()
    {
        // Best-effort contract: a cost-store fault must not break the API. The
        // helper swallows non-cancellation exceptions and logs at Debug; the
        // surface still returns 200 with usage absent. A future commit that
        // removed the catch (or narrowed it incorrectly) would break this
        // invariant — keeping the test guards that.
        using var factory = new ThrowingCostStoreApiFactory();
        var item = NewItem();
        await factory.Store.CreateAsync(item);

        var client = factory.CreateClient();
        var resp = await client.GetAsync($"/workitems/{item.Id}");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.TryGetProperty("usage", out _));
        Assert.False(body.TryGetProperty("usageTotal", out _));
    }

    [Fact]
    public async Task List_ThrowingCostStore_ReturnsOkWithUsageOmitted()
    {
        using var factory = new ThrowingCostStoreApiFactory();
        var item = NewItem();
        await factory.Store.CreateAsync(item);

        var client = factory.CreateClient();
        var resp = await client.GetAsync("/workitems");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        var row = Assert.Single(body.EnumerateArray().ToList());
        Assert.False(row.TryGetProperty("usage", out _));
        Assert.False(row.TryGetProperty("usageTotal", out _));
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
        double elapsedSeconds,
        int cached = 0)
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
            CachedInputTokens = cached,
            OutputTokens = output,
            EstimatedUsd = usd,
            StartedAt = ended.AddSeconds(-elapsedSeconds),
            EndedAt = ended,
        };
    }
}

/// <summary>
/// Variant API factory that wires a cost store whose every read throws. Used to
/// pin the "best-effort, return 200 with usage absent" contract on both the
/// single-item and list endpoints.
/// </summary>
public sealed class ThrowingCostStoreApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-throwing-cost-httptest-{Guid.NewGuid():N}.db");

    public SqliteWorkItemStore Store { get; }

    public ThrowingCostStoreApiFactory()
    {
        Store = new SqliteWorkItemStore(_dbPath);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var tmp = Path.GetTempPath();
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IWorkItemStore>();
            services.AddSingleton<IWorkItemStore>(Store);
            services.RemoveAll<IWorkItemCostStore>();
            services.AddSingleton<IWorkItemCostStore>(new ThrowingCostStore());
            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository());
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Store.Dispose();
            try { File.Delete(_dbPath); } catch { /* best-effort */ }
        }
        base.Dispose(disposing);
    }

    private sealed class ThrowingCostStore : IWorkItemCostStore
    {
        public Task RecordAsync(WorkItemCost cost, CancellationToken ct = default)
            => throw new InvalidOperationException("injected cost store failure");

        public Task<IReadOnlyList<WorkItemCost>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default)
            => throw new InvalidOperationException("injected cost store failure");

        public Task<IReadOnlyList<WorkItemCost>> GetByProjectAsync(
            string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
            => throw new InvalidOperationException("injected cost store failure");

        public Task<IReadOnlyList<(string ProjectId, double TotalUsd)>> GetFleetCostSummaryAsync(
            DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
            => throw new InvalidOperationException("injected cost store failure");

        public Task DeleteByWorkItemAsync(string workItemId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<decimal> SumEstimatedUsdAsync(
            string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
            => Task.FromResult(0m);
    }
}
