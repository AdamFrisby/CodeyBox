using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeyBox.RunloopPlugin;

/// <summary>
/// Minimal Runloop REST client over an injected <see cref="HttpClient"/>.
/// Every method is bounded (no unbounded pagination or buffering) and maps
/// service-side failures to <see cref="RunloopApiException"/> — the provider
/// converts those into the pipeline's infrastructure exceptions, never into a
/// verdict on the work item's diff. Request bodies never carry the token in
/// the URL; bodies are small JSON documents, never unbounded guest output.
/// </summary>
internal sealed class RunloopApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public RunloopApiClient(HttpClient httpClient)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<DevboxView> CreateDevboxAsync(
        string baseUrl,
        string token,
        DevboxCreateRequest request,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(request);
        using var content = JsonContent.Create(request, options: JsonOptions);
        using var response = await SendAsync(
            baseUrl, token, HttpMethod.Post, "/v1/devboxes", content, timeout, "create-devbox", ct).ConfigureAwait(false);
        return await ReadJsonAsync<DevboxView>(response, "create-devbox", ct).ConfigureAwait(false);
    }

    public async Task<DevboxView> GetDevboxAsync(
        string baseUrl, string token, string devboxId, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devboxId);
        using var response = await SendAsync(
            baseUrl, token, HttpMethod.Get, $"/v1/devboxes/{Uri.EscapeDataString(devboxId)}",
            content: null, timeout, "get-devbox", ct).ConfigureAwait(false);
        return await ReadJsonAsync<DevboxView>(response, "get-devbox", ct).ConfigureAwait(false);
    }

    public async Task<DevboxView> WaitForDevboxStatusAsync(
        string baseUrl,
        string token,
        string devboxId,
        IReadOnlyList<string> statuses,
        int timeoutSeconds,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devboxId);
        var payload = new { statuses, timeout_seconds = timeoutSeconds };
        using var content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await SendAsync(
            baseUrl, token, HttpMethod.Post, $"/v1/devboxes/{Uri.EscapeDataString(devboxId)}/wait_for_status",
            content, timeout, "wait-for-devbox-status", ct).ConfigureAwait(false);
        return await ReadJsonAsync<DevboxView>(response, "wait-for-devbox-status", ct).ConfigureAwait(false);
    }

    public async Task<DevboxListPage> ListDevboxesAsync(
        string baseUrl, string token, int limit, string? startingAfter, TimeSpan timeout, CancellationToken ct)
    {
        var query = $"/v1/devboxes?limit={Math.Clamp(limit, 1, 5000)}";
        if (!string.IsNullOrWhiteSpace(startingAfter))
        {
            query += $"&starting_after={Uri.EscapeDataString(startingAfter)}";
        }

        using var response = await SendAsync(
            baseUrl, token, HttpMethod.Get, query, content: null, timeout, "list-devboxes", ct).ConfigureAwait(false);
        return await ReadJsonAsync<DevboxListPage>(response, "list-devboxes", ct).ConfigureAwait(false);
    }

    public async Task<AsyncExecutionView> StartExecutionAsync(
        string baseUrl, string token, string devboxId, string command, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devboxId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var payload = new { command };
        using var content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await SendAsync(
            baseUrl, token, HttpMethod.Post, $"/v1/devboxes/{Uri.EscapeDataString(devboxId)}/execute_async",
            content, timeout, "start-execution", ct).ConfigureAwait(false);
        return await ReadJsonAsync<AsyncExecutionView>(response, "start-execution", ct).ConfigureAwait(false);
    }

    public async Task<AsyncExecutionView> WaitForExecutionAsync(
        string baseUrl,
        string token,
        string devboxId,
        string executionId,
        int timeoutSeconds,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var payload = new { statuses = new[] { "completed" }, timeout_seconds = Math.Clamp(timeoutSeconds, 1, 25) };
        using var content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await SendAsync(
            baseUrl, token, HttpMethod.Post,
            $"/v1/devboxes/{Uri.EscapeDataString(devboxId)}/executions/{Uri.EscapeDataString(executionId)}/wait_for_status?last_n=100000",
            content, timeout, "wait-for-execution", ct).ConfigureAwait(false);
        return await ReadJsonAsync<AsyncExecutionView>(response, "wait-for-execution", ct).ConfigureAwait(false);
    }

    public async Task<AsyncExecutionView> GetExecutionAsync(
        string baseUrl, string token, string devboxId, string executionId, TimeSpan timeout, CancellationToken ct)
    {
        using var response = await SendAsync(
            baseUrl, token, HttpMethod.Get,
            $"/v1/devboxes/{Uri.EscapeDataString(devboxId)}/executions/{Uri.EscapeDataString(executionId)}?last_n=100000",
            content: null, timeout, "get-execution", ct).ConfigureAwait(false);
        return await ReadJsonAsync<AsyncExecutionView>(response, "get-execution", ct).ConfigureAwait(false);
    }

    public async Task KillExecutionAsync(
        string baseUrl, string token, string devboxId, string executionId, TimeSpan timeout, CancellationToken ct)
    {
        var payload = new { kill_process_group = true };
        using var content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await SendAsync(
            baseUrl, token, HttpMethod.Post,
            $"/v1/devboxes/{Uri.EscapeDataString(devboxId)}/executions/{Uri.EscapeDataString(executionId)}/kill",
            content, timeout, "kill-execution", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task WriteFileAsync(
        string baseUrl, string token, string devboxId, string path, string contents, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);
        var payload = new { file_path = path, contents };
        using var content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await SendAsync(
            baseUrl, token, HttpMethod.Post, $"/v1/devboxes/{Uri.EscapeDataString(devboxId)}/write_file_contents",
            content, timeout, "write-file", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task UploadFileAsync(
        string baseUrl, string token, string devboxId, string path, byte[] bytes, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bytes);
        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", "codeybox-upload.bin");
        form.Add(new StringContent(path, Encoding.UTF8), "path");
        using var response = await SendAsync(
            baseUrl, token, HttpMethod.Post, $"/v1/devboxes/{Uri.EscapeDataString(devboxId)}/upload_file",
            form, timeout, "upload-file", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<string> ReadFileAsync(
        string baseUrl, string token, string devboxId, string path, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var payload = new { file_path = path };
        using var content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await SendAsync(
            baseUrl, token, HttpMethod.Post, $"/v1/devboxes/{Uri.EscapeDataString(devboxId)}/read_file_contents",
            content, timeout, "read-file", ct).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    public async Task<byte[]> DownloadFileAsync(
        string baseUrl, string token, string devboxId, string path, long maxBytes, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var payload = new { path };
        using var content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await SendAsync(
            baseUrl, token, HttpMethod.Post, $"/v1/devboxes/{Uri.EscapeDataString(devboxId)}/download_file",
            content, timeout, "download-file", ct).ConfigureAwait(false);
        return await ReadBoundedAsync(response, maxBytes, "download-file", ct).ConfigureAwait(false);
    }

    public async Task<DiskSnapshotView> SnapshotDiskAsync(
        string baseUrl, string token, string devboxId, string? name, TimeSpan timeout, CancellationToken ct)
    {
        var payload = new
        {
            name,
            metadata = new Dictionary<string, string>
            {
                ["codeybox-managed"] = "true",
                ["codeybox-provider"] = "runloop",
            },
        };
        using var content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await SendAsync(
            baseUrl, token, HttpMethod.Post, $"/v1/devboxes/{Uri.EscapeDataString(devboxId)}/snapshot_disk",
            content, timeout, "snapshot-disk", ct).ConfigureAwait(false);
        return await ReadJsonAsync<DiskSnapshotView>(response, "snapshot-disk", ct).ConfigureAwait(false);
    }

    public async Task<DevboxView> SuspendAsync(
        string baseUrl, string token, string devboxId, TimeSpan timeout, CancellationToken ct)
    {
        using var response = await SendAsync(
            baseUrl, token, HttpMethod.Post, $"/v1/devboxes/{Uri.EscapeDataString(devboxId)}/suspend",
            content: null, timeout, "suspend", ct).ConfigureAwait(false);
        return await ReadJsonAsync<DevboxView>(response, "suspend", ct).ConfigureAwait(false);
    }

    public async Task<DevboxView> ResumeAsync(
        string baseUrl, string token, string devboxId, TimeSpan timeout, CancellationToken ct)
    {
        using var response = await SendAsync(
            baseUrl, token, HttpMethod.Post, $"/v1/devboxes/{Uri.EscapeDataString(devboxId)}/resume",
            content: null, timeout, "resume", ct).ConfigureAwait(false);
        return await ReadJsonAsync<DevboxView>(response, "resume", ct).ConfigureAwait(false);
    }

    public async Task ShutdownAsync(
        string baseUrl, string token, string devboxId, bool force, TimeSpan timeout, CancellationToken ct)
    {
        var query = $"/v1/devboxes/{Uri.EscapeDataString(devboxId)}/shutdown{(force ? "?force=true" : string.Empty)}";
        using var response = await SendAsync(
            baseUrl, token, HttpMethod.Post, query, content: null, timeout, "shutdown", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> SendAsync(
        string baseUrl,
        string token,
        HttpMethod method,
        string pathAndQuery,
        HttpContent? content,
        TimeSpan timeout,
        string operation,
        CancellationToken ct)
    {
        var uri = new Uri(new Uri(EnsureTrailingSlash(baseUrl), UriKind.Absolute), pathAndQuery.TrimStart('/'));
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
            throw new RunloopApiException(null, "timeout", $"{operation} exceeded {timeout.TotalSeconds}s", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new RunloopApiException(null, "unreachable", $"{operation} transport failure: {ex.GetType().Name}", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await ReadErrorBodyAsync(response, ct).ConfigureAwait(false);
            var classification = RunloopFailureClassification.Classify(response.StatusCode, operation);
            response.Dispose();
            throw new RunloopApiException(response.StatusCode, classification.ErrorClass, $"{operation}: {body}");
        }

        return response;
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        try
        {
            var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
            if (result is null)
            {
                throw new RunloopApiException(null, "malformed-response", $"{operation} returned an empty body");
            }

            return result;
        }
        catch (RunloopApiException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new RunloopApiException(null, "malformed-response", $"{operation} returned unparseable JSON", ex);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, long maxBytes, string operation, CancellationToken ct)
    {
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > maxBytes)
        {
            throw new RunloopApiException(null, "oversize-response", $"{operation} declared {contentLength.Value} bytes (limit {maxBytes})");
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var sink = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (sink.Length + read > maxBytes)
            {
                throw new RunloopApiException(null, "oversize-response", $"{operation} exceeded {maxBytes} bytes");
            }

            sink.Write(buffer, 0, read);
        }

        return sink.ToArray();
    }

    private static async Task<string> ReadErrorBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return body.Length > 1024 ? body[..1024] : body;
        }
        catch (Exception)
        {
            return "(unreadable error body)";
        }
    }

    private static string EnsureTrailingSlash(string baseUrl) =>
        baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : baseUrl + "/";

    /// <summary>
    /// Timeout for server-side wait calls. The service holds these requests
    /// up to ~30s, so the client timeout must clear that plus margin even
    /// when the operator configures a short general <c>ApiTimeout</c>.
    /// </summary>
    internal static TimeSpan WaitCallTimeout(TimeSpan configured) =>
        configured < TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : configured + TimeSpan.FromSeconds(30);
}

/// <summary>Devbox creation payload (snake_case per the Runloop API).</summary>
internal sealed record DevboxCreateRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("environment_variables")]
    public Dictionary<string, string>? EnvironmentVariables { get; init; }

    [JsonPropertyName("blueprint_id")]
    public string? BlueprintId { get; init; }

    [JsonPropertyName("blueprint_name")]
    public string? BlueprintName { get; init; }

    [JsonPropertyName("snapshot_id")]
    public string? SnapshotId { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; init; }

    [JsonPropertyName("launch_parameters")]
    public DevboxLaunchParameters? LaunchParameters { get; init; }
}

/// <summary>Launch parameters (resource size, keep-alive).</summary>
internal sealed record DevboxLaunchParameters
{
    [JsonPropertyName("resource_size_request")]
    public string? ResourceSizeRequest { get; init; }

    [JsonPropertyName("custom_cpu_cores")]
    public double? CustomCpuCores { get; init; }

    [JsonPropertyName("custom_gb_memory")]
    public int? CustomGbMemory { get; init; }

    [JsonPropertyName("custom_disk_size")]
    public int? CustomDiskSize { get; init; }

    [JsonPropertyName("keep_alive_time_seconds")]
    public long? KeepAliveTimeSeconds { get; init; }
}

/// <summary>Recorded shape of a Runloop devbox view.</summary>
internal sealed record DevboxView
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("create_time_ms")]
    public long CreateTimeMs { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; init; }

    [JsonPropertyName("failure_reason")]
    public string? FailureReason { get; init; }
}

/// <summary>One page of the devbox inventory.</summary>
internal sealed record DevboxListPage
{
    [JsonPropertyName("devboxes")]
    public List<DevboxView> Devboxes { get; init; } = [];

    [JsonPropertyName("has_more")]
    public bool HasMore { get; init; }
}

/// <summary>Recorded shape of an async execution view.</summary>
internal sealed record AsyncExecutionView
{
    [JsonPropertyName("devbox_id")]
    public string DevboxId { get; init; } = string.Empty;

    [JsonPropertyName("execution_id")]
    public string ExecutionId { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("stdout")]
    public string? Stdout { get; init; }

    [JsonPropertyName("stderr")]
    public string? Stderr { get; init; }

    [JsonPropertyName("exit_status")]
    public int? ExitStatus { get; init; }

    [JsonPropertyName("stdout_truncated")]
    public bool StdoutTruncated { get; init; }

    [JsonPropertyName("stderr_truncated")]
    public bool StderrTruncated { get; init; }
}

/// <summary>Recorded shape of a disk snapshot view.</summary>
internal sealed record DiskSnapshotView
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("create_time_ms")]
    public long CreateTimeMs { get; init; }

    [JsonPropertyName("source_devbox_id")]
    public string SourceDevboxId { get; init; } = string.Empty;
}
