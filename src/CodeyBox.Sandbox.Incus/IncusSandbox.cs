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
    IProviderOwnedSandbox,
    IResourceMetricsCapturingSandbox
{
    internal const int MaxExecArguments = 4096;
    internal const int MaxExecEnvironmentEntries = 512;
    internal const int MaxExecEnvironmentNameCharacters = 256;
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
    private readonly TimeProvider _timeProvider;
    private readonly Func<Guid> _newGuid;
    private readonly Func<IncusSandboxOptions> _liveOptionsAccessor;
    private readonly IncusRecoveryAuthorization _recoveryAuthorization;
    private readonly SandboxRecoveryLease _recoveryLease;
    private readonly IncusRecoveryManifestStore _recoveryManifestStore;
    private readonly string _recoveryTokenHash;
    private readonly string _recoveryBaseManifestHash;
    private readonly IReadOnlyList<IncusPreparedMount> _guestTmpfsMounts;
    private readonly ConcurrentDictionary<string, bool> _activeExecs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    // 0 running, 1 stopping-to-preserve, 2 preserved, 3 disposing, 4 disposed,
    // 5 VM absent but private staging cleanup is pending, 6 durably retained
    // for an infrastructure-recovery lease.
    private int _lifecycleState;
    private int _preserveOnDispose;
    private int _infrastructureRecoveryLeaseArmed;
    private int _ownedByShutdownHandler;
    private int _execCount;
    private int _execCleanupPoisoned;
    private int _noLongerActive;
    private PendingInterruptedExecRecovery? _pendingInterruptedExecRecovery;
    private IncusRecoveryManifest _recoveryManifest;

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
        Action<string> onDisposed,
        IncusRecoveryAuthorization recoveryAuthorization,
        SandboxRecoveryLease recoveryLease,
        IncusRecoveryManifest recoveryManifest,
        IncusRecoveryManifestStore recoveryManifestStore,
        TimeProvider? timeProvider = null,
        Func<Guid>? newGuid = null,
        Func<IncusSandboxOptions>? liveOptionsAccessor = null)
    {
        IncusInputValidation.ValidateInstanceName(id, nameof(id));
        Id = id;
        _sandboxRoot = sandboxRoot;
        _stagingRoot = stagingRoot;
        _spec = IncusInputSnapshot.CaptureSpec(spec);
        _options = IncusInputSnapshot.CaptureOptions(options);
        _cli = cli;
        _log = log;
        _timings = timings;
        _timingItemId = timingItemId;
        _timingPhase = timingPhase;
        _baselineRef = baselineRef;
        _resourceUsageStore = resourceUsageStore;
        _onDisposed = onDisposed;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _newGuid = newGuid ?? Guid.NewGuid;
        _liveOptionsAccessor = liveOptionsAccessor ?? (() => _options);
        _recoveryAuthorization = recoveryAuthorization
            ?? throw new ArgumentNullException(nameof(recoveryAuthorization));
        _recoveryLease = recoveryLease
            ?? throw new ArgumentNullException(nameof(recoveryLease));
        _recoveryManifest = recoveryManifest
            ?? throw new ArgumentNullException(nameof(recoveryManifest));
        _recoveryManifestStore = recoveryManifestStore
            ?? throw new ArgumentNullException(nameof(recoveryManifestStore));
        _recoveryTokenHash = IncusRecoveryManifestCodec.ComputeTokenSha256(_recoveryLease.Token);
        var normalizedManifest = _recoveryManifest with
        {
            Retained = false,
            PendingExec = null,
        };
        _recoveryBaseManifestHash = IncusRecoveryManifestCodec.ComputeSha256(
            IncusRecoveryManifestCodec.Serialize(normalizedManifest));
        ValidateRecoveryBinding();
        _recoveryAuthorization.RevalidateForRestart(_spec, _options, _stagingRoot);
        _guestTmpfsMounts = _recoveryAuthorization.GuestTmpfsMounts;
        if (_recoveryManifest.Retained)
        {
            // Adoption must remain fail-closed until the orchestrator has
            // durably published the replacement checkpoint and explicitly
            // disarms preservation. A failed preparation attempt can then be
            // retried by a later process with the same private capability.
            _infrastructureRecoveryLeaseArmed = 1;
            _preserveOnDispose = 1;
        }
        if (_recoveryManifest.PendingExec is { } pending)
        {
            _pendingInterruptedExecRecovery = new PendingInterruptedExecRecovery(
                pending.RunId,
                pending.EnvironmentPath,
                pending.PidPath,
                pending.CompletionPath,
                pending.HostDevicesDetached);
            _activeExecs[pending.RunId] = true;
            _execCleanupPoisoned = 1;
        }
    }

    public string Id { get; }
    public string ProviderId => IncusSandboxProvider.ProviderId;
    public bool CapturesResourceMetrics => _options.CaptureResourceMetrics;
    public SandboxResourceMetrics? ResourceMetrics { get; private set; }
    public bool IsOwnedByShutdownHandler => Volatile.Read(ref _ownedByShutdownHandler) != 0;

    public void MarkOwnedByShutdownHandler() => Interlocked.Exchange(ref _ownedByShutdownHandler, 1);

    private void ValidateRecoveryBinding()
    {
        if (!string.Equals(_recoveryLease.ProviderId, IncusSandboxProvider.ProviderId, StringComparison.Ordinal)
            || !string.Equals(_recoveryLease.SandboxId, Id, StringComparison.Ordinal)
            || _recoveryManifest.Version != IncusRecoveryManifest.CurrentVersion
            || !string.Equals(_recoveryManifest.ProviderId, IncusSandboxProvider.ProviderId, StringComparison.Ordinal)
            || !string.Equals(_recoveryManifest.SandboxId, Id, StringComparison.Ordinal)
            || !string.Equals(_recoveryManifest.ProjectName, _options.ProjectName, StringComparison.Ordinal)
            || !string.Equals(_recoveryManifest.StoragePoolName, _options.StoragePoolName, StringComparison.Ordinal)
            || !string.Equals(_recoveryManifest.GuestHome, _options.GuestHome, StringComparison.Ordinal)
            || _recoveryManifest.GuestUserId != _options.GuestUserId
            || _recoveryManifest.GuestGroupId != _options.GuestGroupId)
        {
            throw new InvalidDataException("Incus recovery lease and manifest identity do not match this sandbox.");
        }
        var tokenHash = IncusRecoveryManifestCodec.ComputeTokenSha256(_recoveryLease.Token);
        if (!IncusRecoveryManifestCodec.FixedTimeEqualsHash(tokenHash, _recoveryManifest.LeaseTokenSha256))
            throw new InvalidDataException("Incus recovery lease token does not match its manifest.");
        var specHash = IncusRecoveryManifestCodec.ComputeSpecSha256(_spec);
        if (!IncusRecoveryManifestCodec.FixedTimeEqualsHash(specHash, _recoveryManifest.SpecSha256))
            throw new InvalidDataException("Incus recovery manifest does not match the sandbox specification.");
        if (_recoveryManifest.Retained != (_recoveryManifest.PendingExec is not null))
            throw new InvalidDataException("Incus recovery manifest has an invalid retained-state shape.");
        if (_recoveryManifest.Retained)
            _recoveryManifest.ValidatePendingExec();
    }

    public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        exec = IncusInputSnapshot.CaptureExec(exec);
        ValidateExec(exec);
        var runId = NextGuid("exec control files").ToString("N");
        var workingDirectory = exec.WorkingDirectory ?? _spec.WorkingDirectory;
        IncusInputValidation.ValidateAbsoluteGuestPath(workingDirectory, nameof(exec.WorkingDirectory));
        var environment = MergeEnvironment(_spec.Environment, exec.ExtraEnvironment, exec);
        var environmentPayload = SerializeEnvironment(environment);
        var environmentPath = $"{IncusCloudInit.ControlDirectory}/env-{runId}";
        var pidPath = $"{IncusCloudInit.ControlDirectory}/pid-{runId}";
        var completionPath = $"{IncusCloudInit.ControlDirectory}/complete-{runId}";
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_lifecycleState != 0)
                throw new InvalidOperationException("The Incus sandbox is stopping, preserved, or disposed.");
            if (Volatile.Read(ref _execCleanupPoisoned) != 0
                && !await TryRecoverPendingInterruptedExecUnderGateAsync(ct).ConfigureAwait(false))
            {
                return new SandboxExecResult(
                    255,
                    string.Empty,
                    "Incus infrastructure recovery remains unavailable after this bounded attempt window; the exact interrupted exec is retained for a later resume.\n",
                    ExecutionUnavailable: true);
            }
            if (Volatile.Read(ref _infrastructureRecoveryLeaseArmed) != 0)
            {
                if (!_activeExecs.IsEmpty)
                {
                    throw new InvalidOperationException(
                        "Incus recovery-checkpoint preparation permits only one durably journaled exec at a time.");
                }
                PublishRetainedPendingExec(new PendingInterruptedExecRecovery(
                    runId,
                    environmentPath,
                    pidPath,
                    completionPath,
                    HostDevicesDetached: false));
            }
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
            var execTimeout = _options.ExecTimeout;
            if (_spec.Limits.WallClock is { } limit)
            {
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
                    ct,
                    heavyOperation: false,
                    maxStdoutBytes: exec.MaxStdoutBytes ?? _options.MaxCliStdoutBytes,
                    maxStderrBytes: exec.MaxStderrBytes ?? _options.MaxCliStderrBytes,
                    stdoutChunkCallback: exec.StdoutChunkCallback,
                    stderrChunkCallback: exec.StderrChunkCallback,
                    killOnOutputLimit: exec.KillOnOutputLimit).ConfigureAwait(false);
            }
            catch
            {
                Interlocked.Exchange(ref _execCleanupPoisoned, 1);
                _ = await TerminateAmbiguousExecAsync(runId).ConfigureAwait(false);
                cleanupConfirmed = false;
                _ = await RegisterAndTryRecoverInterruptedExecAsync(
                    runId,
                    environmentPath,
                    pidPath,
                    completionPath).ConfigureAwait(false);
                throw;
            }
            if (exec.KillOnOutputLimit && (result.StdoutLimitExceeded || result.StderrLimitExceeded))
            {
                Interlocked.Exchange(ref _execCleanupPoisoned, 1);
                _ = await TerminateAmbiguousExecAsync(runId).ConfigureAwait(false);
                cleanupConfirmed = false;
                _ = await RegisterAndTryRecoverInterruptedExecAsync(
                    runId,
                    environmentPath,
                    pidPath,
                    completionPath).ConfigureAwait(false);
            }
            var executionInterrupted = result.ExecutionUnavailable || result.StartFailed;
            if (cleanupConfirmed && executionInterrupted)
            {
                Interlocked.Exchange(ref _execCleanupPoisoned, 1);
                _ = await TerminateAmbiguousExecAsync(runId).ConfigureAwait(false);
                cleanupConfirmed = false;
                _ = await RegisterAndTryRecoverInterruptedExecAsync(
                    runId,
                    environmentPath,
                    pidPath,
                    completionPath).ConfigureAwait(false);
            }
            if (cleanupConfirmed
                && !await VerifyGuestExecCompletionAsync(completionPath, result.ExitCode).ConfigureAwait(false))
            {
                executionInterrupted = true;
                Interlocked.Exchange(ref _execCleanupPoisoned, 1);
                _ = await TerminateAmbiguousExecAsync(runId).ConfigureAwait(false);
                cleanupConfirmed = false;
                _ = await RegisterAndTryRecoverInterruptedExecAsync(
                    runId,
                    environmentPath,
                    pidPath,
                    completionPath).ConfigureAwait(false);
            }
            sandboxResult = new SandboxExecResult(
                result.ExitCode,
                result.Stdout,
                result.Stderr,
                result.StdoutLimitExceeded,
                result.StderrLimitExceeded,
                executionInterrupted);
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
                    cleanupConfirmed = false;
                    _ = await RegisterAndTryRecoverInterruptedExecAsync(
                        runId,
                        environmentPath,
                        pidPath,
                        completionPath).ConfigureAwait(false);
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
            await EnsureLifecycleBindingAsync(ct).ConfigureAwait(false);
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

    public async Task<SandboxRecoveryLease?> RetainForInfrastructureRecoveryAsync(
        CancellationToken ct = default)
    {
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_lifecycleState == 6)
            {
                var retainedJournal = _recoveryManifestStore.ReadRetained();
                retainedJournal.ValidatePendingExec();
                if (retainedJournal.PendingExec != _recoveryManifest.PendingExec)
                    throw new InvalidDataException("Incus retained recovery journal changed after publication.");
                Interlocked.Exchange(ref _infrastructureRecoveryLeaseArmed, 1);
                Interlocked.Exchange(ref _preserveOnDispose, 1);
                return _recoveryLease;
            }
            if (_lifecycleState != 0)
                throw new InvalidOperationException("The Incus sandbox is stopping, preserved, or disposed.");
            if (Volatile.Read(ref _execCleanupPoisoned) != 1
                || _pendingInterruptedExecRecovery is not { } pending)
            {
                throw new InvalidOperationException(
                    "Incus infrastructure retention requires one exact pending interrupted exec.");
            }
            var activeRunIds = _activeExecs.Keys.ToArray();
            if (activeRunIds.Length != 1
                || !string.Equals(activeRunIds[0], pending.RunId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Incus infrastructure retention cannot prove a sole interrupted exec owner.");
            }

            var durablePending = new IncusRecoveryPendingExec(
                pending.RunId,
                pending.EnvironmentPath,
                pending.PidPath,
                pending.CompletionPath,
                pending.HostDevicesDetached);
            if (_recoveryManifest.Retained
                && _recoveryManifest.PendingExec != durablePending)
            {
                throw new InvalidOperationException(
                    "Incus infrastructure retention cannot replace different pending recovery metadata.");
            }
            var retained = _recoveryManifest.Retain(durablePending);
            _recoveryManifestStore.WriteRetained(
                retained,
                NextGuid("retained recovery manifest"));
            _recoveryManifest = retained;
            Interlocked.Exchange(ref _preserveOnDispose, 1);
            Interlocked.Exchange(ref _infrastructureRecoveryLeaseArmed, 1);
            _lifecycleState = 6;
            return _recoveryLease;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    internal async Task RecoverRetainedForAdoptionAsync(CancellationToken ct)
    {
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_lifecycleState != 0
                || Volatile.Read(ref _execCleanupPoisoned) != 1
                || _pendingInterruptedExecRecovery is null)
            {
                throw new InvalidDataException("Incus retained sandbox has no exact pending recovery to adopt.");
            }
            if (!await TryRecoverInterruptedExecUnderGateAsync(
                    _pendingInterruptedExecRecovery,
                    ct).ConfigureAwait(false))
            {
                throw new SandboxExecutionUnavailableException(255);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    internal void ReleaseFailedAdoptionHandle()
    {
        _recoveryAuthorization.Dispose();
        _recoveryManifestStore.Dispose();
    }

    private async Task DeleteAfterUnsafePreservationAsync()
    {
        Interlocked.Exchange(ref _preserveOnDispose, 0);
        Interlocked.Exchange(ref _infrastructureRecoveryLeaseArmed, 0);
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

    public void DisablePreserveOnDispose()
    {
        Interlocked.Exchange(ref _infrastructureRecoveryLeaseArmed, 0);
        Interlocked.Exchange(ref _preserveOnDispose, 0);
    }

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
            if (Volatile.Read(ref _preserveOnDispose) != 0
                && (_lifecycleState is 2 or 6
                    || Volatile.Read(ref _infrastructureRecoveryLeaseArmed) != 0))
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
        var maxPidPollAttempts = ReadRetryAttempts(
            static options => options.ExecPidPollAttempts,
            nameof(IncusSandboxOptions.ExecPidPollAttempts));
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
                await Task.Delay(_options.ReadinessPollInterval, _timeProvider, ct).ConfigureAwait(false);
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
        using var timeoutCancellation = new CancellationTokenSource(_options.VmStopTimeout, _timeProvider);
        using var verificationDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCancellation.Token);
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
                await Task.Delay(_options.ReadinessPollInterval, _timeProvider, verificationDeadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCancellation.IsCancellationRequested)
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
            await EnsureLifecycleBindingAsync(CancellationToken.None).ConfigureAwait(false);
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

    private async Task<bool> RegisterAndTryRecoverInterruptedExecAsync(
        string runId,
        string environmentPath,
        string pidPath,
        string completionPath)
    {
        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_lifecycleState != 0 || Volatile.Read(ref _execCleanupPoisoned) != 1)
                return false;

            var pending = new PendingInterruptedExecRecovery(
                runId,
                environmentPath,
                pidPath,
                completionPath,
                HostDevicesDetached: false);
            if (_pendingInterruptedExecRecovery is { } existing)
            {
                if (!string.Equals(existing.RunId, pending.RunId, StringComparison.Ordinal)
                    || !string.Equals(existing.EnvironmentPath, pending.EnvironmentPath, StringComparison.Ordinal)
                    || !string.Equals(existing.PidPath, pending.PidPath, StringComparison.Ordinal)
                    || !string.Equals(existing.CompletionPath, pending.CompletionPath, StringComparison.Ordinal))
                {
                    _log.LogWarning(
                        "Refusing to replace pending interrupted-exec recovery metadata for Incus sandbox {SandboxId}",
                        Id);
                }

                return false;
            }
            _pendingInterruptedExecRecovery = pending;

            return await TryRecoverInterruptedExecUnderGateAsync(
                pending,
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task<bool> TryRecoverPendingInterruptedExecUnderGateAsync(CancellationToken ct)
    {
        if (_pendingInterruptedExecRecovery is not { } pending)
            return false;

        var policy = ReadInterruptedExecRecoveryPolicy();
        var attemptsThisWindow = 0;
        while (attemptsThisWindow < policy.MaximumRetryAttempts)
        {
            await Task.Delay(policy.RetryDelay, _timeProvider, ct).ConfigureAwait(false);
            attemptsThisWindow++;

            if (await TryRecoverInterruptedExecUnderGateAsync(pending, ct).ConfigureAwait(false))
                return true;

            if (_pendingInterruptedExecRecovery is not { } current
                || !string.Equals(current.RunId, pending.RunId, StringComparison.Ordinal))
            {
                return false;
            }
            pending = current;
        }

        _log.LogWarning(
            "Exhausted {Attempts} delayed interrupted-exec recovery attempts for Incus sandbox {SandboxId}",
            attemptsThisWindow,
            Id);
        return false;
    }

    private async Task<bool> TryRecoverInterruptedExecUnderGateAsync(
        PendingInterruptedExecRecovery pending,
        CancellationToken ct)
    {
        var vmStartMayHaveOccurred = false;
        var recoverySucceeded = false;
        try
        {
            if (_lifecycleState != 0
                || Volatile.Read(ref _execCleanupPoisoned) != 1
                || _pendingInterruptedExecRecovery is not { } current
                || !string.Equals(current.RunId, pending.RunId, StringComparison.Ordinal))
            {
                return false;
            }

            var activeRunIds = _activeExecs.Keys.ToArray();
            if (activeRunIds.Length != 1
                || !string.Equals(activeRunIds[0], pending.RunId, StringComparison.Ordinal))
            {
                _log.LogWarning(
                    "Refusing interrupted-exec recovery for Incus sandbox {SandboxId} because another exec may be active",
                    Id);
                return false;
            }

            var stoppedStatus = await ReadOwnedInstanceStatusAsync(ct).ConfigureAwait(false);
            if (!string.Equals(stoppedStatus, "STOPPED", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning(
                    "Refusing interrupted-exec recovery for Incus sandbox {SandboxId} without an exact owned STOPPED VM",
                    Id);
                return false;
            }

            _recoveryAuthorization.RevalidateForRestart(_spec, _options, _stagingRoot);
            if (_recoveryAuthorization.HasHostDevices)
            {
                if (!pending.HostDevicesDetached)
                {
                    await VerifyRecoveryTopologyAsync(
                        _recoveryAuthorization.Mounts,
                        ct).ConfigureAwait(false);
                    // Record ownership of the topology mutation before the
                    // first remove. A later bounded window can then reconcile
                    // any partial detach to the exact isolated topology.
                    pending = UpdatePendingHostDeviceState(pending, detached: true);
                }
                await RemoveRecoveryHostDevicesAsync(ct).ConfigureAwait(false);
                await VerifyRecoveryTopologyAsync([], ct).ConfigureAwait(false);
            }
            else
            {
                await VerifyRecoveryTopologyAsync(
                    _recoveryAuthorization.Mounts,
                    ct).ConfigureAwait(false);
            }

            // Treat even an unsuccessful start invocation as ambiguous: the
            // daemon may have started the VM before the client lost its reply.
            vmStartMayHaveOccurred = true;
            await IncusGuestLifecycle.StartAndWaitForAgentAsync(
                _cli,
                _options,
                Id,
                _timeProvider,
                token => AuthorizeRecoveryStartAsync([], token),
                ct).ConfigureAwait(false);
            await IncusGuestLinkLifecycle.RemoveForIsolatedValidationAsync(
                _cli,
                _options,
                Id,
                _recoveryAuthorization.GuestLinks,
                ct).ConfigureAwait(false);
            await IncusGuestPathAuthorization.ValidateCanonicalMountPathsAsync(
                _cli,
                _options,
                Id,
                _recoveryAuthorization.CanonicalGuestPaths,
                ct).ConfigureAwait(false);
            foreach (var executableLink in _recoveryAuthorization.ExecutableLinks)
            {
                await IncusGuestLinkLifecycle.VerifyExactAsync(
                    _cli,
                    _options,
                    Id,
                    executableLink,
                    ct).ConfigureAwait(false);
            }

            if (_recoveryAuthorization.HasHostDevices)
            {
                await StopAndVerifyRecoveryVmAsync(ct).ConfigureAwait(false);
                _recoveryAuthorization.RevalidateForRestart(_spec, _options, _stagingRoot);
                await AddRecoveryHostDevicesAsync(ct).ConfigureAwait(false);
                await VerifyRecoveryTopologyAsync(
                    _recoveryAuthorization.Mounts,
                    ct).ConfigureAwait(false);
                pending = UpdatePendingHostDeviceState(pending, detached: false);
                await IncusGuestLifecycle.StartAndWaitForAgentAsync(
                    _cli,
                    _options,
                    Id,
                    _timeProvider,
                    token => AuthorizeRecoveryStartAsync(
                        _recoveryAuthorization.Mounts,
                        token),
                    ct).ConfigureAwait(false);
            }
            var runningStatus = await ReadOwnedInstanceStatusAsync(ct).ConfigureAwait(false);
            if (!string.Equals(runningStatus, "RUNNING", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning(
                    "Interrupted-exec recovery for Incus sandbox {SandboxId} did not reach an exact owned RUNNING VM",
                    Id);
                return false;
            }

            await IncusGuestLifecycle.PrepareRuntimeDirectoryAsync(
                _cli,
                _options,
                Id,
                ct).ConfigureAwait(false);
            await RestoreGuestTmpfsMountsAsync(ct).ConfigureAwait(false);
            await IncusMountReadiness.WaitAsync(
                _cli,
                _options,
                Id,
                _stagingRoot,
                _recoveryAuthorization.Mounts,
                _timeProvider,
                ct).ConfigureAwait(false);
            await IncusGuestLinkLifecycle.CreateAsync(
                _cli,
                _options,
                Id,
                _recoveryAuthorization.GuestLinks,
                ct).ConfigureAwait(false);
            foreach (var executableLink in _recoveryAuthorization.ExecutableLinks)
            {
                await IncusGuestLinkLifecycle.VerifyExactAsync(
                    _cli,
                    _options,
                    Id,
                    executableLink,
                    ct).ConfigureAwait(false);
            }
            await IncusGuestLifecycle.VerifyExecWrapperAsync(
                _cli,
                _options,
                Id,
                ct).ConfigureAwait(false);

            runningStatus = await ReadOwnedInstanceStatusAsync(ct).ConfigureAwait(false);
            if (!string.Equals(runningStatus, "RUNNING", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning(
                    "Interrupted-exec cleanup for Incus sandbox {SandboxId} lost its exact owned RUNNING VM",
                    Id);
                return false;
            }

            var environmentAbsent = await EnsureGuestControlFileAbsentAsync(pending.EnvironmentPath, ct).ConfigureAwait(false);
            var pidAbsent = await EnsureGuestControlFileAbsentAsync(pending.PidPath, ct).ConfigureAwait(false);
            var completionAbsent = await EnsureGuestControlFileAbsentAsync(pending.CompletionPath, ct).ConfigureAwait(false);
            if (!environmentAbsent || !pidAbsent || !completionAbsent)
                return false;
            if (!_activeExecs.TryRemove(pending.RunId, out _))
                return false;
            if (Interlocked.CompareExchange(ref _execCleanupPoisoned, 0, 1) != 1)
            {
                _activeExecs.TryAdd(pending.RunId, true);
                Interlocked.Exchange(ref _execCleanupPoisoned, 1);
                return false;
            }
            _pendingInterruptedExecRecovery = null;
            if (_recoveryManifest.Retained)
            {
                _recoveryManifest = _recoveryManifest with
                {
                    Retained = false,
                    PendingExec = null,
                };
            }
            recoverySucceeded = true;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _execCleanupPoisoned, 1);
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _execCleanupPoisoned, 1);
            _log.LogWarning(
                ex,
                "Bounded interrupted-exec recovery failed closed for Incus sandbox {SandboxId}",
                Id);
            return false;
        }
        finally
        {
            if (vmStartMayHaveOccurred && !recoverySucceeded)
            {
                Interlocked.Exchange(ref _execCleanupPoisoned, 1);
                _ = await ForceStopAndVerifyOwnedVmAfterFailedRecoveryAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> ForceStopAndVerifyOwnedVmAfterFailedRecoveryAsync()
    {
        string? status;
        try
        {
            status = await ReadOwnedInstanceStatusAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "Refusing post-recovery forced stop because exact Incus VM ownership could not be verified for sandbox {SandboxId}",
                Id);
            return false;
        }

        if (string.Equals(status, "STOPPED", StringComparison.OrdinalIgnoreCase))
            return true;
        if (status is null)
        {
            _log.LogError(
                "Could not restore a verified STOPPED state after failed recovery because owned Incus VM {SandboxId} is absent",
                Id);
            return false;
        }

        Exception? stopError = null;
        int? stopExitCode = null;
        try
        {
            await EnsureLifecycleBindingAsync(CancellationToken.None).ConfigureAwait(false);
            var stop = await _cli.RunAllowFailureAsync(
                _options,
                IncusCommandBuilder.Prefix(_options, "stop", Id, "--force"),
                stdin: null,
                _options.VmStopTimeout + _options.OperationTimeout,
                CancellationToken.None,
                heavyOperation: true,
                maxStdoutBytes: 4096,
                maxStderrBytes: 4096).ConfigureAwait(false);
            stopExitCode = stop.ExitCode;
        }
        catch (Exception ex)
        {
            stopError = ex;
        }

        try
        {
            status = await ReadOwnedInstanceStatusAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception verificationError)
        {
            if (stopError is not null)
            {
                _log.LogError(
                    new AggregateException(stopError, verificationError),
                    "Post-recovery forced stop and ownership verification both failed for Incus sandbox {SandboxId}",
                    Id);
            }
            else
            {
                _log.LogError(
                    verificationError,
                    "Could not verify Incus sandbox {SandboxId} after post-recovery forced stop (exit {ExitCode})",
                    Id,
                    stopExitCode);
            }
            return false;
        }

        if (string.Equals(status, "STOPPED", StringComparison.OrdinalIgnoreCase))
            return true;

        if (stopError is not null)
        {
            _log.LogError(
                stopError,
                "Post-recovery forced stop failed and Incus sandbox {SandboxId} remained in status {Status}",
                Id,
                status);
        }
        else
        {
            _log.LogError(
                "Incus sandbox {SandboxId} remained in status {Status} after post-recovery forced stop (exit {ExitCode})",
                Id,
                status,
                stopExitCode);
        }
        return false;
    }

    private async Task RestoreGuestTmpfsMountsAsync(CancellationToken ct)
    {
        var aggregateBytes = 0L;
        foreach (var mount in _guestTmpfsMounts)
        {
            if (mount.HostSource is not null
                || mount.RootDiskDirectory
                || mount.TmpfsSizeBytes is not { } sizeBytes)
            {
                throw new InvalidOperationException(
                    "Interrupted-exec recovery received an invalid guest tmpfs descriptor.");
            }
            checked { aggregateBytes += sizeBytes; }
            if (aggregateBytes > _options.MaxAggregateTmpfsBytes)
            {
                throw new InvalidOperationException(
                    "Interrupted-exec recovery tmpfs mounts exceed the configured aggregate bound.");
            }
            await IncusGuestLifecycle.MountTmpfsAsync(
                _cli,
                _options,
                Id,
                mount.GuestPath,
                sizeBytes,
                ct).ConfigureAwait(false);
            await IncusGuestLifecycle.VerifyTmpfsAsync(
                _cli,
                _options,
                Id,
                mount.GuestPath,
                ct).ConfigureAwait(false);
        }
    }

    private async Task VerifyRecoveryTopologyAsync(
        IReadOnlyList<IncusPreparedMount> expectedMounts,
        CancellationToken ct)
    {
        var topology = await _cli.RunCheckedAsync(
            "verify interrupted-exec recovery topology",
            _options,
            [_options.BinaryPath, "query", $"/1.0/instances/{Id}?project={_options.ProjectName}"],
            stdin: null,
            _options.OperationTimeout,
            ct,
            heavyOperation: false,
            maxStdoutBytes: _options.MaxCliStdoutBytes,
            maxStderrBytes: 4096).ConfigureAwait(false);
        IncusDeviceTopology.Verify(
            topology.Stdout,
            _options,
            _recoveryAuthorization.Bridge,
            expectedMounts,
            _recoveryTokenHash,
            _recoveryBaseManifestHash);
    }

    private async Task AuthorizeRecoveryStartAsync(
        IReadOnlyList<IncusPreparedMount> expectedMounts,
        CancellationToken ct)
    {
        _recoveryAuthorization.RevalidateForRestart(_spec, _options, _stagingRoot);
        await VerifyRecoveryTopologyAsync(expectedMounts, ct).ConfigureAwait(false);
    }

    private async Task RemoveRecoveryHostDevicesAsync(CancellationToken ct)
    {
        for (var index = 0; index < _recoveryAuthorization.Mounts.Count; index++)
        {
            if (_recoveryAuthorization.Mounts[index].HostSource is null)
                continue;
            var deviceName = IncusSandboxProvider.BuildMountDeviceNameForVerification(index);
            var remove = await _cli.RunAllowFailureAsync(
                _options,
                IncusCommandBuilder.BuildDeviceRemove(_options, Id, deviceName),
                stdin: null,
                _options.OperationTimeout,
                ct,
                heavyOperation: true,
                maxStdoutBytes: 4096,
                maxStderrBytes: 4096).ConfigureAwait(false);
            if (!remove.Success)
            {
                _log.LogDebug(
                    "Interrupted-exec recovery device removal for {SandboxId}/{Device} returned exit {ExitCode}; exact isolated topology verification will decide the result",
                    Id,
                    deviceName,
                    remove.ExitCode);
            }
        }
    }

    private async Task AddRecoveryHostDevicesAsync(CancellationToken ct)
    {
        for (var index = 0; index < _recoveryAuthorization.Mounts.Count; index++)
        {
            var mount = _recoveryAuthorization.Mounts[index];
            if (mount.HostSource is not { } source)
                continue;
            var authorizedSource = IncusMountStaging.ReauthorizeHostSource(
                _options,
                _stagingRoot,
                source);
            if (!string.Equals(authorizedSource, source, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("An Incus recovery host mount source changed canonical path before reattachment.");
            var pinnedSource = mount.PinnedHostDirectory
                ?? throw new InvalidOperationException("An Incus recovery host source has no retained identity pin.");
            IncusMountStaging.EnsurePinnedHostSourceMatches(authorizedSource, pinnedSource);
            var deviceName = IncusSandboxProvider.BuildMountDeviceNameForVerification(index);
            var add = await _cli.RunAllowFailureAsync(
                _options,
                IncusCommandBuilder.BuildDeviceAdd(
                    _options,
                    Id,
                    deviceName,
                    authorizedSource,
                    mount.GuestPath,
                    mount.ReadOnly),
                stdin: null,
                _options.OperationTimeout,
                ct,
                heavyOperation: true,
                maxStdoutBytes: 4096,
                maxStderrBytes: 4096).ConfigureAwait(false);
            if (!add.Success)
            {
                _log.LogDebug(
                    "Interrupted-exec recovery device reattachment for {SandboxId}/{Device} returned exit {ExitCode}; exact full topology verification will decide the result",
                    Id,
                    deviceName,
                    add.ExitCode);
            }
            IncusMountStaging.EnsurePinnedHostSourceMatches(authorizedSource, pinnedSource);
        }
    }

    private async Task StopAndVerifyRecoveryVmAsync(CancellationToken ct)
    {
        await EnsureLifecycleBindingAsync(ct).ConfigureAwait(false);
        var stop = await _cli.RunAllowFailureAsync(
            _options,
            IncusCommandBuilder.Prefix(_options, "stop", Id, "--force"),
            stdin: null,
            _options.VmStopTimeout + _options.OperationTimeout,
            ct,
            heavyOperation: true,
            maxStdoutBytes: 4096,
            maxStderrBytes: 4096).ConfigureAwait(false);
        var status = await ReadOwnedInstanceStatusAsync(ct).ConfigureAwait(false);
        if (!string.Equals(status, "STOPPED", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Incus interrupted-exec isolated validation did not return the exact owned VM to STOPPED (stop exit {stop.ExitCode}, status {status ?? "absent"}).");
        }
    }

    private PendingInterruptedExecRecovery UpdatePendingHostDeviceState(
        PendingInterruptedExecRecovery expected,
        bool detached)
    {
        if (_pendingInterruptedExecRecovery is not { } current
            || !string.Equals(current.RunId, expected.RunId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Incus interrupted-exec recovery ownership changed during topology admission.");
        }
        var updated = current with { HostDevicesDetached = detached };
        if (Volatile.Read(ref _infrastructureRecoveryLeaseArmed) != 0)
            PublishRetainedPendingExec(updated);
        _pendingInterruptedExecRecovery = updated;
        return updated;
    }

    private void PublishRetainedPendingExec(PendingInterruptedExecRecovery pending)
    {
        var durablePending = new IncusRecoveryPendingExec(
            pending.RunId,
            pending.EnvironmentPath,
            pending.PidPath,
            pending.CompletionPath,
            pending.HostDevicesDetached);
        var immutableBase = _recoveryManifest with
        {
            Retained = false,
            PendingExec = null,
        };
        var retained = immutableBase.Retain(durablePending);
        _recoveryManifestStore.WriteRetained(
            retained,
            NextGuid("retained recovery manifest"));
        _recoveryManifest = retained;
    }

    private async Task<bool> EnsureGuestControlFileAbsentAsync(
        string path,
        CancellationToken ct = default)
    {
        var maximumAttempts = ReadRetryAttempts(
            static options => options.ExecControlFileCleanupAttempts,
            nameof(IncusSandboxOptions.ExecControlFileCleanupAttempts));
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            try
            {
                _ = await _cli.RunAllowFailureAsync(
                    _options,
                    IncusCommandBuilder.Prefix(_options, "file", "delete", $"{Id}{path}"),
                    stdin: null,
                    _options.OperationTimeout,
                    ct,
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
                    ct,
                    heavyOperation: false,
                    maxStdoutBytes: 128,
                    maxStderrBytes: 4096).ConfigureAwait(false);
                if (verify.Success)
                    return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
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
                await Task.Delay(_options.ReadinessPollInterval, _timeProvider, ct).ConfigureAwait(false);
        }
        _log.LogWarning(
            "Could not verify transient guest-file cleanup for Incus sandbox {SandboxId}",
            Id);
        return false;
    }

    private async Task<bool> VerifyGuestExecCompletionAsync(string path, int expectedExitCode)
    {
        var maximumAttempts = ReadRetryAttempts(
            static options => options.ExecCompletionProbeAttempts,
            nameof(IncusSandboxOptions.ExecCompletionProbeAttempts));
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
                await Task.Delay(_options.ReadinessPollInterval, _timeProvider, CancellationToken.None).ConfigureAwait(false);
        }
        return false;
    }

    private async Task<bool> VerifyOwnershipOrAbsenceAsync(CancellationToken ct)
        => await ReadOwnedInstanceStatusAsync(ct).ConfigureAwait(false) is not null;

    private async Task WaitForInstanceAbsenceAsync(CancellationToken ct)
    {
        using var timeoutCancellation = new CancellationTokenSource(_options.OperationTimeout, _timeProvider);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCancellation.Token);
        try
        {
            while (await VerifyOwnershipOrAbsenceAsync(deadline.Token).ConfigureAwait(false))
                await Task.Delay(_options.ReadinessPollInterval, _timeProvider, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeoutCancellation.IsCancellationRequested)
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
        return ParseOwnedInstanceStatus(
            instances.Stdout,
            Id,
            _recoveryTokenHash,
            _recoveryBaseManifestHash);
    }

    internal static bool ParseOwnedInstancePresence(string json, string instanceName) =>
        ParseOwnedInstanceStatus(json, instanceName) is not null;

    internal static string? ParseOwnedInstanceStatus(
        string json,
        string instanceName,
        string? expectedRecoveryTokenHash = null,
        string? expectedRecoveryManifestHash = null)
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
        if (expectedRecoveryTokenHash is not null || expectedRecoveryManifestHash is not null)
        {
            if (expectedRecoveryTokenHash is null
                || expectedRecoveryManifestHash is null
                || !config.TryGetProperty(IncusSandboxProvider.RecoveryTokenHashKey, out var tokenHash)
                || !config.TryGetProperty(IncusSandboxProvider.RecoveryManifestHashKey, out var manifestHash)
                || tokenHash.ValueKind != JsonValueKind.String
                || manifestHash.ValueKind != JsonValueKind.String
                || !IncusRecoveryManifestCodec.FixedTimeEqualsHash(
                    tokenHash.GetString() ?? string.Empty,
                    expectedRecoveryTokenHash)
                || !IncusRecoveryManifestCodec.FixedTimeEqualsHash(
                    manifestHash.GetString() ?? string.Empty,
                    expectedRecoveryManifestHash))
            {
                throw new InvalidOperationException(
                    "Refusing Incus lifecycle access because the recovery capability binding changed.");
            }
        }
        return exact.Value.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.String
                ? status.GetString() ?? string.Empty
                : string.Empty;
    }

    private async Task SetConfigAsync(string key, string value, CancellationToken ct)
    {
        await EnsureLifecycleBindingAsync(ct).ConfigureAwait(false);
        await _cli.RunCheckedAsync(
            $"set sandbox config {key}",
            _options,
            IncusCommandBuilder.Prefix(_options, "config", "set", Id, $"{key}={value}"),
            stdin: null,
            _options.OperationTimeout,
            ct).ConfigureAwait(false);
    }

    private async Task EnsureLifecycleBindingAsync(CancellationToken ct)
    {
        if (await ReadOwnedInstanceStatusAsync(ct).ConfigureAwait(false) is null)
            throw new InvalidOperationException("Incus lifecycle target no longer exists.");
    }

    private IReadOnlyList<string> BuildRootExec(IReadOnlyList<string> command)
        => IncusCommandBuilder.BuildRootExec(_options, Id, command);

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
            var uptimeSeconds = ParseMetricDouble(values, "uptime", minimumInclusive: 0);
            var avgCpuPercent = ParseMetricDouble(
                values,
                "cpu",
                minimumInclusive: 0,
                maximumInclusive: 100);
            var peakRamBytes = ParseLong(values, "peak");
            var rxBytes = ParseLong(values, "rx");
            var txBytes = ParseLong(values, "tx");
            var load1 = ParseMetricDouble(values, "load1", minimumInclusive: 0);
            var load5 = ParseMetricDouble(values, "load5", minimumInclusive: 0);
            var load15 = ParseMetricDouble(values, "load15", minimumInclusive: 0);
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
                _timeProvider.GetUtcNow());
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

    internal static double? ParseMetricDouble(
        IReadOnlyDictionary<string, string> values,
        string key,
        double minimumInclusive,
        double? maximumInclusive = null) =>
        values.TryGetValue(key, out var value)
            ? SandboxResourceMetricValidation.ParseFiniteDouble(
                value,
                minimumInclusive,
                maximumInclusive)
            : null;

    private static long? ParseLong(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value)
        && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static IReadOnlyDictionary<string, string> MergeEnvironment(
        IReadOnlyDictionary<string, string> baseline,
        IReadOnlyDictionary<string, string>? extra,
        SandboxExec exec)
    {
        ValidateEnvironment(baseline, nameof(baseline));
        if (extra is not null)
            ValidateEnvironment(extra, nameof(extra));

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        AddEntries(baseline);
        if (extra is not null)
            AddEntries(extra);
        exec.ApplyEnvironmentRemovals(name => result.Remove(name));
        return result;

        void AddEntries(IReadOnlyDictionary<string, string> source)
        {
            foreach (var (key, value) in source)
            {
                if (!result.ContainsKey(key) && result.Count >= MaxExecEnvironmentEntries)
                {
                    throw new ArgumentException(
                        $"Exec environment exceeds {MaxExecEnvironmentEntries} unique entries.",
                        nameof(extra));
                }
                result[key] = value;
            }
        }
    }

    internal static void ValidateEnvironment(
        IReadOnlyDictionary<string, string> environment,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (environment.Count > MaxExecEnvironmentEntries)
        {
            throw new ArgumentException(
                $"Exec environment exceeds {MaxExecEnvironmentEntries} entries.",
                parameterName);
        }

        long bytes = 0;
        foreach (var (key, value) in environment)
        {
            if (key is null || value is null || !IsEnvironmentKey(key))
            {
                throw new ArgumentException(
                    "Exec environment contains an invalid key, null entry, or NUL value.",
                    parameterName);
            }
            var remaining = checked((int)(MaxExecInputUtf8Bytes - bytes));
            var keyBytes = IncusInputValidation.GetBoundedUtf8ByteCount(
                key,
                remaining,
                parameterName,
                "Exec environment key");
            remaining -= keyBytes;
            var valueBytes = IncusInputValidation.GetBoundedUtf8ByteCount(
                value,
                remaining,
                parameterName,
                "Exec environment value");
            if (value.Contains('\0'))
            {
                throw new ArgumentException(
                    "Exec environment contains an invalid key, null entry, or NUL value.",
                    parameterName);
            }
            var entryBytes = (long)keyBytes + valueBytes + 2;
            if (entryBytes > MaxExecInputUtf8Bytes - bytes)
            {
                throw new ArgumentException(
                    "Exec environment exceeds the 16 MiB safety bound.",
                    parameterName);
            }
            bytes += entryBytes;
        }
    }

    internal static string SerializeEnvironment(IReadOnlyDictionary<string, string> environment)
    {
        ValidateEnvironment(environment, nameof(environment));
        var result = new StringBuilder();
        long bytes = 0;
        foreach (var (key, value) in environment.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var remaining = checked((int)(MaxExecInputUtf8Bytes - bytes));
            var keyBytes = IncusInputValidation.GetBoundedUtf8ByteCount(
                key,
                remaining,
                nameof(environment),
                "Exec environment key");
            remaining -= keyBytes;
            var valueBytes = IncusInputValidation.GetBoundedUtf8ByteCount(
                value,
                remaining,
                nameof(environment),
                "Exec environment value");
            var entryBytes = (long)keyBytes + valueBytes + 2;
            if (entryBytes > MaxExecInputUtf8Bytes - bytes)
                throw new ArgumentException("Exec environment exceeds the 16 MiB safety bound.", nameof(environment));
            bytes += entryBytes;
            result.Append(key).Append('=').Append(value).Append('\0');
        }
        return result.ToString();
    }

    private static bool IsEnvironmentKey(string key)
    {
        if (key.Length is < 1 or > MaxExecEnvironmentNameCharacters
            || !(key[0] == '_' || char.IsAsciiLetter(key[0])))
            return false;
        return key.Skip(1).All(c => c == '_' || char.IsAsciiLetterOrDigit(c));
    }

    private void ValidateExec(SandboxExec exec)
    {
        if (exec.Argv.Count is < 1 or > MaxExecArguments)
            throw new ArgumentException($"Exec argv must contain between 1 and {MaxExecArguments} arguments.", nameof(exec));
        long bytes = 0;
        for (var index = 0; index < exec.Argv.Count; index++)
        {
            var argument = exec.Argv[index];
            var argumentBytes = IncusInputValidation.GetBoundedUtf8ByteCount(
                argument,
                checked((int)(MaxExecArgvUtf8Bytes - bytes)),
                nameof(exec),
                $"Exec argv argument {index}");
            if (argument.Contains('\0'))
                throw new ArgumentException($"Exec argv argument {index} contains NUL.", nameof(exec));
            bytes += argumentBytes;
        }
        if (string.IsNullOrEmpty(exec.Argv[0]))
            throw new ArgumentException("Exec executable must not be empty.", nameof(exec));
        if (exec.Stdin is { } stdin)
        {
            _ = IncusInputValidation.GetBoundedUtf8ByteCount(
                stdin,
                MaxExecInputUtf8Bytes,
                nameof(exec),
                "Exec stdin");
        }
        if (exec.ExtraEnvironment is not null)
            ValidateEnvironment(exec.ExtraEnvironment, nameof(exec));
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

    private Guid NextGuid(string purpose)
    {
        var value = _newGuid();
        if (value == Guid.Empty)
            throw new InvalidOperationException($"The injected GUID source returned an empty value for {purpose}.");
        return value;
    }

    private int ReadRetryAttempts(
        Func<IncusSandboxOptions, int> selector,
        string optionName)
    {
        var liveOptions = _liveOptionsAccessor()
            ?? throw new InvalidOperationException("The live Incus options accessor returned null.");
        var attempts = selector(liveOptions);
        if (attempts is < 1 or > IncusSandboxOptions.MaximumExecRetryAttempts)
        {
            throw new InvalidOperationException(
                $"Live Incus option {optionName} must be between 1 and {IncusSandboxOptions.MaximumExecRetryAttempts}.");
        }
        return attempts;
    }

    private InterruptedExecRecoveryPolicy ReadInterruptedExecRecoveryPolicy()
    {
        var liveOptions = _liveOptionsAccessor()
            ?? throw new InvalidOperationException("The live Incus options accessor returned null.");
        var attempts = liveOptions.InterruptedExecRecoveryRetryAttempts;
        if (attempts is < 0 or > IncusSandboxOptions.MaximumInterruptedExecRecoveryRetryAttempts)
        {
            throw new InvalidOperationException(
                $"Live Incus option {nameof(IncusSandboxOptions.InterruptedExecRecoveryRetryAttempts)} must be between 0 and {IncusSandboxOptions.MaximumInterruptedExecRecoveryRetryAttempts}.");
        }

        var delay = liveOptions.InterruptedExecRecoveryRetryDelay;
        if (delay <= TimeSpan.Zero
            || delay > IncusSandboxOptions.MaximumInterruptedExecRecoveryRetryDelay)
        {
            throw new InvalidOperationException(
                $"Live Incus option {nameof(IncusSandboxOptions.InterruptedExecRecoveryRetryDelay)} must be positive and no greater than {IncusSandboxOptions.MaximumInterruptedExecRecoveryRetryDelay}.");
        }

        return new InterruptedExecRecoveryPolicy(attempts, delay);
    }

    private sealed record PendingInterruptedExecRecovery(
        string RunId,
        string EnvironmentPath,
        string PidPath,
        string CompletionPath,
        bool HostDevicesDetached);

    private readonly record struct InterruptedExecRecoveryPolicy(
        int MaximumRetryAttempts,
        TimeSpan RetryDelay);

    private void NotifyNoLongerActive()
    {
        if (Interlocked.Exchange(ref _noLongerActive, 1) != 0)
            return;
        _recoveryAuthorization.Dispose();
        _recoveryManifestStore.Dispose();
        _onDisposed(Id);
        SandboxLiveCounter.Decrement();
    }
}
