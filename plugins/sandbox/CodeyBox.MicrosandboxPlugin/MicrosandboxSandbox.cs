using System.Collections.Concurrent;
using System.Text;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.MicrosandboxPlugin;

/// <summary>One mount planned for a microsandbox sandbox.</summary>
internal sealed record MicrosandboxMountPlan(
    string SandboxPath,
    string? HostPath,
    bool ReadOnly,
    bool IsGuestDir);

/// <summary>
/// A live microsandbox microVM. Execution runs through the server exec API:
/// an exec is started with an argv array plus environment/stdin, then polled
/// until it stops reporting <c>running</c>; incremental stdout/stderr deltas
/// are delivered to the caller's chunk callbacks as they arrive.
/// Cancellation and output-limit trips kill the guest process. Host file
/// mounts are staged into the sandbox at create and writable file mounts are
/// synced back to the host by <see cref="ISandbox.SyncStateToHostAsync"/> and
/// once more at disposal. Paths are canonicalize-then-contained on both sides.
/// </summary>
internal sealed class MicrosandboxSandbox :
    IPreemptibleSandbox,
    IPreserveOnDisposeSandbox,
    ISuspendableSandbox,
    IShutdownTeardownSandbox,
    IProviderOwnedSandbox,
    IActiveSandboxLease,
    ISandboxExecOutputLimits
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private const int MaxArgvEntryBytes = 32 * 1024;
    private const int MaxArgvEntries = 512;
    private const int WallClockTimeoutExitCode = 124;
    private const int ExecutionUnavailableExitCode = 255;
    private const int StderrPreviewCapBytes = 64 * 1024;
    private static readonly TimeSpan DisposeGatePollInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan DisposeGateWaitTimeout = TimeSpan.FromSeconds(30);

    private readonly string _name;
    private readonly SandboxSpec _spec;
    private readonly Func<MicrosandboxSandboxOptions> _readOptions;
    private readonly MicrosandboxApiClient _api;
    private readonly Func<MicrosandboxEndpoint> _endpointFactory;
    private readonly IReadOnlyList<MicrosandboxMountPlan> _mounts;
    private readonly Action<string> _onDisposed;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, byte> _activeExecs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _execGate = new(1, 1);
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private int _ownedByShutdownHandler;
    private int _preserveOnDispose;
    private int _suspended;
    private int _disposing;
    private int _disposed;
    private int _activeTrackingReleased;

    internal MicrosandboxSandbox(
        string name,
        SandboxSpec spec,
        Func<MicrosandboxSandboxOptions> readOptions,
        MicrosandboxApiClient api,
        Func<MicrosandboxEndpoint> endpointFactory,
        IReadOnlyList<MicrosandboxMountPlan> mounts,
        Action<string> onDisposed,
        TimeProvider clock,
        ILogger log)
    {
        _name = name;
        _spec = spec;
        _readOptions = readOptions ?? throw new ArgumentNullException(nameof(readOptions));
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _endpointFactory = endpointFactory ?? throw new ArgumentNullException(nameof(endpointFactory));
        _mounts = mounts;
        _onDisposed = onDisposed;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public string Id => _name;
    public string ProviderId => MicrosandboxSandboxOptions.ProviderKind;
    public bool IsOwnedByShutdownHandler => Volatile.Read(ref _ownedByShutdownHandler) == 1;
    public void MarkOwnedByShutdownHandler() => Interlocked.Exchange(ref _ownedByShutdownHandler, 1);
    public bool IsSuspended => Volatile.Read(ref _suspended) == 1;
    public long? MemoryBytes => _spec.Limits.MemoryBytes;
    public void ReleaseActiveTracking() => Interlocked.Exchange(ref _activeTrackingReleased, 1);
    public void DisablePreserveOnDispose() => Interlocked.Exchange(ref _preserveOnDispose, 0);
    public int MaxStdoutBytes => _readOptions().MaxExecOutputBytes;
    public int MaxStderrBytes => _readOptions().MaxExecOutputBytes;

    internal static bool IsValidAbsolutePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/'))
            return false;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments.All(s => s is not "." and not "..");
    }

    internal static string ContainGuestPath(string path)
    {
        if (!IsValidAbsolutePath(path))
            throw new ArgumentException($"Guest path '{path}' is not an absolute contained path.", nameof(path));
        return path;
    }

    // ------------------------------------------------------------------
    // Exec
    // ------------------------------------------------------------------

    public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exec);
        if (exec.Argv.Count == 0 || exec.Argv.Count > MaxArgvEntries)
            throw new ArgumentException($"Exec argv must hold 1–{MaxArgvEntries} entries.", nameof(exec));
        foreach (var arg in exec.Argv)
        {
            if (arg is null)
                throw new ArgumentException("Exec argv must not contain null entries.", nameof(exec));
            if (Utf8.GetByteCount(arg) > MaxArgvEntryBytes)
                throw new ArgumentException("Exec argv entry exceeds the size limit.", nameof(exec));
        }

        var opts = _readOptions();
        var stdoutCap = exec.MaxStdoutBytes ?? opts.MaxExecOutputBytes;
        var stderrCap = exec.MaxStderrBytes ?? opts.MaxExecOutputBytes;
        var env = MergeEnvironment(exec);
        var stdinBase64 = EncodeBounded(exec.Stdin, opts.MaxExecInputBytes, "stdin");
        var envBytes = env.Sum(kv => Utf8.GetByteCount(kv.Key) + Utf8.GetByteCount(kv.Value));
        if (envBytes > opts.MaxExecInputBytes)
            throw new ArgumentException("Exec environment exceeds the input payload bound.", nameof(exec));

        var endpoint = _endpointFactory();
        var workdir = string.IsNullOrWhiteSpace(exec.WorkingDirectory) ? _spec.WorkingDirectory : exec.WorkingDirectory;
        if (!IsValidAbsolutePath(workdir))
            throw new ArgumentException($"Exec working directory '{workdir}' is not an absolute contained path.", nameof(exec));

        await _execGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            MicrosandboxExecCreatedDto created;
            try
            {
                created = await _api.StartExecAsync(endpoint, _name, new MicrosandboxStartExecRequest(
                    exec.Argv, workdir, env, stdinBase64), ct).ConfigureAwait(false);
            }
            catch (MicrosandboxApiException ex) when (IsTransportLoss(ex))
            {
                return Unavailable($"exec start unreachable: {ex.Message}");
            }

            var execId = created.ExecId ?? throw new MicrosandboxApiException(
                MicrosandboxFailureKind.Unexpected, "start exec", "server returned no exec id");
            _activeExecs[execId] = 1;
            try
            {
                return await PollExecAsync(endpoint, execId, exec, stdoutCap, stderrCap, ct).ConfigureAwait(false);
            }
            finally
            {
                _activeExecs.TryRemove(execId, out _);
            }
        }
        finally
        {
            _execGate.Release();
        }
    }

    private async Task<SandboxExecResult> PollExecAsync(
        MicrosandboxEndpoint endpoint,
        string execId,
        SandboxExec exec,
        int stdoutCap,
        int stderrCap,
        CancellationToken ct)
    {
        var opts = _readOptions();
        var pollDelay = TimeSpan.FromMilliseconds(Math.Clamp(opts.PollIntervalMilliseconds, 100, 60_000));
        var wallClock = _spec.Limits.WallClock ?? TimeSpan.FromHours(6);
        var deadline = _clock.GetUtcNow() + wallClock;
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutDelivered = 0;
        var stderrDelivered = 0;
        var stdoutBytes = 0;
        var stderrBytes = 0;
        var stdoutOver = false;
        var stderrOver = false;

        while (true)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
            MicrosandboxExecStatusDto status;
            try
            {
                status = await _api.GetExecAsync(
                    endpoint, _name, execId,
                    MicrosandboxApiClient.BoundExecPollResponseBytes(stdoutCap, stderrCap), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await TryKillExecAsync(endpoint, execId).ConfigureAwait(false);
                throw;
            }
            catch (MicrosandboxApiException ex) when (IsTransportLoss(ex))
            {
                await TryKillExecAsync(endpoint, execId).ConfigureAwait(false);
                return Unavailable($"exec poll unreachable: {ex.Message}");
            }

            AppendDelta(stdout, status.Stdout, ref stdoutDelivered, ref stdoutBytes, exec.StdoutChunkCallback, stdoutCap, ref stdoutOver);
            AppendDelta(stderr, status.Stderr, ref stderrDelivered, ref stderrBytes, exec.StderrChunkCallback, stderrCap, ref stderrOver);

            if (stdoutOver || stderrOver)
            {
                if (exec.KillOnOutputLimit)
                {
                    await TryKillExecAsync(endpoint, execId).ConfigureAwait(false);
                    return new SandboxExecResult(
                        WallClockTimeoutExitCode, stdout.ToString(), stderr.ToString(),
                        StdoutLimitExceeded: stdoutOver, StderrLimitExceeded: stderrOver);
                }
            }

            if (!string.Equals(status.Status, "running", StringComparison.OrdinalIgnoreCase))
            {
                return new SandboxExecResult(
                    status.ExitCode ?? 0, stdout.ToString(), stderr.ToString(),
                    StdoutLimitExceeded: stdoutOver, StderrLimitExceeded: stderrOver);
            }

            if (_clock.GetUtcNow() >= deadline)
            {
                await TryKillExecAsync(endpoint, execId).ConfigureAwait(false);
                return new SandboxExecResult(
                    WallClockTimeoutExitCode, stdout.ToString(), stderr.ToString(),
                    StdoutLimitExceeded: stdoutOver, StderrLimitExceeded: stderrOver);
            }

            await Task.Delay(pollDelay, _clock, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation can land in the poll wait as well as inside the
                // poll request: either way the guest process must be killed so
                // a cancelled exec never keeps burning server-side resources.
                await TryKillExecAsync(endpoint, execId).ConfigureAwait(false);
                throw;
            }
        }
    }

    private static void AppendDelta(
        StringBuilder sink, string? full, ref int delivered, ref int sinkBytes, Action<string>? callback, int cap, ref bool overCap)
    {
        if (string.IsNullOrEmpty(full) || full.Length <= delivered)
            return;
        var delta = full[delivered..];
        delivered = full.Length;
        var deltaBytes = Encoding.UTF8.GetByteCount(delta);
        if (sinkBytes + deltaBytes > cap)
        {
            var room = Math.Max(0, cap - sinkBytes);
            delta = TruncateToBytes(delta, room);
            deltaBytes = Encoding.UTF8.GetByteCount(delta);
            overCap = true;
        }

        sink.Append(delta);
        sinkBytes += deltaBytes;
        try
        {
            callback?.Invoke(delta);
        }
        catch (Exception)
        {
        }
    }

    private static string TruncateToBytes(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
            return text;
        var bytes = Encoding.UTF8.GetBytes(text);
        var length = maxBytes;
        while (length > 0 && (bytes[length] & 0xC0) == 0x80)
            length--;
        return Encoding.UTF8.GetString(bytes, 0, Math.Max(0, length));
    }

    private Dictionary<string, string> MergeEnvironment(SandboxExec exec)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in _spec.Environment)
            merged[kv.Key] = kv.Value;
        if (exec.ExtraEnvironment is not null)
        {
            foreach (var kv in exec.ExtraEnvironment)
            {
                SandboxEnvironmentVariableName.Validate(kv.Key, nameof(exec));
                merged[kv.Key] = kv.Value ?? string.Empty;
            }
        }

        if (exec.EnvironmentVariablesToUnset.Count > SandboxExec.MaximumEnvironmentVariablesToUnset)
            throw new ArgumentException("Too many environment variables to unset.", nameof(exec));
        foreach (var name in exec.EnvironmentVariablesToUnset)
        {
            SandboxEnvironmentVariableName.Validate(name, nameof(exec));
            merged.Remove(name);
        }

        return merged;
    }

    private static string? EncodeBounded(string? text, long maxBytes, string what)
    {
        if (text is null)
            return null;
        var bytes = Utf8.GetBytes(text);
        if (bytes.Length > maxBytes)
            throw new ArgumentException($"Exec {what} exceeds the input payload bound.", nameof(text));
        return Convert.ToBase64String(bytes);
    }

    private static bool IsTransportLoss(MicrosandboxApiException ex) =>
        ex.Kind is MicrosandboxFailureKind.Unreachable
            or MicrosandboxFailureKind.ServerError
            or MicrosandboxFailureKind.Unexpected;

    private static SandboxExecResult Unavailable(string detail) =>
        new(ExecutionUnavailableExitCode, string.Empty, detail, ExecutionUnavailable: true);

    private async Task TryKillExecAsync(MicrosandboxEndpoint endpoint, string execId)
    {
        try
        {
            await _api.KillExecAsync(endpoint, _name, execId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Best-effort kill of exec {ExecId} in {Sandbox} failed", execId, _name);
        }
    }

    public async Task KillActiveExecsAsync(CancellationToken ct = default)
    {
        var endpoint = _endpointFactory();
        foreach (var execId in _activeExecs.Keys)
        {
            ct.ThrowIfCancellationRequested();
            await TryKillExecAsync(endpoint, execId).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------
    // File in/out
    // ------------------------------------------------------------------

    public async Task WriteFileAsync(string guestPath, byte[] content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var opts = _readOptions();
        if (content.LongLength > opts.MaxFileSyncBytes)
            throw new ArgumentException("File content exceeds the sync bound.", nameof(content));
        var path = ContainGuestPath(guestPath);
        var base64 = Convert.ToBase64String(content);
        if (base64.Length > opts.MaxFileSyncBase64Bytes)
            throw new ArgumentException("File payload exceeds the base64 bound.", nameof(content));
        ThrowIfDisposed();
        await _api.WriteFileAsync(_endpointFactory(), _name,
            new MicrosandboxWriteFileRequest(path, base64), ct).ConfigureAwait(false);
    }

    public async Task<byte[]?> ReadFileAsync(string guestPath, CancellationToken ct = default)
    {
        var opts = _readOptions();
        var path = ContainGuestPath(guestPath);
        ThrowIfDisposed();
        var dto = await _api.ReadFileAsync(_endpointFactory(), _name, path,
            opts.MaxFileSyncBase64Bytes + 64 * 1024, ct).ConfigureAwait(false);
        if (dto is null || !dto.Exists || string.IsNullOrEmpty(dto.ContentBase64))
            return dto is null || !dto.Exists ? null : [];
        if (dto.ContentBase64.Length > opts.MaxFileSyncBase64Bytes)
            throw new MicrosandboxApiException(MicrosandboxFailureKind.Unexpected, "read file",
                "server returned a file payload past the base64 bound; refusing to buffer it");
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(dto.ContentBase64);
        }
        catch (FormatException ex)
        {
            throw new MicrosandboxApiException(
                MicrosandboxFailureKind.Unexpected, "read file", "server returned malformed base64", ex);
        }

        if (bytes.LongLength > opts.MaxFileSyncBytes)
            throw new MicrosandboxApiException(MicrosandboxFailureKind.Unexpected, "read file",
                "server returned a file past the decoded bound; refusing to buffer it");
        return bytes;
    }

    internal async Task RunSetupCommandsAsync(IReadOnlyList<string> commands, CancellationToken ct)
    {
        foreach (var command in commands)
        {
            if (string.IsNullOrWhiteSpace(command))
                continue;
            var result = await ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-lc", command],
                MaxStdoutBytes = StderrPreviewCapBytes,
                MaxStderrBytes = StderrPreviewCapBytes,
            }, ct).ConfigureAwait(false);
            if (result.ExecutionUnavailable)
                throw new MicrosandboxApiException(MicrosandboxFailureKind.Unreachable, "setup",
                    "microsandbox server unreachable during setup commands");
            if (result.ExitCode != 0)
                throw new InvalidOperationException(
                    $"microsandbox setup command failed with exit {result.ExitCode}: {result.Stderr}");
        }
    }

    internal async Task PrepareFilesystemAsync(CancellationToken ct)
    {
        var opts = _readOptions();
        foreach (var mount in _mounts)
        {
            ct.ThrowIfCancellationRequested();
            if (mount.IsGuestDir || mount.HostPath is null)
            {
                await ExecAsync(new SandboxExec
                {
                    Argv = ["mkdir", "-p", mount.SandboxPath],
                    MaxStdoutBytes = StderrPreviewCapBytes,
                    MaxStderrBytes = StderrPreviewCapBytes,
                }, ct).ConfigureAwait(false);
                continue;
            }

            if (File.Exists(mount.HostPath))
            {
                var bytes = await ReadHostFileBoundedAsync(mount.HostPath, opts.MaxFileSyncBytes, ct).ConfigureAwait(false);
                await WriteFileAsync(mount.SandboxPath, bytes, ct).ConfigureAwait(false);
            }
            else if (Directory.Exists(mount.HostPath))
            {
                await ExecAsync(new SandboxExec
                {
                    Argv = ["mkdir", "-p", mount.SandboxPath],
                    MaxStdoutBytes = StderrPreviewCapBytes,
                    MaxStderrBytes = StderrPreviewCapBytes,
                }, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task<byte[]> ReadHostFileBoundedAsync(string path, long maxBytes, CancellationToken ct)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > maxBytes)
            throw new ArgumentException($"Host mount source '{path}' exceeds the sync bound.", nameof(path));
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }

    public async Task SyncStateToHostAsync(CancellationToken ct = default)
    {
        await _syncGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var mount in _mounts)
            {
                ct.ThrowIfCancellationRequested();
                if (mount.ReadOnly || mount.IsGuestDir || mount.HostPath is null)
                    continue;
                if (!File.Exists(mount.HostPath))
                    continue;
                var bytes = await ReadFileAsync(mount.SandboxPath, ct).ConfigureAwait(false);
                if (bytes is null)
                    continue;
                await WriteHostFileAtomicallyAsync(mount.HostPath, bytes, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private static async Task WriteHostFileAtomicallyAsync(string hostPath, byte[] bytes, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(hostPath);
        if (string.IsNullOrEmpty(directory))
            throw new ArgumentException($"Host path '{hostPath}' has no directory.", nameof(hostPath));
        var staging = Path.Combine(directory, $".codeybox-sync-{Guid.NewGuid():N}.tmp");
        await File.WriteAllBytesAsync(staging, bytes, ct).ConfigureAwait(false);
        try
        {
            File.Move(staging, hostPath, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(staging);
            }
            catch (Exception)
            {
            }

            throw;
        }
    }

    // ------------------------------------------------------------------
    // Suspend / preempt / teardown
    // ------------------------------------------------------------------

    public async Task SuspendAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _api.TransitionAsync(_endpointFactory(), _name, "pause", ct).ConfigureAwait(false);
        Interlocked.Exchange(ref _suspended, 1);
        Interlocked.Exchange(ref _preserveOnDispose, 1);
    }

    public async Task StopAndPreserveAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await SyncStateToHostAsync(ct).ConfigureAwait(false);
        await _api.TransitionAsync(_endpointFactory(), _name, "stop", ct).ConfigureAwait(false);
        Interlocked.Exchange(ref _preserveOnDispose, 1);
    }

    public async Task<SandboxRecoveryLease?> RetainForInfrastructureRecoveryAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var token = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        try
        {
            await _api.TransitionAsync(_endpointFactory(), _name, "stop", ct).ConfigureAwait(false);
        }
        catch (MicrosandboxApiException ex) when (ex.Kind == MicrosandboxFailureKind.NotFound)
        {
            return null;
        }

        await _api.SetLabelsAsync(_endpointFactory(), _name,
            new Dictionary<string, string>
            {
                [MicrosandboxSandboxOptions.RecoveryTokenHashLabelKey] = hash,
            }, ct).ConfigureAwait(false);
        Interlocked.Exchange(ref _preserveOnDispose, 1);
        return new SandboxRecoveryLease(
            MicrosandboxSandboxOptions.ProviderKind, _name, token);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposing, 1) == 1)
        {
            var deadline = _clock.GetUtcNow() + DisposeGateWaitTimeout;
            while (Volatile.Read(ref _disposed) == 0 && _clock.GetUtcNow() < deadline)
                await Task.Delay(DisposeGatePollInterval, _clock).ConfigureAwait(false);
            return;
        }

        try
        {
            if (Volatile.Read(ref _preserveOnDispose) == 1)
                return;
            try
            {
                await SyncStateToHostAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Best-effort pre-dispose sync of {Sandbox} failed", _name);
            }

            try
            {
                await _api.DeleteSandboxAsync(_endpointFactory(), _name, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Best-effort delete of {Sandbox} failed", _name);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _disposed, 1);
            _execGate.Dispose();
            _syncGate.Dispose();
            if (Volatile.Read(ref _activeTrackingReleased) == 0)
                _onDisposed(_name);
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) == 1)
            throw new ObjectDisposedException(nameof(MicrosandboxSandbox));
    }
}
