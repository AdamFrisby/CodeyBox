using System.Net;
using System.Net.Http.Json;
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
/// HTTP-level tests for /queue/pause, /queue/resume, /queue/status, and
/// /projects/{id}/budget/usage. Uses the auth-disabled WAF pattern matching
/// the other HTTP test classes in this project.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class QueueStatusHttpTests : IDisposable
{
    private readonly QueueApiFactory _factory = new();
    private readonly HttpClient _client;

    public QueueStatusHttpTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task GetQueueStatus_ReturnsRunning_ByDefault()
    {
        var resp = await _client.GetAsync("/queue/status");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<QueueStatusResponse>();
        Assert.Equal("Running", body!.State);
        Assert.Null(body.PausedAt);
        Assert.Null(body.PausedReason);
    }

    [Fact]
    public async Task PauseQueue_Returns200_AndStateIsPaused()
    {
        var resp = await _client.PostAsJsonAsync("/queue/pause", new { reason = "test pause" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var statusResp = await _client.GetAsync("/queue/status");
        var body = await statusResp.Content.ReadFromJsonAsync<QueueStatusResponse>();
        Assert.Equal("Paused", body!.State);
        Assert.Equal("test pause", body.PausedReason);
        Assert.NotNull(body.PausedAt);
    }

    [Fact]
    public async Task ResumeQueue_Returns200_AndStateIsRunning()
    {
        await _client.PostAsJsonAsync("/queue/pause", new { reason = "before resume" });
        var resp = await _client.PostAsJsonAsync("/queue/resume", new { });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var statusResp = await _client.GetAsync("/queue/status");
        var body = await statusResp.Content.ReadFromJsonAsync<QueueStatusResponse>();
        Assert.Equal("Running", body!.State);
    }

    [Fact]
    public async Task PauseQueue_EmptyReason_Returns400()
    {
        var resp = await _client.PostAsJsonAsync("/queue/pause", new { reason = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PauseQueue_MissingReason_Returns400()
    {
        var resp = await _client.PostAsJsonAsync("/queue/pause", new { });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetBudgetUsage_KnownProject_Returns200()
    {
        // The test factory seeds one project with id "proj".
        var resp = await _client.GetAsync("/projects/proj/budget/usage");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<BudgetUsageResponse>();
        Assert.NotNull(body);
        Assert.Equal(0, body.LastHour);
        Assert.Equal(0, body.Last24h);
        Assert.Equal(0, body.CurrentlyInFlight);
    }

    [Fact]
    public async Task GetBudgetUsage_UnknownProject_Returns404()
    {
        var resp = await _client.GetAsync("/projects/no-such-project/budget/usage");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── Test-local record shapes ───────────────────────────────────────────────

    private sealed record QueueStatusResponse(
        string State,
        DateTimeOffset? PausedAt,
        string? PausedReason);

    private sealed record BudgetUsageResponse(
        int LastHour,
        int Last24h,
        int CurrentlyInFlight);
}

/// <summary>
/// Variant of WorkItemApiFactory that also replaces IQueueController with
/// a real SqliteQueueController backed by the same temp DB.
/// </summary>
internal sealed class QueueApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-queuehttp-{Guid.NewGuid():N}.db");

    public SqliteWorkItemStore Store { get; }
    public SqliteQueueController QueueController { get; }

    public QueueApiFactory()
    {
        Store = new SqliteWorkItemStore(_dbPath);
        QueueController = new SqliteQueueController(_dbPath, NullLogger<SqliteQueueController>.Instance);
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
            services.RemoveAll<IQueueController>();
            services.AddSingleton<IQueueController>(QueueController);
            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository(
                new Project
                {
                    Id = new ProjectId("proj"),
                    DisplayName = "Test Project",
                    RepositoryUrl = "https://github.com/test/repo",
                }));
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            QueueController.Dispose();
            Store.Dispose();
            try { File.Delete(_dbPath); } catch { /* best-effort */ }
        }
        base.Dispose(disposing);
    }
}
