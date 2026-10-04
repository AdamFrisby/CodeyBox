using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// In-process <see cref="IExecutorPhaseChannel"/> over an
/// <see cref="ExecutorPhaseBroker"/>: the same call sequence a remote
/// executor makes over HTTPS, without the network hop. Used by tests and by
/// any in-process driver that wants the exact remote protocol (poll,
/// download, incremental chunk posts, fail, complete) against the real
/// broker semantics.
/// </summary>
public sealed class BrokerExecutorPhaseChannel : IExecutorPhaseChannel
{
    private readonly ExecutorPhaseBroker _broker;
    private readonly string _hostId;
    private readonly Func<ExecutorPhaseDispatchOptions> _optionsAccessor;
    private readonly ILogger<BrokerExecutorPhaseChannel> _log;

    public BrokerExecutorPhaseChannel(
        ExecutorPhaseBroker broker,
        string hostId,
        Func<ExecutorPhaseDispatchOptions>? optionsAccessor = null,
        ILogger<BrokerExecutorPhaseChannel>? log = null)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        _hostId = hostId.Trim();
        _optionsAccessor = optionsAccessor ?? (static () => new ExecutorPhaseDispatchOptions());
        _log = log ?? NullLogger<BrokerExecutorPhaseChannel>.Instance;
    }

    /// <inheritdoc />
    public async Task<ExecutorPendingPhase?> PollAsync(TimeSpan wait, CancellationToken ct)
    {
        var pending = await _broker.PollAsync(_hostId, wait, ct).ConfigureAwait(false);
        return pending is null
            ? null
            : new ExecutorPendingPhase(pending.DispatchKey, pending.Request, pending.RepoRootName);
    }

    /// <inheritdoc />
    public async Task DownloadStageInAsync(string dispatchKey, string destinationTarPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationTarPath);
        var options = _optionsAccessor();
        options.Validate();
        var source = _broker.GetStageInPath(_hostId, dispatchKey);
        var parent = Path.GetDirectoryName(Path.GetFullPath(destinationTarPath));
        if (parent is not null)
            Directory.CreateDirectory(parent);
        await ExecutorTarTransfer.CopyFileBoundedAsync(
            source, destinationTarPath, options.StageOutMaxArchiveBytes, _hostId, "phase-stagein", ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task PostChunkAsync(string dispatchKey, ExecutorStreamChunk chunk, CancellationToken ct) =>
        _broker.PostChunkAsync(_hostId, dispatchKey, chunk, ct);

    /// <inheritdoc />
    public Task FailAsync(string dispatchKey, string message, CancellationToken ct) =>
        _broker.FailAsync(_hostId, dispatchKey, message, ct);

    /// <inheritdoc />
    public Task FailPhaseAsync(string dispatchKey, string message, CancellationToken ct) =>
        _broker.FailPhaseAsync(_hostId, dispatchKey, message, ct);

    /// <inheritdoc />
    /// <remarks>
    /// The stage-out tar is copied into broker-owned temp storage (bounded
    /// by the archive cap) before completing: the worker deletes its scratch
    /// directory on return, so the orchestrator must own the bytes it still
    /// has to copy into the dispatch archive. The polling transport deletes
    /// the broker copy once it lands the archive.
    /// </remarks>
    public async Task CompleteAsync(string dispatchKey, ExecutorPhaseResult result, string stageOutTarPath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(stageOutTarPath);
        var options = _optionsAccessor();
        options.Validate();
        var key = ExecutorPhaseBroker.NormalizeDispatchKey(dispatchKey);
        var owned = Path.Combine(Path.GetTempPath(), "codeybox-executor-stageout-" + Guid.NewGuid().ToString("N") + ".tar");
        try
        {
            await ExecutorTarTransfer.CopyFileBoundedAsync(
                stageOutTarPath, owned, options.StageOutMaxArchiveBytes, _hostId, "phase-stageout", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Executor host stage-out upload rejected for {DispatchKey}", key);
            await _broker.FailAsync(_hostId, key, ex.Message, ct).ConfigureAwait(false);
            throw;
        }
        try
        {
            await _broker.CompleteAsync(_hostId, key, result, owned, ct).ConfigureAwait(false);
        }
        catch
        {
            try { if (File.Exists(owned)) File.Delete(owned); } catch { }
            throw;
        }
    }
}
