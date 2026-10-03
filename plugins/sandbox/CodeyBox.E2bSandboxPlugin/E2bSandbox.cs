using System.Collections.Concurrent;
using System.Text;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.E2bSandboxPlugin;

/// <summary>
/// One live E2B sandbox. Disposal syncs writable host mounts back once and
/// deletes the sandbox; a prior <see cref="SuspendAsync"/> (or
/// <see cref="StopAndPreserveAsync"/>) flips preserve-on-dispose so disposal
/// becomes a no-op and the paused sandbox survives for a later resume.
///
/// <para>Exec goes through the envd command gateway synchronously: output is
/// captured fully (bounded) per the <see cref="ISandbox"/> contract and handed
/// to the pipeline's exec pipe, which streams it onward. Cancelling the exec
/// token aborts the client wait; the guest command is bounded by the
/// per-exec <c>timeoutMs</c> but is not guaranteed dead on cancel — the
/// service kills it at the deadline. There is no server-side kill endpoint in
/// the recorded contract, so <see cref="KillActiveExecsAsync"/> cancels the
/// tracked client waits best-effort.</para>
/// </summary>
public sealed class E2bSandbox : ISandbox, ISuspendableSandbox, IPreemptibleSandbox,
    IShutdownTeardownSandbox, IProviderOwnedSandbox, IPreserveOnDisposeSandbox,
    IRejectsFileBackedAgentCredentials, ISandboxPortPublisher
{
    private readonly E2bApiClient _client;
    private readonly Func<E2bSandboxOptions> _readOptions;
    private readonly Func<string> _readApiKey;
    private readonly SandboxSpec _spec;
    private readonly string _workingDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _log;
    private readonly Action<string> _untrack;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _inFlightExecs = new();
    private List<E2bWritableMountSync> _stagedWritableMounts = [];
    private string _accessToken;

    private int _disposed;
    private bool _preserveOnDispose;
    private bool _ownedByShutdownHandler;

    internal E2bSandbox(
        string id,
        string accessToken,
        E2bApiClient client,
        Func<E2bSandboxOptions> readOptions,
        Func<string> readApiKey,
        SandboxSpec spec,
        TimeProvider timeProvider,
        ILogger log,
        Action<string> untrack)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        Id = id;
        _accessToken = accessToken;
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _readOptions = readOptions ?? throw new ArgumentNullException(nameof(readOptions));
        _readApiKey = readApiKey ?? throw new ArgumentNullException(nameof(readApiKey));
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        _workingDirectory = string.IsNullOrWhiteSpace(spec.WorkingDirectory) ? "/home/user" : spec.WorkingDirectory;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _untrack = untrack ?? throw new ArgumentNullException(nameof(untrack));
    }

    public string Id { get; }

    string IProviderOwnedSandbox.ProviderId => E2bSandboxOptions.ProviderKind;

    bool IShutdownTeardownSandbox.IsOwnedByShutdownHandler => _ownedByShutdownHandler;

    void IShutdownTeardownSandbox.MarkOwnedByShutdownHandler() => _ownedByShutdownHandler = true;

    void IPreserveOnDisposeSandbox.DisablePreserveOnDispose() => _preserveOnDispose = false;

    public bool IsSuspended { get; private set; }

    string IRejectsFileBackedAgentCredentials.FileBackedAgentCredentialsUnsupportedReason =>
        "E2B sandboxes have no tmpfs and their disk lives on hosted infrastructure CodeyBox does not control: " +
        "credential files would persist on third-party storage past the work item. Pass credentials as environment variables instead.";

    public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exec);
        ThrowIfDisposed();
        var opts = _readOptions();

        string? stagedEnvFile = null;
        string command;
        if (exec.EnvironmentContainsSecrets && exec.ExtraEnvironment is { Count: > 0 })
        {
            (command, stagedEnvFile) = await BuildSecretCommandAsync(opts, exec, exec.WorkingDirectory ?? _workingDirectory, ct).ConfigureAwait(false);
        }
        else
        {
            command = E2bShellCommand.Build(
                _spec.Environment,
                exec,
                exec.WorkingDirectory ?? _workingDirectory,
                opts.MaxEnvironmentBytes,
                opts.MaxCommandBytes,
                opts.MaxStdinBytes);
        }

        try
        {
            return await RunGuestCommandAsync(opts, command, exec, exec.WorkingDirectory ?? _workingDirectory, ct).ConfigureAwait(false);
        }
        finally
        {
            if (stagedEnvFile is not null)
            {
                await DeleteStagedEnvFileAsync(opts, stagedEnvFile).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Guest directory staging per-exec secret environment files. Random
    /// unguessable file names keep one exec's secrets out of other guests'
    /// reach; the sourcing command deletes its file before running argv.
    /// </summary>
    private const string SecretEnvStagingDirectory = "/tmp/.codeybox-exec-env";

    private async Task<(string Command, string EnvFilePath)> BuildSecretCommandAsync(
        E2bSandboxOptions opts, SandboxExec exec, string workingDirectory, CancellationToken ct)
    {
        var (merged, removals) = E2bShellCommand.MergeEnvironment(_spec.Environment, exec);
        var content = E2bShellCommand.BuildEnvFileContent(merged, removals, opts.MaxEnvironmentBytes);
        var envFilePath = $"{SecretEnvStagingDirectory}/env-{Guid.NewGuid():N}";
        E2bGuestPath.ValidateAbsolute(envFilePath);

        try
        {
            await WriteFileAsync(envFilePath, content, ct).ConfigureAwait(false);
        }
        catch (E2bApiException ex)
        {
            throw ToUnavailable(ex);
        }

        return (E2bShellCommand.BuildSourcingCommand(
            envFilePath, exec, workingDirectory, opts.MaxCommandBytes, opts.MaxStdinBytes), envFilePath);
    }

    private async Task DeleteStagedEnvFileAsync(E2bSandboxOptions opts, string envFilePath)
    {
        try
        {
            await RunInternalAsync(opts, ["rm", "-f", envFilePath], "/", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "E2B sandbox {SandboxId}: best-effort staged env file delete failed.", Id);
        }
    }

    private async Task CleanupSecretStagingAsync(E2bSandboxOptions opts)
    {
        try
        {
            await RunInternalAsync(opts, ["rm", "-rf", SecretEnvStagingDirectory], "/", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "E2B sandbox {SandboxId}: best-effort secret staging cleanup failed.", Id);
        }
    }

    private async Task<SandboxExecResult> RunGuestCommandAsync(
        E2bSandboxOptions opts, string command, SandboxExec exec, string workingDirectory, CancellationToken ct)
    {
        var maxStdout = exec.MaxStdoutBytes ?? opts.MaxExecOutputBytes;
        var maxStderr = exec.MaxStderrBytes ?? opts.MaxExecOutputBytes;
        var deadline = _timeProvider.GetUtcNow() + (_spec.Limits.WallClock ?? TimeSpan.FromHours(6));
        var timeoutMs = Math.Max(1000, (int)Math.Min((deadline - _timeProvider.GetUtcNow()).TotalMilliseconds, int.MaxValue));

        var request = new E2bRunCommandRequest
        {
            Command = command,
            Cwd = workingDirectory,
            TimeoutMs = timeoutMs,
        };

        var execId = Guid.NewGuid();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _inFlightExecs[execId] = linked;
        try
        {
            E2bCommandResult result;
            try
            {
                var httpTimeout = deadline - _timeProvider.GetUtcNow() + TimeSpan.FromSeconds(30);
                if (httpTimeout <= TimeSpan.Zero)
                {
                    throw new TimeoutException($"Exec on sandbox {Id} exceeded the wall-clock budget.");
                }

                var maxResponseBytes = (long)maxStdout + maxStderr + 1024 * 1024;
                result = await _client.RunCommandAsync(
                    E2bSandboxProvider.EnvdBaseUrl(opts, Id), _accessToken, request, maxResponseBytes, httpTimeout, linked.Token).ConfigureAwait(false);
            }
            catch (E2bApiException ex)
            {
                throw ToUnavailable(ex);
            }

            // The synchronous gateway returns full output; caps are enforced
            // here on receipt. Kill-on-limit is inherent: the command already
            // finished, so over-cap output is truncated and flagged rather
            // than streamed past the bound.
            var (stdout, stdoutLimit) = Truncate(result.Stdout, maxStdout, exec.StreamOutputWithoutKill ? null : maxStdout);
            var (stderr, stderrLimit) = Truncate(result.Stderr, maxStderr, exec.StreamOutputWithoutKill ? null : maxStderr);
            if (exec.StreamOutputWithoutKill)
            {
                (stdout, _) = Tail(result.Stdout, exec.MaxRetainedStdoutBytes ?? SandboxExec.DefaultStreamedOutputTailBytes);
                (stderr, _) = Tail(result.Stderr, exec.MaxRetainedStderrBytes ?? SandboxExec.DefaultStreamedOutputTailBytes);
            }

            if (stdout.Length > 0)
            {
                exec.StdoutChunkCallback?.Invoke(stdout);
            }

            if (stderr.Length > 0)
            {
                exec.StderrChunkCallback?.Invoke(stderr);
            }

            return new SandboxExecResult(result.ExitCode, stdout, stderr, stdoutLimit, stderrLimit);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller abandoned the exec: the client wait is aborted. The
            // guest command is NOT guaranteed dead — the service kills it at
            // the per-exec timeoutMs. Propagate cancellation unchanged.
            throw;
        }
        finally
        {
            _inFlightExecs.TryRemove(execId, out _);
            linked.Dispose();
        }
    }

    internal void SetWritableMounts(IReadOnlyList<E2bWritableMountSync> writableMounts)
    {
        ArgumentNullException.ThrowIfNull(writableMounts);
        _stagedWritableMounts = writableMounts.ToList();
    }

    internal void RefreshAccessToken(string accessToken)
    {
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            _accessToken = accessToken;
        }
    }

    private IReadOnlyList<E2bWritableMountSync> WritableMounts => _stagedWritableMounts;

    public override string ToString() => $"e2b:{Id}";

    public Task KillActiveExecsAsync(CancellationToken ct = default)
    {
        _ = ct;
        ThrowIfDisposed();
        foreach (var exec in _inFlightExecs.Values)
        {
            try
            {
                exec.Cancel();
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "E2B sandbox {SandboxId}: cancelling a tracked exec wait failed.", Id);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Writes raw bytes to a guest-absolute path (base64 over the envd file gateway).</summary>
    public async Task WriteFileBytesAsync(string guestPath, byte[] contents, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guestPath);
        ArgumentNullException.ThrowIfNull(contents);
        ThrowIfDisposed();
        E2bGuestPath.ValidateAbsolute(guestPath);
        var opts = _readOptions();
        var encoded = Convert.ToBase64String(contents);
        try
        {
            await _client.WriteFileAsync(
                E2bSandboxProvider.EnvdBaseUrl(opts, Id), _accessToken, guestPath, encoded, opts.ApiTimeout, ct).ConfigureAwait(false);
        }
        catch (E2bApiException ex)
        {
            throw ToUnavailable(ex);
        }
    }

    /// <summary>Writes UTF-8 text to a guest-absolute path.</summary>
    public async Task WriteFileAsync(string guestPath, string contents, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guestPath);
        ArgumentNullException.ThrowIfNull(contents);
        ThrowIfDisposed();
        E2bGuestPath.ValidateAbsolute(guestPath);
        var opts = _readOptions();
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(contents));
        try
        {
            await _client.WriteFileAsync(
                E2bSandboxProvider.EnvdBaseUrl(opts, Id), _accessToken, guestPath, encoded, opts.ApiTimeout, ct).ConfigureAwait(false);
        }
        catch (E2bApiException ex)
        {
            throw ToUnavailable(ex);
        }
    }

    /// <summary>Reads bytes from a guest-absolute path (bounded).</summary>
    public async Task<byte[]> ReadFileBytesAsync(string guestPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guestPath);
        ThrowIfDisposed();
        E2bGuestPath.ValidateAbsolute(guestPath);
        var opts = _readOptions();
        try
        {
            return await _client.ReadFileAsync(
                E2bSandboxProvider.EnvdBaseUrl(opts, Id), _accessToken, guestPath, opts.MaxReadBackBytes, opts.ApiTimeout, ct).ConfigureAwait(false);
        }
        catch (E2bApiException ex)
        {
            throw ToUnavailable(ex);
        }
    }

    /// <summary>Reads UTF-8 text from a guest-absolute path.</summary>
    public async Task<string> ReadFileAsync(string guestPath, CancellationToken ct = default)
    {
        var bytes = await ReadFileBytesAsync(guestPath, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Creates a snapshot of this sandbox and returns its snapshot id (persistence).</summary>
    public async Task<string> CreateSnapshotAsync(string? name, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var opts = _readOptions();
        try
        {
            var snapshot = await _client.SnapshotSandboxAsync(
                opts.ApiBaseUrl, _readApiKey(), Id, name, opts.ApiTimeout, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(snapshot.SnapshotID))
            {
                throw new SandboxExecutionUnavailableException(-1);
            }

            return snapshot.SnapshotID;
        }
        catch (E2bApiException ex)
        {
            throw ToUnavailable(ex);
        }
    }

    public async Task SuspendAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var opts = _readOptions();
        await CleanupSecretStagingAsync(opts).ConfigureAwait(false);
        try
        {
            await _client.PauseSandboxAsync(opts.ApiBaseUrl, _readApiKey(), Id, opts.ApiTimeout, ct).ConfigureAwait(false);
        }
        catch (E2bApiException ex)
        {
            throw ToUnavailable(ex);
        }

        IsSuspended = true;
        _preserveOnDispose = true;
    }

    public Task StopAndPreserveAsync(CancellationToken ct = default) => SuspendAsync(ct);

    public bool CanPublishPort(int port)
    {
        var opts = _readOptions();
        return opts.EnablePreviewUrls && opts.AllowedPreviewPorts.Contains(port);
    }

    public SandboxPublishedPort PublishPort(int port)
    {
        var opts = _readOptions();
        if (!opts.EnablePreviewUrls)
        {
            throw new InvalidOperationException(
                $"E2B preview URLs are disabled (CodeyBox:Plugins:{E2bSandboxOptions.PluginId}:EnablePreviewUrls=false): " +
                "publishing a port would expose the running sandbox over the public internet. " +
                "An operator must opt in explicitly.");
        }

        if (!opts.AllowedPreviewPorts.Contains(port))
        {
            throw new InvalidOperationException(
                $"E2B preview port {port} is not in AllowedPreviewPorts; refusing to publish it.");
        }

        var host = $"{port}-{Id}.{opts.SandboxDomain}";
        return new SandboxPublishedPort(
            host,
            443,
            new Dictionary<string, string>
            {
                ["url"] = $"https://{host}/",
                ["guestPort"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["provider"] = E2bSandboxOptions.ProviderKind,
            });
    }

    public async Task SyncStateToHostAsync(CancellationToken ct = default)
    {
        string? syncError;
        try
        {
            syncError = await SyncWritableMountsBackAsync(_readOptions(), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "E2B sandbox {SandboxId}: SyncStateToHostAsync failed.", Id);
            return;
        }

        if (syncError is not null)
        {
            _log.LogWarning(
                "E2B sandbox {SandboxId}: teardown sync-back incomplete ({Reason}); continuing.",
                Id, syncError);
        }
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
        await CleanupSecretStagingAsync(opts).ConfigureAwait(false);
        var syncError = await SyncWritableMountsBackAsync(opts, CancellationToken.None).ConfigureAwait(false);
        if (syncError is not null)
        {
            _log.LogWarning(
                "E2B sandbox {SandboxId}: teardown sync-back incomplete ({Reason}); deleting anyway.",
                Id, syncError);
        }

        try
        {
            await _client.DeleteSandboxAsync(opts.ApiBaseUrl, _readApiKey(), Id, opts.ApiTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "E2B sandbox {SandboxId}: delete failed.", Id);
        }
    }

    internal async Task<string?> SyncWritableMountsBackAsync(E2bSandboxOptions opts, CancellationToken ct)
    {
        if (WritableMounts.Count == 0)
        {
            return null;
        }

        long totalBytes = 0;
        foreach (var mount in WritableMounts)
        {
            SandboxExecResult list;
            try
            {
                list = await RunInternalAsync(
                    opts, ["find", mount.GuestPath, "-type", "f", "-print"], mount.GuestPath, ct).ConfigureAwait(false);
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
                    relative = E2bGuestPath.GetRelativePath(mount.GuestPath, guestFile);
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

                if (HostPathPassesThroughSymlink(mount.HostPath, hostFile))
                {
                    return $"host path passes through a symlink for {guestFile}";
                }

                byte[] bytes;
                try
                {
                    bytes = await _client.ReadFileAsync(
                        E2bSandboxProvider.EnvdBaseUrl(opts, Id), _accessToken, guestFile, opts.MaxReadBackBytes, opts.ApiTimeout, ct).ConfigureAwait(false);
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

    private static bool HostPathPassesThroughSymlink(string mountRoot, string hostFile)
    {
        var current = hostFile;
        if (!File.Exists(current) && !Directory.Exists(current))
        {
            current = Path.GetDirectoryName(current) ?? mountRoot;
        }

        while (true)
        {
            if (string.Equals(current, mountRoot, StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                if (File.Exists(current) || Directory.Exists(current))
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                return true;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent.Length >= current.Length)
            {
                return true;
            }

            current = parent;
            if (!current.StartsWith(mountRoot, StringComparison.Ordinal))
            {
                return true;
            }
        }
    }

    internal async Task<SandboxExecResult> RunInternalAsync(
        E2bSandboxOptions opts, string[] argv, string workingDirectory, CancellationToken ct)
    {
        var command = E2bShellCommand.Build(
            new Dictionary<string, string>(),
            new SandboxExec { Argv = argv, WorkingDirectory = workingDirectory },
            workingDirectory,
            opts.MaxEnvironmentBytes,
            opts.MaxCommandBytes,
            opts.MaxStdinBytes);
        var request = new E2bRunCommandRequest { Command = command, Cwd = workingDirectory, TimeoutMs = 60000 };
        E2bCommandResult result;
        try
        {
            result = await _client.RunCommandAsync(
                E2bSandboxProvider.EnvdBaseUrl(opts, Id), _accessToken, request,
                opts.MaxExecOutputBytes + 1024 * 1024, opts.ApiTimeout, ct).ConfigureAwait(false);
        }
        catch (E2bApiException ex)
        {
            throw ToUnavailable(ex);
        }

        return new SandboxExecResult(result.ExitCode, result.Stdout, result.Stderr);
    }

    private static (string Text, bool Limited) Truncate(string value, int maxBytes, int? hardCap)
    {
        var bytes = Encoding.UTF8.GetByteCount(value);
        var cap = hardCap ?? maxBytes;
        if (bytes <= cap)
        {
            return (value, false);
        }

        var budget = Math.Max(0, cap);
        var builder = new StringBuilder(value.Length);
        var used = 0;
        foreach (var ch in value)
        {
            var charBytes = Encoding.UTF8.GetByteCount(new[] { ch });
            if (used + charBytes > budget)
            {
                break;
            }

            builder.Append(ch);
            used += charBytes;
        }

        return (builder.ToString(), true);
    }

    private static (string Text, bool Limited) Tail(string value, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetByteCount(value);
        if (bytes <= maxBytes)
        {
            return (value, false);
        }

        var chars = value.ToCharArray();
        var used = 0;
        var start = chars.Length;
        for (var i = chars.Length - 1; i >= 0; i--)
        {
            var charBytes = Encoding.UTF8.GetByteCount(new[] { chars[i] });
            if (used + charBytes > maxBytes)
            {
                break;
            }

            used += charBytes;
            start = i;
        }

        return (new string(chars, start, chars.Length - start), true);
    }

    private SandboxExecutionUnavailableException ToUnavailable(E2bApiException ex) =>
        new(ToExitCode(ex));

    private static int ToExitCode(E2bApiException ex) =>
        ex.StatusCode.HasValue ? -(int)ex.StatusCode.Value : -1;

    private void ThrowIfDisposed()
    {
        if (_disposed != 0)
        {
            throw new ObjectDisposedException(nameof(E2bSandbox), $"Sandbox {Id} is disposed.");
        }
    }
}

/// <summary>One writable host mount synced back to the host once at teardown.</summary>
internal sealed record E2bWritableMountSync(string GuestPath, string HostPath);
