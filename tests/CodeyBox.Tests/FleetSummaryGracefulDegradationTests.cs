using System.Net;
using System.Net.Http.Json;
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

/// <summary>
/// Tests that GET /fleet/summary degrades gracefully when the cost store
/// reads throw (e.g., work_item_costs table not yet created). Budget fields
/// must be null; the endpoint must not propagate the exception.
///
/// Note: the 'if (costStore is not null)' null-service branch in FleetEndpoints
/// is exercised only when IWorkItemCostStore is absent from the DI container. In
/// production and integration tests IWorkItemCostStore is always registered
/// (Program.cs line ~706) because other endpoints take it as a required DI
/// parameter; removing it crashes router initialization. The throwing-store path
/// below is the operationally realistic graceful-degradation scenario and covers
/// the important code paths.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class FleetSummaryGracefulDegradationTests : IDisposable
{
    private readonly ThrowingCostStoreFleetFactory _factory = new();
    private readonly HttpClient _client;

    public FleetSummaryGracefulDegradationTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task GetFleetSummary_WithThrowingCostStore_Returns200()
    {
        var resp = await _client.GetAsync("/fleet/summary");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task GetFleetSummary_WithThrowingCostStore_BudgetFieldsNull()
    {
        var resp = await _client.GetAsync("/fleet/summary");
        var summaries = await resp.Content.ReadFromJsonAsync<List<FleetDegradationRow>>();
        Assert.NotNull(summaries);
        Assert.Single(summaries);

        var row = summaries[0];
        Assert.Null(row.MonthlySpendUsd);
        Assert.Null(row.MonthlyBudgetUsd);
        Assert.Equal("unknown", row.BudgetThresholdState);
    }

    [Fact]
    public async Task GetFleetSummary_WithThrowingCostStore_OtherFieldsStillPopulated()
    {
        await _factory.Store.CreateAsync(new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("deg-proj"),
            Title = "t",
            Prompt = "p",
        });

        var resp = await _client.GetAsync("/fleet/summary");
        var summaries = await resp.Content.ReadFromJsonAsync<List<FleetDegradationRow>>();
        var row = summaries![0];

        // State counts and project metadata still populated despite cost-store failure.
        Assert.Equal("deg-proj", row.ProjectId);
        Assert.Equal(1, row.QueuedCount);
        Assert.False(row.IsPaused);
    }

    private sealed record FleetDegradationRow(
        string ProjectId,
        int QueuedCount,
        int InFlightCount,
        bool IsPaused,
        double? MonthlySpendUsd,
        double? MonthlyBudgetUsd,
        string BudgetThresholdState);
}

/// <summary>
/// Factory where IWorkItemCostStore is replaced with a stub that always throws
/// on reads, simulating the scenario where the work_item_costs table has not yet
/// been created (cost-reporting work item not landed). The endpoint must catch
/// the exception and return null budget fields rather than propagating the error.
/// </summary>
internal sealed class ThrowingCostStoreFleetFactory : CodeyBox.Tests.CodeyBoxWebApplicationFactory
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-fleet-deg-{Guid.NewGuid():N}.db");

    public SqliteWorkItemStore Store { get; }

    public ThrowingCostStoreFleetFactory()
    {
        Store = new SqliteWorkItemStore(_dbPath);
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
            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository(
                new Project
                {
                    Id = new ProjectId("deg-proj"),
                    DisplayName = "Degradation Project",
                    RepositoryUrl = "https://github.com/test/deg",
                }));
            // Replace cost store with a throwing stub to simulate missing table.
            services.RemoveAll<IWorkItemCostStore>();
            services.AddSingleton<IWorkItemCostStore>(new ThrowingWorkItemCostStore());
        });
    }

    protected override void Dispose(bool disposing)
        => DisposeHostThenDeleteSqliteDatabase(disposing, _dbPath, Store.Dispose);
}

/// <summary>
/// Stub IWorkItemCostStore whose reads always throw, simulating a missing or broken table.
/// </summary>
internal sealed class ThrowingWorkItemCostStore : IWorkItemCostStore
{
    public Task RecordAsync(WorkItemCost cost, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<WorkItemCost>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default)
        => Task.FromException<IReadOnlyList<WorkItemCost>>(new InvalidOperationException("no such table: work_item_costs"));

    public Task<IReadOnlyList<WorkItemCost>> GetByProjectAsync(
        string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
        => Task.FromException<IReadOnlyList<WorkItemCost>>(new InvalidOperationException("no such table: work_item_costs"));

    public Task<IReadOnlyList<(string ProjectId, double TotalUsd)>> GetFleetCostSummaryAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
        => Task.FromException<IReadOnlyList<(string, double)>>(new InvalidOperationException("no such table: work_item_costs"));

    public Task DeleteByWorkItemAsync(string workItemId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<decimal> SumEstimatedUsdAsync(
        string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
        => Task.FromException<decimal>(new InvalidOperationException("no such table: work_item_costs"));
}
