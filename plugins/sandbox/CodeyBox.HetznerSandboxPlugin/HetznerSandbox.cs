using System.Collections.Concurrent;
using System.Text;
using CodeyBox.Core;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.MultipassRemote;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.HetznerSandboxPlugin;

/// <summary>A host→guest staged mount: the guest path plus the host source and writability.</summary>
public sealed record HetznerStagedMount(string RemotePath, string? HostPath, bool Writable);

/// <summary>
/// Live handle for one Hetzner Cloud sandbox server: exec over the shared
/// OpenSSH transport, file staging, writable-mount sync-back, and teardown of
/// the server plus its SSH key, firewall, and floating IP.
///
/// <para>Disposal order is sync-back first, then cloud cleanup: writable
/// mounts are synced while the transport is still alive, and only afterwards
/// is the server deleted. Two flags enforce this — <c>_disposeStarted</c>
/// refuses new execs the moment disposal begins, while <c>_disposed</c> is set
/// only after cleanup finishes, so the in-dispose sync is never skipped as a
/// no-op.</para>
/// </summary>
public sealed class HetznerSandbox : IShutdownTeardownSandbox
{
    private readonly SandboxSpec _spec;
    private readonly IReadOnlyList<HetznerStagedMount> _stagedMounts;
    private readonly IRemoteHostTransport _transport;
    private readonly Func<Task> _deleteCloudResourcesAsync;
    private readonly Action _onDisposed;
    private readonly string _sshTempDirectory;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<CancellationTokenSource, byte> _activeExecCts = new();
    private readonly SemaphoreSlim _disposeLock = new(1, 1);
    private int _disposeStarted;
    private int _disposed;

    /// <summary>Creates a live sandbox handle. The provider owns provisioning; this owns the handle.</summary>
    public HetznerSandbox(
        string id,
        SandboxSpec spec,
        IReadOnlyList<HetznerStagedMount> stagedMounts,
        IRemoteHostTransport transport,
        Func<Task> deleteCloudResourcesAsync,
        Action onDisposed,
        string sshTempDirectory,
        ILogger? log = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        _stagedMounts = stagedMounts ?? throw new ArgumentNullException(nameof(stagedMounts));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _deleteCloudResourcesAsync = deleteCloudResourcesAsync ?? throw new ArgumentNullException(nameof(deleteCloudResourcesAsync));
        _onDisposed = onDisposed ?? throw new ArgumentNullException(nameof(onDisposed));
        _sshTempDirectory = sshTempDirectory ?? throw new ArgumentNullException(nameof(sshTempDirectory));
        _log = log ?? NullLogger.Instance;
    }

    /// <inheritdoc/>
    public string Id { get; }

    /// <inheritdoc/>
    public bool IsTrackedActive => Volatile.Read(ref _disposed) == 0;

    /// <summary>
    /// Releases active tracking without running disposal (provider-owned
    /// teardown paths that clean cloud resources themselves call this so the
    /// live counter stays accurate).
    /// </summary>
    internal void ReleaseActiveTracking() => Interlocked.Exchange(ref _disposed, 1);

