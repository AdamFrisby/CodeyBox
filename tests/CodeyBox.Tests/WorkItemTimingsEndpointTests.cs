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

public sealed class WorkItemTimingsEndpointTests : IClassFixture<TimingsApiFactory>
{
    private readonly TimingsApiFactory _factory;

    public WorkItemTimingsEndpointTests(TimingsApiFactory factory) => _factory = factory;

    [Fact]
    public async Task GetTimings_UnknownId_Returns404()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/workitems/{Guid.NewGuid()}/timings");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetTimings_InvalidGuid_Returns400()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/workitems/not-a-guid/timings");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetTimings_ExistingItemNoTimings_ReturnsZeroTotal()
    {
        var item = CreateItem();
        await _factory.Store.CreateAsync(item);

        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/workitems/{item.Id}/timings");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(item.Id.Value.ToString("D"), body.GetProperty("workItemId").GetString());
        Assert.Equal(0, body.GetProperty("totalDurationMs").GetInt64());
    }

    [Fact]
    public async Task GetTimings_WithTimingRows_ReturnsCorrectTotals()
    {
        var item = CreateItem();
        await _factory.Store.CreateAsync(item);

        var rec = new TimingRecord
        {
            WorkItemId = item.Id,
            Phase = "work",
            Step = "agent.exec",
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-10),
            EndedAt = DateTimeOffset.UtcNow,
            DurationMs = 10_000,
            MetadataJson = "{}",
        };
        await _factory.TimingStore.BeginAsync(rec);
        await _factory.TimingStore.EndAsync(rec.Id, rec.EndedAt!.Value, rec.DurationMs!.Value);

        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/workitems/{item.Id}/timings");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(10_000, body.GetProperty("totalDurationMs").GetInt64());

        var topSteps = body.GetProperty("topSteps").EnumerateArray().ToList();
        Assert.NotEmpty(topSteps);
        Assert.Equal("agent.exec", topSteps[0].GetProperty("step").GetString());
    }

    [Fact]
    public async Task GetTimings_LegacyDotnetSubSteps_AreExcludedFromTotals()
    {
        var item = CreateItem();
        await _factory.Store.CreateAsync(item);

        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-10);
        await AddCompletedTimingAsync(item.Id, "audit", "audit.phase", startedAt, 1_000);
        await AddCompletedTimingAsync(item.Id, "audit", "dotnet.build", startedAt.AddMilliseconds(100), 400);
        await AddCompletedTimingAsync(item.Id, "audit", "dotnet.test_run", startedAt.AddMilliseconds(200), 600);

        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/workitems/{item.Id}/timings");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1_000, body.GetProperty("totalDurationMs").GetInt64());
        Assert.Equal(1_000, body.GetProperty("byPhase").GetProperty("audit").GetProperty("durationMs").GetInt64());

        var topSteps = body.GetProperty("topSteps").EnumerateArray().ToList();
        Assert.DoesNotContain(topSteps, step => step.GetProperty("step").GetString() == "dotnet.build");
        Assert.DoesNotContain(topSteps, step => step.GetProperty("step").GetString() == "dotnet.test_run");
    }

    [Fact]
    public async Task GetTimings_InFlightRow_AppearsWithNullDuration()
    {
        var item = CreateItem();
        await _factory.Store.CreateAsync(item);

        var rec = new TimingRecord
        {
            WorkItemId = item.Id,
            Phase = "work",
            Step = "agent.exec",
            StartedAt = DateTimeOffset.UtcNow,
            MetadataJson = "{}",
        };
        await _factory.TimingStore.BeginAsync(rec);

        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/workitems/{item.Id}/timings");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var byPhase = body.GetProperty("byPhase");
        Assert.True(byPhase.TryGetProperty("work", out _), "work phase must be present");
    }

    private async Task AddCompletedTimingAsync(
        WorkItemId workItemId,
        string phase,
        string step,
        DateTimeOffset startedAt,
        long durationMs)
    {
        var rowId = Guid.NewGuid().ToString("N");
        await _factory.TimingStore.BeginAsync(new TimingRecord
        {
            Id = rowId,
            WorkItemId = workItemId,
            Phase = phase,
            Step = step,
            StartedAt = startedAt,
            MetadataJson = "{}",
        });
        await _factory.TimingStore.EndAsync(rowId, startedAt.AddMilliseconds(durationMs), durationMs);
    }

    private static WorkItem CreateItem() => new WorkItem
    {
        Id = new WorkItemId(Guid.NewGuid()),
        ProjectId = new ProjectId("test-project"),
        Title = "Test",
        Prompt = "test",
        State = WorkItemState.Done,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        WorkTimeout = TimeSpan.FromHours(1),
        MergeTimeout = TimeSpan.FromMinutes(30),
    };
}

