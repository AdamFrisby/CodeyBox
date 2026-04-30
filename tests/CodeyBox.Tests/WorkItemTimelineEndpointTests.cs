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

/// <summary>
/// HTTP-level tests for GET /workitems/{id}/timeline.
/// Uses a synthesized audit log directory populated before each request.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class WorkItemTimelineEndpointTests : IDisposable
{
    private readonly TimelineApiFactory _factory = new();
    private readonly HttpClient _client;

    public WorkItemTimelineEndpointTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Timeline_NotFound_WhenWorkItemDoesNotExist()
    {
        var resp = await _client.GetAsync("/workitems/aabbccdd-0000-0000-0000-000000000099/timeline");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Timeline_InvalidId_Returns400()
    {
        var resp = await _client.GetAsync("/workitems/not-a-guid/timeline");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Timeline_ReturnsSortedEntries_FromAuditFile()
    {
        var id = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        await _factory.Store.CreateAsync(MakeItem(id, t0), CancellationToken.None);

        // Write events intentionally out of order; reader must sort them.
        await File.AppendAllLinesAsync(_factory.TodayAuditFile, [
            MakeClef(id, "work_item.transitioned", t0.AddMinutes(3), new { State = "Working" }),
            MakeClef(id, "work_item.created",       t0,              new { Title = "Test" }),
        ]);

        var body = await GetTimelineAsync(id);

        Assert.Equal(2, body.Entries.Count);
        Assert.True(body.Entries[0].OccurredAt <= body.Entries[1].OccurredAt);
        Assert.Equal("state_transition", body.Entries[0].Kind);
        Assert.Contains("Queued", body.Entries[0].Summary);
        Assert.Contains("Working", body.Entries[1].Summary);
    }

    [Fact]
    public async Task Timeline_EmptyWhenNoEntriesInLog()
    {
        var id = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _factory.Store.CreateAsync(MakeItem(id, t0), CancellationToken.None);

        // No CLEF events written for this work item
        var body = await GetTimelineAsync(id);

        Assert.Equal(id.ToString(), body.WorkItemId);
        Assert.Empty(body.Entries);
    }

    [Fact]
    public async Task Timeline_FilterByKind_ReturnsOnlyMatchingKind()
    {
        var id = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        await _factory.Store.CreateAsync(MakeItem(id, t0), CancellationToken.None);

        await File.AppendAllLinesAsync(_factory.TodayAuditFile, [
            MakeClef(id, "work_item.transitioned", t0, new { State = "Working" }),
            MakeClef(id, "auditor.run", t0.AddMinutes(1), new { AuditorName = "lint", WorstSeverity = "None", DurationMs = 1000L }),
            MakeClef(id, "audit.iteration_complete", t0.AddMinutes(2), new { Iteration = 1, MaxIterations = 3, BlockingCount = 0, NonBlockingCount = 2 }),
        ]);

        var resp = await _client.GetAsync($"/workitems/{id}/timeline?kind=auditor_run");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<TimelineResponse>();

        Assert.NotNull(body);
        Assert.Single(body.Entries);
        Assert.Equal("auditor_run", body.Entries[0].Kind);
    }

    [Fact]
    public async Task Timeline_FilterBySince_ExcludesOlderEntries()
    {
        var id = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-20);
        await _factory.Store.CreateAsync(MakeItem(id, t0), CancellationToken.None);

        var cutoff = t0.AddMinutes(5);
        await File.AppendAllLinesAsync(_factory.TodayAuditFile, [
            MakeClef(id, "work_item.transitioned", t0, new { State = "Working" }),
            MakeClef(id, "agent.started", t0.AddMinutes(8), new { Agent = "claude", Phase = "work", Sandbox = "vm" }),
        ]);

        var resp = await _client.GetAsync(
            $"/workitems/{id}/timeline?since={Uri.EscapeDataString(cutoff.ToString("O"))}");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<TimelineResponse>();

        Assert.NotNull(body);
        Assert.Single(body.Entries);
        Assert.Equal("agent_started", body.Entries[0].Kind);
    }

    [Fact]
    public async Task Timeline_FilterByIteration_ReturnsOnlyThatIteration()
    {
        var id = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-20);
        await _factory.Store.CreateAsync(MakeItem(id, t0), CancellationToken.None);

        await File.AppendAllLinesAsync(_factory.TodayAuditFile, [
            MakeClef(id, "auditor.run", t0,              new { AuditorName = "a", WorstSeverity = "None", DurationMs = 100L }),
            MakeClef(id, "audit.iteration_complete", t0.AddMinutes(1), new { Iteration = 1, MaxIterations = 3, BlockingCount = 0, NonBlockingCount = 0 }),
            MakeClef(id, "auditor.run", t0.AddMinutes(2), new { AuditorName = "b", WorstSeverity = "None", DurationMs = 100L }),
            MakeClef(id, "audit.iteration_complete", t0.AddMinutes(3), new { Iteration = 2, MaxIterations = 3, BlockingCount = 0, NonBlockingCount = 0 }),
        ]);

        var resp = await _client.GetAsync($"/workitems/{id}/timeline?iteration=2");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<TimelineResponse>();

        Assert.NotNull(body);
        Assert.Equal(2, body.Entries.Count);
        Assert.All(body.Entries, e => Assert.Contains("2", e.Summary));
    }

    [Fact]
    public async Task Timeline_AgentFinished_IncludesStdoutTailInDetails()
    {
        var id = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        await _factory.Store.CreateAsync(MakeItem(id, t0), CancellationToken.None);

        await File.AppendAllLinesAsync(_factory.TodayAuditFile, [
            MakeClef(id, "agent.finished", t0, new
            {
                Agent = "claude", Sandbox = "vm", Success = true,
                DurationMs = 60_000L, StdoutTail = "Task complete.", StderrTail = ""
            }),
        ]);

        var body = await GetTimelineAsync(id);

        var entry = Assert.Single(body.Entries);
        Assert.Equal("agent_finished", entry.Kind);
        Assert.Contains("succeeded", entry.Summary);
        Assert.Contains("stdoutTail", entry.Details.ToString());
    }

    [Fact]
    public async Task Timeline_AuditorRunSummary_IncludesIterationNumber()
    {
        var id = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        await _factory.Store.CreateAsync(MakeItem(id, t0), CancellationToken.None);

        await File.AppendAllLinesAsync(_factory.TodayAuditFile, [
            MakeClef(id, "auditor.run", t0, new { AuditorName = "csharp:format-check", WorstSeverity = "None", DurationMs = 34_045L }),
            MakeClef(id, "audit.iteration_complete", t0.AddMinutes(1), new { Iteration = 1, MaxIterations = 10, BlockingCount = 7, NonBlockingCount = 15 }),
        ]);

        var body = await GetTimelineAsync(id);

        var auditorEntry = body.Entries.First(e => e.Kind == "auditor_run");
        Assert.Contains("csharp:format-check", auditorEntry.Summary);
        Assert.Contains("iter 1", auditorEntry.Summary);

        var iterEntry = body.Entries.First(e => e.Kind == "iteration_complete");
        Assert.Contains("7 blocking", iterEntry.Summary);
        Assert.Contains("15 non-blocking", iterEntry.Summary);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<TimelineResponse> GetTimelineAsync(WorkItemId id)
    {
        var resp = await _client.GetAsync($"/workitems/{id}/timeline");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<TimelineResponse>();
        return body!;
    }

    private static WorkItem MakeItem(WorkItemId id, DateTimeOffset createdAt) => new()
    {
        Id = id,
        ProjectId = new ProjectId("proj"),
        Title = "Test",
        Prompt = "p",
        State = WorkItemState.Working,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
        QueuePosition = 1,
    };

    private static string MakeClef(WorkItemId id, string eventName, DateTimeOffset time, object extra)
    {
        var extraJson = JsonSerializer.Serialize(extra);
        using var extraDoc = JsonDocument.Parse(extraJson);
        var result = new Dictionary<string, JsonElement>
        {
            ["@t"]          = JsonSerializer.SerializeToElement(time.ToString("O")),
            ["EventName"]   = JsonSerializer.SerializeToElement(eventName),
            ["WorkItemId"]  = JsonSerializer.SerializeToElement(id.ToString()),
            ["Audit"]       = JsonSerializer.SerializeToElement(true),
        };
        foreach (var prop in extraDoc.RootElement.EnumerateObject())
            result[prop.Name] = prop.Value.Clone();
        return JsonSerializer.Serialize(result);
    }

    private sealed record TimelineResponse(string WorkItemId, List<EntryRecord> Entries);
    private sealed record EntryRecord(DateTimeOffset OccurredAt, string Kind, string Summary, JsonElement Details);
}

/// <summary>
/// WebApplicationFactory variant that points the AuditLogTimelineReader at an
/// isolated temp directory so tests can write synthetic CLEF events.
/// </summary>
internal sealed class TimelineApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-tltest-{Guid.NewGuid():N}.db");

    public string AuditDir { get; } = Path.Combine(
        Path.GetTempPath(), $"codeybox-tlogs-{Guid.NewGuid():N}");

    public string TodayAuditFile => Path.Combine(AuditDir, $"audit-{DateTime.UtcNow:yyyyMMdd}.json");

    public SqliteWorkItemStore Store { get; }

    public TimelineApiFactory()
    {
        Directory.CreateDirectory(AuditDir);
        Store = new SqliteWorkItemStore(_dbPath);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitRootDirectory"] = Path.Combine(Path.GetTempPath(), $"test-git-{Guid.NewGuid():N}"),
                ["CodeyBox:AuditLog:Path"] = Path.Combine(AuditDir, "log-.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(AuditDir, "audit-.json"),
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
            Store.Dispose();
            try { File.Delete(_dbPath); } catch { /* best-effort */ }
            try { Directory.Delete(AuditDir, recursive: true); } catch { /* best-effort */ }
        }
        base.Dispose(disposing);
    }
}
