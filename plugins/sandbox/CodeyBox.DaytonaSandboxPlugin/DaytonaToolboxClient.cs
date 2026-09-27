using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeyBox.DaytonaSandboxPlugin;

/// <summary>
/// REST client for the per-sandbox Daytona toolbox daemon, reached through the
/// toolbox proxy at <c>{toolboxProxyUrl}/{sandboxId}/...</c>. Session exec and
/// file endpoints are documented in the Daytona toolbox (gin) API. Every
/// non-2xx becomes a <see cref="DaytonaApiException"/> so callers classify
/// service failures without scraping status codes.
/// </summary>
internal sealed class DaytonaToolboxClient
{
    private static readonly JsonSerializerOptions Json = DaytonaApiClient.Json;
    private const int MaxErrorBodyChars = 2048;
    private const int DownloadChunkBytes = 64 * 1024;

    private readonly HttpClient _http;
    private readonly DaytonaEndpoint _endpoint;
    private readonly Uri _sandboxBase;

    /// <summary>
    /// <paramref name="toolboxBaseUri"/> is the resolved toolbox URL for ONE
    /// sandbox: <c>{proxyBase}/{sandboxId}</c>. The sandbox id is always
    /// provider-generated (codeybox-&lt;guid&gt;), never caller-supplied
    /// metadata — it is still escaped when composed into the request URI.
    /// </summary>
    public DaytonaToolboxClient(HttpClient http, DaytonaEndpoint endpoint, Uri toolboxBaseUri)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _sandboxBase = toolboxBaseUri ?? throw new ArgumentNullException(nameof(toolboxBaseUri));
        if (_sandboxBase.Scheme != Uri.UriSchemeHttp && _sandboxBase.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Toolbox base URI must be http(s).", nameof(toolboxBaseUri));
        if (_sandboxBase.Scheme == Uri.UriSchemeHttp && !DaytonaApiClient.IsCleartextHttpPermitted(_sandboxBase, endpoint.AllowUnsafeHttp))
            throw new ArgumentException(
                "Toolbox base URI uses cleartext http that is not permitted; " +
                "the API key rides every toolbox request, so https is required outside loopback tests " +
                "(AllowUnsafeHttp permits http only for loopback hosts).",
                nameof(toolboxBaseUri));
    }

    public Uri SandboxBaseUri => _sandboxBase;

    /// <summary>The ws(s) URI for the command log stream of one exec.</summary>
    public Uri BuildLogStreamUri(string sessionId, string commandId)
    {
        var path = $"process/session/{Uri.EscapeDataString(sessionId)}/command/{Uri.EscapeDataString(commandId)}/logs?follow=true";
        var uri = new Uri(_sandboxBase, path);
        var scheme = uri.Scheme == Uri.UriSchemeHttp ? "ws" : "wss";
        var builder = new UriBuilder(uri) { Scheme = scheme };
        if (builder.Port == 80 || builder.Port == 443)
            builder.Port = -1;
        return builder.Uri;
    }

    public async Task CreateSessionAsync(string sessionId, CancellationToken ct)
    {
        using var response = await SendJsonAsync(
            HttpMethod.Post, "process/session", new DaytonaCreateSessionRequest(sessionId), ct).ConfigureAwait(false);
        await DaytonaApiClient.EnsureSuccessAsync(response, "create session", ct).ConfigureAwait(false);
    }

