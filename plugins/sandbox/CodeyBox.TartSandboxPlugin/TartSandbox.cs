using System.Collections.Concurrent;
using System.Text;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.TartSandboxPlugin;

/// <summary>One host/guest directory pair synced back on teardown.</summary>
public sealed record TartWritableMountSync(string HostDirectory, string GuestDirectory);

/// <summary>
/// One live Tart VM. Disposal syncs writable host mounts back once and then
/// stops and deletes the VM; a prior <see cref="SuspendAsync"/> (or
/// <see cref="StopAndPreserveAsync"/>) flips preserve-on-dispose so disposal
/// becomes a no-op and the clone directory survives for a later resume.
/// </summary>
public sealed class TartSandbox : ISandbox, ISuspendableSandbox, IPreemptibleSandbox,
    IShutdownTeardownSandbox, IProviderOwnedSandbox, IPreserveOnDisposeSandbox, ITartSoftnetPolicyReport,
    IEgressFilterHealth
{
    private readonly ITartProcessRunner _runner;
    private readonly TartSshGuestTransport _transport;
    private readonly Func<TartSandboxOptions> _readOptions;
    private readonly SandboxSpec _spec;
    private readonly string _workingDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _log;
    private readonly Action<string> _untrack;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _inFlightExecs = new();
    private readonly object _ipSync = new();
    private readonly object _policySync = new();
    private List<TartWritableMountSync> _writableMounts = [];
    private TartNetworkMode _networkMode = TartNetworkMode.Nat;
    private List<string> _effectiveAllowCidrs = [];

    private int _disposed;
    private bool _preserveOnDispose;
    private bool _ownedByShutdownHandler;
    private ITartDetachedProcess? _vmProcess;

    internal TartSandbox(
        string vmName,
        string guestIp,
        ITartProcessRunner runner,
        TartSshGuestTransport transport,
        Func<TartSandboxOptions> readOptions,
        SandboxSpec spec,
        TimeProvider timeProvider,
        ILogger log,
        Action<string> untrack)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vmName);
        ArgumentException.ThrowIfNullOrWhiteSpace(guestIp);
        Id = vmName;
        VmName = vmName;
        GuestIp = guestIp;
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _readOptions = readOptions ?? throw new ArgumentNullException(nameof(readOptions));
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        _workingDirectory = string.IsNullOrWhiteSpace(spec.WorkingDirectory) ? "/work" : spec.WorkingDirectory;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _untrack = untrack ?? throw new ArgumentNullException(nameof(untrack));
    }

    public string Id { get; }

    public string VmName { get; }

    public string GuestIp { get; private set; }

    string IProviderOwnedSandbox.ProviderId => TartSandboxOptions.ProviderKind;

    bool IShutdownTeardownSandbox.IsOwnedByShutdownHandler => _ownedByShutdownHandler;

    void IShutdownTeardownSandbox.MarkOwnedByShutdownHandler() => _ownedByShutdownHandler = true;

    void IPreserveOnDisposeSandbox.DisablePreserveOnDispose() => _preserveOnDispose = false;

    public bool IsSuspended { get; private set; }

    /// <summary>Guest-network backend the VM was launched with (default NAT).</summary>
    public TartNetworkMode NetworkMode
    {
        get { lock (_policySync) return _networkMode; }
    }

    /// <summary>
    /// Exact CIDR allowlist installed via <c>--net-softnet-allow</c>
    /// (gateway first, then resolved <c>/32</c>s); empty in NAT mode.
    /// </summary>
    public IReadOnlyList<string> EffectiveAllowCidrs
    {
        get { lock (_policySync) return _effectiveAllowCidrs.ToList(); }
    }

    /// <summary>
    /// Records the effective Softnet policy at create time so the host
    /// verifier and operators can read exactly what the VM's filter allows.
    /// Called once by the provider before <c>tart run</c>; NAT keeps the
    /// defaults (empty allowlist).
    /// </summary>
    internal void SetNetworkPolicy(TartNetworkMode mode, IReadOnlyList<string> allowCidrs)
    {
        ArgumentNullException.ThrowIfNull(allowCidrs);
        lock (_policySync)
        {
            _networkMode = mode;
            _effectiveAllowCidrs = allowCidrs.ToList();
        }
    }

    internal void SetWritableMounts(List<TartWritableMountSync> mounts) =>
        _writableMounts = mounts ?? throw new ArgumentNullException(nameof(mounts));

    internal void AttachVmProcess(ITartDetachedProcess? process) => _vmProcess = process;

    /// <summary>
    /// Provider-host filter liveness for the host verifier: in Softnet mode
    /// the Softnet filter is hosted by the <c>tart run</c> process, so a
    /// positively-exited run process means the filter is gone. False only on
    /// that positive evidence — NAT mode (no filter), a missing handle
    /// (suspended, or resumed where the provider releases the handle), a
    /// disposed sandbox, or a throwing probe all report alive/unknown and
    /// leave the verdict to the guest canary.
    /// </summary>
    bool IEgressFilterHealth.IsFilterAlive
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0)
                return true;
            TartNetworkMode mode;
            lock (_policySync)
                mode = _networkMode;
            if (mode != TartNetworkMode.Softnet)
                return true;
            var process = Volatile.Read(ref _vmProcess);
            if (process is null)
                return true;
            try
            {
                return !process.HasExited;
            }
            catch (Exception)
            {
                // A throwing liveness probe is not positive evidence of
                // filter death; the guest canary stays the authority.
                return true;
            }
        }
    }

    internal void RefreshIp(string guestIp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guestIp);
        lock (_ipSync)
            GuestIp = guestIp;
    }

    private string CurrentIp()
    {
        lock (_ipSync)
            return GuestIp;
    }

    public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exec);
        ThrowIfDisposed();
        if (exec.Argv.Count == 0)
            throw new ArgumentException("Exec requires at least one argv entry.", nameof(exec));
        if (exec.StreamOutputWithoutKill && exec.KillOnOutputLimit)
            throw new ArgumentException("StreamOutputWithoutKill requires KillOnOutputLimit=false.", nameof(exec));

        var opts = _readOptions();
        var maxStdout = exec.MaxStdoutBytes ?? opts.MaxExecOutputBytes;
        var maxStderr = exec.MaxStderrBytes ?? opts.MaxExecOutputBytes;
        if (maxStdout <= 0 || maxStderr <= 0)
            throw new ArgumentOutOfRangeException(nameof(exec), "Output caps must be positive.");
        if (exec.Stdin is not null && Encoding.UTF8.GetByteCount(exec.Stdin) > opts.MaxStdinBytes)
            throw new InvalidOperationException($"Exec stdin exceeds the {opts.MaxStdinBytes}-byte bound.");

        var deadline = _timeProvider.GetUtcNow() + (_spec.Limits.WallClock ?? TimeSpan.FromHours(6));
        var ip = CurrentIp();

        string remoteCommand;
        if (exec.EnvironmentContainsSecrets && exec.ExtraEnvironment is { Count: > 0 })
        {
            var staged = TartShellCommand.SecretEnvStagingDirectory + "/env-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture);
            var fileContent = TartShellCommand.BuildSecretEnvFile(_spec.Environment, exec, opts.MaxEnvironmentBytes);
            try
            {
                await _transport.WriteFileAsync(ip, staged, fileContent, ControlTimeout(opts), ct).ConfigureAwait(false);
            }
            catch (TartCliException ex)
            {
                throw TartFailureClassification.ToUnavailable(ex);
            }
            remoteCommand = TartShellCommand.BuildSourced(exec, exec.WorkingDirectory ?? _workingDirectory, staged, opts.MaxCommandBytes);
        }
        else
        {
            remoteCommand = TartShellCommand.Build(_spec.Environment, exec, exec.WorkingDirectory ?? _workingDirectory, opts.MaxEnvironmentBytes, opts.MaxCommandBytes);
        }

        var stdout = new OutputSink(exec.StreamOutputWithoutKill ? (exec.MaxRetainedStdoutBytes ?? SandboxExec.DefaultStreamedOutputTailBytes) : maxStdout, tailOnly: exec.StreamOutputWithoutKill);
        var stderr = new OutputSink(exec.StreamOutputWithoutKill ? (exec.MaxRetainedStderrBytes ?? SandboxExec.DefaultStreamedOutputTailBytes) : maxStderr, tailOnly: exec.StreamOutputWithoutKill);
        var capTripped = 0;

        using var capCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, capCts.Token);
        var inFlightId = Guid.NewGuid();
        _inFlightExecs.TryAdd(inFlightId, capCts);
        try
        {
            var statusFile = TartShellCommand.ExecStatusDirectory + "/st-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture);
            TartProcessResult streamed;
            try
            {
                var remaining = deadline - _timeProvider.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                    throw new SandboxExecutionUnavailableException(-1);
                // Host-memory backstop for the process pipe. Exec semantics
                // (kill vs tail) live in the sinks; the runner's own
                // truncated tail is ignored — chunks already reached the
                // callbacks and the sinks.
                var pipeCap = exec.StreamOutputWithoutKill
                    ? Math.Max(
                        exec.MaxRetainedStdoutBytes ?? SandboxExec.DefaultStreamedOutputTailBytes,
                        exec.MaxRetainedStderrBytes ?? SandboxExec.DefaultStreamedOutputTailBytes)
                    : Math.Max(maxStdout, maxStderr);
                var wrapped = TartShellCommand.WrapWithStatusFile(remoteCommand, statusFile);
                streamed = await _transport.ExecRawAsync(
                    ip,
                    wrapped,
                    exec.Stdin,
                    remaining,
                    chunk => TripOnCap(chunk, stdout, exec.StdoutChunkCallback, maxStdout, exec, ref capTripped, capCts),
                    chunk => TripOnCap(chunk, stderr, exec.StderrChunkCallback, maxStderr, exec, ref capTripped, capCts),
                    maxOutputBytes: pipeCap,
                    linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (Volatile.Read(ref capTripped) == 1)
            {
                return new SandboxExecResult(-1, stdout.ToString(), stderr.ToString(), stdout.OverCap, stderr.OverCap);
            }
            catch (TartCliException ex)
            {
                throw TartFailureClassification.ToUnavailable(ex);
            }

            var guestExit = await ReadGuestExitAsync(ip, statusFile, streamed, ct).ConfigureAwait(false);
            return new SandboxExecResult(
                guestExit,
                stdout.ToString(),
                stderr.ToString(),
                stdout.OverCap,
                stderr.OverCap);
        }
        finally
        {
            _inFlightExecs.TryRemove(inFlightId, out _);
        }
    }

    /// <summary>
    /// Reads the guest exit code through the status-file channel. A bare SSH
    /// exit (255 with no status file) is a transport failure, never a guest
    /// verdict — this is what separates "the service said no" from "the work
    /// failed".
    /// </summary>
    private async Task<int> ReadGuestExitAsync(string ip, string statusFile, TartProcessResult streamed, CancellationToken ct)
    {
        var opts = _readOptions();
        var timeout = TimeSpan.FromSeconds(opts.CliTimeoutSeconds);
        TartProcessResult status;
        try
        {
            status = await _transport.ExecRawAsync(
                ip,
                TartShellCommand.BuildReadStatusCommand(statusFile),
                stdin: null, timeout, stdoutChunk: null, stderrChunk: null, maxOutputBytes: 64, ct).ConfigureAwait(false);
        }
        catch (TartCliException ex)
        {
            throw TartFailureClassification.ToUnavailable(ex);
        }
        finally
        {
            try
            {
                await _transport.ExecRawAsync(
                    ip,
                    TartShellCommand.BuildRemoveFileCommand(statusFile),
                    stdin: null, timeout, stdoutChunk: null, stderrChunk: null, maxOutputBytes: 64, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort: staging files under /tmp never affect the next exec.
            }
        }

        if (status.ExitCode == 0 && int.TryParse(status.Stdout.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var guestExit))
            return guestExit;

        throw TartFailureClassification.ToUnavailable(
            TartFailureClassification.ForExit("ssh", ["exec"], streamed.ExitCode, streamed.Stderr));
    }

    public Task KillActiveExecsAsync(CancellationToken ct = default)
    {
        _ = ct;
        foreach (var entry in _inFlightExecs.Values)
        {
            try
            {
                entry.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Raced with exec completion; the exec already observed its own teardown.
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>Writes a guest file (piped over SSH stdin, never argv).</summary>
    public Task WriteFileAsync(string guestPath, string content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ThrowIfDisposed();
        return _transport.WriteFileAsync(CurrentIp(), guestPath, content, ControlTimeout(_readOptions()), ct);
    }

    /// <summary>Reads a guest file, bounded by the provider's read-back cap.</summary>
    public Task<string> ReadFileAsync(string guestPath, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var opts = _readOptions();
        return _transport.ReadFileAsync(CurrentIp(), guestPath, opts.MaxReadBackBytes, ControlTimeout(opts), ct);
    }

    public async Task SyncStateToHostAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await SyncStateToHostCoreAsync(ct).ConfigureAwait(false);
    }

    internal async Task SyncStateToHostCoreAsync(CancellationToken ct)
    {
        var mounts = _writableMounts;
        if (mounts.Count == 0)
            return;
        var opts = _readOptions();
        var timeout = ControlTimeout(opts);
        foreach (var mount in mounts)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<string> entries;
            try
            {
                entries = await _transport.ListFilesAsync(CurrentIp(), mount.GuestDirectory, timeout, ct).ConfigureAwait(false);
            }
            catch (TartCliException ex)
            {
                throw TartFailureClassification.ToUnavailable(ex);
            }
            foreach (var relative in entries)
            {
                ct.ThrowIfCancellationRequested();
                var guestPath = TartGuestPath.Join(mount.GuestDirectory, relative);
                byte[] content;
                try
                {
                    content = await _transport.ReadFileBytesAsync(CurrentIp(), guestPath, opts.MaxReadBackBytes, timeout, ct).ConfigureAwait(false);
                }
                catch (TartCliException ex)
                {
                    throw TartFailureClassification.ToUnavailable(ex);
                }
                var hostPath = TartGuestPath.ContainHostPath(mount.HostDirectory, relative);
                var directory = Path.GetDirectoryName(hostPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                await File.WriteAllBytesAsync(hostPath, content, ct).ConfigureAwait(false);
            }
        }
    }

    public async Task SuspendAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var opts = _readOptions();
        await StopVmAsync(opts, ct).ConfigureAwait(false);
        DetachSpentVmProcess();
        _preserveOnDispose = true;
        IsSuspended = true;
    }

    public async Task StopAndPreserveAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var opts = _readOptions();
        await StopVmAsync(opts, ct).ConfigureAwait(false);
        DetachSpentVmProcess();
        _preserveOnDispose = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var opts = _readOptions();
        try
        {
            if (!_preserveOnDispose)
            {
                try
                {
                    // Core (unguarded): disposal already marked this instance
                    // disposed, and the sync-back must still run once.
                    await SyncStateToHostCoreAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Tart VM '{Vm}': teardown sync-back failed; continuing with stop/delete.", VmName);
                }
                await StopVmAsync(opts, CancellationToken.None).ConfigureAwait(false);
                await DeleteVmAsync(opts, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            KillVmProcessBestEffort();
            _untrack(Id);
        }
    }

    internal async Task StopVmAsync(TartSandboxOptions opts, CancellationToken ct)
    {
        var result = await _runner.RunAsync(
            new TartProcessSpec(opts.TartBinaryPath, ["stop", VmName], Stdin: null, TimeSpan.FromSeconds(opts.TransitionTimeoutSeconds)),
            stdoutChunk: null, stderrChunk: null, maxOutputBytes: 65536, ct).ConfigureAwait(false);
        if (result.ExitCode != 0 && !IsNotFound(result.Stderr))
            throw TartFailureClassification.ForExit(opts.TartBinaryPath, ["stop", VmName], result.ExitCode, result.Stderr);
    }

    internal async Task DeleteVmAsync(TartSandboxOptions opts, CancellationToken ct)
    {
        var result = await _runner.RunAsync(
            new TartProcessSpec(opts.TartBinaryPath, ["delete", VmName], Stdin: null, TimeSpan.FromSeconds(opts.TransitionTimeoutSeconds)),
            stdoutChunk: null, stderrChunk: null, maxOutputBytes: 65536, ct).ConfigureAwait(false);
        if (result.ExitCode != 0 && !IsNotFound(result.Stderr))
            throw TartFailureClassification.ForExit(opts.TartBinaryPath, ["delete", VmName], result.ExitCode, result.Stderr);
    }

    /// <summary>
    /// Drops the <c>tart run</c> handle after a stop/suspend without killing:
    /// the CLI stop already ended the process, and the spent handle must not
    /// condemn a later resume (which reinstalls the filter under a new,
    /// provider-released handle). After this the health signal reports
    /// unknown-alive and the guest canary stays the authority.
    /// </summary>
    private void DetachSpentVmProcess()
    {
        var process = Interlocked.Exchange(ref _vmProcess, null);
        if (process is null)
            return;
        try
        {
            process.Dispose();
        }
        catch (Exception)
        {
            // Best effort; the CLI stop above owns guest teardown.
        }
    }

    private void KillVmProcessBestEffort()
    {
        var process = Interlocked.Exchange(ref _vmProcess, null);
        if (process is null)
            return;
        try
        {
            process.Kill();
        }
        catch (Exception)
        {
            // Best effort; the CLI stop/delete above owns guest teardown.
        }
        try
        {
            process.Dispose();
        }
        catch (Exception)
        {
            // Best effort; see above.
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(TartSandbox));
    }

    private static TimeSpan ControlTimeout(TartSandboxOptions opts) =>
        TimeSpan.FromSeconds(opts.CliTimeoutSeconds);

    private static bool IsNotFound(string stderr) =>
        stderr.Contains("not found", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("no such", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("does not exist", StringComparison.OrdinalIgnoreCase);

    private static string LastLine(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? string.Empty;
    }

    private static void TripOnCap(
        string chunk,
        OutputSink sink,
        Action<string>? callback,
        int cap,
        SandboxExec exec,
        ref int tripped,
        CancellationTokenSource capCts)
    {
        sink.Append(chunk);
        try
        {
            callback?.Invoke(chunk);
        }
        catch (Exception)
        {
            // Chunk callbacks are best-effort observers; never fail the exec.
        }
        if (!exec.StreamOutputWithoutKill && exec.KillOnOutputLimit && sink.Length > cap
            && Interlocked.CompareExchange(ref tripped, 1, 0) == 0)
        {
            try
            {
                capCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Raced with exec completion; the cap flag still marks the result.
            }
        }
    }

    /// <summary>Bounded per-stream accumulator; streaming mode keeps a tail only.</summary>
    private sealed class OutputSink
    {
        private readonly int _cap;
        private readonly bool _tailOnly;
        private readonly StringBuilder _builder = new();
        private readonly Queue<string> _tail = new();
        private int _tailChars;
        private int _length;

        public OutputSink(int cap, bool tailOnly)
        {
            _cap = Math.Max(1, cap);
            _tailOnly = tailOnly;
        }

        public int Length => _length;

        public bool OverCap => _length > _cap && !_tailOnly;

        public void Append(string chunk)
        {
            _length += chunk.Length;
            if (!_tailOnly)
            {
                if (_builder.Length < _cap)
                    _builder.Append(chunk.Length <= _cap - _builder.Length ? chunk : chunk[..(_cap - _builder.Length)]);
                return;
            }
            // Tail mode: keep the last _cap characters across chunk
            // boundaries. A single chunk larger than the cap contributes
            // only its own tail — evicting it whole would lose everything.
            var text = chunk.Length > _cap ? chunk[^_cap..] : chunk;
            _tail.Enqueue(text);
            _tailChars += text.Length;
            while (_tailChars > _cap && _tail.TryDequeue(out var dropped))
                _tailChars -= dropped.Length;
        }

        public override string ToString()
        {
            if (!_tailOnly)
                return _builder.ToString();
            return string.Concat(_tail);
        }
    }
}
