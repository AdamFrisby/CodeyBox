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
/// HTTP-level tests for GET /workitems/{id}/audit-progress and
/// GET /workitems/{id}/audit-progress/{progressId}, including list-view description truncation
/// (cap configured to 16 chars in the factory) and full detail retrieval.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class AuditProgressEndpointTests : IDisposable
{
    private readonly AuditProgressApiFactory _factory = new();
    private readonly HttpClient _client;

    public AuditProgressEndpointTests() => _client = _factory.CreateClient();

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

    private Task RecordAsync(WorkItemId id, DateTimeOffset attempt, int iteration, string description)
        => ((IAuditProgressStore)_factory.WorkItemStore).RecordAuditProgressAsync(
            id,
            attempt,
            new AuditProgressRecord(
                Iteration: iteration,
                MaxIterations: 5,
                BlockingFindings: 1,
                NonBlockingFindings: 0,
                BlockingFindingIds: ["b1"],
                BlockingFindingsDetails:
                [
                    new AuditProgressFinding("sec", AuditSeverity.Error, "T", description, "src/A.cs:1"),
                ],
                Findings:
                [
                    new AuditProgressFinding("sec", AuditSeverity.Error, "T", description, "src/A.cs:1"),
                ],
                WorkBranchTip: "tip"),
            DateTimeOffset.UtcNow);

    [Fact]
    public async Task List_UnknownWorkItem_Returns404()
    {
        var resp = await _client.GetAsync("/workitems/aabbccdd-0000-0000-0000-000000000099/audit-progress");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task List_InvalidId_Returns400()
    {
        var resp = await _client.GetAsync("/workitems/not-a-guid/audit-progress");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task List_TruncatesDescription_And_Detail_ReturnsFull()
    {
        var id = WorkItemId.New();
        await _factory.WorkItemStore.CreateAsync(MakeItem(id));
        var full = new string('x', 100);   // longer than the 16-char cap set in the factory
        await RecordAsync(id, DateTimeOffset.UtcNow.AddMinutes(-3), iteration: 1, full);

        // LIST: description truncated to the cap, flagged, full length reported.
        var listResp = await _client.GetAsync($"/workitems/{id}/audit-progress");
        listResp.EnsureSuccessStatusCode();
        var listDoc = await listResp.Content.ReadFromJsonAsync<JsonElement>();
        var rows = listDoc.GetProperty("progress");
        Assert.Equal(1, rows.GetArrayLength());

        var row = rows[0];
        Assert.True(row.GetProperty("truncated").GetBoolean());
        var rowId = row.GetProperty("id").GetString();
        Assert.False(string.IsNullOrEmpty(rowId));

        var finding = row.GetProperty("findings")[0];
        Assert.Equal(16, finding.GetProperty("description").GetString()!.Length);
        Assert.True(finding.GetProperty("descriptionTruncated").GetBoolean());
        Assert.Equal(100, finding.GetProperty("descriptionLength").GetInt32());

        // DETAIL: full description, not truncated.
        var detailResp = await _client.GetAsync($"/workitems/{id}/audit-progress/{rowId}");
        detailResp.EnsureSuccessStatusCode();
        var detailDoc = await detailResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(detailDoc.GetProperty("truncated").GetBoolean());
        var detailFinding = detailDoc.GetProperty("findings")[0];
        Assert.Equal(100, detailFinding.GetProperty("description").GetString()!.Length);
        Assert.False(detailFinding.GetProperty("descriptionTruncated").GetBoolean());
    }

    [Fact]
    public async Task Detail_UnknownId_Returns404()
    {
        var id = WorkItemId.New();
        await _factory.WorkItemStore.CreateAsync(MakeItem(id));
        var resp = await _client.GetAsync($"/workitems/{id}/audit-progress/deadbeef");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}

internal sealed class AuditProgressApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-ap-api-{Guid.NewGuid():N}.db");

    public SqliteWorkItemStore WorkItemStore { get; }

    public AuditProgressApiFactory() => WorkItemStore = new SqliteWorkItemStore(_dbPath);

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
                ["CodeyBox:AuditProgressApi:ListFindingDescriptionMaxChars"] = "16",
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IWorkItemStore>();
            services.AddSingleton<IWorkItemStore>(WorkItemStore);
            services.RemoveAll<IAuditProgressStore>();
            services.AddSingleton<IAuditProgressStore>(WorkItemStore);
            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository());
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            WorkItemStore.Dispose();
            try { File.Delete(_dbPath); } catch { }
        }
        base.Dispose(disposing);
    }
}
