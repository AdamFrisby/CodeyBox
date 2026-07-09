using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox;

namespace CodeyBox.Sandbox.MultipassRemote;

/// <summary>
/// Sandbox handle for a single remote multipass VM. Implements the orchestrator
/// contract: streaming <see cref="ExecAsync"/>, best-effort cancellation,
/// disposal that stops + deletes the remote VM and rsync's writable mounts
/// back to the orchestrator host so the merge phase can see them.
///
/// <para>The implementation deliberately stays narrow. It does NOT implement
/// <see cref="IPreemptibleSandbox"/> or <see cref="ISuspendableSandbox"/>;
/// those would require durable remote VM identity and shutdown-handler
/// integration beyond the pooled-executor placement layer. It DOES report
/// itself as an <see cref="IShutdownTeardownSandbox"/> so the active-sandbox
/// snapshot is correctly typed and a future suspend implementation slots in
/// without changing the provider surface.</para>
/// </summary>
internal sealed class MultipassRemoteSandbox : IShutdownTeardownSandbox, IHostQualifiedSandbox
{
    private readonly SandboxSpec _spec;
    private readonly IReadOnlyList<StagedBindMount> _stagedMounts;
    private readonly string _remoteSandboxRoot;
    private readonly IRemoteHostTransport _transport;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<ProcessRunResultLike>> _runRemoteMaybeGated;
    private readonly MultipassRemoteSandboxOptions _opts;
    private readonly ILogger _log;
    private readonly Action<RemoteSshTransportException> _onTransportFailure;
    private readonly Action<string, string> _onDispose;
    private readonly ConcurrentDictionary<CancellationTokenSource, byte> _activeExecCts = new();
    private readonly SemaphoreSlim _disposeLock = new(1, 1);
    private int _disposed; // 0/1 via Interlocked
    private int _activeTrackingReleased;
    private int _executionTransportLost;

    public MultipassRemoteSandbox(
        string vmName,
        string hostId,
        SandboxSpec spec,
        IReadOnlyList<StagedBindMount> stagedMounts,
        string remoteSandboxRoot,
        IRemoteHostTransport transport,
        Func<IReadOnlyList<string>, CancellationToken, Task<ProcessRunResultLike>> runRemoteMaybeGated,
        MultipassRemoteSandboxOptions opts,
        ILogger log,
        Action<RemoteSshTransportException> onTransportFailure,
        Action<string, string> onDispose)
    {
        Id = vmName;
        HostId = hostId;
        _spec = spec;
        _stagedMounts = stagedMounts;
        _remoteSandboxRoot = remoteSandboxRoot;
        _transport = transport;
        _runRemoteMaybeGated = runRemoteMaybeGated;
        _opts = opts;
        _log = log;
        _onTransportFailure = onTransportFailure;
        _onDispose = onDispose;
    }

    public string Id { get; }
    public string HostId { get; }

    // Reaper-exemption gate. DisposeAsync sets _disposed=1 up front to reject
    // new ExecAsync calls, then performs fallible sync-back. While sync-back
    // might still preserve a successful agent commit, the sandbox must remain
    // active/reaper-exempt. After sync-back succeeds, or after an exec-time
    // host loss proves sync-back cannot run and the work will be rescheduled,
    // active tracking is released so the leak reaper can reclaim any remaining
    // remote VM/staging state when the host is reachable.
    internal bool IsTrackedActive => Volatile.Read(ref _activeTrackingReleased) == 0;
    internal MultipassRemoteSandboxOptions HostOptions => _opts;
    internal IRemoteHostTransport Transport => _transport;

    public WorkItemId? OwningWorkItemId => _spec.TimingWorkItemId;

