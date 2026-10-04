using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

/// <summary>
/// Remote phase-dispatch surface: the executor-side half of the poll-driven
/// dispatch protocol. Every route is executor-initiated (poll for work,
/// download the stage-in tar, post live stream chunks, upload the stage-out
/// tar, complete or fail) over plain HTTPS POSTs/GETs authenticated by the
/// existing host-bound bearer middleware — the executor never exposes an
/// inbound listening port, so it can sit behind NAT or a host firewall.
///
/// <para>Each route binds the path host to the authenticated caller through
/// <see cref="ExecutorEndpoints.CheckExecutorHostCaller"/> before touching
/// the broker, so one executor cannot poll, download, stream, or complete
/// another host's dispatches. All bounds are enforced at ingress before
/// buffering; the broker revalidates at the sink.</para>
/// </summary>
internal static class ExecutorPhaseEndpoints
{
    private const string PhaseFailureCode = "phase-failure";

    // Ingress JSON ceilings are derived from the hot-reloadable dispatch
    // options, never literals: each cap is the largest legitimate body for
    // its route plus envelope slack for field names, the dispatch key, and
    // JSON structure. The slack also lets small over-limit payloads reach
    // the broker so near-limit violations keep their downstream
    // classification (phase failure vs transport failure); far-over
    // payloads are rejected here before a byte is buffered beyond the cap.
    // Kestrel's default ~30 MiB ceiling would otherwise buffer the whole
    // body through model binding before any KB-scale check ran.
    private const int JsonReadChunkBytes = 16 * 1024;
    private const int ChunkEnvelopeSlackBytes = 16 * 1024;
    private const int FailEnvelopeSlackBytes = 16 * 1024;
    private const int ResultEnvelopeSlackBytes = 256 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static long MaxChunkRequestBytes(ExecutorPhaseDispatchOptions options) =>
        (long)options.MaxRemoteStreamChunkChars + ChunkEnvelopeSlackBytes;

    private static long MaxFailRequestBytes(ExecutorPhaseDispatchOptions options) =>
        (long)options.MaxResultErrorLengthChars + FailEnvelopeSlackBytes;

    private static long MaxResultRequestBytes(ExecutorPhaseDispatchOptions options) =>
        (long)options.MaxResultFindings * (options.MaxFindingLengthChars + 16L)
        + options.MaxResultErrorLengthChars
        + ResultEnvelopeSlackBytes;

