using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// HTTP <see cref="IExecutorPhaseChannel"/> over the orchestrator's phase
/// endpoints: the executor polls for work, downloads the stage-in tar,
/// posts live stream chunks, and completes — every leg executor-initiated
/// over plain HTTPS with the existing host-bound bearer, so the executor
/// needs no inbound port. Chunks are posted as produced, never buffered to
/// phase end. All bounds are enforced while receiving, before buffering.
/// </summary>
public sealed class HttpExecutorPhaseChannel : IExecutorPhaseChannel
{
    private const int MaxErrorBytes = 4096;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _hostId;
    private readonly Func<ExecutorOptions> _optionsAccessor;
    private readonly Func<ExecutorPhaseDispatchOptions> _dispatchOptionsAccessor;
    private readonly ILogger<HttpExecutorPhaseChannel> _log;

    public HttpExecutorPhaseChannel(
        HttpClient http,
        string hostId,
        Func<ExecutorOptions> optionsAccessor,
        Func<ExecutorPhaseDispatchOptions>? dispatchOptionsAccessor = null,
        ILogger<HttpExecutorPhaseChannel>? log = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        _hostId = hostId.Trim();
        _optionsAccessor = optionsAccessor ?? throw new ArgumentNullException(nameof(optionsAccessor));
        _dispatchOptionsAccessor = dispatchOptionsAccessor ?? (static () => new ExecutorPhaseDispatchOptions());
        _log = log ?? NullLogger<HttpExecutorPhaseChannel>.Instance;
    }

    /// <inheritdoc />
    public async Task<ExecutorPendingPhase?> PollAsync(TimeSpan wait, CancellationToken ct)
    {
        var options = _optionsAccessor();
        var seconds = Math.Clamp((int)wait.TotalSeconds, 0, 60);
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"executors/{Uri.EscapeDataString(_hostId)}/phase/next?waitSeconds={seconds}");
        using var response = await SendAsync(request, "phase-poll", TimeoutFor(options, wait), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return null;
        await EnsureSuccessAsync(response, "phase-poll", ct).ConfigureAwait(false);
        // Assignments carry the phase payload (up to the configured request
        // cap), so the bound here is the dispatch envelope cap — not the
        // small error-body cap.
        var assignmentOptions = _dispatchOptionsAccessor();
        assignmentOptions.Validate();
        var maxAssignmentBytes = (long)assignmentOptions.MaxRequestPayloadBytes + 256 * 1024;
        var dto = await ReadJsonBoundedAsync<AssignmentDto>(response, "phase-poll", maxAssignmentBytes, ct).ConfigureAwait(false)
            ?? throw new ExecutorPhaseTransportException(_hostId, "phase-poll", "Empty assignment body.");
        var dispatch = assignmentOptions;
        var rebuilt = new ExecutorPhaseRequest
        {
            WorkItemId = dto.WorkItemId ?? string.Empty,
            Phase = dto.Phase ?? string.Empty,
            Attempt = dto.Attempt,
            RepositoryId = dto.RepositoryId ?? string.Empty,
            PayloadJson = dto.PayloadJson ?? string.Empty,
            RequiredCredential = dto.RequiredCredential,
            RequiredNetworkProfile = dto.RequiredNetworkProfile,
            RequiredCapabilities = dto.RequiredCapabilities ?? [],
        };
        try
        {
            ExecutorPhaseProxy.ValidateRequest(rebuilt, dispatch);
        }
        catch (Exception ex)
        {
            throw new ExecutorPhaseTransportException(_hostId, "phase-poll", $"Assignment failed validation: {ex.Message}", ex);
        }
        if (string.IsNullOrWhiteSpace(dto.DispatchKey) || string.IsNullOrWhiteSpace(dto.RepoRootName))
            throw new ExecutorPhaseTransportException(_hostId, "phase-poll", "Assignment is missing its dispatch identity.");
        return new ExecutorPendingPhase(dto.DispatchKey.Trim(), rebuilt, dto.RepoRootName.Trim());
    }

