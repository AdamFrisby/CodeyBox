using System.Net;
using System.Net.Http.Json;using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeyBox.BlaxelPlugin;

/// <summary>
/// Minimal Blaxel control-plane client over an injected <see cref="HttpClient"/>.
/// Covers the sandbox lifecycle the provider needs: create, get, list, delete.
/// Every method is bounded (no unbounded pagination or buffering) and maps
/// service-side failures to <see cref="BlaxelApiException"/> — the provider
/// converts those into the pipeline's infrastructure exceptions, never into a
/// verdict on the work item's diff.
///
/// <para>Shapes follow the published Blaxel control-plane contract
/// (<c>https://api.blaxel.ai/v0</c>, <c>/sandboxes</c> CRUD with bearer auth
/// plus the workspace header): <c>metadata.name</c> (≤49 chars,
/// lowercase alnum + hyphens), <c>metadata.url</c> (auto-generated sandbox
/// endpoint), <c>spec.runtime.image</c>, <c>spec.runtime.memory</c> (MB),
/// <c>spec.region</c>, <c>state</c> (<c>RUNNING</c>/<c>STANDBY</c>),
/// <c>status</c> (<c>DEPLOYED</c>, …). List responses are accepted both
/// wrapped (<c>{data, meta}</c> with cursor pagination) and as a bare array.
/// See <c>Fixtures/blaxel</c> for the recorded shapes.</para>
/// </summary>
internal sealed class BlaxelControlPlaneClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public BlaxelControlPlaneClient(HttpClient httpClient)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<BlaxelSandboxView> CreateSandboxAsync(
        string baseUrl,
        BlaxelCredentials credentials,
        BlaxelSandboxCreateRequest request,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(request);
        using var content = JsonContent.Create(request, options: JsonOptions);
        using var response = await SendAsync(
            baseUrl, credentials, HttpMethod.Post, "/sandboxes", content, timeout, "create-sandbox", ct).ConfigureAwait(false);
        return await ReadJsonAsync<BlaxelSandboxView>(response, "create-sandbox", ct).ConfigureAwait(false);
    }

    public async Task<BlaxelSandboxView> GetSandboxAsync(
        string baseUrl, BlaxelCredentials credentials, string name, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using var response = await SendAsync(
            baseUrl, credentials, HttpMethod.Get, $"/sandboxes/{Uri.EscapeDataString(name)}",
            content: null, timeout, "get-sandbox", ct).ConfigureAwait(false);
        return await ReadJsonAsync<BlaxelSandboxView>(response, "get-sandbox", ct).ConfigureAwait(false);
    }

    public async Task<BlaxelSandboxListPage> ListSandboxesAsync(
        string baseUrl,
        BlaxelCredentials credentials,
        int limit,
        string? cursor,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var query = new StringBuilder("/sandboxes?limit=");
        query.Append(Math.Clamp(limit, 1, 1000));
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            query.Append("&cursor=");
            query.Append(Uri.EscapeDataString(cursor));
        }

        using var response = await SendAsync(
            baseUrl, credentials, HttpMethod.Get, query.ToString(), content: null, timeout, "list-sandboxes", ct).ConfigureAwait(false);
        var body = await ReadBodyAsync(response, "list-sandboxes", ct).ConfigureAwait(false);
        return BlaxelSandboxListPage.Parse(body);
    }

    public async Task DeleteSandboxAsync(
        string baseUrl, BlaxelCredentials credentials, string name, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using var response = await SendAsync(
            baseUrl, credentials, HttpMethod.Delete, $"/sandboxes/{Uri.EscapeDataString(name)}",
            content: null, timeout, "delete-sandbox", ct).ConfigureAwait(false);
        await DrainAsync(response, "delete-sandbox", ct).ConfigureAwait(false);
    }

    internal async Task<HttpResponseMessage> SendAsync(
        string baseUrl,
        BlaxelCredentials credentials,
        HttpMethod method,
        string pathAndQuery,
        HttpContent? content,
        TimeSpan timeout,
        string operation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var requestUri = Combine(baseUrl, pathAndQuery);
        using var request = new HttpRequestMessage(method, requestUri);
        request.Headers.TryAddWithoutValidation("x-blaxel-authorization", $"Bearer {credentials.ApiKey}");
        request.Headers.TryAddWithoutValidation("x-blaxel-workspace", credentials.Workspace);
        if (content is not null)
        {
            request.Content = content;
        }

        HttpResponseMessage response;
        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new BlaxelApiException(null, "timeout", $"{operation}: request timed out after {timeout.TotalSeconds:0}s", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new BlaxelApiException(null, "unreachable", $"{operation}: transport failure: {TrimMessage(ex.Message)}", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var detail = await ReadErrorDetailAsync(response, ct).ConfigureAwait(false);
            response.Dispose();
            throw new BlaxelApiException(
                response.StatusCode,
                ClassifyStatus(response.StatusCode),
                $"{operation}: {detail}");
        }

        return response;
    }

    internal static string Combine(string baseUrl, string pathAndQuery)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(pathAndQuery);
        return baseUrl.TrimEnd('/') + "/" + pathAndQuery.TrimStart('/');
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        var body = await ReadBodyAsync(response, operation, ct).ConfigureAwait(false);
        try
        {
            var value = JsonSerializer.Deserialize<T>(body, JsonOptions);
            if (value is null)
            {
                throw new BlaxelApiException(null, "malformed-response", $"{operation}: empty JSON body");
            }

            return value;
        }
        catch (JsonException ex)
        {
            throw new BlaxelApiException(null, "malformed-response", $"{operation}: invalid JSON: {TrimMessage(ex.Message)}", ex);
        }
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        const long MaxBodyBytes = 4L * 1024 * 1024;
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var sink = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (sink.Length + read > MaxBodyBytes)
                {
                    throw new BlaxelApiException(null, "malformed-response", $"{operation}: response body exceeds the read bound");
                }

                sink.Write(buffer, 0, read);
            }

            sink.Position = 0;
            using var reader = new StreamReader(sink, Encoding.UTF8);
            return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }
        catch (BlaxelApiException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            throw new BlaxelApiException(null, "unreachable", $"{operation}: failed reading the response body", ex);
        }
    }

    private static async Task DrainAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        _ = await ReadBodyAsync(response, operation, ct).ConfigureAwait(false);
    }

    private static async Task<string> ReadErrorDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await ReadBodyAsync(response, "error-detail", ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                return $"HTTP {(int)response.StatusCode}";
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(message.GetString()))
                {
                    return TrimMessage(message.GetString()!);
                }

                if (document.RootElement.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(error.GetString()))
                {
                    return TrimMessage(error.GetString()!);
                }
            }
            catch (JsonException)
            {
            }

            return TrimMessage(body);
        }
        catch (BlaxelApiException)
        {
            return $"HTTP {(int)response.StatusCode}";
        }
    }

    private static string ClassifyStatus(HttpStatusCode status) => (int)status switch
    {
        401 or 403 => "unauthorised",
        404 => "not-found",
        408 => "timeout",
        409 => "conflict",
        425 or 429 => "throttled",
        >= 500 => "server-error",
        _ => "request-rejected",
    };

    private static string TrimMessage(string message)
    {
        var trimmed = message.Trim();
        return trimmed.Length > 512 ? trimmed[..512] : trimmed;
    }

    /// <summary>Long-call timeout floor: status waits reuse the configured timeout with a 60s minimum.</summary>
    public static TimeSpan WaitCallTimeout(TimeSpan configured) =>
        configured < TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : configured;
}

