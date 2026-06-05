using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;

namespace CodeyBox.Tests;

public sealed class WorkItemCostsEndpointTests : IClassFixture<CostsApiFactory>
{
    private readonly CostsApiFactory _factory;

    public WorkItemCostsEndpointTests(CostsApiFactory factory) => _factory = factory;

    [Fact]
    public async Task GetCosts_UnknownId_Returns404()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/workitems/{Guid.NewGuid()}/costs");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetCosts_InvalidGuid_Returns400()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/workitems/not-a-guid/costs");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetCosts_ExistingItemNoCosts_ReturnsTotalsZero()
    {
        var item = CreateItem();
        await _factory.Store.CreateAsync(item);

        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/workitems/{item.Id}/costs");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(item.Id.Value.ToString("D"), body.GetProperty("workItemId").GetString());
        Assert.Equal(0, body.GetProperty("totals").GetProperty("inputTokens").GetInt32());
        Assert.Equal(0, body.GetProperty("totals").GetProperty("outputTokens").GetInt32());
        Assert.Equal(0.0, body.GetProperty("totals").GetProperty("estimatedUsd").GetDouble());
    }

    [Fact]
    public async Task GetCosts_WithCostRows_ReturnsTotalsAndByPhase()
    {
        var item = CreateItem();
        await _factory.Store.CreateAsync(item);

        var workCost = new WorkItemCost
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkItemId = item.Id.ToString(),
            Phase = "work",
            AgentKind = "claude",
            ModelId = "claude-opus-4-7",
            InputTokens = 10000,
            CachedInputTokens = 0,
            OutputTokens = 500,
            EstimatedUsd = 0.1875,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-10),
            EndedAt = DateTimeOffset.UtcNow,
        };
        var auditCost = new WorkItemCost
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkItemId = item.Id.ToString(),
            Phase = "audit",
            Iteration = 1,
            AgentKind = "claude",
            ModelId = "claude-opus-4-7",
            InputTokens = 2000,
            CachedInputTokens = 0,
            OutputTokens = 100,
            EstimatedUsd = 0.0375,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
            EndedAt = DateTimeOffset.UtcNow,
        };

        await _factory.CostStore.RecordAsync(workCost);
        await _factory.CostStore.RecordAsync(auditCost);

        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/workitems/{item.Id}/costs");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(12000, body.GetProperty("totals").GetProperty("inputTokens").GetInt32());
        Assert.Equal(600, body.GetProperty("totals").GetProperty("outputTokens").GetInt32());

        var byPhase = body.GetProperty("byPhase");
        Assert.True(byPhase.TryGetProperty("work", out _));
        Assert.True(byPhase.TryGetProperty("audit", out _));
    }

    [Fact]
    public async Task GetCosts_ByAgentSplitsSameKindSameModelByInstance()
    {
        var item = CreateItem();
        await _factory.Store.CreateAsync(item);

        await _factory.CostStore.RecordAsync(new WorkItemCost
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkItemId = item.Id.ToString(),
            Phase = "work",
            AgentKind = "claude",
            AgentInstanceId = "claude/acct-a",
            ModelId = "claude-opus-4-7",
            InputTokens = 1000,
            OutputTokens = 100,
            EstimatedUsd = 0.10,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-10),
            EndedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
        });
        await _factory.CostStore.RecordAsync(new WorkItemCost
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkItemId = item.Id.ToString(),
            Phase = "audit",
            AgentKind = "claude",
            AgentInstanceId = "claude/acct-b",
            ModelId = "claude-opus-4-7",
            InputTokens = 2000,
            OutputTokens = 200,
            EstimatedUsd = 0.20,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-4),
            EndedAt = DateTimeOffset.UtcNow,
        });

        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/workitems/{item.Id}/costs");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var byAgent = body.GetProperty("byAgent").EnumerateArray().ToList();
        Assert.Equal(2, byAgent.Count);
        Assert.Contains(byAgent, a =>
            a.GetProperty("agent").GetString() == "claude" &&
            a.GetProperty("agentInstanceId").GetString() == "claude/acct-a" &&
            a.GetProperty("estimatedUsd").GetDouble() == 0.10);
        Assert.Contains(byAgent, a =>
            a.GetProperty("agent").GetString() == "claude" &&
            a.GetProperty("agentInstanceId").GetString() == "claude/acct-b" &&
            a.GetProperty("estimatedUsd").GetDouble() == 0.20);
    }

    [Fact]
    public async Task GetProjectCosts_ReturnsTotalsForDateRange()
    {
        var item = CreateItem(projectId: "proj-costs-test");
        await _factory.Store.CreateAsync(item);

        var cost = new WorkItemCost
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkItemId = item.Id.ToString(),
            Phase = "work",
            AgentKind = "claude",
            InputTokens = 1000,
            OutputTokens = 200,
            EstimatedUsd = 0.03,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
            EndedAt = DateTimeOffset.UtcNow,
        };
        await _factory.CostStore.RecordAsync(cost);

        var from = DateTimeOffset.UtcNow.AddDays(-1).ToString("O");
        var to = DateTimeOffset.UtcNow.AddDays(1).ToString("O");

        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/projects/proj-costs-test/costs?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("proj-costs-test", body.GetProperty("projectId").GetString());
        Assert.Equal(1000, body.GetProperty("totals").GetProperty("inputTokens").GetInt32());
    }

    private static WorkItem CreateItem(string projectId = "test-project") => new()
    {
        Id = new WorkItemId(Guid.NewGuid()),
        ProjectId = new ProjectId(projectId),
        Title = "Test",
        Prompt = "test",
        State = WorkItemState.Done,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        WorkTimeout = TimeSpan.FromHours(1),
        MergeTimeout = TimeSpan.FromMinutes(30),
    };
}

public sealed class CostsApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-costs-httptest-{Guid.NewGuid():N}.db");

    public SqliteWorkItemStore Store { get; }
    public SqliteWorkItemCostStore CostStore { get; }

    public CostsApiFactory()
    {
        Store = new SqliteWorkItemStore(_dbPath);
        CostStore = new SqliteWorkItemCostStore(_dbPath);
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
            services.AddSingleton<IWorkItemCostStore>(CostStore);
            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository(
                new Project
                {
                    Id = new ProjectId("proj-costs-test"),
                    DisplayName = "Cost Test Project",
                    RepositoryUrl = "https://example.com/test",
                }));
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CostStore.Dispose();
            Store.Dispose();
            try { File.Delete(_dbPath); } catch { /* best-effort */ }
        }
        base.Dispose(disposing);
    }
}
