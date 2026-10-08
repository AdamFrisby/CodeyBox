using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Projects;
using Serilog;
using Serilog.Events;

namespace CodeyBox.Tests;

/// <summary>
/// Reporting-only regression tests for GET /workitems/{id}/timeline.
/// All assertions run through the real <c>AuditLogTimelineReader</c> + API
/// mapping with synthetic CLEF fixtures whose property names match the
/// <c>AuditLog</c> emitters exactly (see
/// <c>WorkItemTimelineEmittedSchemaTests</c> for the emitter side of that
/// contract).
/// </summary>
[Collection("GlobalSerilog")]
public sealed class WorkItemTimelineReportingTests : IDisposable
{
    private readonly TimelineApiFactory _factory = new();
    private readonly HttpClient _client;

    public WorkItemTimelineReportingTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Pickup_InWorkComplete_DoesNotBecomeWorking()
    {
        var id = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-30);
        await _factory.Store.CreateAsync(MakeItem(id, t0, WorkItemState.WorkComplete), CancellationToken.None);

        await File.AppendAllLinesAsync(_factory.TodayAuditFile, [
            MakeClef(id, "work_item.created", t0, new { Title = "Boundary wedge" }),
            MakeClef(id, "work_item.transitioned", t0.AddMinutes(1), new { State = "WorkComplete" }),
            // Watchdog recovered the wedged boundary worker in place, then the
            // dispatcher picked the item up still in WorkComplete.
            MakeClef(id, "work_item.watchdog_recovered", t0.AddMinutes(2),
                new { WorkerId = "worker-7", FromState = "WorkComplete", ToState = "WorkComplete", DependentsRestored = 0 }),
            MakeClef(id, "work_item.picked_up", t0.AddMinutes(3),
                new { WorkerId = 7, State = "WorkComplete", Attempt = 2 }),
        ]);

        var body = await GetTimelineAsync(id);