public sealed class AggregateTimingsEndpointTests : IClassFixture<AggregateTimingsApiFactory>
{
    private readonly AggregateTimingsApiFactory _factory;

    public AggregateTimingsEndpointTests(AggregateTimingsApiFactory factory) => _factory = factory;

    [Fact]
    public async Task GetAggregate_NoData_ReturnsZeroItems()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/workitems/timings/aggregate");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("workItemCount", out _));
        Assert.True(body.TryGetProperty("stepStats", out _));
    }

    [Fact]
    public async Task GetAggregate_WithMultipleItems_ComputesCorrectPercentiles()
    {
        // 5 Done work items, each with one work/agent.exec row at a known duration.
        // Sorted: [100, 200, 300, 400, 500]
        // Median (p50): floor(0.5 * 4) = 2 → sorted[2] = 300 ms
        // P95:          floor(0.95 * 4) = floor(3.8) = 3 → sorted[3] = 400 ms
        var durations = new long[] { 500, 100, 300, 200, 400 };
        foreach (var dur in durations)
        {
            var item = CreateDoneItem();
            await _factory.Store.CreateAsync(item);

            var rowId = Guid.NewGuid().ToString("N");
            var startedAt = DateTimeOffset.UtcNow;
            await _factory.TimingStore.BeginAsync(new TimingRecord
            {
                Id = rowId,
                WorkItemId = item.Id,
                Phase = "work",
                Step = "agent.exec",
                StartedAt = startedAt,
                MetadataJson = "{}",
            });
            await _factory.TimingStore.EndAsync(rowId, startedAt.AddMilliseconds(dur), dur);
        }

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/workitems/timings/aggregate");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(5, body.GetProperty("workItemCount").GetInt32());

        var execStat = body.GetProperty("stepStats")
            .EnumerateArray()
            .Single(s => s.GetProperty("step").GetString() == "agent.exec");

        Assert.Equal(300L, execStat.GetProperty("medianMs").GetInt64());
        Assert.Equal(400L, execStat.GetProperty("p95Ms").GetInt64());
    }

    private static WorkItem CreateDoneItem() => new()
    {
        Id = new WorkItemId(Guid.NewGuid()),
        ProjectId = new ProjectId("test-project"),
        Title = "Aggregate test",
        Prompt = "test",
        State = WorkItemState.Done,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        WorkTimeout = TimeSpan.FromHours(1),
        MergeTimeout = TimeSpan.FromMinutes(30),
    };
}

/// <summary>
/// Separate factory for AggregateTimingsEndpointTests so its database is
/// isolated from WorkItemTimingsEndpointTests.
/// </summary>
public sealed class AggregateTimingsApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-timings-aggtest-{Guid.NewGuid():N}.db");

    public SqliteWorkItemStore Store { get; }
    public SqliteTimingStore TimingStore { get; }

    public AggregateTimingsApiFactory()
    {
        Store = new SqliteWorkItemStore(_dbPath);
        TimingStore = new SqliteTimingStore(_dbPath);
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
            services.RemoveAll<ITimingStore>();
            services.AddSingleton<ITimingStore>(TimingStore);
            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository());
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            TimingStore.Dispose();
            Store.Dispose();
            try { File.Delete(_dbPath); } catch { /* best-effort */ }
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Test host that exposes both a work item store and a timing store backed by
/// the same temp database.
/// </summary>
public sealed class TimingsApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-timings-httptest-{Guid.NewGuid():N}.db");

    public SqliteWorkItemStore Store { get; }
    public SqliteTimingStore TimingStore { get; }

    public TimingsApiFactory()
    {
        Store = new SqliteWorkItemStore(_dbPath);
        TimingStore = new SqliteTimingStore(_dbPath);
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
            services.RemoveAll<ITimingStore>();
            services.AddSingleton<ITimingStore>(TimingStore);
            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository());
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            TimingStore.Dispose();
            Store.Dispose();
            try { File.Delete(_dbPath); } catch { /* best-effort */ }
        }
        base.Dispose(disposing);
    }
}