/// <summary>Credentials carried on every Blaxel request (never logged, never in URLs).</summary>
internal sealed record BlaxelCredentials(string ApiKey, string Workspace)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new InvalidOperationException("Blaxel API key is missing.");
        }

        if (string.IsNullOrWhiteSpace(Workspace))
        {
            throw new InvalidOperationException("Blaxel workspace is missing.");
        }
    }
}

internal sealed record BlaxelSandboxCreateRequest(
    [property: JsonPropertyName("metadata")] BlaxelMetadata Metadata,
    [property: JsonPropertyName("spec")] BlaxelSandboxSpecBody Spec);

internal sealed record BlaxelMetadata(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("labels")] Dictionary<string, string>? Labels = null);

internal sealed record BlaxelSandboxSpecBody(
    [property: JsonPropertyName("runtime")] BlaxelRuntimeSpec Runtime,
    [property: JsonPropertyName("region")] string? Region = null);

internal sealed record BlaxelRuntimeSpec(
    [property: JsonPropertyName("image")] string Image,
    [property: JsonPropertyName("memory")] int MemoryMb,
    [property: JsonPropertyName("envs")] List<BlaxelEnvVar>? Envs = null,
    [property: JsonPropertyName("ttl")] string? Ttl = null);

internal sealed record BlaxelEnvVar(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string Value);

