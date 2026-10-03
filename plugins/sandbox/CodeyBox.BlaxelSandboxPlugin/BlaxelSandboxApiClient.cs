using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeyBox.BlaxelPlugin;

/// <summary>
/// Minimal Blaxel sandbox data-plane client over an injected <see cref="HttpClient"/>.
/// Talks to one sandbox's auto-generated endpoint (<c>metadata.url</c>) with the
/// same bearer + workspace headers as the control plane. Covers process
/// execution the pipeline needs: start (<c>POST /process</c>), poll
/// (<c>GET /process/{id}</c>), logs (<c>GET /process/{id}/logs</c>), and kill
/// (<c>DELETE /process/{id}/kill</c>). Shapes follow the published sandbox API;
/// see <c>Fixtures/blaxel</c> for the recorded shapes. Failures surface as
/// <see cref="BlaxelApiException"/> (infrastructure, never a diff verdict).
/// </summary>
internal sealed class BlaxelSandboxApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly BlaxelControlPlaneClient _transport;

    /// <summary>
    /// Ceiling for process-start responses, which carry no guest output
    /// (just pid/status) and must stay small.
    /// </summary>
    internal const long MaxStartResponseBytes = 1L * 1024 * 1024;

    /// <summary>
    /// Envelope slack added to the caller's stdout+stderr caps when bounding
    /// one data-plane poll response, which carries both streams plus JSON framing.
    /// </summary>
    internal const long DataPlaneOverheadBytes = 1L * 1024 * 1024;

    /// <summary>
    /// Response ceiling for one poll, tied to the exec's output caps so a
    /// runaway guest cannot OOM the host before accumulation caps apply.
    /// </summary>
    public static long ResponseCap(long maxStdoutBytes, long maxStderrBytes) =>
        maxStdoutBytes + maxStderrBytes + DataPlaneOverheadBytes;

    public BlaxelSandboxApiClient(HttpClient httpClient)
    {
        _transport = new BlaxelControlPlaneClient(httpClient ?? throw new ArgumentNullException(nameof(httpClient)));
    }

    public async Task<BlaxelProcessView> StartProcessAsync(
        string sandboxUrl,
        BlaxelCredentials credentials,
        BlaxelProcessRequest request,
        TimeSpan timeout,
        CancellationToken ct,
        long maxResponseBytes = MaxStartResponseBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxUrl);
        ArgumentNullException.ThrowIfNull(request);
        using var content = JsonContent.Create(request, options: JsonOptions);
        using var response = await _transport.SendAsync(
            sandboxUrl, credentials, HttpMethod.Post, "/process", content, timeout, "start-process", ct).ConfigureAwait(false);
        return await ReadProcessAsync(response, "start-process", maxResponseBytes, ct).ConfigureAwait(false);
    }

    public async Task<BlaxelProcessView> GetProcessAsync(
        string sandboxUrl,
        BlaxelCredentials credentials,
        string processId,
        TimeSpan timeout,
        CancellationToken ct,
        long maxResponseBytes = 64L * 1024 * 1024 + 64L * 1024 * 1024 + DataPlaneOverheadBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        using var response = await _transport.SendAsync(
            sandboxUrl, credentials, HttpMethod.Get, $"/process/{Uri.EscapeDataString(processId)}",
            content: null, timeout, "get-process", ct).ConfigureAwait(false);
        return await ReadProcessAsync(response, "get-process", maxResponseBytes, ct).ConfigureAwait(false);
    }

    public async Task<BlaxelProcessLogs> GetProcessLogsAsync(
        string sandboxUrl,
        BlaxelCredentials credentials,
        string processId,
        TimeSpan timeout,
        CancellationToken ct,
        long maxResponseBytes = 64L * 1024 * 1024 + 64L * 1024 * 1024 + DataPlaneOverheadBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        using var response = await _transport.SendAsync(
            sandboxUrl, credentials, HttpMethod.Get, $"/process/{Uri.EscapeDataString(processId)}/logs",
            content: null, timeout, "get-process-logs", ct).ConfigureAwait(false);
        return await ReadLogsAsync(response, maxResponseBytes, ct).ConfigureAwait(false);
    }

    public async Task KillProcessAsync(
        string sandboxUrl,
        BlaxelCredentials credentials,
        string processId,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        using var response = await _transport.SendAsync(
            sandboxUrl, credentials, HttpMethod.Delete, $"/process/{Uri.EscapeDataString(processId)}/kill",
            content: null, timeout, "kill-process", ct).ConfigureAwait(false);
        await DrainAsync(response, "kill-process", ct).ConfigureAwait(false);
    }

    private static async Task DrainAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        _ = await ReadBoundedAsync(response.Content, MaxStartResponseBytes, operation, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Streams an untrusted data-plane response through a byte ceiling before
    /// materialising it: guest output is attacker-influenced and unbounded, so
    /// the declared <c>Content-Length</c> is checked first (fail fast on a
    /// lying-large header), then every chunk is counted so a missing or lying
    /// header cannot blow the bound either. Breaches report
    /// <c>output-limit</c> so callers kill the guest process instead of buffering.
    /// </summary>
    private static async Task<string> ReadBoundedAsync(
        HttpContent content, long maxBytes, string operation, CancellationToken ct)
    {
        try
        {
            if (content.Headers.ContentLength is { } known && known > maxBytes)
            {
                throw new BlaxelApiException(
                    null, "output-limit",
                    $"{operation}: response body of {known} bytes exceeds the {maxBytes}-byte bound");
            }

            await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var sink = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (sink.Length + read > maxBytes)
                {
                    throw new BlaxelApiException(
                        null, "output-limit",
                        $"{operation}: response body exceeds the {maxBytes}-byte bound");
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

    private static async Task<BlaxelProcessView> ReadProcessAsync(HttpResponseMessage response, string operation, long maxBytes, CancellationToken ct)
    {
        var body = await ReadBoundedAsync(response.Content, maxBytes, operation, ct).ConfigureAwait(false);
        try
        {
            var view = JsonSerializer.Deserialize<BlaxelProcessView>(body, JsonOptions);
            if (view is null)
            {
                throw new BlaxelApiException(null, "malformed-response", $"{operation}: empty JSON body");
            }

            return view;
        }
        catch (JsonException ex)
        {
            throw new BlaxelApiException(null, "malformed-response", $"{operation}: invalid JSON: {ex.Message}", ex);
        }
    }

    private static async Task<BlaxelProcessLogs> ReadLogsAsync(HttpResponseMessage response, long maxBytes, CancellationToken ct)
    {
        var body = await ReadBoundedAsync(response.Content, maxBytes, "get-process-logs", ct).ConfigureAwait(false);
        try
        {
            var logs = JsonSerializer.Deserialize<BlaxelProcessLogs>(body, JsonOptions);
            if (logs is null)
            {
                throw new BlaxelApiException(null, "malformed-response", "get-process-logs: empty JSON body");
            }

            return logs;
        }
        catch (JsonException ex)
        {
            throw new BlaxelApiException(null, "malformed-response", $"get-process-logs: invalid JSON: {ex.Message}", ex);
        }
    }
}

internal sealed record BlaxelProcessRequest(
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("workingDir")] string? WorkingDir = null,
    [property: JsonPropertyName("waitForCompletion")] bool WaitForCompletion = false,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("env")] Dictionary<string, string>? Env = null);

internal sealed class BlaxelProcessView
{
    [JsonPropertyName("pid")]
    public string? Pid { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("exitCode")]
    public int? ExitCode { get; set; }

    [JsonPropertyName("stdout")]
    public string? Stdout { get; set; }

    [JsonPropertyName("stderr")]
    public string? Stderr { get; set; }

    [JsonPropertyName("logs")]
    public string? Logs { get; set; }

    [JsonIgnore]
    public bool IsTerminal =>
        string.Equals(Status, "completed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Status, "failed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Status, "killed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Status, "stopped", StringComparison.OrdinalIgnoreCase);
}

internal sealed class BlaxelProcessLogs
{
    [JsonPropertyName("logs")]
    public string? Logs { get; set; }

    [JsonPropertyName("stdout")]
    public string? Stdout { get; set; }

    [JsonPropertyName("stderr")]
    public string? Stderr { get; set; }
}
