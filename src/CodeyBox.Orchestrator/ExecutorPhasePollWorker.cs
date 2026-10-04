using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Executor-side phase pump: polls the orchestrator for work over the
/// outbound-only <see cref="IExecutorPhaseChannel"/>, downloads each
/// assignment's stage-in tar, runs it through the executor-local
/// <see cref="ExecutorHostPhaseRunner"/> (the same runner the colocated host
/// uses), and reports back — live stream chunks incrementally as the handler
/// produces them, then the result plus the stage-out tar. The worker never
/// binds a listening socket: every leg is executor-initiated.
///
/// <para>Failure taxonomy mirrors the runner's: an agent failure is a result
/// with <see cref="ExecutorPhaseOutcome.AgentFailed"/> reported through
/// <c>CompleteAsync</c>; an infrastructure failure (nothing staged,
/// provisioning or sandbox loss, handler transport loss) is reported through
/// <c>FailAsync</c> so the orchestrator fails over elsewhere instead of
/// charging the work item. A worker-side crash between poll and complete
/// leaves the dispatch running until the broker's lease expires it — the
/// same host-attributed outcome, never an agent verdict.</para>
/// </summary>
public sealed class ExecutorPhasePollWorker
{
    private readonly IExecutorPhaseChannel _channel;
    private readonly string _hostId;
    private readonly ExecutorHostPhaseRunner? _runner;
    private readonly string _stagingRoot;
    private readonly Func<ExecutorPhaseDispatchOptions> _dispatchOptionsAccessor;
    private readonly TimeProvider _clock;
    private readonly ILogger<ExecutorPhasePollWorker> _log;

    /// <summary>
    /// A null <paramref name="runner"/> keeps the acceptance state: the
    /// worker idles (polls nothing) while registration and heartbeats
    /// continue elsewhere.
    /// </summary>
    public ExecutorPhasePollWorker(
        IExecutorPhaseChannel channel,
        string hostId,
        ExecutorHostPhaseRunner? runner,
        string stagingRoot,
        Func<ExecutorPhaseDispatchOptions>? dispatchOptionsAccessor = null,
        TimeProvider? clock = null,
        ILogger<ExecutorPhasePollWorker>? log = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        _hostId = hostId.Trim();
        _runner = runner;
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        _stagingRoot = Path.GetFullPath(stagingRoot.Trim());
        _dispatchOptionsAccessor = dispatchOptionsAccessor ?? (static () => new ExecutorPhaseDispatchOptions());
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullLogger<ExecutorPhasePollWorker>.Instance;
    }

