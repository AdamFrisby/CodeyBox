using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Orchestrator-side dispatch transport for a poll-driven remote executor.
/// Stage-in tars the phase's single bare repo into a temp archive; the run
/// enqueues the dispatch on the <see cref="ExecutorPhaseBroker"/> and awaits
/// the executor — which polls over its existing outbound channel, streams
/// live chunks back incrementally, and completes with the result plus the
/// stage-out tar — then stage-out copies that tar to the requested archive
/// path under the cap before the proxy validates it exactly like a colocated
/// archive. Only the per-item repo path is ever transferred.
///
/// <para>Instances carry one dispatch, like
/// <see cref="ColocatedExecutorTransport"/>: the transport factory hands out
/// a fresh instance per resolve and the proxy uses one instance per host
/// attempt. Sequential reuse is safe; concurrent use of one instance is not
/// supported.</para>
/// </summary>
public sealed class PollingExecutorPhaseTransport : IStreamingExecutorPhaseTransport
{
    private readonly ExecutorPhaseBroker _broker;
    private readonly Func<ExecutorPhaseDispatchOptions> _optionsAccessor;
    private readonly ILogger<PollingExecutorPhaseTransport> _log;

    private string? _scratchRoot;
    private string? _stageInTarPath;
    private string? _repoRootName;
    private BrokerDispatchResult? _dispatchResult;

    public PollingExecutorPhaseTransport(
        string hostId,
        ExecutorPhaseBroker broker,
        Func<ExecutorPhaseDispatchOptions>? optionsAccessor = null,
        ILogger<PollingExecutorPhaseTransport>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        HostId = hostId.Trim();
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _optionsAccessor = optionsAccessor ?? (static () => new ExecutorPhaseDispatchOptions());
        _log = log ?? NullLogger<PollingExecutorPhaseTransport>.Instance;
    }

    /// <inheritdoc />
    public string HostId { get; }

    /// <inheritdoc />
    /// <remarks>
    /// The path is canonicalized here at the sink; the proxy already contains
    /// it under the repositories root before dispatch. Links are never
    /// followed: a staged source that is itself a link, or contains one,
    /// fails loudly.
    /// </remarks>
    public async Task StageInAsync(string hostRepoPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var options = _optionsAccessor();
        options.Validate();
        if (string.IsNullOrWhiteSpace(hostRepoPath))
            throw new ExecutorPhaseTransportException(HostId, "stage-in", "Host repository path is empty.");
        string canonical;
        try
        {
            canonical = Path.GetFullPath(hostRepoPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ExecutorPhaseTransportException(HostId, "stage-in", "Host repository path is not a valid path.", ex);
        }
        if (!Directory.Exists(canonical))
            throw new ExecutorPhaseTransportException(HostId, "stage-in", "Host repository path does not exist.");
        if (IsLink(canonical))
            throw new ExecutorPhaseTransportException(HostId, "stage-in", "Host repository path is a link; refusing to stage through it.");

        var rootName = Path.GetFileName(canonical.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(rootName))
            throw new ExecutorPhaseTransportException(HostId, "stage-in", "Host repository path has no directory name.");

        CleanupQuietly();
        _scratchRoot = Path.Combine(Path.GetTempPath(), "codeybox-executor-phase-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchRoot);
        _stageInTarPath = Path.Combine(_scratchRoot, "stagein.tar");
        _repoRootName = rootName;
        _dispatchResult = null;
        await ExecutorTarTransfer.WriteDirectoryToTarAsync(
            canonical, rootName, _stageInTarPath, options.StageOutMaxArchiveBytes, HostId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<ExecutorPhaseResult> RunPhaseAsync(ExecutorPhaseRequest request, CancellationToken ct) =>
        RunPhaseAsync(request, onChunk: null, ct);

    /// <inheritdoc />
    /// <remarks>
    /// Live chunks posted by the executor are forwarded to
    /// <paramref name="onChunk"/> incrementally as they arrive — the await
    /// only completes when the executor completes, so output streams during
    /// execution, never as a terminal flush. A null callback still accepts
    /// (and discards) chunk posts so a non-streaming dispatch never breaks
    /// the executor.
    /// </remarks>
    public async Task<ExecutorPhaseResult> RunPhaseAsync(
        ExecutorPhaseRequest request,
        Func<ExecutorStreamChunk, CancellationToken, Task>? onChunk,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stageIn = _stageInTarPath
            ?? throw new ExecutorPhaseTransportException(HostId, "run-phase", "No repository was staged; StageInAsync must run first.");
        var rootName = _repoRootName
            ?? throw new ExecutorPhaseTransportException(HostId, "run-phase", "No repository was staged; StageInAsync must run first.");
        try
        {
            _dispatchResult = await _broker.DispatchAsync(HostId, request, stageIn, rootName, onChunk, ct).ConfigureAwait(false);
            return _dispatchResult.Result;
        }
        catch (OperationCanceledException)
        {
            CleanupQuietly();
            throw;
        }
        catch (ExecutorPhaseTransportException)
        {
            CleanupQuietly();
            throw;
        }
        catch (ExecutorPhaseException)
        {
            CleanupQuietly();
            throw;
        }
        catch (Exception ex)
        {
            CleanupQuietly();
            throw new ExecutorPhaseTransportException(HostId, "run-phase", ex.Message, ex);
        }
    }

    /// <inheritdoc />
    public async Task StageOutToArchiveAsync(string hostArchivePath, long maxArchiveBytes, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostArchivePath);
        if (maxArchiveBytes <= 0)
            throw new ExecutorPhaseTransportException(HostId, "stage-out", $"Archive cap must be positive, got {maxArchiveBytes}.");
        var result = _dispatchResult
            ?? throw new ExecutorPhaseTransportException(HostId, "stage-out", "No phase has run; RunPhaseAsync must run before stage-out.");
        try
        {
            await ExecutorTarTransfer.CopyFileBoundedAsync(
                result.StageOutTarPath, hostArchivePath, maxArchiveBytes, HostId, "stage-out", ct).ConfigureAwait(false);
        }
        finally
        {
            _dispatchResult = null;
            DeleteQuietly(result.StageOutTarPath);
            CleanupQuietly();
        }
    }

    private void CleanupQuietly()
    {
        try
        {
            if (_scratchRoot is not null && Directory.Exists(_scratchRoot))
                Directory.Delete(_scratchRoot, recursive: true);
        }
        catch
        {
            // Best-effort temp cleanup: never mask the dispatch outcome.
        }
        finally
        {
            _scratchRoot = null;
            _stageInTarPath = null;
            _repoRootName = null;
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

    private static bool IsLink(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
