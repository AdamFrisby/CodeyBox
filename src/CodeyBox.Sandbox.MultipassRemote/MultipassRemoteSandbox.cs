using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;
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
internal sealed class MultipassRemoteSandbox : IShutdownTeardownSandbox
{
    private readonly SandboxSpec _spec;
    private readonly IReadOnlyList<StagedBindMount> _stagedMounts;
    private readonly string _remoteSandboxRoot;
    private readonly IRemoteHostTransport _transport;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<ProcessRunResultLike>> _runRemoteMaybeGated;
    private readonly MultipassRemoteSandboxOptions _opts;
    private readonly ILogger _log;
    private readonly Action<RemoteSshTransportException> _onTransportFailure;
    private readonly Action<string> _onDispose;
    private readonly ConcurrentDictionary<CancellationTokenSource, byte> _activeExecCts = new();
    private int _disposed; // 0/1 via Interlocked

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
        Action<string> onDispose)
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

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
                    "Remote VM {Vm} stop exited {ExitCode} during dispose: {Detail}",
                    vmName,
                    stop.ExitCode,
                    stop.Stderr);
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
        //    delete the staged copy. A failed sync here is logged but does
        //    not block deletion — the merge phase will report missing
        //    artifacts upstream.
        foreach (var mount in _stagedMounts)
        {
            if (mount.SyncBackHostPath is not { } hostPath) continue;
            if (string.IsNullOrWhiteSpace(hostPath)) continue;
            try
            {
                await _transport.StageOutAsync(mount.RemoteStagedPath, hostPath, CancellationToken.None).ConfigureAwait(false);
            }
            catch (RemoteSshTransportException ex)
            {
                _onTransportFailure(ex);
                _log.LogWarning(ex,
                    "Sync-back from remote {Remote} to host {Host} failed (transport)",
                    mount.RemoteStagedPath, hostPath);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Sync-back from remote {Remote} to host {Host} failed",
                    mount.RemoteStagedPath, hostPath);
            }
        }

        // 3) Delete VM + staging dir.
        try
        {
            var delete = await _runRemoteMaybeGated(
                [opts.RemoteMultipassPath, "delete", "--purge", vmName],
                CancellationToken.None).ConfigureAwait(false);
            if (delete.ExitCode != 0)
            {
                _log.LogWarning(
                    "Remote VM {Vm} delete exited {ExitCode} during dispose: {Detail}",
                    vmName,
                    delete.ExitCode,
                    delete.Stderr);
            }
        }
        catch (Exception ex)
        {
            if (ex is RemoteSshTransportException transportEx)
                _onTransportFailure(transportEx);
            _log.LogWarning(ex, "Remote VM {Vm} delete failed during dispose", vmName);
        }
        try
        {
            await _transport.RunAsync(
                ["rm", "-rf", _remoteSandboxRoot],
                stdin: null,
                ct: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (ex is RemoteSshTransportException transportEx)
                _onTransportFailure(transportEx);
            _log.LogWarning(ex, "Remote staging dir {Dir} cleanup failed during dispose", _remoteSandboxRoot);
        }

        SandboxLiveCounter.Decrement();
        _onDispose(vmName);
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
}
