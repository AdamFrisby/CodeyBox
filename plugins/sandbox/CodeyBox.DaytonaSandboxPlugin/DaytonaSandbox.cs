using System.Buffers;
using System.Collections.Concurrent;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.DaytonaSandboxPlugin;

/// <summary>
/// Conventional in-sandbox paths shared with every provider. These literals
/// duplicate <c>SandboxConventions</c> in <c>CodeyBox.Sandbox</c> — the plugin
/// SDK surface is Core + PluginSdk only, so the convention constants cannot be
/// referenced here. The values are fixed pipeline contract, not options: a
/// plugin diverging from them breaks every agent runner.
/// </summary>
internal static class DaytonaSandboxConventions
{
    /// <summary>Tmpfs credential dir on local providers (sandbox-side path).</summary>
    public const string CredentialsDir = "/run/codeybox/creds";

    /// <summary>
    /// Directory the exec wrapper tees agent stdout/stderr into; the
    /// resume-adoption tail path is anchored under it.
    /// </summary>
    public const string AgentLogDir = "/work/.codeybox/agent-logs";
}

/// <summary>One mount planned for a Daytona sandbox.</summary>
internal sealed record DaytonaMountPlan(
    string SandboxPath,
    string? HostPath,
    bool ReadOnly,
    bool IsPersistentTmpfsDirectory);