    /// <inheritdoc/>
    public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exec);
        if (Volatile.Read(ref _disposeStarted) != 0 || Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(HetznerSandbox));
        if (exec.Argv.Count == 0)
            throw new ArgumentException("Argv must be non-empty.", nameof(exec));

        var workdir = exec.WorkingDirectory ?? _spec.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(workdir))
            workdir = SandboxConventions.WorkDir;
        var effectiveEnvironment = exec.ExtraEnvironment is { Count: > 0 }
            ? new Dictionary<string, string>(exec.ExtraEnvironment, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        exec.ApplyEnvironmentRemovals(name => effectiveEnvironment.Remove(name));

        IReadOnlyList<string> remoteArgv;
        string? transportStdin;
        if (exec.EnvironmentContainsSecrets && effectiveEnvironment.Count > 0)
        {
            var environmentFile = SandboxEnvironmentVariablePolicy.BuildShellEnvironmentFileContent(
                effectiveEnvironment);
            var commandStdin = exec.Stdin ?? string.Empty;
            remoteArgv =
            [
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
            var script = new StringBuilder();
            script.Append("cd ").Append(QuoteShellWord(workdir)).Append(" && ");
            foreach (var name in exec.EnvironmentVariablesToUnset)
                script.Append("unset -- ").Append(QuoteShellWord(name)).Append(" && ");
            foreach (var (key, value) in effectiveEnvironment)
            {
                SandboxEnvironmentVariableName.Validate(key, nameof(exec.ExtraEnvironment));
                script.Append(key).Append('=').Append(QuoteShellWord(value)).Append(' ');
            }
            script.Append(QuoteShellArgv(exec.Argv));
            remoteArgv = ["bash", "-lc", script.ToString()];
            transportStdin = exec.Stdin;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _activeExecCts[linkedCts] = 0;
        try
        {
            try
            {
                var run = await _transport.RunAsync(
                    remoteArgv,
                    stdin: transportStdin,
                    linkedCts.Token,
                    stdoutChunkCallback: exec.StdoutChunkCallback,
                    stderrChunkCallback: exec.StderrChunkCallback,
                    maxStdoutBytes: exec.MaxStdoutBytes,
                    maxStderrBytes: exec.MaxStderrBytes,
                    killOnOutputLimit: exec.KillOnOutputLimit).ConfigureAwait(false);
                return new SandboxExecResult(
                    ExitCode: run.ExitCode,
                    Stdout: run.Stdout,
                    Stderr: run.Stderr,
                    StdoutLimitExceeded: run.StdoutLimitExceeded,
                    StderrLimitExceeded: run.StderrLimitExceeded);
            }
            catch (RemoteSshTransportException)
            {
                throw new SandboxExecutionUnavailableException(OpenSshCliTransport.SshTransportFailureExitCode);
            }
        }
        finally
        {
            _activeExecCts.TryRemove(linkedCts, out _);
        }
    }

    /// <summary>
    /// Cancels the local SSH operations for active execs. This proves only
    /// that the local transport stopped waiting — the remote command may still
    /// be running in the guest. The provider's disposal path therefore still
    /// shuts down and deletes the server: killing the VM is the termination
    /// fallback, never the local cancel alone.
    /// </summary>
    public Task KillActiveExecsAsync(CancellationToken ct = default)
    {
        _ = ct;
        foreach (var cts in _activeExecCts.Keys)
        {
            try { cts.Cancel(); } catch { }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task SyncStateToHostAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        foreach (var mount in _stagedMounts)
        {
            if (!mount.Writable || mount.HostPath is null)
                continue;
            await _transport.StageOutAsync(mount.RemotePath, mount.HostPath, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Disposes the sandbox: refuses new execs, cancels active ones,
    /// syncs writable mounts back to the host, then deletes the cloud
    /// resources. Sync-back runs BEFORE the server is deleted and BEFORE the
    /// handle is marked disposed, so disposal never skips the sync as a
    /// no-op. Cloud-cleanup failures are logged and retained (the leak
    /// reaper re-lists by labels) — disposal itself does not throw.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;
        await _disposeLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            try
            {
                await KillActiveExecsAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Hetzner sandbox {Id}: failed to cancel active execs during disposal", Id);
            }
            try
            {
                await SyncStateToHostAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Hetzner sandbox {Id}: writable-mount sync-back failed during disposal", Id);
            }
            try
            {
                await _deleteCloudResourcesAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Hetzner sandbox {Id}: cloud cleanup failed during disposal", Id);
            }
            DeleteSshTempDirectory();
        }
        finally
        {
            Interlocked.Exchange(ref _disposed, 1);
            _disposeLock.Release();
        }
        try
        {
            _onDisposed();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Hetzner sandbox {Id}: dispose notification failed", Id);
        }
        GC.SuppressFinalize(this);
    }

    internal void DeleteSshTempDirectory()
    {
        try
        {
            if (Directory.Exists(_sshTempDirectory))
                Directory.Delete(_sshTempDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Hetzner sandbox {Id}: failed to remove SSH key directory", Id);
        }
    }

    internal static bool IsCredentialPath(string sandboxPath)
    {
        var trimmed = sandboxPath.TrimEnd('/');
        return trimmed.Equals(SandboxConventions.CredentialsDir, StringComparison.Ordinal)
            || trimmed.StartsWith(SandboxConventions.CredentialsDir + "/", StringComparison.Ordinal);
    }

    internal static string QuoteShellArgv(IReadOnlyList<string> argv)
    {
        var script = new StringBuilder(argv.Count * 16);
        for (var i = 0; i < argv.Count; i++)
        {
            if (i > 0)
                script.Append(' ');
            script.Append(QuoteShellWord(argv[i]));
        }
        return script.ToString();
    }

    internal static string QuoteShellWord(string value)
    {
        if (value.Length == 0)
            return "''";
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    // Delivers secret-bearing environment via stdin (byte-counted dd reads),
    // never via argv: argv is visible to host-side process listings, while
    // stdin bytes travel inside the SSH channel.
    internal const string SecretEnvironmentBootstrapScript =
        "set -e; " +
        "env_len=$1; stdin_len=$2; wd=$3; unset_count=$4; shift 4; " +
        "i=0; while [ \"$i\" -lt \"$unset_count\" ]; do unset -- \"$1\"; shift; i=$((i+1)); done; " +
        "env_file=$(mktemp /tmp/.codeybox-env-XXXXXX); " +
        "trap 'rm -f \"$env_file\"' EXIT; " +
        "if [ \"$env_len\" -gt 0 ]; then dd bs=1 count=\"$env_len\" of=\"$env_file\" 2>/dev/null; fi; " +
        "set -a; . \"$env_file\"; set +a; " +
        "rm -f \"$env_file\"; trap - EXIT; " +
        "cd \"$wd\" || exit 127; " +
        "if [ \"$stdin_len\" -gt 0 ]; then dd bs=1 count=\"$stdin_len\" 2>/dev/null | exec \"$@\"; " +
        "else exec \"$@\" < /dev/null; fi";
}
