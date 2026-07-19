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

/// <summary>
/// Exercises GET /workitems/failure-events through the real HTTP host and a
/// real SQLite-backed <see cref="IFailureEventStore"/> — the read half of the
/// failure-history feature (deliverable #5). Mirrors
/// <see cref="WorkItemTimingsEndpointTests"/>.
/// </summary>
public sealed class WorkItemFailureEventsEndpointTests : IClassFixture<FailureEventsApiFactory>
{
    private readonly FailureEventsApiFactory _factory;

    public WorkItemFailureEventsEndpointTests(FailureEventsApiFactory factory) => _factory = factory;

    [Fact]
    public async Task GetFailureEvents_NoData_ReturnsEmptyList()
    {
        var client = _factory.CreateClient();
        // Filter on a kind no seeded row uses so the shared store stays isolated.
        var resp = await client.GetAsync("/workitems/failure-events?kind=__none__");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("count").GetInt32());
        Assert.Empty(body.GetProperty("events").EnumerateArray());
    }

    [Fact]
    public async Task GetFailureEvents_AfterAppend_ReturnsRowWithAllFields()
    {
        var workItemId = new WorkItemId(Guid.NewGuid());
        await _factory.Store.CreateAsync(CreateItem(workItemId));

        var record = new FailureEventRecord
        {
            WorkItemId = workItemId,
            Agent = "claude",
            Phase = "Failed",
            Iteration = 3,
            FailureKind = "endpoint-roundtrip-kind",
            ErrorMessage = "boom",
            SandboxName = "vm-42",
            Provider = "incus",
            OccurredAt = DateTimeOffset.UtcNow,
        };
        await _factory.FailureEventStore.AppendAsync(record);

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/workitems/failure-events?kind=endpoint-roundtrip-kind");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("count").GetInt32());

        var evt = body.GetProperty("events").EnumerateArray().Single();
        Assert.Equal(record.Id, evt.GetProperty("id").GetString());
        Assert.Equal(workItemId.ToString(), evt.GetProperty("workItemId").GetString());
        Assert.Equal("claude", evt.GetProperty("agent").GetString());
        Assert.Equal("Failed", evt.GetProperty("phase").GetString());
        Assert.Equal(3, evt.GetProperty("iteration").GetInt32());
        Assert.Equal("endpoint-roundtrip-kind", evt.GetProperty("failureKind").GetString());
        Assert.Equal("boom", evt.GetProperty("errorMessage").GetString());
        Assert.Equal("vm-42", evt.GetProperty("sandboxName").GetString());
        Assert.Equal("incus", evt.GetProperty("provider").GetString());
    }

    [Fact]
    public async Task GetFailureEvents_KindFilter_ReturnsOnlyMatching()
    {
        var workItemId = new WorkItemId(Guid.NewGuid());
        await _factory.Store.CreateAsync(CreateItem(workItemId));

        var at = DateTimeOffset.UtcNow;
        await _factory.FailureEventStore.AppendAsync(new FailureEventRecord
        {
            WorkItemId = workItemId,
            Phase = "Failed",
            FailureKind = "filter-kind-A",
            OccurredAt = at,
        });
        await _factory.FailureEventStore.AppendAsync(new FailureEventRecord
        {
            WorkItemId = workItemId,
            Phase = "Failed",
            FailureKind = "filter-kind-B",
            OccurredAt = at,
        });

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/workitems/failure-events?kind=filter-kind-A");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var kinds = body.GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty("failureKind").GetString())
            .ToList();
        Assert.All(kinds, k => Assert.Equal("filter-kind-A", k));
        Assert.NotEmpty(kinds);
    }

    [Fact]
    public async Task GetFailureEvents_SinceFilter_ExcludesOlderRows()
    {
        var workItemId = new WorkItemId(Guid.NewGuid());
        await _factory.Store.CreateAsync(CreateItem(workItemId));

        var baseTime = DateTimeOffset.UtcNow;
        await _factory.FailureEventStore.AppendAsync(new FailureEventRecord
        {
            WorkItemId = workItemId,
            Phase = "Failed",
            FailureKind = "since-old",
            OccurredAt = baseTime.AddHours(-2),
        });
        await _factory.FailureEventStore.AppendAsync(new FailureEventRecord
        {
            WorkItemId = workItemId,
            Phase = "Failed",
            FailureKind = "since-new",
            OccurredAt = baseTime,
        });

        var cutoff = Uri.EscapeDataString(baseTime.AddHours(-1).ToUniversalTime().ToString("O"));
        var client = _factory.CreateClient();
        var resp = await client.GetAsync(
            $"/workitems/failure-events?since={cutoff}&kind=since-old");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task GetFailureEvents_InvalidSince_Returns400()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/workitems/failure-events?since=not-a-date");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private static WorkItem CreateItem(WorkItemId id) => new()
    {
        Id = id,
        ProjectId = new ProjectId("test-project"),
        Title = "Test",
        Prompt = "test",
        State = WorkItemState.Failed,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        WorkTimeout = TimeSpan.FromHours(1),
        MergeTimeout = TimeSpan.FromMinutes(30),
    };
}

/// <summary>
/// Test host exposing a work item store and a failure-event store backed by the
/// same temp database, mirroring <see cref="TimingsApiFactory"/>.
/// </summary>
public sealed class FailureEventsApiFactory : CodeyBoxWebApplicationFactory
{
    private readonly string _dbPath;

    public SqliteWorkItemStore Store { get; }
    public SqliteFailureEventStore FailureEventStore { get; }

    public FailureEventsApiFactory()
    {
        _dbPath = TempDatabasePath("failureevents-httptest");
        Store = new SqliteWorkItemStore(_dbPath);
        FailureEventStore = new SqliteFailureEventStore(_dbPath);
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
            services.RemoveAll<IFailureEventStore>();
            services.AddSingleton<IFailureEventStore>(FailureEventStore);
            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository());
        });
    }

    protected override void Dispose(bool disposing)
        => DisposeHostThenDeleteSqliteDatabase(
            disposing,
            _dbPath,
            FailureEventStore.Dispose,
            Store.Dispose);
}
