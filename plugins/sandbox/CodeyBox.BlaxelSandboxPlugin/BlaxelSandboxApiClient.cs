using System.Net.Http.Json;
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

    public BlaxelSandboxApiClient(HttpClient httpClient)
    {
        _transport = new BlaxelControlPlaneClient(httpClient ?? throw new ArgumentNullException(nameof(httpClient)));
    }

    public async Task<BlaxelProcessView> StartProcessAsync(
        string sandboxUrl,
        BlaxelCredentials credentials,
        BlaxelProcessRequest request,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxUrl);
        ArgumentNullException.ThrowIfNull(request);
        using var content = JsonContent.Create(request, options: JsonOptions);
        using var response = await _transport.SendAsync(
            sandboxUrl, credentials, HttpMethod.Post, "/process", content, timeout, "start-process", ct).ConfigureAwait(false);
        return await ReadProcessAsync(response, "start-process", ct).ConfigureAwait(false);
    }

    public async Task<BlaxelProcessView> GetProcessAsync(
        string sandboxUrl,
        BlaxelCredentials credentials,
        string processId,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        using var response = await _transport.SendAsync(
            sandboxUrl, credentials, HttpMethod.Get, $"/process/{Uri.EscapeDataString(processId)}",
            content: null, timeout, "get-process", ct).ConfigureAwait(false);
        return await ReadProcessAsync(response, "get-process", ct).ConfigureAwait(false);
    }

    public async Task<BlaxelProcessLogs> GetProcessLogsAsync(
        string sandboxUrl,
        BlaxelCredentials credentials,
        string processId,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        using var response = await _transport.SendAsync(
            sandboxUrl, credentials, HttpMethod.Get, $"/process/{Uri.EscapeDataString(processId)}/logs",
            content: null, timeout, "get-process-logs", ct).ConfigureAwait(false);
        return await ReadLogsAsync(response, ct).ConfigureAwait(false);
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
        response.Dispose();
    }

    private static async Task<BlaxelProcessView> ReadProcessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            var view = await JsonSerializer.DeserializeAsync<BlaxelProcessView>(stream, JsonOptions, ct).ConfigureAwait(false);
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

    private static async Task<BlaxelProcessLogs> ReadLogsAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            var logs = await JsonSerializer.DeserializeAsync<BlaxelProcessLogs>(stream, JsonOptions, ct).ConfigureAwait(false);
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
