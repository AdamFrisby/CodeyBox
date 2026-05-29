using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Cli.Models;

namespace CodeyBox.Cli.Services;

internal sealed class CodeyBoxApiException(int statusCode, string body)
    : Exception($"HTTP {statusCode}: {body}")
{
    internal int StatusCode { get; } = statusCode;
}

internal sealed class CodeyBoxClient
{
    private readonly HttpClient _http;
    private readonly HttpClient _sseHttp;

    internal CodeyBoxClient(HttpClient http) : this(http, http) { }

    internal CodeyBoxClient(HttpClient http, HttpClient sseHttp)
    {
        _http = http;
        _sseHttp = sseHttp;
    }

    internal static CodeyBoxClient Create(ResolvedConfig config)
    {
        var http = CreateHttpClient(config, TimeSpan.FromSeconds(30));
        var sseHttp = CreateHttpClient(config, Timeout.InfiniteTimeSpan);
        return new CodeyBoxClient(http, sseHttp);
    }

    private static HttpClient CreateHttpClient(ResolvedConfig config, TimeSpan timeout)
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri(config.ApiBaseUrl),
            Timeout = timeout,
        };
        if (!string.IsNullOrEmpty(config.ApiKey))
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", config.ApiKey);
        return http;
    }

    internal async Task<List<WorkItemDto>> GetWorkItemsAsync(
        string? project = null, string? state = null, int? limit = null,
        CancellationToken ct = default)
    {
        var parts = new List<string>();
        if (project is not null) parts.Add($"project={Uri.EscapeDataString(project)}");
        if (state is not null) parts.Add($"state={Uri.EscapeDataString(state)}");
        if (limit is not null) parts.Add($"limit={limit}");
        var qs = parts.Count > 0 ? "?" + string.Join("&", parts) : "";

        var resp = await _http.GetAsync($"/workitems{qs}", ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync(CliJsonContext.Default.ListWorkItemDto, ct) ?? [];
    }

    internal async Task<WorkItemDto?> GetWorkItemAsync(string id, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/workitems/{Uri.EscapeDataString(id)}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync(CliJsonContext.Default.WorkItemDto, ct);
    }

    internal async Task<WorkItemDto> CreateWorkItemAsync(CreateWorkItemRequest req, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("/workitems", req, CliJsonContext.Default.CreateWorkItemRequest, ct);
        await EnsureSuccessAsync(resp, ct);
        return (await resp.Content.ReadFromJsonAsync(CliJsonContext.Default.WorkItemDto, ct))!;
    }

    internal async Task DeleteWorkItemAsync(string id, CancellationToken ct = default)
    {
        var resp = await _http.DeleteAsync($"/workitems/{Uri.EscapeDataString(id)}", ct);
        await EnsureSuccessAsync(resp, ct);
    }

    /// <summary>
    /// Watches a work item via SSE (<c>GET /workitems/{id}/events</c>).
    /// </summary>
    internal async Task<SseWatchResult> TryWatchWorkItemEventsAsync(
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

    internal async Task<WorkItemDto> RetryWorkItemAsync(string id, string? from = null, CancellationToken ct = default)
    {
        var path = $"/workitems/{Uri.EscapeDataString(id)}/retry";
        var requestedFrom = string.IsNullOrWhiteSpace(from) ? null : from;
        var resp = requestedFrom is null
            ? await _http.PostAsync(path, content: null, ct)
            : await _http.PostAsJsonAsync(
                path,
                new RetryRequest { From = requestedFrom },
                CliJsonContext.Default.RetryRequest,
                ct);
        await EnsureSuccessAsync(resp, ct);
        return (await resp.Content.ReadFromJsonAsync(CliJsonContext.Default.WorkItemDto, ct))!;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        // Truncate verbose 5xx bodies to avoid leaking server internals (stack traces, hostnames).
        if ((int)resp.StatusCode >= 500 && body.Length > 200)
            body = body[..200] + "... (truncated)";
        throw new CodeyBoxApiException((int)resp.StatusCode, body);
    }
}
