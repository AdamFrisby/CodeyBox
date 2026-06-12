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
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// HTTP-level tests for GET /fleet/summary. Uses the WebApplicationFactory
/// pattern with a real in-memory SQLite store. Asserts summary shape, counts,
/// and recent-outcome ordering.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class FleetSummaryEndpointTests : IDisposable
{
    private readonly FleetApiFactory _factory = new();
    private readonly HttpClient _client;

    public FleetSummaryEndpointTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task GetFleetSummary_NoItems_ReturnsSummaryWithZeroCounts()
    {
        var resp = await _client.GetAsync("/fleet/summary");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var summaries = await resp.Content.ReadFromJsonAsync<List<FleetRow>>();
        Assert.NotNull(summaries);
        // Factory always registers two projects; both should appear.
        Assert.Equal(2, summaries.Count);

        var row = summaries.Single(r => r.ProjectId == "proj-alpha");
        Assert.Equal("Alpha Project", row.DisplayName);
        Assert.Equal(0, row.QueuedCount);
        Assert.Equal(0, row.InFlightCount);
        Assert.Null(row.CurrentPhase);
        Assert.Empty(row.RecentOutcomes);
        Assert.False(row.IsPaused);
        Assert.False(row.HasRecentFailures);
    }

    [Fact]
    public async Task GetFleetSummary_QueuedItem_CountsCorrectly()
    {
        _factory.SeedWorkItem("proj-alpha", WorkItemState.Queued);
        _factory.SeedWorkItem("proj-alpha", WorkItemState.Queued);

        var resp = await _client.GetAsync("/fleet/summary");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var summaries = await resp.Content.ReadFromJsonAsync<List<FleetRow>>();
        var row = summaries!.Single(r => r.ProjectId == "proj-alpha");
        Assert.Equal(2, row.QueuedCount);
        Assert.Equal(0, row.InFlightCount);
        Assert.Null(row.CurrentPhase);
    }

    [Fact]
    public async Task GetFleetSummary_WorkingItem_CountsAsInFlight()
    {
        _factory.SeedWorkItem("proj-alpha", WorkItemState.Working);

        var resp = await _client.GetAsync("/fleet/summary");
        var summaries = await resp.Content.ReadFromJsonAsync<List<FleetRow>>();
        var row = summaries!.Single(r => r.ProjectId == "proj-alpha");
        Assert.Equal(0, row.QueuedCount);
        Assert.Equal(1, row.InFlightCount);
        Assert.Equal("Working", row.CurrentPhase);
    }

    [Fact]
    public async Task GetFleetSummary_RecentOutcomes_OrderedNewestFirst()
    {
        var now = DateTimeOffset.UtcNow;
        // Seed in reverse order to ensure UpdatedAt drives sorting, not insertion order.
        _factory.SeedWorkItemAt("proj-alpha", WorkItemState.Failed, now.AddMinutes(-1));
        _factory.SeedWorkItemAt("proj-alpha", WorkItemState.Done, now.AddMinutes(-5));
        _factory.SeedWorkItemAt("proj-alpha", WorkItemState.Done, now.AddMinutes(-3));

        var resp = await _client.GetAsync("/fleet/summary");
        var summaries = await resp.Content.ReadFromJsonAsync<List<FleetRow>>();
        var row = summaries!.Single(r => r.ProjectId == "proj-alpha");

        Assert.Equal(3, row.RecentOutcomes.Count);
        Assert.Equal("Failed", row.RecentOutcomes[0]);   // newest first
        Assert.Equal("Done", row.RecentOutcomes[1]);
        Assert.Equal("Done", row.RecentOutcomes[2]);
    }

    [Fact]
    public async Task GetFleetSummary_RecentOutcomes_CappedAtFive()
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 8; i++)
            _factory.SeedWorkItemAt("proj-alpha", WorkItemState.Done, now.AddMinutes(-i));

        var resp = await _client.GetAsync("/fleet/summary");
        var summaries = await resp.Content.ReadFromJsonAsync<List<FleetRow>>();
        Assert.Equal(5, summaries!.Single(r => r.ProjectId == "proj-alpha").RecentOutcomes.Count);
    }

    [Fact]
    public async Task GetFleetSummary_InFlightItemsNotInRecentOutcomes()
    {
        _factory.SeedWorkItem("proj-alpha", WorkItemState.Working);
        _factory.SeedWorkItem("proj-alpha", WorkItemState.Done);

        var resp = await _client.GetAsync("/fleet/summary");
        var summaries = await resp.Content.ReadFromJsonAsync<List<FleetRow>>();
        var row = summaries!.Single(r => r.ProjectId == "proj-alpha");

        Assert.Equal(1, row.InFlightCount);
        Assert.Single(row.RecentOutcomes);
        Assert.Equal("Done", row.RecentOutcomes[0]);
    }

    [Fact]
    public async Task GetFleetSummary_MultipleProjects_ReturnsRowPerProject()
    {
        _factory.SeedWorkItem("proj-alpha", WorkItemState.Done);
        // proj-beta has no items

        var resp = await _client.GetAsync("/fleet/summary");
        var summaries = await resp.Content.ReadFromJsonAsync<List<FleetRow>>();
        Assert.NotNull(summaries);
        Assert.Equal(2, summaries.Count);

        var alpha = summaries.Single(r => r.ProjectId == "proj-alpha");
        var beta = summaries.Single(r => r.ProjectId == "proj-beta");

        Assert.Single(alpha.RecentOutcomes);
        Assert.Empty(beta.RecentOutcomes);
        Assert.Null(beta.CurrentPhase);
    }

    [Fact]
    public async Task GetFleetSummary_IsPaused_FalseByDefault()
    {
        var resp = await _client.GetAsync("/fleet/summary");
        var summaries = await resp.Content.ReadFromJsonAsync<List<FleetRow>>();
        Assert.All(summaries!, r => Assert.False(r.IsPaused));
    }

    // Regression: GetFleetPauseStatesAsync used to SELECT a non-existent
    // `is_paused` column, returning HTTP 500 on every /fleet/summary call.
    // The column is `paused`, owned by SqliteQueueController.
    [Fact]
    public async Task GetFleetSummary_PausedProject_ReflectedAsPaused()
    {
        var pauseResp = await _client.PostAsJsonAsync(
            "/projects/proj-alpha/queue/pause",
            new { reason = "regression test" });
        Assert.Equal(HttpStatusCode.OK, pauseResp.StatusCode);

        var resp = await _client.GetAsync("/fleet/summary");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var summaries = await resp.Content.ReadFromJsonAsync<List<FleetRow>>();
        Assert.NotNull(summaries);
        Assert.True(summaries.Single(r => r.ProjectId == "proj-alpha").IsPaused);
        Assert.False(summaries.Single(r => r.ProjectId == "proj-beta").IsPaused);
    }

    [Fact]
    public async Task GetFleetSummary_BudgetThresholdState_OkWhenCostStoreAvailable()
    {
        var resp = await _client.GetAsync("/fleet/summary");
        var summaries = await resp.Content.ReadFromJsonAsync<List<FleetRow>>();
        // Cost store is available in this factory; threshold state should be "ok".
        Assert.All(summaries!, r => Assert.Equal("ok", r.BudgetThresholdState));
    }

    [Fact]
    public async Task GetFleetSummary_ThreeOrMoreRecentFailures_HasRecentFailuresTrue()
    {
        // Seed 3 failed and 1 successful outcome for proj-alpha.
        _factory.SeedWorkItem("proj-alpha", WorkItemState.Failed);
        _factory.SeedWorkItem("proj-alpha", WorkItemState.Failed);
        _factory.SeedWorkItem("proj-alpha", WorkItemState.Failed);
        _factory.SeedWorkItem("proj-alpha", WorkItemState.Done);

        var resp = await _client.GetAsync("/fleet/summary");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var summaries = await resp.Content.ReadFromJsonAsync<List<FleetRow>>();
        var alpha = summaries!.Single(r => r.ProjectId == "proj-alpha");
        Assert.True(alpha.HasRecentFailures);

        // proj-beta with no items should not have recent failures.
        var beta = summaries!.Single(r => r.ProjectId == "proj-beta");
        Assert.False(beta.HasRecentFailures);
    }

    [Fact]
    public async Task GetFleetSummary_TwoRecentFailures_HasRecentFailuresFalse()
    {
        // Only 2 failures — below the ≥3 threshold.
        _factory.SeedWorkItem("proj-alpha", WorkItemState.Failed);
        _factory.SeedWorkItem("proj-alpha", WorkItemState.Failed);
        _factory.SeedWorkItem("proj-alpha", WorkItemState.Done);

        var resp = await _client.GetAsync("/fleet/summary");
        var summaries = await resp.Content.ReadFromJsonAsync<List<FleetRow>>();
        var row = summaries!.Single(r => r.ProjectId == "proj-alpha");
        Assert.False(row.HasRecentFailures);
    }

    [Fact]
    public async Task GetFleetSummary_AuditFailedCountsAsFailureForRedDot()
    {
        // AuditFailed should count toward the ≥3 threshold.
        _factory.SeedWorkItem("proj-alpha", WorkItemState.AuditFailed);
        _factory.SeedWorkItem("proj-alpha", WorkItemState.Failed);
        _factory.SeedWorkItem("proj-alpha", WorkItemState.AuditFailed);

        var resp = await _client.GetAsync("/fleet/summary");
        var summaries = await resp.Content.ReadFromJsonAsync<List<FleetRow>>();
        var row = summaries!.Single(r => r.ProjectId == "proj-alpha");
        Assert.True(row.HasRecentFailures);
    }

    // ── Local record shapes for deserialization ────────────────────────────────

    private sealed record FleetRow(
        string ProjectId,
        string DisplayName,
        int QueuedCount,
        int InFlightCount,
        string? CurrentPhase,
        List<string> RecentOutcomes,
        bool IsPaused,
        bool HasRecentFailures,
        string? PausedReason,
        double? MonthlySpendUsd,
        double? MonthlyBudgetUsd,
        string BudgetThresholdState);
}