    public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(MultipassRemoteSandbox));
        if (exec.Argv.Count == 0)
            throw new ArgumentException("Argv must be non-empty.", nameof(exec));

        var opts = _opts;
        var workdir = exec.WorkingDirectory ?? _spec.WorkingDirectory ?? SandboxConventions.WorkDir;

        // Build the in-VM command: `cd <wd> && <argv...>` so subsequent execs
        // honour the requested working directory without depending on a
        // multipass --working-directory flag (versions differ).
        var quotedArgv = QuoteArgvForShell(exec.Argv);
        var inVmScript = new StringBuilder();
        inVmScript.Append("cd ").Append(QuoteShellWord(workdir)).Append(" && ");

        if (exec.ExtraEnvironment is not null && exec.ExtraEnvironment.Count > 0)
        {
            foreach (var (k, v) in exec.ExtraEnvironment)
            {
                ValidateEnvKey(k);
                inVmScript.Append(k).Append('=').Append(QuoteShellWord(v)).Append(' ');
            }
        }
        inVmScript.Append(quotedArgv);

        // multipass exec <vm> -- bash -lc 'cd ... && ...'
        var remoteArgv = new List<string>(8)
        {
            opts.RemoteMultipassPath, "exec", Id, "--", "bash", "-lc", inVmScript.ToString(),
        };

        // Chunk callbacks are pure live-update side channels; the transport's
        // ProcessRunResult.Stdout / Stderr remain authoritative for the final
        // SandboxExecResult. Output-limit caps are enforced by counting bytes
        // off the live stream and cancelling the linked token when exceeded.
        long stdoutBytes = 0, stderrBytes = 0;
        bool stdoutLimitHit = false, stderrLimitHit = false;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _activeExecCts[linkedCts] = 0;
        try
        {
            void OnStdout(string chunk)
            {
                if (chunk.Length == 0) return;
                if (exec.MaxStdoutBytes is { } cap)
                {
                    if (stdoutLimitHit) return;
                    var chunkBytes = Encoding.UTF8.GetByteCount(chunk);
                    if (stdoutBytes + chunkBytes > cap)
                    {
                        stdoutLimitHit = true;
                        if (exec.KillOnOutputLimit) { try { linkedCts.Cancel(); } catch { } }
                        return;
                    }
                    stdoutBytes += chunkBytes;
                }
                exec.StdoutChunkCallback?.Invoke(chunk);
            }
            void OnStderr(string chunk)
            {
                if (chunk.Length == 0) return;
                if (exec.MaxStderrBytes is { } cap)
                {
                    if (stderrLimitHit) return;
                    var chunkBytes = Encoding.UTF8.GetByteCount(chunk);
                    if (stderrBytes + chunkBytes > cap)
                    {
                        stderrLimitHit = true;
                        if (exec.KillOnOutputLimit) { try { linkedCts.Cancel(); } catch { } }
                        return;
                    }
                    stderrBytes += chunkBytes;
                }
                exec.StderrChunkCallback?.Invoke(chunk);
            }

            try
            {
                var run = await _transport.RunAsync(
                    remoteArgv,
                    stdin: exec.Stdin,
                    linkedCts.Token,
                    stdoutChunkCallback: OnStdout,
                    stderrChunkCallback: OnStderr).ConfigureAwait(false);

                return new SandboxExecResult(
                    ExitCode: run.ExitCode,
                    Stdout: stdoutLimitHit ? TruncateUtf8(run.Stdout, exec.MaxStdoutBytes!.Value) : run.Stdout,
                    Stderr: stderrLimitHit ? TruncateUtf8(run.Stderr, exec.MaxStderrBytes!.Value) : run.Stderr,
                    StdoutLimitExceeded: stdoutLimitHit,
                    StderrLimitExceeded: stderrLimitHit);
            }
            catch (RemoteSshTransportException ex)
            {
                _onTransportFailure(ex);
                if (ex.IsHostTransportFailure)
                    Volatile.Write(ref _executionTransportLost, 1);
                throw new SandboxProvisioningDeferredException(
                    provider: "multipass-remote",
                    operation: "exec",
                    errorClass: "remote-host-unreachable",
                    detail: $"host={HostId}; vm={Id}; {ex.Message}",
                    recheckIn: opts.PlacementRecheckIn,
                    innerException: ex);
            }
            catch (OperationCanceledException) when ((stdoutLimitHit || stderrLimitHit) && !ct.IsCancellationRequested)
            {
                // The output-limit watchdog cancelled the linked token to
                // tear down the SSH child. The caller's own cancellation
                // wasn't triggered, so we synthesize a result rather than
                // bubbling OCE — matching what the local exec providers do.
                return new SandboxExecResult(
                    ExitCode: 137, // SIGKILL convention
                    Stdout: "",
                    Stderr: "",
                    StdoutLimitExceeded: stdoutLimitHit,
                    StderrLimitExceeded: stderrLimitHit);
            }
        }
        finally
        {
            _activeExecCts.TryRemove(linkedCts, out _);
        }
    }

    public async Task KillActiveExecsAsync(CancellationToken ct = default)
    {
        // Best-effort: cancel every in-flight exec's linked token. The
        // OpenSSH child observing cancellation will tear down the SSH
        // session, which kills the remote command.
        foreach (var (cts, _) in _activeExecCts)
        {
            try { cts.Cancel(); } catch { }
        }
        _ = ct;
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public bool IsOwnedByShutdownHandler { get; private set; }
    public void MarkOwnedByShutdownHandler() => IsOwnedByShutdownHandler = true;

    public async Task SyncStateToHostAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _activeTrackingReleased) != 0)
            return;

        await _disposeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _activeTrackingReleased) != 0)
                return;
            await SyncWritableMountsAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _disposeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _activeTrackingReleased) != 0)
            return;

        await _disposeLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _activeTrackingReleased) != 0)
                return;

            Volatile.Write(ref _disposed, 1);

            var opts = _opts;
            var vmName = Id;

            // 1) Try to cleanly stop the VM so background processes flush.
            try
            {
                var stop = await _runRemoteMaybeGated(
                    [opts.RemoteMultipassPath, "stop", "--time", "0", vmName],
                    CancellationToken.None).ConfigureAwait(false);
                if (stop.ExitCode != 0)
                {
                    _log.LogWarning(
                        "Remote VM {Vm} stop returned exit {ExitCode} during dispose: {Stderr}",
                        vmName,
                        stop.ExitCode,
                        TruncateForLog(stop.Stderr));
                }
            }
            catch (RemoteSshTransportException ex)
            {
                _onTransportFailure(ex);
                _log.LogWarning(ex, "Remote VM {Vm} stop failed during dispose (transport)", vmName);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Remote VM {Vm} stop failed during dispose", vmName);
            }

            // 2) Sync writable mounts back to the orchestrator host BEFORE we
            //    delete the staged copy. If this fails, keep the remote staged
            //    data intact and surface infrastructure deferral to the caller.
            try
            {
                await SyncWritableMountsAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (SandboxProvisioningDeferredException ex) when (ShouldReleaseAfterExecutionHostLoss(ex))
            {
                _log.LogWarning(
                    ex,
                    "Remote VM {Vm} sync-back could not run after execution transport loss; releasing active tracking for leak reaper recovery",
                    vmName);
                ReleaseActiveTracking(vmName);
                return;
            }

            // 3) Delete VM + staging dir. Cleanup failures after sync-back are
            //    infrastructure hygiene, not a reason to replay completed work.
            var deleteConfirmed = await TryDeleteVmAsync(opts, vmName).ConfigureAwait(false);
            if (deleteConfirmed)
                await TryRemoveRemoteStagingAsync(vmName).ConfigureAwait(false);

            ReleaseActiveTracking(vmName);
        }
        finally
        {
            _disposeLock.Release();
        }
    }

    internal async Task ForceDisposeLeakedAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _activeTrackingReleased) != 0)
            return;

        await _disposeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _activeTrackingReleased) != 0)
                return;

            Volatile.Write(ref _disposed, 1);
            await DeleteVmAndStagingOrThrowAsync(_opts, Id, ct).ConfigureAwait(false);
            ReleaseActiveTracking(Id);
        }
        finally
        {
            _disposeLock.Release();
        }
    }

    private async Task SyncWritableMountsAsync(CancellationToken ct)
    {
        foreach (var mount in _stagedMounts)
        {
            if (mount.SyncBackHostPath is not { } hostPath) continue;
            if (string.IsNullOrWhiteSpace(hostPath)) continue;
            try
            {
                await _transport.StageOutAsync(mount.RemoteStagedPath, hostPath, ct).ConfigureAwait(false);
            }
            catch (RemoteSshTransportException ex)
            {
                if (ex.IsHostTransportFailure)
                    _onTransportFailure(ex);
                _log.LogWarning(ex,
                    "Sync-back from remote {Remote} to host {Host} failed ({FailureKind})",
                    mount.RemoteStagedPath, hostPath, ex.Kind);
                throw BuildDisposeDeferred("sync-back", SyncBackErrorClass(ex), ex);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Sync-back from remote {Remote} to host {Host} failed",
                    mount.RemoteStagedPath, hostPath);
                throw BuildDisposeDeferred("sync-back", "remote-syncback-failed", ex);
            }
        }
    }

    private async Task DeleteVmAndStagingOrThrowAsync(MultipassRemoteSandboxOptions opts, string vmName, CancellationToken ct)
    {
        ProcessRunResult delete;
        try
        {
            delete = await _transport.RunAsync(
                [opts.RemoteMultipassPath, "delete", "--purge", vmName],
                stdin: null,
                ct: ct).ConfigureAwait(false);
        }
        catch (RemoteSshTransportException ex)
        {
            _onTransportFailure(ex);
            throw BuildDisposeDeferred("leak-cleanup", "remote-cleanup-unconfirmed", ex);
        }

        if (delete.ExitCode != 0 && await SandboxMayStillExistAfterFailedDeleteAsync(opts, vmName, ct).ConfigureAwait(false))
        {
            var ex = new RemoteHostProvisioningException(
                HostId,
                "delete",
                $"Remote cleanup command 'delete' for VM '{vmName}' exited {delete.ExitCode}: {TruncateForLog(delete.Stderr)}");
            throw BuildDisposeDeferred("leak-cleanup", "remote-cleanup-unconfirmed", ex);
        }

        ProcessRunResult rm;
        try
        {
            rm = await _transport.RunAsync(
                ["rm", "-rf", _remoteSandboxRoot],
                stdin: null,
                ct: ct).ConfigureAwait(false);
        }
        catch (RemoteSshTransportException ex)
        {
            _onTransportFailure(ex);
            throw BuildDisposeDeferred("leak-cleanup", "remote-cleanup-unconfirmed", ex);
        }

        if (rm.ExitCode != 0)
        {
            var ex = new RemoteHostProvisioningException(
                HostId,
                "staging-cleanup",
                $"rm -rf {_remoteSandboxRoot} exited {rm.ExitCode}: {TruncateForLog(rm.Stderr)}");
            throw BuildDisposeDeferred("leak-cleanup", "remote-cleanup-unconfirmed", ex);
        }
    }

    private async Task<bool> SandboxMayStillExistAfterFailedDeleteAsync(MultipassRemoteSandboxOptions opts, string vmName, CancellationToken ct)
    {
        try
        {
            var info = await _transport.RunAsync(
                [opts.RemoteMultipassPath, "info", vmName, "--format", "json"],
                stdin: null,
                ct: ct).ConfigureAwait(false);
            if (info.ExitCode == 0)
                return true;
            if (IsInstanceNotFound(info.Stderr))
                return false;

            _log.LogWarning(
                "Could not prove remote sandbox {Vm} on host {HostId} was absent after delete --purge failed (info exit {ExitCode}): {Stderr}",
                vmName,
                HostId,
                info.ExitCode,
                TruncateForLog(info.Stderr));
            return true;
        }
        catch (RemoteSshTransportException ex)
        {
            _onTransportFailure(ex);
            _log.LogWarning(
                ex,
                "Could not prove remote sandbox {Vm} on host {HostId} was absent after delete --purge failed",
                vmName,
                HostId);
            return true;
        }
    }

    private static string SyncBackErrorClass(RemoteSshTransportException ex) =>
        ex.Kind is RemoteSshTransportFailureKind.ContentValidation or RemoteSshTransportFailureKind.ResourceLimit
            ? "remote-syncback-invalid-content"
            : "remote-syncback-failed";

    private static bool IsInstanceNotFound(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return false;

        return stderr.Contains("argument not found", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("instance not found", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> TryDeleteVmAsync(MultipassRemoteSandboxOptions opts, string vmName)
    {
        try
        {
            var result = await _runRemoteMaybeGated(
                [opts.RemoteMultipassPath, "delete", "--purge", vmName],
                CancellationToken.None).ConfigureAwait(false);
            if (result.ExitCode == 0)
                return true;

            if (!await SandboxMayStillExistAfterFailedDeleteAsync(opts, vmName, CancellationToken.None).ConfigureAwait(false))
            {
                _log.LogWarning(
                    "Remote VM {Vm} on host {HostId} was already absent after delete --purge exited {ExitCode}; continuing staging cleanup",
                    vmName,
                    HostId,
                    result.ExitCode);
                return true;
            }

            var ex = new RemoteHostProvisioningException(
                HostId,
                "delete",
                $"Remote cleanup command 'delete' for VM '{vmName}' exited {result.ExitCode}: {TruncateForLog(result.Stderr)}");
            _log.LogWarning(ex, "Remote VM {Vm} cleanup operation delete failed; leaving it for leak reaper cleanup", vmName);
            return false;
        }
        catch (RemoteSshTransportException ex)
        {
            _onTransportFailure(ex);
            _log.LogWarning(ex, "Remote VM {Vm} cleanup operation delete failed (transport); leaving it for leak reaper cleanup", vmName);
            return false;
        }
    }

    private async Task TryRemoveRemoteStagingAsync(string vmName)
    {
        try
        {
            var result = await _transport.RunAsync(
                ["rm", "-rf", _remoteSandboxRoot],
                stdin: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                _log.LogWarning(
                    "Remote VM {Vm} staging cleanup exited {ExitCode}: {Stderr}",
                    vmName,
                    result.ExitCode,
                    TruncateForLog(result.Stderr));
            }
        }
        catch (RemoteSshTransportException ex)
        {
            _onTransportFailure(ex);
            _log.LogWarning(ex, "Remote VM {Vm} staging cleanup failed (transport)", vmName);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Remote VM {Vm} staging cleanup failed", vmName);
        }
    }

    private SandboxProvisioningDeferredException BuildDisposeDeferred(
        string operation,
        string errorClass,
        Exception inner) =>
        new(
            provider: "multipass-remote",
            operation: operation,
            errorClass: errorClass,
            detail: $"host={HostId}; vm={Id}; {inner.Message}",
            recheckIn: _opts.PlacementRecheckIn,
            retainedSandboxName: Id,
            retainedSandboxHostId: HostId,
            innerException: inner);

    private bool ShouldReleaseAfterExecutionHostLoss(SandboxProvisioningDeferredException ex) =>
        Volatile.Read(ref _executionTransportLost) != 0
        && ex.InnerException is RemoteSshTransportException { IsHostTransportFailure: true };

    private void ReleaseActiveTracking(string vmName)
    {
        SandboxLiveCounter.Decrement();
        Volatile.Write(ref _activeTrackingReleased, 1);
        _onDispose(HostId, vmName);
    }

    private static string QuoteArgvForShell(IReadOnlyList<string> argv)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < argv.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(QuoteShellWord(argv[i]));
        }
        return sb.ToString();
    }

    private static string QuoteShellWord(string s)
    {
        if (s.Length == 0) return "''";
        return "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private static void ValidateEnvKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("Environment key cannot be empty.", nameof(key));
        foreach (var ch in key)
        {
            if (!(char.IsLetterOrDigit(ch) || ch == '_'))
                throw new ArgumentException($"Environment key '{key}' contains invalid character '{ch}'.", nameof(key));
        }
    }

    private static string TruncateUtf8(string s, int maxBytes)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (Encoding.UTF8.GetByteCount(s) <= maxBytes) return s;
        var used = 0;
        for (var i = 0; i < s.Length;)
        {
            var charCount = char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
            var b = Encoding.UTF8.GetByteCount(s.AsSpan(i, charCount));
            if (used + b > maxBytes) return s[..i];
            used += b;
            i += charCount;
        }
        return s;
    }

    private static string TruncateForLog(string s, int max = 200)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var trimmed = s.Trim();
        if (trimmed.Length <= max) return trimmed;
        return trimmed[..max] + "...";
    }
}