/// <summary>
/// A live Daytona-hosted sandbox. Execution runs through the toolbox daemon:
/// a session is created per exec, the command runs under
/// <c>runAsync</c>, its stdin (env block + caller stdin, byte-counted through
/// <c>dd</c>) is delivered over the command-input channel, and output streams
/// back over the follow WebSocket demuxed by the daemon's 3-byte channel
/// prefixes. Cancellation deletes the session, which kills the command.
///
/// <para>Host mounts are staged into the sandbox at create (read-only sources
/// and writable seeds alike); writable host mounts are synced back to the host
/// by <see cref="ISandbox.SyncStateToHostAsync"/> and once more at disposal.</para>
/// </summary>
internal sealed class DaytonaSandbox :
    IPreemptibleSandbox,
    IPreserveOnDisposeSandbox,
    ISuspendableSandbox,
    IShutdownTeardownSandbox,
    IProviderOwnedSandbox,
    IRejectsFileBackedAgentCredentials,
    IActiveSandboxLease
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan DisposeGateWaitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExitMarkerPollDelay = TimeSpan.FromMilliseconds(250);
    private const int ExitMarkerPollAttempts = 8;

    // Slack on top of the configured output cap when bounding a single WS
    // message, covering the 3-byte channel prefix and framing overhead.
    private const long MessageSizeSlackBytes = 1024 * 1024;

    // Size of the rented WS receive buffer; messages larger than the
    // per-message ceiling are accumulated across frames in ReceiveMessageAsync.
    private const int WsReceiveBufferBytes = 64 * 1024;

    // Stderr preview cap for the file-sync helper execs (base64/tar): enough
    // to diagnose a failed helper without letting a noisy daemon blow the cap.
    private const int SyncHelperStderrCapBytes = 64 * 1024;

    // Exit code reported when the spec's wall-clock limit fires; matches the
    // conventional timeout(1) 124 so orchestrator tooling reads it as timeout.
    private const int WallClockTimeoutExitCode = 124;

    private readonly string _name;
    private readonly SandboxSpec _spec;
    private readonly Func<DaytonaSandboxOptions> _readOptions;
    private readonly DaytonaApiClient _api;
    private readonly Func<DaytonaEndpoint> _endpointFactory;
    private readonly Func<Uri> _toolboxBase;
    private readonly IDaytonaWebSocketFactory _webSocketFactory;
    private readonly HttpClient _http;
    private readonly IReadOnlyList<DaytonaMountPlan> _mounts;
    private readonly Action<DaytonaSandbox> _onDisposed;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, byte> _activeSessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _execGate = new(1, 1);
    private int _ownedByShutdownHandler;
    private int _preserveOnDispose;
    private int _suspended;
    private int _disposing;
    private int _disposed;
    private int _activeTrackingReleased;

    internal DaytonaSandbox(
        string name,
        SandboxSpec spec,
        Func<DaytonaSandboxOptions> readOptions,
        DaytonaApiClient api,
        Func<DaytonaEndpoint> endpointFactory,
        Func<Uri> toolboxBase,
        IDaytonaWebSocketFactory webSocketFactory,
        HttpClient http,
        IReadOnlyList<DaytonaMountPlan> mounts,
        Action<DaytonaSandbox> onDisposed,
        TimeProvider clock,
        ILogger log)
    {
        _name = name;
        _spec = spec;
        _readOptions = readOptions;
        _api = api;
        _endpointFactory = endpointFactory;
        _toolboxBase = toolboxBase;
        _webSocketFactory = webSocketFactory;
        _http = http;
        _mounts = mounts;
        _onDisposed = onDisposed;
        _clock = clock;
        _log = log;
    }

    public string Id => _name;

    public string ProviderId => DaytonaSandboxOptions.ProviderKind;

    public string FileBackedAgentCredentialsUnsupportedReason =>
        "Daytona hosted sandboxes have no tmpfs-backed credential path CodeyBox controls; " +
        "file-backed OAuth/subscription credentials would persist on the service-side sandbox " +
        "filesystem on infrastructure CodeyBox does not control. Use environment-delivered " +
        "credential variables instead.";

    public bool IsOwnedByShutdownHandler => Volatile.Read(ref _ownedByShutdownHandler) != 0;
    public void MarkOwnedByShutdownHandler() => Interlocked.Exchange(ref _ownedByShutdownHandler, 1);

    public bool IsSuspended => Volatile.Read(ref _suspended) != 0;
    public long? MemoryBytes => _spec.Limits.MemoryBytes;

    public void DisablePreserveOnDispose() => Interlocked.Exchange(ref _preserveOnDispose, 0);

    public void ReleaseActiveTracking() => Interlocked.Exchange(ref _activeTrackingReleased, 1);

    /// <summary>
    /// True while the provider still counts this handle as live. Deployment
    /// cleanup calls <see cref="ReleaseActiveTracking"/> after a failed remote
    /// delete so the leak reaper can retry against the inventory instead.
    /// </summary>
    internal bool IsTrackedActive => Volatile.Read(ref _activeTrackingReleased) == 0;

    /// <summary>Toolbox REST client for this sandbox's daemon (stateless wrapper over the shared HTTP client).</summary>
    internal DaytonaToolboxClient Toolbox => new(_http, _endpointFactory(), _toolboxBase());

    // ------------------------------------------------------------------
    // Exec
    // ------------------------------------------------------------------

    public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposing) != 0 || Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(DaytonaSandbox));
        await _execGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ExecInternalAsync(exec, includeSpecEnvironment: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _execGate.Release();
        }
    }

    /// <summary>Internal exec path used by provisioning/staging/sync callers that already hold the gate.</summary>
    internal async Task<SandboxExecResult> ExecInternalAsync(
        SandboxExec exec,
        bool includeSpecEnvironment,
        CancellationToken ct)
    {
        // _disposing is intentionally NOT checked here: disposal itself runs
        // the writable-mount sync through this path (under the held gate).
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(DaytonaSandbox));
        if (exec.Argv.Count == 0)
            throw new ArgumentException("Argv must be non-empty", nameof(exec));

        var opts = _readOptions();
        var effectiveEnvironment = BuildEffectiveEnvironment(exec, includeSpecEnvironment);
        if (WouldPersistCredentialFile(exec))
        {
            throw new NotSupportedException(
                "Daytona hosted sandboxes do not expose tmpfs credential storage CodeyBox controls; " +
                "refusing to write credential file material to the service-side filesystem. " +
                "Use non-file credential environment variables for daytona-backed sandboxes.");
        }

        // The sandbox's wall-clock limit bounds each exec the way the Incus
        // provider clamps ExecTimeout: a linked token fires the limit, kills
        // the session, and surfaces as a timed-out (not caller-cancelled)
        // exec result.
        var wallClockLimit = _spec.Limits.WallClock;
        using var wallClockCts = wallClockLimit is { } limit && limit > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (wallClockCts is not null && wallClockLimit is { } wc)
            wallClockCts.CancelAfter(wc);
        var execCt = wallClockCts?.Token ?? ct;

        var workingDirectory = string.IsNullOrWhiteSpace(exec.WorkingDirectory)
            ? _spec.WorkingDirectory
            : exec.WorkingDirectory!;
        var wire = BuildWireExec(exec, effectiveEnvironment, workingDirectory, opts);
        var toolbox = Toolbox;
        var sessionId = "cb-" + Guid.NewGuid().ToString("N");

        var sessionRegistered = false;
        try
        {
            await toolbox.CreateSessionAsync(sessionId, execCt).ConfigureAwait(false);
            _activeSessions[sessionId] = 0;
            sessionRegistered = true;
            var commandId = await toolbox.ExecuteSessionCommandAsync(sessionId, wire.Command, execCt).ConfigureAwait(false);
            await toolbox.SendSessionInputAsync(sessionId, commandId, wire.Input, execCt).ConfigureAwait(false);

            var stdout = new DaytonaOutputCollector(exec.MaxStdoutBytes ?? opts.MaxExecOutputBytes, exec.StdoutChunkCallback);
            var stderr = new DaytonaOutputCollector(exec.MaxStderrBytes ?? opts.MaxExecOutputBytes, exec.StderrChunkCallback);

            var stream = await StreamExecOutputAsync(
                toolbox, sessionId, commandId, exec, stdout, stderr, opts, execCt).ConfigureAwait(false);

            int? exitCode = stream.ExitCode;
            if (!stream.StreamFailed && exitCode is null)
            {
                // The follow stream closed before the exit marker was readable:
                // poll the command record briefly before declaring the exec
                // unobservable (ExecutionUnavailable).
                exitCode = await PollExitCodeAsync(
                    toolbox, sessionId, commandId, ExitMarkerPollAttempts, ExitMarkerPollDelay, execCt).ConfigureAwait(false);
            }

            stdout.Flush();
            stderr.Flush();
            return new SandboxExecResult(
                exitCode ?? 255,
                stdout.ToString(),
                stderr.ToString(),
                stdout.LimitExceeded,
                stderr.LimitExceeded,
                ExecutionUnavailable: exitCode is null);
        }
        catch (DaytonaApiException ex)
        {
            // Any service-side failure during dispatch or observation — the
            // command may still be running; kill the session (best-effort) and
            // report ExecutionUnavailable so the orchestrator classifies this
            // as infrastructure, never as a verdict on the work item's diff.
            await TryDeleteSessionAsync(toolbox, sessionId, CancellationToken.None).ConfigureAwait(false);
            return new SandboxExecResult(
                255,
                string.Empty,
                $"daytona exec transport failure: {ex.Message}",
                ExecutionUnavailable: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && wallClockCts is { IsCancellationRequested: true })
        {
            // The spec's wall-clock limit fired (the caller's token did not):
            // kill the remote command and report a deterministic timeout,
            // not a cancellation and not an infra outage.
            await TryDeleteSessionAsync(toolbox, sessionId, CancellationToken.None).ConfigureAwait(false);
            return new SandboxExecResult(
                WallClockTimeoutExitCode,
                string.Empty,
                $"daytona exec exceeded the sandbox wall-clock limit ({_spec.Limits.WallClock})",
                ExecutionUnavailable: false);
        }
        finally
        {
            // Exactly-once teardown: whichever path removes the session from
            // the tracking set first owns the delete (kill-on-limit, catch,
            // or normal completion).
            if (sessionRegistered && _activeSessions.TryRemove(sessionId, out _))
                await TryDeleteSessionAsync(toolbox, sessionId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Streams the command's output over the follow WebSocket until the daemon
    /// closes the stream (it closes after the command's exit marker exists).
    /// When the WS upgrade fails outright, falls back to polling the command
    /// record for an exit code then fetching the non-follow logs snapshot.
    /// </summary>
    private async Task<(int? ExitCode, bool StreamFailed)> StreamExecOutputAsync(
        DaytonaToolboxClient toolbox,
        string sessionId,
        string commandId,
        SandboxExec exec,
        DaytonaOutputCollector stdout,
        DaytonaOutputCollector stderr,
        DaytonaSandboxOptions opts,
        CancellationToken ct)
    {
        var uri = toolbox.BuildLogStreamUri(sessionId, commandId);
        await using var webSocket = _webSocketFactory.Create();
        try
        {
            await webSocket.ConnectAsync(uri, _endpointFactory().ApiKey, _endpointFactory().OrganizationId, ct).ConfigureAwait(false);
        }
        catch (DaytonaApiException)
        {
            // The snapshot body carries both streams in one payload, so its
            // pre-decode ceiling is the sum of the per-stream caps (the
            // collectors still enforce each stream's share on append).
            long snapshotCapBytes = (long)(exec.MaxStdoutBytes ?? opts.MaxExecOutputBytes)
                + (exec.MaxStderrBytes ?? opts.MaxExecOutputBytes)
                + MessageSizeSlackBytes;
            return await PollExecToCompletionAsync(
                toolbox, sessionId, commandId, stdout, stderr, opts, snapshotCapBytes, ct).ConfigureAwait(false);
        }

        long maxMessageBytes = Math.Max(exec.MaxStdoutBytes ?? 0, exec.MaxStderrBytes ?? 0);
        if (maxMessageBytes <= 0)
            maxMessageBytes = opts.MaxExecOutputBytes;
        maxMessageBytes += MessageSizeSlackBytes;

        var demuxer = new DaytonaLogDemuxer(
            chunk => { stdout.Append(chunk); return Task.CompletedTask; },
            chunk => { stderr.Append(chunk); return Task.CompletedTask; });


        var buffer = ArrayPool<byte>.Shared.Rent(WsReceiveBufferBytes);
        var killRequested = false;
        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                var message = await ReceiveMessageAsync(webSocket, buffer, maxMessageBytes, ct).ConfigureAwait(false);
                if (message is null)
                    break; // server closed the stream
                if (message.Length == 0)
                    continue;

                await demuxer.FeedAsync(message).ConfigureAwait(false);
                if (!killRequested && (stdout.LimitExceeded || stderr.LimitExceeded) && exec.KillOnOutputLimit)
                {
                    killRequested = true;
                    if (_activeSessions.TryRemove(sessionId, out _))
                        await TryDeleteSessionAsync(toolbox, sessionId, CancellationToken.None).ConfigureAwait(false);
                }
            }
            await demuxer.CompleteAsync().ConfigureAwait(false);
            return (null, StreamFailed: false);
        }
        catch (WebSocketException)
        {
            // Mid-stream transport failure: try to recover the exit code so a
            // finished command still reports its real result.
            var recovered = await PollExitCodeAsync(
                toolbox, sessionId, commandId, ExitMarkerPollAttempts, ExitMarkerPollDelay, CancellationToken.None)
                .ConfigureAwait(false);
            await demuxer.CompleteAsync().ConfigureAwait(false);
            return (recovered, StreamFailed: recovered is null);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            await webSocket.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Reads one complete WS message (bounded), or null on a close frame.</summary>
    private static async Task<byte[]?> ReceiveMessageAsync(
        IDaytonaWebSocket webSocket, byte[] buffer, long maxMessageBytes, CancellationToken ct)
    {
        using var payload = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await webSocket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            payload.Write(buffer, 0, result.Count);
            if (payload.Length > maxMessageBytes)
            {
                throw new DaytonaApiException(
                    DaytonaFailureKind.Unexpected,
                    "exec log stream",
                    $"log message exceeded the {maxMessageBytes}-byte per-message ceiling (output-cap defeat guard)");
            }
        }
        while (!result.EndOfMessage);
        return payload.ToArray();
    }

    /// <summary>Fallback for daemons without WS follow: poll the command record, then fetch the logs snapshot once.</summary>
    private async Task<(int? ExitCode, bool StreamFailed)> PollExecToCompletionAsync(
        DaytonaToolboxClient toolbox,
        string sessionId,
        string commandId,
        DaytonaOutputCollector stdout,
        DaytonaOutputCollector stderr,
        DaytonaSandboxOptions opts,
        long snapshotCapBytes,
        CancellationToken ct)
    {
        var exitCode = await PollExitCodeAsync(
            toolbox, sessionId, commandId, attempts: int.MaxValue,
            TimeSpan.FromMilliseconds(opts.PollIntervalMilliseconds), ct).ConfigureAwait(false);
        if (exitCode is null)
            return (null, StreamFailed: true);

        var logs = await toolbox.GetSessionCommandLogsAsync(sessionId, commandId, snapshotCapBytes, ct).ConfigureAwait(false);
        if (logs.Stdout is { } stdoutText)
            stdout.Append(stdoutText);
        if (logs.Stderr is { } stderrText)
            stderr.Append(stderrText);
        if (logs.Stdout is null && logs.Stderr is null && logs.Output is { } combined)
            stdout.Append(combined);
        return (exitCode, StreamFailed: false);
    }

    private async Task<int?> PollExitCodeAsync(
        DaytonaToolboxClient toolbox,
        string sessionId,
        string commandId,
        int attempts,
        TimeSpan delay,
        CancellationToken ct)
    {
        for (var i = 0; i < attempts; i++)
        {
            DaytonaSessionCommand? command;
            try
            {
                command = await toolbox.GetSessionCommandAsync(sessionId, commandId, ct).ConfigureAwait(false);
            }
            catch (DaytonaApiException)
            {
                command = null;
            }
            if (command?.ExitCode is { } exit)
                return exit;
            if (command is null)
                return null; // session/command record gone — nothing more to learn
            await Task.Delay(delay, _clock, ct).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>Best-effort session teardown — kills every command in the session.</summary>
    internal async Task TryDeleteSessionAsync(DaytonaToolboxClient toolbox, string sessionId, CancellationToken ct)
    {
        try
        {
            await toolbox.DeleteSessionAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Failed to delete daytona session {SessionId} in sandbox {Name}", sessionId, Id);
        }
    }

    public async Task KillActiveExecsAsync(CancellationToken ct = default)
    {
        var toolbox = Toolbox;
        foreach (var sessionId in _activeSessions.Keys.ToList())
            await TryDeleteSessionAsync(toolbox, sessionId, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Wire-exec construction
    // ------------------------------------------------------------------

    private sealed record DaytonaWireExec(string Command, string Input);

    private static DaytonaWireExec BuildWireExec(
        SandboxExec exec,
        IReadOnlyDictionary<string, string> effectiveEnvironment,
        string workingDirectory,
        DaytonaSandboxOptions opts)
    {
        var envBlock = BuildEnvironmentBlock(effectiveEnvironment, exec.EnvironmentVariablesToUnset);
        var envBytes = Utf8.GetByteCount(envBlock);
        var stdinBytes = exec.Stdin is null ? 0 : Utf8.GetByteCount(exec.Stdin);
        var input = envBlock + (exec.Stdin ?? string.Empty);
        if (Utf8.GetByteCount(input) > opts.MaxExecInputBytes)
        {
            throw new ArgumentException(
                $"Exec env+stdin payload exceeds the {opts.MaxExecInputBytes}-byte input bound.",
                nameof(exec));
        }

        var command = BuildBootstrapCommand(envBytes, stdinBytes, workingDirectory, exec.Argv);
        return new DaytonaWireExec(command, input);
    }

    /// <summary>
    /// The in-sandbox bootstrap. Reads the env block and stdin blob from the
    /// command's stdin pipe via byte-counted dd reads (the daemon's input
    /// channel never EOFs, so exact byte counts are required — a plain
    /// <c>cat</c>/<c>head</c> would either block forever or over-consume),
    /// exports the decoded environment, applies unsets, cd's to the working
    /// directory, then runs the real argv with stdin redirected from the
    /// captured file — giving the command true EOF semantics an open input
    /// pipe cannot express.
    /// </summary>
    private static string BuildBootstrapCommand(
        int envBytes, int stdinBytes, string workingDirectory, IReadOnlyList<string> argv)
    {
        var builder = new StringBuilder(512);
        builder.Append("set -u\n");
        builder.Append("__cb_dir=\"/tmp/.codeybox-exec-$$\"\n");
        builder.Append("mkdir -p \"$__cb_dir\"\n");
        builder.Append(CultureInfo.InvariantCulture, $"dd bs=1 count={envBytes} of=\"$__cb_dir/env\" 2>/dev/null || true\n");
        builder.Append(CultureInfo.InvariantCulture, $"dd bs=1 count={stdinBytes} of=\"$__cb_dir/stdin\" 2>/dev/null || true\n");
        builder.Append(
            """
            while IFS= read -r __cb_line; do
              case "$__cb_line" in
                \!*) unset "${__cb_line#!}" ;;
                *=*)
                  __cb_key="${__cb_line%%=*}"
                  __cb_b64="${__cb_line#*=}"
                  if __cb_val="$(printf '%s' "$__cb_b64" | base64 -d 2>/dev/null)"; then
                    export "$__cb_key=$__cb_val"
                  fi
                  ;;
              esac
            done < "$__cb_dir/env"
            unset __cb_line __cb_key __cb_b64 __cb_val
            """ + "\n");
        builder.Append(CultureInfo.InvariantCulture, $"mkdir -p {ShellSingleQuote(workingDirectory)}\n");
        builder.Append(CultureInfo.InvariantCulture, $"cd {ShellSingleQuote(workingDirectory)}\n");
        builder.Append("__cb_rc=0\n");
        builder.Append(JoinQuotedArgv(argv));
        builder.Append(" < \"$__cb_dir/stdin\" || __cb_rc=$?\n");
        builder.Append("cd /\nrm -rf \"$__cb_dir\"\nexit $__cb_rc\n");
        return builder.ToString();
    }

    /// <summary>Env block format: <c>!NAME</c> per removal then <c>NAME=base64(value)</c> per variable.</summary>
    private static string BuildEnvironmentBlock(
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<string> removals)
    {
        var builder = new StringBuilder();
        foreach (var name in removals)
            builder.Append('!').Append(name).Append('\n');
        foreach (var (key, value) in environment.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            SandboxEnvironmentVariableName.Validate(key, nameof(environment));
            builder
                .Append(key)
                .Append('=')
                .Append(Convert.ToBase64String(Utf8.GetBytes(value)))
                .Append('\n');
        }
        return builder.ToString();
    }

    private static string JoinQuotedArgv(IReadOnlyList<string> argv)
    {
        var builder = new StringBuilder();
        foreach (var arg in argv)
        {
            if (builder.Length > 0)
                builder.Append(' ');
            builder.Append(ShellSingleQuote(arg));
        }
        return builder.ToString();
    }

    internal static string ShellSingleQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private Dictionary<string, string> BuildEffectiveEnvironment(SandboxExec exec, bool includeSpecEnvironment)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
            ["HOME"] = "/root",
        };
        if (includeSpecEnvironment)
        {
            foreach (var (key, value) in _spec.Environment)
                env[key] = value;
        }
        if (exec.ExtraEnvironment is not null)
        {
            foreach (var (key, value) in exec.ExtraEnvironment)
                env[key] = value;
        }
        exec.ApplyEnvironmentRemovals(name => env.Remove(name));
        return env;
    }

    /// <summary>
    /// Defence-in-depth backstop that refuses execs writing credential material
    /// into the conventional credentials directory. The robust guard is the
    /// runner-level <see cref="IRejectsFileBackedAgentCredentials"/> contract;
    /// this catches only the narrow stdin-redirect shape.
    /// </summary>
    private static bool WouldPersistCredentialFile(SandboxExec exec)
    {
        if (exec.Stdin is not null &&
            exec.Argv.Any(arg =>
                arg.Equals(DaytonaSandboxConventions.CredentialsDir, StringComparison.Ordinal) ||
                arg.Contains(DaytonaSandboxConventions.CredentialsDir + "/", StringComparison.Ordinal)))
        {
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------------
    // Provisioning-time staging (called while the provider still owns create)
    // ------------------------------------------------------------------

    internal async Task RunSetupCommandsAsync(IReadOnlyList<string> setupCommands, CancellationToken ct)
    {
        foreach (var command in setupCommands)
        {
            if (string.IsNullOrWhiteSpace(command))
                continue;
            var opts = _readOptions();
            var result = await ExecInternalAsync(new SandboxExec
            {
                Argv = ["bash", "-lc", command],
                WorkingDirectory = "/",
                MaxStdoutBytes = opts.MaxExecOutputBytes,
                MaxStderrBytes = opts.MaxExecOutputBytes,
                KillOnOutputLimit = true,
            }, includeSpecEnvironment: false, ct).ConfigureAwait(false);
            if (result.ExecutionUnavailable)
            {
                throw new DaytonaApiException(
                    DaytonaFailureKind.Unreachable, "setup exec",
                    $"exec transport unavailable during setup in {Id}: {DaytonaTextUtil.Tail(result.Stderr)}");
            }
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"Daytona setup command failed in {Id} with exit {result.ExitCode}: {DaytonaTextUtil.Tail(result.Stderr)}");
            }
        }
    }

    internal async Task PrepareFilesystemAsync(CancellationToken ct)
    {
        foreach (var mount in _mounts)
        {
            if (mount.IsPersistentTmpfsDirectory)
            {
                await ExecInternalAsync(new SandboxExec
                {
                    Argv = ["mkdir", "-p", mount.SandboxPath],
                    WorkingDirectory = "/",
                }, includeSpecEnvironment: false, ct).ConfigureAwait(false);
                continue;
            }

            if (mount.HostPath is null)
                continue;
            if (Directory.Exists(mount.HostPath))
                await UploadDirectoryAsync(mount.HostPath, mount.SandboxPath, ct).ConfigureAwait(false);
            else
                await UploadFileAsync(mount.HostPath, mount.SandboxPath, ct).ConfigureAwait(false);
        }
    }

    private async Task UploadDirectoryAsync(string hostPath, string sandboxPath, CancellationToken ct)
    {
        var archive = CreateDirectoryArchiveBase64(hostPath);
        var result = await ExecInternalAsync(new SandboxExec
        {
            Argv =
            [
                "sh", "-c",
                // Decode to a temp file first: a failed base64 decode piped
                // straight into tar would be masked by tar's exit status and
                // could still leave a partial tree behind.
                "set -eu; rm -rf \"$1\"; mkdir -p \"$1\"; tmp=$(mktemp); trap 'rm -f \"$tmp\"' EXIT; base64 -d > \"$tmp\"; tar -xzf \"$tmp\" -C \"$1\"",
                "_", sandboxPath,
            ],
            WorkingDirectory = "/",
            Stdin = archive,
        }, includeSpecEnvironment: false, ct).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException($"Failed to stage host directory {hostPath} into daytona sandbox {Id}:{sandboxPath}: {DaytonaTextUtil.Tail(result.Stderr)}");
    }

    private async Task UploadFileAsync(string hostPath, string sandboxPath, CancellationToken ct)
    {
        var payload = Convert.ToBase64String(await File.ReadAllBytesAsync(hostPath, ct).ConfigureAwait(false));
        var result = await ExecInternalAsync(new SandboxExec
        {
            Argv =
            [
                "sh", "-c",
                "set -eu; mkdir -p \"$(dirname \"$1\")\"; base64 -d > \"$1\"",
                "_", sandboxPath,
            ],
            WorkingDirectory = "/",
            Stdin = payload,
        }, includeSpecEnvironment: false, ct).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException($"Failed to stage host file {hostPath} into daytona sandbox {Id}:{sandboxPath}: {DaytonaTextUtil.Tail(result.Stderr)}");
    }

    // ------------------------------------------------------------------
    // Writable-mount sync back to host
    // ------------------------------------------------------------------

    public async Task SyncStateToHostAsync(CancellationToken ct = default)
    {
        // The mount sync needs exclusive exec access; a caller that waits
        // forever behind a wedged exec would deadlock, so bound the gate wait
        // and fail loudly rather than silently skipping the flush.
        var acquired = await _execGate.WaitAsync(DisposeGateWaitTimeout, ct).ConfigureAwait(false);
        if (!acquired)
        {
            throw new InvalidOperationException(
                $"Timed out acquiring the exec gate to sync writable mounts for daytona sandbox {Id}.");
        }
        try
        {
            await SyncWritableMountsToHostAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _execGate.Release();
        }
    }

    private async Task SyncWritableMountsToHostAsync(CancellationToken ct)
    {
        foreach (var mount in _mounts)
        {
            if (mount.ReadOnly || mount.HostPath is null)
                continue;
            if (Directory.Exists(mount.HostPath))
                await SyncDirectoryToHostAsync(mount.SandboxPath, mount.HostPath, ct).ConfigureAwait(false);
            else if (File.Exists(mount.HostPath))
                await SyncFileToHostAsync(mount.SandboxPath, mount.HostPath, ct).ConfigureAwait(false);
        }
    }

    private async Task SyncDirectoryToHostAsync(string sandboxPath, string hostPath, CancellationToken ct)
    {
        var opts = _readOptions();
        var result = await ExecInternalAsync(new SandboxExec
        {
            Argv =
            [
                "sh", "-c",
                // Stage the tar to a temp file before base64 so a mid-archive
                // tar failure cannot be masked by the encoder's exit status.
                "set -eu; test -d \"$1\"; tmp=$(mktemp); trap 'rm -f \"$tmp\"' EXIT; tar -czf \"$tmp\" -C \"$1\" .; base64 -w0 \"$tmp\"",
                "_", sandboxPath,
            ],
            WorkingDirectory = "/",
            MaxStdoutBytes = opts.MaxSyncArchiveBase64Bytes,
            MaxStderrBytes = SyncHelperStderrCapBytes,
            KillOnOutputLimit = true,
        }, includeSpecEnvironment: false, ct).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException($"Failed to archive daytona directory {Id}:{sandboxPath}: {DaytonaTextUtil.Tail(result.Stderr)}");

        ReplaceHostDirectoryFromArchive(hostPath, result.Stdout.Trim(), opts);
    }

    private async Task SyncFileToHostAsync(string sandboxPath, string hostPath, CancellationToken ct)
    {
        var opts = _readOptions();
        var result = await ExecInternalAsync(new SandboxExec
        {
            Argv = ["base64", "-w0", sandboxPath],
            WorkingDirectory = "/",
            MaxStdoutBytes = opts.MaxFileSyncBase64Bytes,
            MaxStderrBytes = SyncHelperStderrCapBytes,
            KillOnOutputLimit = true,
        }, includeSpecEnvironment: false, ct).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException($"Failed to read daytona file {Id}:{sandboxPath}: {DaytonaTextUtil.Tail(result.Stderr)}");

        var payload = result.Stdout.Trim();
        if (payload.Length > opts.MaxFileSyncBase64Bytes)
            throw new InvalidOperationException($"Daytona file sync exceeded base64 limit for {Id}:{sandboxPath}.");
        var bytes = Convert.FromBase64String(payload);
        if (bytes.LongLength > opts.MaxFileSyncBytes)
            throw new InvalidOperationException($"Daytona file sync exceeded decoded-size limit for {Id}:{sandboxPath}.");
        var parent = Path.GetDirectoryName(hostPath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);
        File.WriteAllBytes(hostPath, bytes);
    }

    private static string CreateDirectoryArchiveBase64(string hostPath)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            TarFile.CreateFromDirectory(hostPath, gzip, includeBaseDirectory: false);
        return Convert.ToBase64String(output.ToArray());
    }

    /// <summary>
    /// Atomically replaces the host directory with the sandbox-supplied
    /// archive. The remote bytes are untrusted: entry count, expanded size,
    /// entry types (no links — a symlink entry could redirect a later write
    /// outside the target), and path containment are all validated BEFORE any
    /// byte lands on the host filesystem.
    /// </summary>
    private static void ReplaceHostDirectoryFromArchive(
        string hostPath, string archiveBase64, DaytonaSandboxOptions opts)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(hostPath))
            ?? throw new InvalidOperationException($"Unable to determine parent directory for {hostPath}");
        Directory.CreateDirectory(parent);

        var tempPath = Path.Combine(parent, $".codeybox-daytona-sync-{Guid.NewGuid():N}");
        var backupPath = Path.Combine(parent, $".codeybox-daytona-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempPath);
        var movedExisting = false;
        try
        {
            if (archiveBase64.Length > opts.MaxSyncArchiveBase64Bytes)
                throw new InvalidOperationException($"Daytona sync archive for {hostPath} exceeded base64 limit.");
            var bytes = Convert.FromBase64String(archiveBase64);
            if (bytes.LongLength > opts.MaxSyncArchiveBytes)
                throw new InvalidOperationException($"Daytona sync archive for {hostPath} exceeded compressed-size limit.");
            ValidateTarGzipArchive(bytes, opts, hostPath);
            using (var input = new MemoryStream(bytes))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            {
                TarFile.ExtractToDirectory(gzip, tempPath, overwriteFiles: true);
            }

            var hadExisting = Directory.Exists(hostPath);
            if (hadExisting)
            {
                Directory.Move(hostPath, backupPath);
                movedExisting = true;
            }
            Directory.Move(tempPath, hostPath);
            if (hadExisting)
                Directory.Delete(backupPath, recursive: true);
        }
        catch
        {
            if (movedExisting && Directory.Exists(backupPath))
            {
                if (Directory.Exists(hostPath))
                    Directory.Delete(hostPath, recursive: true);
                Directory.Move(backupPath, hostPath);
            }
            throw;
        }
        finally
        {
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, recursive: true);
            if (Directory.Exists(backupPath) && !Directory.Exists(hostPath))
                Directory.Move(backupPath, hostPath);
        }
    }

    private static void ValidateTarGzipArchive(byte[] bytes, DaytonaSandboxOptions opts, string hostPath)
    {
        var entries = 0;
        long expandedBytes = 0;
        var root = Path.GetFullPath(hostPath);
        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
        {
            entries++;
            if (entries > opts.MaxSyncArchiveEntries)
                throw new InvalidOperationException($"Daytona sync archive for {hostPath} exceeded file-count limit.");

            var destination = Path.GetFullPath(Path.Combine(root, entry.Name));
            if (!destination.Equals(root, StringComparison.Ordinal) &&
                !destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Daytona sync archive for {hostPath} contains an unsafe path.");
            }

            if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                throw new InvalidOperationException($"Daytona sync archive for {hostPath} contains a link entry.");
            if (entry.EntryType is not TarEntryType.Directory and not TarEntryType.RegularFile)
                throw new InvalidOperationException($"Daytona sync archive for {hostPath} contains unsupported entry type {entry.EntryType}.");

            expandedBytes += entry.Length;
            if (expandedBytes > opts.MaxSyncArchiveExpandedBytes)
                throw new InvalidOperationException($"Daytona sync archive for {hostPath} exceeded expanded-size limit.");
        }
    }

    // ------------------------------------------------------------------
    // Teardown / preserve / suspend
    // ------------------------------------------------------------------

    /// <summary>Graceful shutdown stop: preserve the sandbox so a later process can resume it.</summary>
    public async Task StopAndPreserveAsync(CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _preserveOnDispose, 1);
        await KillActiveExecsAsync(CancellationToken.None).ConfigureAwait(false);
        await StopIfRunningAsync(ct).ConfigureAwait(false);
    }

    private async Task StopIfRunningAsync(CancellationToken ct)
    {
        try
        {
            await _api.StopSandboxAsync(_endpointFactory(), _name, force: false, ct).ConfigureAwait(false);
        }
        catch (DaytonaApiException ex) when (ex.Kind is DaytonaFailureKind.Conflict or DaytonaFailureKind.NotFound)
        {
            // Already stopped / paused / archived / gone — all preserved states.
        }
        var sandbox = await _api.GetSandboxAsync(_endpointFactory(), _name, ct).ConfigureAwait(false);
        if (sandbox is null)
            return;
        if (string.Equals(sandbox.State, "stopped", StringComparison.OrdinalIgnoreCase))
            return;
        if (sandbox.State is "stopping" or "starting")
            await WaitForStateAsync(_endpointFactory(), _name, "stopped", ct).ConfigureAwait(false);
        // paused / archived also satisfy "preserve": no live execution surface remains.
    }

    /// <summary>
    /// Retains the sandbox for infrastructure-failure recovery and returns a
    /// capability lease the orchestrator can later pass back via
    /// <see cref="SandboxSpec.RecoveryLease"/>. The token's SHA-256 lands on
    /// the sandbox's service-side labels — the token itself never does — so an
    /// adopt request must prove it holds the issued token.
    /// </summary>
    public async Task<SandboxRecoveryLease?> RetainForInfrastructureRecoveryAsync(CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _preserveOnDispose, 1);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var hash = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(token)));
        try
        {
            await StopAndPreserveAsync(ct).ConfigureAwait(false);
            await MergeLabelsAsync(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [DaytonaSandboxOptions.RecoveryTokenHashLabelKey] = hash,
                }, ct).ConfigureAwait(false);
            return new SandboxRecoveryLease(DaytonaSandboxOptions.ProviderKind, _name, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Failed to retain daytona sandbox {Name} for infrastructure recovery", _name);
            return null;
        }
    }

    internal async Task MergeLabelsAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var sandbox = await _api.GetSandboxAsync(_endpointFactory(), _name, ct).ConfigureAwait(false);
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        if (sandbox?.Labels is not null)
        {
            foreach (var (key, value) in sandbox.Labels)
                merged[key] = value;
        }
        foreach (var (key, value) in labels)
            merged[key] = value;
        await _api.SetLabelsAsync(_endpointFactory(), _name, merged, ct).ConfigureAwait(false);
    }

    /// <summary>Freeze the sandbox (Daytona pause keeps the sandbox, including its RAM, service-side).</summary>
    public async Task SuspendAsync(CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _preserveOnDispose, 1);
        await KillActiveExecsAsync(CancellationToken.None).ConfigureAwait(false);
        await _api.PauseSandboxAsync(_endpointFactory(), _name, ct).ConfigureAwait(false);
        await WaitForStateAsync(_endpointFactory(), _name, "paused", ct).ConfigureAwait(false);
        Interlocked.Exchange(ref _suspended, 1);
    }

    internal async Task WaitForStateAsync(DaytonaEndpoint endpoint, string name, string targetState, CancellationToken ct)
    {
        var opts = _readOptions();
        var deadline = _clock.GetUtcNow() + TimeSpan.FromSeconds(opts.TransitionTimeoutSeconds);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var sandbox = await _api.GetSandboxAsync(endpoint, name, ct).ConfigureAwait(false)
                ?? throw new DaytonaApiException(
                    DaytonaFailureKind.NotFound, "wait sandbox state", $"sandbox {name} disappeared");
            var state = sandbox.State ?? string.Empty;
            if (string.Equals(state, targetState, StringComparison.OrdinalIgnoreCase))
                return;
            if (string.Equals(state, "error", StringComparison.OrdinalIgnoreCase))
            {
                throw new DaytonaApiException(
                    DaytonaFailureKind.ServerError, "wait sandbox state",
                    $"sandbox {name} entered error state: {sandbox.ErrorReason ?? "no reason reported"}");
            }
            if (_clock.GetUtcNow() >= deadline)
            {
                throw new DaytonaApiException(
                    DaytonaFailureKind.ServerError, "wait sandbox state",
                    $"sandbox {name} did not reach '{targetState}' within {opts.TransitionTimeoutSeconds}s (state={state})");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(opts.PollIntervalMilliseconds), _clock, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        if (Interlocked.CompareExchange(ref _disposing, 1, 0) != 0)
            return;
        if (Volatile.Read(ref _disposed) != 0)
        {
            Volatile.Write(ref _disposing, 0);
            return;
        }
        _onDisposed(this);

        await KillActiveExecsAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var acquiredGate = await _execGate.WaitAsync(DisposeGateWaitTimeout, CancellationToken.None).ConfigureAwait(false);
            if (acquiredGate)
            {
                try
                {
                    await SyncWritableMountsToHostAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Final daytona mount sync failed for {Name}; proceeding with teardown", Id);
                }
                finally
                {
                    _execGate.Release();
                    // Safe now: the gate is held exclusively and _disposing
                    // blocks any new exec from acquiring it.
                    _execGate.Dispose();
                }
            }
            else
            {
                _log.LogWarning(
                    "Could not acquire exec gate within {Timeout} during teardown of {Name}; " +
                    "skipping final mount sync and proceeding to sandbox deletion.",
                    DisposeGateWaitTimeout, Id);
            }

            if (Volatile.Read(ref _preserveOnDispose) != 0)
            {
                _log.LogInformation("Preserved daytona sandbox {Name} (stop/pause on dispose)", Id);
            }
            else
            {
                await _api.DeleteSandboxAsync(_endpointFactory(), Id, CancellationToken.None).ConfigureAwait(false);
                _log.LogInformation("Deleted daytona sandbox {Name}", Id);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Failed to delete daytona sandbox {Name}", Id);
        }
        finally
        {
            Volatile.Write(ref _disposed, 1);
            Volatile.Write(ref _disposing, 0);
        }
    }
}