/// <summary>
/// WebApplicationFactory for fleet endpoint tests. Seeds two projects and
/// exposes helpers to seed work items directly into the SQLite store.
/// </summary>
internal sealed class FleetApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-fleet-{Guid.NewGuid():N}.db");

    public SqliteWorkItemStore Store { get; }

    private static readonly Project[] Projects =
    [
        new Project
        {
            Id = new ProjectId("proj-alpha"),
            DisplayName = "Alpha Project",
            RepositoryUrl = "https://github.com/test/alpha",
        },
        new Project
        {
            Id = new ProjectId("proj-beta"),
            DisplayName = "Beta Project",
            RepositoryUrl = "https://github.com/test/beta",
        },
    ];

    public FleetApiFactory()
    {
        Store = new SqliteWorkItemStore(_dbPath);
    }

    public void SeedWorkItem(string projectId, WorkItemState state)
        => SeedWorkItemAt(projectId, state, DateTimeOffset.UtcNow);

    public void SeedWorkItemAt(string projectId, WorkItemState state, DateTimeOffset updatedAt)
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId(projectId),
            Title = "test",
            Prompt = "test",
        };
        // Use CreateAsync + UpdateAsync pattern via direct store calls.
        Store.CreateAsync(item).GetAwaiter().GetResult();

        if (state != WorkItemState.Queued)
        {
            var updated = item.With(state) with { UpdatedAt = updatedAt };
            Store.UpdateAsync(updated).GetAwaiter().GetResult();
        }
        else if (updatedAt != item.UpdatedAt)
        {
            var updated = item with { UpdatedAt = updatedAt };
            Store.UpdateAsync(updated).GetAwaiter().GetResult();
        }
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
            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository(Projects));
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
}