internal sealed class BlaxelSandboxView
{
    [JsonPropertyName("metadata")]
    public BlaxelSandboxViewMetadata? Metadata { get; set; }

    [JsonPropertyName("spec")]
    public BlaxelSandboxViewSpec? Spec { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonIgnore]
    public string Name => Metadata?.Name ?? string.Empty;

    [JsonIgnore]
    public string Url => Metadata?.Url ?? string.Empty;

    [JsonIgnore]
    public string Region => Spec?.Region ?? string.Empty;
}

internal sealed class BlaxelSandboxViewMetadata
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("workspace")]
    public string? Workspace { get; set; }

    [JsonPropertyName("labels")]
    public Dictionary<string, string>? Labels { get; set; }
}

internal sealed class BlaxelSandboxViewSpec
{
    [JsonPropertyName("region")]
    public string? Region { get; set; }
}

internal sealed class BlaxelSandboxListPage
{
    private static readonly JsonSerializerOptions PageOptions = new() { PropertyNameCaseInsensitive = true };

    public List<BlaxelSandboxView> Sandboxes { get; set; } = [];

    public bool HasMore { get; set; }

    public string? Cursor { get; set; }

    /// <summary>
    /// Parses both list shapes the API serves: the cursor-paginated wrapper
    /// (<c>{data, meta}</c>) and the legacy bare array.
    /// </summary>
    public static BlaxelSandboxListPage Parse(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                return new BlaxelSandboxListPage
                {
                    Sandboxes = JsonSerializer.Deserialize<List<BlaxelSandboxView>>(body, PageOptions) ?? [],
                };
            }

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data))
            {
                var page = new BlaxelSandboxListPage
                {
                    Sandboxes = JsonSerializer.Deserialize<List<BlaxelSandboxView>>(data.GetRawText(), PageOptions) ?? [],
                };

                if (root.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
                {
                    if (meta.TryGetProperty("hasMore", out var hasMore) && hasMore.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        page.HasMore = hasMore.GetBoolean();
                    }

                    if (meta.TryGetProperty("cursor", out var cursor) && cursor.ValueKind == JsonValueKind.String)
                    {
                        page.Cursor = cursor.GetString();
                    }
                }

                return page;
            }

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("sandboxes", out var sandboxes))
            {
                return new BlaxelSandboxListPage
                {
                    Sandboxes = JsonSerializer.Deserialize<List<BlaxelSandboxView>>(sandboxes.GetRawText(), PageOptions) ?? [],
                    HasMore = root.TryGetProperty("hasMore", out var hasMore) && hasMore.ValueKind == JsonValueKind.True,
                };
            }

            throw new BlaxelApiException(null, "malformed-response", "list-sandboxes: unrecognised response shape");
        }
        catch (JsonException ex)
        {
            throw new BlaxelApiException(null, "malformed-response", $"list-sandboxes: invalid JSON: {ex.Message}", ex);
        }
    }
}
