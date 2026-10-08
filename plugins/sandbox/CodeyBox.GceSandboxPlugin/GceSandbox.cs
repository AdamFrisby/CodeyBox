using System.Collections.Concurrent;
using System.Text;
using CodeyBox.Core;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.MultipassRemote;
using Microsoft.Extensions.Logging;

namespace CodeyBox.GceSandboxPlugin;

/// <summary>A host path staged into the guest at <see cref="RemotePath"/>.</summary>
public sealed record GceStagedMount(string RemotePath, string? HostPath, bool Writable);

/// <summary>
/// Live sandbox handle for one Compute Engine VM. Exec and file staging go through
/// the shared <see cref="IRemoteHostTransport"/> seam (the same OpenSSH CLI transport
/// the multipass-remote and openstack providers use) with plain argv — no wrapper.
/// Disposal syncs writable mounts home <em>before</em> cloud resources are deleted and
/// before the handle is released, then runs the provider's idempotent cloud cleanup and
/// wipes the per-sandbox SSH key material from host disk.
///
/// <para>Ordering note: unlike a naive dispose-then-sync, this handle claims disposal
/// (blocking new execs), cancels tracked active execs, performs the writable-mount
/// sync-back, and only then deletes cloud resources and notifies the provider. The
/// public <see cref="SyncStateToHostAsync"/> refuses work once disposal has started;
/// disposal itself uses an internal path that bypasses that refusal, so the sync is
/// never skipped by the disposed flag.</para>
/// </summary>
public sealed class GceSandbox : IShutdownTeardownSandbox, IPrivilegedGuestFileHardeningSandbox
{
    private readonly SandboxSpec _spec;
    private readonly IReadOnlyList<GceStagedMount> _stagedMounts;
    private readonly IRemoteHostTransport _transport;
    private readonly Func<Task> _deleteCloudResourcesAsync;
    private readonly Action _onDisposed;
    private readonly string _sshTempDirectory;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<CancellationTokenSource, byte> _activeExecCts = new();
    private readonly SemaphoreSlim _disposeLock = new(1, 1);
    private int _disposeStarted;
    private int _activeTrackingReleased;

    public GceSandbox(
        string instanceName,
        SandboxSpec spec,
        IReadOnlyList<GceStagedMount> stagedMounts,
        IRemoteHostTransport transport,
        Func<Task> deleteCloudResourcesAsync,
        Action onDisposed,
        string sshTempDirectory,
        ILogger log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(stagedMounts);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(deleteCloudResourcesAsync);
        ArgumentNullException.ThrowIfNull(onDisposed);
        ArgumentException.ThrowIfNullOrWhiteSpace(sshTempDirectory);
        ArgumentNullException.ThrowIfNull(log);
        Id = instanceName;
        _spec = spec;
        _stagedMounts = stagedMounts;
        _transport = transport;
        _deleteCloudResourcesAsync = deleteCloudResourcesAsync;
        _onDisposed = onDisposed;
        _sshTempDirectory = sshTempDirectory;
        _log = log;
    }

    public string Id { get; }

    internal bool IsTrackedActive => Volatile.Read(ref _activeTrackingReleased) == 0;

    internal void ReleaseActiveTracking() => Volatile.Write(ref _activeTrackingReleased, 1);

    internal bool DisposalStarted => Volatile.Read(ref _disposeStarted) != 0;

    public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exec);
        if (Volatile.Read(ref _disposeStarted) != 0)
            throw new ObjectDisposedException(nameof(GceSandbox));
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
    /// Cancels locally-tracked execs. This stops the host-side SSH client; it is NOT proof
    /// the remote command stopped — the provider's teardown/termination fallback (instance
    /// deletion) is the backstop that ends guest execution, and resource identity is
    /// retained for reconciliation until deletion is confirmed.
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

    public async Task SyncStateToHostAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposeStarted) != 0)
            return;
        await SyncStateInternalAsync(ct).ConfigureAwait(false);
    }

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
                _log.LogWarning(ex, "GCE sandbox {Id}: cancelling active execs failed during disposal", Id);
            }
            try
            {
                await SyncStateInternalAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "GCE sandbox {Id}: writable-mount sync-back failed during disposal", Id);
            }
            try
            {
                await _deleteCloudResourcesAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "GCE sandbox {Id}: cloud cleanup failed during disposal", Id);
            }
            DeleteSshTempDirectory();
        }
        finally
        {
            _disposeLock.Release();
        }
        try
        {
            _onDisposed();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GCE sandbox {Id}: dispose notification failed", Id);
        }
        GC.SuppressFinalize(this);
    }

    private async Task SyncStateInternalAsync(CancellationToken ct)
    {
        foreach (var mount in _stagedMounts)
        {
            if (!mount.Writable || mount.HostPath is null)
                continue;
            await _transport.StageOutAsync(mount.RemotePath, mount.HostPath, ct).ConfigureAwait(false);
        }
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
            _log.LogWarning(ex, "GCE sandbox {Id}: failed to remove SSH key directory", Id);
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