        Assert.Equal(4, body.Entries.Count);
        var pickup = body.Entries[3];
        Assert.Equal("pickup", pickup.Kind);
        Assert.Contains("WorkComplete", pickup.Summary);
        Assert.DoesNotContain("→ Working", pickup.Summary);
        Assert.DoesNotContain("→ Working", string.Join("\n", body.Entries.Select(e => e.Summary)));
        Assert.All(body.Entries.Where(e => e.Kind == "state_transition"),
            e => Assert.DoesNotContain("Working → Working", e.Summary));
    }

    [Fact]
    public async Task LegacyPickup_WithoutState_RendersHonestUnknown_AndDoesNotAdvancePrevState()
    {
        var id = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-20);
        await _factory.Store.CreateAsync(MakeItem(id, t0, WorkItemState.Working), CancellationToken.None);

        await File.AppendAllLinesAsync(_factory.TodayAuditFile, [
            MakeClef(id, "work_item.created", t0, new { Title = "Legacy" }),
            // Pre-instrumentation record: no State/Attempt properties.
            MakeClef(id, "work_item.picked_up", t0.AddMinutes(1), new { WorkerId = 3 }),
            MakeClef(id, "work_item.transitioned", t0.AddMinutes(2), new { State = "Working" }),
        ]);

        var body = await GetTimelineAsync(id);

        Assert.Equal(3, body.Entries.Count);
        var pickup = body.Entries[1];
        Assert.Equal("pickup", pickup.Kind);
        Assert.Contains("legacy", pickup.Summary);
        Assert.Contains("not a state transition", pickup.Summary);
        // The pickup must not poison prevState: the real transition still
        // reads Queued → Working, not Working → Working.
        Assert.Contains("Queued → Working", body.Entries[2].Summary);
    }

    [Fact]
    public async Task WatchdogRecovery_SameState_IsVisibleInPlace()
    {
        var id = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-20);
        await _factory.Store.CreateAsync(MakeItem(id, t0, WorkItemState.WorkComplete), CancellationToken.None);

        await File.AppendAllLinesAsync(_factory.TodayAuditFile, [
            MakeClef(id, "work_item.watchdog_recovered", t0,
                new { WorkerId = "worker-2", FromState = "WorkComplete", ToState = "WorkComplete", DependentsRestored = 1 }),
        ]);

        var body = await GetTimelineAsync(id);

        var entry = Assert.Single(body.Entries);
        Assert.Equal("recovery", entry.Kind);
        Assert.Contains("WorkComplete", entry.Summary);
        Assert.Contains("in place", entry.Summary);
        Assert.Contains("not a phase transition", entry.Summary);
        Assert.Equal(1, entry.Details.GetProperty("dependentsRestored").GetInt32());
    }

    [Fact]
    public async Task DeadWorkerRecovery_SurfacesAttemptAndStates()
    {
        var id = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-20);
        await _factory.Store.CreateAsync(MakeItem(id, t0, WorkItemState.Queued), CancellationToken.None);

        await File.AppendAllLinesAsync(_factory.TodayAuditFile, [
            MakeClef(id, "work_item.worker_dead_recovered", t0,
                new { WorkerId = "host-1:9", FromState = "Working", ToState = "Queued", Attempt = 3 }),
        ]);

        var body = await GetTimelineAsync(id);

        var entry = Assert.Single(body.Entries);
        Assert.Equal("recovery", entry.Kind);
        Assert.Contains("Working → Queued", entry.Summary);
        Assert.Contains("attempt 3", entry.Summary);
        Assert.Equal(3, entry.Details.GetProperty("attempt").GetInt32());
    }

    [Fact]
    public async Task ItemStaleRecovery_SurfacesTrigger()
    {
        var id = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-20);
        await _factory.Store.CreateAsync(MakeItem(id, t0, WorkItemState.Queued), CancellationToken.None);

        await File.AppendAllLinesAsync(_factory.TodayAuditFile, [
            MakeClef(id, "work_item.item_stale_recovered", t0,
                new { WorkerId = "worker-4", FromState = "Reworking", ToState = "Queued", Attempt = 1, BranchPreserved = true, Trigger = "transport-reconnect-loop" }),
        ]);

        var body = await GetTimelineAsync(id);

        var entry = Assert.Single(body.Entries);
        Assert.Equal("recovery", entry.Kind);
        Assert.Contains("Reworking → Queued", entry.Summary);
        Assert.Contains("transport-reconnect-loop", entry.Summary);
        Assert.True(entry.Details.GetProperty("branchPreserved").GetBoolean());
    }

    [Fact]
    public async Task Recovery_MissingStates_StaysHonest()
    {
        var id = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-20);
        await _factory.Store.CreateAsync(MakeItem(id, t0, WorkItemState.Queued), CancellationToken.None);

        await File.AppendAllLinesAsync(_factory.TodayAuditFile, [
            MakeClef(id, "work_item.watchdog_recovered", t0, new { WorkerId = "worker-2", DependentsRestored = 0 }),
            MakeClef(id, "work_item.transitioned", t0.AddMinutes(1), new { State = "Queued" }),
        ]);

        var body = await GetTimelineAsync(id);

        Assert.Equal(2, body.Entries.Count);
        Assert.Contains("not recorded", body.Entries[0].Summary);
        // A stateless recovery must not corrupt prevState inference: with no
        // prior state, the transition renders open-ended rather than invented.
        Assert.Equal("→ Queued", body.Entries[1].Summary);
    }

    [Fact]
    public async Task InfrastructureDeferral_ShowsRecordedCauseAndResumeState()
    {
        var id = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-20);
        await _factory.Store.CreateAsync(MakeItem(id, t0, WorkItemState.Queued), CancellationToken.None);

        await File.AppendAllLinesAsync(_factory.TodayAuditFile, [
            MakeClef(id, "sandbox.provisioning_deferred", t0,
                new { Provider = "ec2", Operation = "run-instances", ErrorClass = "InsufficientCapacity", ResumeState = "Queued", RecheckSeconds = 300L, Detail = "no capacity in us-east-1a" }),
        ]);

        var body = await GetTimelineAsync(id);

        var entry = Assert.Single(body.Entries);
        Assert.Equal("deferral", entry.Kind);
        Assert.Contains("ec2", entry.Summary);
        Assert.Contains("InsufficientCapacity", entry.Summary);
        Assert.Contains("Queued", entry.Summary);
        Assert.DoesNotContain("not inferred", entry.Summary);
    }

    [Fact]
    public async Task InfrastructureDeferral_MissingCause_DoesNotInventOne()
    {
        var id = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-20);
        await _factory.Store.CreateAsync(MakeItem(id, t0, WorkItemState.Queued), CancellationToken.None);

        // Historical/sparse record: provider only, no error class, resume
        // state, or detail. The timeline must say so, not fill the gap.
        await File.AppendAllLinesAsync(_factory.TodayAuditFile, [
            MakeClef(id, "sandbox.provisioning_deferred", t0, new { Provider = "ec2" }),
        ]);

        var body = await GetTimelineAsync(id);

        var entry = Assert.Single(body.Entries);
        Assert.Equal("deferral", entry.Kind);
        Assert.Contains("no recorded cause; not inferred", entry.Summary);
        Assert.Contains("not recorded", entry.Summary);
    }

    [Fact]
    public async Task MultiplePickups_PreserveAttemptIdentityAndOrder()
    {
        var id = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-40);
        await _factory.Store.CreateAsync(MakeItem(id, t0, WorkItemState.Queued), CancellationToken.None);

        await File.AppendAllLinesAsync(_factory.TodayAuditFile, [
            MakeClef(id, "work_item.created", t0, new { Title = "Flaky" }),
            MakeClef(id, "work_item.picked_up", t0.AddMinutes(1), new { WorkerId = 1, State = "Queued", Attempt = 0 }),
            MakeClef(id, "work_item.worker_dead_recovered", t0.AddMinutes(2),
                new { WorkerId = "host-1", FromState = "Working", ToState = "Queued", Attempt = 1 }),
            MakeClef(id, "work_item.picked_up", t0.AddMinutes(3), new { WorkerId = 2, State = "Queued", Attempt = 1 }),
        ]);

        var body = await GetTimelineAsync(id);

        Assert.Equal(4, body.Entries.Count);
        var pickups = body.Entries.Where(e => e.Kind == "pickup").ToList();
        Assert.Equal(2, pickups.Count);
        Assert.Equal(0, pickups[0].Details.GetProperty("attempt").GetInt32());
        Assert.Equal(1, pickups[1].Details.GetProperty("attempt").GetInt32());
        Assert.True(body.Entries[0].OccurredAt <= body.Entries[1].OccurredAt);
    }

    [Fact]
    public async Task EqualTimestamps_PreserveFileOrder()
    {
        var id = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        await _factory.Store.CreateAsync(MakeItem(id, t0, WorkItemState.Working), CancellationToken.None);

        var same = t0.AddMinutes(1);
        await File.AppendAllLinesAsync(_factory.TodayAuditFile, [
            MakeClef(id, "work_item.created", same, new { Title = "Tie" }),
            MakeClef(id, "work_item.picked_up", same, new { WorkerId = 5, State = "Queued", Attempt = 0 }),
            MakeClef(id, "agent.started", same, new { Agent = "claude", Phase = "work", Sandbox = "vm-1" }),
        ]);

        var first = await GetTimelineAsync(id);
        var second = await GetTimelineAsync(id);

        Assert.Equal(3, first.Entries.Count);
        Assert.True(first.Entries.Select(e => e.Kind).SequenceEqual(
            ["state_transition", "pickup", "agent_started"]));
        Assert.True(first.Entries.Select(e => e.Summary).SequenceEqual(
            second.Entries.Select(e => e.Summary)));
    }

    [Fact]
    public async Task SinceFilter_WorksAcrossDayBoundary()
    {
        var id = WorkItemId.New();
        var yesterday = DateTime.UtcNow.AddDays(-1);
        var t0 = new DateTimeOffset(yesterday.Year, yesterday.Month, yesterday.Day,
            10, 0, 0, TimeSpan.Zero);
        await _factory.Store.CreateAsync(MakeItem(id, t0, WorkItemState.Done), CancellationToken.None);

        var yesterdayFile = Path.Combine(_factory.AuditDir, $"audit-{yesterday:yyyyMMdd}.json");
        await File.AppendAllLinesAsync(yesterdayFile, [
            MakeClef(id, "work_item.created", t0, new { Title = "Yesterday" }),
            MakeClef(id, "work_item.picked_up", t0.AddHours(1), new { WorkerId = 1, State = "Queued", Attempt = 0 }),
        ]);
        var todayEvent = t0.AddDays(1);
        await File.AppendAllLinesAsync(_factory.TodayAuditFile, [
            MakeClef(id, "work_item.transitioned", todayEvent, new { State = "Done" }),
        ]);

        var cutoff = todayEvent.AddMinutes(-1);
        var resp = await _client.GetAsync(
            $"/workitems/{id}/timeline?since={Uri.EscapeDataString(cutoff.ToString("O"))}");
        resp.EnsureSuccessStatusCode();
        var body = (await resp.Content.ReadFromJsonAsync<TimelineResponse>())!;

        var entry = Assert.Single(body.Entries);
        Assert.Contains("Done", entry.Summary);
    }

    [Fact]
    public async Task MalformedAndUnknownLines_AreSkipped()
    {
        var id = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        await _factory.Store.CreateAsync(MakeItem(id, t0, WorkItemState.Queued), CancellationToken.None);

        await File.AppendAllLinesAsync(_factory.TodayAuditFile, [
            "{not json",
            MakeClef(id, "work_item.created", t0, new { Title = "Valid" }),
            """{"EventName":"work_item.transitioned","WorkItemId":"WRONG","@t":"2026-01-01T00:00:00Z","State":"Working"}""",
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["@t"] = t0.AddMinutes(1).ToString("O"),
                ["EventName"] = "work_item.transitioned",
                ["WorkItemId"] = id.ToString(),
            }),
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["@t"] = "not-a-timestamp",
                ["EventName"] = "work_item.transitioned",
                ["WorkItemId"] = id.ToString(),
                ["State"] = "Working",
            }),
            MakeClef(id, "some.future_event_not_yet_mapped", t0.AddMinutes(2), new { Anything = 1 }),
            MakeClef(id, "quota_router.deferred", t0.AddMinutes(3), new { RecheckMs = 15000L }),
        ]);

        var body = await GetTimelineAsync(id);

        // created + stateless transitioned (State missing → Unknown) + deferral.
        Assert.Equal(3, body.Entries.Count);
        Assert.Contains("→ Unknown", body.Entries[1].Summary);
        Assert.Equal("deferral", body.Entries[2].Kind);
    }

    [Fact]
    public async Task AgentFinished_IsRawExit_BoundedAndEscaped()
    {
        var id = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        await _factory.Store.CreateAsync(MakeItem(id, t0, WorkItemState.Working), CancellationToken.None);

        var hostileTail = "\x1b[31mred\n" + new string('y', 5000) + "\r\nmore";
        var hostileTitle = "T\x1b]0;pwned\x07\nline2";
        await File.AppendAllLinesAsync(_factory.TodayAuditFile, [
            MakeClef(id, "work_item.created", t0, new { Title = hostileTitle }),
            MakeClef(id, "agent.finished", t0.AddMinutes(1), new
            {
                Agent = "claude", Sandbox = "vm", Success = true,
                DurationMs = 60_000L, StdoutTail = hostileTail, StderrTail = ""
            }),
        ]);

        var body = await GetTimelineAsync(id);

        Assert.Equal(2, body.Entries.Count);
        Assert.DoesNotContain("\x1b", body.Entries[0].Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", body.Entries[0].Summary, StringComparison.Ordinal);
        var finished = body.Entries[1];
        Assert.Contains("succeeded", finished.Summary);
        Assert.Contains("raw agent exit", finished.Summary);
        Assert.Contains("not a phase verdict", finished.Summary);
        var tail = finished.Details.GetProperty("stdoutTail").GetString()!;
        Assert.True(tail.Length <= 501, $"tail must be bounded, was {tail.Length}");
        Assert.DoesNotContain("\x1b", tail, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", tail, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('y', 5000), tail);
    }

    [Fact]
    public async Task KindFilter_IncludesNewRecoveryAndDeferralKinds()
    {
        var id = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        await _factory.Store.CreateAsync(MakeItem(id, t0, WorkItemState.Queued), CancellationToken.None);

        await File.AppendAllLinesAsync(_factory.TodayAuditFile, [
            MakeClef(id, "work_item.created", t0, new { Title = "Filter" }),
            MakeClef(id, "work_item.picked_up", t0.AddMinutes(1), new { WorkerId = 1, State = "Queued", Attempt = 0 }),
            MakeClef(id, "quota_router.deferred", t0.AddMinutes(2), new { RecheckMs = 15000L }),
            MakeClef(id, "work_item.watchdog_stuck", t0.AddMinutes(3),
                new { WorkerId = "worker-1", State = "Working", SinceProgressSeconds = 900L, LastStreamEvent = "none" }),
        ]);

        var resp = await _client.GetAsync($"/workitems/{id}/timeline?kind=recovery,deferral,pickup,watchdog_stuck");
        resp.EnsureSuccessStatusCode();
        var body = (await resp.Content.ReadFromJsonAsync<TimelineResponse>())!;

        Assert.Equal(3, body.Entries.Count);
        Assert.Contains(body.Entries, e => e.Kind == "pickup");
        Assert.Contains(body.Entries, e => e.Kind == "deferral");
        Assert.Contains(body.Entries, e => e.Kind == "watchdog_stuck");
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
        Title = "Reporting Test",
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
    private sealed record EntryRecord(DateTimeOffset OccurredAt, string Kind, string Summary, JsonElement Details);
}

/// <summary>
/// Guards the emitter/reader contract with the real <c>AuditLog</c> methods:
/// events are emitted through Serilog exactly as production emits them,
/// converted to CLEF lines, and read back through the real timeline API.
/// If an emitter renames a property the reader relies on, these fail.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class WorkItemTimelineEmittedSchemaTests : IDisposable
{
    private readonly TimelineApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly TestSink _sink = new();

    public WorkItemTimelineEmittedSchemaTests()
    {
        _client = _factory.CreateClient();
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        Log.CloseAndFlush();
    }

    [Fact]
    public async Task EmittedPickup_WithState_RendersPickupInRecordedState()
    {
        var id = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        await _factory.Store.CreateAsync(MakeItem(id, t0), CancellationToken.None);

        AuditLog.WorkItemPickedUp(4, id, "WorkComplete", 2);
        await FlushEmittedAsync(id, t0);

        var body = await GetTimelineAsync(id);
        var entry = Assert.Single(body.Entries);
        Assert.Equal("pickup", entry.Kind);
        Assert.Contains("WorkComplete", entry.Summary);
        Assert.Contains("attempt 2", entry.Summary);
        Assert.DoesNotContain("→ Working", entry.Summary);
    }

    [Fact]
    public async Task EmittedLegacyPickup_StaysHonest()
    {
        var id = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        await _factory.Store.CreateAsync(MakeItem(id, t0), CancellationToken.None);

        AuditLog.WorkItemPickedUp(4, id);
        // The legacy overload must not emit State/Attempt properties at all:
        // presence (even empty) would defeat the reader's legacy detection.
        var evt = Assert.Single(_sink.Events);
        Assert.False(evt.Properties.ContainsKey("State"));
        Assert.False(evt.Properties.ContainsKey("Attempt"));
        await FlushEmittedAsync(id, t0);

        var body = await GetTimelineAsync(id);
        var entry = Assert.Single(body.Entries);
        Assert.Equal("pickup", entry.Kind);
        Assert.Contains("legacy", entry.Summary);
    }

    [Fact]
    public async Task EmittedWatchdogRecovery_RoundTripsStates()
    {
        var id = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        await _factory.Store.CreateAsync(MakeItem(id, t0), CancellationToken.None);

        AuditLog.WorkItemWatchdogRecovered(id, "worker-9", WorkItemState.WorkComplete, WorkItemState.WorkComplete, 0);
        await FlushEmittedAsync(id, t0);

        var body = await GetTimelineAsync(id);
        var entry = Assert.Single(body.Entries);
        Assert.Equal("recovery", entry.Kind);
        Assert.Contains("in place", entry.Summary);
        Assert.Equal("WorkComplete", entry.Details.GetProperty("from").GetString());
        Assert.Equal("WorkComplete", entry.Details.GetProperty("to").GetString());
    }

    [Fact]
    public async Task EmittedDeadWorkerAndStaleRecoveries_RoundTrip()
    {
        var id = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        await _factory.Store.CreateAsync(MakeItem(id, t0), CancellationToken.None);

        AuditLog.DeadWorkerRecovered(id, "host-2", WorkItemState.Working, WorkItemState.Queued, 1);
        AuditLog.WorkItemStaleDetected(id, "host-2", WorkItemState.Working, 700, "heartbeat-gap");
        await FlushEmittedAsync(id, t0, step: TimeSpan.FromSeconds(1));

        var body = await GetTimelineAsync(id);
        Assert.Equal(2, body.Entries.Count);
        Assert.Equal("recovery", body.Entries[0].Kind);
        Assert.Contains("attempt 1", body.Entries[0].Summary);
        Assert.Equal("watchdog_stuck", body.Entries[1].Kind);
        Assert.Contains("heartbeat-gap", body.Entries[1].Summary);
    }

    [Fact]
    public async Task EmittedDeferrals_RoundTripRecordedCause()
    {
        var id = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        await _factory.Store.CreateAsync(MakeItem(id, t0), CancellationToken.None);

        AuditLog.SandboxProvisioningDeferred(id, "ec2", "run-instances", "InsufficientCapacity", "Queued", TimeSpan.FromMinutes(5), "no capacity");
        AuditLog.BudgetDeferred(id, new ProjectId("proj"), "over budget");
        AuditLog.QuotaRouterDeferred(id, TimeSpan.FromSeconds(15));
        await FlushEmittedAsync(id, t0, step: TimeSpan.FromSeconds(1));

        var body = await GetTimelineAsync(id);
        Assert.Equal(3, body.Entries.Count);
        Assert.All(body.Entries, e => Assert.Equal("deferral", e.Kind));
        Assert.Contains("InsufficientCapacity", body.Entries[0].Summary);
        Assert.Contains("over budget", body.Entries[1].Summary);
        Assert.Contains("15000ms", body.Entries[2].Summary);
    }

    [Fact]
    public async Task EmittedTerminalClassification_WithoutReason_DoesNotInventCause()
    {
        var id = WorkItemId.New();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        await _factory.Store.CreateAsync(MakeItem(id, t0), CancellationToken.None);

        AuditLog.TerminalFailureClassified(id, "unknown", "", "Failed", "parked", 1, 3);
        await FlushEmittedAsync(id, t0);

        var body = await GetTimelineAsync(id);
        var entry = Assert.Single(body.Entries);
        Assert.Equal("failure_classified", entry.Kind);
        Assert.Contains("no recorded cause; not inferred", entry.Summary);
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
        Title = "Schema Test",
        Prompt = "p",
        State = WorkItemState.Working,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
        QueuePosition = 1,
    };

    // Writes captured Serilog events as CLEF lines with staggered timestamps
    // so file order matches emission order even at equal wall-clock times.
    private async Task FlushEmittedAsync(WorkItemId id, DateTimeOffset t0, TimeSpan? step = null)
    {
        var increment = step ?? TimeSpan.FromMilliseconds(1);
        var lines = _sink.Events.Select((evt, i) => ToClef(evt, id, t0.AddTicks(increment.Ticks * i)));
        await File.AppendAllLinesAsync(_factory.TodayAuditFile, lines);
        _sink.Clear();
    }

    private static string ToClef(LogEvent evt, WorkItemId id, DateTimeOffset time)
    {
        var dict = new Dictionary<string, object?>
        {
            ["@t"] = time.ToString("O"),
        };
        foreach (var kv in evt.Properties)
        {
            dict[kv.Key] = kv.Value switch
            {
                ScalarValue sv when sv.Value is Enum e => e.ToString(),
                ScalarValue sv => sv.Value,
                _ => kv.Value.ToString(),
            };
        }
        // Belt and suspenders: the timeline filters on WorkItemId, so every
        // emitted line under test must carry the id under test.
        dict["WorkItemId"] = id.ToString();
        return JsonSerializer.Serialize(dict);
    }

    private sealed record TimelineResponse(string WorkItemId, List<EntryRecord> Entries);
    private sealed record EntryRecord(DateTimeOffset OccurredAt, string Kind, string Summary, JsonElement Details);
}
