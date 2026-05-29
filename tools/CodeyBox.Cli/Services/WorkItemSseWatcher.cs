using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CodeyBox.Cli.Models;

namespace CodeyBox.Cli.Services;

/// <summary>
/// SSE client for <c>GET /workitems/{id}/events</c> (long-lived read, separate HttpClient).
/// </summary>
internal sealed class WorkItemSseWatcher
{
    internal const int MaxDataPayloadBytes = 64 * 1024;

    private readonly HttpClient _sseHttp;

    internal WorkItemSseWatcher(HttpClient sseHttp) => _sseHttp = sseHttp;

    internal static HttpClient CreateHttpClient(ResolvedConfig config) =>
        CodeyBoxClient.CreateHttpClient(config, Timeout.InfiniteTimeSpan);

    internal async Task<SseWatchResult> WatchAsync(
        string id,
        Action<string> onStateTransition,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Get, $"/workitems/{Uri.EscapeDataString(id)}/events");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage resp;
        try
        {
            resp = await _sseHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException)
        {
            return SseWatchResult.ShouldFallback;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return SseWatchResult.ShouldFallback;
        }

        if (!resp.IsSuccessStatusCode)
        {
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                await CodeyBoxClient.EnsureSuccessAsync(resp, ct);

            resp.Dispose();
            return SseWatchResult.ShouldFallback;
        }

        try
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            string? lastState = null;

            while (!ct.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (IOException)
                {
                    return SseWatchResult.ShouldFallback;
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    return SseWatchResult.ShouldFallback;
                }

                if (line is null)
                    break;

                if (!line.StartsWith("data: ", StringComparison.Ordinal))
                    continue;

                var json = line["data: ".Length..];
                if (string.IsNullOrWhiteSpace(json))
                    continue;

                if (json.Length > MaxDataPayloadBytes)
                    return SseWatchResult.ShouldFallback;

                if (!TryParseWorkItemState(json, out var state) || state is null)
                    continue;

                if (state != lastState)
                {
                    onStateTransition(state);
                    lastState = state;
                }

                if (WorkItemDto.IsTerminalState(state))
                    return SseWatchResult.Completed;
            }

            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);

            return SseWatchResult.ShouldFallback;
        }
        finally
        {
            resp.Dispose();
        }
    }

    private static bool TryParseWorkItemState(string json, out string? state)
    {
        state = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("workItem", out var workItem)
                || !workItem.TryGetProperty("state", out var stateEl))
                return false;

            state = stateEl.GetString();
            return state is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