    /// <summary>DELETE /process/session/{id} — kills every command running in the session. 404 = already gone.</summary>
    public async Task<bool> DeleteSessionAsync(string sessionId, CancellationToken ct)
    {
        using var response = await SendAsync(
            HttpMethod.Delete, $"process/session/{Uri.EscapeDataString(sessionId)}", null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        await DaytonaApiClient.EnsureSuccessAsync(response, "delete session", ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>POST /process/session/{id}/exec with runAsync=true. Returns the command id.</summary>
    public async Task<string> ExecuteSessionCommandAsync(string sessionId, string command, CancellationToken ct)
    {
        using var response = await SendJsonAsync(
            HttpMethod.Post,
            $"process/session/{Uri.EscapeDataString(sessionId)}/exec",
            new DaytonaSessionExecuteRequest(command),
            ct).ConfigureAwait(false);
        var dto = await ReadJsonAsync<DaytonaSessionExecuteResponse>(response, "exec session command", ct).ConfigureAwait(false);
        if (dto is null || string.IsNullOrWhiteSpace(dto.CmdId))
            throw new DaytonaApiException(DaytonaFailureKind.Unexpected, "exec session command", "response carried no cmdId");
        return dto.CmdId;
    }

    /// <summary>POST /process/session/{sid}/command/{cid}/input — appends bytes to the command's stdin.</summary>
    public async Task SendSessionInputAsync(string sessionId, string commandId, string data, CancellationToken ct)
    {
        using var response = await SendJsonAsync(
            HttpMethod.Post,
            $"process/session/{Uri.EscapeDataString(sessionId)}/command/{Uri.EscapeDataString(commandId)}/input",
            new DaytonaSessionInputRequest(data),
            ct).ConfigureAwait(false);
        await DaytonaApiClient.EnsureSuccessAsync(response, "send session input", ct).ConfigureAwait(false);
    }

    /// <summary>GET /process/session/{sid}/command/{cid}. Null exitCode = still running.</summary>
    public async Task<DaytonaSessionCommand?> GetSessionCommandAsync(
        string sessionId, string commandId, CancellationToken ct)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"process/session/{Uri.EscapeDataString(sessionId)}/command/{Uri.EscapeDataString(commandId)}",
            null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        return await ReadJsonAsync<DaytonaSessionCommand>(response, "get session command", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// GET /process/session/{sid}/command/{cid}/logs without follow: snapshot of
    /// the command's output. Newer daemons return JSON ({stdout, stderr,
    /// output}); older ones return plain combined text — callers handle both.
    /// </summary>
    /// <param name="maxBytes">
    /// Upper bound on the raw response body. Guest output is untrusted and
    /// unbounded, so the body is streamed through a byte ceiling (mirroring
    /// <see cref="DownloadFileAsync"/>) before UTF-8 decoding — it is never
    /// buffered with an unbounded <c>ReadAsStringAsync</c>.
    /// </param>
    public async Task<DaytonaCommandLogs> GetSessionCommandLogsAsync(
        string sessionId, string commandId, long maxBytes, CancellationToken ct)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"process/session/{Uri.EscapeDataString(sessionId)}/command/{Uri.EscapeDataString(commandId)}/logs",
            null, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            await DaytonaApiClient.EnsureSuccessAsync(response, "get session command logs", ct).ConfigureAwait(false);

        var bodyBytes = await ReadBoundedAsync(response.Content, maxBytes, "get session command logs", "logs content", ct)
            .ConfigureAwait(false);
        var body = Encoding.UTF8.GetString(bodyBytes);

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var dto = JsonSerializer.Deserialize<DaytonaSessionCommandLogs>(body, Json);
                return new DaytonaCommandLogs(dto?.Stdout, dto?.Stderr, dto?.Output);
            }
            catch (JsonException ex)
            {
                throw new DaytonaApiException(
                    DaytonaFailureKind.Unexpected, "get session command logs", "unparseable JSON logs body", null, null, ex);
            }
        }
        // Plain-text daemons return one combined stream; attribute it to stdout
        // (the dominant channel) rather than fabricating a stderr split.
        return new DaytonaCommandLogs(body, null, body);
    }

    /// <summary>GET /files/info?path=. Returns null when the path does not exist (404).</summary>
    public async Task<DaytonaFileInfo?> GetFileInfoAsync(string sandboxPath, CancellationToken ct)
    {
        using var response = await SendAsync(
            HttpMethod.Get, $"files/info?path={Uri.EscapeDataString(sandboxPath)}", null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        return await ReadJsonAsync<DaytonaFileInfo>(response, "get file info", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// GET /files/download?path= — raw file bytes. Callers bound the buffered
    /// length before materialising (the response is untrusted remote input).
    /// Returns null on 404.
    /// </summary>
    public async Task<byte[]?> DownloadFileAsync(string sandboxPath, long maxBytes, CancellationToken ct)
    {
        using var response = await SendAsync(
            HttpMethod.Get, $"files/download?path={Uri.EscapeDataString(sandboxPath)}", null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await DaytonaApiClient.EnsureSuccessAsync(response, "download file", ct).ConfigureAwait(false);
        return await ReadBoundedAsync(response.Content, maxBytes, "download file", "file content", ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Streams an untrusted response body through a byte ceiling before
    /// materialising it: the declared <c>Content-Length</c> is checked first
    /// (fail fast on a lying-large header), then every chunk is counted so a
    /// missing or lying header cannot blow the bound either.
    /// </summary>
    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content, long maxBytes, string operation, string what, CancellationToken ct)
    {
        if (content.Headers.ContentLength is { } known && known > maxBytes)
        {
            throw new DaytonaApiException(
                DaytonaFailureKind.Unexpected, operation,
                $"{what} content length {known} exceeds the {maxBytes}-byte bound");
        }
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[DownloadChunkBytes];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new DaytonaApiException(
                    DaytonaFailureKind.Unexpected, operation,
                    $"{what} content exceeded the {maxBytes}-byte bound");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string path, object body, CancellationToken ct) =>
        SendAsync(method, path, new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"), ct);

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        var uri = new Uri(_sandboxBase, path);
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _endpoint.ApiKey);
        if (!string.IsNullOrEmpty(_endpoint.OrganizationId))
            request.Headers.Add("X-Daytona-Organization-ID", _endpoint.OrganizationId);

        try
        {
            return await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new DaytonaApiException(DaytonaFailureKind.Unreachable, $"{method.Method} {path}", "transport error", null, null, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new DaytonaApiException(DaytonaFailureKind.Unreachable, $"{method.Method} {path}", "request timed out", null, null, ex);
        }
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, string operation, CancellationToken ct)
        where T : class
    {
        await DaytonaApiClient.EnsureSuccessAsync(response, operation, ct).ConfigureAwait(false);
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new DaytonaApiException(DaytonaFailureKind.Unexpected, operation, "unparseable response body", null, null, ex);
        }
        catch (NotSupportedException ex)
        {
            throw new DaytonaApiException(DaytonaFailureKind.Unexpected, operation, "unsupported response body", null, null, ex);
        }
    }
}

/// <summary>A detached view of one command's output snapshot.</summary>
internal sealed record DaytonaCommandLogs(string? Stdout, string? Stderr, string? Output);

internal sealed class DaytonaCreateSessionRequest
{
    [JsonPropertyName("sessionId")] public string SessionId { get; }
    public DaytonaCreateSessionRequest(string sessionId) => SessionId = sessionId;
}

internal sealed class DaytonaSessionExecuteRequest
{
    [JsonPropertyName("command")] public string Command { get; }
    [JsonPropertyName("runAsync")] public bool RunAsync => true;
    [JsonPropertyName("suppressInputEcho")] public bool SuppressInputEcho => true;
    public DaytonaSessionExecuteRequest(string command) => Command = command;
}

internal sealed class DaytonaSessionExecuteResponse
{
    [JsonPropertyName("cmdId")] public string? CmdId { get; set; }
    [JsonPropertyName("exitCode")] public int? ExitCode { get; set; }
    [JsonPropertyName("stdout")] public string? Stdout { get; set; }
    [JsonPropertyName("stderr")] public string? Stderr { get; set; }
}

internal sealed class DaytonaSessionInputRequest
{
    [JsonPropertyName("data")] public string Data { get; }
    public DaytonaSessionInputRequest(string data) => Data = data;
}

internal sealed class DaytonaSessionCommand
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("command")] public string? Command { get; set; }
    [JsonPropertyName("exitCode")] public int? ExitCode { get; set; }
}

internal sealed class DaytonaSessionCommandLogs
{
    [JsonPropertyName("stdout")] public string? Stdout { get; set; }
    [JsonPropertyName("stderr")] public string? Stderr { get; set; }
    [JsonPropertyName("output")] public string? Output { get; set; }
}

internal sealed class DaytonaFileInfo
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("size")] public long? Size { get; set; }
    [JsonPropertyName("isDir")] public bool IsDir { get; set; }
}
