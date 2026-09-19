using System.Collections.Concurrent;
using System.Text;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.RunloopPlugin;

/// <summary>
/// One live Runloop devbox. Disposal syncs writable host mounts back once and
/// shuts the devbox down; a prior <see cref="SuspendAsync"/> (or
/// <see cref="StopAndPreserveAsync"/>) flips preserve-on-dispose so disposal
/// becomes a no-op and the frozen disk survives for a later resume.
/// </summary>
public sealed class RunloopSandbox : ISandbox, ISuspendableSandbox, IPreemptibleSandbox,
    IShutdownTeardownSandbox, IProviderOwnedSandbox, IPreserveOnDisposeSandbox
{
    private readonly RunloopApiClient _client;
    private readonly Func<RunloopSandboxOptions> _readOptions;
    private readonly Func<string> _readToken;
    private readonly SandboxSpec _spec;
    private readonly string _workingDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _log;
    private readonly Action<string> _untrack;
    private readonly ConcurrentDictionary<string, byte> _inFlightExecutions = new(StringComparer.Ordinal);
    private List<WritableMountSync> _stagedWritableMounts = [];

    private int _disposed;
    private bool _preserveOnDispose;
    private bool _ownedByShutdownHandler;

    internal RunloopSandbox(
        string id,
        string name,
        RunloopApiClient client,
        Func<RunloopSandboxOptions> readOptions,
        Func<string> readToken,
        SandboxSpec spec,
        TimeProvider timeProvider,
        ILogger log,
        Action<string> untrack)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = id;
        DevboxName = name;
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _readOptions = readOptions ?? throw new ArgumentNullException(nameof(readOptions));
        _readToken = readToken ?? throw new ArgumentNullException(nameof(readToken));
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        _workingDirectory = string.IsNullOrWhiteSpace(spec.WorkingDirectory) ? "/work" : spec.WorkingDirectory;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _untrack = untrack ?? throw new ArgumentNullException(nameof(untrack));
    }

    public string Id { get; }

    public string DevboxName { get; }

    string IProviderOwnedSandbox.ProviderId => RunloopSandboxOptions.ProviderKind;

    bool IShutdownTeardownSandbox.IsOwnedByShutdownHandler => _ownedByShutdownHandler;

    void IShutdownTeardownSandbox.MarkOwnedByShutdownHandler() => _ownedByShutdownHandler = true;

    void IPreserveOnDisposeSandbox.DisablePreserveOnDispose() => _preserveOnDispose = false;

    public bool IsSuspended { get; private set; }

    public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exec);
        ThrowIfDisposed();
        var opts = _readOptions();
        var token = _readToken();
        var baseUrl = opts.ApiBaseUrl;

        var command = RunloopShellCommand.Build(
            _spec.Environment,
            exec,
            exec.WorkingDirectory ?? _workingDirectory,
            opts.MaxEnvironmentBytes,
            opts.MaxCommandBytes,
            opts.MaxStdinBytes);

        var maxStdout = exec.MaxStdoutBytes ?? opts.MaxExecOutputBytes;
        var maxStderr = exec.MaxStderrBytes ?? opts.MaxExecOutputBytes;
        var deadline = _timeProvider.GetUtcNow() + (_spec.Limits.WallClock ?? TimeSpan.FromHours(6));

        AsyncExecutionView started;
        try
        {
            started = await _client.StartExecutionAsync(baseUrl, token, Id, command, opts.ApiTimeout, ct).ConfigureAwait(false);
        }
        catch (RunloopApiException ex)
        {
            throw ToUnavailable(ex);
        }

        if (string.IsNullOrWhiteSpace(started.ExecutionId))
        {
            throw new SandboxExecutionUnavailableException(-1);
        }

        _inFlightExecutions.TryAdd(started.ExecutionId, 0);
        try
        {
            return await PollToCompletionAsync(
                opts, token, started.ExecutionId, exec, maxStdout, maxStderr, deadline, ct).ConfigureAwait(false);
        }
        finally
        {
            _inFlightExecutions.TryRemove(started.ExecutionId, out _);
        }
    }

    internal void SetWritableMounts(IReadOnlyList<WritableMountSync> writableMounts)
    {
        ArgumentNullException.ThrowIfNull(writableMounts);
        _stagedWritableMounts = writableMounts.ToList();
    }

    private IReadOnlyList<WritableMountSync> WritableMounts => _stagedWritableMounts;

    public override string ToString() => $"runloop:{Id}";

    public Task KillActiveExecsAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return KillExecutionsAsync(_inFlightExecutions.Keys.ToArray(), _readOptions(), CancellationToken.None);
    }

    /// <summary>Writes UTF-8 text to a guest-absolute path.</summary>
    public async Task WriteFileAsync(string guestPath, string contents, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guestPath);
        ArgumentNullException.ThrowIfNull(contents);
        ThrowIfDisposed();
        RunloopGuestPath.ValidateAbsolute(guestPath);
        var opts = _readOptions();
        try
        {
            await _client.WriteFileAsync(opts.ApiBaseUrl, _readToken(), Id, guestPath, contents, opts.ApiTimeout, ct).ConfigureAwait(false);
        }
        catch (RunloopApiException ex)
        {
            throw ToUnavailable(ex);
        }
    }

    /// <summary>Reads UTF-8 text from a guest-absolute path.</summary>
    public async Task<string> ReadFileAsync(string guestPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guestPath);
        ThrowIfDisposed();
        RunloopGuestPath.ValidateAbsolute(guestPath);
        var opts = _readOptions();
        try
        {
            return await _client.ReadFileAsync(opts.ApiBaseUrl, _readToken(), Id, guestPath, opts.ApiTimeout, ct).ConfigureAwait(false);
        }
        catch (RunloopApiException ex)
        {
            throw ToUnavailable(ex);
        }
    }

    /// <summary>Creates a disk snapshot of this devbox and returns its snapshot id.</summary>
    public async Task<string> CreateSnapshotAsync(string? name, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var opts = _readOptions();
        try
        {
            var snapshot = await _client.SnapshotDiskAsync(
                opts.ApiBaseUrl, _readToken(), Id, name, ToApiTimeout(opts.ApiTimeout), ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(snapshot.Id))
            {
                throw new SandboxExecutionUnavailableException(-1);
            }

            return snapshot.Id;
        }
        catch (RunloopApiException ex)
        {
            throw ToUnavailable(ex);
        }
    }

    public async Task SuspendAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var opts = _readOptions();
        try
        {
            await _client.SuspendAsync(opts.ApiBaseUrl, _readToken(), Id, ToApiTimeout(opts.ApiTimeout), ct).ConfigureAwait(false);
        }
        catch (RunloopApiException ex)
        {
            throw ToUnavailable(ex);
        }

        IsSuspended = true;
        _preserveOnDispose = true;
    }

    public Task StopAndPreserveAsync(CancellationToken ct = default) => SuspendAsync(ct);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _untrack(Id);
        if (_preserveOnDispose)
        {
            return;
        }

        var opts = _readOptions();
        var syncError = await SyncWritableMountsBackAsync(opts, CancellationToken.None).ConfigureAwait(false);
        if (syncError is not null)
        {
            _log.LogWarning(
                "Runloop devbox {DevboxId}: teardown sync-back incomplete ({Reason}); shutting down anyway.",
                Id, syncError);
        }

        try
        {
            await _client.ShutdownAsync(opts.ApiBaseUrl, _readToken(), Id, force: false, opts.ApiTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (RunloopApiException ex) when (string.Equals(ex.ErrorClass, "conflict", StringComparison.Ordinal))
        {
            try
            {
                await _client.ShutdownAsync(opts.ApiBaseUrl, _readToken(), Id, force: true, opts.ApiTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception retryEx)
            {
                _log.LogWarning(retryEx, "Runloop devbox {DevboxId}: forced shutdown failed.", Id);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Runloop devbox {DevboxId}: shutdown failed.", Id);
        }
    }

    private async Task<SandboxExecResult> PollToCompletionAsync(
        RunloopSandboxOptions opts,
        string token,
        string executionId,
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

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
            if (_timeProvider.GetUtcNow() >= deadline)
            {
                await KillExecutionsAsync([executionId], opts, CancellationToken.None).ConfigureAwait(false);
                throw new RunloopApiException(null, "timeout", $"exec on devbox {Id} exceeded the wall-clock budget");
            }

            AsyncExecutionView view;
            try
            {
                view = await _client.WaitForExecutionAsync(
                    opts.ApiBaseUrl, token, Id, executionId, 25, RunloopApiClient.WaitCallTimeout(opts.ApiTimeout), ct).ConfigureAwait(false);
            }
            catch (RunloopApiException ex) when (string.Equals(ex.ErrorClass, "timeout", StringComparison.Ordinal))
            {
                // wait_for_status 408: command still running. Fall through to a
                // direct poll so output deltas keep streaming during long runs.
                var polled = await PollExecutionOnceAsync(opts, token, executionId, ct).ConfigureAwait(false);
                if (polled is null)
                {
                    continue;
                }

                view = polled;
            }
            catch (RunloopApiException ex)
            {
                throw ToUnavailable(ex);
            }

            EmitDelta(view.Stdout, stdout, ref accumulatingStdout, maxStdout, ref stdoutLimit, exec.StdoutChunkCallback);
            EmitDelta(view.Stderr, stderr, ref accumulatingStderr, maxStderr, ref stderrLimit, exec.StderrChunkCallback);

            if ((stdoutLimit || stderrLimit) && exec.KillOnOutputLimit)
            {
                await KillExecutionsAsync([executionId], opts, CancellationToken.None).ConfigureAwait(false);
                return FinalResult(view, stdout, stderr, stdoutLimit: true, stderrLimit: true);
            }

            if (string.Equals(view.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                if (view.StdoutTruncated)
                {
                    stdoutLimit = true;
                }

                if (view.StderrTruncated)
                {
                    stderrLimit = true;
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
            await KillExecutionsAsync([executionId], opts, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<AsyncExecutionView?> PollExecutionOnceAsync(
        RunloopSandboxOptions opts, string token, string executionId, CancellationToken ct)
    {
        try
        {
            return await _client.GetExecutionAsync(opts.ApiBaseUrl, token, Id, executionId, opts.ApiTimeout, ct).ConfigureAwait(false);
        }
        catch (RunloopApiException ex)
        {
            throw ToUnavailable(ex);
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

    private static SandboxExecResult FinalResult(
        AsyncExecutionView view, StringBuilder stdout, StringBuilder stderr, bool stdoutLimit, bool stderrLimit)
    {
        var exit = view.ExitStatus ?? 1;
        return new SandboxExecResult(exit, stdout.ToString(), stderr.ToString(), stdoutLimit, stderrLimit);
    }

    private async Task KillExecutionsAsync(string[] executionIds, RunloopSandboxOptions opts, CancellationToken ct)
    {
        foreach (var executionId in executionIds)
        {
            try
            {
                await _client.KillExecutionAsync(opts.ApiBaseUrl, _readToken(), Id, executionId, opts.ApiTimeout, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Runloop devbox {DevboxId}: kill of execution {ExecutionId} failed.", Id, executionId);
            }
        }
    }

    private async Task<string?> SyncWritableMountsBackAsync(RunloopSandboxOptions opts, CancellationToken ct)
    {
        if (WritableMounts.Count == 0)
        {
            return null;
        }

        var token = _readToken();
        long totalBytes = 0;
        foreach (var mount in WritableMounts)
        {
            // List guest files with a bounded exec; output is untrusted and validated per entry.
            SandboxExecResult list;
            try
            {
                list = await ExecInternalAsync(
                    opts, token, ["find", mount.GuestPath, "-type", "f", "-print"],
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
                    relative = RunloopGuestPath.GetRelativePath(mount.GuestPath, guestFile);
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

                byte[] bytes;
                try
                {
                    bytes = await _client.DownloadFileAsync(
                        opts.ApiBaseUrl, token, Id, guestFile, opts.MaxReadBackBytes, opts.ApiTimeout, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return $"read-back {guestFile}: {ex.GetType().Name}";
                }

                totalBytes += bytes.Length;
                if (totalBytes > opts.MaxStageTotalBytes)
                {
                    return $"read-back exceeds {opts.MaxStageTotalBytes} bytes";
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(hostFile)!);
                    await File.WriteAllBytesAsync(hostFile, bytes, ct).ConfigureAwait(false);
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
        RunloopSandboxOptions opts,
        string token,
        string[] argv,
        string workingDirectory,
        int maxStdout,
        int maxStderr,
        CancellationToken ct)
    {
        var command = RunloopShellCommand.Build(
            new Dictionary<string, string>(),
            new SandboxExec { Argv = argv, WorkingDirectory = workingDirectory },
            workingDirectory,
            opts.MaxEnvironmentBytes,
            opts.MaxCommandBytes,
            opts.MaxStdinBytes);
        var started = await _client.StartExecutionAsync(opts.ApiBaseUrl, token, Id, command, opts.ApiTimeout, ct).ConfigureAwait(false);
        var deadline = _timeProvider.GetUtcNow() + TimeSpan.FromMinutes(5);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_timeProvider.GetUtcNow() >= deadline)
            {
                throw new TimeoutException($"Internal exec on devbox {Id} exceeded its budget.");
            }

            var view = await _client.WaitForExecutionAsync(
                opts.ApiBaseUrl, token, Id, started.ExecutionId, 25, RunloopApiClient.WaitCallTimeout(opts.ApiTimeout), ct).ConfigureAwait(false);
            if (string.Equals(view.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                return new SandboxExecResult(view.ExitStatus ?? 1, view.Stdout ?? string.Empty, view.Stderr ?? string.Empty);
            }
        }
    }

    private static TimeSpan ToApiTimeout(TimeSpan configured) =>
        configured < TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : configured;

    private SandboxExecutionUnavailableException ToUnavailable(RunloopApiException ex) =>
        new(ToExitCode(ex));

    private static int ToExitCode(RunloopApiException ex) =>
        ex.StatusCode.HasValue ? -(int)ex.StatusCode.Value : -1;

    private void ThrowIfDisposed()
    {
        if (_disposed != 0)
        {
            throw new ObjectDisposedException(nameof(RunloopSandbox), $"Devbox {Id} is disposed.");
        }
    }
}

/// <summary>One writable host mount synced back to the host once at teardown.</summary>
internal sealed record WritableMountSync(string GuestPath, string HostPath);