    /// <summary>
    /// Reads a JSON body bounded by <paramref name="maxBytes"/>: a declared
    /// ContentLength above the cap rejects without reading, and the stream
    /// read aborts the moment the cap is crossed, so untrusted input is
    /// never fully buffered before its cap is applied. Returns the value
    /// (null for an empty body, which the caller reports as missing) or a
    /// rejection message for over-cap and malformed bodies.
    /// </summary>
    private static async Task<(T? Value, string? Rejection)> ReadJsonBoundedAsync<T>(
        HttpContext httpContext,
        long maxBytes,
        string overCapMessage,
        CancellationToken ct)
    {
        if (httpContext.Request.ContentLength > maxBytes)
            return (default, overCapMessage);
        using var buffer = new MemoryStream();
        var chunk = new byte[JsonReadChunkBytes];
        long total = 0;
        int read;
        while ((read = await httpContext.Request.Body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
                return (default, overCapMessage);
            buffer.Write(chunk, 0, read);
        }
        if (total == 0)
            return (default, null);
        try
        {
            return (JsonSerializer.Deserialize<T>(buffer.ToArray(), JsonOptions), null);
        }
        catch (JsonException)
        {
            return (default, "request body was not valid JSON.");
        }
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/executors/{hostId}/phase/next", PollAsync);
        app.MapGet("/executors/{hostId}/phase/stagein", DownloadStageInAsync);
        app.MapPost("/executors/{hostId}/phase/chunk", PostChunkAsync);
        app.MapPost("/executors/{hostId}/phase/stageout", UploadStageOutAsync);
        app.MapPost("/executors/{hostId}/phase/complete", CompleteAsync);
        app.MapPost("/executors/{hostId}/phase/fail", FailAsync);
        app.MapPost("/executors/{hostId}/phase/failphase", FailPhaseAsync);
    }

    private static async Task<IResult> PollAsync(
        string hostId,
        int? waitSeconds,
        ExecutorPhaseBroker broker,
        Func<ExecutorPhaseDispatchOptions> dispatchOptions,
        IWorkerRegistry registry,
        HttpContext httpContext,
        CancellationToken ct)
    {
        string normalized;
        try
        {
            normalized = ExecutorEndpoints.NormalizeHostId(hostId);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        if (ExecutorEndpoints.CheckExecutorHostCaller(httpContext, normalized) is { } callerRejection)
            return callerRejection;

        var workers = await registry.ListAsync(ct).ConfigureAwait(false);
        if (!workers.Any(w => string.Equals(w.WorkerId, ExecutorRegistration.WorkerIdFor(normalized), StringComparison.Ordinal)))
            return Results.NotFound(new { error = $"no executor registered for host id '{normalized}'" });

        var wait = TimeSpan.FromSeconds(Math.Clamp(waitSeconds ?? 20, 0, 60));
        BrokerPendingDispatch? pending;
        try
        {
            pending = await broker.PollAsync(normalized, wait, ct).ConfigureAwait(false);
        }
        catch (ExecutorPhaseTransportException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        if (pending is null)
            return Results.NoContent();
        return Results.Ok(new ExecutorPhaseAssignmentDto(
            pending.DispatchKey,
            pending.Request.WorkItemId,
            pending.Request.Phase,
            pending.Request.Attempt,
            pending.Request.RepositoryId,
            pending.Request.PayloadJson,
            pending.Request.RequiredCredential,
            pending.Request.RequiredNetworkProfile,
            pending.Request.RequiredCapabilities,
            pending.RepoRootName));
    }

    private static async Task<IResult> DownloadStageInAsync(
        string hostId,
        string? dispatchKey,
        ExecutorPhaseBroker broker,
        Func<ExecutorPhaseDispatchOptions> dispatchOptions,
        HttpContext httpContext,
        CancellationToken ct)
    {
        string normalized;
        try
        {
            normalized = ExecutorEndpoints.NormalizeHostId(hostId);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        if (ExecutorEndpoints.CheckExecutorHostCaller(httpContext, normalized) is { } callerRejection)
            return callerRejection;

        var options = dispatchOptions();
        string source;
        try
        {
            source = broker.GetStageInPath(normalized, dispatchKey);
        }
        catch (ExecutorPhaseTransportException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }

        // The stage-in archive is a complete file on orchestrator disk
        // (written under the cap at stage-in time), so an exact size
        // pre-check bounds the response before a byte is sent — the body is
        // then streamed by the framework, never buffered in memory.
        long length;
        try
        {
            length = new FileInfo(source).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Results.Problem("Failed to read the dispatch archive.", statusCode: StatusCodes.Status500InternalServerError);
        }
        if (length > options.StageOutMaxArchiveBytes)
        {
            await broker.FailPhaseAsync(normalized, dispatchKey ?? string.Empty,
                $"Stage-in archive exceeded the archive cap ({options.StageOutMaxArchiveBytes} bytes).", ct).ConfigureAwait(false);
            return Results.BadRequest(new { error = "Stage-in archive exceeded the archive cap.", code = PhaseFailureCode });
        }
        return Results.File(source, "application/octet-stream", Path.GetFileName(source));
    }

    private static async Task<IResult> PostChunkAsync(
        string hostId,
        ExecutorPhaseBroker broker,
        Func<ExecutorPhaseDispatchOptions> dispatchOptions,
        HttpContext httpContext,
        CancellationToken ct)
    {
        string normalized;
        try
        {
            normalized = ExecutorEndpoints.NormalizeHostId(hostId);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        if (ExecutorEndpoints.CheckExecutorHostCaller(httpContext, normalized) is { } callerRejection)
            return callerRejection;

        var options = dispatchOptions();
        options.Validate();
        var (dto, rejection) = await ReadJsonBoundedAsync<ExecutorPhaseChunkDto>(
            httpContext, MaxChunkRequestBytes(options), "Chunk exceeds the configured maximum.", ct).ConfigureAwait(false);
        if (rejection is not null)
            return Results.BadRequest(new { error = rejection });

        if (dto is null)
            return Results.BadRequest(new { error = "request body is required" });
        if (dto.Sequence < 0)
            return Results.BadRequest(new { error = "sequence must be zero or positive" });

        try
        {
            await broker.PostChunkAsync(
                normalized, dto.DispatchKey, new ExecutorStreamChunk { Sequence = dto.Sequence, Data = dto.Data }, ct).ConfigureAwait(false);
        }
        catch (ExecutorPhaseTransportException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        return Results.Ok(new { accepted = true });
    }

    private static async Task<IResult> UploadStageOutAsync(
        string hostId,
        string? dispatchKey,
        ExecutorPhaseBroker broker,
        ExecutorPhaseStageOutUploads uploads,
        Func<ExecutorPhaseDispatchOptions> dispatchOptions,
        TimeProvider clock,
        HttpContext httpContext,
        CancellationToken ct)
    {
        string normalized;
        try
        {
            normalized = ExecutorEndpoints.NormalizeHostId(hostId);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        if (ExecutorEndpoints.CheckExecutorHostCaller(httpContext, normalized) is { } callerRejection)
            return callerRejection;

        var options = dispatchOptions();
        options.Validate();
        string key;
        try
        {
            key = ExecutorPhaseBroker.NormalizeDispatchKey(dispatchKey);
        }
        catch (ExecutorPhaseTransportException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        if (httpContext.Request.ContentLength > options.StageOutMaxArchiveBytes)
        {
            // Fail the dispatch, not just the upload: without this the
            // dispatcher would hang until the remote lease expires although
            // the outcome (hostile oversized payload) is already decided.
            // An unknown key still rejects the upload; there is simply
            // nothing to fail.
            try
            {
                await broker.FailPhaseAsync(normalized, key,
                    $"Staged-back archive exceeded the archive cap ({options.StageOutMaxArchiveBytes} bytes).", ct).ConfigureAwait(false);
            }
            catch (ExecutorPhaseTransportException)
            {
            }
            return Results.BadRequest(new { error = "Stage-out upload exceeds the archive cap.", code = PhaseFailureCode });
        }

        // Ownership before bytes: a key with no running dispatch owned by
        // the caller is rejected before a single byte reaches orchestrator
        // temp disk, so random-key uploads cannot pin disk.
        bool owned;
        try
        {
            owned = broker.HasRunningDispatch(normalized, key);
        }
        catch (ExecutorPhaseTransportException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        if (!owned)
            return Results.BadRequest(new { error = "No running dispatch matches this key." });

        var tempPath = Path.Combine(Path.GetTempPath(), "codeybox-executor-stageout-" + Guid.NewGuid().ToString("N") + ".tar");
        try
        {
            await using var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
            var buffer = new byte[128 * 1024];
            long total = 0;
            int read;
            while ((read = await httpContext.Request.Body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > options.StageOutMaxArchiveBytes)
                {
                    output.Dispose();
                    DeleteQuietly(tempPath);
                    await broker.FailPhaseAsync(normalized, key,
                        $"Staged-back archive exceeded the archive cap ({options.StageOutMaxArchiveBytes} bytes).", ct).ConfigureAwait(false);
                    return Results.BadRequest(new { error = "Stage-out upload exceeds the archive cap.", code = PhaseFailureCode });
                }
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            DeleteQuietly(tempPath);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(tempPath);
            return Results.Problem("Failed to receive the stage-out upload.", statusCode: StatusCodes.Status500InternalServerError);
        }

        try
        {
            uploads.Put(normalized, key, tempPath, clock.GetUtcNow() + options.RemotePhaseLeaseTimeout, options.MaxPendingStageOutUploadsPerHost);
        }
        catch (ExecutorPhaseTransportException ex)
        {
            DeleteQuietly(tempPath);
            return Results.BadRequest(new { error = ex.Message });
        }
        return Results.Ok(new { accepted = true });
    }

    private static async Task<IResult> CompleteAsync(
        string hostId,
        ExecutorPhaseBroker broker,
        ExecutorPhaseStageOutUploads uploads,
        Func<ExecutorPhaseDispatchOptions> dispatchOptions,
        HttpContext httpContext,
        CancellationToken ct)
    {
        string normalized;
        try
        {
            normalized = ExecutorEndpoints.NormalizeHostId(hostId);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        if (ExecutorEndpoints.CheckExecutorHostCaller(httpContext, normalized) is { } callerRejection)
            return callerRejection;

        var options = dispatchOptions();
        options.Validate();
        var (dto, rejection) = await ReadJsonBoundedAsync<ExecutorPhaseResultDto>(
            httpContext, MaxResultRequestBytes(options), "Result exceeds the configured maximum.", ct).ConfigureAwait(false);
        if (rejection is not null)
            return Results.BadRequest(new { error = rejection });

        if (dto is null)
            return Results.BadRequest(new { error = "request body is required" });

        string upload;
        try
        {
            upload = uploads.Take(normalized, dto.DispatchKey);
        }
        catch (ExecutorPhaseTransportException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        var result = new ExecutorPhaseResult
        {
            Outcome = dto.Outcome,
            CommitSha = dto.CommitSha,
            Findings = dto.Findings ?? [],
            Usage = new ExecutorPhaseUsage(dto.InputTokens, dto.OutputTokens, dto.CostUsd),
            ErrorMessage = dto.ErrorMessage,
        };
        try
        {
            await broker.CompleteAsync(normalized, dto.DispatchKey, result, upload, ct).ConfigureAwait(false);
        }
        catch (ExecutorPhaseException ex)
        {
            DeleteQuietly(upload);
            return Results.BadRequest(new { error = ex.Message, code = PhaseFailureCode });
        }
        catch (ExecutorPhaseTransportException ex)
        {
            DeleteQuietly(upload);
            return Results.BadRequest(new { error = ex.Message });
        }
        return Results.Ok(new { accepted = true });
    }

    private static async Task<IResult> FailAsync(
        string hostId,
        ExecutorPhaseBroker broker,
        Func<ExecutorPhaseDispatchOptions> dispatchOptions,
        HttpContext httpContext,
        CancellationToken ct)
    {
        string normalized;
        try
        {
            normalized = ExecutorEndpoints.NormalizeHostId(hostId);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        if (ExecutorEndpoints.CheckExecutorHostCaller(httpContext, normalized) is { } callerRejection)
            return callerRejection;

        var options = dispatchOptions();
        options.Validate();
        var (dto, rejection) = await ReadJsonBoundedAsync<ExecutorPhaseFailDto>(
            httpContext, MaxFailRequestBytes(options), "Failure message exceeds the configured maximum.", ct).ConfigureAwait(false);
        if (rejection is not null)
            return Results.BadRequest(new { error = rejection });

        if (dto is null)
            return Results.BadRequest(new { error = "request body is required" });
        try
        {
            await broker.FailAsync(normalized, dto.DispatchKey, dto.Message ?? string.Empty, ct).ConfigureAwait(false);
        }
        catch (ExecutorPhaseTransportException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        return Results.Ok(new { accepted = true });
    }

    private static async Task<IResult> FailPhaseAsync(
        string hostId,
        ExecutorPhaseBroker broker,
        Func<ExecutorPhaseDispatchOptions> dispatchOptions,
        HttpContext httpContext,
        CancellationToken ct)
    {
        string normalized;
        try
        {
            normalized = ExecutorEndpoints.NormalizeHostId(hostId);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        if (ExecutorEndpoints.CheckExecutorHostCaller(httpContext, normalized) is { } callerRejection)
            return callerRejection;

        var options = dispatchOptions();
        options.Validate();
        var (dto, rejection) = await ReadJsonBoundedAsync<ExecutorPhaseFailDto>(
            httpContext, MaxFailRequestBytes(options), "Failure message exceeds the configured maximum.", ct).ConfigureAwait(false);
        if (rejection is not null)
            return Results.BadRequest(new { error = rejection });

        if (dto is null)
            return Results.BadRequest(new { error = "request body is required" });
        try
        {
            await broker.FailPhaseAsync(normalized, dto.DispatchKey, dto.Message ?? string.Empty, ct).ConfigureAwait(false);
        }
        catch (ExecutorPhaseTransportException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        return Results.Ok(new { accepted = true });
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

    public sealed record ExecutorPhaseAssignmentDto(
        string DispatchKey,
        string WorkItemId,
        string Phase,
        int Attempt,
        string RepositoryId,
        string PayloadJson,
        string? RequiredCredential,
        string? RequiredNetworkProfile,
        IReadOnlyList<string> RequiredCapabilities,
        string RepoRootName);

    public sealed class ExecutorPhaseChunkDto
    {
        public string? DispatchKey { get; set; }
        public long Sequence { get; set; }
        public string? Data { get; set; }
    }

    public sealed class ExecutorPhaseResultDto
    {
        public string? DispatchKey { get; set; }
        public ExecutorPhaseOutcome Outcome { get; set; }
        public string? CommitSha { get; set; }
        public List<string>? Findings { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public decimal CostUsd { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public sealed class ExecutorPhaseFailDto
    {
        public string? DispatchKey { get; set; }
        public string? Message { get; set; }
    }
}
