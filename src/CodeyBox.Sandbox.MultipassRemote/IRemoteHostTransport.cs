using CodeyBox.HostProcess;

namespace CodeyBox.Sandbox.MultipassRemote;

/// <summary>
/// Single seam through which <see cref="MultipassRemoteSandboxProvider"/> talks
/// to a remote host. Wraps SSH-style transports (OpenSSH client by default) so
/// that:
/// <list type="bullet">
///   <item>Remote command exec is uniform — the provider doesn't know whether
///   bytes flow over OpenSSH, Renci.SshNet, or an in-memory test fake.</item>
///   <item>Live stdout/stderr chunks reach the orchestrator host as they're
///   produced — required for AgentStreamCapture to tail an agent CLI in real
///   time across the network.</item>
///   <item>Transport-level failures (connection refused, auth rejected,
///   key permission errors) raise <see cref="RemoteSshTransportException"/>,
///   distinct from a non-zero exit code on a successful remote call — so the
///   orchestrator can classify the failure as recoverable sandbox-level rather
///   than agent-level.</item>
/// </list>
///
/// <para>File staging is exposed as separate <see cref="StageInAsync"/> /
/// <see cref="StageOutAsync"/> calls rather than a generic copy because the
/// underlying transport may use a more efficient channel (e.g. SFTP via
/// Renci.SshNet, or <c>tar | ssh tar</c> over OpenSSH). Both shapes must
/// preserve recursive directory contents and Unix file modes.</para>
/// </summary>
public interface IRemoteHostTransport
{
    /// <summary>
    /// Stable id for diagnostics ("openssh-cli", "fake", ...). Embedded into
    /// log messages so multi-host deployments can attribute failures.
    /// </summary>
    string DiagnosticId { get; }

    /// <summary>
    /// Execute a single command on the remote host. The argv is interpreted as
    /// program + args by the remote shell after the transport's own quoting.
    ///
    /// <para>Stdout / stderr chunk callbacks must be invoked as bytes arrive —
    /// implementations MUST NOT buffer to EOF, since agent runners rely on the
    /// live stream for AgentStreamCapture.</para>
    ///
    /// <para>When <paramref name="maxStdoutBytes"/> or
    /// <paramref name="maxStderrBytes"/> is non-null, the value is the maximum
    /// UTF-8 byte count retained and forwarded for that stream. Implementations
    /// set the matching limit-exceeded flag on <see cref="ProcessRunResult"/>
    /// when the cap is crossed; <paramref name="killOnOutputLimit"/> controls
    /// whether the remote command is terminated at that point or the excess
    /// bytes are drained and discarded. A null stream limit means the caller has
    /// not requested a cap for that stream.</para>
    ///
    /// <para>Connection / auth failures throw
    /// <see cref="RemoteSshTransportException"/>. A successfully-executed
    /// remote command that exits non-zero returns a <see cref="ProcessRunResult"/>
    /// with <c>ExitCode != 0</c> — that is NOT a transport failure.</para>
    /// </summary>
    /// <param name="argv">Remote program and arguments before transport quoting.</param>
    /// <param name="stdin">Optional text stdin to send to the remote command.</param>
    /// <param name="ct">Cancellation token for the local transport operation.</param>
    /// <param name="stdoutChunkCallback">Optional live stdout callback receiving retained chunks.</param>
    /// <param name="stderrChunkCallback">Optional live stderr callback receiving retained chunks.</param>
    /// <param name="maxStdoutBytes">Optional retained stdout limit in UTF-8 bytes; null means no caller cap.</param>
    /// <param name="maxStderrBytes">Optional retained stderr limit in UTF-8 bytes; null means no caller cap.</param>
    /// <param name="killOnOutputLimit">True to terminate the remote command when a stream cap is exceeded; false to keep draining while discarding excess bytes.</param>
    Task<ProcessRunResult> RunAsync(
        IReadOnlyList<string> argv,
        string? stdin,
        CancellationToken ct,
        Action<string>? stdoutChunkCallback = null,
        Action<string>? stderrChunkCallback = null,
        int? maxStdoutBytes = null,
        int? maxStderrBytes = null,
        bool killOnOutputLimit = true);

    /// <summary>
    /// Stage a host-local source path into the remote host at
    /// <paramref name="remotePath"/>. <paramref name="hostPath"/> may be a
    /// regular file or a directory; directories are copied recursively and
    /// file modes preserved. The transport creates the target directory tree
    /// as needed.
    ///
    /// <para>Transport-level failures throw <see cref="RemoteSshTransportException"/>.</para>
    /// </summary>
    Task StageInAsync(string hostPath, string remotePath, CancellationToken ct);

    /// <summary>
    /// Sync changes from <paramref name="remotePath"/> back to
    /// <paramref name="hostPath"/>. Used for writable bind mounts (e.g. the
    /// bare-repo mount) so commits the in-VM agent pushed land back on the
    /// orchestrator host. Recursive, mode-preserving.
    ///
    /// <para>Transport-level failures throw <see cref="RemoteSshTransportException"/>.</para>
    /// </summary>
    Task StageOutAsync(string remotePath, string hostPath, CancellationToken ct);
}

public interface IRemotePortForwardTransport : IRemoteHostTransport
{
    IRemotePortForward StartLocalForward(
        string localHost,
        int localPort,
        string remoteHost,
        int remotePort);
}

public interface IRemotePortForward : IDisposable
{
}
