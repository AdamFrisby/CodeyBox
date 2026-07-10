using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Sandbox.Incus;

internal sealed class IncusSandbox :
    IPreemptibleSandbox,
    IPreserveOnDisposeSandbox,
    IShutdownTeardownSandbox,
    IProviderOwnedSandbox
{
    private const int MaxExecArguments = 4096;
    private const int MaxExecEnvironmentEntries = 512;
    private const int MaxExecArgvUtf8Bytes = 1024 * 1024;
    private const int MaxExecInputUtf8Bytes = 16 * 1024 * 1024;
    private const string ResourceMetricsScript = """
        set -eu
        read -r _ user nice system idle iowait irq softirq steal _ < /proc/stat
        total=$((user + nice + system + idle + iowait + irq + softirq + steal))
        busy=$((total - idle - iowait))
        cpu=$(awk -v busy="$busy" -v total="$total" 'BEGIN { if (total > 0) printf "%.6f", (busy * 100.0) / total }')
        read -r uptime _ < /proc/uptime
        read -r load1 load5 load15 _ < /proc/loadavg
        peak=$(head -c 64 /run/codeybox-peak-ram-bytes 2>/dev/null || true)
        rx=0
        tx=0
        for interface_path in /sys/class/net/*; do
          [ "${interface_path##*/}" = lo ] && continue
          interface_rx=$(head -c 64 "$interface_path/statistics/rx_bytes" 2>/dev/null || printf 0)
          interface_tx=$(head -c 64 "$interface_path/statistics/tx_bytes" 2>/dev/null || printf 0)
          rx=$((rx + interface_rx))
          tx=$((tx + interface_tx))
        done
        printf 'uptime=%s\nload1=%s\nload5=%s\nload15=%s\ncpu=%s\npeak=%s\nrx=%s\ntx=%s\n' \
          "$uptime" "$load1" "$load5" "$load15" "$cpu" "$peak" "$rx" "$tx"
        """;
    private readonly string _sandboxRoot;
    private readonly string _stagingRoot;
    private readonly SandboxSpec _spec;
    private readonly IncusSandboxOptions _options;
    private readonly IncusCliRunner _cli;
    private readonly ILogger _log;
    private readonly ITimingStore? _timings;
    private readonly WorkItemId _timingItemId;
    private readonly string _timingPhase;
    private readonly string? _baselineRef;
    private readonly ISandboxResourceUsageStore? _resourceUsageStore;
    private readonly Action<string> _onDisposed;
    private readonly ConcurrentDictionary<string, bool> _activeExecs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    // 0 running, 1 stopping-to-preserve, 2 preserved, 3 disposing, 4 disposed,
    // 5 VM absent but private staging cleanup is pending.
    private int _lifecycleState;
    private int _preserveOnDispose;
    private int _ownedByShutdownHandler;
    private int _execCount;
    private int _execCleanupPoisoned;
    private int _noLongerActive;

    internal IncusSandbox(
        string id,
        string sandboxRoot,
        string stagingRoot,
        SandboxSpec spec,
        IncusSandboxOptions options,
        IncusCliRunner cli,
        ILogger log,
        ITimingStore? timings,
        WorkItemId timingItemId,
        string timingPhase,
        string? baselineRef,
        ISandboxResourceUsageStore? resourceUsageStore,
        Action<string> onDisposed)
    {
        Id = id;
        _sandboxRoot = sandboxRoot;
        _stagingRoot = stagingRoot;
        _spec = spec;
        _options = options;
        _cli = cli;
        _log = log;
        _timings = timings;
        _timingItemId = timingItemId;
        _timingPhase = timingPhase;
        _baselineRef = baselineRef;
        _resourceUsageStore = resourceUsageStore;
        _onDisposed = onDisposed;
    }

    public string Id { get; }
    public string ProviderId => IncusSandboxProvider.ProviderId;
    public SandboxResourceMetrics? ResourceMetrics { get; private set; }
    public bool IsOwnedByShutdownHandler => Volatile.Read(ref _ownedByShutdownHandler) != 0;

    public void MarkOwnedByShutdownHandler() => Interlocked.Exchange(ref _ownedByShutdownHandler, 1);

    public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exec);
        ValidateExec(exec);
        var runId = Guid.NewGuid().ToString("N");
        var workingDirectory = exec.WorkingDirectory ?? _spec.WorkingDirectory;
        IncusInputValidation.ValidateAbsoluteGuestPath(workingDirectory, nameof(exec.WorkingDirectory));
        var environment = MergeEnvironment(_spec.Environment, exec.ExtraEnvironment);
        var environmentPayload = SerializeEnvironment(environment);
        var environmentPath = $"{IncusCloudInit.ControlDirectory}/env-{runId}";
        var pidPath = $"{IncusCloudInit.ControlDirectory}/pid-{runId}";
        var completionPath = $"{IncusCloudInit.ControlDirectory}/complete-{runId}";
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_lifecycleState != 0)
                throw new InvalidOperationException("The Incus sandbox is stopping, preserved, or disposed.");
            if (Volatile.Read(ref _execCleanupPoisoned) != 0)
                throw new InvalidOperationException("The Incus sandbox has an unverified prior exec cleanup and must be disposed.");
            _activeExecs[runId] = true;
        }
        finally
        {
            _lifecycleGate.Release();
        }
        var cleanupConfirmed = true;
        Exception? primaryFailure = null;
        SandboxExecResult? sandboxResult = null;
        InvalidOperationException? cleanupFailure = null;
        try
        {
            var firstExec = Interlocked.Increment(ref _execCount) == 1;
            await using var execTiming = await TimingScope.BeginAsync(
                firstExec ? _timings : null,
                _timingItemId,
                _timingPhase,
                "vm.exec_first",
                log: _log).ConfigureAwait(false);
            await PushEnvironmentAsync(environmentPath, environmentPayload, ct).ConfigureAwait(false);
            var command = new List<string>(exec.Argv.Count + 7)
            {
                IncusCloudInit.ExecWrapperPath,
                environmentPath,
                pidPath,
                completionPath,
                workingDirectory,
                _options.GuestHome,
                _options.GuestUserId.ToString(CultureInfo.InvariantCulture),
                _options.GuestGroupId.ToString(CultureInfo.InvariantCulture),
            };
            command.AddRange(exec.Argv);
            using var wallClock = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var execTimeout = _options.ExecTimeout;
            if (_spec.Limits.WallClock is { } limit)
            {
                wallClock.CancelAfter(limit);
                if (limit < execTimeout)
                    execTimeout = limit;
            }
            ProcessRunResult result;
            try
            {
                result = await _cli.RunAllowFailureAsync(
                    _options,
                    BuildRootExec(command),
                    exec.Stdin,
                    execTimeout,
                    wallClock.Token,
                    heavyOperation: false,
                    maxStdoutBytes: exec.MaxStdoutBytes ?? _options.MaxCliStdoutBytes,
                    maxStderrBytes: exec.MaxStderrBytes ?? _options.MaxCliStderrBytes,
                    stdoutChunkCallback: exec.StdoutChunkCallback,
                    stderrChunkCallback: exec.StderrChunkCallback,
                    killOnOutputLimit: exec.KillOnOutputLimit).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                Interlocked.Exchange(ref _execCleanupPoisoned, 1);
                _ = await TerminateAmbiguousExecAsync(runId).ConfigureAwait(false);
                cleanupConfirmed = false;
                throw;
            }
            if (exec.KillOnOutputLimit && (result.StdoutLimitExceeded || result.StderrLimitExceeded))
            {
                Interlocked.Exchange(ref _execCleanupPoisoned, 1);
                _ = await TerminateAmbiguousExecAsync(runId).ConfigureAwait(false);
                cleanupConfirmed = false;
            }
            if (result.ExecutionUnavailable || result.StartFailed)
            {
                Interlocked.Exchange(ref _execCleanupPoisoned, 1);
                _ = await TerminateAmbiguousExecAsync(runId).ConfigureAwait(false);
                cleanupConfirmed = false;
            }
            if (cleanupConfirmed
                && !await VerifyGuestExecCompletionAsync(completionPath, result.ExitCode).ConfigureAwait(false))
            {
                Interlocked.Exchange(ref _execCleanupPoisoned, 1);
                _ = await TerminateAmbiguousExecAsync(runId).ConfigureAwait(false);
                cleanupConfirmed = false;
                throw new InvalidOperationException(
                    "Incus CLI returned without a matching guest completion sentinel; the VM was stopped and must be disposed.");
            }
            sandboxResult = new SandboxExecResult(
                result.ExitCode,
                result.Stdout,
                result.Stderr,
                result.StdoutLimitExceeded,
                result.StderrLimitExceeded,
                result.ExecutionUnavailable || result.StartFailed);
        }
        catch (Exception ex)
        {
            primaryFailure = ex;
            throw;
        }
        finally
        {
            if (cleanupConfirmed)
            {
                var environmentAbsent = await EnsureGuestControlFileAbsentAsync(environmentPath).ConfigureAwait(false);
                var pidAbsent = await EnsureGuestControlFileAbsentAsync(pidPath).ConfigureAwait(false);
                var completionAbsent = await EnsureGuestControlFileAbsentAsync(completionPath).ConfigureAwait(false);
                if (environmentAbsent && pidAbsent && completionAbsent)
                {
                    _activeExecs.TryRemove(runId, out _);
                }
                else
                {
                    Interlocked.Exchange(ref _execCleanupPoisoned, 1);
                    _ = await TerminateAmbiguousExecAsync(runId).ConfigureAwait(false);
                    if (primaryFailure is null)
                    {
                        cleanupFailure = new InvalidOperationException(
                            "Incus exec completed, but transient guest control-file cleanup could not be verified; the VM was stopped and must be disposed.");
                    }
                }
            }
        }
        if (cleanupFailure is not null)
            throw cleanupFailure;
        return sandboxResult
            ?? throw new InvalidOperationException("Incus exec completed without a process result.");
    }

    public async Task KillActiveExecsAsync(CancellationToken ct = default)
    {
        var failed = new List<string>();
        foreach (var runId in _activeExecs.Keys)
        {
            if (!await KillRunAsync(runId, ct).ConfigureAwait(false))
            {
                failed.Add(runId);
                continue;
            }
            var environmentAbsent = await EnsureGuestControlFileAbsentAsync(
                $"{IncusCloudInit.ControlDirectory}/env-{runId}").ConfigureAwait(false);
            var pidAbsent = await EnsureGuestControlFileAbsentAsync(
                $"{IncusCloudInit.ControlDirectory}/pid-{runId}").ConfigureAwait(false);
            var completionAbsent = await EnsureGuestControlFileAbsentAsync(
                $"{IncusCloudInit.ControlDirectory}/complete-{runId}").ConfigureAwait(false);
            if (!environmentAbsent || !pidAbsent || !completionAbsent)
            {
                Interlocked.Exchange(ref _execCleanupPoisoned, 1);
                failed.Add(runId);
                continue;
            }
            _activeExecs.TryRemove(runId, out _);
        }
        if (failed.Count != 0)
            throw new InvalidOperationException($"Could not verify termination of {failed.Count} active Incus exec process group(s).");
    }

    public async Task StopAndPreserveAsync(CancellationToken ct = default)
    {
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        var unsafeExecCleanup = false;
        var preemptMarkerWritten = false;
        try
        {
            if (_lifecycleState != 0)
                throw new InvalidOperationException("The Incus sandbox is already stopping, preserved, or disposed.");
            _lifecycleState = 1;
            Exception? guestCleanupError = null;
            try
            {
                await KillActiveExecsAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                guestCleanupError = ex;
                _log.LogWarning(
                    ex,
                    "In-guest Incus exec cleanup was indeterminate; authoritative VM stop will still be attempted");
            }
            unsafeExecCleanup = guestCleanupError is not null
                || Volatile.Read(ref _execCleanupPoisoned) != 0;
            await SetConfigAsync(IncusSandboxProvider.PreemptKey, "true", ct).ConfigureAwait(false);
            preemptMarkerWritten = true;
            if (_options.CaptureResourceMetrics)
                await CaptureResourceMetricsBestEffortAsync().ConfigureAwait(false);
            var argv = IncusCommandBuilder.Prefix(
                _options,
                "stop",
                Id,
                "--timeout",
                Math.Max(1, (int)_options.VmStopTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture));
            await _cli.RunCheckedAsync(
                "stop and preserve VM",
                _options,
                argv,
                stdin: null,
                _options.VmStopTimeout + _options.OperationTimeout,
                ct).ConfigureAwait(false);
            var stoppedStatus = await ReadOwnedInstanceStatusAsync(ct).ConfigureAwait(false);
            if (!string.Equals(stoppedStatus, "STOPPED", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Incus stop returned success without a positively verified STOPPED state.");
            _lifecycleState = 2;
            if (unsafeExecCleanup)
            {
                await DeleteAfterUnsafePreservationAsync().ConfigureAwait(false);
                throw new InvalidOperationException(
                    "The Incus VM was deleted instead of preserved because transient exec-secret cleanup could not be verified.",
                    guestCleanupError);
            }
            Interlocked.Exchange(ref _preserveOnDispose, 1);
        }
        catch (Exception stopError)
        {
            if (_lifecycleState == 4)
                throw;
            if (unsafeExecCleanup && _lifecycleState != 4)
            {
                try
                {
                    await DeleteAfterUnsafePreservationAsync().ConfigureAwait(false);
                }
                catch (Exception deleteError)
                {
                    Interlocked.Exchange(ref _preserveOnDispose, 0);
                    _lifecycleState = 1;
                    throw new AggregateException(
                        "Incus preservation was unsafe and forced deletion could not be verified.",
                        stopError,
                        deleteError);
                }
                throw new InvalidOperationException(
                    "Incus preservation was unsafe, so the owned VM was force-deleted.",
                    stopError);
            }
            string? status;
            try
            {
                status = await ReadOwnedInstanceStatusAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception verificationError)
            {
                _lifecycleState = 1;
                throw new AggregateException(
                    "Incus stop failed and the VM state could not be verified; the sandbox remains lifecycle-indeterminate.",
                    stopError,
                    verificationError);
            }
            if (string.Equals(status, "STOPPED", StringComparison.OrdinalIgnoreCase))
            {
                if (!preemptMarkerWritten)
                    await EnsurePreemptMarkerOrDeleteAsync(stopError).ConfigureAwait(false);
                Interlocked.Exchange(ref _preserveOnDispose, 1);
                _lifecycleState = 2;
                throw new InvalidOperationException(
                    "Incus stop reported a failure, but the VM is stopped and remains preserved.",
                    stopError);
            }
            if (string.Equals(status, "RUNNING", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _ = await _cli.RunAllowFailureAsync(
                        _options,
                        IncusCommandBuilder.Prefix(_options, "stop", Id, "--force"),
                        stdin: null,
                        _options.VmStopTimeout + _options.OperationTimeout,
                        CancellationToken.None,
                        heavyOperation: true,
                        maxStdoutBytes: 4096,
                        maxStderrBytes: 4096).ConfigureAwait(false);
                    status = await ReadOwnedInstanceStatusAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception forceStopError)
                {
                    Interlocked.Exchange(ref _preserveOnDispose, 0);
                    _lifecycleState = 1;
                    throw new AggregateException(
                        "Incus graceful stop failed and forced-stop verification also failed; later disposal must force-delete the active VM.",
                        stopError,
                        forceStopError);
                }
                if (string.Equals(status, "STOPPED", StringComparison.OrdinalIgnoreCase))
                {
                    if (!preemptMarkerWritten)
                        await EnsurePreemptMarkerOrDeleteAsync(stopError).ConfigureAwait(false);
                    Interlocked.Exchange(ref _preserveOnDispose, 1);
                    _lifecycleState = 2;
                    throw new InvalidOperationException(
                        "Incus graceful stop failed, but a forced stop was verified and the VM remains preserved.",
                        stopError);
                }
                if (status is null)
                {
                    _lifecycleState = 5;
                    NotifyNoLongerActive();
                    throw new InvalidOperationException(
                        "Incus graceful stop failed and the owned VM became absent during forced stop; private staging cleanup is pending.",
                        stopError);
                }
                Interlocked.Exchange(ref _preserveOnDispose, 0);
                _lifecycleState = 1;
                throw new InvalidOperationException(
                    "Incus stop could not reach a verified STOPPED state; later disposal must force-delete the active VM.",
                    stopError);
            }
            if (status is null)
            {
                _lifecycleState = 5;
                NotifyNoLongerActive();
                throw new InvalidOperationException(
                    "Incus stop failed and the owned VM is now absent; private staging cleanup is pending.",
                    stopError);
            }
            _lifecycleState = 1;
            throw new InvalidOperationException(
                $"Incus stop failed and the VM entered indeterminate status '{status}'.",
                stopError);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task DeleteAfterUnsafePreservationAsync()
    {
        Interlocked.Exchange(ref _preserveOnDispose, 0);
        var exists = await VerifyOwnershipOrAbsenceAsync(CancellationToken.None).ConfigureAwait(false);
        if (exists)
        {
            await _cli.RunCheckedAsync(
                "delete VM after unsafe preservation",
                _options,
                IncusCommandBuilder.Prefix(_options, "delete", Id, "--force"),
                stdin: null,
                _options.OperationTimeout,
                CancellationToken.None).ConfigureAwait(false);
            await WaitForInstanceAbsenceAsync(CancellationToken.None).ConfigureAwait(false);
        }
        DeleteStaging();
        _lifecycleState = 4;
        NotifyNoLongerActive();
    }

    private async Task EnsurePreemptMarkerOrDeleteAsync(Exception stopError)
    {
        try
        {
            await SetConfigAsync(
                IncusSandboxProvider.PreemptKey,
                "true",
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception markerError)
        {
            try
            {
                await DeleteAfterUnsafePreservationAsync().ConfigureAwait(false);
            }
            catch (Exception deleteError)
            {
                throw new AggregateException(
                    "Incus stopped but could not establish its preempt marker or verify fail-closed deletion.",
                    stopError,
                    markerError,
                    deleteError);
            }
            throw new AggregateException(
                "Incus stopped but could not establish its preempt marker, so the owned VM was deleted instead of preserved.",
                stopError,
                markerError);
        }
    }

    public void DisablePreserveOnDispose() => Interlocked.Exchange(ref _preserveOnDispose, 0);

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        var finalized = false;
        var vmAbsent = false;
        var previousState = _lifecycleState;
        try
        {
            if (_lifecycleState is 3 or 4)
                return;
            if (_lifecycleState == 2 && Volatile.Read(ref _preserveOnDispose) != 0)
            {
                NotifyNoLongerActive();
                return;
            }
            _lifecycleState = 3;
            await using var disposeTiming = await TimingScope.BeginAsync(
                _timings,
                _timingItemId,
                _timingPhase,
                "vm.dispose",
                log: _log).ConfigureAwait(false);
            try
            {
                await KillActiveExecsAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Force-deleting the VM below is the authoritative termination
                // boundary when an in-guest process group cannot be verified.
                _log.LogWarning(ex, "Incus exec cleanup was indeterminate; sandbox VM deletion will force termination");
            }
            var exists = await VerifyOwnershipOrAbsenceAsync(CancellationToken.None).ConfigureAwait(false);
            vmAbsent = !exists;
            if (exists && _options.CaptureResourceMetrics)
            {
                await using var resourceTiming = await TimingScope.BeginAsync(
                    _timings,
                    _timingItemId,
                    _timingPhase,
                    "vm.resource_capture",
                    log: _log).ConfigureAwait(false);
                await CaptureResourceMetricsBestEffortAsync().ConfigureAwait(false);
            }
            if (exists)
            {
                exists = await VerifyOwnershipOrAbsenceAsync(CancellationToken.None).ConfigureAwait(false);
                vmAbsent = !exists;
            }
            if (exists)
            {
                try
                {
                    await _cli.RunCheckedAsync(
                        "delete sandbox VM",
                        _options,
                        IncusCommandBuilder.Prefix(_options, "delete", Id, "--force"),
                        stdin: null,
                        _options.OperationTimeout,
                        CancellationToken.None).ConfigureAwait(false);
                    await WaitForInstanceAbsenceAsync(CancellationToken.None).ConfigureAwait(false);
                    vmAbsent = true;
                }
                catch (Exception deleteError)
                {
                    bool stillExists;
                    try
                    {
                        stillExists = await VerifyOwnershipOrAbsenceAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception verificationError)
                    {
                        throw new AggregateException(
                            "Incus sandbox deletion failed and instance absence could not be verified.",
                            deleteError,
                            verificationError);
                    }
                    if (stillExists)
                        throw;
                    vmAbsent = true;
                }
            }
            DeleteStaging();
            finalized = true;
            AuditLog.SandboxDisposed(Id, ResourceMetrics);
        }
        finally
        {
            if (finalized)
                _lifecycleState = 4;
            else if (vmAbsent)
                _lifecycleState = 5;
            else if (_lifecycleState == 3)
                _lifecycleState = previousState;
            if (finalized || vmAbsent)
                NotifyNoLongerActive();
            _lifecycleGate.Release();
        }
    }

    private async Task PushEnvironmentAsync(string path, string payload, CancellationToken ct)
    {
        await _cli.RunCheckedAsync(
            "push exec environment",
            _options,
            IncusCommandBuilder.Prefix(
                _options,
                "file", "push", "-", $"{Id}{path}",
                "--mode=0600",
                "--uid=0",
                "--gid=0"),
            payload,
            _options.OperationTimeout,
            ct).ConfigureAwait(false);
    }

    private async Task<bool> KillRunAsync(string runId, CancellationToken ct)
    {
        var pidPath = $"{IncusCloudInit.ControlDirectory}/pid-{runId}";
        int? pid = null;
        const int maxPidPollAttempts = 5;
        for (var attempt = 0; attempt < maxPidPollAttempts; attempt++)
        {
            var pull = await _cli.RunAllowFailureAsync(
                _options,
                IncusCommandBuilder.Prefix(_options, "file", "pull", $"{Id}{pidPath}", "-"),
                stdin: null,
                _options.OperationTimeout,
                ct,
                heavyOperation: false,
                maxStdoutBytes: 128,
                maxStderrBytes: 4096).ConfigureAwait(false);
            var text = pull.Stdout.Trim();
            if (pull.Success
                && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                && parsed > 1)
            {
                pid = parsed;
                break;
            }
            if (attempt + 1 < maxPidPollAttempts)
                await Task.Delay(_options.ReadinessPollInterval, ct).ConfigureAwait(false);
        }
        if (pid is null)
            return false;
        var processGroup = $"-{pid.Value.ToString(CultureInfo.InvariantCulture)}";
        await _cli.RunAllowFailureAsync(
            _options,
            BuildRootExec(["kill", "-TERM", "--", processGroup]),
            stdin: null,
            _options.OperationTimeout,
            ct,
            heavyOperation: false,
            maxStdoutBytes: 4096,
            maxStderrBytes: 4096).ConfigureAwait(false);
        await _cli.RunAllowFailureAsync(
            _options,
            BuildRootExec(["kill", "-KILL", "--", processGroup]),
            stdin: null,
            _options.OperationTimeout,
            ct,
            heavyOperation: false,
            maxStdoutBytes: 4096,
            maxStderrBytes: 4096).ConfigureAwait(false);
        using var verificationDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        verificationDeadline.CancelAfter(_options.VmStopTimeout);
        try
        {
            while (true)
            {
                var stillRunning = await _cli.RunAllowFailureAsync(
                    _options,
                    BuildRootExec(["kill", "-0", "--", processGroup]),
                    stdin: null,
                    _options.OperationTimeout,
                    verificationDeadline.Token,
                    heavyOperation: false,
                    maxStdoutBytes: 4096,
                    maxStderrBytes: 4096).ConfigureAwait(false);
                if (stillRunning.ExecutionUnavailable || stillRunning.StartFailed)
                    return false;
                if (stillRunning.ExitCode != 0)
                    return true;
                await Task.Delay(_options.ReadinessPollInterval, verificationDeadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && verificationDeadline.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<bool> TerminateAmbiguousExecAsync(string runId)
    {
        try
        {
            _ = await KillRunAsync(runId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not complete guest process-group cleanup before stopping the Incus VM");
        }

        ProcessRunResult stop;
        try
        {
            stop = await _cli.RunAllowFailureAsync(
                _options,
                IncusCommandBuilder.Prefix(_options, "stop", Id, "--force"),
                stdin: null,
                _options.VmStopTimeout + _options.OperationTimeout,
                CancellationToken.None,
                heavyOperation: true,
                maxStdoutBytes: 4096,
                maxStderrBytes: 4096).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not issue authoritative Incus VM stop after ambiguous exec termination");
            return false;
        }
        try
        {
            var status = await ReadOwnedInstanceStatusAsync(CancellationToken.None).ConfigureAwait(false);
            var stopped = status is null || string.Equals(status, "STOPPED", StringComparison.OrdinalIgnoreCase);
            if (!stopped)
            {
                _log.LogError(
                    "Incus VM {SandboxId} remained in status {Status} after ambiguous exec termination",
                    Id,
                    status);
            }
            return stopped;
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "Could not verify Incus VM {SandboxId} stopped after ambiguous exec termination (stop exit {ExitCode})",
                Id,
                stop.ExitCode);
            return false;
        }
    }

    private async Task<bool> EnsureGuestControlFileAbsentAsync(string path)
    {
        const int maximumAttempts = 3;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            try
            {
                _ = await _cli.RunAllowFailureAsync(
                    _options,
                    IncusCommandBuilder.Prefix(_options, "file", "delete", $"{Id}{path}"),
                    stdin: null,
                    _options.OperationTimeout,
                    CancellationToken.None,
                    heavyOperation: false,
                    maxStdoutBytes: 4096,
                    maxStderrBytes: 4096).ConfigureAwait(false);
                var verify = await _cli.RunAllowFailureAsync(
                    _options,
                    // test(1) has no portable -- operand delimiter. This path
                    // is provider-generated, absolute, and never begins with '-'.
                    BuildRootExec(["test", "!", "-e", path]),
                    stdin: null,
                    _options.OperationTimeout,
                    CancellationToken.None,
                    heavyOperation: false,
                    maxStdoutBytes: 128,
                    maxStderrBytes: 4096).ConfigureAwait(false);
                if (verify.Success)
                    return true;
            }
            catch (Exception ex)
            {
                _log.LogDebug(
                    ex,
                    attempt + 1 < maximumAttempts
                        ? "Retrying transient guest-file cleanup verification for Incus sandbox {SandboxId}"
                        : "Transient guest-file cleanup verification failed for Incus sandbox {SandboxId}",
                    Id);
            }
            if (attempt + 1 < maximumAttempts)
                await Task.Delay(_options.ReadinessPollInterval).ConfigureAwait(false);
        }
        _log.LogWarning(
            "Could not verify transient guest-file cleanup for Incus sandbox {SandboxId}",
            Id);
        return false;
    }

    private async Task<bool> VerifyGuestExecCompletionAsync(string path, int expectedExitCode)
    {
        const int maximumAttempts = 3;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            try
            {
                var pull = await _cli.RunAllowFailureAsync(
                    _options,
                    IncusCommandBuilder.Prefix(_options, "file", "pull", $"{Id}{path}", "-"),
                    stdin: null,
                    _options.OperationTimeout,
                    CancellationToken.None,
                    heavyOperation: false,
                    maxStdoutBytes: 64,
                    maxStderrBytes: 4096).ConfigureAwait(false);
                if (pull.Success
                    && int.TryParse(
                        pull.Stdout.Trim(),
                        NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture,
                        out var completedExitCode)
                    && completedExitCode == expectedExitCode)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Retrying Incus guest completion verification for sandbox {SandboxId}", Id);
            }
            if (attempt + 1 < maximumAttempts)
                await Task.Delay(_options.ReadinessPollInterval).ConfigureAwait(false);
        }
        return false;
    }

    private async Task<bool> VerifyOwnershipOrAbsenceAsync(CancellationToken ct)
        => await ReadOwnedInstanceStatusAsync(ct).ConfigureAwait(false) is not null;

    private async Task WaitForInstanceAbsenceAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_options.OperationTimeout);
        try
        {
            while (await VerifyOwnershipOrAbsenceAsync(deadline.Token).ConfigureAwait(false))
                await Task.Delay(_options.ReadinessPollInterval, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Incus reported deleting sandbox '{Id}', but exact absence was not observed within the configured deadline.",
                ex);
        }
    }

    private async Task<string?> ReadOwnedInstanceStatusAsync(CancellationToken ct)
    {
        var instances = await _cli.RunCheckedAsync(
            "verify sandbox ownership or absence",
            _options,
            IncusCommandBuilder.Prefix(_options, "list", Id, "--format=json"),
            stdin: null,
            _options.OperationTimeout,
            ct,
            heavyOperation: false,
            maxStdoutBytes: _options.MaxCliStdoutBytes,
            maxStderrBytes: 4096).ConfigureAwait(false);
        return ParseOwnedInstanceStatus(instances.Stdout, Id);
    }

    internal static bool ParseOwnedInstancePresence(string json, string instanceName) =>
        ParseOwnedInstanceStatus(json, instanceName) is not null;

    internal static string? ParseOwnedInstanceStatus(string json, string instanceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Incus instance-list response was not a JSON array.");
        JsonElement? exact = null;
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (!element.TryGetProperty("name", out var name)
                || !string.Equals(name.GetString(), instanceName, StringComparison.Ordinal))
                continue;
            if (exact is not null)
                throw new InvalidOperationException("Incus returned duplicate exact instance names.");
            exact = element;
        }
        if (exact is null)
            return null;
        if (!exact.Value.TryGetProperty("type", out var type)
            || !string.Equals(type.GetString(), "virtual-machine", StringComparison.Ordinal)
            || !exact.Value.TryGetProperty("config", out var config)
            || config.ValueKind != JsonValueKind.Object
            || !config.TryGetProperty(IncusSandboxProvider.ManagedKey, out var managed)
            || !config.TryGetProperty(IncusSandboxProvider.KindKey, out var kind)
            || !string.Equals(managed.GetString(), "true", StringComparison.Ordinal)
            || !string.Equals(kind.GetString(), IncusSandboxProvider.SandboxKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to delete an Incus instance whose ownership metadata changed.");
        }
        return exact.Value.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.String
                ? status.GetString() ?? string.Empty
                : string.Empty;
    }

    private async Task SetConfigAsync(string key, string value, CancellationToken ct)
    {
        await _cli.RunCheckedAsync(
            $"set sandbox config {key}",
            _options,
            IncusCommandBuilder.Prefix(_options, "config", "set", Id, $"{key}={value}"),
            stdin: null,
            _options.OperationTimeout,
            ct).ConfigureAwait(false);
    }

    private IReadOnlyList<string> BuildRootExec(IReadOnlyList<string> command)
    {
        var argv = IncusCommandBuilder.Prefix(_options, "exec", Id, "--");
        argv.AddRange(command);
        return argv;
    }

    private async Task CaptureResourceMetricsBestEffortAsync()
    {
        if (ResourceMetrics is not null)
            return;
        try
        {
            var result = await _cli.RunCheckedAsync(
                "capture guest resource metrics",
                _options,
                BuildRootExec(["/bin/sh", "-c", ResourceMetricsScript]),
                stdin: null,
                _options.ResourceMetricsCaptureTimeout,
                CancellationToken.None,
                heavyOperation: false,
                maxStdoutBytes: 4096,
                maxStderrBytes: 4096).ConfigureAwait(false);
            var values = ParseMetrics(result.Stdout);
            var uptimeSeconds = ParseDouble(values, "uptime");
            var avgCpuPercent = ParseDouble(values, "cpu");
            var peakRamBytes = ParseLong(values, "peak");
            var rxBytes = ParseLong(values, "rx");
            var txBytes = ParseLong(values, "tx");
            var load1 = ParseDouble(values, "load1");
            var load5 = ParseDouble(values, "load5");
            var load15 = ParseDouble(values, "load15");
            ResourceMetrics = new SandboxResourceMetrics(
                peakRamBytes,
                avgCpuPercent,
                rxBytes,
                txBytes,
                uptimeSeconds,
                load1,
                load5,
                load15,
                _baselineRef,
                _spec.Network.ProfileName,
                _timingPhase,
                DateTimeOffset.UtcNow);
            SandboxResourceMetricsTelemetry.Record(ResourceMetrics);
            if (_resourceUsageStore is not null && _timingItemId.Value != Guid.Empty)
            {
                await _resourceUsageStore.RecordAsync(new SandboxResourceUsageRecord
                {
                    WorkItemId = _timingItemId,
                    Phase = _timingPhase,
                    VmName = Id,
                    DurationSeconds = uptimeSeconds,
                    AvgCpuPercent = avgCpuPercent,
                    PeakRamMb = peakRamBytes / (1024d * 1024d),
                    NetRxMb = rxBytes / (1024d * 1024d),
                    NetTxMb = txBytes / (1024d * 1024d),
                    BaselineRef = _baselineRef,
                    NetworkProfile = _spec.Network.ProfileName,
                    LoadAvg1 = load1,
                    LoadAvg5 = load5,
                    LoadAvg15 = load15,
                    CapturedAt = ResourceMetrics.CapturedAt,
                }, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to capture resource metrics for Incus sandbox {SandboxId}", Id);
        }
    }

    private static IReadOnlyDictionary<string, string> ParseMetrics(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0 || separator == line.Length - 1)
                continue;
            var key = line[..separator];
            if (key is "uptime" or "load1" or "load5" or "load15" or "cpu" or "peak" or "rx" or "tx")
                result[key] = line[(separator + 1)..];
        }
        return result;
    }

    private static double? ParseDouble(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value)
        && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static long? ParseLong(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value)
        && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static IReadOnlyDictionary<string, string> MergeEnvironment(
        IReadOnlyDictionary<string, string> baseline,
        IReadOnlyDictionary<string, string>? extra)
    {
        var result = new Dictionary<string, string>(baseline, StringComparer.Ordinal);
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
                result[key] = value;
        }
        if (result.Count > MaxExecEnvironmentEntries)
            throw new ArgumentException($"Exec environment exceeds {MaxExecEnvironmentEntries} entries.", nameof(extra));
        foreach (var (key, value) in result)
        {
            if (!IsEnvironmentKey(key) || value.Contains('\0'))
                throw new ArgumentException($"Exec environment contains an invalid key or NUL value for '{key}'.", nameof(extra));
        }
        return result;
    }

    private static string SerializeEnvironment(IReadOnlyDictionary<string, string> environment)
    {
        var result = new StringBuilder();
        var bytes = 0;
        foreach (var (key, value) in environment.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var entry = $"{key}={value}\0";
            bytes += Encoding.UTF8.GetByteCount(entry);
            if (bytes > MaxExecInputUtf8Bytes)
                throw new ArgumentException("Exec environment exceeds the 16 MiB safety bound.", nameof(environment));
            result.Append(entry);
        }
        return result.ToString();
    }

    private static bool IsEnvironmentKey(string key)
    {
        if (key.Length is < 1 or > 256 || !(key[0] == '_' || char.IsAsciiLetter(key[0])))
            return false;
        return key.Skip(1).All(c => c == '_' || char.IsAsciiLetterOrDigit(c));
    }

    private void ValidateExec(SandboxExec exec)
    {
        if (exec.Argv.Count is < 1 or > MaxExecArguments)
            throw new ArgumentException($"Exec argv must contain between 1 and {MaxExecArguments} arguments.", nameof(exec));
        var bytes = 0;
        for (var index = 0; index < exec.Argv.Count; index++)
        {
            var argument = exec.Argv[index];
            if (argument.Contains('\0'))
                throw new ArgumentException($"Exec argv argument {index} contains NUL.", nameof(exec));
            bytes += Encoding.UTF8.GetByteCount(argument);
            if (bytes > MaxExecArgvUtf8Bytes)
                throw new ArgumentException("Exec argv exceeds the 1 MiB safety bound.", nameof(exec));
        }
        if (string.IsNullOrEmpty(exec.Argv[0]))
            throw new ArgumentException("Exec executable must not be empty.", nameof(exec));
        if (exec.Stdin is { } stdin && Encoding.UTF8.GetByteCount(stdin) > MaxExecInputUtf8Bytes)
            throw new ArgumentException("Exec stdin exceeds the 16 MiB safety bound.", nameof(exec));
        if (exec.MaxStdoutBytes is <= 0 || exec.MaxStderrBytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(exec), "Exec output limits must be positive when supplied.");
        if (exec.MaxStdoutBytes > _options.MaxCliStdoutBytes
            || exec.MaxStderrBytes > _options.MaxCliStderrBytes)
            throw new ArgumentOutOfRangeException(
                nameof(exec),
                "Exec output limits cannot exceed the provider-wide CLI output bounds.");
    }

    private void DeleteStaging() =>
        IncusMountStaging.DeleteOwnedTreeIfContained(_stagingRoot, _sandboxRoot, Id);

    private void NotifyNoLongerActive()
    {
        if (Interlocked.Exchange(ref _noLongerActive, 1) != 0)
            return;
        _onDisposed(Id);
        SandboxLiveCounter.Decrement();
    }
}
