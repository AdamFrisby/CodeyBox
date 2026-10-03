using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeyBox.E2bSandboxPlugin;

/// <summary>
/// Minimal E2B REST client over an injected <see cref="HttpClient"/>.
///
/// <para>Control plane (<c>{ApiBaseUrl}</c>, header <c>X-API-KEY</c>): sandbox
/// lifecycle — <c>POST /v2/sandboxes</c>, <c>GET /sandboxes/{id}</c>,
/// <c>GET /v2/sandboxes</c>, <c>DELETE /sandboxes/{id}</c>,
/// <c>POST /sandboxes/{id}/pause</c> (persistence),
/// <c>POST /v2/sandboxes/{id}/connect</c> (resume),
/// <c>POST /sandboxes/{id}/timeout</c> (keep-alive),
/// <c>POST /sandboxes/{id}/snapshots</c> (artifacts). Paths and the
/// <c>X-API-KEY</c> header are verified against the E2B JS SDK
/// (<c>e2b@2.52.0</c>); so are the create-response fields
/// (<c>sandboxID</c>, <c>domain</c>, <c>envdVersion</c>,
/// <c>envdAccessToken</c>, <c>trafficAccessToken</c>).</para>
///
/// <para>Data plane (per-sandbox envd gateway
/// <c>https://{envdPort}-{sandboxId}.{domain}</c>, header
/// <c>X-Access-Token</c> carrying the sandbox-scoped token from create — the
/// long-lived API key never rides data-plane traffic. <c>GET /files</c>,
/// <c>POST /files</c>, and <c>GET /health</c> are verified against the same
/// SDK; the synchronous <c>POST /commands</c> envelope
/// (<c>command</c>/<c>envs</c>/<c>cwd</c>/<c>timeoutMs</c> in,
/// <c>exitCode</c>/<c>stdout</c>/<c>stderr</c> out) is this plugin's recorded
/// mapping of the envd command gateway (the SDK drives commands over
/// Connect-RPC, which this plugin does not reimplement). The envelope is
/// pinned by the recorded-shape fixture and the gated live integration test;
/// re-verify it against a live sandbox before trusting a version upgrade.
/// Stale-format risks are listed in
/// <c>docs/extending/e2b-sandbox-plugin.md</c>.</para>
///
/// <para>Every method is bounded (no unbounded pagination or buffering) and
/// maps service-side failures to <see cref="E2bApiException"/> — the provider
/// converts those into the pipeline's infrastructure exceptions, never into a
/// verdict on the work item's diff.</para>
/// </summary>
internal sealed class E2bApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const int MaxErrorBodyChars = 1024;
    private const int ListPageLimit = 100;
    private const long MaxControlPlaneResponseBytes = 1L * 1024 * 1024;
    private const long MaxErrorResponseBytes = 16L * 1024;

    private readonly HttpClient _http;

    public E2bApiClient(HttpClient httpClient)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<E2bSandboxDto> CreateSandboxAsync(
        string baseUrl,
        string apiKey,
        E2bCreateSandboxRequest request,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentNullException.ThrowIfNull(request);
        using var content = JsonContent.Create(request, options: JsonOptions);
        using var response = await SendControlPlaneAsync(
            baseUrl, apiKey, HttpMethod.Post, "/v2/sandboxes", content, timeout, "create-sandbox", ct).ConfigureAwait(false);
        return await ReadJsonAsync<E2bSandboxDto>(response, "create-sandbox", ct).ConfigureAwait(false);
    }

    /// <summary>Returns null when the sandbox does not exist (404).</summary>
    public async Task<E2bSandboxDto?> GetSandboxAsync(
        string baseUrl, string apiKey, string sandboxId, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxId);
        using var response = await SendControlPlaneAsync(
            baseUrl, apiKey, HttpMethod.Get, $"/sandboxes/{Uri.EscapeDataString(sandboxId)}",
            content: null, timeout, "get-sandbox", allowNotFound: true, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return await ReadJsonAsync<E2bSandboxDto>(response, "get-sandbox", ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<E2bSandboxDto>> ListSandboxesAsync(
        string baseUrl, string apiKey, TimeSpan timeout, CancellationToken ct)
    {
        using var response = await SendControlPlaneAsync(
            baseUrl, apiKey, HttpMethod.Get, $"/v2/sandboxes?limit={ListPageLimit}",
            content: null, timeout, "list-sandboxes", ct).ConfigureAwait(false);
        return await ReadSandboxListAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>Deletes a sandbox; a missing sandbox (404) is success.</summary>
    public async Task DeleteSandboxAsync(
        string baseUrl, string apiKey, string sandboxId, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxId);
        using var response = await SendControlPlaneAsync(
            baseUrl, apiKey, HttpMethod.Delete, $"/sandboxes/{Uri.EscapeDataString(sandboxId)}",
            content: null, timeout, "delete-sandbox", allowNotFound: true, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    public async Task PauseSandboxAsync(
        string baseUrl, string apiKey, string sandboxId, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxId);
        using var response = await SendControlPlaneAsync(
            baseUrl, apiKey, HttpMethod.Post, $"/sandboxes/{Uri.EscapeDataString(sandboxId)}/pause",
            content: null, timeout, "pause-sandbox", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Resumes a paused sandbox (E2B "connect" with restore semantics).</summary>
    public async Task<E2bSandboxDto> ResumeSandboxAsync(
        string baseUrl, string apiKey, string sandboxId, int timeoutSeconds, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxId);
        var payload = new { timeout = Math.Clamp(timeoutSeconds, 60, 86400) };
        using var content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await SendControlPlaneAsync(
            baseUrl, apiKey, HttpMethod.Post, $"/v2/sandboxes/{Uri.EscapeDataString(sandboxId)}/connect",
            content, E2bApiClientWaitTimeout(timeout), "resume-sandbox", ct).ConfigureAwait(false);
        return await ReadJsonAsync<E2bSandboxDto>(response, "resume-sandbox", ct).ConfigureAwait(false);
    }

    public async Task ExtendTimeoutAsync(
        string baseUrl, string apiKey, string sandboxId, int timeoutSeconds, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxId);
        var payload = new { timeout = Math.Clamp(timeoutSeconds, 60, 86400) };
        using var content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await SendControlPlaneAsync(
            baseUrl, apiKey, HttpMethod.Post, $"/sandboxes/{Uri.EscapeDataString(sandboxId)}/timeout",
            content, timeout, "extend-timeout", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<E2bSnapshotDto> SnapshotSandboxAsync(
        string baseUrl, string apiKey, string sandboxId, string? name, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxId);
        var payload = new
        {
            name,
            metadata = new Dictionary<string, string>
            {
                [E2bSandboxOptions.ManagedMetadataKey] = E2bSandboxOptions.ManagedMetadataValue,
                [E2bSandboxOptions.ProviderMetadataKey] = E2bSandboxOptions.ProviderKind,
            },
        };
        using var content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await SendControlPlaneAsync(
            baseUrl, apiKey, HttpMethod.Post, $"/sandboxes/{Uri.EscapeDataString(sandboxId)}/snapshots",
            content, E2bApiClientWaitTimeout(timeout), "snapshot-sandbox", ct).ConfigureAwait(false);
        return await ReadJsonAsync<E2bSnapshotDto>(response, "snapshot-sandbox", ct).ConfigureAwait(false);
    }

    public async Task CheckEnvdHealthAsync(
        string envdBaseUrl, string accessToken, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envdBaseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        using var response = await SendEnvdAsync(
            envdBaseUrl, accessToken, HttpMethod.Get, "/health",
            content: null, timeout, "envd-health", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<E2bCommandResult> RunCommandAsync(
        string envdBaseUrl,
        string accessToken,
        E2bRunCommandRequest request,
        long maxResponseBytes,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envdBaseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentNullException.ThrowIfNull(request);
        using var content = JsonContent.Create(request, options: JsonOptions);
        using var response = await SendEnvdAsync(
            envdBaseUrl, accessToken, HttpMethod.Post, "/commands",
            content, timeout, "run-command", ct).ConfigureAwait(false);
        var body = await ReadBoundedAsync(response, maxResponseBytes, "run-command", ct).ConfigureAwait(false);
        try
        {
            var result = JsonSerializer.Deserialize<E2bCommandResult>(body, JsonOptions);
            if (result is null)
            {
                throw new E2bApiException(null, "malformed-response", "run-command returned an empty body");
            }

            return result;
        }
        catch (JsonException ex)
        {
            throw new E2bApiException(null, "malformed-response", "run-command returned unparseable JSON", ex);
        }
    }

    public async Task WriteFileAsync(
        string envdBaseUrl, string accessToken, string path, string contentsBase64, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contentsBase64);
        var payload = new { content = contentsBase64 };
        using var content = JsonContent.Create(payload, options: JsonOptions);
        var target = $"/files?path={Uri.EscapeDataString(path)}";
        using var response = await SendEnvdAsync(
            envdBaseUrl, accessToken, HttpMethod.Post, target,
            content, timeout, "write-file", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<byte[]> ReadFileAsync(
        string envdBaseUrl, string accessToken, string path, long maxBytes, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var target = $"/files?path={Uri.EscapeDataString(path)}";
        using var response = await SendEnvdAsync(
            envdBaseUrl, accessToken, HttpMethod.Get, target,
            content: null, timeout, "read-file", ct).ConfigureAwait(false);
        return await ReadBoundedAsync(response, maxBytes, "read-file", ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendControlPlaneAsync(
        string baseUrl,
        string apiKey,
        HttpMethod method,
        string pathAndQuery,
        HttpContent? content,
        TimeSpan timeout,
        string operation,
        CancellationToken ct)
    {
        var uri = new Uri(new Uri(EnsureTrailingSlash(baseUrl), UriKind.Absolute), pathAndQuery.TrimStart('/'));
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("X-API-KEY", apiKey);
        return await SendAsync(request, content, timeout, operation, allowNotFound: false, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendControlPlaneAsync(
        string baseUrl,
        string apiKey,
        HttpMethod method,
        string pathAndQuery,
        HttpContent? content,
        TimeSpan timeout,
        string operation,
        bool allowNotFound,
        CancellationToken ct)
    {
        var uri = new Uri(new Uri(EnsureTrailingSlash(baseUrl), UriKind.Absolute), pathAndQuery.TrimStart('/'));
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("X-API-KEY", apiKey);
        return await SendAsync(request, content, timeout, operation, allowNotFound, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendEnvdAsync(
        string envdBaseUrl,
        string accessToken,
        HttpMethod method,
        string pathAndQuery,
        HttpContent? content,
        TimeSpan timeout,
        string operation,
        CancellationToken ct)
    {
        var uri = new Uri(new Uri(EnsureTrailingSlash(envdBaseUrl), UriKind.Absolute), pathAndQuery.TrimStart('/'));
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("X-Access-Token", accessToken);
        return await SendAsync(request, content, timeout, operation, allowNotFound: false, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        HttpContent? content,
        TimeSpan timeout,
        string operation,
        bool allowNotFound,
        CancellationToken ct)
    {
        if (content is not null)
        {
            request.Content = content;
        }

        HttpResponseMessage response;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new E2bApiException(null, "timeout", $"{operation} exceeded {timeout.TotalSeconds}s", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new E2bApiException(null, "unreachable", $"{operation} transport failure: {ex.GetType().Name}", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
            {
                return response;
            }

            var body = await ReadErrorBodyAsync(response, ct).ConfigureAwait(false);
            var classification = E2bFailureClassification.Classify(response.StatusCode, operation);
            response.Dispose();
            throw new E2bApiException(response.StatusCode, classification.ErrorClass, $"{operation}: {body}");
        }

        return response;
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        try
        {
            var body = await ReadBoundedAsync(response, MaxControlPlaneResponseBytes, operation, ct).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<T>(body, JsonOptions);
            if (result is null)
            {
                throw new E2bApiException(null, "malformed-response", $"{operation} returned an empty body");
            }

            return result;
        }
        catch (E2bApiException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new E2bApiException(null, "malformed-response", $"{operation} returned unparseable JSON", ex);
        }
    }

    private static async Task<IReadOnlyList<E2bSandboxDto>> ReadSandboxListAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await ReadBoundedAsync(response, MaxControlPlaneResponseBytes, "list-sandboxes", ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                return doc.Deserialize<List<E2bSandboxDto>>(JsonOptions) ?? [];
            }

            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("sandboxes", out var items)
                && items.ValueKind == JsonValueKind.Array)
            {
                return items.Deserialize<List<E2bSandboxDto>>(JsonOptions) ?? [];
            }

            throw new E2bApiException(null, "malformed-response", "list-sandboxes returned an unexpected shape");
        }
        catch (E2bApiException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException)
        {
            throw new E2bApiException(null, "malformed-response", "list-sandboxes returned unparseable JSON", ex);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, long maxBytes, string operation, CancellationToken ct)
    {
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > maxBytes)
        {
            throw new E2bApiException(null, "oversize-response", $"{operation} declared {contentLength.Value} bytes (limit {maxBytes})");
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var sink = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (sink.Length + read > maxBytes)
            {
                throw new E2bApiException(null, "oversize-response", $"{operation} exceeded {maxBytes} bytes");
            }

            sink.Write(buffer, 0, read);
        }

        return sink.ToArray();
    }

    private static async Task<string> ReadErrorBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var bounded = await ReadBoundedAsync(response, MaxErrorResponseBytes, "error-body", ct).ConfigureAwait(false);
            var body = Encoding.UTF8.GetString(bounded);
            return body.Length > MaxErrorBodyChars ? body[..MaxErrorBodyChars] : body;
        }
        catch (E2bApiException ex) when (string.Equals(ex.ErrorClass, "oversize-response", StringComparison.Ordinal))
        {
            return "(error body exceeded bounds)";
        }
        catch (Exception)
        {
            return "(unreadable error body)";
        }
    }

    private static string EnsureTrailingSlash(string baseUrl) =>
        baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : baseUrl + "/";

    internal static TimeSpan E2bApiClientWaitTimeout(TimeSpan configured) =>
        configured < TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : configured + TimeSpan.FromSeconds(30);
}

/// <summary>Sandbox creation payload for <c>POST /v2/sandboxes</c>.</summary>
internal sealed record E2bCreateSandboxRequest
{
    [JsonPropertyName("templateID")]
    public string? TemplateID { get; init; }

    [JsonPropertyName("timeout")]
    public int Timeout { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; init; }
}

/// <summary>Recorded shape of an E2B sandbox view (create/get/list/resume).</summary>
internal sealed record E2bSandboxDto
{
    [JsonPropertyName("sandboxID")]
    public string SandboxID { get; init; } = string.Empty;

    [JsonPropertyName("templateID")]
    public string? TemplateID { get; init; }

    [JsonPropertyName("domain")]
    public string? Domain { get; init; }

    [JsonPropertyName("envdVersion")]
    public string? EnvdVersion { get; init; }

    [JsonPropertyName("envdAccessToken")]
    public string? EnvdAccessToken { get; init; }

    [JsonPropertyName("trafficAccessToken")]
    public string? TrafficAccessToken { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; init; }

    [JsonIgnore]
    public string LifecycleState => !string.IsNullOrWhiteSpace(State) ? State! : (Status ?? string.Empty);

    [JsonIgnore]
    public string Id => SandboxID;
}

/// <summary>Recorded shape of an E2B snapshot view.</summary>
internal sealed record E2bSnapshotDto
{
    [JsonPropertyName("snapshotID")]
    public string SnapshotID { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

/// <summary>
/// Synchronous envd command request. This envelope is the plugin's recorded
/// mapping of the envd command gateway — see the client doc comment for
/// provenance and staleness risks.
/// </summary>
internal sealed record E2bRunCommandRequest
{
    [JsonPropertyName("command")]
    public string Command { get; init; } = string.Empty;

    [JsonPropertyName("envs")]
    public Dictionary<string, string>? Envs { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("timeoutMs")]
    public int? TimeoutMs { get; init; }
}

/// <summary>Synchronous envd command result.</summary>
internal sealed record E2bCommandResult
{
    [JsonPropertyName("exitCode")]
    public int ExitCode { get; init; }

    [JsonPropertyName("stdout")]
    public string Stdout { get; init; } = string.Empty;

    [JsonPropertyName("stderr")]
    public string Stderr { get; init; } = string.Empty;
}
