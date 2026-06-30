using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox;

namespace CodeyBox.Sandbox.MultipassRemote;

/// <summary>
/// Sandbox handle for a single remote multipass VM. Implements the orchestrator
/// contract: streaming <see cref="ExecAsync"/>, best-effort cancellation,
/// disposal that first syncs writable mounts back to the orchestrator host when
/// possible, then performs best-effort remote VM and staging cleanup. If the
/// executor host is lost before sync-back can run, active tracking is released
/// so the existing recovery and leak-reaper paths can reschedule the work and
/// reclaim remote state when the host returns.
///
/// <para>The implementation deliberately stays narrow. It does NOT implement
/// <see cref="IPreemptibleSandbox"/> or <see cref="ISuspendableSandbox"/>;
/// those would require durable remote VM identity and shutdown-handler
/// integration beyond the pooled-executor placement layer. It DOES report
/// itself as an <see cref="IShutdownTeardownSandbox"/> so the active-sandbox
/// snapshot is correctly typed and a future suspend implementation slots in
/// without changing the provider surface.</para>
/// </summary>
internal sealed class MultipassRemoteSandbox :
    IShutdownTeardownSandbox,
    IPrivilegedGuestFileHardeningSandbox,
    IHostQualifiedSandbox,
    IReleaseAdmissionOnHostLossSandbox,
    IRoutableSandbox
{
    private readonly SandboxSpec _spec;
    private readonly IReadOnlyList<StagedBindMount> _stagedMounts;
    private readonly string _remoteSandboxRoot;
    private readonly IRemoteHostTransport _transport;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<ProcessRunResultLike>> _runRemoteMaybeGated;
    private readonly MultipassRemoteSandboxOptions _opts;
    private readonly ILogger _log;
    private readonly Action<RemoteSshTransportException> _onTransportFailure;
    private readonly RemoteMultipassCleanup _cleanup;
    private readonly Action<string, string> _onDispose;
    private readonly ConcurrentDictionary<CancellationTokenSource, byte> _activeExecCts = new();
    private readonly SemaphoreSlim _disposeLock = new(1, 1);
    private int _disposed; // 0/1 via Interlocked
    private int _activeTrackingReleased;
    private int _executionTransportLost;
    private int _releaseAdmissionAfterHostLoss;

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
        Action<string, string> onDispose,
        string? hostAddress = null)
    {
        Id = vmName;
        HostId = hostId;
        HostAddress = hostAddress;
        _spec = spec;
        _stagedMounts = stagedMounts;
        _remoteSandboxRoot = remoteSandboxRoot;
        _transport = transport;
        _runRemoteMaybeGated = runRemoteMaybeGated;
        _opts = opts;
        _log = log;
        _onTransportFailure = onTransportFailure;
        _onDispose = onDispose;
        _cleanup = new RemoteMultipassCleanup(
            opts,
            transport,
            runRemoteMaybeGated,
            onTransportFailure,
            log);
    }

    public string Id { get; }
    public string HostId { get; }
    public string? HostAddress { get; internal set; }

    // Reaper-exemption gate. DisposeAsync sets _disposed=1 up front to reject
    // new ExecAsync calls, then performs fallible sync-back. While sync-back
    // might still preserve a successful agent commit, the sandbox must remain
    // active/reaper-exempt. After sync-back succeeds, or after an exec-time
    // host loss proves sync-back cannot run and the work will be rescheduled,
    // active tracking is released so the leak reaper can reclaim any remaining
    // remote VM/staging state when the host is reachable.
    internal bool IsTrackedActive => Volatile.Read(ref _activeTrackingReleased) == 0;
    public bool ReleaseAdmissionAfterHostLoss => Volatile.Read(ref _releaseAdmissionAfterHostLoss) != 0;

    public WorkItemId? OwningWorkItemId => _spec.TimingWorkItemId;

    public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(MultipassRemoteSandbox));
        if (exec.Argv.Count == 0)
            throw new ArgumentException("Argv must be non-empty.", nameof(exec));

        var opts = _opts;
        var workdir = exec.WorkingDirectory ?? _spec.WorkingDirectory ?? SandboxConventions.WorkDir;
        var effectiveEnvironment = exec.ExtraEnvironment is { Count: > 0 }
            ? new Dictionary<string, string>(exec.ExtraEnvironment, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        exec.ApplyEnvironmentRemovals(name => effectiveEnvironment.Remove(name));

        IReadOnlyList<string> remoteArgv;
        string? transportStdin;
        if (exec.EnvironmentContainsSecrets && effectiveEnvironment.Count > 0)
        {
            var environmentFile = SandboxEnvironmentVariablePolicy.BuildShellEnvironmentFileContent(effectiveEnvironment);
            var commandStdin = exec.Stdin ?? string.Empty;
            remoteArgv =
            [
                opts.RemoteMultipassPath,
                "exec",
                Id,
                "--",
                "bash",
                "-c",
                SecretEnvironmentBootstrapScript,
                "codeybox-secret-environment",
                Encoding.UTF8.GetByteCount(environmentFile).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Encoding.UTF8.GetByteCount(commandStdin).ToString(System.Globalization.CultureInfo.InvariantCulture),
                workdir,
                exec.EnvironmentVariablesToUnset.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                .. exec.EnvironmentVariablesToUnset,
                .. exec.Argv,
            ];
            transportStdin = environmentFile + commandStdin;
        }
        else
        {
            // Build the in-VM command: `cd <wd> && <argv...>` so subsequent execs
            // honour the requested working directory without depending on a
            // multipass --working-directory flag (versions differ).
            var quotedArgv = QuoteArgvForShell(exec.Argv);
            var inVmScript = new StringBuilder();
            inVmScript.Append("cd ").Append(QuoteShellWord(workdir)).Append(" && ");

            foreach (var name in exec.EnvironmentVariablesToUnset)
                inVmScript.Append("unset -- ").Append(QuoteShellWord(name)).Append(" && ");

            if (effectiveEnvironment.Count > 0)
            {
                foreach (var (k, v) in effectiveEnvironment)
                {
                    ValidateEnvKey(k);
                    inVmScript.Append(k).Append('=').Append(QuoteShellWord(v)).Append(' ');
                }
            }
            inVmScript.Append(quotedArgv);

            // multipass exec <vm> -- bash -lc 'cd ... && ...'
            remoteArgv =
            [
                opts.RemoteMultipassPath,
                "exec",
                Id,
                "--",
                "bash",
                "-lc",
                inVmScript.ToString(),
            ];
            transportStdin = exec.Stdin;
        }

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
                    stdin: transportStdin,
                    linkedCts.Token,
                    stdoutChunkCallback: OnStdout,
                    stderrChunkCallback: OnStderr,
                    maxStdoutBytes: exec.MaxStdoutBytes,
                    maxStderrBytes: exec.MaxStderrBytes,
                    killOnOutputLimit: exec.KillOnOutputLimit).ConfigureAwait(false);

                var stdoutLimitExceeded = stdoutLimitHit || run.StdoutLimitExceeded;
                var stderrLimitExceeded = stderrLimitHit || run.StderrLimitExceeded;

                return new SandboxExecResult(
                    ExitCode: run.ExitCode,
                    Stdout: ApplyOutputLimit(run.Stdout, exec.MaxStdoutBytes, stdoutLimitExceeded),
                    Stderr: ApplyOutputLimit(run.Stderr, exec.MaxStderrBytes, stderrLimitExceeded),
                    StdoutLimitExceeded: stdoutLimitExceeded,
                    StderrLimitExceeded: stderrLimitExceeded);
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

    private const string SecretEnvironmentBootstrapScript =
        """
        set -eu
        umask 077
        codeybox_env_file=$(mktemp)
        codeybox_stdin_file=$(mktemp)
        trap 'rm -f "$codeybox_env_file" "$codeybox_stdin_file"' EXIT
        dd if=/dev/stdin of="$codeybox_env_file" bs=1 count="$1" status=none
        dd if=/dev/stdin of="$codeybox_stdin_file" bs=1 count="$2" status=none
        codeybox_workdir=$3
        codeybox_unset_count=$4
        shift 4
        set -a
        . "$codeybox_env_file"
        set +a
        while [ "$codeybox_unset_count" -gt 0 ]; do
            unset -- "$1"
            shift
            codeybox_unset_count=$((codeybox_unset_count - 1))
        done
        cd "$codeybox_workdir"
        "$@" < "$codeybox_stdin_file"
        """;

    public async Task KillActiveExecsAsync(CancellationToken ct = default)
    {
        // Best-effort: cancel every in-flight exec's linked token. The SSH
        // child observing cancellation tears down the current remote command.
        foreach (var (cts, _) in _activeExecCts)
        {
            try { cts.Cancel(); } catch { }
        }

        var opts = _opts;
        var kill = await _runRemoteMaybeGated(
            [
                opts.RemoteMultipassPath,
                "exec",
                Id,
                "--",
                "bash",
                "-lc",
                KillSameUserProcessesScript,
            ],
            ct).ConfigureAwait(false);
        if (kill.ExitCode != 0)
        {
            _log.LogWarning(
                "Best-effort process cleanup for remote multipass sandbox {Name} returned exit {ExitCode}: {Stderr}",
                Id,
                kill.ExitCode,
                kill.Stderr);
        }
    }

    private const string KillSameUserProcessesScript =
        """
        set -eu
        self=$$
        parent=$PPID
        uid=$(id -u)
        list_pids() {
          ps -eo pid=,ppid=,uid= |
            awk -v uid="$uid" -v self="$self" -v parent="$parent" \
              '$3 == uid && $1 != self && $1 != parent { print $1 }'
        }
        pids=$(list_pids || true)
        if [ -n "$pids" ]; then
          kill -TERM $pids 2>/dev/null || true
          sleep 1
          pids=$(list_pids || true)
          if [ -n "$pids" ]; then
            kill -KILL $pids 2>/dev/null || true
          fi
        fi
        """;

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
                Volatile.Write(ref _releaseAdmissionAfterHostLoss, 1);
                ReleaseActiveTracking(vmName);
                return;
            }

            // 3) Delete VM + staging dir. Cleanup failures after sync-back are
            //    infrastructure hygiene, not a reason to replay completed work.
            await _cleanup.TryDeleteVmAndStagingAsync(vmName, _remoteSandboxRoot, CancellationToken.None).ConfigureAwait(false);

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
            try
            {
                await _cleanup.DeleteVmAndStagingOrThrowAsync(Id, _remoteSandboxRoot, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (RemoteSshTransportException ex)
            {
                throw BuildDisposeDeferred("leak-cleanup", "remote-cleanup-unconfirmed", ex);
            }
            catch (RemoteHostProvisioningException ex)
            {
                throw BuildDisposeDeferred("leak-cleanup", "remote-cleanup-unconfirmed", ex);
            }
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

    private static string SyncBackErrorClass(RemoteSshTransportException ex) =>
        ex.Kind is RemoteSshTransportFailureKind.ContentValidation or RemoteSshTransportFailureKind.ResourceLimit
            ? "remote-syncback-invalid-content"
            : "remote-syncback-failed";

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

    private static string ApplyOutputLimit(string value, int? maxBytes, bool limitExceeded) =>
        limitExceeded && maxBytes is { } cap
            ? TruncateUtf8(value, cap)
            : value;

    private static string TruncateForLog(string s, int max = 200)
        => RemoteMultipassText.TruncateForLog(s, max);
}
