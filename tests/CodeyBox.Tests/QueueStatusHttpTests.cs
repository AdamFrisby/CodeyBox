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
    public async Task PauseAndResumeAgent_UpdatesPausedAgentsList()
    {
        var pause = await _client.PostAsJsonAsync(
            "/agents/claude/pause",
            new { reason = "reserve quota", durationSeconds = 3600 });
        Assert.Equal(HttpStatusCode.OK, pause.StatusCode);

        var paused = await _client.GetFromJsonAsync<List<AgentPauseResponse>>("/agents/paused");
        var claude = Assert.Single(paused!);
        Assert.Equal("claude", claude.Agent);
        Assert.True(claude.Paused);
        Assert.Equal("reserve quota", claude.PausedReason);
        Assert.NotNull(claude.ExpiresAt);

        var resume = await _client.PostAsJsonAsync("/agents/claude/resume", new { });
        Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
        paused = await _client.GetFromJsonAsync<List<AgentPauseResponse>>("/agents/paused");
        Assert.Empty(paused!);
    }

    [Fact]
    public async Task AgentPauseEndpoints_AcceptDurationStringAndFutureExpiresAt()
    {
        var durationPause = await _client.PostAsJsonAsync(
            "/agents/claude/pause",
            new { reason = "outage", duration = "6h" });
        Assert.Equal(HttpStatusCode.OK, durationPause.StatusCode);

        var paused = await _client.GetFromJsonAsync<List<AgentPauseResponse>>("/agents/paused");
        var claude = Assert.Single(paused!);
        Assert.Equal("claude", claude.Agent);
        Assert.Equal("outage", claude.PausedReason);
        Assert.NotNull(claude.ExpiresAt);

        var resume = await _client.PostAsJsonAsync("/agents/claude/resume", new { reason = "switch to absolute" });
        Assert.Equal(HttpStatusCode.OK, resume.StatusCode);

        var future = DateTimeOffset.UtcNow.AddHours(2);
        var expiresAtPause = await _client.PostAsJsonAsync(
            "/agents/claude/pause",
            new { reason = "maintenance", expiresAt = future });
        Assert.Equal(HttpStatusCode.OK, expiresAtPause.StatusCode);

        paused = await _client.GetFromJsonAsync<List<AgentPauseResponse>>("/agents/paused");
        claude = Assert.Single(paused!);
        Assert.Equal("maintenance", claude.PausedReason);
        Assert.NotNull(claude.ExpiresAt);
        Assert.True(claude.ExpiresAt >= future.AddSeconds(-5));
    }

    [Fact]
    public async Task AgentPauseEndpoints_RejectUnknownAgentAndInvalidBodies()
    {
        var unknown = await _client.PostAsJsonAsync("/agents/not-real/pause", new { reason = "test" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var unknownResume = await _client.PostAsJsonAsync("/agents/not-real/resume", new { });
        Assert.Equal(HttpStatusCode.NotFound, unknownResume.StatusCode);

        var missingReason = await _client.PostAsJsonAsync("/agents/claude/pause", new { });
        Assert.Equal(HttpStatusCode.BadRequest, missingReason.StatusCode);

        var controlReason = await _client.PostAsJsonAsync("/agents/claude/pause", new { reason = "bad\nreason" });
        Assert.Equal(HttpStatusCode.BadRequest, controlReason.StatusCode);

        var longReason = new string('x', 501);
        var tooLongReason = await _client.PostAsJsonAsync("/agents/claude/pause", new { reason = longReason });
        Assert.Equal(HttpStatusCode.BadRequest, tooLongReason.StatusCode);

        var conflictingExpiry = await _client.PostAsJsonAsync("/agents/claude/pause", new
        {
            reason = "test",
            durationSeconds = 60,
            expiresAt = DateTimeOffset.UtcNow.AddHours(1),
        });
        Assert.Equal(HttpStatusCode.BadRequest, conflictingExpiry.StatusCode);

        var nonPositiveDuration = await _client.PostAsJsonAsync("/agents/claude/pause", new
        {
            reason = "test",
            durationSeconds = 0,
        });
        Assert.Equal(HttpStatusCode.BadRequest, nonPositiveDuration.StatusCode);

        var badDuration = await _client.PostAsJsonAsync("/agents/claude/pause", new
        {
            reason = "test",
            duration = "12w",
        });
        Assert.Equal(HttpStatusCode.BadRequest, badDuration.StatusCode);

        var overflowDuration = await _client.PostAsJsonAsync("/agents/claude/pause", new
        {
            reason = "test",
            duration = "1e100d",
        });
        Assert.Equal(HttpStatusCode.BadRequest, overflowDuration.StatusCode);

        var pastExpiry = await _client.PostAsJsonAsync("/agents/claude/pause", new
        {
            reason = "test",
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        });
        Assert.Equal(HttpStatusCode.BadRequest, pastExpiry.StatusCode);

        var badResumeReason = await _client.PostAsJsonAsync("/agents/claude/resume", new { reason = "bad\nreason" });
        Assert.Equal(HttpStatusCode.BadRequest, badResumeReason.StatusCode);

        var longResumeReason = await _client.PostAsJsonAsync("/agents/claude/resume", new { reason = longReason });
        Assert.Equal(HttpStatusCode.BadRequest, longResumeReason.StatusCode);
    }

    [Fact]
    public async Task QuotaEndpoint_ReportsPausedAgentAsPausedAndNotAllowed()
    {
        var pause = await _client.PostAsJsonAsync("/agents/claude/pause", new { reason = "reserve quota" });
        Assert.Equal(HttpStatusCode.OK, pause.StatusCode);

        var quota = await _client.GetAsync("/quota");
        Assert.Equal(HttpStatusCode.OK, quota.StatusCode);
        using var doc = JsonDocument.Parse(await quota.Content.ReadAsStringAsync());
        var claude = doc.RootElement.GetProperty("probes").EnumerateArray()
            .First(e => e.GetProperty("agent").GetString() == "claude");
        var pausedClaude = doc.RootElement.GetProperty("pausedAgents").EnumerateArray()
            .First(e => e.GetProperty("agent").GetString() == "claude");

        Assert.True(claude.GetProperty("paused").GetBoolean());
        Assert.True(pausedClaude.GetProperty("paused").GetBoolean());
        Assert.Equal("paused", claude.GetProperty("dispatchStatus").GetString());
        Assert.Contains("paused by operator", claude.GetProperty("dispatchReason").GetString());
        Assert.False(claude.GetProperty("wouldAllow").GetBoolean());
        Assert.False(claude.GetProperty("defaultModelWouldAllow").GetBoolean());
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
        // The test factory seeds one project with id "proj" (no budget caps configured).
        var resp = await _client.GetAsync("/projects/proj/budget/usage");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<BudgetUsageResponse>();
        Assert.NotNull(body);
        Assert.Equal(0, body.LastHour);
        Assert.Equal(0, body.Last24h);
        Assert.Equal(0, body.CurrentlyInFlight);
        Assert.NotNull(body.Limits);
        Assert.Equal(0, body.Limits.PerHour);
        Assert.Equal(0, body.Limits.PerDay);
        Assert.Equal(0, body.Limits.Concurrent);
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

    private sealed record AgentPauseResponse(
        string Agent,
        bool Paused,
        DateTimeOffset? PausedAt,
        string? PausedReason,
        string? PausedBy,
        DateTimeOffset? ExpiresAt);

    private sealed record BudgetLimitsResponse(int PerHour, int PerDay, int Concurrent);

    private sealed record BudgetUsageResponse(
        int LastHour,
        int Last24h,
        int CurrentlyInFlight,
        BudgetLimitsResponse? Limits);
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
            services.RemoveAll<IAgentQuotaProbe>();
            services.AddSingleton<IAgentQuotaProbe>(new FakeProbe(AgentKind.Claude, 100));
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
