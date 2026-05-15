using System.Text;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Webhooks;
using Microsoft.Extensions.Options;

namespace CodeyBox.Api;

/// <summary>
/// Server-Sent Events streams that surface pipeline events to subscribers
/// without polling. Two endpoints:
///
/// <list type="bullet">
///   <item><c>GET /workitems/{id}/events</c> — events for one work item,
///   closes when the item reaches a terminal state or the client disconnects.</item>
///   <item><c>GET /workitems/events</c> — every event across the fleet,
///   optionally filtered by <c>?projectId=</c> and/or <c>?eventType=</c>
///   (comma-separated). Stays open until the client disconnects.</item>
/// </list>
///
/// <para>Payloads use the same JSON envelope as outbound webhooks (see
/// <see cref="HttpWebhookDispatcher.BuildPayload"/>) so a webhook receiver
/// and an SSE client see identical event shapes.</para>
///
/// <para>Resume: clients reconnecting with a <c>Last-Event-ID</c> header
/// (or <c>?lastEventId=</c> query) get every event with id greater than
/// the supplied cursor that is still in the broadcaster's ring buffer.</para>
/// </summary>
internal static class SseEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/workitems");
        group.MapGet("/events", StreamAllAsync);
        group.MapGet("/{id}/events", StreamForWorkItemAsync);
    }

    private static async Task StreamForWorkItemAsync(
        HttpContext ctx,
        string id,
        IWorkItemStore store,
        WebhookEventBroadcaster broadcaster,
        IOptions<CodeyBoxOptions> opts)
    {
        var ct = ctx.RequestAborted;
        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null)
        {
            await WriteErrorAsync(ctx, err);
            return;
        }

        var filter = new SubscriptionFilter { WorkItemId = item!.Id.ToString() };
        await StreamAsync(ctx, broadcaster, filter, closeOnTerminalState: true, opts.Value.WebhookEventBus, ct);
    }

    private static async Task StreamAllAsync(
        HttpContext ctx,
        string? projectId,
        string? eventType,
        WebhookEventBroadcaster broadcaster,
        IOptions<CodeyBoxOptions> opts)
    {
        var ct = ctx.RequestAborted;
        var filter = new SubscriptionFilter
        {
            ProjectId = string.IsNullOrWhiteSpace(projectId) ? null : projectId.Trim(),
            EventTypes = ParseEventTypes(eventType),
        };
        await StreamAsync(ctx, broadcaster, filter, closeOnTerminalState: false, opts.Value.WebhookEventBus, ct);
    }

    private static async Task StreamAsync(
        HttpContext ctx,
        WebhookEventBroadcaster broadcaster,
        SubscriptionFilter filter,
        bool closeOnTerminalState,
        WebhookEventBusOptions busOptions,
        CancellationToken ct)
    {
        SetSseHeaders(ctx.Response);

        var lastEventId = ParseLastEventId(ctx.Request);
        await using var subscription = broadcaster.Subscribe(filter, lastEventId);

        // One write semaphore serialises the heartbeat path and the event
        // path so we never interleave a ':keepalive' inside an SSE frame.
        var writeGate = new SemaphoreSlim(1, 1);

        // Send an initial flush so the client knows the stream is open
        // (also flushes response headers).
        await ctx.Response.Body.FlushAsync(ct);

        var heartbeatInterval = TimeSpan.FromSeconds(Math.Max(1, busOptions.HeartbeatSeconds));
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeatTask = RunHeartbeatAsync(ctx.Response, writeGate, heartbeatInterval, heartbeatCts.Token);

        try
        {
            await foreach (var evt in subscription.ReadAsync(ct).ConfigureAwait(false))
            {
                await WriteEventAsync(ctx.Response, writeGate, evt, ct);

                if (closeOnTerminalState
                    && evt.Event.WorkItem is { } wi
                    && WorkItemDependencies.TerminalStates.Contains(wi.State))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) { /* client disconnected */ }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeatTask.ConfigureAwait(false); } catch { /* swallow */ }
            writeGate.Dispose();
        }
    }

    private static async Task RunHeartbeatAsync(HttpResponse response, SemaphoreSlim writeGate, TimeSpan interval, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await WriteRawAsync(response, writeGate, ":keepalive\n\n", ct);
        }
        catch (OperationCanceledException) { /* cancellation is normal */ }
        catch (Exception)
        {
            // Heartbeat failures (disposed response, broken pipe) are not
            // actionable: the main loop will observe the same condition
            // and exit. Swallow so the loop's CancellationToken cleanup runs.
        }
    }

    private static async Task WriteEventAsync(
        HttpResponse response,
        SemaphoreSlim writeGate,
        BroadcastedEvent evt,
        CancellationToken ct)
    {
        var payload = HttpWebhookDispatcher.BuildPayload(evt.Event);
        // SSE 'data:' fields must not contain raw LFs. The webhook payload
        // is serialised on a single line by the default JsonSerializer, but
        // be defensive — split on any embedded LF to keep each one a
        // separate data line per the SSE spec.
        var sb = new StringBuilder(payload.Length + 64);
        sb.Append("id: ").Append(evt.SequenceId).Append('\n');
        sb.Append("event: ").Append(evt.Event.Event).Append('\n');
        foreach (var line in payload.Split('\n'))
            sb.Append("data: ").Append(line).Append('\n');
        sb.Append('\n');
        await WriteRawAsync(response, writeGate, sb.ToString(), ct);
    }

    private static async Task WriteRawAsync(HttpResponse response, SemaphoreSlim writeGate, string text, CancellationToken ct)
    {
        await writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await response.Body.WriteAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
            await response.Body.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private static void SetSseHeaders(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        // Don't keep the response in any buffering layer between handler and socket.
        var bufferFeature = response.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
        bufferFeature?.DisableBuffering();
    }

    private static long? ParseLastEventId(HttpRequest request)
    {
        // Header takes precedence (per SSE spec, set automatically by EventSource clients).
        if (request.Headers.TryGetValue("Last-Event-ID", out var headerVals)
            && long.TryParse(headerVals.ToString(), out var headerId))
            return headerId;
        if (request.Query.TryGetValue("lastEventId", out var queryVals)
            && long.TryParse(queryVals.ToString(), out var queryId))
            return queryId;
        return null;
    }

    private static IReadOnlyList<string>? ParseEventTypes(string? eventType)
    {
        if (string.IsNullOrWhiteSpace(eventType)) return null;
        var parts = eventType.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : parts;
    }

    private static async Task WriteErrorAsync(HttpContext ctx, IResult err)
    {
        // Bridge an IResult that the resolver returned (e.g. 404 NotFound)
        // through to the response when we can't return IResult directly
        // because the handler signature is 'Task' (the SSE handler writes
        // the response body itself, so it must use HttpContext directly).
        await err.ExecuteAsync(ctx);
    }

    private static async Task<(WorkItem? item, IResult? error)> ResolveWorkItemAsync(
        string idSegment,
        IWorkItemStore store,
        CancellationToken ct)
    {
        if (idSegment.Contains(':'))
        {
            var colonIdx = idSegment.IndexOf(':', StringComparison.Ordinal);
            var projectPart = idSegment[..colonIdx];
            var externalPart = idSegment[(colonIdx + 1)..];
            if (string.IsNullOrEmpty(projectPart) || string.IsNullOrEmpty(externalPart))
                return (null, Results.BadRequest(new { error = "composite id format requires non-empty projectId and externalId: '<projectId>:<externalId>'" }));
            ProjectId pid;
            try { pid = new ProjectId(projectPart); }
            catch (ArgumentException ex) { return (null, Results.BadRequest(new { error = ex.Message })); }
            try { Validation.ValidateExternalId(externalPart, "externalId"); }
            catch (ArgumentException ex) { return (null, Results.BadRequest(new { error = ex.Message })); }
            var byExtId = await store.GetByExternalIdAsync(pid, externalPart, ct);
            return byExtId is null ? (null, Results.NotFound()) : (byExtId, null);
        }

        if (!Guid.TryParse(idSegment, out var g))
            return (null, Results.BadRequest(new { error = "invalid id" }));
        var byId = await store.GetAsync(new WorkItemId(g), ct);
        return byId is null ? (null, Results.NotFound()) : (byId, null);
    }
}