    /// <summary>
    /// Runs the poll loop until <paramref name="ct"/> is cancelled:
    /// assignments execute one at a time, in poll order. A per-dispatch
    /// failure is reported to the orchestrator and the loop continues;
    /// cancellation stops the loop. Without a runner the worker idles: it
    /// polls nothing while registration and heartbeats continue elsewhere.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (_runner is null)
        {
            _log.LogInformation("No phase runner wired on executor host {HostId}; phase polling idles", _hostId);
            return;
        }
        while (!ct.IsCancellationRequested)
        {
            ExecutorPendingPhase? pending;
            try
            {
                pending = await _channel.PollAsync(ReadPollTimeout(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Executor host {HostId} poll failed; retrying", _hostId);
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                continue;
            }
            if (pending is null)
                continue;
            try
            {
                await ExecuteOneAsync(pending, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Executor host {HostId} dispatch {DispatchKey} failed locally", _hostId, pending.DispatchKey);
            }
        }
    }

    /// <summary>
    /// Executes one polled assignment: download, extract, run, tar back,
    /// complete. Public for tests driving a single assignment deterministically.
    /// </summary>
    public async Task ExecuteOneAsync(ExecutorPendingPhase pending, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var options = _dispatchOptionsAccessor();
        options.Validate();

        var scratchRoot = Path.Combine(Path.GetTempPath(), "codeybox-executor-work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchRoot);
        try
        {
            var stageInTar = Path.Combine(scratchRoot, "stagein.tar");
            await _channel.DownloadStageInAsync(pending.DispatchKey, stageInTar, ct).ConfigureAwait(false);

            // The runner resolves the per-dispatch leaf deterministically
            // from the request; extract there through a single-root move so
            // concurrent dispatches against the same repo stay isolated on
            // this host exactly as they do on the colocated host.
            var staged = ExecutorPhaseExecution.ResolveStagedRepoPathForDispatch(_stagingRoot, pending.Request);
            var extractParent = Path.Combine(scratchRoot, "extracted");
            Directory.CreateDirectory(extractParent);
            await ExecutorTarTransfer.ExtractTarToDirectoryAsync(
                stageInTar, extractParent, options.StageOutMaxArchiveBytes, options.StageOutMaxEntries, _hostId, ct).ConfigureAwait(false);
            MoveSingleRoot(extractParent, staged, pending.DispatchKey);

            ExecutorPhaseResult result;
            var runner = _runner ?? throw new InvalidOperationException(
                $"Executor host '{_hostId}' polled a dispatch with no phase runner wired.");
            try
            {
                result = await runner.ExecutePhaseAsync(
                    pending.Request,
                    (chunk, token) => _channel.PostChunkAsync(pending.DispatchKey, chunk, token),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ExecutorPhaseTransportException ex)
            {
                // Already host-attributed by the runner with this host's id:
                // rethrow unchanged and report the inner evidence, so the
                // broker's wrap is the single prefix — the same
                // single-prefix evidence a colocated failure carries.
                await FailQuietAsync(pending.DispatchKey, ex.InnerException?.Message ?? ex.Message, ct).ConfigureAwait(false);
                throw;
            }
            catch (ExecutorPhaseException ex)
            {
                await FailQuietAsync(pending.DispatchKey, ex.Message, ct).ConfigureAwait(false);
                throw new ExecutorPhaseTransportException(_hostId, "run-phase", ex.Message, ex);
            }
            catch (Exception ex)
            {
                await FailQuietAsync(pending.DispatchKey, ex.Message, ct).ConfigureAwait(false);
                throw new ExecutorPhaseTransportException(_hostId, "run-phase", ex.Message, ex);
            }

            var stageOutTar = Path.Combine(scratchRoot, "stageout.tar");
            try
            {
                await ExecutorTarTransfer.WriteDirectoryToTarAsync(
                    staged, pending.RepoRootName, stageOutTar, options.StageOutMaxArchiveBytes, _hostId, ct).ConfigureAwait(false);
            }
            catch (ExecutorPhaseException ex)
            {
                // The staged copy itself is unacceptable (over the archive
                // cap): a phase failure like the orchestrator-side validator
                // rejecting a landed archive — reachable host, no failover.
                await FailPhaseQuietAsync(pending.DispatchKey, ex.Message, ct).ConfigureAwait(false);
                throw new ExecutorPhaseException(ex.Message, ex);
            }
            catch (Exception ex)
            {
                await FailQuietAsync(pending.DispatchKey, ex.Message, ct).ConfigureAwait(false);
                throw;
            }

            await _channel.CompleteAsync(pending.DispatchKey, result, stageOutTar, ct).ConfigureAwait(false);
            // The channel owns the uploaded bytes from here (it copies them
            // into orchestrator-owned storage before completing), so deleting
            // the worker scratch below is safe.
        }
        finally
        {
            try { if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true); } catch { }
        }
    }

    private TimeSpan ReadPollTimeout()
    {
        try
        {
            return _dispatchOptionsAccessor().RemotePhasePollTimeout;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Executor host {HostId} dispatch options unavailable; using fallback poll timeout", _hostId);
            return TimeSpan.FromSeconds(25);
        }
    }

    private static void MoveSingleRoot(string extractParent, string staged, string dispatchKey)
    {
        var tops = Directory.GetFileSystemEntries(extractParent);
        if (tops.Length != 1 || !Directory.Exists(tops[0]))
            throw new ExecutorPhaseTransportException(
                "(worker)", "stage", $"Dispatch '{dispatchKey}' stage-in archive has no single repository root.");
        var parent = Path.GetDirectoryName(Path.GetFullPath(staged));
        if (parent is not null)
            Directory.CreateDirectory(parent);
        try
        {
            if (Directory.Exists(staged))
                Directory.Delete(staged, recursive: true);
            Directory.Move(tops[0], staged);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ExecutorPhaseTransportException("(worker)", "stage", ex.Message, ex);
        }
    }

    private async Task FailQuietAsync(string dispatchKey, string message, CancellationToken ct)
    {
        try
        {
            await _channel.FailAsync(dispatchKey, message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Executor host {HostId} failed to report infrastructure failure for {DispatchKey}", _hostId, dispatchKey);
        }
    }

    private async Task FailPhaseQuietAsync(string dispatchKey, string message, CancellationToken ct)
    {
        try
        {
            await _channel.FailPhaseAsync(dispatchKey, message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Executor host {HostId} failed to report phase failure for {DispatchKey}", _hostId, dispatchKey);
        }
    }
}