    /// <inheritdoc />
    public async Task DownloadStageInAsync(string dispatchKey, string destinationTarPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationTarPath);
        var options = _optionsAccessor();
        var key = ExecutorPhaseBroker.NormalizeDispatchKey(dispatchKey);
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"executors/{Uri.EscapeDataString(_hostId)}/phase/stagein?dispatchKey={Uri.EscapeDataString(key)}");
        using var response = await SendAsync(request, "phase-stagein", TimeoutFor(options, null), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "phase-stagein", ct).ConfigureAwait(false);
        var dispatch = _dispatchOptionsAccessor();
        dispatch.Validate();
        var parent = Path.GetDirectoryName(Path.GetFullPath(destinationTarPath));
        if (parent is not null)
            Directory.CreateDirectory(parent);
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = new FileStream(
                destinationTarPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
            var buffer = new byte[128 * 1024];
            long total = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > dispatch.StageOutMaxArchiveBytes)
                {
                    output.Dispose();
                    DeleteQuietly(destinationTarPath);
                    throw new ExecutorPhaseException(
                        $"Stage-in archive exceeded the archive cap ({dispatch.StageOutMaxArchiveBytes} bytes).");
                }
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
        catch (ExecutorPhaseException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ExecutorPhaseTransportException(_hostId, "phase-stagein", ex.Message, ex);
        }
    }

    /// <inheritdoc />
    public async Task PostChunkAsync(string dispatchKey, ExecutorStreamChunk chunk, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var key = ExecutorPhaseBroker.NormalizeDispatchKey(dispatchKey);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"executors/{Uri.EscapeDataString(_hostId)}/phase/chunk")
        {
            Content = JsonContent.Create(
                new ChunkDto(key, chunk.Sequence, chunk.Data),
                options: JsonOptions),
        };
        using var response = await SendAsync(request, "phase-chunk", TimeoutFor(_optionsAccessor(), null), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "phase-chunk", ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task FailAsync(string dispatchKey, string message, CancellationToken ct)
    {
        var key = ExecutorPhaseBroker.NormalizeDispatchKey(dispatchKey);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"executors/{Uri.EscapeDataString(_hostId)}/phase/fail")
        {
            Content = JsonContent.Create(new FailDto(key, message ?? string.Empty), options: JsonOptions),
        };
        using var response = await SendAsync(request, "phase-fail", TimeoutFor(_optionsAccessor(), null), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "phase-fail", ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task FailPhaseAsync(string dispatchKey, string message, CancellationToken ct)
    {
        var key = ExecutorPhaseBroker.NormalizeDispatchKey(dispatchKey);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"executors/{Uri.EscapeDataString(_hostId)}/phase/failphase")
        {
            Content = JsonContent.Create(new FailDto(key, message ?? string.Empty), options: JsonOptions),
        };
        using var response = await SendAsync(request, "phase-failphase", TimeoutFor(_optionsAccessor(), null), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "phase-failphase", ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CompleteAsync(string dispatchKey, ExecutorPhaseResult result, string stageOutTarPath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        var key = ExecutorPhaseBroker.NormalizeDispatchKey(dispatchKey);
        var options = _optionsAccessor();
        if (!File.Exists(stageOutTarPath))
            throw new ExecutorPhaseTransportException(_hostId, "phase-stageout", "Stage-out archive is not present.");
        await using (var upload = File.OpenRead(stageOutTarPath))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"executors/{Uri.EscapeDataString(_hostId)}/phase/stageout?dispatchKey={Uri.EscapeDataString(key)}")
            {
                Content = new StreamContent(upload),
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            using var response = await SendAsync(request, "phase-stageout", TimeoutFor(options, null), ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "phase-stageout", ct).ConfigureAwait(false);
        }
        using (var request = new HttpRequestMessage(HttpMethod.Post, $"executors/{Uri.EscapeDataString(_hostId)}/phase/complete")
        {
            Content = JsonContent.Create(
                new ResultDto(
                    key,
                    result.Outcome,
                    result.CommitSha,
                    [.. result.Findings],
                    result.Usage.InputTokens,
                    result.Usage.OutputTokens,
                    result.Usage.CostUsd,
                    result.ErrorMessage),
                options: JsonOptions),
        })
        {
            using var response = await SendAsync(request, "phase-complete", TimeoutFor(options, null), ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "phase-complete", ct).ConfigureAwait(false);
        }
    }

    private static TimeSpan TimeoutFor(ExecutorOptions options, TimeSpan? pollWait)
    {
        var floor = options.RequestTimeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(20) : options.RequestTimeout;
        if (pollWait is { } wait)
        {
            var pollTimeout = wait + TimeSpan.FromSeconds(10);
            return pollTimeout > floor ? pollTimeout : floor;
        }
        return floor;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        string operation,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var options = _optionsAccessor();
        var key = Environment.GetEnvironmentVariable(options.ApiKeyEnvVar);
        if (string.IsNullOrEmpty(key))
            throw new ExecutorPhaseTransportException(_hostId, operation, $"API key env var '{options.ApiKeyEnvVar}' is not set.");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ExecutorPhaseTransportException(_hostId, operation, "request timed out", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ExecutorPhaseTransportException(_hostId, operation, ex.Message, ex);
        }
    }

    /// <summary>
    /// Maps non-success responses: a <c>phase-failure</c> code is an
    /// <see cref="ExecutorPhaseException"/> (reachable host, no failover);
    /// anything else is a host-attributed transport failure. Bodies are
    /// bounded and never logged (they can carry untrusted content).
    /// </summary>
    private async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        string body;
        try
        {
            body = await ReadBoundedAsync(response, ct).ConfigureAwait(false);
        }
        catch
        {
            body = string.Empty;
        }
        string message;
        string? code = null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(body) ? "{}" : body);
            message = doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString() ?? response.ReasonPhrase ?? "request failed"
                : response.ReasonPhrase ?? "request failed";
            if (doc.RootElement.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.String)
                code = codeEl.GetString();
        }
        catch (JsonException)
        {
            message = response.ReasonPhrase ?? "request failed";
        }
        if (string.Equals(code, "phase-failure", StringComparison.Ordinal))
            throw new ExecutorPhaseException(message);
        throw new ExecutorPhaseTransportException(_hostId, operation, $"{(int)response.StatusCode} {message}");
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, string operation, CancellationToken ct) =>
        await ReadJsonBoundedAsync<T>(response, operation, MaxErrorBytes, ct).ConfigureAwait(false);

    private static async Task<T?> ReadJsonBoundedAsync<T>(
        HttpResponseMessage response,
        string operation,
        long maxBytes,
        CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new ExecutorPhaseTransportException("(unknown)", operation, "Response body exceeded the receive cap.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new ExecutorPhaseTransportException("(unknown)", operation, "Response body exceeded the receive cap.");
            buffer.Write(chunk, 0, read);
        }
        try
        {
            return JsonSerializer.Deserialize<T>(buffer.ToArray(), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ExecutorPhaseTransportException("(unknown)", operation, "Response body was not valid JSON.", ex);
        }
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[MaxErrorBytes + 1];
        var total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxErrorBytes)
                break;
        }
        return System.Text.Encoding.UTF8.GetString(buffer, 0, Math.Min(total, MaxErrorBytes));
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    private sealed record AssignmentDto(
        [property: JsonPropertyName("dispatchKey")] string? DispatchKey,
        [property: JsonPropertyName("workItemId")] string? WorkItemId,
        [property: JsonPropertyName("phase")] string? Phase,
        [property: JsonPropertyName("attempt")] int Attempt,
        [property: JsonPropertyName("repositoryId")] string? RepositoryId,
        [property: JsonPropertyName("payloadJson")] string? PayloadJson,
        [property: JsonPropertyName("requiredCredential")] string? RequiredCredential,
        [property: JsonPropertyName("requiredNetworkProfile")] string? RequiredNetworkProfile,
        [property: JsonPropertyName("requiredCapabilities")] List<string>? RequiredCapabilities,
        [property: JsonPropertyName("repoRootName")] string? RepoRootName);

    private sealed record ChunkDto(
        [property: JsonPropertyName("dispatchKey")] string DispatchKey,
        [property: JsonPropertyName("sequence")] long Sequence,
        [property: JsonPropertyName("data")] string? Data);

    private sealed record FailDto(
        [property: JsonPropertyName("dispatchKey")] string DispatchKey,
        [property: JsonPropertyName("message")] string Message);

    private sealed record ResultDto(
        [property: JsonPropertyName("dispatchKey")] string DispatchKey,
        [property: JsonPropertyName("outcome")] ExecutorPhaseOutcome Outcome,
        [property: JsonPropertyName("commitSha")] string? CommitSha,
        [property: JsonPropertyName("findings")] List<string> Findings,
        [property: JsonPropertyName("inputTokens")] long InputTokens,
        [property: JsonPropertyName("outputTokens")] long OutputTokens,
        [property: JsonPropertyName("costUsd")] decimal CostUsd,
        [property: JsonPropertyName("errorMessage")] string? ErrorMessage);
}
