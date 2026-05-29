using System.Net;
using System.Net.Http.Json;
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
    private readonly HttpClient? _sseHttp;
    private readonly ResolvedConfig? _lazySseConfig;
    private HttpClient? _lazySseHttp;

    internal CodeyBoxClient(HttpClient http)
        : this(http, sseHttp: null, lazySseConfig: null) { }

    internal CodeyBoxClient(HttpClient http, HttpClient sseHttp)
        : this(http, sseHttp, lazySseConfig: null) { }

    private CodeyBoxClient(HttpClient http, HttpClient? sseHttp, ResolvedConfig? lazySseConfig)
    {
        _http = http;
        _sseHttp = sseHttp;
        _lazySseConfig = lazySseConfig;
    }

    internal static CodeyBoxClient Create(ResolvedConfig config)
    {
        var http = CodeyBoxHttpFactory.CreateClient(config, TimeSpan.FromSeconds(30));
        return new CodeyBoxClient(http, sseHttp: null, lazySseConfig: config);
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

    internal async Task<SseWatchResult> TryWatchWorkItemEventsAsync(
        string id,
        Action<string> onStateTransition,
        CancellationToken ct = default)
    {
        var watcher = new WorkItemSseWatcher(GetSseHttp());
        return await watcher.WatchAsync(id, onStateTransition, ct);
    }

    private HttpClient GetSseHttp()
    {
        if (_sseHttp is not null)
            return _sseHttp;
        return _lazySseHttp ??= CodeyBoxHttpFactory.CreateClient(
            _lazySseConfig!, Timeout.InfiniteTimeSpan);
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

    internal static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        // Truncate verbose 5xx bodies to avoid leaking server internals (stack traces, hostnames).
        if ((int)resp.StatusCode >= 500 && body.Length > 200)
            body = body[..200] + "... (truncated)";
        throw new CodeyBoxApiException((int)resp.StatusCode, body);
    }
}
