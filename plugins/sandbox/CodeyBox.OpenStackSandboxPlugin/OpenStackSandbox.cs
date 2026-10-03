using System.Collections.Concurrent;
using System.Text;
using CodeyBox.Core;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.MultipassRemote;
using Microsoft.Extensions.Logging;

namespace CodeyBox.OpenStackSandboxPlugin;

/// <summary>A host path staged into the guest at <see cref="RemotePath"/>.</summary>
public sealed record OpenStackStagedMount(string RemotePath, string? HostPath, bool Writable);

/// <summary>
/// Live sandbox handle for one OpenStack VM. Exec and file staging go through
/// the shared <see cref="IRemoteHostTransport"/> seam (the same OpenSSH CLI
/// transport the multipass-remote provider uses) with plain argv — no
/// multipass wrapper. Disposal syncs writable mounts home, then runs the
/// provider's idempotent cloud cleanup and wipes the per-sandbox SSH key
/// material from host disk.
/// </summary>
public sealed class OpenStackSandbox : IShutdownTeardownSandbox, IPrivilegedGuestFileHardeningSandbox
{
    private readonly SandboxSpec _spec;
    private readonly IReadOnlyList<OpenStackStagedMount> _stagedMounts;
    private readonly IRemoteHostTransport _transport;
    private readonly Func<Task> _deleteCloudResourcesAsync;
    private readonly Action _onDisposed;
    private readonly string _sshTempDirectory;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<CancellationTokenSource, byte> _activeExecCts = new();
    private readonly SemaphoreSlim _disposeLock = new(1, 1);
    private int _disposed;
    private int _activeTrackingReleased;

    public OpenStackSandbox(
        string serverName,
        SandboxSpec spec,
        IReadOnlyList<OpenStackStagedMount> stagedMounts,
        IRemoteHostTransport transport,
        Func<Task> deleteCloudResourcesAsync,
        Action onDisposed,
        string sshTempDirectory,
        ILogger log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(stagedMounts);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(deleteCloudResourcesAsync);
        ArgumentNullException.ThrowIfNull(onDisposed);
        ArgumentException.ThrowIfNullOrWhiteSpace(sshTempDirectory);
        ArgumentNullException.ThrowIfNull(log);
        Id = serverName;
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

    public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exec);
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(OpenStackSandbox));
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
        if (Volatile.Read(ref _disposed) != 0)
            return;
        foreach (var mount in _stagedMounts)
        {
            if (!mount.Writable || mount.HostPath is null)
                continue;
            await _transport.StageOutAsync(mount.RemotePath, mount.HostPath, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _disposeLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            try
            {
                await SyncStateToHostAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "OpenStack sandbox {Id}: writable-mount sync-back failed during disposal", Id);
            }
            try
            {
                await _deleteCloudResourcesAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "OpenStack sandbox {Id}: cloud cleanup failed during disposal", Id);
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
            _log.LogWarning(ex, "OpenStack sandbox {Id}: dispose notification failed", Id);
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
            _log.LogWarning(ex, "OpenStack sandbox {Id}: failed to remove SSH key directory", Id);
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
