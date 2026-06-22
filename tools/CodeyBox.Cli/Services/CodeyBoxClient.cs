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
    internal static TimeSpan SseResponseHeaderTimeout { get; set; } = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly HttpClient? _sseHttp;
    private readonly ResolvedConfig? _lazySseConfig;
    private readonly ResolvedConfig? _config;
    private HttpClient? _lazySseHttp;

    internal CodeyBoxClient(HttpClient http)
        : this(http, sseHttp: null, lazySseConfig: null, config: null) { }

    internal CodeyBoxClient(HttpClient http, HttpClient sseHttp)
        : this(http, sseHttp, lazySseConfig: null, config: null) { }

    internal CodeyBoxClient(HttpClient http, HttpClient sseHttp, ResolvedConfig config)
        : this(http, sseHttp, lazySseConfig: null, config: config) { }

    internal CodeyBoxClient(HttpClient http, ResolvedConfig config)
        : this(http, sseHttp: null, lazySseConfig: null, config: config) { }

    private CodeyBoxClient(
        HttpClient http,
        HttpClient? sseHttp,
        ResolvedConfig? lazySseConfig,
        ResolvedConfig? config)
    {
        _http = http;
        _sseHttp = sseHttp;
        _lazySseConfig = lazySseConfig;
        _config = config;
    }

    internal static CodeyBoxClient Create(ResolvedConfig config)
    {
        var http = CodeyBoxHttpFactory.CreateClient(config, TimeSpan.FromSeconds(30));
        return new CodeyBoxClient(http, sseHttp: null, lazySseConfig: config, config: config);
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

        var resp = await SendAsync(token => _http.GetAsync($"/workitems{qs}", token), ct);
        await HttpResponseGuards.EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync(CliJsonContext.Default.ListWorkItemDto, ct) ?? [];
    }

    internal async Task<WorkItemDto?> GetWorkItemAsync(string id, CancellationToken ct = default)
    {
        var resp = await SendAsync(token => _http.GetAsync($"/workitems/{Uri.EscapeDataString(id)}", token), ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        await HttpResponseGuards.EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync(CliJsonContext.Default.WorkItemDto, ct);
    }

    internal async Task<WorkItemDto> CreateWorkItemAsync(CreateWorkItemRequest req, CancellationToken ct = default)
    {
        var resp = await SendAsync(
            token => _http.PostAsJsonAsync("/workitems", req, CliJsonContext.Default.CreateWorkItemRequest, token),
            ct);
        await HttpResponseGuards.EnsureSuccessAsync(resp, ct);
        return (await resp.Content.ReadFromJsonAsync(CliJsonContext.Default.WorkItemDto, ct))!;
    }

    internal async Task<QueueTemplateResponse> QueueTemplateAsync(QueueTemplateRequest req, CancellationToken ct = default)
    {
        var resp = await SendAsync(
            token => _http.PostAsJsonAsync("/templates/queue", req, CliJsonContext.Default.QueueTemplateRequest, token),
            ct);
        await HttpResponseGuards.EnsureSuccessAsync(resp, ct);
        return (await resp.Content.ReadFromJsonAsync(CliJsonContext.Default.QueueTemplateResponse, ct))!;
    }

    internal async Task DeleteWorkItemAsync(string id, CancellationToken ct = default)
    {
        var resp = await SendAsync(token => _http.DeleteAsync($"/workitems/{Uri.EscapeDataString(id)}", token), ct);
        await HttpResponseGuards.EnsureSuccessAsync(resp, ct);
    }

    internal async Task<SseWatchResult> TryWatchWorkItemEventsAsync(
        string id,
        Action<string> onStateTransition,
        CancellationToken ct = default)
    {
        var watcher = _config is null
            ? new WorkItemSseWatcher(GetSseHttp())
            : new WorkItemSseWatcher(SendSseRequestAsync);
        return await watcher.WatchAsync(id, onStateTransition, ct);
    }

    private async Task<HttpResponseMessage> SendSseRequestAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerCts.CancelAfter(SseResponseHeaderTimeout);

        return await SendAsync(
            token => GetSseHttp().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token),
            headerCts.Token,
            ct);
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
            ? await SendAsync(token => _http.PostAsync(path, null, token), ct)
            : await SendAsync(
                token => _http.PostAsJsonAsync(
                    path,
                    new RetryRequest { From = requestedFrom },
                    CliJsonContext.Default.RetryRequest,
                    token),
                ct);
        await HttpResponseGuards.EnsureSuccessAsync(resp, ct);
        return (await resp.Content.ReadFromJsonAsync(CliJsonContext.Default.WorkItemDto, ct))!;
    }

    internal async Task<string> PostWorkItemVerbAsync(string id, string verb, CancellationToken ct = default)
    {
        var resp = await SendAsync(
            token => _http.PostAsync(
                $"/workitems/{Uri.EscapeDataString(id)}/{verb}",
                null,
                token),
            ct);
        await HttpResponseGuards.EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    internal async Task PauseQueueAsync(string reason, CancellationToken ct = default)
    {
        var resp = await SendAsync(
            token => _http.PostAsJsonAsync(
                "/queue/pause",
                new PauseQueueRequest { Reason = reason },
                CliJsonContext.Default.PauseQueueRequest,
                token),
            ct);
        await HttpResponseGuards.EnsureSuccessAsync(resp, ct);
    }

    internal async Task ResumeQueueAsync(CancellationToken ct = default)
    {
        var resp = await SendAsync(token => _http.PostAsync("/queue/resume", null, token), ct);
        await HttpResponseGuards.EnsureSuccessAsync(resp, ct);
    }

    internal async Task<string> GetQueueStatusAsync(CancellationToken ct = default)
    {
        return await GetRawAsync("/queue/status", ct);
    }

    internal async Task PauseAgentAsync(
        string kind,
        string reason,
        double? durationSeconds = null,
        CancellationToken ct = default)
    {
        var resp = await SendAsync(
            token => _http.PostAsJsonAsync(
                AgentPausePath(kind, "pause"),
                new PauseAgentRequest { Reason = reason, DurationSeconds = durationSeconds },
                CliJsonContext.Default.PauseAgentRequest,
                token),
            ct);
        await HttpResponseGuards.EnsureSuccessAsync(resp, ct);
    }

    internal async Task ResumeAgentAsync(string kind, CancellationToken ct = default)
    {
        var resp = await SendAsync(token => _http.PostAsync(AgentPausePath(kind, "resume"), null, token), ct);
        await HttpResponseGuards.EnsureSuccessAsync(resp, ct);
    }

    private static string AgentPausePath(string routeKeyOrKind, string action)
    {
        var value = routeKeyOrKind.Trim();
        var slash = value.IndexOf('/');
        if (slash <= 0 || slash == value.Length - 1)
            return $"/agents/{Uri.EscapeDataString(value)}/{action}";

        var kind = value[..slash];
        var instanceId = value[(slash + 1)..];
        return $"/agents/{Uri.EscapeDataString(kind)}/instances/{Uri.EscapeDataString(instanceId)}/{action}";
    }

    internal async Task<string> GetPausedAgentsAsync(CancellationToken ct = default)
    {
        return await GetRawAsync("/agents/paused", ct);
    }

    internal async Task<string> GetWorkersAsync(CancellationToken ct = default)
    {
        return await GetRawAsync("/workers", ct);
    }

    internal async Task<string> GetWorkerStatusAsync(CancellationToken ct = default)
    {
        return await GetRawAsync("/workers/status", ct);
    }

    internal async Task<string> GetQuotaAsync(CancellationToken ct = default)
    {
        return await GetRawAsync("/quota", ct);
    }

    internal async Task<string> GetConcurrencyAsync(CancellationToken ct = default)
    {
        return await GetRawAsync("/concurrency", ct);
    }

    internal async Task<string> GetFleetSummaryAsync(CancellationToken ct = default)
    {
        return await GetRawAsync("/fleet/summary", ct);
    }

    internal async Task ReorderQueueAsync(string[] ids, CancellationToken ct = default)
    {
        var resp = await SendAsync(
            token => _http.PostAsJsonAsync(
                "/workitems/reorder",
                new ReorderRequest { Ids = ids },
                CliJsonContext.Default.ReorderRequest,
                token),
            ct);
        await HttpResponseGuards.EnsureSuccessAsync(resp, ct);
    }

    private async Task<string> GetRawAsync(string path, CancellationToken ct)
    {
        var resp = await SendAsync(token => _http.GetAsync(path, token), ct);
        await HttpResponseGuards.EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken ct) =>
        await SendAsync(send, ct, ct);

    private async Task<HttpResponseMessage> SendAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken sendCt,
        CancellationToken callerCt)
    {
        try
        {
            return await send(sendCt);
        }
        catch (HttpRequestException ex) when (_config is not null)
        {
            throw new CodeyBoxConnectionException(
                CliConnectionDiagnostics.FormatConnectionFailure(_config, ex),
                ex);
        }
        catch (OperationCanceledException ex) when (!callerCt.IsCancellationRequested && _config is not null)
        {
            throw new CodeyBoxConnectionException(
                CliConnectionDiagnostics.FormatConnectionFailure(_config, ex),
                ex);
        }
    }
}
