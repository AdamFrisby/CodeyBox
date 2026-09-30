using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.BoxLiteSandboxPlugin;

/// <summary>
/// Conventional in-guest paths shared with the pipeline. These literals
/// duplicate <c>SandboxConventions</c> in <c>CodeyBox.Sandbox</c> — the plugin
/// SDK surface is Core + PluginSdk only, so the convention constants cannot be
/// referenced here. The values are fixed pipeline contract, not options: a
/// plugin diverging from them breaks every agent runner.
/// </summary>
internal static class BoxLiteSandboxConventions
{
    /// <summary>Directory the exec wrapper tees agent stdout/stderr into; the resume-adoption tail path is anchored under it.</summary>
    public const string AgentLogDir = "/work/.codeybox/agent-logs";
}

/// <summary>One mount planned for a BoxLite VM.</summary>
internal sealed record BoxLiteMountPlan(
    string SandboxPath,
    string? HostPath,
    bool ReadOnly,
    bool IsGuestDir);

/// <summary>
/// A live BoxLite microVM. Execution runs through the daemon's exec API: an
/// exec is started with an argv array plus base64 environment/stdin, then
/// polled until it stops reporting <c>running</c>; incremental stdout/stderr
/// deltas are delivered to the caller's chunk callbacks as they arrive.
/// Cancellation and output-limit trips kill the guest process via DELETE.
/// Host mounts are staged into the VM at create and writable mounts are
/// synced back to the host by <see cref="ISandbox.SyncStateToHostAsync"/> and
/// once more at disposal, validated and applied atomically.
/// </summary>
internal sealed class BoxLiteSandbox :
    IPreemptibleSandbox,
    IPreserveOnDisposeSandbox,
    ISuspendableSandbox,
    IShutdownTeardownSandbox,
    IProviderOwnedSandbox,
    IActiveSandboxLease
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan DisposeGateWaitTimeout = TimeSpan.FromSeconds(30);

    // Stderr preview cap for the archive helper execs: enough to diagnose a
    // failed helper without letting a noisy daemon blow the cap.
    private const int SyncHelperStderrCapBytes = 64 * 1024;

    // Bounds on exec argv: entries are individually capped so one huge
    // argument cannot blow the daemon request, and the count is capped so
    // request construction stays O(cap).
    private const int MaxArgvEntryBytes = 32 * 1024;
    private const int MaxArgvEntries = 512;

    // Copy buffer for host-side tar extraction.
    private const int CopyBufferBytes = 64 * 1024;

    // Interval at which a second concurrent disposer polls for the first
    // disposer to finish.
    private static readonly TimeSpan DisposeGatePollInterval = TimeSpan.FromMilliseconds(10);

    // Exit code reported when the spec's wall-clock limit fires; matches the
    // conventional timeout(1) 124 so orchestrator tooling reads it as timeout.
    private const int WallClockTimeoutExitCode = 124;

    // Exit code reported when the daemon is unreachable or the exec is
    // unobservable (transport failure, shape drift). It is not a verdict on
    // the work item diff: results carrying it set ExecutionUnavailable so the
    // orchestrator classifies them as infra.
    private const int ExecutionUnavailableExitCode = 255;

    private readonly string _name;
    private readonly SandboxSpec _spec;
    private readonly Func<BoxLiteSandboxOptions> _readOptions;
    private readonly BoxLiteApiClient _api;
    private readonly Func<BoxLiteEndpoint> _endpointFactory;
    private readonly IReadOnlyList<BoxLiteMountPlan> _mounts;
    private readonly Action<BoxLiteSandbox> _onDisposed;
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

    internal BoxLiteSandbox(
        string name,
        SandboxSpec spec,
        Func<BoxLiteSandboxOptions> readOptions,
        BoxLiteApiClient api,
        Func<BoxLiteEndpoint> endpointFactory,
        IReadOnlyList<BoxLiteMountPlan> mounts,
        Action<BoxLiteSandbox> onDisposed,
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

    public string ProviderId => BoxLiteSandboxOptions.ProviderKind;

    public bool IsOwnedByShutdownHandler => Volatile.Read(ref _ownedByShutdownHandler) == 1;

    public void MarkOwnedByShutdownHandler() => Interlocked.Exchange(ref _ownedByShutdownHandler, 1);

    public bool IsSuspended => Volatile.Read(ref _suspended) == 1;

    public long? MemoryBytes => _spec.Limits.MemoryBytes;

    /// <summary>
    /// Releases provider-side active tracking without claiming the sandbox
    /// was successfully disposed, so leak reapers can retry in-process (see
    /// <see cref="IActiveSandboxLease"/>). Invokes the provider removal
    /// callback exactly once; the callback is idempotent with
    /// <see cref="DisposeAsync"/>'s, so a later dispose still completes cleanly.
    /// </summary>
    public void ReleaseActiveTracking()
    {
        if (Interlocked.Exchange(ref _activeTrackingReleased, 1) != 0)
            return;
        try
        {
            _onDisposed(this);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "BoxLite VM {Name} release-tracking callback failed", _name);
        }
    }

    /// <summary>True while the provider still counts this handle as live.</summary>
    internal bool IsTrackedActive => Volatile.Read(ref _activeTrackingReleased) == 0;

    public void DisablePreserveOnDispose() => Interlocked.Exchange(ref _preserveOnDispose, 0);

    // ------------------------------------------------------------------
    // Exec
    // ------------------------------------------------------------------

    public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exec);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (exec.Argv is null || exec.Argv.Count == 0)
            throw new ArgumentException("Exec requires a non-empty argv.", nameof(exec));
        foreach (var arg in exec.Argv)
        {
            if (arg is null)
                throw new ArgumentException("Exec argv must not contain null entries.", nameof(exec));
            if (arg.Length > MaxArgvEntryBytes)
                throw new ArgumentException("Exec argv entries are bounded to 32 KiB each.", nameof(exec));
        }
        if (exec.Argv.Count > MaxArgvEntries)
            throw new ArgumentException("Exec argv is bounded to 512 entries.", nameof(exec));

        var opts = _readOptions();
        var stdoutCap = exec.MaxStdoutBytes ?? opts.MaxExecOutputBytes;
        var stderrCap = exec.MaxStderrBytes ?? opts.MaxExecOutputBytes;
        if (stdoutCap <= 0 || stderrCap <= 0)
            throw new ArgumentOutOfRangeException(nameof(exec), "Exec output caps must be positive.");

        var cwd = string.IsNullOrWhiteSpace(exec.WorkingDirectory) ? _spec.WorkingDirectory : exec.WorkingDirectory;
        if (!IsValidAbsolutePath(cwd))
            throw new ArgumentException($"Exec working directory '{cwd}' is not an absolute contained path.", nameof(exec));

        var env = MergeEnvironment(exec);
        var envBase64 = env.ToDictionary(
            static kvp => kvp.Key,
            static kvp => Convert.ToBase64String(Utf8.GetBytes(kvp.Value)),
            StringComparer.Ordinal);
        var stdinBase64 = exec.Stdin is null ? null : Convert.ToBase64String(Utf8.GetBytes(exec.Stdin));
        var inputBytes = (stdinBase64?.Length ?? 0) + envBase64.Sum(static kvp => kvp.Key.Length + kvp.Value.Length);
        if (inputBytes > opts.MaxExecInputBytes)
            throw new ArgumentException($"Exec environment/stdin payload exceeds the {opts.MaxExecInputBytes}-byte bound.", nameof(exec));

        var wallClock = _spec.Limits.WallClock;
        var deadline = wallClock is { } wc && wc > TimeSpan.Zero
            ? _clock.GetUtcNow() + wc
            : (DateTimeOffset?)null;
        var timeoutSeconds = wallClock is { } wc2 && wc2 > TimeSpan.Zero
            ? (long)Math.Ceiling(wc2.TotalSeconds)
            : (long?)null;

        await _execGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            var endpoint = _endpointFactory();
            string execId;
            try
            {
                var created = await _api.StartExecAsync(endpoint, _name, new BoxLiteStartExecRequest(
                    exec.Argv, cwd, envBase64, stdinBase64, timeoutSeconds), ct).ConfigureAwait(false);
                execId = string.IsNullOrWhiteSpace(created.ExecId)
                    ? throw new BoxLiteApiException(BoxLiteFailureKind.Unexpected, "start exec", "empty exec id")
                    : created.ExecId;
            }
            catch (BoxLiteApiException ex)
            {
                return Unavailable($"start exec refused: {ex.Message}");
            }
            _activeExecs[execId] = 0;
            try
            {
                return await PollExecAsync(
                    endpoint, execId, exec, stdoutCap, stderrCap, deadline, opts, ct).ConfigureAwait(false);
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
        BoxLiteEndpoint endpoint,
        string execId,
        SandboxExec exec,
        int stdoutCap,
        int stderrCap,
        DateTimeOffset? deadline,
        BoxLiteSandboxOptions opts,
        CancellationToken ct)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutBytes = 0;
        var stderrBytes = 0;
        var stdoutLimitExceeded = false;
        var stderrLimitExceeded = false;
        var stdoutDelivered = 0;
        var stderrDelivered = 0;
        var pollDelay = TimeSpan.FromMilliseconds(Math.Clamp(opts.PollIntervalMilliseconds, 100, 60_000));

        // Slack on top of the configured output cap when bounding one poll's
        // snapshot, covering multi-byte decoding growth. A snapshot past
        // cap + slack is unbounded guest output: kill and classify as infra
        // rather than buffering it into the host. The transport ceiling
        // covers the same snapshots on the wire, so it is derived from the
        // same caps — the wire can never reject what the decoder accepts.
        var pollResponseCap = BoxLiteApiClient.BoundExecPollResponseBytes(stdoutCap, stderrCap);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (deadline is { } dl && _clock.GetUtcNow() >= dl)
            {
                await TryKillExecAsync(endpoint, execId).ConfigureAwait(false);
                return new SandboxExecResult(
                    WallClockTimeoutExitCode, stdout.ToString(), stderr.ToString(),
                    stdoutLimitExceeded, stderrLimitExceeded);
            }

            BoxLiteExecStatusDto status;
            try
            {
                status = await _api.GetExecAsync(endpoint, _name, execId, ct, pollResponseCap).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await TryKillExecAsync(endpoint, execId).ConfigureAwait(false);
                throw;
            }
            catch (BoxLiteApiException ex)
            {
                await TryKillExecAsync(endpoint, execId).ConfigureAwait(false);
                return Unavailable($"poll exec failed: {ex.Message}");
            }

            var stdoutText = DecodeBounded(status.StdoutBase64, stdoutCap + BoxLiteApiClient.ExecSnapshotSlackBytes, out var stdoutHuge, _log);
            var stderrText = DecodeBounded(status.StderrBase64, stderrCap + BoxLiteApiClient.ExecSnapshotSlackBytes, out var stderrHuge, _log);
            if (stdoutHuge || stderrHuge || stdoutText.Length < stdoutDelivered || stderrText.Length < stderrDelivered)
            {
                await TryKillExecAsync(endpoint, execId).ConfigureAwait(false);
                return Unavailable("exec stream regressed or exceeded the snapshot ceiling");
            }
            // Once a stream is over cap with KillOnOutputLimit off, the buffered
            // result freezes: later deltas still reach the live chunk callback
            // and still advance the delivered cursor (so a regressed stream is
            // still detected), but they are not accumulated — otherwise a
            // chatty guest grows host memory without bound. The frozen prefix
            // is byte-identical to what an unbounded buffer would truncate to.
            AppendDelta(stdout, stdoutText, ref stdoutDelivered, exec.StdoutChunkCallback, stdoutLimitExceeded && !exec.KillOnOutputLimit, _log);
            AppendDelta(stderr, stderrText, ref stderrDelivered, exec.StderrChunkCallback, stderrLimitExceeded && !exec.KillOnOutputLimit, _log);
            stdoutBytes = Utf8.GetByteCount(stdoutText);
            stderrBytes = Utf8.GetByteCount(stderrText);

            if (stdoutBytes > stdoutCap)
            {
                stdoutLimitExceeded = true;
                if (exec.KillOnOutputLimit)
                {
                    await TryKillExecAsync(endpoint, execId).ConfigureAwait(false);
                    return new SandboxExecResult(status.ExitCode ?? ExecutionUnavailableExitCode,
                        TruncateToBytes(stdout.ToString(), stdoutCap), stderr.ToString(), true, stderrLimitExceeded);
                }
            }
            if (stderrBytes > stderrCap)
            {
                stderrLimitExceeded = true;
                if (exec.KillOnOutputLimit)
                {
                    await TryKillExecAsync(endpoint, execId).ConfigureAwait(false);
                    return new SandboxExecResult(status.ExitCode ?? ExecutionUnavailableExitCode,
                        stdout.ToString(), TruncateToBytes(stderr.ToString(), stderrCap), stdoutLimitExceeded, true);
                }
            }

            if (!status.Running)
            {
                if (status.ExitCode is null)
                {
                    // Shape drift: a completed exec without an exit code is
                    // unobservable — infra, never a diff verdict.
                    await TryKillExecAsync(endpoint, execId).ConfigureAwait(false);
                    return Unavailable("exec completed without an exit code");
                }
                return new SandboxExecResult(
                    status.ExitCode.Value,
                    stdoutLimitExceeded ? TruncateToBytes(stdout.ToString(), stdoutCap) : stdout.ToString(),
                    stderrLimitExceeded ? TruncateToBytes(stderr.ToString(), stderrCap) : stderr.ToString(),
                    stdoutLimitExceeded, stderrLimitExceeded);
            }

            try
            {
                await Task.Delay(pollDelay, _clock, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await TryKillExecAsync(endpoint, execId).ConfigureAwait(false);
                throw;
            }
        }
    }

    private static void AppendDelta(StringBuilder sink, string full, ref int delivered, Action<string>? callback, bool freeze, ILogger log)
    {
        if (full.Length <= delivered)
            return;
        var delta = full[delivered..];
        delivered = full.Length;
        if (!freeze)
            sink.Append(delta);
        try
        {
            callback?.Invoke(delta);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "BoxLite exec chunk callback failed; continuing with buffered output");
        }
    }

    /// <summary>
    /// Decodes one guest-controlled exec snapshot with the ceiling enforced
    /// BEFORE the host buffers it: a base64-length guard runs before
    /// <c>Convert.FromBase64String</c> and a decoded-byte guard runs before
    /// UTF-8 decoding, so each poll tick allocates at most O(ceiling) no
    /// matter how much the guest has written. A snapshot past the ceiling
    /// reports <c>overCap</c> and decodes to empty — the caller kills the
    /// guest process and classifies the exec as infrastructure, never a diff
    /// verdict.
    /// </summary>
    private static string DecodeBounded(string? base64, int maxDecodedBytes, out bool overCap, ILogger log)
    {
        overCap = false;
        if (string.IsNullOrEmpty(base64))
            return string.Empty;
        // Base64 carries 3 raw bytes per 4 chars; the 4-char tolerance covers
        // padding, so this only fires when the payload must decode past the
        // ceiling even in the most padding-favourable reading.
        if ((base64.Length - 4) * 3L / 4 > maxDecodedBytes)
        {
            overCap = true;
            return string.Empty;
        }
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            log.LogDebug("BoxLite daemon returned malformed base64; treating as empty output");
            return string.Empty;
        }
        if (bytes.Length > maxDecodedBytes)
        {
            overCap = true;
            return string.Empty;
        }
        var text = Utf8.GetString(bytes);
        if (text.Length > maxDecodedBytes)
        {
            // Unreachable with standard UTF-8 (every character decodes from
            // at least one byte, so the byte guard above already fired);
            // retained as the ceiling if the decoder ever changes.
            overCap = true;
            return text[..maxDecodedBytes];
        }
        return text;
    }

    /// <summary>
    /// Streams a host file up to <paramref name="maxBytes"/> plus one probe
    /// byte, so the read never buffers past the ceiling even if the file
    /// grows after the pre-read length guard. Callers treat a past-ceiling
    /// result as over the file bound.
    /// </summary>
    private static async Task<byte[]> ReadHostFileBoundedAsync(string path, long maxBytes, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes, useAsync: true);
        using var buffer = new MemoryStream();
        var chunk = new byte[CopyBufferBytes];
        int read;
        while ((read = await input.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            var room = maxBytes + 1 - buffer.Length;
            if (read >= room)
            {
                buffer.Write(chunk, 0, (int)room);
                break;
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static string TruncateToBytes(string text, int maxBytes)
    {
        if (Utf8.GetByteCount(text) <= maxBytes)
            return text;
        var bytes = Utf8.GetBytes(text);
        var cut = maxBytes;
        while (cut > 0 && (bytes[cut] & 0xC0) == 0x80)
            cut--;
        return Utf8.GetString(bytes, 0, cut);
    }

    private IReadOnlyDictionary<string, string> MergeEnvironment(SandboxExec exec)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kvp in _spec.Environment)
            merged[kvp.Key] = kvp.Value;
        if (exec.ExtraEnvironment is not null)
        {
            foreach (var kvp in exec.ExtraEnvironment)
                merged[kvp.Key] = kvp.Value;
        }
        exec.ApplyEnvironmentRemovals(name => merged.Remove(name));
        return merged;
    }

    private static SandboxExecResult Unavailable(string detail) =>
        new(ExecutionUnavailableExitCode, string.Empty, detail, ExecutionUnavailable: true);

    private async Task TryKillExecAsync(BoxLiteEndpoint endpoint, string execId)
    {
        try
        {
            await _api.KillExecAsync(endpoint, _name, execId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Kill of BoxLite exec {ExecId} on {Name} failed", execId, _name);
        }
    }

    public async Task KillActiveExecsAsync(CancellationToken ct = default)
    {
        var endpoint = _endpointFactory();
        foreach (var execId in _activeExecs.Keys)
        {
            try
            {
                await _api.KillExecAsync(endpoint, _name, execId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Kill of BoxLite exec {ExecId} on {Name} failed", execId, _name);
            }
        }
    }

    // ------------------------------------------------------------------
    // File in/out
    // ------------------------------------------------------------------

    /// <summary>Reads a single guest file. Returns null when absent.</summary>
    internal async Task<string?> ReadGuestFileAsync(string guestPath, CancellationToken ct)
    {
        if (!IsValidAbsolutePath(guestPath))
            throw new ArgumentException($"Guest path '{guestPath}' is not an absolute contained path.", nameof(guestPath));
        var opts = _readOptions();
        var dto = await _api.ReadFileAsync(
            _endpointFactory(), _name, guestPath, ct,
            BoxLiteApiClient.BoundPayloadResponseBytes(opts.MaxFileSyncBase64Bytes)).ConfigureAwait(false);
        if (dto?.ContentBase64 is null)
            return null;
        if (dto.ContentBase64.Length > opts.MaxFileSyncBase64Bytes)
            throw new InvalidOperationException($"Guest file '{guestPath}' exceeds the read bound.");
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(dto.ContentBase64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"Guest file '{guestPath}' is not valid base64.", ex);
        }
        if (bytes.Length > opts.MaxFileSyncBytes)
            throw new InvalidOperationException($"Guest file '{guestPath}' exceeds the read bound.");
        return Utf8.GetString(bytes);
    }

    /// <summary>Writes a single guest file, creating parent directories.</summary>
    internal async Task WriteGuestFileAsync(string guestPath, string content, string? mode, CancellationToken ct)
    {
        if (!IsValidAbsolutePath(guestPath))
            throw new ArgumentException($"Guest path '{guestPath}' is not an absolute contained path.", nameof(guestPath));
        ArgumentNullException.ThrowIfNull(content);
        var opts = _readOptions();
        var bytes = Utf8.GetBytes(content);
        if (bytes.Length > opts.MaxFileSyncBytes)
            throw new ArgumentException($"Content exceeds the {opts.MaxFileSyncBytes}-byte write bound.", nameof(content));
        var payload = Convert.ToBase64String(bytes);
        if (payload.Length > opts.MaxFileSyncBase64Bytes)
            throw new ArgumentException("Encoded content exceeds the write bound.", nameof(content));
        await _api.WriteFileAsync(_endpointFactory(), _name, guestPath, payload, mode, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Mounts
    // ------------------------------------------------------------------

    /// <summary>Stages read-only sources, writable seeds, and guest dirs into the VM.</summary>
    internal async Task PrepareFilesystemAsync(CancellationToken ct)
    {
        foreach (var mount in _mounts)
        {
            ct.ThrowIfCancellationRequested();
            if (mount.IsGuestDir)
            {
                var mkdir = await ExecAsync(new SandboxExec
                {
                    Argv = ["mkdir", "-p", "--", mount.SandboxPath],
                    MaxStdoutBytes = SyncHelperStderrCapBytes,
                    MaxStderrBytes = SyncHelperStderrCapBytes,
                }, ct).ConfigureAwait(false);
                if (!mkdir.Success)
                    throw new InvalidOperationException($"Failed to create guest directory '{mount.SandboxPath}': {mkdir.Stderr}");
                continue;
            }
            if (mount.HostPath is null)
                continue;
            if (File.Exists(mount.HostPath))
            {
                var opts = _readOptions();
                // Pre-read length guard: stat before buffering so a large
                // mount source is refused without growing host memory to the
                // full file size.
                if (new FileInfo(mount.HostPath).Length > opts.MaxFileSyncBytes)
                    throw new InvalidOperationException($"Mount source '{mount.HostPath}' exceeds the file bound.");
                // Streamed read with an incremental ceiling: closes the
                // stat-then-read race if the file grows between the guard
                // above and the copy, mirroring the pre-buffer guards used
                // for guest data (CopyBoundedAsync/DecodeBounded).
                var bytes = await ReadHostFileBoundedAsync(mount.HostPath, opts.MaxFileSyncBytes, ct).ConfigureAwait(false);
                if (bytes.Length > opts.MaxFileSyncBytes)
                    throw new InvalidOperationException($"Mount source '{mount.HostPath}' exceeds the file bound.");
                await WriteGuestFileAsync(mount.SandboxPath, Utf8.GetString(bytes), mode: null, ct).ConfigureAwait(false);
                continue;
            }
            if (Directory.Exists(mount.HostPath))
            {
                var archive = CreateTarGzBase64(mount.HostPath, _readOptions());
                await _api.ExtractArchiveAsync(_endpointFactory(), _name, mount.SandboxPath, archive, ct).ConfigureAwait(false);
                continue;
            }
            throw new InvalidOperationException($"Mount source '{mount.HostPath}' does not exist.");
        }
    }

    public async Task SyncStateToHostAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) == 1)
            return;
        await _syncGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var mount in _mounts)
            {
                ct.ThrowIfCancellationRequested();
                if (mount.ReadOnly || mount.IsGuestDir || mount.HostPath is null)
                    continue;
                await SyncMountBackAsync(mount, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private async Task SyncMountBackAsync(BoxLiteMountPlan mount, CancellationToken ct)
    {
        var opts = _readOptions();
        var hostPath = mount.HostPath!;
        if (File.Exists(hostPath) && !Directory.Exists(hostPath))
        {
            await SyncFileMountBackAsync(mount, hostPath, ct).ConfigureAwait(false);
            return;
        }
        BoxLiteArchiveResult? archive;
        try
        {
            archive = await _api.CreateArchiveAsync(
                _endpointFactory(), _name, mount.SandboxPath, ct,
                BoxLiteApiClient.BoundPayloadResponseBytes(opts.MaxSyncArchiveBase64Bytes)).ConfigureAwait(false);
        }
        catch (BoxLiteApiException ex)
        {
            _log.LogWarning(ex, "Sync-back of mount {SandboxPath} failed; host copy left untouched", mount.SandboxPath);
            return;
        }
        if (archive?.ArchiveBase64 is null)
        {
            _log.LogWarning("Sync-back of mount {SandboxPath} returned no archive; host copy left untouched", mount.SandboxPath);
            return;
        }
        if (archive.ArchiveBase64.Length > opts.MaxSyncArchiveBase64Bytes)
        {
            _log.LogWarning("Sync-back of mount {SandboxPath} exceeded the archive bound; host copy left untouched", mount.SandboxPath);
            return;
        }
        byte[] compressed;
        try
        {
            compressed = Convert.FromBase64String(archive.ArchiveBase64);
        }
        catch (FormatException)
        {
            _log.LogWarning("Sync-back of mount {SandboxPath} was not valid base64; host copy left untouched", mount.SandboxPath);
            return;
        }
        if (compressed.Length > opts.MaxSyncArchiveBytes)
        {
            _log.LogWarning("Sync-back of mount {SandboxPath} exceeded the compressed bound; host copy left untouched", mount.SandboxPath);
            return;
        }
        var staging = hostPath + ".codeybox-incoming-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(staging);
            ExtractTarGzValidated(compressed, staging, opts);
            SwapDirectories(staging, hostPath);
            staging = string.Empty;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sync-back of mount {SandboxPath} failed validation; host copy left untouched", mount.SandboxPath);
        }
        finally
        {
            if (!string.IsNullOrEmpty(staging))
            {
                try { Directory.Delete(staging, recursive: true); } catch { }
            }
        }
    }

    /// <summary>
    /// Reads a writable single-file mount back from the guest and replaces
    /// the host file atomically (write temp + move). A missing or unreadable
    /// guest file leaves the host copy untouched.
    /// </summary>
    private async Task SyncFileMountBackAsync(BoxLiteMountPlan mount, string hostPath, CancellationToken ct)
    {
        string? content;
        try
        {
            content = await ReadGuestFileAsync(mount.SandboxPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sync-back of file mount {SandboxPath} failed; host copy left untouched", mount.SandboxPath);
            return;
        }
        if (content is null)
            return;
        var tmp = hostPath + ".codeybox-incoming-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(tmp, content, Utf8, ct).ConfigureAwait(false);
            File.Move(tmp, hostPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sync-back of file mount {SandboxPath} failed; host copy left untouched", mount.SandboxPath);
            try { File.Delete(tmp); } catch { }
        }
    }

    private static void SwapDirectories(string staging, string target)
    {
        var backup = target + ".codeybox-prev-" + Guid.NewGuid().ToString("N");
        var targetExisted = Directory.Exists(target) || File.Exists(target);
        try
        {
            if (targetExisted)
                Directory.Move(target, backup);
            Directory.Move(staging, target);
        }
        catch
        {
            try
            {
                if (Directory.Exists(target) || File.Exists(target))
                {
                    try { Directory.Delete(target, recursive: true); } catch { }
                    try { File.Delete(target); } catch { }
                }
                if (targetExisted && Directory.Exists(backup))
                    Directory.Move(backup, target);
            }
            catch
            {
            }
            throw;
        }
        if (targetExisted && Directory.Exists(backup))
        {
            try { Directory.Delete(backup, recursive: true); } catch { }
        }
    }

    internal static string CreateTarGzBase64(string hostDir, BoxLiteSandboxOptions opts)
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            var expanded = 0L;
            var entries = 0;
            foreach (var file in Directory.EnumerateFiles(hostDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(hostDir, file);
                if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                    continue;
                var info = new FileInfo(file);
                if (info.Length + expanded > opts.MaxSyncArchiveExpandedBytes)
                    throw new InvalidOperationException($"Mount source '{hostDir}' exceeds the staging bound.");
                if (++entries > opts.MaxSyncArchiveEntries)
                    throw new InvalidOperationException($"Mount source '{hostDir}' exceeds the entry-count bound.");
                var entryName = relative.Replace(Path.DirectorySeparatorChar, '/');
                var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName)
                {
                    ModificationTime = new DateTimeOffset(info.LastWriteTimeUtc),
                };
                using var input = File.OpenRead(file);
                entry.DataStream = input;
                writer.WriteEntry(entry);
                expanded += info.Length;
            }
        }
        var bytes = compressed.ToArray();
        if (bytes.Length > opts.MaxSyncArchiveBytes)
            throw new InvalidOperationException($"Mount source '{hostDir}' exceeds the compressed staging bound.");
        var encoded = Convert.ToBase64String(bytes);
        if (encoded.Length > opts.MaxSyncArchiveBase64Bytes)
            throw new InvalidOperationException($"Mount source '{hostDir}' exceeds the encoded staging bound.");
        return encoded;
    }

    internal static void ExtractTarGzValidated(byte[] compressed, string targetDir, BoxLiteSandboxOptions opts)
    {
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new TarReader(gzip, leaveOpen: false);
        var expanded = 0L;
        var entries = 0;
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (++entries > opts.MaxSyncArchiveEntries)
                throw new InvalidOperationException("Archive exceeds the entry-count bound.");
            var name = entry.Name.Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrWhiteSpace(name) || name.Contains("..", StringComparison.Ordinal))
                throw new InvalidOperationException("Archive entry escapes the target directory.");
            if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink or TarEntryType.Directory)
            {
                if (entry.EntryType is TarEntryType.Directory)
                {
                    var dirFull = Path.GetFullPath(Path.Combine(targetDir, name));
                    if (!dirFull.StartsWith(Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        throw new InvalidOperationException("Archive entry escapes the target directory.");
                    Directory.CreateDirectory(dirFull);
                    continue;
                }
                throw new InvalidOperationException("Archive links are refused.");
            }
            if (entry.EntryType is not TarEntryType.RegularFile and not TarEntryType.V7RegularFile)
                throw new InvalidOperationException($"Archive entry type '{entry.EntryType}' is refused.");
            var dest = Path.Combine(targetDir, name);
            var full = Path.GetFullPath(dest);
            if (!full.StartsWith(Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException("Archive entry escapes the target directory.");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (entry.DataStream is null)
                continue;
            using var output = File.Create(full);
            var buffer = new byte[CopyBufferBytes];
            int read;
            while ((read = entry.DataStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                expanded += read;
                if (expanded > opts.MaxSyncArchiveExpandedBytes)
                    throw new InvalidOperationException("Archive exceeds the expanded-size bound.");
                output.Write(buffer, 0, read);
            }
        }
    }

    // ------------------------------------------------------------------
    // Setup commands
    // ------------------------------------------------------------------

    /// <summary>
    /// Runs operator-trusted setup commands through <c>bash -lc</c> before any
    /// agent code. A non-zero exit is a deterministic provisioning defect.
    /// </summary>
    internal async Task RunSetupCommandsAsync(IReadOnlyList<string> commands, CancellationToken ct)
    {
        foreach (var command in commands)
        {
            if (string.IsNullOrWhiteSpace(command))
                continue;
            var result = await ExecAsync(new SandboxExec
            {
                Argv = ["bash", "-lc", command],
                MaxStdoutBytes = SyncHelperStderrCapBytes,
                MaxStderrBytes = SyncHelperStderrCapBytes,
            }, ct).ConfigureAwait(false);
            if (result.ExecutionUnavailable)
                throw new InvalidOperationException($"Setup command transport failed: {result.Stderr}");
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"Setup command failed (exit {result.ExitCode}): {result.Stderr}");
        }
    }

    // ------------------------------------------------------------------
    // Suspend / preempt / teardown
    // ------------------------------------------------------------------

    public async Task SuspendAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        await _api.TransitionVmAsync(_endpointFactory(), _name, "pause", ct).ConfigureAwait(false);
        Interlocked.Exchange(ref _suspended, 1);
        Interlocked.Exchange(ref _preserveOnDispose, 1);
    }

    public async Task StopAndPreserveAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        await _api.TransitionVmAsync(_endpointFactory(), _name, "stop", ct).ConfigureAwait(false);
        Interlocked.Exchange(ref _preserveOnDispose, 1);
    }

    /// <summary>
    /// Retains the VM for infrastructure recovery: stops it, stamps the
    /// SHA-256 of a fresh token on the VM labels, and returns the lease. The
    /// token itself never leaves the lease; adoption re-verifies the hash.
    /// </summary>
    internal async Task<SandboxRecoveryLease?> RetainForInfrastructureRecoveryAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        try
        {
            await _api.TransitionVmAsync(_endpointFactory(), _name, "stop", ct).ConfigureAwait(false);
            await _api.SetVmLabelsAsync(_endpointFactory(), _name,
                new Dictionary<string, string> { [BoxLiteSandboxOptions.RecoveryTokenHashLabelKey] = hash }, ct).ConfigureAwait(false);
        }
        catch (BoxLiteApiException ex)
        {
            _log.LogWarning(ex, "Retain of BoxLite VM {Name} failed", _name);
            return null;
        }
        Interlocked.Exchange(ref _preserveOnDispose, 1);
        return new SandboxRecoveryLease(BoxLiteSandboxOptions.ProviderKind, _name, token);
    }

    async Task<SandboxRecoveryLease?> IPreemptibleSandbox.RetainForInfrastructureRecoveryAsync(CancellationToken ct) =>
        await RetainForInfrastructureRecoveryAsync(ct).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposing, 1) == 1)
        {
            var start = _clock.GetUtcNow();
            while (Volatile.Read(ref _disposed) == 0 && _clock.GetUtcNow() - start < DisposeGateWaitTimeout)
                await Task.Delay(DisposeGatePollInterval, _clock).ConfigureAwait(false);
            return;
        }
        try
        {
            if (Volatile.Read(ref _preserveOnDispose) == 0)
            {
                try
                {
                    await SyncStateToHostAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "BoxLite VM {Name} state sync during dispose failed", _name);
                }
                try
                {
                    await _api.DeleteVmAsync(_endpointFactory(), _name, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Delete of BoxLite VM {Name} failed during dispose", _name);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _disposed, 1);
            _execGate.Dispose();
            _syncGate.Dispose();
            try
            {
                _onDisposed(this);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "BoxLite VM {Name} dispose callback failed", _name);
            }
        }
    }

    internal static bool IsValidAbsolutePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        if (path[0] != '/')
            return false;
        if (path.Contains("..", StringComparison.Ordinal))
            return false;
        foreach (var ch in path)
        {
            if (ch < 0x20 || ch == 0x7f)
                return false;
            if (ch is '\'' or '"' or '`' or '$' or '\\' or '\n' or '\r' or '\0')
                return false;
        }
        return true;
    }
}
