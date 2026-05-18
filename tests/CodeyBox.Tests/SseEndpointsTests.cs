using System.Net;
using System.Net.Http.Headers;
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
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// HTTP-level tests for the SSE endpoints (<c>/workitems/events</c> and
/// <c>/workitems/{id}/events</c>). Each test publishes events through the
/// real <see cref="WebhookEventBroadcaster"/> registered in the app and
/// asserts on the bytes that come back over a streaming HttpClient.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class SseEndpointsTests : IDisposable
{
    private readonly SseApiFactory _factory = new();
    private readonly HttpClient _client;

    public SseEndpointsTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Stream_EmitsBroadcasterEventsAsSseFrames()
    {
        var workItemId = WorkItemId.New();
        var item = await CreateWorkItemAsync(workItemId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var resp = await _client.GetAsync(
            $"/workitems/{item.Id}/events",
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);
        resp.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);

        // Give the handler a beat to register its subscriber before we publish.
        await WaitForSubscribersAsync(1, cts.Token);

        var broadcaster = _factory.Services.GetRequiredService<WebhookEventBroadcaster>();
        broadcaster.Publish(EventFor(item, "work_item.working"));
        broadcaster.Publish(EventFor(item, "work_item.done", state: WorkItemState.Done));

        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        var first = await ReadFrameAsync(reader, cts.Token);
        var second = await ReadFrameAsync(reader, cts.Token);

        Assert.Equal("work_item.working", first.EventType);
        Assert.Equal("work_item.done", second.EventType);

        // Same shape as a webhook payload — verify the data field round-trips
        // through the webhook's JSON serialiser.
        using var doc = JsonDocument.Parse(second.Data);
        Assert.Equal("work_item.done", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal(item.Id.ToString(), doc.RootElement.GetProperty("workItem").GetProperty("id").GetString());

        // Sequence IDs are monotonic and present.
        Assert.True(long.Parse(first.Id) < long.Parse(second.Id));
    }

    [Fact]
    public async Task Stream_ClosesAfterTerminalStateForSingleItemStream()
    {
        var item = await CreateWorkItemAsync(WorkItemId.New());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var resp = await _client.GetAsync(
            $"/workitems/{item.Id}/events",
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);
        resp.EnsureSuccessStatusCode();
        await WaitForSubscribersAsync(1, cts.Token);

        var broadcaster = _factory.Services.GetRequiredService<WebhookEventBroadcaster>();
        broadcaster.Publish(EventFor(item, "work_item.done", state: WorkItemState.Done));

        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);
        var frame = await ReadFrameAsync(reader, cts.Token);
        Assert.Equal("work_item.done", frame.EventType);

        // After terminal state, the handler closes the response. ReadLine
        // returns null once the writer closes.
        var tail = await reader.ReadToEndAsync(cts.Token);
        Assert.Equal(string.Empty, tail.Replace(":keepalive", "").Trim());

        await WaitForSubscribersAsync(0, cts.Token);
    }

    [Fact]
    public async Task Disconnect_RemovesSubscriberFromBroadcaster()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var resp = await _client.GetAsync(
            "/workitems/events",
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);
        resp.EnsureSuccessStatusCode();
        await WaitForSubscribersAsync(1, cts.Token);

        resp.Dispose();
        await WaitForSubscribersAsync(0, cts.Token);
    }

    [Fact]
    public async Task LastEventId_ReplaysEventsAfterCursor()
    {
        var item = await CreateWorkItemAsync(WorkItemId.New());
        var broadcaster = _factory.Services.GetRequiredService<WebhookEventBroadcaster>();

        // Publish 3 events before any client connects; their server-assigned
        // ids will be N+1, N+2, N+3 — capture N to make the test stable
        // regardless of any startup-time events published by background services.
        var baseEvt = broadcaster.Publish(EventFor(item, "work_item.auditing"));
        long baseId = baseEvt.SequenceId;
        var e1 = broadcaster.Publish(EventFor(item, "work_item.working"));
        var e2 = broadcaster.Publish(EventFor(item, "work_item.audit_passed"));
        var e3 = broadcaster.Publish(EventFor(item, "work_item.done", state: WorkItemState.Done));

        using var req = new HttpRequestMessage(HttpMethod.Get, $"/workitems/{item.Id}/events");
        req.Headers.TryAddWithoutValidation("Last-Event-ID", e1.SequenceId.ToString());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        // We should receive e2 and e3 (but not e1, since cursor was e1.SequenceId).
        var first = await ReadFrameAsync(reader, cts.Token);
        var second = await ReadFrameAsync(reader, cts.Token);

        Assert.Equal(e2.SequenceId.ToString(), first.Id);
        Assert.Equal("work_item.audit_passed", first.EventType);
        Assert.Equal(e3.SequenceId.ToString(), second.Id);
        Assert.Equal("work_item.done", second.EventType);

        // Sanity: baseId < e1 < e2 < e3 (monotonic ids).
        Assert.True(baseId < e1.SequenceId && e1.SequenceId < e2.SequenceId && e2.SequenceId < e3.SequenceId);
    }

    [Fact]
    public async Task SingleItemStream_ResolvesCompositeProjectAndExternalId()
    {
        // A work item identified by '<projectId>:<externalId>' must resolve via
        // IWorkItemStore.GetByExternalIdAsync and stream events for that item.
        const string externalId = "ext-42";
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            ExternalId = externalId,
            Title = "Composite",
            Prompt = "p",
            State = WorkItemState.Queued,
        };
        await _factory.Store.CreateAsync(item, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var resp = await _client.GetAsync(
            $"/workitems/proj:{externalId}/events",
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);
        resp.EnsureSuccessStatusCode();
        await WaitForSubscribersAsync(1, cts.Token);

        var broadcaster = _factory.Services.GetRequiredService<WebhookEventBroadcaster>();
        broadcaster.Publish(EventFor(item, "work_item.working"));

        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);
        var frame = await ReadFrameAsync(reader, cts.Token);
        Assert.Equal("work_item.working", frame.EventType);
    }

    [Fact]
    public async Task SingleItemStream_ReturnsBadRequestForNonGuidNonCompositeId()
    {
        // 'not-a-uuid' has no ':' so it must parse as a Guid; failing that, 400.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var resp = await _client.GetAsync("/workitems/not-a-uuid/events", cts.Token);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task SingleItemStream_ReturnsNotFoundForUnknownGuidId()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var resp = await _client.GetAsync(
            $"/workitems/{Guid.NewGuid()}/events",
            cts.Token);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task SingleItemStream_ReturnsNotFoundForUnknownCompositeId()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var resp = await _client.GetAsync(
            "/workitems/proj:nope-not-here/events",
            cts.Token);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Theory]
    [InlineData(":external-id")]      // empty projectId
    [InlineData("proj:")]              // empty externalId
    public async Task SingleItemStream_ReturnsBadRequestForMalformedCompositeId(string idSegment)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var resp = await _client.GetAsync($"/workitems/{idSegment}/events", cts.Token);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task SingleItemStream_ReturnsBadRequestForInvalidProjectIdSegment()
    {
        // '!!!' is rejected by ProjectId's validator (non-alnum/dash/underscore).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var resp = await _client.GetAsync("/workitems/%21%21%21:ext-1/events", cts.Token);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task SingleItemStream_ReturnsBadRequestForInvalidExternalIdSegment()
    {
        // External ids must not start with 'wi-' (reserved prefix).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var resp = await _client.GetAsync("/workitems/proj:wi-reserved/events", cts.Token);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_FiresWhenStreamIsIdle()
    {
        // SseApiFactory sets HeartbeatSeconds=1 so this test can wait briefly.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var resp = await _client.GetAsync(
            "/workitems/events",
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        // First non-empty line on an idle stream must be a comment heartbeat.
        string? line;
        while ((line = await reader.ReadLineAsync(cts.Token)) is not null)
        {
            if (line.Length == 0) continue;
            Assert.StartsWith(":", line);
            Assert.Contains("keepalive", line);
            return;
        }
        Assert.Fail("stream closed before any heartbeat arrived");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<WorkItem> CreateWorkItemAsync(WorkItemId id)
    {
        var item = new WorkItem
        {
            Id = id,
            ProjectId = new ProjectId("proj"),
            Title = "Test",
            Prompt = "p",
            State = WorkItemState.Queued,
        };
        await _factory.Store.CreateAsync(item, CancellationToken.None);
        return item;
    }

    private static WebhookEvent EventFor(WorkItem item, string name, WorkItemState state = WorkItemState.Working) => new()
    {
        Event = name,
        WorkItem = item with { State = state },
        Project = new Project
        {
            Id = item.ProjectId,
            DisplayName = "Test Project",
            RepositoryUrl = "https://github.com/test/repo",
        },
    };

    private async Task WaitForSubscribersAsync(int expected, CancellationToken ct)
    {
        var broadcaster = _factory.Services.GetRequiredService<WebhookEventBroadcaster>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (broadcaster.SubscriberCount == expected) return;
            await Task.Delay(25, ct);
        }
        Assert.Equal(expected, broadcaster.SubscriberCount);
    }

    private static async Task<SseFrame> ReadFrameAsync(StreamReader reader, CancellationToken ct)
    {
        string? id = null, eventType = null;
        var data = new System.Text.StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.Length == 0)
            {
                // End of frame. If the frame had a non-comment field, return it.
                if (id is not null || eventType is not null || data.Length > 0)
                    return new SseFrame(id ?? "", eventType ?? "", data.ToString());
                continue;
            }
            if (line.StartsWith(":", StringComparison.Ordinal)) continue;     // heartbeat
            if (line.StartsWith("id: ", StringComparison.Ordinal)) id = line[4..];
            else if (line.StartsWith("event: ", StringComparison.Ordinal)) eventType = line[7..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line[6..]);
            }
        }
        throw new InvalidOperationException("stream closed before a complete SSE frame was read");
    }

    private sealed record SseFrame(string Id, string EventType, string Data);
}

/// <summary>
/// WebApplicationFactory that bootstraps the real CodeyBox API with an
/// isolated database, no orchestrator background services, and a short
/// SSE heartbeat interval so the idle-heartbeat test doesn't wait 15s.
/// </summary>
internal sealed class SseApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-ssetest-{Guid.NewGuid():N}.db");

    public SqliteWorkItemStore Store { get; }

    public SseApiFactory() => Store = new SqliteWorkItemStore(_dbPath);

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
                ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"sse-log-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"sse-audit-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:WebhookEventBus:HeartbeatSeconds"] = "1",
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
        }
        base.Dispose(disposing);
    }
}
