using System.Collections.Concurrent;
using System.Text;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.ModalPlugin;

/// <summary>
/// One live Modal sandbox. Disposal syncs writable host mounts back once and
/// terminates the sandbox; a prior <see cref="StopAndPreserveAsync"/> flips
/// preserve-on-dispose so disposal becomes a no-op and the filesystem snapshot
/// taken at preserve time survives service-side for a later restore.
/// </summary>
public sealed class ModalSandbox : ISandbox, IPreemptibleSandbox,
    IShutdownTeardownSandbox, IProviderOwnedSandbox, IPreserveOnDisposeSandbox,
    IRejectsFileBackedAgentCredentials
{
    private const int WallClockTimeoutExitCode = 124;
    private const int ExecutionUnavailableExitCode = 255;

    /// <summary>
    /// Guest directory staging per-exec secret environment files. Random
    /// unguessable file names keep one exec's secrets out of other guests'
    /// reach; the sourcing command deletes its file before running argv.
    /// </summary>
    private const string SecretEnvStagingDirectory = "/tmp/.codeybox-exec-env";

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly ModalApiClient _client;
    private readonly Func<ModalSandboxOptions> _readOptions;
    private readonly Func<ModalCredentials> _readCredentials;
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

    internal ModalSandbox(
        string id,
        string name,
        ModalApiClient client,
        Func<ModalSandboxOptions> readOptions,
        Func<ModalCredentials> readCredentials,
        SandboxSpec spec,
        TimeProvider timeProvider,
        ILogger log,
        Action<string> untrack)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = id;
        SandboxName = name;
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _readOptions = readOptions ?? throw new ArgumentNullException(nameof(readOptions));
        _readCredentials = readCredentials ?? throw new ArgumentNullException(nameof(readCredentials));
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        _workingDirectory = string.IsNullOrWhiteSpace(spec.WorkingDirectory) ? "/work" : spec.WorkingDirectory;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _untrack = untrack ?? throw new ArgumentNullException(nameof(untrack));
    }

    public string Id { get; }

    public string SandboxName { get; }

    /// <summary>Snapshot id captured by the last <see cref="StopAndPreserveAsync"/>; null when never preserved.</summary>
    public string? LastSnapshotId { get; private set; }

    string IProviderOwnedSandbox.ProviderId => ModalSandboxOptions.ProviderKind;

    bool IShutdownTeardownSandbox.IsOwnedByShutdownHandler => _ownedByShutdownHandler;

    void IShutdownTeardownSandbox.MarkOwnedByShutdownHandler() => _ownedByShutdownHandler = true;

    void IPreserveOnDisposeSandbox.DisablePreserveOnDispose() => _preserveOnDispose = false;

    string IRejectsFileBackedAgentCredentials.FileBackedAgentCredentialsUnsupportedReason =>
        "Modal sandboxes persist the guest disk (including snapshots) on third-party infrastructure " +
        "CodeyBox does not control, so file-materialised credentials would outlive the work item. " +
        "Pass credentials as environment variables instead.";

    public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exec);
        ThrowIfDisposed();
        return await ExecInternalAsync(exec, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Exec core without the disposed gate, for teardown sync-back: disposal
    /// marks the handle disposed before syncing mounts back, but the remote
    /// sandbox is still alive until terminate runs, so the listing exec must
    /// not refuse. Never called after terminate.
    /// </summary>
    private async Task<SandboxExecResult> ExecInternalAsync(SandboxExec exec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(exec);
        ValidateStreaming(exec);
        var opts = _readOptions();
        var credentials = _readCredentials();
        var baseUrl = opts.ApiBaseUrl;

        string command;
        try
        {
            if (exec.EnvironmentContainsSecrets && exec.ExtraEnvironment is { Count: > 0 })
            {
                command = await BuildSecretCommandAsync(
                    opts, baseUrl, credentials, exec, exec.WorkingDirectory ?? _workingDirectory, ct).ConfigureAwait(false);
            }
            else
            {
                command = ModalShellCommand.Build(
                    _spec.Environment,
                    exec,
                    exec.WorkingDirectory ?? _workingDirectory,
                    opts.MaxEnvironmentBytes,
                    opts.MaxCommandBytes,
                    opts.MaxStdinBytes);
            }
        }
        catch (ModalApiException ex)
        {
            return Unavailable($"modal secret-env staging failure: {ex.Message}");
        }

        var workingDirectory = exec.WorkingDirectory ?? _workingDirectory;
        var wallClockLimit = _spec.Limits.WallClock;
        using var wallClockCts = wallClockLimit is { } limit && limit > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (wallClockCts is not null && wallClockLimit is { } wallClock)
        {
            wallClockCts.CancelAfter(wallClock);
        }

        var execCt = wallClockCts?.Token ?? ct;

        ModalExecView started;
        try
        {
            started = await _client.StartExecAsync(
                baseUrl, credentials, Id, new ModalExecStartRequest(command, workingDirectory), opts.ApiTimeout, execCt).ConfigureAwait(false);
        }
        catch (ModalApiException ex)
        {
            return Unavailable($"modal exec dispatch failure: {ex.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SandboxExecResult(
                WallClockTimeoutExitCode,
                string.Empty,
                $"modal exec exceeded the sandbox wall-clock limit ({_spec.Limits.WallClock})",
                ExecutionUnavailable: false);
        }

        if (string.IsNullOrWhiteSpace(started.ExecId))
        {
            return Unavailable("modal exec dispatch returned no exec id");
        }

        _inFlightExecutions.TryAdd(started.ExecId, 0);
        try
        {
            return await PollToCompletionAsync(
                opts, credentials, started.ExecId, exec, execCt, ct).ConfigureAwait(false);
        }
        finally
        {
            _inFlightExecutions.TryRemove(started.ExecId, out _);
        }
    }

    /// <summary>
    /// Builds the guest command for a secret-bearing exec without placing
    /// values in host-visible command argv: the merged environment is staged
    /// as a sourceable file via files:write and the command only sources it.
    /// A staging failure fails the exec as infrastructure (never a diff
    /// verdict) and never falls back to inline transport.
    /// </summary>
    private async Task<string> BuildSecretCommandAsync(
        ModalSandboxOptions opts,
        string baseUrl,
        ModalCredentials credentials,
        SandboxExec exec,
        string workingDirectory,
        CancellationToken ct)
    {
        var (merged, removals) = ModalShellCommand.MergeEnvironment(_spec.Environment, exec);
        var content = ModalShellCommand.BuildEnvFileContent(merged, removals, opts.MaxEnvironmentBytes);
        var contentBytes = Encoding.UTF8.GetBytes(content);
        var envFilePath = $"{SecretEnvStagingDirectory}/env-{Guid.NewGuid():N}";

        await _client.WriteFileAsync(baseUrl, credentials, Id, envFilePath, contentBytes, opts.ApiTimeout, ct).ConfigureAwait(false);

        return ModalShellCommand.BuildSourcingCommand(
            envFilePath, exec, workingDirectory, opts.MaxCommandBytes, opts.MaxStdinBytes);
    }

    internal void SetWritableMounts(IReadOnlyList<WritableMountSync> writableMounts)
    {
        ArgumentNullException.ThrowIfNull(writableMounts);
        _stagedWritableMounts = writableMounts.ToList();
    }

    public override string ToString() => $"modal:{Id}";

    public Task KillActiveExecsAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return KillExecutionsAsync(_inFlightExecutions.Keys.ToArray(), _readOptions(), ct);
    }

    /// <summary>Writes UTF-8 text to a guest-absolute path.</summary>
    public async Task WriteFileAsync(string guestPath, string contents, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guestPath);
        ArgumentNullException.ThrowIfNull(contents);
        ThrowIfDisposed();
        ModalGuestPath.ValidateAbsolute(guestPath);
        var opts = _readOptions();
        var contentBytes = Encoding.UTF8.GetBytes(contents);
        if (contentBytes.Length > opts.MaxStageFileBytes)
        {
            throw new ArgumentException(
                $"Guest file '{guestPath}' is {contentBytes.Length} bytes (limit {opts.MaxStageFileBytes}); refusing to write.",
                nameof(contents));
        }

        try
        {
            await _client.WriteFileAsync(opts.ApiBaseUrl, _readCredentials(), Id, guestPath, contentBytes, opts.ApiTimeout, ct).ConfigureAwait(false);
        }
        catch (ModalApiException ex)
        {
            _log.LogWarning(ex, "Modal sandbox {SandboxId}: write-file of '{GuestPath}' failed.", Id, guestPath);
            throw new SandboxExecutionUnavailableException(-1);
        }
    }

    /// <summary>Reads UTF-8 text from a guest-absolute path.</summary>
    public async Task<string> ReadFileAsync(string guestPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guestPath);
        ThrowIfDisposed();
        ModalGuestPath.ValidateAbsolute(guestPath);
        var opts = _readOptions();
        try
        {
            var bytes = await _client.ReadFileBytesAsync(
                opts.ApiBaseUrl, _readCredentials(), Id, guestPath, opts.MaxReadBackBytes, opts.ApiTimeout, ct).ConfigureAwait(false);
            try
            {
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidOperationException(
                    $"Guest file '{guestPath}' is not valid UTF-8; refusing to decode it as text.", ex);
            }
        }
        catch (ModalApiException ex)
        {
            _log.LogWarning(ex, "Modal sandbox {SandboxId}: read-file of '{GuestPath}' failed.", Id, guestPath);
            throw new SandboxExecutionUnavailableException(-1);
        }
    }

    /// <summary>
    /// Preserves this sandbox for graceful shutdown: snapshots the filesystem
    /// service-side, then terminates the sandbox so it stops burning quota.
    /// The snapshot (not running processes) is what survives — the pipeline
    /// replays from its checkpoint, so this is expected, not data loss.
    /// Subsequent disposal is a no-op.
    /// </summary>
    public async Task StopAndPreserveAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var opts = _readOptions();
        var credentials = _readCredentials();
        ModalSnapshotView snapshot;
        try
        {
            snapshot = await _client.SnapshotSandboxAsync(
                opts.ApiBaseUrl, credentials, Id, $"codeybox-preserve-{Id}", ModalApiClient.WaitCallTimeout(opts.ApiTimeout), ct).ConfigureAwait(false);
        }
        catch (ModalApiException ex)
        {
            _log.LogWarning(ex, "Modal sandbox {SandboxId}: preserve snapshot failed.", Id);
            throw new SandboxExecutionUnavailableException(-1);
        }

        if (string.IsNullOrWhiteSpace(snapshot.Id))
        {
            _log.LogWarning("Modal sandbox {SandboxId}: preserve snapshot returned no snapshot id.", Id);
            throw new SandboxExecutionUnavailableException(-1);
        }

        try
        {
            await _client.TerminateSandboxAsync(opts.ApiBaseUrl, credentials, Id, opts.ApiTimeout, ct).ConfigureAwait(false);
        }
        catch (ModalApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
        }
        catch (ModalApiException ex)
        {
            _log.LogWarning(ex, "Modal sandbox {SandboxId}: preserve terminate failed.", Id);
            throw new SandboxExecutionUnavailableException(-1);
        }

        LastSnapshotId = snapshot.Id;
        _preserveOnDispose = true;
    }

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
                "Modal sandbox {SandboxId}: teardown sync-back incomplete ({Reason}); terminating anyway.",
                Id, syncError);
        }

        try
        {
            await _client.TerminateSandboxAsync(opts.ApiBaseUrl, _readCredentials(), Id, opts.ApiTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ModalApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Modal sandbox {SandboxId}: terminate failed.", Id);
        }
    }

    public async Task SyncStateToHostAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var error = await SyncWritableMountsBackAsync(_readOptions(), ct).ConfigureAwait(false);
        if (error is not null)
        {
            throw new InvalidOperationException($"Modal sandbox {Id}: sync-back incomplete ({error}).");
        }
    }

    private async Task<SandboxExecResult> PollToCompletionAsync(
        ModalSandboxOptions opts,
        ModalCredentials credentials,
        string execId,
        SandboxExec exec,
        CancellationToken execCt,
        CancellationToken callerCt)
    {
        var streaming = exec.StreamOutputWithoutKill;
        var stdoutCap = streaming
            ? exec.MaxRetainedStdoutBytes ?? SandboxExec.DefaultStreamedOutputTailBytes
            : exec.MaxStdoutBytes ?? opts.MaxExecOutputBytes;
        var stderrCap = streaming
            ? exec.MaxRetainedStderrBytes ?? SandboxExec.DefaultStreamedOutputTailBytes
            : exec.MaxStderrBytes ?? opts.MaxExecOutputBytes;
        var stdout = new OutputBuffer(stdoutCap, streaming, exec.StdoutChunkCallback);
        var stderr = new OutputBuffer(stderrCap, streaming, exec.StderrChunkCallback);
        long stdoutAfter = 0;
        long stderrAfter = 0;

        try
        {
            while (true)
            {
                callerCt.ThrowIfCancellationRequested();
                execCt.ThrowIfCancellationRequested();

                ModalExecView view;
                try
                {
                    view = await _client.PollExecAsync(
                        opts.ApiBaseUrl, credentials, Id, execId,
                        stdoutAfter, stderrAfter,
                        ModalApiClient.WaitCallTimeout(opts.ApiTimeout), execCt).ConfigureAwait(false);
                }
                catch (ModalApiException ex)
                {
                    return Unavailable($"modal exec observation failure: {ex.Message}");
                }

                stdout.Append(view.Stdout);
                stderr.Append(view.Stderr);
                stdoutAfter += Encoding.UTF8.GetByteCount(view.Stdout);
                stderrAfter += Encoding.UTF8.GetByteCount(view.Stderr);

                if ((stdout.LimitExceeded || stderr.LimitExceeded) && exec.KillOnOutputLimit)
                {
                    await KillExecutionsAsync([execId], opts, CancellationToken.None).ConfigureAwait(false);
                    return FinalResult(view.ExitCode, stdout, stderr, stdout.LimitExceeded, stderr.LimitExceeded);
                }

                if (string.Equals(view.Status, "completed", StringComparison.OrdinalIgnoreCase))
                {
                    return FinalResult(
                        view.ExitCode,
                        stdout,
                        stderr,
                        stdoutLimit: stdout.LimitExceeded || view.StdoutTruncated,
                        stderrLimit: stderr.LimitExceeded || view.StderrTruncated);
                }

                if (string.Equals(view.Status, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    return Unavailable($"modal exec entered 'failed' while observed: exit unknown");
                }

                try
                {
                    await Task.Delay(opts.ExecPollInterval, _timeProvider, execCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
                {
                }
            }
        }
        catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
        {
            await KillExecutionsAsync([execId], opts, CancellationToken.None).ConfigureAwait(false);
            return new SandboxExecResult(
                WallClockTimeoutExitCode,
                stdout.ToString(),
                stderr.ToString(),
                stdout.LimitExceeded,
                stderr.LimitExceeded,
                ExecutionUnavailable: false);
        }
        catch (OperationCanceledException)
        {
            await KillExecutionsAsync([execId], opts, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static SandboxExecResult FinalResult(
        int? exitCode,
        OutputBuffer stdout,
        OutputBuffer stderr,
        bool stdoutLimit,
        bool stderrLimit)
    {
        if (exitCode is null)
        {
            return Unavailable("modal exec completed without an observable exit code");
        }

        return new SandboxExecResult(exitCode.Value, stdout.ToString(), stderr.ToString(), stdoutLimit, stderrLimit);
    }

    private static SandboxExecResult Unavailable(string reason) =>
        new(ExecutionUnavailableExitCode, string.Empty, reason, ExecutionUnavailable: true);

    private static void ValidateStreaming(SandboxExec exec)
    {
        if (exec.StreamOutputWithoutKill && exec.KillOnOutputLimit)
        {
            throw new ArgumentException(
                "Streaming execs (StreamOutputWithoutKill) require KillOnOutputLimit to be false.",
                nameof(exec));
        }

        if (exec.MaxStdoutBytes is <= 0 || exec.MaxStderrBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(exec), "Exec output limits must be positive when supplied.");
        }

        if (exec.MaxRetainedStdoutBytes is <= 0 || exec.MaxRetainedStderrBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(exec), "Exec retained-output limits must be positive when supplied.");
        }
    }

    private async Task KillExecutionsAsync(string[] execIds, ModalSandboxOptions opts, CancellationToken ct)
    {
        foreach (var execId in execIds)
        {
            try
            {
                await _client.KillExecAsync(opts.ApiBaseUrl, _readCredentials(), Id, execId, opts.ApiTimeout, ct).ConfigureAwait(false);
            }
            catch (ModalApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Modal sandbox {SandboxId}: kill of exec {ExecId} failed.", Id, execId);
            }
        }
    }

    private async Task<string?> SyncWritableMountsBackAsync(ModalSandboxOptions opts, CancellationToken ct)
    {
        var mounts = _stagedWritableMounts;
        if (mounts.Count == 0)
        {
            return null;
        }

        try
        {
            foreach (var mount in mounts)
            {
                ct.ThrowIfCancellationRequested();
                var error = await SyncOneMountBackAsync(opts, mount, ct).ConfigureAwait(false);
                if (error is not null)
                {
                    return error;
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private async Task<string?> SyncOneMountBackAsync(ModalSandboxOptions opts, WritableMountSync mount, CancellationToken ct)
    {
        var hostRoot = Path.GetFullPath(mount.HostPath);
        var guestRoot = mount.GuestPath;
        if (string.Equals(hostRoot.TrimEnd(Path.DirectorySeparatorChar), Path.GetPathRoot(hostRoot)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
        {
            return $"refusing sync-back onto filesystem root '{hostRoot}'";
        }

        if (mount.IsFile)
        {
            return await SyncOneFileBackAsync(opts, guestRoot, hostRoot, ct).ConfigureAwait(false);
        }

        var listing = await ExecInternalAsync(
            new SandboxExec
            {
                Argv = ["find", guestRoot, "-type", "f", "-print"],
                WorkingDirectory = "/",
                MaxStdoutBytes = opts.MaxExecOutputBytes,
            },
            ct).ConfigureAwait(false);
        if (listing.ExecutionUnavailable)
        {
            return $"guest listing of '{guestRoot}' was unobservable: {listing.Stderr}";
        }

        if (listing.ExitCode != 0)
        {
            return $"guest listing of '{guestRoot}' exited {listing.ExitCode}: {listing.Stderr}";
        }

        var relatives = new List<string>();
        foreach (var line in listing.Stdout.Split('\n'))
        {
            var entry = line.TrimEnd('\r');
            if (entry.Length == 0)
            {
                continue;
            }

            if (entry.Contains('\0'))
            {
                return $"guest listing of '{guestRoot}' contains an undecodable entry; refusing sync-back";
            }

            string relative;
            try
            {
                relative = ModalGuestPath.GetRelativePath(guestRoot, entry);
            }
            catch (ArgumentException)
            {
                return $"guest listing of '{guestRoot}' escaped its root; refusing sync-back";
            }

            if (relative.Length == 0)
            {
                continue;
            }

            relatives.Add(relative);
            if (relatives.Count > opts.MaxStageFileCount)
            {
                return $"sync-back of '{guestRoot}' exceeds {opts.MaxStageFileCount} files; refusing sync-back";
            }
        }

        var staging = hostRoot + ".codeybox-sync-" + Guid.NewGuid().ToString("N");
        long totalBytes = 0;
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var relative in relatives)
            {
                ct.ThrowIfCancellationRequested();
                var guestPath = guestRoot.TrimEnd('/') + "/" + relative;
                byte[] bytes;
                try
                {
                    bytes = await _client.ReadFileBytesAsync(
                        opts.ApiBaseUrl, _readCredentials(), Id, guestPath, opts.MaxReadBackBytes, opts.ApiTimeout, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return $"sync-back read of '{guestPath}' failed: {ex.Message}";
                }

                totalBytes += bytes.Length;
                if (totalBytes > opts.MaxStageTotalBytes)
                {
                    return $"sync-back of '{guestRoot}' exceeds {opts.MaxStageTotalBytes} bytes; refusing sync-back";
                }

                var stagedPath = Path.GetFullPath(Path.Combine(staging, relative));
                if (!stagedPath.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    return $"guest path '{relative}' escapes the sync-back staging dir; refusing sync-back";
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
                    await File.WriteAllBytesAsync(stagedPath, bytes, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return $"sync-back write of '{relative}' failed: {ex.Message}";
                }
            }

            SwapInStaging(hostRoot, staging);
            staging = string.Empty;
            return null;
        }
        finally
        {
            if (!string.IsNullOrEmpty(staging) && Directory.Exists(staging))
            {
                try
                {
                    Directory.Delete(staging, recursive: true);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Modal sandbox {SandboxId}: sync-back staging cleanup failed.", Id);
                }
            }
        }
    }

    private async Task<string?> SyncOneFileBackAsync(
        ModalSandboxOptions opts, string guestPath, string hostFile, CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            bytes = await _client.ReadFileBytesAsync(
                opts.ApiBaseUrl, _readCredentials(), Id, guestPath, opts.MaxReadBackBytes, opts.ApiTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"sync-back read of '{guestPath}' failed: {ex.Message}";
        }

        if (bytes.Length > opts.MaxStageTotalBytes)
        {
            return $"sync-back of '{guestPath}' exceeds {opts.MaxStageTotalBytes} bytes; refusing sync-back";
        }

        try
        {
            var temp = hostFile + ".codeybox-sync-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(hostFile))!);
            await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
            File.Move(temp, hostFile, overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            return $"sync-back write of '{hostFile}' failed: {ex.Message}";
        }
    }

    private static void SwapInStaging(string hostRoot, string staging)
    {
        if (!Directory.Exists(hostRoot) && !File.Exists(hostRoot))
        {
            Directory.Move(staging, hostRoot);
            return;
        }

        var backup = hostRoot + ".codeybox-prev-" + Guid.NewGuid().ToString("N");
        Directory.Move(hostRoot, backup);
        try
        {
            Directory.Move(staging, hostRoot);
        }
        catch
        {
            Directory.Move(backup, hostRoot);
            throw;
        }

        Directory.Delete(backup, recursive: true);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

    /// <summary>
    /// Bounded per-stream output buffer. Bounded execs retain the head of the
    /// stream and flag the limit; streaming execs (<c>StreamOutputWithoutKill</c>)
    /// retain a bounded tail while the full volume still reaches the chunk
    /// callback (the existing stdout broadcast path — no parallel mechanism).
    /// </summary>
    private sealed class OutputBuffer
    {
        private readonly int _capBytes;
        private readonly bool _tail;
        private readonly Action<string>? _callback;
        private readonly StringBuilder _head = new();
        private readonly Queue<(string Text, int Bytes)> _tailChunks = new();
        private int _bufferedBytes;

        public OutputBuffer(int capBytes, bool tail, Action<string>? callback)
        {
            _capBytes = Math.Max(1, capBytes);
            _tail = tail;
            _callback = callback;
        }

        public bool LimitExceeded { get; private set; }

        public void Append(string chunk)
        {
            if (string.IsNullOrEmpty(chunk))
            {
                return;
            }

            _callback?.Invoke(chunk);

            var chunkBytes = Encoding.UTF8.GetByteCount(chunk);
            if (_bufferedBytes + chunkBytes <= _capBytes)
            {
                Enqueue(chunk, chunkBytes);
                return;
            }

            LimitExceeded = true;
            if (!_tail)
            {
                return;
            }

            Enqueue(chunk, chunkBytes);
            while (_bufferedBytes > _capBytes && _tailChunks.Count > 0)
            {
                var dropped = _tailChunks.Dequeue();
                _bufferedBytes -= dropped.Bytes;
            }
        }

        public override string ToString()
        {
            if (!_tail)
            {
                return _head.ToString();
            }

            var builder = new StringBuilder();
            foreach (var (text, _) in _tailChunks)
            {
                builder.Append(text);
            }

            return builder.ToString();
        }

        private void Enqueue(string chunk, int chunkBytes)
        {
            if (_tail)
            {
                _tailChunks.Enqueue((chunk, chunkBytes));
            }
            else
            {
                _head.Append(chunk);
            }

            _bufferedBytes += chunkBytes;
        }
    }
}

/// <summary>One staged writable mount awaiting teardown sync-back.</summary>
internal sealed record WritableMountSync(string GuestPath, string HostPath, bool IsFile);
