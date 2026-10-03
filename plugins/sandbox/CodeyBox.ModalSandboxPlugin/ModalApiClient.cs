using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeyBox.ModalPlugin;

/// <summary>
/// Minimal Modal control-plane client over an injected <see cref="HttpClient"/>.
/// Every method is bounded (no unbounded pagination or buffering) and maps
/// service-side failures to <see cref="ModalApiException"/> — the provider
/// converts those into the pipeline's infrastructure outcomes, never into a
/// verdict on the work item's diff. Credentials travel as request headers,
/// never in the URL; bodies are small JSON documents, never unbounded guest output.
/// </summary>
///
/// <remarks>
/// Endpoint shapes are the plugin's recorded contract (see
/// <c>tests/CodeyBox.Tests/Fixtures/modal/</c>): Modal's control plane is
/// SDK-driven and offers no stable public REST sandbox contract, so these
/// shapes were transcribed from SDK/CLI traffic at the time of writing and
/// may drift. The README names this risk; the recorded-shape tests pin the
/// parsing so drift fails loudly in tests, not silently in a phase.
///
/// <para>The files:write endpoint is assumed to create missing parent
/// directories (the provider never pre-creates them); a service that 404s on
/// missing parents surfaces as an ordinary write failure, never silent loss.
/// Exec polls are incremental: each response carries only the output since
/// the requested offsets, and offsets count UTF-8 bytes.</para>
/// </remarks>
internal sealed class ModalApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public ModalApiClient(HttpClient httpClient)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<ModalSandboxView> CreateSandboxAsync(
        string baseUrl,
        ModalCredentials credentials,
        ModalSandboxCreateRequest request,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(request);
        using var content = JsonContent.Create(request, options: JsonOptions);
        using var response = await SendAsync(
            baseUrl, credentials, HttpMethod.Post, "/v1/sandboxes", content, timeout, "create-sandbox", ct).ConfigureAwait(false);
        return await ReadJsonAsync<ModalSandboxView>(response, "create-sandbox", ct).ConfigureAwait(false);
    }

    public async Task<ModalSandboxView> GetSandboxAsync(
        string baseUrl, ModalCredentials credentials, string sandboxId, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxId);
        using var response = await SendAsync(
            baseUrl, credentials, HttpMethod.Get, $"/v1/sandboxes/{Uri.EscapeDataString(sandboxId)}",
            content: null, timeout, "get-sandbox", ct).ConfigureAwait(false);
        return await ReadJsonAsync<ModalSandboxView>(response, "get-sandbox", ct).ConfigureAwait(false);
    }

    public async Task<ModalSandboxListPage> ListSandboxesAsync(
        string baseUrl, ModalCredentials credentials, int limit, string? cursor, TimeSpan timeout, CancellationToken ct)
    {
        var query = new StringBuilder($"/v1/sandboxes?limit={Math.Clamp(limit, 1, 200)}");
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            query.Append("&cursor=").Append(Uri.EscapeDataString(cursor));
        }

        using var response = await SendAsync(
            baseUrl, credentials, HttpMethod.Get, query.ToString(), content: null, timeout, "list-sandboxes", ct).ConfigureAwait(false);
        return await ReadJsonAsync<ModalSandboxListPage>(response, "list-sandboxes", ct).ConfigureAwait(false);
    }

    public async Task TerminateSandboxAsync(
        string baseUrl, ModalCredentials credentials, string sandboxId, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxId);
        using var response = await SendAsync(
            baseUrl, credentials, HttpMethod.Post, $"/v1/sandboxes/{Uri.EscapeDataString(sandboxId)}/terminate",
            content: null, timeout, "terminate-sandbox", ct).ConfigureAwait(false);
        await DrainAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<ModalSnapshotView> SnapshotSandboxAsync(
        string baseUrl, ModalCredentials credentials, string sandboxId, string? name, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxId);
        var payload = new { name };
        using var content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await SendAsync(
            baseUrl, credentials, HttpMethod.Post, $"/v1/sandboxes/{Uri.EscapeDataString(sandboxId)}/snapshot",
            content, timeout, "snapshot-sandbox", ct).ConfigureAwait(false);
        return await ReadJsonAsync<ModalSnapshotView>(response, "snapshot-sandbox", ct).ConfigureAwait(false);
    }

    public async Task<ModalExecView> StartExecAsync(
        string baseUrl,
        ModalCredentials credentials,
        string sandboxId,
        ModalExecStartRequest request,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxId);
        ArgumentNullException.ThrowIfNull(request);
        using var content = JsonContent.Create(request, options: JsonOptions);
        using var response = await SendAsync(
            baseUrl, credentials, HttpMethod.Post, $"/v1/sandboxes/{Uri.EscapeDataString(sandboxId)}/exec",
            content, timeout, "start-exec", ct).ConfigureAwait(false);
        return await ReadJsonAsync<ModalExecView>(response, "start-exec", ct).ConfigureAwait(false);
    }

    public async Task<ModalExecView> PollExecAsync(
        string baseUrl,
        ModalCredentials credentials,
        string sandboxId,
        string execId,
        long stdoutAfter,
        long stderrAfter,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxId);
        ArgumentException.ThrowIfNullOrWhiteSpace(execId);
        var query = $"/v1/sandboxes/{Uri.EscapeDataString(sandboxId)}/exec/{Uri.EscapeDataString(execId)}" +
            $"?stdout_after={Math.Max(0, stdoutAfter)}&stderr_after={Math.Max(0, stderrAfter)}";
        using var response = await SendAsync(
            baseUrl, credentials, HttpMethod.Get, query, content: null, timeout, "poll-exec", ct).ConfigureAwait(false);
        return await ReadJsonAsync<ModalExecView>(response, "poll-exec", ct).ConfigureAwait(false);
    }

    public async Task KillExecAsync(
        string baseUrl, ModalCredentials credentials, string sandboxId, string execId, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxId);
        ArgumentException.ThrowIfNullOrWhiteSpace(execId);
        using var response = await SendAsync(
            baseUrl, credentials, HttpMethod.Post,
            $"/v1/sandboxes/{Uri.EscapeDataString(sandboxId)}/exec/{Uri.EscapeDataString(execId)}/kill",
            content: null, timeout, "kill-exec", ct).ConfigureAwait(false);
        await DrainAsync(response, ct).ConfigureAwait(false);
    }

    public async Task WriteFileAsync(
        string baseUrl,
        ModalCredentials credentials,
        string sandboxId,
        string guestPath,
        byte[] contents,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxId);
        ArgumentException.ThrowIfNullOrWhiteSpace(guestPath);
        ArgumentNullException.ThrowIfNull(contents);
        var payload = new ModalFileWriteRequest(
            guestPath,
            Convert.ToBase64String(contents));
        using var content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await SendAsync(
            baseUrl, credentials, HttpMethod.Post, $"/v1/sandboxes/{Uri.EscapeDataString(sandboxId)}/files:write",
            content, timeout, "write-file", ct).ConfigureAwait(false);
        await DrainAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<byte[]> ReadFileBytesAsync(
        string baseUrl,
        ModalCredentials credentials,
        string sandboxId,
        string guestPath,
        long maxBytes,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxId);
        ArgumentException.ThrowIfNullOrWhiteSpace(guestPath);
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "Read-back bound must be positive.");
        }

        var query = $"/v1/sandboxes/{Uri.EscapeDataString(sandboxId)}/files?path={Uri.EscapeDataString(guestPath)}";
        using var response = await SendAsync(
            baseUrl, credentials, HttpMethod.Get, query, content: null, timeout, "read-file", ct).ConfigureAwait(false);
        var view = await ReadBoundedJsonAsync<ModalFileView>(response, "read-file", guestPath, maxBytes, ct).ConfigureAwait(false);
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(view.ContentBase64 ?? string.Empty);
        }
        catch (FormatException ex)
        {
            throw new ModalApiException(null, "malformed-response", $"read-file for '{guestPath}' returned undecodable content", ex);
        }

        if (decoded.Length > maxBytes)
        {
            throw new ModalApiException(null, "limit-exceeded", $"read-file for '{guestPath}' is {decoded.Length} bytes (limit {maxBytes})");
        }

        return decoded;
    }

    public static TimeSpan WaitCallTimeout(TimeSpan configured) =>
        configured < TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : configured;

    private async Task<HttpResponseMessage> SendAsync(
        string baseUrl,
        ModalCredentials credentials,
        HttpMethod method,
        string pathAndQuery,
        HttpContent? content,
        TimeSpan timeout,
        string operation,
        CancellationToken ct)
    {
        var uri = Combine(baseUrl, pathAndQuery);
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("X-Modal-Token-Id", credentials.TokenId);
        request.Headers.TryAddWithoutValidation("X-Modal-Token-Secret", credentials.TokenSecret);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
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
            throw new ModalApiException(null, "timeout", $"{operation} exceeded {timeout}", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ModalApiException(null, "unreachable", $"{operation} transport failure: {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var detail = await ReadErrorDetailAsync(response, ct).ConfigureAwait(false);
            var retryAfter = GetRetryAfter(response);
            var classification = ModalFailureClassification.Classify(response.StatusCode, operation, retryAfter);
            var statusCode = response.StatusCode;
            response.Dispose();
            throw new ModalApiException(statusCode, classification.ErrorClass, $"{operation}: {detail}", retryAfter);
        }

        return response;
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta;
        }

        return null;
    }

    private static async Task<string> ReadErrorDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (body.Length > 512)
            {
                body = body[..512];
            }

            return string.IsNullOrWhiteSpace(body) ? response.StatusCode.ToString() : body;
        }
        catch (OperationCanceledException)
        {
            return response.StatusCode.ToString();
        }
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        try
        {
            var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
            if (value is null)
            {
                throw new ModalApiException(null, "malformed-response", $"{operation} returned an empty body");
            }

            return value;
        }
        catch (ModalApiException)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new ModalApiException(null, "timeout", $"{operation} read exceeded its budget", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            throw new ModalApiException(null, "malformed-response", $"{operation} returned an unreadable body: {ex.Message}", ex);
        }
    }

    private static async Task<T> ReadBoundedJsonAsync<T>(
        HttpResponseMessage response, string operation, string guestPath, long maxDecodedBytes, CancellationToken ct)
    {
        // Bound before buffering: base64 inflates by 4/3 plus a small JSON
        // envelope. Reject on Content-Length first, then stream through a
        // capped reader so guest-controlled bytes can never force unbounded
        // host allocation; the decoded-size check at the call site remains
        // as the second line of defence.
        const long envelopeSlackBytes = 8192;
        long maxBodyBytes;
        try
        {
            checked
            {
                maxBodyBytes = ((maxDecodedBytes + 2) / 3) * 4 + envelopeSlackBytes;
            }
        }
        catch (OverflowException)
        {
            maxBodyBytes = long.MaxValue;
        }

        if (response.Content.Headers.ContentLength is { } contentLength && contentLength > maxBodyBytes)
        {
            throw new ModalApiException(null, "limit-exceeded", $"{operation} for '{guestPath}' exceeds its {maxDecodedBytes}-byte bound");
        }

        byte[] body;
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var sink = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (sink.Length + read > maxBodyBytes)
                {
                    throw new ModalApiException(null, "limit-exceeded", $"{operation} for '{guestPath}' exceeds its {maxDecodedBytes}-byte bound");
                }

                sink.Write(buffer, 0, read);
            }

            body = sink.ToArray();
        }
        catch (ModalApiException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new ModalApiException(null, "timeout", $"{operation} read exceeded its budget", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new ModalApiException(null, "malformed-response", $"{operation} returned an unreadable body: {ex.Message}", ex);
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(body, JsonOptions);
            if (value is null)
            {
                throw new ModalApiException(null, "malformed-response", $"{operation} returned an empty body");
            }

            return value;
        }
        catch (ModalApiException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new ModalApiException(null, "malformed-response", $"{operation} returned an unreadable body: {ex.Message}", ex);
        }
    }

    private static async Task DrainAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
        }
    }

    private static Uri Combine(string baseUrl, string pathAndQuery)
    {
        var trimmed = baseUrl.TrimEnd('/');
        return new Uri(trimmed + pathAndQuery, UriKind.Absolute);
    }
}

