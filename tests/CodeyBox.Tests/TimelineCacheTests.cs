using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Projects;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that AuditLogTimelineReader caches terminal-item timelines and
/// re-reads in-flight timelines on every request.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class TimelineCacheTests : IDisposable
{
    private readonly TimelineApiFactory _factory = new();
    private readonly HttpClient _client;

    public TimelineCacheTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task TerminalItem_IsCached_SecondCallIgnoresNewEntries()
    {
        var id = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        await _factory.Store.CreateAsync(MakeItem(id, t0, WorkItemState.Done), CancellationToken.None);

        await File.AppendAllLinesAsync(_factory.TodayAuditFile, [
            MakeClef(id, "work_item.created", t0, new { Title = "Cache Test" }),
        ]);

        var first = await GetTimelineAsync(id);
        Assert.Single(first.Entries);

        // Write a second event — the terminal cache should prevent it from appearing.
        await File.AppendAllLinesAsync(_factory.TodayAuditFile, [
            MakeClef(id, "work_item.transitioned", t0.AddMinutes(1), new { State = "Done" }),
        ]);

        var second = await GetTimelineAsync(id);
        Assert.Single(second.Entries); // still 1 — cached
    }

    [Fact]
    public async Task InFlightItem_IsNotCached_SecondCallReflectsNewEntries()
    {
        var id = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        await _factory.Store.CreateAsync(MakeItem(id, t0, WorkItemState.Working), CancellationToken.None);

        await File.AppendAllLinesAsync(_factory.TodayAuditFile, [
            MakeClef(id, "work_item.created", t0, new { Title = "Live Test" }),
        ]);

        var first = await GetTimelineAsync(id);
        Assert.Single(first.Entries);

        // Write a second event — the in-flight reader must pick it up.
        await File.AppendAllLinesAsync(_factory.TodayAuditFile, [
            MakeClef(id, "work_item.transitioned", t0.AddMinutes(1), new { State = "Working" }),
        ]);

        var second = await GetTimelineAsync(id);
        Assert.Equal(2, second.Entries.Count); // now 2 — re-read
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<TimelineResponse> GetTimelineAsync(WorkItemId id)
    {
        var resp = await _client.GetAsync($"/workitems/{id}/timeline");
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TimelineResponse>())!;
    }

    private static WorkItem MakeItem(WorkItemId id, DateTimeOffset createdAt, WorkItemState state) => new()
    {
        Id = id,
        ProjectId = new ProjectId("proj"),
        Title = "Cache Test",
        Prompt = "p",
        State = state,
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
            ["@t"] = JsonSerializer.SerializeToElement(time.ToString("O")),
            ["EventName"] = JsonSerializer.SerializeToElement(eventName),
            ["WorkItemId"] = JsonSerializer.SerializeToElement(id.ToString()),
            ["Audit"] = JsonSerializer.SerializeToElement(true),
        };
        foreach (var prop in extraDoc.RootElement.EnumerateObject())
            result[prop.Name] = prop.Value.Clone();
        return JsonSerializer.Serialize(result);
    }

    private sealed record TimelineResponse(string WorkItemId, List<EntryRecord> Entries);
    private sealed record EntryRecord(DateTimeOffset OccurredAt, string Kind, string Summary);
}
