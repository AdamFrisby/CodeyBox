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
/// HTTP-level tests for GET /workitems/{id}/audit-reports and
/// GET /workitems/{id}/audit-reports/{iteration}/{auditor}/raw.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class AuditReportsEndpointTests : IDisposable
{
    private readonly AuditReportApiFactory _factory = new();
    private readonly HttpClient _client;

    public AuditReportsEndpointTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static WorkItem MakeItem(WorkItemId id) => new()
    {
        Id = id,
        ProjectId = new ProjectId("p"),
        Title = "t",
        Prompt = "pr",
        Agent = AgentKind.Claude,
    };

    private static AuditReport MakeReport(
        string workItemId, int iteration = 1,
        string auditorName = "Lint",
        string auditorKind = "diff-pattern",
        string? rawOutput = null,
        IReadOnlyList<AuditReportFinding>? findings = null) => new()
        {
            Id = Guid.NewGuid().ToString(),
            WorkItemId = workItemId,
            Iteration = iteration,
            AuditorName = auditorName,
            AuditorKind = auditorKind,
            WorstSeverity = findings?.Count > 0 ? "Error" : "none",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            EndedAt = DateTimeOffset.UtcNow,
            DurationMs = 150,
            Findings = findings ?? [],
            RawOutput = rawOutput,
        };

    [Fact]
    public async Task GetAuditReports_NotFound_WhenWorkItemDoesNotExist()
    {
        var resp = await _client.GetAsync("/workitems/aabbccdd-0000-0000-0000-000000000099/audit-reports");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetAuditReports_InvalidId_Returns400()
    {
        var resp = await _client.GetAsync("/workitems/not-a-guid/audit-reports");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetAuditReports_ReturnsEmptyIterations_WhenNoReports()
    {
        var id = WorkItemId.New();
        await _factory.WorkItemStore.CreateAsync(MakeItem(id));

        var resp = await _client.GetAsync($"/workitems/{id}/audit-reports");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var iterations = doc.GetProperty("iterations");
        Assert.Equal(JsonValueKind.Array, iterations.ValueKind);
        Assert.Equal(0, iterations.GetArrayLength());
    }

    [Fact]
    public async Task GetAuditReports_ReturnsSingleIteration_WithAuditor()
    {
        var id = WorkItemId.New();
        await _factory.WorkItemStore.CreateAsync(MakeItem(id));
        await _factory.AuditReportStore.CreateAsync(MakeReport(id.ToString(), iteration: 1, auditorName: "Lint"));

        var resp = await _client.GetAsync($"/workitems/{id}/audit-reports");
        resp.EnsureSuccessStatusCode();

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var iterations = doc.GetProperty("iterations");
        Assert.Equal(1, iterations.GetArrayLength());

        var iter = iterations[0];
        Assert.Equal(1, iter.GetProperty("iteration").GetInt32());
        var auditors = iter.GetProperty("auditors");
        Assert.Equal(1, auditors.GetArrayLength());
        Assert.Equal("Lint", auditors[0].GetProperty("name").GetString());
        Assert.Equal("diff-pattern", auditors[0].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task GetAuditReports_GroupsMultipleAuditors_SameIteration()
    {
        var id = WorkItemId.New();
        await _factory.WorkItemStore.CreateAsync(MakeItem(id));
        await _factory.AuditReportStore.CreateAsync(MakeReport(id.ToString(), auditorName: "Lint"));
        await _factory.AuditReportStore.CreateAsync(MakeReport(id.ToString(), auditorName: "Security"));

        var resp = await _client.GetAsync($"/workitems/{id}/audit-reports");
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();

        var iterations = doc.GetProperty("iterations");
        Assert.Equal(1, iterations.GetArrayLength());
        var auditors = iterations[0].GetProperty("auditors");
        Assert.Equal(2, auditors.GetArrayLength());
    }

    [Fact]
    public async Task GetAuditReports_MultipleIterations_OrderedByIteration()
    {
        var id = WorkItemId.New();
        await _factory.WorkItemStore.CreateAsync(MakeItem(id));
        await _factory.AuditReportStore.CreateAsync(MakeReport(id.ToString(), iteration: 3, auditorName: "Lint"));
        await _factory.AuditReportStore.CreateAsync(MakeReport(id.ToString(), iteration: 1, auditorName: "Lint"));
        await _factory.AuditReportStore.CreateAsync(MakeReport(id.ToString(), iteration: 2, auditorName: "Lint"));

        var resp = await _client.GetAsync($"/workitems/{id}/audit-reports");
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var iterations = doc.GetProperty("iterations");

        Assert.Equal(3, iterations.GetArrayLength());
        Assert.Equal(1, iterations[0].GetProperty("iteration").GetInt32());
        Assert.Equal(2, iterations[1].GetProperty("iteration").GetInt32());
        Assert.Equal(3, iterations[2].GetProperty("iteration").GetInt32());
    }

    [Fact]
    public async Task GetAuditReports_BlockingCount_CountsErrorFindings()
    {
        var id = WorkItemId.New();
        await _factory.WorkItemStore.CreateAsync(MakeItem(id));
        var findings = new List<AuditReportFinding>
        {
            new("f-aa", "Error", "Bad thing", "desc", [], []),
            new("f-bb", "Warning", "Risky thing", "desc", [], []),
            new("f-cc", "Error", "Another bad", "desc", [], []),
        };
        await _factory.AuditReportStore.CreateAsync(MakeReport(id.ToString(), findings: findings));

        var resp = await _client.GetAsync($"/workitems/{id}/audit-reports");
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var iter = doc.GetProperty("iterations")[0];

        Assert.Equal(2, iter.GetProperty("blockingCount").GetInt32());
        Assert.Equal(1, iter.GetProperty("nonBlockingCount").GetInt32());
    }

    [Fact]
    public async Task GetAuditReports_RawOutputAvailable_WhenRawOutputPresent()
    {
        var id = WorkItemId.New();
        await _factory.WorkItemStore.CreateAsync(MakeItem(id));
        await _factory.AuditReportStore.CreateAsync(
            MakeReport(id.ToString(), auditorName: "Lint", rawOutput: "some output"));

        var resp = await _client.GetAsync($"/workitems/{id}/audit-reports");
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var auditor = doc.GetProperty("iterations")[0].GetProperty("auditors")[0];

        Assert.True(auditor.GetProperty("rawOutputAvailable").GetBoolean());
    }

    [Fact]
    public async Task GetAuditReports_RawOutputAvailable_FalseWhenNoRawOutput()
    {
        var id = WorkItemId.New();
        await _factory.WorkItemStore.CreateAsync(MakeItem(id));
        await _factory.AuditReportStore.CreateAsync(
            MakeReport(id.ToString(), auditorName: "Lint", rawOutput: null));

        var resp = await _client.GetAsync($"/workitems/{id}/audit-reports");
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var auditor = doc.GetProperty("iterations")[0].GetProperty("auditors")[0];

        Assert.False(auditor.GetProperty("rawOutputAvailable").GetBoolean());
    }

    [Fact]
    public async Task GetRawOutput_ReturnsPlainText()
    {
        var id = WorkItemId.New();
        await _factory.WorkItemStore.CreateAsync(MakeItem(id));
        await _factory.AuditReportStore.CreateAsync(
            MakeReport(id.ToString(), auditorName: "Lint", rawOutput: "line 1\nline 2\n"));

        var resp = await _client.GetAsync($"/workitems/{id}/audit-reports/1/Lint/raw");
        resp.EnsureSuccessStatusCode();

        Assert.Contains("text/plain", resp.Content.Headers.ContentType?.MediaType);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Equal("line 1\nline 2\n", body);
    }

    [Fact]
    public async Task GetRawOutput_NotFound_WhenNoRawOutput()
    {
        var id = WorkItemId.New();
        await _factory.WorkItemStore.CreateAsync(MakeItem(id));
        await _factory.AuditReportStore.CreateAsync(
            MakeReport(id.ToString(), auditorName: "Lint", rawOutput: null));

        var resp = await _client.GetAsync($"/workitems/{id}/audit-reports/1/Lint/raw");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetRawOutput_NotFound_WhenWorkItemDoesNotExist()
    {
        var resp = await _client.GetAsync("/workitems/aabbccdd-0000-0000-0000-000000000099/audit-reports/1/Lint/raw");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetAuditReports_Findings_IncludedInResponse()
    {
        var id = WorkItemId.New();
        await _factory.WorkItemStore.CreateAsync(MakeItem(id));
        var findings = new List<AuditReportFinding>
        {
            new("f-aa", "Error", "Missing null check", "Details here", ["src/A.cs"], [42]),
        };
        await _factory.AuditReportStore.CreateAsync(MakeReport(id.ToString(), findings: findings));

        var resp = await _client.GetAsync($"/workitems/{id}/audit-reports");
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var fArr = doc.GetProperty("iterations")[0].GetProperty("auditors")[0].GetProperty("findings");

        Assert.Equal(1, fArr.GetArrayLength());
        var f = fArr[0];
        Assert.Equal("f-aa", f.GetProperty("id").GetString());
        Assert.Equal("Error", f.GetProperty("severity").GetString());
        Assert.Equal("Missing null check", f.GetProperty("title").GetString());
        Assert.Equal("Details here", f.GetProperty("message").GetString());
    }
}

/// <summary>
/// A WebApplicationFactory that replaces both <see cref="IWorkItemStore"/> and
/// <see cref="IAuditReportStore"/> with isolated in-memory/SQLite instances.
/// </summary>
internal sealed class AuditReportApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-audit-api-{Guid.NewGuid():N}.db");

    public SqliteWorkItemStore WorkItemStore { get; }
    public SqliteAuditReportStore AuditReportStore { get; }

    public AuditReportApiFactory()
    {
        WorkItemStore = new SqliteWorkItemStore(_dbPath);
        AuditReportStore = new SqliteAuditReportStore(_dbPath);
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
            services.AddSingleton<IWorkItemStore>(WorkItemStore);
            services.RemoveAll<IAuditReportStore>();
            services.AddSingleton<IAuditReportStore>(AuditReportStore);
            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository());
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            WorkItemStore.Dispose();
            AuditReportStore.Dispose();
            try { File.Delete(_dbPath); } catch { }
        }
        base.Dispose(disposing);
    }
}
