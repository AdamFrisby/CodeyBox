using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Result of one brokered remote dispatch: the executor-produced phase
/// result plus the executor-uploaded stage-out tar path and the dispatch
/// timing the usage recorder attributes. The stage-out tar is owned by the
/// caller: copy it under the archive cap, then delete it.
/// </summary>
public sealed record BrokerDispatchResult(
    ExecutorPhaseResult Result,
    string StageOutTarPath,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

/// <summary>
/// One pending dispatch handed to an executor that polled for work: the
/// dispatch key, the phase request envelope, the orchestrator-written
/// stage-in tar the executor downloads before running, and the tar root name
/// the executor must reuse when tarring the staged copy back (the
/// orchestrator-side stage-out validator requires the archive root to be the
/// orchestrator repo basename, which can differ from any staging leaf name).
/// </summary>
public sealed record BrokerPendingDispatch(
    string DispatchKey,
    ExecutorPhaseRequest Request,
    string StageInTarPath,
    string RepoRootName);

/// <summary>
/// Orchestrator-side rendezvous for remote phase dispatch over the
/// executor's existing outbound channel. The executor holds no inbound port:
/// it polls for work, downloads the stage-in tar, posts live stream chunks
/// and uploads the stage-out tar plus the result — every leg is
/// executor-initiated, over plain HTTPS POSTs/GETs authenticated by the
/// existing host-bound bearer middleware. This type is the in-process seam
/// those endpoints (and the in-process test channel) share.
///
/// <para>Streaming is incremental, never buffered to completion: each posted
/// chunk is forwarded inline to the dispatch's chunk observer (the proxy's
/// relay into the orchestrator-side capture and the live hub) as it
/// arrives, in post order per dispatch. A lost or reordered chunk is the
/// relay's gap marker, never silent omission.</para>
///
/// <para>Ownership: the stage-in tar is written by the dispatching transport
/// and deleted here on terminal state (completion, lease expiry,
/// cancellation, shutdown). The stage-out tar is written by the completing
/// executor and owned by the dispatch caller. Every bound (queue depth,
/// chunk size, lease) is read from <see cref="ExecutorPhaseDispatchOptions"/>
/// per call so hot-reload edits apply without a restart.</para>
///
/// <para>Thread-safe: one lock guards the pending queues and the in-flight
/// map; chunk forwarding and result validation run outside the lock.</para>
/// </summary>
public sealed class ExecutorPhaseBroker : IDisposable
{
    private readonly Func<ExecutorPhaseDispatchOptions> _optionsAccessor;
    private readonly TimeProvider _clock;
    private readonly ILogger<ExecutorPhaseBroker> _log;
    private readonly object _mutex = new();
    private readonly Dictionary<string, Queue<BrokerEntry>> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BrokerEntry> _running = new(StringComparer.Ordinal);
    private bool _disposed;

    public ExecutorPhaseBroker(
        Func<ExecutorPhaseDispatchOptions> optionsAccessor,
        TimeProvider? clock = null,
        ILogger<ExecutorPhaseBroker>? log = null)
    {
        _optionsAccessor = optionsAccessor ?? throw new ArgumentNullException(nameof(optionsAccessor));
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullLogger<ExecutorPhaseBroker>.Instance;
    }

    /// <summary>
    /// Dispatches one phase to <paramref name="hostId"/>: queues it for the
    /// next executor poll, forwards posted chunks to <paramref name="onChunk"/>
    /// incrementally as they arrive, and completes with the executor's result
    /// once it completes. A null <paramref name="onChunk"/> still accepts
    /// (and discards) chunk posts so a non-streaming dispatch never breaks
    /// the executor. Throws <see cref="ExecutorPhaseTransportException"/> on
    /// queue overflow, lease expiry, cancellation, or shutdown — all
    /// host-attributed, retried elsewhere — and surfaces
    /// <see cref="ExecutorPhaseException"/> when the executor's own result
    /// fails validation (the host was reachable).
    /// </summary>
    public async Task<BrokerDispatchResult> DispatchAsync(
        string? hostId,
        ExecutorPhaseRequest request,
        string stageInTarPath,
        string repoRootName,
        Func<ExecutorStreamChunk, CancellationToken, Task>? onChunk,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(stageInTarPath);
        var options = _optionsAccessor();
        options.Validate();
        var host = NormalizeHostId(hostId);
        var dispatchKey = ExecutorPhaseProxy.BuildDispatchKey(request);
        ExecutorPhaseProxy.ValidateRequest(request, options);
        var rootName = ValidateRepoRootName(repoRootName, host);
        if (!File.Exists(stageInTarPath))
            throw new ExecutorPhaseTransportException(host, "stage-in", "Staged dispatch archive is not present.");

        var enqueuedAt = _clock.GetUtcNow();
        var entry = new BrokerEntry(host, dispatchKey, request, stageInTarPath, rootName, onChunk, enqueuedAt, enqueuedAt + options.RemotePhaseLeaseTimeout);
        lock (_mutex)
        {
            ThrowIfDisposedLocked(host, "run-phase");
            SweepExpiredLocked(_clock.GetUtcNow());
            var queue = GetQueueLocked(host);
            if (queue.Count >= options.MaxPendingRemoteDispatchesPerHost)
                throw new ExecutorPhaseTransportException(host, "run-phase", "Host dispatch queue is full.");
            queue.Enqueue(entry);
            if (_running.ContainsKey(dispatchKey))
                throw new ExecutorPhaseTransportException(host, "run-phase", "A dispatch with the same key is already running.");
            // No pulse needed: pollers wake on their delay slice (at most
            // 250ms), which keeps the broker free of condition-variable
            // pairing obligations across every mutation site.
        }

        try
        {
            var remaining = entry.Deadline - _clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                throw new ExecutorPhaseTransportException(host, "run-phase", "Remote dispatch lease expired before pickup.");
            using var leaseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            leaseCts.CancelAfter(remaining);
            try
            {
                return await entry.Completion.Task.WaitAsync(leaseCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ExecutorPhaseTransportException(host, "run-phase", "Remote dispatch lease expired while waiting for the executor.");
            }
        }
        catch
        {
            lock (_mutex)
            {
                RemoveEntryLocked(entry);
                SweepExpiredLocked(_clock.GetUtcNow());
            }
            DeleteQuietly(stageInTarPath);
            throw;
        }
    }

    /// <summary>
    /// Executor-side poll: returns the next pending dispatch for
    /// <paramref name="hostId"/>, waiting up to <paramref name="wait"/> for
    /// one to arrive. Returns null on timeout. Only the assigned host ever
    /// receives its own dispatches (exact host match). Fully asynchronous: a
    /// waiting poll never blocks its thread, so an executor worker shares
    /// the threadpool with the phases it executes instead of pinning a
    /// thread per poll.
    /// </summary>
    public async Task<BrokerPendingDispatch?> PollAsync(string? hostId, TimeSpan wait, CancellationToken ct)
    {
        var host = NormalizeHostId(hostId);
        if (wait < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(wait));
        var deadline = _clock.GetUtcNow() + wait;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            lock (_mutex)
            {
                ThrowIfDisposedLocked(host, "phase-poll");
                SweepExpiredLocked(_clock.GetUtcNow());
                if (_pending.TryGetValue(host, out var queue) && queue.Count > 0)
                {
                    var entry = queue.Dequeue();
                    entry.StartedAt = _clock.GetUtcNow();
                    _running[entry.DispatchKey] = entry;
                    return new BrokerPendingDispatch(entry.DispatchKey, entry.Request, entry.StageInTarPath, entry.RepoRootName);
                }
                if (_clock.GetUtcNow() >= deadline)
                    return null;
            }
            var remaining = deadline - _clock.GetUtcNow();
            var slice = remaining > TimeSpan.FromMilliseconds(250)
                ? TimeSpan.FromMilliseconds(250)
                : remaining;
            if (slice > TimeSpan.Zero)
                await Task.Delay(slice, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executor-side chunk post: forwards one live agent-output chunk to the
    /// dispatch's observer incrementally. Rejects oversized posts at ingress
    /// before buffering. Unknown or expired dispatches throw
    /// <see cref="ExecutorPhaseTransportException"/> so the worker aborts
    /// that dispatch instead of streaming into the void.
    /// </summary>
    public async Task PostChunkAsync(
        string? hostId,
        string? dispatchKey,
        ExecutorStreamChunk chunk,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var options = _optionsAccessor();
        options.Validate();
        var host = NormalizeHostId(hostId);
        var key = NormalizeDispatchKey(dispatchKey);
        if (chunk.Sequence < 0)
            throw new ExecutorPhaseTransportException(host, "phase-chunk", "Chunk sequence must be zero or positive.");
        if ((chunk.Data?.Length ?? 0) > options.MaxRemoteStreamChunkChars)
            throw new ExecutorPhaseTransportException(host, "phase-chunk", "Chunk exceeds the configured maximum.");

        Func<ExecutorStreamChunk, CancellationToken, Task>? observer;
        lock (_mutex)
        {
            ThrowIfDisposedLocked(host, "phase-chunk");
            if (!_running.TryGetValue(key, out var entry) || !string.Equals(entry.HostId, host, StringComparison.Ordinal))
                throw new ExecutorPhaseTransportException(host, "phase-chunk", "No running dispatch matches this key.");
            observer = entry.OnChunk;
        }

        if (observer is not null)
            await observer(chunk, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Executor-side completion: validates the result envelope (a malformed
    /// result is a phase failure — the host was reachable — surfaced to the
    /// dispatcher as <see cref="ExecutorPhaseException"/>) and releases the
    /// dispatcher with the result, the stage-out tar path, and the timing.
    /// </summary>
    public Task CompleteAsync(
        string? hostId,
        string? dispatchKey,
        ExecutorPhaseResult result,
        string stageOutTarPath,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(stageOutTarPath);
        var options = _optionsAccessor();
        options.Validate();
        var host = NormalizeHostId(hostId);
        var key = NormalizeDispatchKey(dispatchKey);

        BrokerEntry? entry;
        lock (_mutex)
        {
            ThrowIfDisposedLocked(host, "phase-complete");
            if (!_running.TryGetValue(key, out entry) || !string.Equals(entry.HostId, host, StringComparison.Ordinal))
                throw new ExecutorPhaseTransportException(host, "phase-complete", "No running dispatch matches this key.");
            _running.Remove(key);
            RemoveFromPendingLocked(entry);
        }

        try
        {
            ExecutorPhaseProxy.ValidateResult(result, options);
        }
        catch (Exception ex)
        {
            entry.Completion.TrySetException(
                ex is ExecutorPhaseException ? ex : new ExecutorPhaseException($"Executor returned an unacceptable result: {ex.Message}", ex));
            DeleteQuietly(entry.StageInTarPath);
            throw new ExecutorPhaseException($"Executor result for '{key}' failed validation and was rejected.", ex);
        }

        if (!File.Exists(stageOutTarPath))
        {
            var missing = new ExecutorPhaseTransportException(host, "stage-out", "Completed dispatch uploaded no stage-out archive.");
            entry.Completion.TrySetException(missing);
            DeleteQuietly(entry.StageInTarPath);
            throw missing;
        }

        var completedAt = _clock.GetUtcNow();
        entry.Completion.TrySetResult(new BrokerDispatchResult(result, stageOutTarPath, entry.EnqueuedAt, entry.StartedAt, completedAt));
        DeleteQuietly(entry.StageInTarPath);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the orchestrator-written stage-in tar path for a pending or
    /// running dispatch owned by <paramref name="hostId"/>. Backs the
    /// executor's stage-in download: ownership is exact-match, so one host
    /// can never download another host's repo bytes. Unknown or expired keys
    /// throw <see cref="ExecutorPhaseTransportException"/>.
    /// </summary>
    public string GetStageInPath(string? hostId, string? dispatchKey)
    {
        var host = NormalizeHostId(hostId);
        var key = NormalizeDispatchKey(dispatchKey);
        lock (_mutex)
        {
            ThrowIfDisposedLocked(host, "phase-stagein");
            foreach (var queue in _pending.Values)
            {
                foreach (var candidate in queue)
                {
                    if (string.Equals(candidate.DispatchKey, key, StringComparison.Ordinal)
                        && string.Equals(candidate.HostId, host, StringComparison.Ordinal))
                        return candidate.StageInTarPath;
                }
            }
            if (_running.TryGetValue(key, out var entry) && string.Equals(entry.HostId, host, StringComparison.Ordinal))
                return entry.StageInTarPath;
            throw new ExecutorPhaseTransportException(host, "phase-stagein", "No pending dispatch matches this key.");
        }
    }

    /// <summary>
    /// Executor-side infrastructure failure: the phase never ran (nothing
    /// staged, provisioning or sandbox loss), so the dispatch fails as a
    /// host-attributed transport failure — retried elsewhere, never charged
    /// to the work item as an agent failure and never cached. An agent
    /// failure on the executor is a <i>result</i> reported through
    /// <see cref="CompleteAsync"/>, never this path.
    /// </summary>
    public Task FailAsync(string? hostId, string? dispatchKey, string? message, CancellationToken ct)
    {
        var host = NormalizeHostId(hostId);
        var key = NormalizeDispatchKey(dispatchKey);
        BrokerEntry? entry;
        lock (_mutex)
        {
            ThrowIfDisposedLocked(host, "phase-fail");
            if (!_running.TryGetValue(key, out entry) || !string.Equals(entry.HostId, host, StringComparison.Ordinal))
                throw new ExecutorPhaseTransportException(host, "phase-fail", "No running dispatch matches this key.");
            _running.Remove(key);
            RemoveFromPendingLocked(entry);
        }
        var detail = string.IsNullOrWhiteSpace(message) ? "executor reported an infrastructure failure" : Truncate(message.Trim(), 2048);
        entry.Completion.TrySetException(new ExecutorPhaseTransportException(host, "run-phase", detail));
        DeleteQuietly(entry.StageInTarPath);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Executor-side phase failure: the phase ran far enough to determine
    /// its own payload is unacceptable (for example the stage-out tar
    /// exceeded the archive cap), so the dispatch fails as an
    /// <see cref="ExecutorPhaseException"/> — the host was reachable, no
    /// failover, no repo write, nothing cached — exactly like the
    /// orchestrator-side stage-out validator rejecting a landed archive.
    /// </summary>
    public Task FailPhaseAsync(string? hostId, string? dispatchKey, string? message, CancellationToken ct)
    {
        var host = NormalizeHostId(hostId);
        var key = NormalizeDispatchKey(dispatchKey);
        BrokerEntry? entry;
        lock (_mutex)
        {
            ThrowIfDisposedLocked(host, "phase-fail");
            if (!_running.TryGetValue(key, out entry) || !string.Equals(entry.HostId, host, StringComparison.Ordinal))
                throw new ExecutorPhaseTransportException(host, "phase-fail", "No running dispatch matches this key.");
            _running.Remove(key);
            RemoveFromPendingLocked(entry);
        }
        var detail = string.IsNullOrWhiteSpace(message) ? "executor reported a phase failure" : Truncate(message.Trim(), 2048);
        entry.Completion.TrySetException(new ExecutorPhaseException(detail));
        DeleteQuietly(entry.StageInTarPath);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        List<BrokerEntry> entries;
        lock (_mutex)
        {
            if (_disposed)
                return;
            _disposed = true;
            entries = [.. _pending.Values.SelectMany(q => q), .. _running.Values];
            _pending.Clear();
            _running.Clear();
        }
        foreach (var entry in entries)
        {
            DeleteQuietly(entry.StageInTarPath);
            entry.Completion.TrySetException(
                new ExecutorPhaseTransportException(entry.HostId, "run-phase", "The dispatch broker shut down."));
        }
    }

    public static string NormalizeHostId(string? hostId)
    {
        if (string.IsNullOrWhiteSpace(hostId))
            throw new ExecutorPhaseTransportException("(unknown)", "phase-dispatch", "Executor host id is required.");
        var trimmed = hostId.Trim();
        if (trimmed.Length > ExecutorRegistration.MaxHostIdLength)
            throw new ExecutorPhaseTransportException("(unknown)", "phase-dispatch", "Executor host id is too long.");
        if (trimmed.Any(char.IsControl))
            throw new ExecutorPhaseTransportException("(unknown)", "phase-dispatch", "Executor host id must not contain control characters.");
        return trimmed;
    }

    public static string NormalizeDispatchKey(string? dispatchKey)
    {
        if (string.IsNullOrWhiteSpace(dispatchKey))
            throw new ExecutorPhaseTransportException("(unknown)", "phase-dispatch", "Dispatch key is required.");
        var trimmed = dispatchKey.Trim();
        if (trimmed.Length > 512)
            throw new ExecutorPhaseTransportException("(unknown)", "phase-dispatch", "Dispatch key is too long.");
        return trimmed;
    }

    public static string ValidateRepoRootName(string? rootName, string host)
    {
        if (string.IsNullOrWhiteSpace(rootName))
            throw new ExecutorPhaseTransportException(host, "stage-in", "Dispatch repo root name is required.");
        var trimmed = rootName.Trim();
        if (trimmed.Length > 256
            || trimmed.Any(ch => ch is '/' or '\\' or '\0' || char.IsControl(ch))
            || trimmed is "." or "..")
            throw new ExecutorPhaseTransportException(host, "stage-in", "Dispatch repo root name is not a safe file name.");
        return trimmed;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";

    private void ThrowIfDisposedLocked(string host, string operation)
    {
        if (_disposed)
            throw new ExecutorPhaseTransportException(host, operation, "The dispatch broker shut down.");
    }

    private Queue<BrokerEntry> GetQueueLocked(string host)
    {
        if (!_pending.TryGetValue(host, out var queue))
        {
            queue = new Queue<BrokerEntry>();
            _pending[host] = queue;
        }
        return queue;
    }

    private void RemoveEntryLocked(BrokerEntry entry)
    {
        if (_running.TryGetValue(entry.DispatchKey, out var running) && ReferenceEquals(running, entry))
            _running.Remove(entry.DispatchKey);
        RemoveFromPendingLocked(entry);
    }

    private void RemoveFromPendingLocked(BrokerEntry entry)
    {
        if (!_pending.TryGetValue(entry.HostId, out var queue) || queue.Count == 0)
            return;
        var kept = new Queue<BrokerEntry>(queue.Count);
        while (queue.Count > 0)
        {
            var candidate = queue.Dequeue();
            if (!ReferenceEquals(candidate, entry))
                kept.Enqueue(candidate);
        }
        if (kept.Count == 0)
            _pending.Remove(entry.HostId);
        else
            _pending[entry.HostId] = kept;
    }

    private void SweepExpiredLocked(DateTimeOffset now)
    {
        foreach (var (host, queue) in _pending.ToArray())
        {
            while (queue.Count > 0 && queue.Peek().Deadline <= now)
            {
                var expired = queue.Dequeue();
                expired.Completion.TrySetException(
                    new ExecutorPhaseTransportException(host, "run-phase", "Remote dispatch lease expired before pickup."));
                DeleteQuietly(expired.StageInTarPath);
            }
            if (queue.Count == 0)
                _pending.Remove(host);
        }
        foreach (var (key, entry) in _running.ToArray())
        {
            if (entry.Deadline <= now)
            {
                _running.Remove(key);
                entry.Completion.TrySetException(
                    new ExecutorPhaseTransportException(entry.HostId, "run-phase", "Remote dispatch lease expired while running."));
                DeleteQuietly(entry.StageInTarPath);
            }
        }
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
            // Best-effort temp cleanup: never mask the dispatch outcome.
        }
    }

    private sealed class BrokerEntry(
        string hostId,
        string dispatchKey,
        ExecutorPhaseRequest request,
        string stageInTarPath,
        string repoRootName,
        Func<ExecutorStreamChunk, CancellationToken, Task>? onChunk,
        DateTimeOffset enqueuedAt,
        DateTimeOffset deadline)
    {
        public string HostId { get; } = hostId;
        public string DispatchKey { get; } = dispatchKey;
        public ExecutorPhaseRequest Request { get; } = request;
        public string StageInTarPath { get; } = stageInTarPath;
        public string RepoRootName { get; } = repoRootName;
        public Func<ExecutorStreamChunk, CancellationToken, Task>? OnChunk { get; } = onChunk;
        public DateTimeOffset EnqueuedAt { get; } = enqueuedAt;
        public DateTimeOffset Deadline { get; } = deadline;
        public DateTimeOffset StartedAt { get; set; } = enqueuedAt;
        public TaskCompletionSource<BrokerDispatchResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
