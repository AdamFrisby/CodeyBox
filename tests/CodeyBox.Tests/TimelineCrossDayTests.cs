using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Projects;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that events spanning two rolling audit-log files (different calendar
/// days) are merged and returned in chronological order.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class TimelineCrossDayTests : IDisposable
{
    private readonly TimelineApiFactory _factory = new();
    private readonly HttpClient _client;

    public TimelineCrossDayTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Timeline_MergesEventsAcrossTwoDayFiles()
    {
        var id = WorkItemId.New();
        var yesterday = DateTime.UtcNow.AddDays(-1);
        // Created yesterday morning so both day-files are >= createdAt.
        var t0 = new DateTimeOffset(yesterday.Year, yesterday.Month, yesterday.Day,
            10, 0, 0, TimeSpan.Zero);

        await _factory.Store.CreateAsync(MakeItem(id, t0), CancellationToken.None);

        var yesterdayFile = Path.Combine(_factory.AuditDir, $"audit-{yesterday:yyyyMMdd}.json");
        await File.AppendAllLinesAsync(yesterdayFile, [
            MakeClef(id, "work_item.created", t0, new { Title = "Cross-day" }),
        ]);

        var todayEvent = t0.AddDays(1); // same time, next day
        await File.AppendAllLinesAsync(_factory.TodayAuditFile, [
            MakeClef(id, "work_item.transitioned", todayEvent, new { State = "Done" }),
        ]);

        var body = await GetTimelineAsync(id);

        Assert.Equal(2, body.Entries.Count);
        Assert.True(body.Entries[0].OccurredAt <= body.Entries[1].OccurredAt,
            "Entries must be sorted chronologically");
        Assert.Contains("Queued", body.Entries[0].Summary);
        Assert.Contains("Done", body.Entries[1].Summary);
    }

    [Fact]
    public async Task Timeline_IgnoresFilesBelowCreationDate()
    {
        var id = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow; // created today
        await _factory.Store.CreateAsync(MakeItem(id, t0), CancellationToken.None);

        // Write an event to a two-days-ago file — it must be excluded.
        var old = DateTime.UtcNow.AddDays(-2);
        var oldFile = Path.Combine(_factory.AuditDir, $"audit-{old:yyyyMMdd}.json");
        await File.AppendAllLinesAsync(oldFile, [
            MakeClef(id, "work_item.created", t0.AddDays(-2), new { Title = "Old" }),
        ]);

        // Event in today's file — must be included.
        await File.AppendAllLinesAsync(_factory.TodayAuditFile, [
            MakeClef(id, "work_item.created", t0, new { Title = "Current" }),
        ]);

        var body = await GetTimelineAsync(id);

        Assert.Single(body.Entries); // only the today event
        Assert.Contains("Current", body.Entries[0].Summary);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<TimelineResponse> GetTimelineAsync(WorkItemId id)
    {
        var resp = await _client.GetAsync($"/workitems/{id}/timeline");
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TimelineResponse>())!;
    }

    private static WorkItem MakeItem(WorkItemId id, DateTimeOffset createdAt) => new()
    {
        Id = id,
        ProjectId = new ProjectId("proj"),
        Title = "Cross-day Test",
        Prompt = "p",
        State = WorkItemState.Done,
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
            ["@t"]         = JsonSerializer.SerializeToElement(time.ToString("O")),
            ["EventName"]  = JsonSerializer.SerializeToElement(eventName),
            ["WorkItemId"] = JsonSerializer.SerializeToElement(id.ToString()),
            ["Audit"]      = JsonSerializer.SerializeToElement(true),
        };
        foreach (var prop in extraDoc.RootElement.EnumerateObject())
            result[prop.Name] = prop.Value.Clone();
        return JsonSerializer.Serialize(result);
    }

    private sealed record TimelineResponse(string WorkItemId, List<EntryRecord> Entries);
    private sealed record EntryRecord(DateTimeOffset OccurredAt, string Kind, string Summary);
}
