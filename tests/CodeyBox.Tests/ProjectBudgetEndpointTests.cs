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

namespace CodeyBox.Tests;

public sealed class ProjectBudgetEndpointTests : IClassFixture<BudgetApiFactory>
{
    private readonly BudgetApiFactory _factory;
    public ProjectBudgetEndpointTests(BudgetApiFactory factory) => _factory = factory;

    // ── GET /projects/{id}/budget ─────────────────────────────────────────────

    [Fact]
    public async Task GetBudget_UnknownProject_Returns404()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/projects/unknown-project/budget");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetBudget_NoBudgetConfigured_ReturnsZeroState()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/projects/budget-test-project/budget");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("budget-test-project", body.GetProperty("projectId").GetString());
        Assert.Equal(0m, body.GetProperty("monthlyBudgetUsd").GetDecimal());
        Assert.Equal("ok", body.GetProperty("thresholdState").GetString());
    }

    [Fact]
    public async Task GetBudget_WithBudgetAndSpend_ReturnsPct()
    {
        // Record some costs for the project.
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("budget-project-with-spend"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Done,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            WorkTimeout = TimeSpan.FromHours(1),
            MergeTimeout = TimeSpan.FromMinutes(30),
        };
        await _factory.Store.CreateAsync(item);
        await _factory.CostStore.RecordAsync(new WorkItemCost
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkItemId = item.Id.ToString(),
            Phase = "work",
            AgentKind = "claude",
            InputTokens = 1000,
            OutputTokens = 100,
            EstimatedUsd = 400.0,  // 80% of $500 budget
            StartedAt = DateTimeOffset.UtcNow.AddDays(-1),
            EndedAt = DateTimeOffset.UtcNow,
        });

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/projects/budget-project-with-spend/budget");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(500m, body.GetProperty("monthlyBudgetUsd").GetDecimal());
        Assert.Equal(400m, body.GetProperty("currentSpendUsd").GetDecimal());
        Assert.Equal(80.0, body.GetProperty("pct").GetDouble(), precision: 1);
        Assert.Equal("warning", body.GetProperty("thresholdState").GetString());
    }

    // ── POST /projects/{id}/queue/pause ───────────────────────────────────────

    [Fact]
    public async Task PauseProjectQueue_UnknownProject_Returns404()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/projects/unknown/queue/pause", new { reason = "test" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task PauseProjectQueue_MissingReason_Returns400()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/projects/budget-test-project/queue/pause", new { reason = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PauseProjectQueue_ValidReason_ReturnsPausedState()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync(
            "/projects/budget-test-project/queue/pause",
            new { reason = "manual test pause" });
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("paused").GetBoolean());
    }

    // ── POST /projects/{id}/queue/resume ──────────────────────────────────────

    [Fact]
    public async Task ResumeProjectQueue_UnknownProject_Returns404()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/projects/unknown/queue/resume", new { });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task ResumeProjectQueue_Returns200_EvenIfNotPaused()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/projects/budget-test-project/queue/resume", new { });
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("paused").GetBoolean());
    }
}

public sealed class BudgetApiFactory : CodeyBox.Tests.CodeyBoxWebApplicationFactory
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-budget-httptest-{Guid.NewGuid():N}.db");

    public SqliteWorkItemStore Store { get; }
    public SqliteWorkItemCostStore CostStore { get; }

    public BudgetApiFactory()
    {
        Store = new SqliteWorkItemStore(_dbPath);
        CostStore = new SqliteWorkItemCostStore(_dbPath);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var tmp = Temp.Root;
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
                    Id = new ProjectId("budget-test-project"),
                    DisplayName = "Budget Test Project",
                    RepositoryUrl = "https://example.com/test",
                    // No cost budget configured — tests basic shape.
                },
                new Project
                {
                    Id = new ProjectId("budget-project-with-spend"),
                    DisplayName = "Budget Project With Spend",
                    RepositoryUrl = "https://example.com/spend",
                    Budget = new ProjectBudget
                    {
                        MonthlyCostBudgetUsd = 500m,
                        CostWarningThresholdPct = 80,
                        CostHardCapPct = 100,
                    },
                }));
        });
    }

    protected override void Dispose(bool disposing)
        => DisposeHostThenDeleteSqliteDatabase(
            disposing,
            _dbPath,
            CostStore.Dispose,
            Store.Dispose);
}
