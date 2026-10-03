using System.Collections.Concurrent;
using System.Text;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.BlaxelPlugin;

/// <summary>
/// One live Blaxel sandbox. Disposal syncs writable host mounts back once and
/// deletes the sandbox; a prior <see cref="SuspendAsync"/> (or
/// <see cref="StopAndPreserveAsync"/>) flips preserve-on-dispose so disposal
/// becomes a no-op and the standby snapshot — memory, processes, and
/// filesystem — survives for a later resume.
///
/// <para>Exec runs through the sandbox data-plane process API: <c>POST
/// /process</c> starts a uniquely named process with the merged environment
/// delivered natively (<c>ProcessRequest.env</c>, never interpolated into the
/// command string), then the provider polls <c>GET /process/{id}</c> and
/// <c>GET /process/{id}/logs</c> for streamed deltas until the process
/// reaches a terminal status. Cancellation, wall-clock expiry, and output
/// limits kill the process via <c>DELETE /process/{id}/kill</c>.</para>
/// </summary>
public sealed class BlaxelSandbox : ISandbox, ISuspendableSandbox, IPreemptibleSandbox,
    IShutdownTeardownSandbox, IProviderOwnedSandbox, IPreserveOnDisposeSandbox
{
    private static readonly IReadOnlySet<string> TerminalStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "completed", "failed", "killed", "stopped",
    };

    private readonly BlaxelControlPlaneClient _control;
    private readonly BlaxelSandboxApiClient _dataPlane;
    private readonly Func<BlaxelSandboxOptions> _readOptions;
    private readonly Func<BlaxelCredentials> _readCredentials;
    private readonly SandboxSpec _spec;
    private readonly string _workingDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _log;
    private readonly Action<string> _untrack;
    private readonly ConcurrentDictionary<string, byte> _inFlightProcesses = new(StringComparer.Ordinal);
    private List<WritableMountSync> _stagedWritableMounts = [];

    private int _disposed;
    private bool _preserveOnDispose;
    private bool _ownedByShutdownHandler;

    internal BlaxelSandbox(
        string name,
        string sandboxUrl,
        BlaxelControlPlaneClient control,
        BlaxelSandboxApiClient dataPlane,
        Func<BlaxelSandboxOptions> readOptions,
        Func<BlaxelCredentials> readCredentials,
        SandboxSpec spec,
        TimeProvider timeProvider,
        ILogger log,
        Action<string> untrack)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxUrl);
        Name = name;
        SandboxUrl = sandboxUrl;
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _dataPlane = dataPlane ?? throw new ArgumentNullException(nameof(dataPlane));
        _readOptions = readOptions ?? throw new ArgumentNullException(nameof(readOptions));
        _readCredentials = readCredentials ?? throw new ArgumentNullException(nameof(readCredentials));
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        _workingDirectory = string.IsNullOrWhiteSpace(spec.WorkingDirectory) ? "/work" : spec.WorkingDirectory;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _untrack = untrack ?? throw new ArgumentNullException(nameof(untrack));
    }

    /// <summary>Control-plane sandbox name; doubles as <see cref="ISandbox.Id"/>.</summary>
    public string Name { get; }

    public string Id => Name;

    /// <summary>Sandbox data-plane endpoint. Refreshed from the control plane on resume.</summary>
    public string SandboxUrl { get; private set; }

    string IProviderOwnedSandbox.ProviderId => BlaxelSandboxOptions.ProviderKind;

    bool IShutdownTeardownSandbox.IsOwnedByShutdownHandler => _ownedByShutdownHandler;

    void IShutdownTeardownSandbox.MarkOwnedByShutdownHandler() => _ownedByShutdownHandler = true;

    void IPreserveOnDisposeSandbox.DisablePreserveOnDispose() => _preserveOnDispose = false;

    public bool IsSuspended { get; private set; }

    public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exec);
        ThrowIfDisposed();
        ValidateStreamingShape(exec);
        var opts = _readOptions();
        var credentials = _readCredentials();

        var command = BlaxelShellCommand.Build(
            exec,
            exec.WorkingDirectory ?? _workingDirectory,
            opts.MaxCommandBytes,
            opts.MaxStdinBytes);
        var environment = BlaxelShellCommand.MergeEnvironment(
            _spec.Environment, exec, opts.MaxEnvironmentBytes);

        var maxStdout = exec.MaxStdoutBytes ?? opts.MaxExecOutputBytes;
        var maxStderr = exec.MaxStderrBytes ?? opts.MaxExecOutputBytes;
        var deadline = _timeProvider.GetUtcNow() + (_spec.Limits.WallClock ?? TimeSpan.FromHours(6));
        var processName = $"codeybox-{Guid.NewGuid():N}";

        BlaxelProcessView started;
        try
        {
            started = await _dataPlane.StartProcessAsync(
                SandboxUrl,
                credentials,
                new BlaxelProcessRequest(command, exec.WorkingDirectory ?? _workingDirectory, false, processName, environment),
                opts.ApiTimeout,
                ct).ConfigureAwait(false);
        }
        catch (BlaxelApiException ex)
        {
            throw ToUnavailable(ex);
        }

        var processId = !string.IsNullOrWhiteSpace(started.Pid) ? started.Pid : processName;
        _inFlightProcesses.TryAdd(processId, 0);
        try
        {
            return await PollToCompletionAsync(
                opts, credentials, processId, exec, maxStdout, maxStderr, deadline, ct).ConfigureAwait(false);
        }
        finally
        {
            _inFlightProcesses.TryRemove(processId, out _);
        }
    }

    internal void SetWritableMounts(IReadOnlyList<WritableMountSync> writableMounts)
    {
        ArgumentNullException.ThrowIfNull(writableMounts);
        _stagedWritableMounts = writableMounts.ToList();
    }

    internal void RefreshSandboxUrl(string sandboxUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxUrl);
        SandboxUrl = sandboxUrl;
    }

    private IReadOnlyList<WritableMountSync> WritableMounts => _stagedWritableMounts;

    public override string ToString() => $"blaxel:{Name}";

    public Task KillActiveExecsAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return KillProcessesAsync(_inFlightProcesses.Keys.ToArray(), CancellationToken.None);
    }

    /// <summary>Writes UTF-8 text to a guest-absolute path via bounded base64-carrying execs.</summary>
    public async Task WriteFileAsync(string guestPath, string contents, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guestPath);
        ArgumentNullException.ThrowIfNull(contents);
        ThrowIfDisposed();
        BlaxelGuestPath.ValidateAbsolute(guestPath);
        var opts = _readOptions();

        var bytes = Encoding.UTF8.GetBytes(contents);
        if (bytes.Length > opts.MaxStageFileBytes)
        {
            throw new InvalidOperationException(
                $"Blaxel staged file '{guestPath}' is {bytes.Length} bytes (limit {opts.MaxStageFileBytes}); refusing to stage.");
        }

        const int ChunkBytes = 192 * 1024;
        var first = true;
        for (var offset = 0; offset < bytes.Length || first; offset += ChunkBytes)
        {
            ct.ThrowIfCancellationRequested();
            var length = Math.Min(ChunkBytes, bytes.Length - offset);
            var chunk = Convert.ToBase64String(bytes, offset, length);
            var redirect = first ? ">" : ">>";
            var script = $"mkdir -p -- $(dirname -- {BlaxelShellCommand.Quote(guestPath)}) && printf %s '{chunk}'|base64 -d {redirect} {BlaxelShellCommand.Quote(guestPath)}";
            first = false;

            var result = await ExecInternalAsync(
                opts, ["sh", "-c", script], "/", maxStdout: 64 * 1024, maxStderr: 64 * 1024, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                throw new SandboxExecutionUnavailableException(result.ExitCode);
            }

            if (bytes.Length == 0)
            {
                break;
            }
        }
    }

    /// <summary>Reads UTF-8 text from a guest-absolute path via a bounded base64-carrying exec.</summary>
    public Task<string> ReadFileAsync(string guestPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guestPath);
        ThrowIfDisposed();
        return ReadFileCoreAsync(guestPath, ct);
    }

    /// <summary>
    /// Disposal-safe file read: teardown sync-back runs after <see cref="DisposeAsync"/>
    /// flips the disposed flag, so it must not go through the public disposed guard.
    /// </summary>
    private async Task<string> ReadFileCoreAsync(string guestPath, CancellationToken ct)
    {
        BlaxelGuestPath.ValidateAbsolute(guestPath);
        var opts = _readOptions();

        var result = await ExecInternalAsync(
            opts,
            ["sh", "-c", $"base64 -- {BlaxelShellCommand.Quote(guestPath)}"],
            "/",
            maxStdout: (int)Math.Min(int.MaxValue, opts.MaxReadBackBytes * 2),
            maxStderr: 64 * 1024,
            ct).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new SandboxExecutionUnavailableException(result.ExitCode);
        }

        var encoded = result.Stdout.Trim();
        if (encoded.Length == 0)
        {
            return string.Empty;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            throw new SandboxExecutionUnavailableException(-1);
        }

        if (bytes.Length > opts.MaxReadBackBytes)
        {
            throw new InvalidOperationException(
                $"Blaxel read-back file '{guestPath}' is {bytes.Length} bytes (limit {opts.MaxReadBackBytes}); refusing to buffer.");
        }

        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Blaxel suspends automatically on idle (scale-to-zero) with memory, processes,
    /// and filesystem preserved — there is no explicit suspend call, so this waits
    /// (bounded) for the control plane to report <c>STANDBY</c> once guest activity
    /// drains. Success flips preserve-on-dispose so teardown keeps the snapshot.
    /// </summary>
    public async Task SuspendAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var opts = _readOptions();
        var credentials = _readCredentials();
        var deadline = _timeProvider.GetUtcNow() + opts.SuspendWaitTimeout;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            BlaxelSandboxView view;
            try
            {
                view = await _control.GetSandboxAsync(opts.ApiBaseUrl, credentials, Name, opts.ApiTimeout, ct).ConfigureAwait(false);
            }
            catch (BlaxelApiException ex)
            {
                throw ToUnavailable(ex);
            }

            if (string.Equals(view.State, "STANDBY", StringComparison.OrdinalIgnoreCase))
            {
                IsSuspended = true;
                _preserveOnDispose = true;
                return;
            }

            if (string.Equals(view.Status, "FAILED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(view.Status, "TERMINATED", StringComparison.OrdinalIgnoreCase))
            {
                throw ToUnavailable(new BlaxelApiException(null, "terminal-state", $"sandbox {Name} entered '{view.Status}' while suspending"));
            }

            if (_timeProvider.GetUtcNow() >= deadline)
            {
                throw ToUnavailable(new BlaxelApiException(null, "timeout", $"sandbox {Name} did not reach standby in time"));
            }

            try
            {
                await Task.Delay(opts.ExecPollInterval, _timeProvider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
        }
    }

    public Task StopAndPreserveAsync(CancellationToken ct = default) => SuspendAsync(ct);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _untrack(Name);
        if (_preserveOnDispose)
        {
            return;
        }

        var opts = _readOptions();
        var syncError = await SyncWritableMountsBackAsync(opts, CancellationToken.None).ConfigureAwait(false);
        if (syncError is not null)
        {
            _log.LogWarning(
                "Blaxel sandbox {SandboxName}: teardown sync-back incomplete ({Reason}); deleting anyway.",
                Name, syncError);
        }

        try
        {
            await _control.DeleteSandboxAsync(opts.ApiBaseUrl, _readCredentials(), Name, opts.ApiTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (BlaxelApiException ex) when (string.Equals(ex.ErrorClass, "not-found", StringComparison.Ordinal))
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Blaxel sandbox {SandboxName}: delete failed.", Name);
        }
    }

    private static void ValidateStreamingShape(SandboxExec exec)
    {
        if (exec.StreamOutputWithoutKill && exec.KillOnOutputLimit)
        {
            throw new ArgumentException(
                "StreamOutputWithoutKill requires KillOnOutputLimit=false; combining streaming with a kill threshold is rejected.",
                nameof(exec));
        }
    }

    private async Task<SandboxExecResult> PollToCompletionAsync(
        BlaxelSandboxOptions opts,
        BlaxelCredentials credentials,
        string processId,
        SandboxExec exec,
        int maxStdout,
        int maxStderr,
        DateTimeOffset deadline,
        CancellationToken ct)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutLimit = false;
        var stderrLimit = false;
        var accumulatingStdout = true;
        var accumulatingStderr = true;
        var retainedStdoutBytes = exec.MaxRetainedStdoutBytes ?? SandboxExec.DefaultStreamedOutputTailBytes;
        var retainedStderrBytes = exec.MaxRetainedStderrBytes ?? SandboxExec.DefaultStreamedOutputTailBytes;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (_timeProvider.GetUtcNow() >= deadline)
                {
                    await KillProcessesAsync([processId], CancellationToken.None).ConfigureAwait(false);
                    throw new BlaxelApiException(null, "timeout", $"exec on sandbox {Name} exceeded the wall-clock budget");
                }

                BlaxelProcessView view;
                try
                {
                    view = await _dataPlane.GetProcessAsync(SandboxUrl, credentials, processId, opts.ApiTimeout, ct).ConfigureAwait(false);
                }
                catch (BlaxelApiException ex)
                {
                    throw ToUnavailable(ex);
                }

                BlaxelProcessLogs? logs = null;
                if (!exec.StreamOutputWithoutKill || !view.IsTerminal)
                {
                    try
                    {
                        logs = await _dataPlane.GetProcessLogsAsync(SandboxUrl, credentials, processId, opts.ApiTimeout, ct).ConfigureAwait(false);
                    }
                    catch (BlaxelApiException ex) when (string.Equals(ex.ErrorClass, "not-found", StringComparison.Ordinal) && view.IsTerminal)
                    {
                        logs = null;
                    }
                    catch (BlaxelApiException ex)
                    {
                        throw ToUnavailable(ex);
                    }
                }

                var latestStdout = logs?.Stdout ?? view.Stdout;
                var latestStderr = logs?.Stderr ?? view.Stderr;
                if (exec.StreamOutputWithoutKill)
                {
                    EmitStreamingDelta(latestStdout, stdout, retainedStdoutBytes, exec.StdoutChunkCallback);
                    EmitStreamingDelta(latestStderr, stderr, retainedStderrBytes, exec.StderrChunkCallback);
                }
                else
                {
                    EmitDelta(latestStdout, stdout, ref accumulatingStdout, maxStdout, ref stdoutLimit, exec.StdoutChunkCallback);
                    EmitDelta(latestStderr, stderr, ref accumulatingStderr, maxStderr, ref stderrLimit, exec.StderrChunkCallback);

                    if ((stdoutLimit || stderrLimit) && exec.KillOnOutputLimit)
                    {
                        await KillProcessesAsync([processId], CancellationToken.None).ConfigureAwait(false);
                        return FinalResult(view, stdout, stderr, stdoutLimit: true, stderrLimit: true);
                    }
                }

                if (view.IsTerminal)
                {
                    if (exec.StreamOutputWithoutKill && logs is null)
                    {
                        try
                        {
                            logs = await _dataPlane.GetProcessLogsAsync(SandboxUrl, credentials, processId, opts.ApiTimeout, ct).ConfigureAwait(false);
                        }
                        catch (BlaxelApiException ex) when (string.Equals(ex.ErrorClass, "not-found", StringComparison.Ordinal))
                        {
                            logs = null;
                        }
                        catch (BlaxelApiException ex)
                        {
                            throw ToUnavailable(ex);
                        }

                        EmitStreamingDelta(logs?.Stdout ?? view.Stdout, stdout, retainedStdoutBytes, exec.StdoutChunkCallback);
                        EmitStreamingDelta(logs?.Stderr ?? view.Stderr, stderr, retainedStderrBytes, exec.StderrChunkCallback);
                    }

                    return FinalResult(view, stdout, stderr, stdoutLimit, stderrLimit);
                }

                try
                {
                    await Task.Delay(opts.ExecPollInterval, _timeProvider, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // TimeProvider delay quirks; re-evaluate the loop instead of faulting.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The caller abandoned the exec: kill the server-side process so a
            // cancelled pipeline phase does not leak guest compute, then let
            // the cancellation propagate unchanged.
            await KillProcessesAsync([processId], CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static void EmitDelta(
        string? latest,
        StringBuilder accumulated,
        ref bool accumulating,
        int maxBytes,
        ref bool limitFlag,
        Action<string>? callback)
    {
        if (!accumulating || latest is null)
        {
            return;
        }

        if (latest.Length < accumulated.Length)
        {
            accumulated.Clear();
        }

        if (latest.Length <= accumulated.Length)
        {
            return;
        }

        var delta = latest.Substring(accumulated.Length);
        var room = maxBytes - accumulated.Length;
        if (delta.Length > room)
        {
            delta = delta.Substring(0, Math.Max(0, room));
            limitFlag = true;
            accumulating = false;
        }

        accumulated.Append(delta);
        if (delta.Length > 0)
        {
            callback?.Invoke(delta);
        }
    }

    private static void EmitStreamingDelta(
        string? latest,
        StringBuilder retained,
        int retainedBytes,
        Action<string>? callback)
    {
        if (latest is null)
        {
            return;
        }

        if (latest.Length < retained.Length && retained.Length <= retainedBytes)
        {
            return;
        }

        string delta;
        if (latest.Length <= retained.Length)
        {
            retained.Clear();
            delta = latest.Length > retainedBytes ? latest.Substring(latest.Length - retainedBytes) : latest;
        }
        else
        {
            delta = latest.Substring(retained.Length);
        }

        if (delta.Length > 0)
        {
            callback?.Invoke(delta);
        }

        retained.Append(delta);
        if (retained.Length > retainedBytes)
        {
            retained.Remove(0, retained.Length - retainedBytes);
        }
    }

    private static SandboxExecResult FinalResult(
        BlaxelProcessView view, StringBuilder stdout, StringBuilder stderr, bool stdoutLimit, bool stderrLimit)
    {
        var exit = view.ExitCode ?? (string.Equals(view.Status, "completed", StringComparison.OrdinalIgnoreCase) ? 0 : 1);
        return new SandboxExecResult(exit, stdout.ToString(), stderr.ToString(), stdoutLimit, stderrLimit);
    }

    private async Task KillProcessesAsync(string[] processIds, CancellationToken ct)
    {
        var credentials = _readCredentials();
        foreach (var processId in processIds)
        {
            try
            {
                await _dataPlane.KillProcessAsync(SandboxUrl, credentials, processId, _readOptions().ApiTimeout, ct).ConfigureAwait(false);
            }
            catch (BlaxelApiException ex) when (string.Equals(ex.ErrorClass, "not-found", StringComparison.Ordinal))
            {
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Blaxel sandbox {SandboxName}: kill of process {ProcessId} failed.", Name, processId);
            }
        }
    }

    private async Task<string?> SyncWritableMountsBackAsync(BlaxelSandboxOptions opts, CancellationToken ct)
    {
        if (WritableMounts.Count == 0)
        {
            return null;
        }

        long totalBytes = 0;
        foreach (var mount in WritableMounts)
        {
            // List guest files with a bounded exec; output is untrusted and validated per entry.
            SandboxExecResult list;
            try
            {
                list = await ExecInternalAsync(
                    opts, ["find", mount.GuestPath, "-type", "f", "-print"],
                    mount.GuestPath, maxStdout: 1024 * 1024, maxStderr: 64 * 1024, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return $"listing {mount.GuestPath}: {ex.GetType().Name}";
            }

            if (list.ExitCode != 0)
            {
                return $"listing {mount.GuestPath} exited {list.ExitCode}";
            }

            var count = 0;
            foreach (var line in list.Stdout.Split('\n'))
            {
                var guestFile = line.TrimEnd('\r').Trim();
                if (string.IsNullOrEmpty(guestFile))
                {
                    continue;
                }

                if (++count > opts.MaxStageFileCount)
                {
                    return $"more than {opts.MaxStageFileCount} files under {mount.GuestPath}";
                }

                string relative;
                try
                {
                    relative = BlaxelGuestPath.GetRelativePath(mount.GuestPath, guestFile);
                }
                catch (ArgumentException)
                {
                    return $"guest path escapes its mount: {guestFile}";
                }

                var hostFile = Path.GetFullPath(Path.Combine(mount.HostPath, relative));
                if (!hostFile.StartsWith(mount.HostPath + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && !string.Equals(hostFile, mount.HostPath, StringComparison.Ordinal))
                {
                    return $"host path escapes its mount root for {guestFile}";
                }

                string content;
                try
                {
                    content = await ReadFileCoreAsync(guestFile, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return $"read-back {guestFile}: {ex.GetType().Name}";
                }

                totalBytes += Encoding.UTF8.GetByteCount(content);
                if (totalBytes > opts.MaxStageTotalBytes)
                {
                    return $"read-back exceeds {opts.MaxStageTotalBytes} bytes";
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(hostFile)!);
                    await File.WriteAllTextAsync(hostFile, content, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return $"write-back {hostFile}: {ex.GetType().Name}";
                }
            }
        }

        return null;
    }

    private async Task<SandboxExecResult> ExecInternalAsync(
        BlaxelSandboxOptions opts,
        string[] argv,
        string workingDirectory,
        int maxStdout,
        int maxStderr,
        CancellationToken ct)
    {
        var command = BlaxelShellCommand.Build(
            new SandboxExec { Argv = argv, WorkingDirectory = workingDirectory },
            workingDirectory,
            opts.MaxCommandBytes,
            opts.MaxStdinBytes);
        var started = await _dataPlane.StartProcessAsync(
            SandboxUrl,
            _readCredentials(),
            new BlaxelProcessRequest(command, workingDirectory, false, $"codeybox-internal-{Guid.NewGuid():N}", new Dictionary<string, string>()),
            opts.ApiTimeout,
            ct).ConfigureAwait(false);
        var processId = !string.IsNullOrWhiteSpace(started.Pid) ? started.Pid : started.Name ?? string.Empty;
        var deadline = _timeProvider.GetUtcNow() + TimeSpan.FromMinutes(5);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_timeProvider.GetUtcNow() >= deadline)
            {
                throw new TimeoutException($"Internal exec on sandbox {Name} exceeded its budget.");
            }

            var view = await _dataPlane.GetProcessAsync(SandboxUrl, _readCredentials(), processId, opts.ApiTimeout, ct).ConfigureAwait(false);
            if (view.IsTerminal)
            {
                BlaxelProcessLogs? logs = null;
                try
                {
                    logs = await _dataPlane.GetProcessLogsAsync(SandboxUrl, _readCredentials(), processId, opts.ApiTimeout, ct).ConfigureAwait(false);
                }
                catch (BlaxelApiException ex) when (string.Equals(ex.ErrorClass, "not-found", StringComparison.Ordinal))
                {
                }

                stdout.Append(logs?.Stdout ?? view.Stdout ?? string.Empty);
                stderr.Append(logs?.Stderr ?? view.Stderr ?? string.Empty);
                var exit = view.ExitCode ?? (string.Equals(view.Status, "completed", StringComparison.OrdinalIgnoreCase) ? 0 : 1);
                if (stdout.Length > maxStdout || stderr.Length > maxStderr)
                {
                    throw new InvalidOperationException("Internal exec output exceeded its bound.");
                }

                return new SandboxExecResult(exit, stdout.ToString(), stderr.ToString());
            }

            try
            {
                await Task.Delay(opts.ExecPollInterval, _timeProvider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
        }
    }

    private SandboxExecutionUnavailableException ToUnavailable(BlaxelApiException ex) =>
        new(ToExitCode(ex));

    private static int ToExitCode(BlaxelApiException ex) =>
        ex.StatusCode.HasValue ? -(int)ex.StatusCode.Value : -1;

    private void ThrowIfDisposed()
    {
        if (_disposed != 0)
        {
            throw new ObjectDisposedException(nameof(BlaxelSandbox), $"Sandbox {Name} is disposed.");
        }
    }
}

/// <summary>One writable host mount synced back to the host once at teardown.</summary>
internal sealed record WritableMountSync(string GuestPath, string HostPath);
