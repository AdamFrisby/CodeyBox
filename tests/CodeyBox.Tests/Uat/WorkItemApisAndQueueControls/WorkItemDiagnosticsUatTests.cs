using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Tests;

namespace CodeyBox.Tests.Uat.WorkItemApisAndQueueControls;

/// <summary>
/// UAT coverage for work item diagnostics endpoints.
/// Plan anchor: docs/uat/00-plan.md#work-item-diagnostics-endpoints---exposes-diff-timeline-audit-reports-stdout-tail-timings-costs-and-stream-artifacts
/// </summary>
public sealed class WorkItemDiffDiagnosticsUatTests : IClassFixture<DiffApiFactory>
{
    private readonly DiffApiFactory _factory;

    public WorkItemDiffDiagnosticsUatTests(DiffApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Diff_JsonResponseIncludesChangedFileStatsFromBareRepo()
    {
        var item = WorkItemApisAndQueueControlsHelpers.Item(WorkItemState.Working) with
        {
            WorkBranch = null,
        };
        await _factory.Store.CreateAsync(item);
        var workBranch = $"codeybox/{item.Id.ToString()[..8]}";
        await WorkItemApisAndQueueControlsHelpers.CreateBareRepoWithSingleFileDiffAsync(
            _factory.GitRootDir,
            item.Id,
            "main",
            workBranch);

        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/workitems/{item.Id}/diff");
        request.Headers.Accept.ParseAdd("application/json");
        var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        var json = await response.ReadJsonAsync();
        Assert.Equal(item.Id.ToString(), json.GetProperty("workItemId").GetString());
        Assert.Equal("main", json.GetProperty("baseBranch").GetString());
        Assert.Equal(workBranch, json.GetProperty("workBranch").GetString());
        Assert.False(json.GetProperty("truncated").GetBoolean());
        Assert.True(json.GetProperty("filesChanged").GetInt32() > 0);
        Assert.Contains("work item change", json.GetProperty("diff").GetString());
    }
}

[Collection("GlobalSerilog")]
public sealed class TimelineDiagnosticsUatTests : IDisposable
{
    private readonly TimelineApiFactory _factory = new();
    private readonly HttpClient _client;

    public TimelineDiagnosticsUatTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Timeline_MergesCrossDayAuditFilesAndOrdersEntries()
    {
        var id = WorkItemId.New();
        var start = DateTimeOffset.UtcNow.AddDays(-1).AddMinutes(-10);
        await _factory.Store.CreateAsync(new WorkItem
        {
            Id = id,
            ProjectId = new ProjectId("proj"),
            Title = "timeline UAT",
            Prompt = "p",
            State = WorkItemState.Done,
            CreatedAt = start,
            UpdatedAt = start,
        });
        var yesterday = Path.Combine(_factory.AuditDir, $"audit-{start.UtcDateTime:yyyyMMdd}.json");
        await File.AppendAllLinesAsync(yesterday,
        [
            MakeClef(id, "work_item.created", start, new { Title = "timeline UAT" }),
        ]);
        await File.AppendAllLinesAsync(_factory.TodayAuditFile,
        [
            MakeClef(id, "agent.finished", start.AddDays(1), new { Agent = "claude", Sandbox = "process", Success = true, DurationMs = 100L }),
        ]);

        var response = await _client.GetAsync($"/workitems/{id}/timeline");

        response.EnsureSuccessStatusCode();
        var json = await response.ReadJsonAsync();
        var entries = json.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal("state_transition", entries[0].GetProperty("kind").GetString());
        Assert.Equal("agent_finished", entries[1].GetProperty("kind").GetString());
    }

    private static string MakeClef(WorkItemId id, string eventName, DateTimeOffset time, object extra)
    {
        var result = new Dictionary<string, JsonElement>
        {
            ["@t"] = JsonSerializer.SerializeToElement(time.ToString("O")),
            ["EventName"] = JsonSerializer.SerializeToElement(eventName),
            ["WorkItemId"] = JsonSerializer.SerializeToElement(id.ToString()),
            ["Audit"] = JsonSerializer.SerializeToElement(true),
        };
        using var extraDoc = JsonDocument.Parse(JsonSerializer.Serialize(extra));
        foreach (var prop in extraDoc.RootElement.EnumerateObject())
            result[prop.Name] = prop.Value.Clone();
        return JsonSerializer.Serialize(result);
    }
}

public sealed class AuditReportDiagnosticsUatTests : IDisposable
{
    private readonly AuditReportApiFactory _factory = new();
    private readonly HttpClient _client;

    public AuditReportDiagnosticsUatTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task AuditReports_RawOutputEndpointReturnsSpecificIterationAndAuditor()
    {
        var item = WorkItemApisAndQueueControlsHelpers.Item(WorkItemState.AuditFailed, projectId: "p");
        await _factory.WorkItemStore.CreateAsync(item);
        await _factory.AuditReportStore.CreateAsync(new AuditReport
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkItemId = item.Id.ToString(),
            Iteration = 2,
            AuditorName = "security:semgrep",
            AuditorKind = "tool",
            WorstSeverity = "Error",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            EndedAt = DateTimeOffset.UtcNow,
            DurationMs = 200,
            Findings = [new AuditReportFinding("f1", "Error", "SQL injection", "Use parameters", ["src/Db.cs"], [42])],
            RawOutput = "semgrep raw output\n",
        });

