using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Tests;

namespace CodeyBox.Tests.Uat.AuditingAndReports;

/// <summary>
/// UAT coverage for persisted audit report API grouping used by the admin report view.
/// Plan anchor: docs/uat/00-plan.md#audit-finding-schema-and-stable-ids---represents-and-correlates-findings-across-reports
/// </summary>
public sealed class AuditReportsEndpointUatTests : IDisposable
{
    private readonly AuditReportApiFactory _factory = new();
    private readonly HttpClient _client;

    public AuditReportsEndpointUatTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task GetAuditReports_GroupsIterationsAndAuditorsWithBlockingCounts()
    {
        var item = NewItem();
        await _factory.WorkItemStore.CreateAsync(item);
        await _factory.AuditReportStore.CreateAsync(AuditingAndReportsHelpers.Report(
            item.Id.ToString(),
            iteration: 2,
            auditorName: "security:semgrep",
            findings:
            [
                new("f-error", "Error", "SQL injection", "parameterize query", ["src/Db.cs"], [18]),
                new("f-warn", "Warning", "Review note", "consider extracting helper", [], []),
            ],
            rawOutput: "semgrep raw"));
        await _factory.AuditReportStore.CreateAsync(AuditingAndReportsHelpers.Report(
            item.Id.ToString(),
            iteration: 1,
            auditorName: "csharp:build-WaE"));

        var response = await _client.GetAsync($"/workitems/{item.Id}/audit-reports");
        response.EnsureSuccessStatusCode();
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();

        var iterations = doc.GetProperty("iterations");
        Assert.Equal(2, iterations.GetArrayLength());
        Assert.Equal(1, iterations[0].GetProperty("iteration").GetInt32());
        Assert.Equal(2, iterations[1].GetProperty("iteration").GetInt32());
        Assert.Equal(1, iterations[1].GetProperty("blockingCount").GetInt32());
        Assert.Equal(1, iterations[1].GetProperty("nonBlockingCount").GetInt32());

        var auditor = iterations[1].GetProperty("auditors")[0];
        Assert.Equal("security:semgrep", auditor.GetProperty("name").GetString());
        Assert.True(auditor.GetProperty("rawOutputAvailable").GetBoolean());
        var finding = auditor.GetProperty("findings")[0];
        Assert.Equal("f-error", finding.GetProperty("id").GetString());
        Assert.Equal("src/Db.cs", finding.GetProperty("files")[0].GetString());
        Assert.Equal(18, finding.GetProperty("lineHints")[0].GetInt32());
    }

    [Fact]
    public async Task RawOutputEndpoint_ReturnsPlainTextForAuditorNameWithPunctuation()
    {
        var item = NewItem();
        await _factory.WorkItemStore.CreateAsync(item);
        await _factory.AuditReportStore.CreateAsync(AuditingAndReportsHelpers.Report(
            item.Id.ToString(),
            iteration: 1,
            auditorName: "security:gitleaks",
            rawOutput: "scan completed\n"));

        var response = await _client.GetAsync($"/workitems/{item.Id}/audit-reports/1/security%3Agitleaks/raw");
        response.EnsureSuccessStatusCode();

        Assert.Contains("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("scan completed\n", await response.Content.ReadAsStringAsync());
    }

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("p"),
        Title = "audit reports UAT",
        Prompt = "test reports",
        Agent = AgentKind.Claude,
    };
}