/// <summary>
/// Bounded stdout/stderr accumulator with a live UTF-8-safe chunk callback.
/// A single shared decoder per collector retains split multi-byte sequences
/// across appends, so the callback only ever emits complete characters
/// (chunks feed the orchestrator's structured stream parsers — a mid-sequence
/// decode would emit U+FFFD and corrupt framed output).
/// </summary>
internal sealed class DaytonaOutputCollector
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly int? _limit;
    private readonly Action<string>? _callback;
    private readonly MemoryStream _buffer = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();

    public DaytonaOutputCollector(int? limit, Action<string>? callback)
    {
        _limit = limit;
        _callback = callback;
    }

    public bool LimitExceeded { get; private set; }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
            return;
        var allowed = bytes.Length;
        if (_limit.HasValue)
        {
            var remaining = _limit.Value - _buffer.Length;
            if (remaining <= 0)
            {
                LimitExceeded = true;
                return;
            }
            if (allowed > remaining)
            {
                allowed = (int)remaining;
                LimitExceeded = true;
            }
        }
        _buffer.Write(bytes[..allowed]);
        if (_callback is null)
            return;

        var chars = new char[Encoding.UTF8.GetMaxCharCount(allowed)];
        var used = _decoder.GetChars(bytes[..allowed], chars, flush: false);
        if (used > 0)
            _callback(new string(chars, 0, used));
    }

    public void Append(string text) => Append(Utf8.GetBytes(text));

    /// <summary>Emits any decoder-retained partial tail once the stream ends.</summary>
    public void Flush()
    {
        if (_callback is null)
            return;
        var chars = new char[8];
        var used = _decoder.GetChars(ReadOnlySpan<byte>.Empty, chars, flush: true);
        if (used > 0)
            _callback(new string(chars, 0, used));
    }

    public override string ToString() => Utf8.GetString(_buffer.ToArray());
}