        var reports = await _client.GetAsync($"/workitems/{item.Id}/audit-reports");
        reports.EnsureSuccessStatusCode();
        var reportJson = await reports.ReadJsonAsync();
        Assert.Equal(1, reportJson.GetProperty("iterations")[0].GetProperty("blockingCount").GetInt32());
        Assert.True(reportJson.GetProperty("iterations")[0].GetProperty("auditors")[0].GetProperty("rawOutputAvailable").GetBoolean());

        var raw = await _client.GetAsync($"/workitems/{item.Id}/audit-reports/2/security%3Asemgrep/raw");
        raw.EnsureSuccessStatusCode();
        Assert.Contains("text/plain", raw.Content.Headers.ContentType?.MediaType);
        Assert.Equal("semgrep raw output\n", await raw.Content.ReadAsStringAsync());
    }
}

[Collection("GlobalSerilog")]
public sealed class LiveDiagnosticsUatTests : IDisposable
{
    private readonly StdoutTailApiFactory _stdoutFactory = new();
    private readonly HttpClient _stdoutClient;

    public LiveDiagnosticsUatTests() => _stdoutClient = _stdoutFactory.CreateClient();

    public void Dispose()
    {
        _stdoutClient.Dispose();
        _stdoutFactory.Dispose();
    }

    [Fact]
    public async Task StdoutTail_ReturnsOnlyTheRequestedWorkItemsTail()
    {
        var first = WorkItemApisAndQueueControlsHelpers.Item(projectId: "p");
        var second = WorkItemApisAndQueueControlsHelpers.Item(projectId: "p");
        await _stdoutFactory.WorkItemStore.CreateAsync(first);
        await _stdoutFactory.WorkItemStore.CreateAsync(second);
        _stdoutFactory.Broadcaster.SetTail(first.Id, "first output\n");
        _stdoutFactory.Broadcaster.SetTail(second.Id, "second output\n");

        var response = await _stdoutClient.GetAsync($"/workitems/{first.Id}/stdout-tail");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("first output", body);
        Assert.DoesNotContain("second output", body);
    }
}

public sealed class CostAndTimingDiagnosticsUatTests : IDisposable
{
    private readonly CostsApiFactory _costsFactory = new();
    private readonly TimingsApiFactory _timingsFactory = new();
    private readonly HttpClient _costsClient;
    private readonly HttpClient _timingsClient;

    public CostAndTimingDiagnosticsUatTests()
    {
        _costsClient = _costsFactory.CreateClient();
        _timingsClient = _timingsFactory.CreateClient();
    }

    public void Dispose()
    {
        _costsClient.Dispose();
        _timingsClient.Dispose();
        _costsFactory.Dispose();
        _timingsFactory.Dispose();
    }

    [Fact]
    public async Task CostAndTimingEndpoints_ReturnPhaseSummaries()
    {
        var costItem = WorkItemApisAndQueueControlsHelpers.Item(WorkItemState.Done);
        await _costsFactory.Store.CreateAsync(costItem);
        await _costsFactory.CostStore.RecordAsync(new WorkItemCost
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkItemId = costItem.Id.ToString(),
            Phase = "work",
            AgentKind = "claude",
            ModelId = "claude-opus-4-7",
            InputTokens = 100,
            CachedInputTokens = 20,
            OutputTokens = 30,
            EstimatedUsd = 0.12,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            EndedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        });

        var timingItem = WorkItemApisAndQueueControlsHelpers.Item(WorkItemState.Done);
        await _timingsFactory.Store.CreateAsync(timingItem);
        var timing = new TimingRecord
        {
            WorkItemId = timingItem.Id,
            Phase = "audit",
            Step = "security:semgrep",
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
            MetadataJson = "{}",
        };
        await _timingsFactory.TimingStore.BeginAsync(timing);
        await _timingsFactory.TimingStore.EndAsync(timing.Id, timing.StartedAt.AddMilliseconds(500), 500);

        var costs = await _costsClient.GetAsync($"/workitems/{costItem.Id}/costs");
        var timings = await _timingsClient.GetAsync($"/workitems/{timingItem.Id}/timings");

        costs.EnsureSuccessStatusCode();
        timings.EnsureSuccessStatusCode();
        var costsJson = await costs.ReadJsonAsync();
        var timingsJson = await timings.ReadJsonAsync();
        Assert.Equal(100, costsJson.GetProperty("totals").GetProperty("inputTokens").GetInt32());
        Assert.True(costsJson.GetProperty("byPhase").TryGetProperty("work", out _));
        Assert.Equal(500, timingsJson.GetProperty("totalDurationMs").GetInt64());
        Assert.True(timingsJson.GetProperty("byPhase").TryGetProperty("audit", out _));
    }
}