/// <summary>Credential pair resolved from the environment chain at use time; never persisted.</summary>
public sealed record ModalCredentials(string TokenId, string TokenSecret);

public sealed record ModalSandboxCreateRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("app_name")] string AppName,
    [property: JsonPropertyName("image_ref")] string? ImageRef,
    [property: JsonPropertyName("snapshot_id")] string? SnapshotId,
    [property: JsonPropertyName("cpu_count")] double CpuCount,
    [property: JsonPropertyName("memory_mib")] int MemoryMiB,
    [property: JsonPropertyName("idle_timeout_seconds")] int IdleTimeoutSeconds,
    [property: JsonPropertyName("env")] IReadOnlyDictionary<string, string>? Environment,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata,
    [property: JsonPropertyName("allowed_hosts")] IReadOnlyList<string>? AllowedHosts);

public sealed record ModalSandboxView(
    [property: JsonPropertyName("sandbox_id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("created_at_unix")] long CreatedAtUnix,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata);

public sealed record ModalSandboxListPage(
    [property: JsonPropertyName("sandboxes")] IReadOnlyList<ModalSandboxView> Sandboxes,
    [property: JsonPropertyName("has_more")] bool HasMore,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

public sealed record ModalSnapshotView(
    [property: JsonPropertyName("snapshot_id")] string Id,
    [property: JsonPropertyName("name")] string? Name);

public sealed record ModalExecStartRequest(
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("workdir")] string Workdir);

public sealed record ModalExecView(
    [property: JsonPropertyName("exec_id")] string ExecId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("exit_code")] int? ExitCode,
    [property: JsonPropertyName("stdout")] string Stdout,
    [property: JsonPropertyName("stderr")] string Stderr,
    [property: JsonPropertyName("stdout_length")] long StdoutLength,
    [property: JsonPropertyName("stderr_length")] long StderrLength,
    [property: JsonPropertyName("stdout_truncated")] bool StdoutTruncated,
    [property: JsonPropertyName("stderr_truncated")] bool StderrTruncated);

public sealed record ModalFileWriteRequest(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("content_b64")] string ContentBase64);

public sealed record ModalFileView(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("content_b64")] string? ContentBase64);
