using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Sandbox.Incus;

/// <summary>
/// Incus virtual-machine sandbox provider. Work VMs are copied from an immutable,
/// content-addressed <c>baseline/ready</c> snapshot on a snapshot-capable ZFS or
/// Btrfs pool, while host directory devices explicitly require
/// <c>io.bus=virtiofs</c> and never permit Incus's 9p fallback.
/// The conventional <c>/work</c> tmpfs request is provider-locally backed by a
/// private directory on the bounded VM root disk so it survives stop/start;
/// credential storage remains a non-persistent guest tmpfs.
///
/// <para>
/// Host prerequisites: Incus 6.3 or newer, the upstream requirements for the
/// installed release, Linux kernel 5.6 or newer for openat2-backed restricted
/// disk paths, KVM, Rust <c>virtiofsd</c>, membership of the CodeyBox service
/// identity in <c>incus-admin</c>, a dedicated non-default Incus project, and a
/// pre-created ZFS or Btrfs pool. The recommended Incus 7.0 LTS release itself
/// requires Linux 6.12 or newer and QEMU 8.2 or newer. A missing project is
/// created with exact CodeyBox ownership/schema markers and restrictions; an
/// existing project is
/// mutated only when those markers, the shared default-project image catalog,
/// and required
/// project-owned profile feature already match exactly. Low-level VM
/// configuration and VM nesting are blocked, and empty restricted disk paths
/// are never accepted. The staging
/// directory's canonical non-symlink parent must exist; an existing staging
/// root additionally requires the provider ownership marker and exact service
/// ownership/mode. ZFS is strongly recommended for VMs. For the intended I/O
/// isolation, back that pool with a dedicated fast device or dataset outside
/// the host's encrypted root disk.
/// Pre-create and firewall every Linux bridge named by
/// <see cref="IncusSandboxOptions.NetworkProfiles"/>; the provider intentionally
/// creates instances with no default profile or Incus NAT NIC.
/// </para>
///
/// <para>
/// Incus administration is a root-equivalent privilege. Run CodeyBox under a
/// dedicated service identity, keep its staging root private, and do not grant
/// untrusted users access to provider configuration. Host-backed virtiofs mounts
/// require the configured guest UID/GID to match that service's effective host
/// UID/GID; guest root can still access every intentionally attached writable
/// path, so allowed roots must remain narrowly scoped.
/// </para>
/// </summary>
public sealed class IncusSandboxProvider :
    ISandboxProvider,
    IActiveSandboxProvider,
    IActiveSandboxProgressProvider,
    IDiskGuardedSandboxProvider,
    IBaselineImageResolver,
    IBaselineImageProvisioner,
    IResourceMetricsCapturingProvider
{
    public const string ProviderId = "incus";
    internal const string ManagedKey = "user.codeybox.managed";
    internal const string KindKey = "user.codeybox.kind";
    internal const string CreatedAtKey = "user.codeybox.created_at";
    internal const string BaselineHashKey = "user.codeybox.baseline_hash";
    internal const string BaselinePoolKey = "user.codeybox.baseline_pool";
    internal const string BaselineProfileKey = "user.codeybox.baseline_profile";
    internal const string BaselineFlavorKey = "user.codeybox.baseline_flavor";
    internal const string BaselineRefKey = "user.codeybox.baseline_ref";
    internal const string PreemptKey = "user.codeybox.preempt";
    internal const string SuspendedKey = "user.codeybox.suspended";
    internal const string BakeTokenKey = "user.codeybox.bake_token";
    internal const string RecoveryTokenHashKey = "user.codeybox.recovery_token_sha256";
    internal const string RecoveryManifestHashKey = "user.codeybox.recovery_manifest_sha256";
    internal const string SandboxKind = "sandbox";
    internal const string BaselineKind = "baseline";
    internal const string ReadySnapshot = "ready";

    private readonly Func<IncusSandboxOptions> _optionsAccessor;
    private readonly ILogger<IncusSandboxProvider> _log;
    private readonly IncusCliRunner _cli;
    private readonly ITimingStore? _timings;
    private readonly ISandboxResourceUsageStore? _resourceUsageStore;
    private readonly IDiskSpaceProbe _diskProbe;
    private readonly TimeProvider _timeProvider;
    private readonly Func<Guid> _newGuid;
    private readonly Func<string, string?> _environmentVariableReader;
    private readonly string _lifecycleProjectName;
    private readonly string _lifecycleStagingRootPath;
    private readonly SemaphoreSlim _hostPreflightLock = new(1, 1);
    private readonly SemaphoreSlim _hostProvisioningInputGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _baselineLocks = new(StringComparer.Ordinal);
    // Boot gate: staggers concurrent VM boots (incus start + guest-agent wait)
    // so a boot storm does not starve incusd/host and blow the readiness window.
    // Hot-reloadable: the semaphore is recreated when MaxConcurrentBoots changes.
    private readonly object _bootGateGuard = new();
    private SemaphoreSlim? _bootGate;
    private int _bootGateCapacity;
    private readonly ConcurrentDictionary<string, bool> _activeNames = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ActiveOwner> _activeOwners = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _uncertainBaselines = new(StringComparer.Ordinal);
    private long _lastPoolFreeBytes = -1;
    private string? _lastPoolName;

    private sealed record ActiveOwner(WorkItemId WorkItemId, IncusSandbox Sandbox);

    public IncusSandboxProvider(
        IncusSandboxOptions options,
        ILogger<IncusSandboxProvider> log,
        ITimingStore? timings = null,
        ISandboxResourceUsageStore? resourceUsageStore = null)
        : this(() => options, log, timings, new IncusCliProcessRunner(() => options), resourceUsageStore)
    {
    }

    public IncusSandboxProvider(
        Func<IncusSandboxOptions> optionsAccessor,
        ILogger<IncusSandboxProvider> log,
        ITimingStore? timings = null,
        ISandboxResourceUsageStore? resourceUsageStore = null)
        : this(optionsAccessor, log, timings, new IncusCliProcessRunner(optionsAccessor), resourceUsageStore)
    {
    }

    internal IncusSandboxProvider(
        Func<IncusSandboxOptions> optionsAccessor,
        ILogger<IncusSandboxProvider> log,
        ITimingStore? timings,
        IProcessRunner runner,
        ISandboxResourceUsageStore? resourceUsageStore = null,
        IDiskSpaceProbe? diskProbe = null,
        TimeProvider? timeProvider = null,
        Func<Guid>? newGuid = null,
        Func<string, string?>? environmentVariableReader = null)
    {
        _optionsAccessor = optionsAccessor ?? throw new ArgumentNullException(nameof(optionsAccessor));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _timings = timings;
        _resourceUsageStore = resourceUsageStore;
        _diskProbe = diskProbe ?? new DefaultDiskSpaceProbe();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _newGuid = newGuid ?? Guid.NewGuid;
        _environmentVariableReader = environmentVariableReader ?? Environment.GetEnvironmentVariable;
        _cli = new IncusCliRunner(runner, _timeProvider);
        var initialOptions = ReadValidatedOptions();
        _lifecycleProjectName = initialOptions.ProjectName;
        _lifecycleStagingRootPath = ResolveStagingRootPath(initialOptions);
    }

    public string Name => ProviderId;
    public SandboxIsolationLevel IsolationLevel => SandboxIsolationLevel.DedicatedKernel;
    public bool CapturesResourceMetrics => ReadOptions().CaptureResourceMetrics;

    public IReadOnlyList<DiskGuardSample> SampleDiskGuardState()
    {
        var options = ReadOptions();
        if (options.DiskGuard is not { } guard)
            return [];
        var poolName = Volatile.Read(ref _lastPoolName) ?? options.StoragePoolName;
        var cachedFree = Interlocked.Read(ref _lastPoolFreeBytes);
        var samples = new List<DiskGuardSample>
        {
            new($"incus-pool:{poolName}", cachedFree >= 0 ? cachedFree : null, guard.MinFreeBytes),
        };
        foreach (var path in guard.HostPaths.Distinct(StringComparer.Ordinal))
            samples.Add(new DiskGuardSample(path, _diskProbe.GetFreeBytes(path), guard.MinFreeBytes));
        return samples;
    }

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        spec = IncusInputSnapshot.CaptureSpec(spec);
        if (spec.Flavor == SandboxProfileFlavor.Graphical)
            throw new NotSupportedException("The Incus provider currently supports headless work/audit/merge sandboxes only.");
        ValidateResourceLimits(spec.Limits);
        IncusInputValidation.ValidateAbsoluteGuestPath(spec.WorkingDirectory, nameof(spec.WorkingDirectory));
        IncusSandbox.ValidateEnvironment(spec.Environment, nameof(spec));
        spec = SandboxConventions.WithTimingEnvironment(spec);
        IncusSandbox.ValidateEnvironment(spec.Environment, nameof(spec));
        var options = ReadOptions();
        IncusHostIdentity.ValidateHostMountIdentity(options, spec.Mounts);
        ValidateProvisioningMountSeparation(options, spec.Mounts);
        await EnsureHostPreflightAsync(options, ct).ConfigureAwait(false);
        var bridge = ResolveBridge(options, spec.Network.ProfileName);
        var stagingRoot = ResolveStagingRoot(options);
        var timingStore = _timings is not null && spec.TimingWorkItemId.HasValue ? _timings : null;
        var timingItem = spec.TimingWorkItemId.GetValueOrDefault();
        var timingPhase = spec.TimingPhase ?? "work";
        if (spec.RecoveryLease is { } recoveryLease)
        {
            return await AdoptRetainedSandboxAsync(
                spec,
                options,
                bridge,
                stagingRoot,
                timingStore,
                timingItem,
                timingPhase,
                recoveryLease,
                ct).ConfigureAwait(false);
        }

        var image = ResolveImage(options, spec.ImageReference);
        var name = CreateInstanceName(options);
        var sandboxRoot = Path.Combine(stagingRoot, name);
        string? baselineRef = null;
        var instanceBecameVisible = false;
        var stagingInitialized = false;

        MarkActive(name);
        try
        {
            CreateSecurePrivateDirectory(sandboxRoot);
            IncusMountStaging.InitializeOwnedTree(sandboxRoot, name, _timeProvider.GetUtcNow());
            stagingInitialized = true;
            using var mountPlan = IncusMountStaging.Prepare(
                options,
                stagingRoot,
                sandboxRoot,
                spec.Mounts,
                spec.Limits.DiskBytes ?? SandboxResourceLimits.Default.DiskBytes ?? options.BaselineDiskBytes,
                ct);
            var canUseBaseline = IncusProvisioningDecision.Decide(options, spec, baselineExists: true)
                == IncusProvisioningPath.CowCopy;
            if (spec.BaselineImageRef is not null && !canUseBaseline)
                throw new InvalidOperationException("A pinned Incus baseline cannot be used with a custom image or profileless sandbox.");

            if (canUseBaseline)
            {
                // Derive the live name only when unpinned; a pinned baseline that
                // still exists must not re-fingerprint executable inputs. The
                // resolver derives it lazily if the pin is missing, so a
                // not-yet-baked CURRENT baseline still bakes (pin == live name).
                var expected = spec.BaselineImageRef is null
                    ? DeriveLiveBaselineName(
                        options,
                        spec.Network.ProfileName!,
                        spec.Flavor,
                        ct)
                    : null;
                baselineRef = await ResolveOrEnsureBaselineAsync(
                    options,
                    spec.Network.ProfileName!,
                    spec.Flavor,
                    expected,
                    spec.BaselineImageRef,
                    ct).ConfigureAwait(false);
                await using var cloneTiming = await TimingScope.BeginAsync(
                    timingStore, timingItem, timingPhase, "vm.clone", log: _log).ConfigureAwait(false);
                await PreflightStorageAsync(options, ct).ConfigureAwait(false);
                await VerifyBaselinePoolAsync(options, baselineRef, ct).ConfigureAwait(false);
                var copyArgs = IncusCommandBuilder.BuildCopy(options, $"{baselineRef}/{ReadySnapshot}", name).ToList();
                AppendCreationMetadata(copyArgs, SandboxKind, baselineRef, baselineHash: null);
                instanceBecameVisible = true;
                await _cli.RunCheckedAsync(
                    "COW baseline copy",
                    options,
                    copyArgs,
                    stdin: null,
                    options.OperationTimeout,
                    ct).ConfigureAwait(false);
                await ApplyCloneLimitsAsync(options, name, spec.Limits, ct).ConfigureAwait(false);
            }
            else
            {
                await using var launchTiming = await TimingScope.BeginAsync(
                    timingStore, timingItem, timingPhase, "vm.launch", log: _log).ConfigureAwait(false);
                var initArgs = IncusCommandBuilder.BuildInit(options, image, name, spec.Limits).ToList();
                AppendCreationMetadata(initArgs, SandboxKind, baselineRef: null, baselineHash: null);
                instanceBecameVisible = true;
                await _cli.RunCheckedAsync(
                    "VM init",
                    options,
                    initArgs,
                    stdin: null,
                    options.ImageProvisioningTimeout,
                    ct).ConfigureAwait(false);
                if (bridge is not null)
                    await AddNicAsync(options, name, bridge, ct).ConfigureAwait(false);
                await SetCloudInitAsync(options, name, IncusCloudInit.Build(options, spec.Flavor), ct).ConfigureAwait(false);
            }

            var requestedMountPaths = spec.Mounts
                .Select(static mount => mount.SandboxPath)
                .ToArray();
            var canonicalRecoveryPaths = IncusRecoveryAuthorization.BuildCanonicalGuestPaths(
                requestedMountPaths,
                mountPlan.Mounts);
            var hasHostMountDevices = mountPlan.Mounts.Any(static mount => mount.HostSource is not null);
            // Resolve guest paths before attaching any host storage. Otherwise
            // an image symlink such as /opt/alias -> /etc could cause cloud-init
            // or boot services to mutate a host-backed directory on first boot.
            await using (var startTiming = await TimingScope.BeginAsync(
                timingStore, timingItem, timingPhase, "vm.start", log: _log).ConfigureAwait(false))
            {
                await StartAndWaitAsync(options, name, bridge, [], runCloudInit: true, ct).ConfigureAwait(false);
            }
            await ValidateCanonicalProvisioningPathsAsync(
                options,
                name,
                canonicalRecoveryPaths,
                ct).ConfigureAwait(false);
            if (hasHostMountDevices)
            {
                await StopInstanceAsync(options, name, stateful: false, ct).ConfigureAwait(false);
                await using (var mountTiming = await TimingScope.BeginAsync(
                    timingStore, timingItem, timingPhase, "vm.mount", log: _log).ConfigureAwait(false))
                {
                    await ApplyMountDevicesAsync(options, name, mountPlan.Mounts, ct).ConfigureAwait(false);
                }
                await using var restartTiming = await TimingScope.BeginAsync(
                    timingStore, timingItem, timingPhase, "vm.restart-after-mount", log: _log).ConfigureAwait(false);
                await StartAndWaitAsync(
                    options,
                    name,
                    bridge,
                    mountPlan.Mounts,
                    runCloudInit: false,
                    ct).ConfigureAwait(false);
            }
            if (!canUseBaseline)
            {
                await RunExtraRuncmdAsync(options, name, ct).ConfigureAwait(false);
                await RunProvisioningWithPrivateWorkspaceAsync(
                    options,
                    name,
                    expectedExecutableContentSha256: null,
                    mountGuestPaths: requestedMountPaths,
                    ct)
                    .ConfigureAwait(false);
            }
            await ApplyGuestLocalMountsAsync(options, name, mountPlan.Mounts, ct).ConfigureAwait(false);
            await WaitForMountsAsync(options, name, mountPlan.Mounts, ct).ConfigureAwait(false);
            await CreateGuestLinksAsync(options, name, mountPlan.GuestLinks, ct).ConfigureAwait(false);
            foreach (var executableLink in IncusRecoveryAuthorization.SnapshotExecutableLinks(options))
            {
                await IncusGuestLinkLifecycle.VerifyExactAsync(
                    _cli,
                    options,
                    name,
                    executableLink,
                    ct).ConfigureAwait(false);
            }
            // Canonical guest paths and the exact effective topology have now
            // both been proved by the live provider path. Preserve only the
            // immutable authorization evidence needed to fail closed before a
            // later interrupted-exec restart.
            var recoveryAuthorization = IncusRecoveryAuthorization.CaptureValidated(
                bridge,
                mountPlan.Mounts,
                requestedMountPaths,
                mountPlan.GuestLinks,
                options);
            IncusRecoveryManifestStore? recoveryManifestStore = null;
            var recoveryHandedOff = false;
            IncusSandbox sandbox;
            try
            {
                recoveryManifestStore = IncusRecoveryManifestStore.Acquire(
                    sandboxRoot,
                    options.RecoveryLeaseAcquireAttempts,
                    options.RecoveryLeaseAcquireRetryDelay);
                var recoveryToken = NextGuid("sandbox recovery capability").ToString("N");
                var recoveryTokenHash = IncusRecoveryManifestCodec.ComputeTokenSha256(recoveryToken);
                var sandboxRecoveryLease = new SandboxRecoveryLease(ProviderId, name, recoveryToken);
                var recoveryManifest = IncusRecoveryManifest.Create(
                    name,
                    spec,
                    options,
                    recoveryTokenHash,
                    baselineRef,
                    recoveryAuthorization);
                var recoveryManifestHash = recoveryManifestStore.Write(
                    recoveryManifest,
                    NextGuid("sandbox recovery manifest"));
                // Bind the private capability and immutable authorization
                // manifest while the daemon is healthy. Later retention needs
                // no daemon call, which is essential when daemon loss is the
                // infrastructure failure being recovered.
                await SetConfigAsync(
                    options,
                    name,
                    RecoveryTokenHashKey,
                    recoveryTokenHash,
                    ct).ConfigureAwait(false);
                await SetConfigAsync(
                    options,
                    name,
                    RecoveryManifestHashKey,
                    recoveryManifestHash,
                    ct).ConfigureAwait(false);
                sandbox = new IncusSandbox(
                    name,
                    sandboxRoot,
                    stagingRoot,
                    spec,
                    options,
                    _cli,
                    _log,
                    timingStore,
                    timingItem,
                    timingPhase,
                    baselineRef,
                    _resourceUsageStore,
                    MarkInactive,
                    recoveryAuthorization,
                    sandboxRecoveryLease,
                    recoveryManifest,
                    recoveryManifestStore,
                    _timeProvider,
                    _newGuid,
                    ReadOptions);
                recoveryHandedOff = true;
            }
            finally
            {
                if (!recoveryHandedOff)
                {
                    recoveryManifestStore?.Dispose();
                    recoveryAuthorization.Dispose();
                }
            }
            if (spec.TimingWorkItemId is { } workItemId)
                _activeOwners[name] = new ActiveOwner(workItemId, sandbox);
            SandboxLiveCounter.Increment();
            _log.LogInformation("Created Incus sandbox {SandboxName} from {Source}", name, baselineRef ?? image);
            return sandbox;
        }
        catch (Exception ex)
        {
            var retained = false;
            if (instanceBecameVisible)
            {
                var deleted = await TryDeleteOwnedInstanceAsync(
                    options, name, SandboxKind, CancellationToken.None).ConfigureAwait(false);
                retained = !deleted;
            }
            MarkInactive(name);
            if (!retained)
            {
                try
                {
                    if (stagingInitialized)
                        IncusMountStaging.DeleteOwnedTreeIfContained(stagingRoot, sandboxRoot, name);
                    else
                        IncusMountStaging.DeleteTreeIfContained(stagingRoot, sandboxRoot);
                }
                catch (Exception cleanupError)
                {
                    throw new AggregateException(
                        "Incus sandbox creation failed and private staging cleanup also failed.",
                        ex,
                        cleanupError);
                }
                if (TryBuildTransientProvisioningDeferral(ex, options) is { } transientDeferral)
                    throw transientDeferral;
                throw;
            }
            throw new SandboxProvisioningDeferredException(
                Name,
                "create-cleanup",
                "incus-delete-failed",
                $"Creation failed and the provider could not prove retained sandbox '{name}' was removed.",
                options.DiskGuard?.RecheckIn ?? TimeSpan.FromMinutes(5),
                retainedSandboxName: name,
                innerException: ex);
        }
    }

    /// <summary>
    /// Re-shapes a transient Incus liveness timeout (guest-agent readiness or a
    /// CLI operation deadline that tripped under concurrent boot load) into a
    /// <see cref="SandboxProvisioningDeferredException"/> so the recovery stack
    /// re-enqueues the work item as RETRYABLE transient infrastructure. Without
    /// this the raw <see cref="IncusTransientTimeoutException"/> would reach the
    /// orchestrator's catch-all and be stamped as an unclassified failure and
    /// parked for an operator instead of auto-retried. Returns <c>null</c> for
    /// any non-transient failure, which is rethrown unchanged.
    /// </summary>
    internal static SandboxProvisioningDeferredException? TryBuildTransientProvisioningDeferral(
        Exception ex,
        IncusSandboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(ex);
        ArgumentNullException.ThrowIfNull(options);
        if (ex is not IncusTransientTimeoutException transient)
            return null;
        return new SandboxProvisioningDeferredException(
            ProviderId,
            transient.Operation,
            "incus-liveness-timeout",
            transient.Message,
            options.ProvisioningRetryRecheckIn,
            innerException: ex);
    }

    private async Task<ISandbox> AdoptRetainedSandboxAsync(
        SandboxSpec spec,
        IncusSandboxOptions options,
        string? bridge,
        string stagingRoot,
        ITimingStore? timingStore,
        WorkItemId timingItem,
        string timingPhase,
        SandboxRecoveryLease lease,
        CancellationToken ct)
    {
        if (!string.Equals(lease.ProviderId, ProviderId, StringComparison.Ordinal))
            throw new InvalidOperationException("Incus cannot adopt a recovery lease owned by another provider.");
        IncusInputValidation.ValidateInstanceName(lease.SandboxId, nameof(lease));
        var name = lease.SandboxId;
        var sandboxRoot = Path.Combine(stagingRoot, name);
        var ownsStaging = IncusMountStaging.EnumerateOwnedTrees(stagingRoot)
            .Any(tree => string.Equals(tree.Name, name, StringComparison.Ordinal));
        if (!ownsStaging)
            throw new InvalidOperationException("Incus recovery lease has no exact provider-owned staging tree.");

        MarkActive(name);
        IncusRecoveryManifestStore? manifestStore = null;
        IncusRecoveryAuthorization? authorization = null;
        IncusSandbox? sandbox = null;
        var adopted = false;
        try
        {
            manifestStore = IncusRecoveryManifestStore.Acquire(
                sandboxRoot,
                options.RecoveryLeaseAcquireAttempts,
                options.RecoveryLeaseAcquireRetryDelay);
            var instance = await FindInstanceAsync(options, name, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Incus recovery lease VM no longer exists.");
            ValidateRecoveryInstanceOwnership(instance);
            var tokenHash = IncusRecoveryManifestCodec.ComputeTokenSha256(lease.Token);
            var manifestHash = ValidateRecoveryInstanceBinding(instance, tokenHash);
            var baseManifest = manifestStore.Read(manifestHash);
            ValidateRecoveryManifestIdentity(baseManifest, spec, options, name, tokenHash, bridge);
            if (baseManifest.Retained || baseManifest.PendingExec is not null)
                throw new InvalidDataException("Incus config-bound recovery manifest is not an immutable base manifest.");

            var retainedManifest = manifestStore.ReadRetained();
            retainedManifest.ValidatePendingExec();
            ValidateRecoveryManifestIdentity(retainedManifest, spec, options, name, tokenHash, bridge);
            var normalizedRetained = retainedManifest with
            {
                Retained = false,
                PendingExec = null,
            };
            var normalizedHash = IncusRecoveryManifestCodec.ComputeSha256(
                IncusRecoveryManifestCodec.Serialize(normalizedRetained));
            if (!IncusRecoveryManifestCodec.FixedTimeEqualsHash(normalizedHash, manifestHash))
            {
                throw new InvalidDataException(
                    "Incus retained recovery journal does not match the config-bound immutable manifest.");
            }

            // A daemon/host outage may have prevented the original process
            // from proving the stop. The durable DB claim and host flock elect
            // this adopter; force-stop before inspecting or mutating recovery
            // topology, then re-read exact ownership and config binding.
            instance = await ReadRecoveryBoundInstanceAsync(
                options,
                name,
                tokenHash,
                manifestHash,
                ct).ConfigureAwait(false);
            if (!string.Equals(instance.Status, "STOPPED", StringComparison.OrdinalIgnoreCase))
            {
                _ = await _cli.RunAllowFailureAsync(
                    options,
                    IncusCommandBuilder.Prefix(options, "stop", name, "--force"),
                    stdin: null,
                    options.VmStopTimeout + options.OperationTimeout,
                    ct,
                    heavyOperation: true,
                    maxStdoutBytes: 4096,
                    maxStderrBytes: 4096).ConfigureAwait(false);
            }
            instance = await ReadRecoveryBoundInstanceAsync(
                options,
                name,
                tokenHash,
                manifestHash,
                ct).ConfigureAwait(false);
            if (!string.Equals(instance.Status, "STOPPED", StringComparison.OrdinalIgnoreCase))
                throw new SandboxExecutionUnavailableException(255);

            authorization = retainedManifest.RestoreAuthorization(options);
            authorization.RevalidateForRestart(spec, options, stagingRoot);
            sandbox = new IncusSandbox(
                name,
                sandboxRoot,
                stagingRoot,
                spec,
                options,
                _cli,
                _log,
                timingStore,
                timingItem,
                timingPhase,
                retainedManifest.BaselineRef,
                _resourceUsageStore,
                MarkInactive,
                authorization,
                lease,
                retainedManifest,
                manifestStore,
                _timeProvider,
                _newGuid,
                ReadOptions);
            authorization = null;
            manifestStore = null;
            await sandbox.RecoverRetainedForAdoptionAsync(ct).ConfigureAwait(false);
            if (spec.TimingWorkItemId is { } workItemId)
                _activeOwners[name] = new ActiveOwner(workItemId, sandbox);
            SandboxLiveCounter.Increment();
            adopted = true;
            _log.LogInformation("Adopted retained Incus sandbox {SandboxName} after infrastructure recovery", name);
            return sandbox;
        }
        finally
        {
            if (!adopted)
            {
                sandbox?.ReleaseFailedAdoptionHandle();
                authorization?.Dispose();
                manifestStore?.Dispose();
                MarkInactive(name);
            }
        }
    }

    private static void ValidateRecoveryInstanceOwnership(IncusInstanceInfo instance)
    {
        if (!IsOwned(instance, SandboxKind))
            throw new InvalidOperationException("Incus recovery lease VM ownership metadata changed.");
    }

    private async Task<IncusInstanceInfo> ReadRecoveryBoundInstanceAsync(
        IncusSandboxOptions options,
        string name,
        string expectedTokenHash,
        string expectedManifestHash,
        CancellationToken ct)
    {
        var instance = await FindInstanceAsync(options, name, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Incus recovery lease VM disappeared during adoption fencing.");
        ValidateRecoveryInstanceOwnership(instance);
        _ = ValidateRecoveryInstanceBinding(instance, expectedTokenHash, expectedManifestHash);
        return instance;
    }

    private static string ValidateRecoveryInstanceBinding(
        IncusInstanceInfo instance,
        string expectedTokenHash,
        string? expectedManifestHash = null)
    {
        var actualTokenHash = GetConfig(instance.Config, RecoveryTokenHashKey);
        var actualManifestHash = GetConfig(instance.Config, RecoveryManifestHashKey);
        if (actualTokenHash is null
            || actualManifestHash is null
            || !IncusRecoveryManifestCodec.FixedTimeEqualsHash(actualTokenHash, expectedTokenHash)
            || (expectedManifestHash is not null
                && !IncusRecoveryManifestCodec.FixedTimeEqualsHash(actualManifestHash, expectedManifestHash)))
        {
            throw new InvalidOperationException("Incus recovery lease does not match the VM's creation-time capability binding.");
        }
        IncusRecoveryManifestCodec.ValidateHash(actualManifestHash, RecoveryManifestHashKey);
        return actualManifestHash;
    }

    private static void ValidateRecoveryManifestIdentity(
        IncusRecoveryManifest manifest,
        SandboxSpec spec,
        IncusSandboxOptions options,
        string name,
        string tokenHash,
        string? bridge)
    {
        if (manifest.Version != IncusRecoveryManifest.CurrentVersion
            || !string.Equals(manifest.ProviderId, ProviderId, StringComparison.Ordinal)
            || !string.Equals(manifest.SandboxId, name, StringComparison.Ordinal)
            || !string.Equals(manifest.ProjectName, options.ProjectName, StringComparison.Ordinal)
            || !string.Equals(manifest.StoragePoolName, options.StoragePoolName, StringComparison.Ordinal)
            || !string.Equals(manifest.GuestHome, options.GuestHome, StringComparison.Ordinal)
            || manifest.GuestUserId != options.GuestUserId
            || manifest.GuestGroupId != options.GuestGroupId
            || !string.Equals(manifest.Bridge, bridge, StringComparison.Ordinal)
            || !IncusRecoveryManifestCodec.FixedTimeEqualsHash(manifest.LeaseTokenSha256, tokenHash))
        {
            throw new InvalidDataException("Incus recovery manifest identity does not match the requested adoption.");
        }
        var specHash = IncusRecoveryManifestCodec.ComputeSpecSha256(spec);
        if (!IncusRecoveryManifestCodec.FixedTimeEqualsHash(manifest.SpecSha256, specHash))
            throw new InvalidDataException("Incus recovery manifest belongs to a different sandbox specification or work item.");
    }

    public string? ResolveBaselineRef(string? profileName, SandboxProfileFlavor flavor)
    {
        var options = ReadOptions();
        if (!options.UseBaselineImages || profileName is null || profileName.Length == 0)
            return null;
        if (profileName.Length > 63)
            throw new ArgumentException("The Incus network profile name exceeds 63 characters.", nameof(profileName));
        if (string.IsNullOrWhiteSpace(profileName))
            return null;
        IncusInputValidation.ValidateDeviceName(profileName, nameof(profileName));
        if (!options.NetworkProfiles.ContainsKey(profileName))
            return null;
        if (flavor == SandboxProfileFlavor.Graphical)
            return null;
        return DeriveLiveBaselineName(
            options,
            profileName,
            flavor,
            CancellationToken.None);
    }

    private string DeriveLiveBaselineName(
        IncusSandboxOptions options,
        string profileName,
        SandboxProfileFlavor flavor,
        CancellationToken ct)
    {
        var fingerprints = FingerprintExecutableInputs(options, ct);
        return IncusBaselineNaming.DeriveBaselineName(
            options,
            profileName,
            flavor,
            executableContentSha256: fingerprints);
    }

    private IReadOnlyList<string> FingerprintExecutableInputs(
        IncusSandboxOptions options,
        CancellationToken ct)
    {
        if (options.ExecutableProvisions.Count == 0)
            return [];

        using var timeoutCancellation = new CancellationTokenSource(
            options.ImageProvisioningTimeout,
            _timeProvider);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            timeoutCancellation.Token);
        var gateHeld = false;
        try
        {
            _hostProvisioningInputGate.Wait(deadline.Token);
            gateHeld = true;
            return IncusBaselineProvisioning.FingerprintExecutables(
                options,
                _environmentVariableReader,
                deadline.Token);
        }
        catch (OperationCanceledException ex) when (
            !ct.IsCancellationRequested && timeoutCancellation.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Incus executable fingerprinting exceeded its " +
                $"{options.ImageProvisioningTimeout.TotalSeconds:F0}-second deadline.",
                ex);
        }
        finally
        {
            if (gateHeld)
                _hostProvisioningInputGate.Release();
        }
    }

    public static bool IsOwnedBaselineRef(
        IncusSandboxOptions options,
        string baselineRef)
    {
        ArgumentNullException.ThrowIfNull(options);
        return IsOwnedBaselineRef(options.BaselineNamePrefix, baselineRef);
    }

    /// <summary>
    /// Classifies a baseline reference from namespace configuration alone.
    /// Invalid or reserved-overlapping dormant-provider configuration is not
    /// considered an owned namespace and never materializes provider state.
    /// </summary>
    public static bool IsOwnedBaselineRef(
        string? baselineNamePrefix,
        string baselineRef)
    {
        if (baselineRef is null || baselineRef.Length is < 1 or > 63)
            return false;
        if (string.IsNullOrWhiteSpace(baselineRef))
            return false;
        try { IncusInputValidation.ValidateInstanceName(baselineRef, nameof(baselineRef)); }
        catch (ArgumentException) { return false; }
        if (!TryNormalizeOwnedBaselinePrefix(baselineNamePrefix, out var prefix))
            return false;
        if (!baselineRef.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var tail = baselineRef[prefix.Length..];
        var hashSeparator = tail.LastIndexOf('-');
        if (hashSeparator < 1)
            return false;
        var stem = tail[..hashSeparator];
        var profileLength = stem.EndsWith("-headless", StringComparison.Ordinal)
            ? stem.Length - "-headless".Length
            : stem.EndsWith("-gui", StringComparison.Ordinal)
                ? stem.Length - "-gui".Length
                : 0;
        var hash = tail[(hashSeparator + 1)..];
        return profileLength > 0
            && hash.Length == 12
            && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    /// <summary>
    /// Classifies the stable structural shape of a durable Incus baseline pin
    /// for cross-provider routing. This deliberately ignores the live prefix so
    /// pins survive prefix edits and process restarts. It is not proof that an
    /// instance exists or is owned; the provider verifies exact metadata,
    /// profile, flavor, pool, and ready snapshot before use.
    /// </summary>
    public static bool IsRoutableBaselineRef(string baselineRef)
    {
        if (baselineRef is null || baselineRef.Length is < 1 or > 63)
            return false;
        if (string.IsNullOrWhiteSpace(baselineRef))
            return false;
        try { IncusInputValidation.ValidateInstanceName(baselineRef, nameof(baselineRef)); }
        catch (ArgumentException) { return false; }
        var hashSeparator = baselineRef.LastIndexOf('-');
        if (hashSeparator < 1)
            return false;
        var stem = baselineRef[..hashSeparator];
        var prefixAndProfileLength = stem.EndsWith("-headless", StringComparison.Ordinal)
            ? stem.Length - "-headless".Length
            : stem.EndsWith("-gui", StringComparison.Ordinal)
                ? stem.Length - "-gui".Length
                : 0;
        var hash = baselineRef[(hashSeparator + 1)..];
        return prefixAndProfileLength > 0
            && hash.Length == 12
            && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    /// <summary>
    /// Normalizes one configured baseline namespace for durable-pin routing.
    /// Invalid and reserved bake-candidate-overlapping prefixes fail closed.
    /// </summary>
    private static bool TryNormalizeOwnedBaselinePrefix(
        string? baselineNamePrefix,
        out string effectivePrefix)
    {
        if (!IncusBaselineNaming.TryNormalizeEffectivePrefix(baselineNamePrefix, out effectivePrefix)
            || IncusBaselineNaming.OverlapsBakeCandidateNamespace(effectivePrefix))
        {
            effectivePrefix = string.Empty;
            return false;
        }
        return true;
    }

    public async Task<string?> EnsureBaselineImageAsync(
        string profileName,
        SandboxProfileFlavor flavor,
        string? pinnedBaselineRef,
        CancellationToken ct)
    {
        var options = ReadOptions();
        if (!options.UseBaselineImages || flavor == SandboxProfileFlavor.Graphical)
            return null;
        await EnsureHostPreflightAsync(options, ct).ConfigureAwait(false);
        _ = ResolveBridge(options, profileName);
        if (pinnedBaselineRef is not null && string.IsNullOrWhiteSpace(pinnedBaselineRef))
            throw new ArgumentException("A pinned Incus baseline reference cannot be blank.", nameof(pinnedBaselineRef));
        // Only derive the live content-addressed name when unpinned. A pinned ref
        // that still exists must not re-fingerprint executable inputs; the
        // resolver derives the live name lazily only if the pin turns out to be
        // missing (to tell a not-yet-baked CURRENT baseline from a stale pin).
        var expected = pinnedBaselineRef is null
            ? DeriveLiveBaselineName(
                options,
                profileName,
                flavor,
                ct)
            : null;
        return await ResolveOrEnsureBaselineAsync(
            options,
            profileName,
            flavor,
            expected,
            pinnedBaselineRef,
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
    {
        var options = ReadOptions();
        var instances = await RequireManagedProjectIfPresentAsync(options, ct).ConfigureAwait(false)
            ? await ListInstancesAsync(options, ct).ConfigureAwait(false)
            : [];
        var listed = instances
            .Where(instance => IsOwned(instance, SandboxKind))
            .Select(instance => new ManagedSandboxInfo(
                instance.Name,
                ParseCreatedAt(instance.Config),
                DiskBytes: null,
                IsTrackedActive: _activeNames.ContainsKey(instance.Name),
                HasPreemptMarker: GetConfig(instance.Config, PreemptKey) == "true",
                IsSuspendLifecycleOrFrozen: GetConfig(instance.Config, SuspendedKey) == "true"))
            .ToList();
        var known = listed.Select(static sandbox => sandbox.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var staging in IncusMountStaging.EnumerateOwnedTrees(ResolveStagingRoot(options)))
        {
            if (known.Add(staging.Name))
            {
                listed.Add(new ManagedSandboxInfo(
                    staging.Name,
                    staging.CreatedAt,
                    DiskBytes: null,
                    IsTrackedActive: _activeNames.ContainsKey(staging.Name)));
            }
        }
        return listed;
    }

    public async Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        var options = ReadOptions();
        IncusInputValidation.ValidateInstanceName(name, nameof(name));
        if (_activeNames.ContainsKey(name))
            throw new InvalidOperationException("Refusing to dispose an Incus sandbox tracked as active.");
        var stagingRoot = ResolveStagingRoot(options);
        var sandboxRoot = Path.Combine(stagingRoot, name);
        var instance = await RequireManagedProjectIfPresentAsync(options, ct).ConfigureAwait(false)
            ? await FindInstanceAsync(options, name, ct).ConfigureAwait(false)
            : null;
        var ownsStaging = IncusMountStaging.EnumerateOwnedTrees(stagingRoot)
            .Any(tree => string.Equals(tree.Name, name, StringComparison.Ordinal));
        if ((instance is null || !IsOwned(instance, SandboxKind)) && !ownsStaging)
            throw new InvalidOperationException("Refusing to delete an instance not owned by this Incus provider.");
        if (instance is not null)
        {
            if (!IsOwned(instance, SandboxKind))
                throw new InvalidOperationException("Refusing to delete an instance not owned by this Incus provider.");
            // Active names are registered before an instance can become
            // visible, and Incus rejects duplicate instance names. Rechecking
            // immediately beside the destructive sink therefore closes the
            // only inactive-to-active transition relevant to this name.
            if (_activeNames.ContainsKey(name))
                throw new InvalidOperationException("Refusing to dispose an Incus sandbox that became active.");
            await DeleteVerifiedOwnedInstanceAsync(options, name, SandboxKind, expectedBakeToken: null, ct).ConfigureAwait(false);
        }
        if (_activeNames.ContainsKey(name))
            throw new InvalidOperationException("Refusing to delete staging for an Incus sandbox that became active.");
        IncusMountStaging.DeleteOwnedTreeIfContained(stagingRoot, sandboxRoot, name);
        MarkInactive(name);
    }

    public async Task<IReadOnlyList<BaselineImageInfo>> ListBaselineImagesAsync(CancellationToken ct)
    {
        var options = ReadOptions();
        if (!await RequireManagedProjectIfPresentAsync(options, ct).ConfigureAwait(false))
            return [];
        var instances = await ListInstancesAsync(options, ct).ConfigureAwait(false);
        var listed = instances
            .Where(instance => IsOwned(instance, BaselineKind))
            .Select(instance => new BaselineImageInfo(instance.Name, ParseCreatedAt(instance.Config), DiskBytes: null))
            .ToList();
        var known = listed.Select(static baseline => baseline.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (name, createdAt) in _uncertainBaselines)
        {
            if (known.Add(name))
                listed.Add(new BaselineImageInfo(name, createdAt, DiskBytes: null));
        }
        return listed;
    }

    public async Task DisposeBaselineImageAsync(string name, CancellationToken ct)
    {
        var options = ReadOptions();
        IncusInputValidation.ValidateInstanceName(name, nameof(name));
        var baseline = await RequireManagedProjectIfPresentAsync(options, ct).ConfigureAwait(false)
            ? await FindInstanceAsync(options, name, ct).ConfigureAwait(false)
            : null;
        if (baseline is null && _uncertainBaselines.TryRemove(name, out _))
            return;
        if (baseline is null || !IsOwned(baseline, BaselineKind))
            throw new InvalidOperationException("Refusing to delete a baseline not owned by this Incus provider.");
        if (name.StartsWith(IncusBaselineNaming.BakeCandidatePrefix, StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(GetConfig(baseline.Config, BakeTokenKey)))
            throw new InvalidOperationException("Refusing to delete a bake candidate without its ownership token.");
        await DeleteVerifiedOwnedInstanceAsync(
            options,
            name,
            BaselineKind,
            name.StartsWith(IncusBaselineNaming.BakeCandidatePrefix, StringComparison.Ordinal)
                ? GetConfig(baseline.Config, BakeTokenKey)
                : null,
            ct).ConfigureAwait(false);
        _uncertainBaselines.TryRemove(name, out _);
    }

    public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes() =>
        _activeOwners.Values
            .Select(owner => (owner.WorkItemId, (IShutdownTeardownSandbox)owner.Sandbox))
            .ToArray();

    public IReadOnlyList<ActiveSandboxProgress> SnapshotActiveSandboxProgress() =>
        _activeOwners.Values
            .Select(owner => new ActiveSandboxProgress(owner.WorkItemId, owner.Sandbox.Id, "incus-running"))
            .ToArray();

    private async Task<string> ResolveOrEnsureBaselineAsync(
        IncusSandboxOptions options,
        string profileName,
        SandboxProfileFlavor flavor,
        string? liveBaselineName,
        string? pinnedBaselineRef,
        CancellationToken ct)
    {
        if (pinnedBaselineRef is null)
        {
            if (liveBaselineName is null)
                throw new InvalidOperationException("An unpinned Incus baseline requires a live content-addressed name.");
            return await EnsureBaselineAsync(
                options,
                profileName,
                flavor,
                liveBaselineName,
                ct).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(pinnedBaselineRef))
            throw new ArgumentException("A pinned Incus baseline reference cannot be blank.", nameof(pinnedBaselineRef));
        IncusInputValidation.ValidateInstanceName(pinnedBaselineRef, nameof(pinnedBaselineRef));
        var pinned = await FindInstanceAsync(options, pinnedBaselineRef, ct).ConfigureAwait(false);
        if (pinned is null)
        {
            // A pin that names the CURRENT content-addressed baseline is not
            // stale — it simply has not been baked yet (fresh cutover, a config
            // change that produced a new hash, or a deleted baseline). Bake it.
            // Only a pin that DIFFERS from the live name is a genuinely stale or
            // foreign ref whose configuration we must not bake under. Derive the
            // live name lazily (only now, on a missing pin) so a pinned baseline
            // that still exists never re-fingerprints executable inputs.
            var live = liveBaselineName ?? DeriveLiveBaselineName(options, profileName, flavor, ct);
            if (live is not null
                && string.Equals(pinnedBaselineRef, live, StringComparison.Ordinal))
            {
                return await EnsureBaselineAsync(
                    options,
                    profileName,
                    flavor,
                    live,
                    ct).ConfigureAwait(false);
            }
            throw new InvalidOperationException(
                $"Pinned Incus baseline '{pinnedBaselineRef}' no longer exists; refusing to bake current configuration under a stale ref.");
        }
        if (!IsOwned(pinned, BaselineKind)
            || !string.Equals(GetConfig(pinned.Config, BaselineProfileKey), profileName, StringComparison.Ordinal)
            || !string.Equals(GetConfig(pinned.Config, BaselineFlavorKey), flavor.ToString(), StringComparison.Ordinal)
            || !string.Equals(GetConfig(pinned.Config, BaselinePoolKey), options.StoragePoolName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Pinned Incus baseline '{pinnedBaselineRef}' is not owned and bound to the requested network profile, flavor, and storage pool.");
        }
        if (!string.Equals(pinned.Status, "STOPPED", StringComparison.OrdinalIgnoreCase)
            || !await SnapshotExistsAsync(options, pinnedBaselineRef, ReadySnapshot, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Pinned Incus baseline '{pinnedBaselineRef}' is not a stopped baseline with an immutable ready snapshot.");
        }
        return pinnedBaselineRef;
    }

    private async Task<string> EnsureBaselineAsync(
        IncusSandboxOptions options,
        string profileName,
        SandboxProfileFlavor flavor,
        string baselineName,
        CancellationToken ct)
    {
        var gate = _baselineLocks.GetOrAdd(baselineName, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var executableFingerprints = FingerprintExecutableInputs(options, ct);
            var baselineHash = IncusBaselineNaming.ComputeConfigHash(
                options,
                profileName,
                flavor,
                _environmentVariableReader,
                ct,
                executableFingerprints);
            if (!string.Equals(
                    IncusBaselineNaming.DeriveBaselineNameFromHash(options, profileName, flavor, baselineHash),
                    baselineName,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Incus executable provisioning content changed after the baseline reference was resolved; retry with a freshly resolved reference.");
            }
            var existing = await FindInstanceAsync(options, baselineName, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!IsOwned(existing, BaselineKind)
                    || GetConfig(existing.Config, BaselineHashKey) != baselineHash
                    || GetConfig(existing.Config, BaselinePoolKey) != options.StoragePoolName)
                    throw new InvalidOperationException($"Incus baseline name collision for '{baselineName}'.");
                if (!string.Equals(existing.Status, "STOPPED", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Published Incus baseline '{baselineName}' is not stopped.");
                if (await SnapshotExistsAsync(options, baselineName, ReadySnapshot, ct).ConfigureAwait(false))
                    return baselineName;
                throw new InvalidOperationException($"Published Incus baseline '{baselineName}' has no immutable ready snapshot.");
            }

            var limits = new SandboxResourceLimits
            {
                CpuCount = options.BaselineCpus,
                MemoryBytes = options.BaselineMemoryBytes,
                DiskBytes = options.BaselineDiskBytes,
            };
            var bakeToken = NextGuid("baseline bake token").ToString("N");
            var candidateName = CreateBakeCandidateName(baselineName, bakeToken);
            var candidateMayExist = false;
            var published = false;
            Exception? bakeFailure = null;
            try
            {
                var initArgs = IncusCommandBuilder.BuildInit(options, options.DefaultImage, candidateName, limits).ToList();
                AppendCreationMetadata(initArgs, BaselineKind, baselineRef: null, baselineHash);
                AddConfig(initArgs, BaselineProfileKey, profileName);
                AddConfig(initArgs, BaselineFlavorKey, flavor.ToString());
                AddConfig(initArgs, BaselinePoolKey, options.StoragePoolName);
                AddConfig(initArgs, BakeTokenKey, bakeToken);
                candidateMayExist = true;
                await _cli.RunCheckedAsync(
                    "baseline init",
                    options,
                    initArgs,
                    stdin: null,
                    options.ImageProvisioningTimeout,
                    ct).ConfigureAwait(false);
                var bridge = ResolveBridge(options, profileName)
                    ?? throw new InvalidOperationException("A baseline requires a mapped network profile.");
                await AddNicAsync(options, candidateName, bridge, ct).ConfigureAwait(false);
                await SetCloudInitAsync(options, candidateName, IncusCloudInit.Build(options, flavor), ct).ConfigureAwait(false);
                await StartAndWaitAsync(
                    options,
                    candidateName,
                    bridge,
                    [],
                    runCloudInit: true,
                    ct).ConfigureAwait(false);
                await RunExtraRuncmdAsync(options, candidateName, ct).ConfigureAwait(false);
                await RunProvisioningWithPrivateWorkspaceAsync(
                    options,
                    candidateName,
                    executableFingerprints,
                    mountGuestPaths: [],
                    ct).ConfigureAwait(false);
                // A copied VM receives a fresh cloud-init instance ID. Replacing user-data
                // and cleaning cloud-init state prevents installer data and logs from
                // persisting in the shared snapshot or commands re-running on clones.
                await SetCloudInitAsync(options, candidateName, "#cloud-config\n", ct).ConfigureAwait(false);
                await CleanCloudInitStateAsync(options, candidateName, ct).ConfigureAwait(false);
                await StopInstanceAsync(options, candidateName, stateful: false, ct).ConfigureAwait(false);
                await _cli.RunCheckedAsync(
                    "create immutable baseline snapshot",
                    options,
                    IncusCommandBuilder.Prefix(options, "snapshot", "create", candidateName, ReadySnapshot),
                    stdin: null,
                    options.OperationTimeout,
                    ct).ConfigureAwait(false);
                var publish = await _cli.RunAllowFailureAsync(
                    options,
                    IncusCommandBuilder.Prefix(options, "move", candidateName, baselineName),
                    stdin: null,
                    options.OperationTimeout,
                    ct,
                    heavyOperation: true).ConfigureAwait(false);
                if (!publish.Success)
                {
                    var winner = await FindInstanceAsync(options, baselineName, ct).ConfigureAwait(false);
                    if (winner is null
                        || !IsOwned(winner, BaselineKind)
                        || GetConfig(winner.Config, BaselineHashKey) != baselineHash
                        || GetConfig(winner.Config, BaselinePoolKey) != options.StoragePoolName
                        || !string.Equals(winner.Status, "STOPPED", StringComparison.OrdinalIgnoreCase)
                        || !await SnapshotExistsAsync(options, baselineName, ReadySnapshot, ct).ConfigureAwait(false))
                        throw new InvalidOperationException($"Failed to publish Incus baseline '{baselineName}' and no valid concurrent winner exists.");
                    return baselineName;
                }
                published = true;
                candidateMayExist = false;
                await _cli.RunAllowFailureAsync(
                    options,
                    IncusCommandBuilder.Prefix(options, "config", "unset", baselineName, BakeTokenKey),
                    stdin: null,
                    options.OperationTimeout,
                    CancellationToken.None,
                    heavyOperation: true,
                    maxStdoutBytes: 4096,
                    maxStderrBytes: 4096).ConfigureAwait(false);
                _log.LogInformation("Baked Incus baseline {BaselineName} for profile {ProfileName}", baselineName, profileName);
                return baselineName;
            }
            catch (Exception ex)
            {
                bakeFailure = ex;
                throw;
            }
            finally
            {
                if (!published && candidateMayExist)
                {
                    var deleted = await TryDeleteBakeCandidateAsync(options, candidateName, bakeToken).ConfigureAwait(false);
                    if (!deleted && bakeFailure is not null)
                    {
                        _uncertainBaselines.TryAdd(candidateName, _timeProvider.GetUtcNow());
                        throw new SandboxProvisioningDeferredException(
                            Name,
                            "baseline-cleanup",
                            "incus-baseline-delete-unconfirmed",
                            $"Baseline bake failed and candidate '{candidateName}' may still appear or require cleanup.",
                            options.DiskGuard?.RecheckIn ?? TimeSpan.FromMinutes(5),
                            retainedSandboxName: candidateName,
                            innerException: bakeFailure);
                    }
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task EnsureHostPreflightAsync(IncusSandboxOptions options, CancellationToken ct)
    {
        await _hostPreflightLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stagingRootPath = ResolveStagingRootPath(options);
            var needsProvisioningStaging = options.ExecutableProvisions.Count > 0
                || options.PackageCacheSeeds.Count > 0
                || options.BaselineVerificationCommands.Count > 0;
            if (needsProvisioningStaging
                || Directory.Exists(stagingRootPath)
                || File.Exists(stagingRootPath))
            {
                var stagingRoot = ResolveStagingRoot(options);
                try
                {
                    _ = await IncusProvisioningWorkspace.RecoverStaleWorkspacesAsync(
                        stagingRoot,
                        options.OperationTimeout,
                        options.ReadinessPollInterval,
                        ct).ConfigureAwait(false);
                }
                catch (IOException contended) when (contended.InnerException is TimeoutException)
                {
                    // Stale-workspace recovery is opportunistic cleanup guarded by a
                    // cross-process lease. A concurrent provisioning/recovery pass —
                    // or a transient lease hold from an unrelated fork inheriting the
                    // descriptor — must not fail preflight; the next operation retries.
                    _log.LogDebug(
                        contended,
                        "Skipping Incus provisioning-workspace recovery this pass; the coordination lease is held.");
                }
            }
            var server = await _cli.RunCheckedAsync(
                "server API probe",
                options,
                [options.BinaryPath, "query", "/1.0"],
                stdin: null,
                options.OperationTimeout,
                ct,
                heavyOperation: false).ConfigureAwait(false);
            IncusProjectSecurity.EnsureServerCapabilities(server.Stdout);
            await EnsureProjectAsync(options, ct).ConfigureAwait(false);
            await PreflightStorageAsync(options, ct).ConfigureAwait(false);
        }
        finally
        {
            _hostPreflightLock.Release();
        }
    }

    private async Task PreflightStorageAsync(IncusSandboxOptions options, CancellationToken ct)
    {
        var pools = await _cli.RunCheckedAsync(
                "storage pool list",
                options,
                IncusCommandBuilder.Prefix(options, "storage", "list", "--format=json"),
                stdin: null,
                options.OperationTimeout,
                ct,
                heavyOperation: false).ConfigureAwait(false);
        var pool = ParseStoragePool(pools.Stdout, options.StoragePoolName)
            ?? throw new InvalidOperationException($"Configured Incus storage pool '{options.StoragePoolName}' does not exist.");
        if (pool.Driver is not ("zfs" or "btrfs"))
            throw new InvalidOperationException("Incus storage pool must use the ZFS or Btrfs snapshot driver.");
        if (pool.Driver == "zfs"
                && pool.Config.TryGetValue("zfs.clone_copy", out var cloneCopy)
                && !string.Equals(cloneCopy, "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Incus ZFS pool must leave zfs.clone_copy at its default or set it to true; other modes do not guarantee lightweight COW copies.");
        if (pool.Driver == "btrfs")
            _log.LogWarning("Incus pool {StoragePool} uses Btrfs; Incus recommends ZFS rather than Btrfs for VM workloads", pool.Name);
        if (options.DiskGuard is not { } guard)
            return;
        var resources = await _cli.RunCheckedAsync(
                "storage pool resource probe",
                options,
                [options.BinaryPath, "query", $"/1.0/storage-pools/{options.StoragePoolName}/resources"],
                stdin: null,
                options.OperationTimeout,
                ct,
                heavyOperation: false,
                maxStdoutBytes: 64 * 1024,
                maxStderrBytes: 4096).ConfigureAwait(false);
        using var document = JsonDocument.Parse(resources.Stdout);
        var space = document.RootElement.GetProperty("space");
        var total = space.GetProperty("total").GetInt64();
        var used = space.GetProperty("used").GetInt64();
        var free = CalculateStorageFreeBytes(total, used);
        Interlocked.Exchange(ref _lastPoolFreeBytes, free);
        Volatile.Write(ref _lastPoolName, options.StoragePoolName);
        if (free < guard.MinFreeBytes)
            throw new SandboxDiskDeferredException(
                $"incus-pool:{options.StoragePoolName}",
                free,
                guard.MinFreeBytes,
                guard.RecheckIn);
        foreach (var path in guard.HostPaths.Distinct(StringComparer.Ordinal))
        {
            var hostFree = _diskProbe.GetFreeBytes(path);
            if (hostFree is { } available && available < guard.MinFreeBytes)
                throw new SandboxDiskDeferredException(path, available, guard.MinFreeBytes, guard.RecheckIn);
        }
    }

    internal static long CalculateStorageFreeBytes(long total, long used)
    {
        if (total < 0 || used < 0 || used > total)
        {
            throw new InvalidOperationException(
                "Incus storage resource data reported an invalid total/used byte relationship.");
        }
        return total - used;
    }

    private async Task VerifyBaselinePoolAsync(
        IncusSandboxOptions options,
        string baselineName,
        CancellationToken ct)
    {
        var actualPool = await _cli.RunCheckedAsync(
            "verify baseline root storage pool",
            options,
            IncusCommandBuilder.Prefix(
                options,
                "config", "device", "get", baselineName, "root", "pool"),
            stdin: null,
            options.OperationTimeout,
            ct,
            heavyOperation: false,
            maxStdoutBytes: 256,
            maxStderrBytes: 4096).ConfigureAwait(false);
        EnsurePoolLocalClone(actualPool.Stdout, options.StoragePoolName);
    }

    internal static void EnsurePoolLocalClone(string actualPoolOutput, string expectedPool)
    {
        if (!string.Equals(actualPoolOutput.Trim(), expectedPool, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Incus baseline root is not on configured pool '{expectedPool}'; refusing a cross-pool full copy.");
        }
    }

    private async Task EnsureProjectAsync(IncusSandboxOptions options, CancellationToken ct)
    {
        IncusInputValidation.ValidateOptionsIdentity(options);
        var stagingRoot = ResolveStagingRootPath(options);
        var requiredRoots = IncusProjectSecurity.ResolveRequiredRoots(options, stagingRoot);
        if (!await ProjectExistsAsync(options, ct).ConfigureAwait(false))
        {
            var create = await _cli.RunAllowFailureAsync(
                options,
                IncusProjectSecurity.BuildCreateArguments(options, requiredRoots),
                stdin: null,
                options.OperationTimeout,
                ct,
                heavyOperation: true).ConfigureAwait(false);
            if (!create.Success && !await ProjectExistsAsync(options, ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"Incus restricted-project creation failed with exit code {create.ExitCode}.");
            }
        }

        var project = await ReadProjectSecurityAsync(options, ct).ConfigureAwait(false);
        if (!IncusProjectSecurity.IsCompliant(project, requiredRoots))
        {
            // The feature/profile flags identify the existing project shape as
            // dedicated before this provider is allowed to alter its security
            // policy. All restriction keys are sent in one project update so
            // restricted.devices.disk=allow is never published with empty paths.
            IncusProjectSecurity.EnsureDedicatedShape(project);
            project = await ReadProjectSecurityAsync(options, ct).ConfigureAwait(false);
            IncusProjectSecurity.EnsureDedicatedShape(project);
            await _cli.RunCheckedAsync(
                "configure restricted Incus project",
                options,
                IncusProjectSecurity.BuildSetArguments(options, requiredRoots),
                stdin: null,
                options.OperationTimeout,
                ct).ConfigureAwait(false);
            project = await ReadProjectSecurityAsync(options, ct).ConfigureAwait(false);
        }

        IncusProjectSecurity.EnsureCompliant(project, requiredRoots);
    }

    private async Task<IncusProjectSecuritySnapshot> ReadProjectSecurityAsync(
        IncusSandboxOptions options,
        CancellationToken ct)
    {
        var result = await _cli.RunCheckedAsync(
            "read dedicated Incus project security",
            options,
            [options.BinaryPath, "query", $"/1.0/projects/{options.ProjectName}"],
            stdin: null,
            options.OperationTimeout,
            ct,
            heavyOperation: false,
            maxStdoutBytes: 64 * 1024,
            maxStderrBytes: 4096).ConfigureAwait(false);
        return IncusProjectSecurity.ParseProjectQuery(result.Stdout, options.ProjectName);
    }

    private async Task<bool> RequireManagedProjectIfPresentAsync(
        IncusSandboxOptions options,
        CancellationToken ct)
    {
        if (!await ProjectExistsAsync(options, ct).ConfigureAwait(false))
            return false;

        // Cold-start inventory and reaping can run before CreateAsync performs
        // host preflight. Prove the project is the exact dedicated project this
        // provider would create before any instance-owned metadata is trusted.
        var project = await ReadProjectSecurityAsync(options, ct).ConfigureAwait(false);
        IncusProjectSecurity.EnsureDedicatedShape(project);
        var requiredRoots = IncusProjectSecurity.ResolveRequiredRoots(
            options,
            ResolveStagingRootPath(options));
        IncusProjectSecurity.EnsureCompliant(project, requiredRoots);
        return true;
    }

    private async Task<bool> ProjectExistsAsync(IncusSandboxOptions options, CancellationToken ct)
    {
        IncusInputValidation.ValidateOptionsIdentity(options);
        var list = await _cli.RunCheckedAsync(
            "project list",
            options,
            [options.BinaryPath, "project", "list", "--format=json"],
            stdin: null,
            options.OperationTimeout,
            ct,
            heavyOperation: false).ConfigureAwait(false);
        return ProjectListContains(list.Stdout, options.ProjectName);
    }

    // Acquires a boot slot from the hot-reloadable gate, then applies the
    // inter-boot stagger. Holding this across `incus start` + guest-agent
    // readiness bounds how many qemu VMs boot at once. Mirrors the Multipass
    // boot-gate primitive. Caller MUST dispose to release the slot.
    internal async Task<IDisposable> AcquireBootSlotAsync(IncusSandboxOptions options, CancellationToken ct)
    {
        var desired = options.MaxConcurrentBoots < 1 ? 1 : options.MaxConcurrentBoots;
        SemaphoreSlim sem;
        lock (_bootGateGuard)
        {
            if (_bootGate is null || _bootGateCapacity != desired)
            {
                // Do NOT dispose the old gate: in-flight releasers still hold it
                // and Release() on dispose; it is GC'd once unreferenced. A
                // downward resize transiently exceeds the new limit until
                // in-flight boots on the old semaphore release.
                _bootGate = new SemaphoreSlim(desired, desired);
                _bootGateCapacity = desired;
            }
            sem = _bootGate;
        }

        await sem.WaitAsync(ct).ConfigureAwait(false);
        var delay = options.BootLaunchDelay;
        if (delay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(delay, _timeProvider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                sem.Release();
                throw;
            }
        }
        return new BootGateReleaser(sem);
    }

    private sealed class BootGateReleaser(SemaphoreSlim sem) : IDisposable
    {
        private SemaphoreSlim? _sem = sem;
        public void Dispose() => Interlocked.Exchange(ref _sem, null)?.Release();
    }

    private async Task StartAndWaitAsync(
        IncusSandboxOptions options,
        string name,
        string? bridge,
        IReadOnlyList<IncusPreparedMount> mounts,
        bool runCloudInit,
        CancellationToken ct)
    {
        // Stagger concurrent VM boots: hold a boot slot across the start +
        // guest-agent readiness window (the actual qemu boot), so a boot storm
        // does not starve incusd/host and push agents past VmStartTimeout.
        using var bootSlot = await AcquireBootSlotAsync(options, ct).ConfigureAwait(false);
        await IncusGuestLifecycle.StartAndWaitForAgentAsync(
            _cli,
            options,
            name,
            _timeProvider,
            token => VerifyDeviceTopologyAsync(options, name, bridge, mounts, token),
            ct).ConfigureAwait(false);
        if (runCloudInit)
            await WaitForCloudInitAsync(options, name, ct).ConfigureAwait(false);
        await IncusGuestLifecycle.PrepareRuntimeDirectoryAsync(
            _cli,
            options,
            name,
            ct).ConfigureAwait(false);
        await PrepareDotnetCliHomeAsync(options, name, ct).ConfigureAwait(false);
        await IncusGuestLifecycle.VerifyExecWrapperAsync(
            _cli,
            options,
            name,
            ct).ConfigureAwait(false);
    }

    private async Task WaitForCloudInitAsync(
        IncusSandboxOptions options,
        string name,
        CancellationToken ct)
    {
        var result = await _cli.RunAllowFailureAsync(
            options,
            BuildRootExec(options, name, ["cloud-init", "status", "--wait", "--format=json"]),
            stdin: null,
            options.CloudInitTimeout,
            ct,
            heavyOperation: false,
            maxStdoutBytes: 64 * 1024,
            maxStderrBytes: 4096).ConfigureAwait(false);
        if (result.ExecutionUnavailable
            || result.StartFailed
            || result.StdoutLimitExceeded
            || result.StderrLimitExceeded
            || !TryAcceptCloudInitStatus(result.Stdout, result.ExitCode, out var degraded))
        {
            throw new InvalidOperationException(
                $"Incus cloud-init did not reach a non-fatal completed state (exit code {result.ExitCode}).");
        }
        if (degraded)
        {
            _log.LogWarning(
                "Cloud-init completed with recoverable warnings in Incus VM {InstanceName}; fatal error list was empty",
                name);
        }
    }

    internal static bool TryAcceptCloudInitStatus(string json, int exitCode, out bool degraded)
    {
        degraded = false;
        if (exitCode is not (0 or 2) || string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("status", out var status)
                || !string.Equals(status.GetString(), "done", StringComparison.Ordinal))
                return false;
            if (!root.TryGetProperty("errors", out var errors)
                || errors.ValueKind != JsonValueKind.Array
                || errors.GetArrayLength() != 0)
                return false;
            if (!root.TryGetProperty("extended_status", out var extendedStatus))
                return exitCode == 0;
            var extended = extendedStatus.GetString();
            if (exitCode == 0)
                return string.Equals(extended, "done", StringComparison.Ordinal);
            degraded = string.Equals(extended, "degraded done", StringComparison.Ordinal);
            return degraded;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task RunExtraRuncmdAsync(
        IncusSandboxOptions options,
        string name,
        CancellationToken ct)
    {
        if (options.ExtraRuncmd.Count == 0)
            return;
        var script = new System.Text.StringBuilder("set -eu\n");
        foreach (var command in options.ExtraRuncmd)
        {
            if (command.Contains('\0'))
                throw new InvalidOperationException("Incus ExtraRuncmd cannot contain NUL.");
            script.AppendLine(command);
        }
        await _cli.RunCheckedAsync(
            "run Incus provisioning commands",
            options,
            BuildRootExec(options, name, ["/bin/sh", "-s"]),
            script.ToString(),
            options.CloudInitTimeout,
            ct,
            heavyOperation: false).ConfigureAwait(false);
    }

    private async Task RunProvisioningWithPrivateWorkspaceAsync(
        IncusSandboxOptions options,
        string name,
        IReadOnlyList<string>? expectedExecutableContentSha256,
        IReadOnlyList<string> mountGuestPaths,
        CancellationToken ct)
    {
        if (options.ExecutableProvisions.Count == 0
            && options.BaselineVerificationCommands.Count == 0
            && options.PackageCacheSeeds.Count == 0)
        {
            return;
        }

        using var timeoutCancellation = new CancellationTokenSource(
            options.ImageProvisioningTimeout,
            _timeProvider);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            timeoutCancellation.Token);
        var provisioningCt = deadline.Token;
        var stagingRoot = ResolveStagingRoot(options);
        IncusProvisioningWorkspace? workspace = null;
        Exception? primaryFailure = null;
        try
        {
            await _hostProvisioningInputGate.WaitAsync(provisioningCt).ConfigureAwait(false);
            try
            {
                workspace = IncusProvisioningWorkspace.Create(
                    options,
                    stagingRoot,
                    _environmentVariableReader,
                    _newGuid,
                    provisioningCt);
            }
            finally
            {
                _hostProvisioningInputGate.Release();
            }
            if (expectedExecutableContentSha256 is not null
                && !workspace.ExecutableContentSha256.SequenceEqual(
                    expectedExecutableContentSha256,
                    StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    "An Incus executable provisioning source changed after baseline identity derivation; refusing to publish mismatched bytes.");
            }
            await ApplyProvisioningAsync(
                options,
                name,
                workspace,
                mountGuestPaths,
                provisioningCt).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            primaryFailure = ex;
        }

        Exception? cleanupFailure = null;
        try
        {
            workspace?.Dispose();
        }
        catch (Exception ex)
        {
            cleanupFailure = ex;
            try
            {
                workspace?.ReleaseLeaseForRecovery();
            }
            catch (Exception releaseFailure)
            {
                cleanupFailure = new AggregateException(
                    "Incus workspace deletion failed and its recovery lease could not be released.",
                    ex,
                    releaseFailure);
            }
        }
        try
        {
            ThrowProvisioningFailures(
                primaryFailure,
                cleanupFailure,
                "Incus VM provisioning failed and private host workspace cleanup also failed.");
        }
        catch (OperationCanceledException ex) when (
            !ct.IsCancellationRequested && timeoutCancellation.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Incus VM provisioning exceeded its {options.ImageProvisioningTimeout.TotalSeconds:F0}-second deadline.",
                ex);
        }
    }

    private async Task ApplyProvisioningAsync(
        IncusSandboxOptions options,
        string name,
        IncusProvisioningWorkspace workspace,
        IReadOnlyList<string> mountGuestPaths,
        CancellationToken ct)
    {
        await ValidateCanonicalProvisioningPathsAsync(
            options,
            name,
            mountGuestPaths,
            ct).ConfigureAwait(false);
        var needsGuestStage = workspace.Executables.Count > 0 || options.PackageCacheSeeds.Count > 0;
        var guestStageRoot = $"{IncusCloudInit.ControlDirectory}/provision-{NextGuid("guest provisioning directory"):N}";
        var guestStageCreated = false;
        Exception? primaryFailure = null;
        try
        {
            if (needsGuestStage)
            {
                await RunRootCommandAsync(
                    options,
                    name,
                    "prepare guest provisioning directory",
                    ["install", "-d", "-m", "0700", "-o", "0", "-g", "0", "--", guestStageRoot],
                    ct).ConfigureAwait(false);
                guestStageCreated = true;
            }

            await ProvisionExecutablesAsync(options, name, workspace, guestStageRoot, ct).ConfigureAwait(false);
            await VerifyProvisioningCommandsAsync(options, name, ct).ConfigureAwait(false);
            await SeedPackageCachesAsync(options, name, workspace, guestStageRoot, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            primaryFailure = ex;
        }

        Exception? cleanupFailure = null;
        if (guestStageCreated)
        {
            try
            {
                await RunRootCommandAsync(
                    options,
                    name,
                    "clean guest provisioning directory",
                    ["rm", "-rf", "--", guestStageRoot],
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                cleanupFailure = ex;
            }
        }
        ThrowProvisioningFailures(
            primaryFailure,
            cleanupFailure,
            "Incus VM provisioning failed and transient guest-file cleanup also failed.");
    }

    private async Task ProvisionExecutablesAsync(
        IncusSandboxOptions options,
        string name,
        IncusProvisioningWorkspace workspace,
        string guestStageRoot,
        CancellationToken ct)
    {
        for (var i = 0; i < workspace.Executables.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var executable = workspace.Executables[i];
            var provision = executable.Provision;
            EnsureProvisioningDestinationAllowed(provision.VmDestPath);
            var label = string.IsNullOrWhiteSpace(provision.Label)
                ? Path.GetFileName(provision.VmDestPath)
                : provision.Label;
            var guestStagePath = $"{guestStageRoot}/executable-{i:D3}";
            _log.LogInformation(
                "Provisioning Incus VM executable {Label} at {GuestDestination}",
                label,
                provision.VmDestPath);
            await _cli.RunCheckedAsync(
                "push staged executable",
                options,
                BuildFilePush(options, name, executable.StagedPath, guestStagePath),
                stdin: null,
                options.ImageProvisioningTimeout,
                ct).ConfigureAwait(false);

            var destinationParent = GetGuestParent(provision.VmDestPath);
            await PrepareGuestDirectoryAsync(
                options,
                name,
                destinationParent,
                "prepare executable destination",
                ct).ConfigureAwait(false);
            await RunRootCommandAsync(
                options,
                name,
                "install staged executable",
                ["install", "-m", "0755", "-o", "0", "-g", "0", "--", guestStagePath, provision.VmDestPath],
                ct,
                options.ImageProvisioningTimeout).ConfigureAwait(false);

            foreach (var symlink in provision.VmSymlinks)
            {
                EnsureProvisioningDestinationAllowed(symlink);
                await PrepareGuestDirectoryAsync(
                    options,
                    name,
                    GetGuestParent(symlink),
                    "prepare executable symlink destination",
                    ct).ConfigureAwait(false);
                await RunRootCommandAsync(
                    options,
                    name,
                    "create executable symlink",
                    ["ln", "-sfnT", "--", provision.VmDestPath, symlink],
                    ct).ConfigureAwait(false);
            }
        }
    }

    internal async Task ValidateCanonicalProvisioningPathsAsync(
        IncusSandboxOptions options,
        string name,
        IReadOnlyList<string> mountGuestPaths,
        CancellationToken ct) =>
        await IncusGuestPathAuthorization.ValidateCanonicalProvisioningPathsAsync(
            _cli,
            options,
            name,
            mountGuestPaths,
            ct).ConfigureAwait(false);

    private async Task VerifyProvisioningCommandsAsync(
        IncusSandboxOptions options,
        string name,
        CancellationToken ct)
    {
        for (var i = 0; i < options.BaselineVerificationCommands.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var verification = options.BaselineVerificationCommands[i];
            _log.LogInformation(
                "Running Incus VM provisioning verification {Step}/{Total}: {Label}",
                i + 1,
                options.BaselineVerificationCommands.Count,
                verification.Label);
            var result = await _cli.RunAllowFailureAsync(
                options,
                BuildUnprivilegedVerificationExec(options, name, verification.Argv),
                stdin: null,
                options.OperationTimeout,
                ct,
                heavyOperation: false,
                maxStdoutBytes: 4096,
                maxStderrBytes: 4096).ConfigureAwait(false);
            if (result.Success && !result.StdoutLimitExceeded && !result.StderrLimitExceeded)
                continue;
            var hint = string.IsNullOrWhiteSpace(verification.FailureHint)
                ? "the required command is not runnable by the configured sandbox identity on its non-login PATH"
                : verification.FailureHint;
            throw new InvalidOperationException(
                $"Incus VM provisioning verification '{verification.Label}' failed: {hint}.");
        }
    }

    private async Task SeedPackageCachesAsync(
        IncusSandboxOptions options,
        string name,
        IncusProvisioningWorkspace workspace,
        string guestStageRoot,
        CancellationToken ct)
    {
        var aggregateBytes = 0L;
        for (var i = 0; i < options.PackageCacheSeeds.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var seed = options.PackageCacheSeeds[i];
            EnsureProvisioningDestinationAllowed(seed.VmDestPath);
            string archivePath;
            await _hostProvisioningInputGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                archivePath = workspace.CreatePackageArchive(
                    options,
                    seed,
                    i,
                    _environmentVariableReader,
                    ref aggregateBytes,
                    ct);
            }
            finally
            {
                _hostProvisioningInputGate.Release();
            }
            var guestArchivePath = $"{guestStageRoot}/package-cache-{i:D3}.tar";
            _log.LogInformation(
                "Seeding Incus VM package cache at {GuestDestination}",
                seed.VmDestPath);
            await _cli.RunCheckedAsync(
                "push staged package cache",
                options,
                BuildFilePush(options, name, archivePath, guestArchivePath),
                stdin: null,
                options.ImageProvisioningTimeout,
                ct).ConfigureAwait(false);
            await PrepareGuestDirectoryAsync(
                options,
                name,
                seed.VmDestPath,
                "prepare package cache destination",
                ct).ConfigureAwait(false);
            await RunRootCommandAsync(
                options,
                name,
                "extract package cache seed",
                [
                    "tar",
                    "--extract",
                    "--file", guestArchivePath,
                    "--directory", seed.VmDestPath,
                    "--no-same-owner",
                    "--no-same-permissions",
                ],
                ct,
                options.ImageProvisioningTimeout).ConfigureAwait(false);
            await RunRootCommandAsync(
                options,
                name,
                "assign package cache ownership",
                [
                    "chown",
                    "-R",
                    $"{options.GuestUserId.ToString(CultureInfo.InvariantCulture)}:{options.GuestGroupId.ToString(CultureInfo.InvariantCulture)}",
                    "--",
                    seed.VmDestPath,
                ],
                ct,
                options.ImageProvisioningTimeout).ConfigureAwait(false);

            // Seed destinations under $HOME/.nuget/... often inherit a
            // root-owned .nuget parent from ExtraRuncmd `mkdir -p` or from
            // install -d against a pre-existing root directory. Chown the
            // packages leaf alone leaves NuGet unable to create
            // $HOME/.nuget/NuGet. Fix the parent directory inode (not -R —
            // packages was already reassigned) when the seed lands there.
            var nugetHome = NuGetPackageCacheGuestPaths.TryGetNuGetHomeDirectory(
                seed.VmDestPath,
                options.GuestHome);
            if (nugetHome is not null)
            {
                await RunRootCommandAsync(
                    options,
                    name,
                    "assign NuGet home ownership",
                    [
                        "chown",
                        $"{options.GuestUserId.ToString(CultureInfo.InvariantCulture)}:{options.GuestGroupId.ToString(CultureInfo.InvariantCulture)}",
                        "--",
                        nugetHome,
                    ],
                    ct,
                    options.ImageProvisioningTimeout).ConfigureAwait(false);
            }
        }
    }

    private async Task RunRootCommandAsync(
        IncusSandboxOptions options,
        string name,
        string operation,
        IReadOnlyList<string> command,
        CancellationToken ct,
        TimeSpan? timeout = null) =>
        await _cli.RunCheckedAsync(
            operation,
            options,
            BuildRootExec(options, name, command),
            stdin: null,
            timeout ?? options.OperationTimeout,
            ct,
            heavyOperation: false).ConfigureAwait(false);

    private async Task PrepareGuestDirectoryAsync(
        IncusSandboxOptions options,
        string name,
        string guestDirectory,
        string operation,
        CancellationToken ct)
    {
        IncusInputValidation.ValidateAbsoluteGuestPath(guestDirectory, nameof(guestDirectory));
        IReadOnlyList<string> command = IsGuestHomePath(options, guestDirectory)
            ?
            [
                "install",
                "-d",
                "-m", "0755",
                "-o", options.GuestUserId.ToString(CultureInfo.InvariantCulture),
                "-g", options.GuestGroupId.ToString(CultureInfo.InvariantCulture),
                "--",
                guestDirectory,
            ]
            : ["mkdir", "-p", "--", guestDirectory];
        await RunRootCommandAsync(options, name, operation, command, ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> BuildFilePush(
        IncusSandboxOptions options,
        string name,
        string hostSource,
        string guestDestination)
    {
        IncusInputValidation.ValidateInstanceName(name, nameof(name));
        IncusInputValidation.ValidateAbsoluteHostPath(hostSource, nameof(hostSource));
        IncusInputValidation.ValidateAbsoluteGuestPath(guestDestination, nameof(guestDestination));
        var argv = IncusCommandBuilder.Prefix(
            options,
            "file", "push",
            "--mode=0600",
            "--uid=0",
            "--gid=0",
            "--");
        argv.Add(hostSource);
        argv.Add($"{name}{guestDestination}");
        return argv;
    }

    private static IReadOnlyList<string> BuildUnprivilegedVerificationExec(
        IncusSandboxOptions options,
        string name,
        IReadOnlyList<string> command)
    {
        if (command.Count == 0)
            throw new ArgumentException("An Incus baseline verification command cannot have empty argv.", nameof(command));
        var unprivileged = new List<string>(command.Count + 14)
        {
            "setsid",
            "--",
            "setpriv",
            "--no-new-privs",
            $"--reuid={options.GuestUserId.ToString(CultureInfo.InvariantCulture)}",
            $"--regid={options.GuestGroupId.ToString(CultureInfo.InvariantCulture)}",
            "--clear-groups",
            "--",
            "env",
            "-i",
            "--",
            $"HOME={options.GuestHome}",
            $"DOTNET_CLI_HOME={IncusCloudInit.DotnetCliHome}",
            $"PATH={IncusCloudInit.NonLoginPath}",
            "LANG=C.UTF-8",
        };
        unprivileged.AddRange(command);
        return BuildRootExec(options, name, unprivileged, options.GuestHome);
    }

    private static bool IsGuestHomePath(IncusSandboxOptions options, string path) =>
        string.Equals(path, options.GuestHome, StringComparison.Ordinal)
        || path.StartsWith(options.GuestHome + "/", StringComparison.Ordinal);

    internal static void ValidateProvisioningMountSeparation(
        IncusSandboxOptions options,
        IReadOnlyList<SandboxMount> mounts)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(mounts);

        var provisioningTargets = SnapshotProvisioningTargets(options);

        for (var mountIndex = 0; mountIndex < mounts.Count; mountIndex++)
        {
            var mount = mounts[mountIndex]
                ?? throw new InvalidOperationException("Incus sandbox mounts cannot contain null entries.");
            IncusInputValidation.ValidateAbsoluteGuestPath(mount.SandboxPath, nameof(mounts));
            foreach (var target in provisioningTargets)
            {
                if (IncusGuestPaths.Overlap(target.Path, mount.SandboxPath))
                {
                    throw new InvalidOperationException(
                        $"{target.Name} overlaps sandbox mount path '{mount.SandboxPath}'; " +
                        "refusing to expose host or transient mount storage to root provisioning writes.");
                }
            }
        }
    }

    private static IReadOnlyList<IncusGuestPathAuthorization.ProvisioningTarget> SnapshotProvisioningTargets(
        IncusSandboxOptions options) =>
        IncusGuestPathAuthorization.SnapshotProvisioningTargets(options);

    private static void EnsureProvisioningDestinationAllowed(string guestPath) =>
        IncusGuestPathAuthorization.EnsureProvisioningDestinationAllowed(guestPath);

    private static string GetGuestParent(string guestPath)
    {
        var separator = guestPath.LastIndexOf('/');
        return separator <= 0 ? "/" : guestPath[..separator];
    }

    private static void ThrowProvisioningFailures(
        Exception? primaryFailure,
        Exception? cleanupFailure,
        string aggregateMessage)
    {
        if (primaryFailure is not null && cleanupFailure is not null)
            throw new AggregateException(aggregateMessage, primaryFailure, cleanupFailure);
        if (cleanupFailure is not null)
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        if (primaryFailure is not null)
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
    }

    private async Task CleanCloudInitStateAsync(
        IncusSandboxOptions options,
        string name,
        CancellationToken ct)
    {
        await _cli.RunCheckedAsync(
            "scrub baseline cloud-init state",
            options,
            BuildRootExec(options, name, ["cloud-init", "clean", "--logs", "--machine-id"]),
            stdin: null,
            options.OperationTimeout,
            ct,
            heavyOperation: false).ConfigureAwait(false);
    }

    // Runs as root on every boot (baseline and COW clones). Positional
    // arguments: $1 guest home, $2 guest uid, $3 guest gid, $4 DOTNET_CLI_HOME.
    // The baseline image ships a populated, offline NuGet package cache under
    // the guest home. When any of it was created by root during warm-up the
    // unprivileged guest user cannot read its config or extend it, so
    // `dotnet build`/`dotnet test` abort restore with "Failed to read
    // NuGet.Config due to unauthorized access". Re-own the whole tree to the
    // guest instead of discarding it, preserving the cache so offline restores
    // still resolve. The re-own is recursive (not just the top directory)
    // because ownership is mixed in practice — the observed failure traversed
    // ".nuget" but was denied ".nuget/NuGet" — and idempotent, so repeated
    // boots of an already-owned tree are harmless. DOTNET_CLI_HOME relocates
    // dotnet's per-user state onto tmpfs, recreated empty each boot; link its
    // NuGet home at the guest's cache-populated one so restores reuse the baked
    // packages instead of trying to re-download them offline.
    internal const string PrepareDotnetHomesScript = """
        set -eu
        guest_home=$1
        guest_uid=$2
        guest_gid=$3
        cli_home=$4
        install -d -m 0700 -o "$guest_uid" -g "$guest_gid" "$cli_home"
        nuget_home="$guest_home/.nuget"
        if [ -e "$nuget_home" ]; then
          chown -R "$guest_uid:$guest_gid" "$nuget_home"
        else
          install -d -m 0700 -o "$guest_uid" -g "$guest_gid" "$nuget_home"
        fi
        # Point the CLI-home NuGet dir at the cache-populated guest one. ln -sfn
        # replaces an existing symlink or file, but when the target path is a
        # real directory it silently creates the link INSIDE it
        # ($cli_home/.nuget/.nuget) instead of replacing it, leaving
        # DOTNET_CLI_HOME with an empty .nuget that misses the baked offline
        # packages. Drop a pre-existing real (non-symlink) directory first so the
        # link always resolves to the cache, keeping the re-own idempotent across
        # boots that start from either an empty tmpfs CLI home or one a prior boot
        # already populated.
        if [ -d "$cli_home/.nuget" ] && [ ! -L "$cli_home/.nuget" ]; then
          rm -rf "$cli_home/.nuget"
        fi
        ln -sfn "$nuget_home" "$cli_home/.nuget"
        """;

    private async Task PrepareDotnetCliHomeAsync(
        IncusSandboxOptions options,
        string name,
        CancellationToken ct)
    {
        await _cli.RunCheckedAsync(
            "prepare guest dotnet CLI home",
            options,
            BuildRootExec(options, name,
            [
                "/bin/sh", "-s", "--",
                options.GuestHome,
                options.GuestUserId.ToString(CultureInfo.InvariantCulture),
                options.GuestGroupId.ToString(CultureInfo.InvariantCulture),
                IncusCloudInit.DotnetCliHome,
            ]),
            PrepareDotnetHomesScript,
            options.OperationTimeout,
            ct,
            heavyOperation: false).ConfigureAwait(false);
    }

    private async Task WaitForMountsAsync(
        IncusSandboxOptions options,
        string name,
        IReadOnlyList<IncusPreparedMount> mounts,
        CancellationToken ct) =>
        await IncusMountReadiness.WaitAsync(
            _cli,
            options,
            name,
            ResolveStagingRoot(options),
            mounts,
            _timeProvider,
            ct).ConfigureAwait(false);

    private async Task ApplyGuestLocalMountsAsync(
        IncusSandboxOptions options,
        string name,
        IReadOnlyList<IncusPreparedMount> mounts,
        CancellationToken ct)
    {
        foreach (var mount in mounts)
        {
            if (!mount.TmpfsSizeBytes.HasValue && !mount.RootDiskDirectory)
                continue;
            if (mount.RootDiskDirectory)
            {
                await _cli.RunCheckedAsync(
                    "create persistent guest root-disk directory",
                    options,
                    BuildRootExec(options, name,
                    [
                        "install", "-d", "-m", "0700",
                        "-o", options.GuestUserId.ToString(CultureInfo.InvariantCulture),
                        "-g", options.GuestGroupId.ToString(CultureInfo.InvariantCulture),
                        mount.GuestPath,
                    ]),
                    stdin: null,
                    options.OperationTimeout,
                    ct,
                    heavyOperation: false).ConfigureAwait(false);
                continue;
            }
            var tmpfsSize = mount.TmpfsSizeBytes
                ?? throw new InvalidOperationException("A guest tmpfs mount has no size.");
            await IncusGuestLifecycle.MountTmpfsAsync(
                _cli,
                options,
                name,
                mount.GuestPath,
                tmpfsSize,
                ct).ConfigureAwait(false);
        }
    }

    private async Task CreateGuestLinksAsync(
        IncusSandboxOptions options,
        string name,
        IReadOnlyList<IncusGuestLink> links,
        CancellationToken ct) =>
        await IncusGuestLinkLifecycle.CreateAsync(
            _cli,
            options,
            name,
            links,
            ct).ConfigureAwait(false);

    private async Task ApplyMountDevicesAsync(
        IncusSandboxOptions options,
        string name,
        IReadOnlyList<IncusPreparedMount> mounts,
        CancellationToken ct)
    {
        for (var index = 0; index < mounts.Count; index++)
        {
            var mount = mounts[index];
            if (mount.TmpfsSizeBytes.HasValue || mount.RootDiskDirectory)
                continue;
            var device = BuildMountDeviceName(index);
            var source = mount.HostSource ?? throw new InvalidOperationException("Host-backed mount has no source.");
            var authorizedSource = IncusMountStaging.ReauthorizeHostSource(options, ResolveStagingRoot(options), source);
            if (mount.PinnedHostDirectory is { } pinnedDirectory)
                IncusMountStaging.EnsurePinnedHostSourceMatches(authorizedSource, pinnedDirectory);
            var argv = IncusCommandBuilder.BuildDeviceAdd(
                options,
                name,
                device,
                authorizedSource,
                mount.GuestPath,
                mount.ReadOnly);
            await _cli.RunCheckedAsync(
                "add mount device",
                options,
                argv,
                stdin: null,
                options.OperationTimeout,
                ct).ConfigureAwait(false);
            if (mount.PinnedHostDirectory is not null)
                VerifyPinnedMountSource(options, mount);
        }
    }

    private async Task VerifyDeviceTopologyAsync(
        IncusSandboxOptions options,
        string name,
        string? bridge,
        IReadOnlyList<IncusPreparedMount> mounts,
        CancellationToken ct)
    {
        var query = await _cli.RunCheckedAsync(
            "verify effective VM device topology",
            options,
            [options.BinaryPath, "query", $"/1.0/instances/{name}?project={options.ProjectName}"],
            stdin: null,
            options.OperationTimeout,
            ct,
            heavyOperation: false,
            maxStdoutBytes: options.MaxCliStdoutBytes,
            maxStderrBytes: 4096).ConfigureAwait(false);
        IncusDeviceTopology.Verify(query.Stdout, options, bridge, mounts);
    }

    private void VerifyPinnedMountSource(
        IncusSandboxOptions options,
        IncusPreparedMount mount)
    {
        if (mount.PinnedHostDirectory is not { } pinnedDirectory)
            return;
        var source = mount.HostSource
            ?? throw new InvalidOperationException("A pinned Incus mount has no host source.");
        var authorizedSource = IncusMountStaging.ReauthorizeHostSource(
            options,
            ResolveStagingRoot(options),
            source);
        if (!string.Equals(authorizedSource, source, StringComparison.Ordinal))
            throw new IOException("The authorized Incus host mount source changed canonical path during provisioning.");
        IncusMountStaging.EnsurePinnedHostSourceMatches(authorizedSource, pinnedDirectory);
    }

    private async Task ApplyCloneLimitsAsync(
        IncusSandboxOptions options,
        string name,
        SandboxResourceLimits limits,
        CancellationToken ct)
    {
        if (limits.DiskBytes is { } disk && disk < options.BaselineDiskBytes)
            throw new InvalidOperationException(
                $"Requested disk limit {disk} is smaller than the {options.BaselineDiskBytes}-byte baked baseline and cannot be honored by a COW clone.");
        if (limits.CpuCount is { } cpus)
            await SetConfigAsync(options, name, "limits.cpu", cpus.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        if (limits.MemoryBytes is { } memory)
            await SetConfigAsync(options, name, "limits.memory", $"{memory.ToString(CultureInfo.InvariantCulture)}B", ct).ConfigureAwait(false);
        if (limits.DiskBytes is { } requestedDisk && requestedDisk > options.BaselineDiskBytes)
        {
            await _cli.RunCheckedAsync(
                "grow clone root disk",
                options,
                IncusCommandBuilder.Prefix(
                    options,
                    "config", "device", "set", name, "root", $"size={requestedDisk.ToString(CultureInfo.InvariantCulture)}B"),
                stdin: null,
                options.OperationTimeout,
                ct).ConfigureAwait(false);
        }
    }

    private async Task AddNicAsync(IncusSandboxOptions options, string name, string bridge, CancellationToken ct) =>
        await _cli.RunCheckedAsync(
            "add bridged NIC",
            options,
            IncusCommandBuilder.BuildNicAdd(options, name, bridge),
            stdin: null,
            options.OperationTimeout,
            ct).ConfigureAwait(false);

    private async Task SetCloudInitAsync(
        IncusSandboxOptions options,
        string name,
        string cloudInit,
        CancellationToken ct) =>
        await _cli.RunCheckedAsync(
            "set cloud-init user-data",
            options,
            IncusCommandBuilder.Prefix(options, "config", "set", name, "user.user-data", "-"),
            cloudInit,
            options.OperationTimeout,
            ct).ConfigureAwait(false);

    private void AppendCreationMetadata(
        ICollection<string> argv,
        string kind,
        string? baselineRef,
        string? baselineHash)
    {
        AddConfig(argv, ManagedKey, "true");
        AddConfig(argv, KindKey, kind);
        AddConfig(argv, CreatedAtKey, _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        if (baselineRef is not null)
            AddConfig(argv, BaselineRefKey, baselineRef);
        if (baselineHash is not null)
            AddConfig(argv, BaselineHashKey, baselineHash);
    }

    private static void AddConfig(ICollection<string> argv, string key, string value)
    {
        argv.Add("--config");
        argv.Add($"{key}={value}");
    }

    private async Task SetConfigAsync(
        IncusSandboxOptions options,
        string name,
        string key,
        string value,
        CancellationToken ct) =>
        await _cli.RunCheckedAsync(
            $"set instance config {key}",
            options,
            IncusCommandBuilder.Prefix(options, "config", "set", name, $"{key}={value}"),
            stdin: null,
            options.OperationTimeout,
            ct).ConfigureAwait(false);

    private async Task StopInstanceAsync(
        IncusSandboxOptions options,
        string name,
        bool stateful,
        CancellationToken ct)
    {
        var argv = IncusCommandBuilder.Prefix(options, "stop", name, "--timeout", Math.Max(1, (int)options.VmStopTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        if (stateful)
            argv.Add("--stateful");
        await _cli.RunCheckedAsync(
            stateful ? "stateful VM stop" : "VM stop",
            options,
            argv,
            stdin: null,
            options.VmStopTimeout + options.OperationTimeout,
            ct).ConfigureAwait(false);
    }

    private async Task DeleteInstanceAsync(IncusSandboxOptions options, string name, CancellationToken ct) =>
        await _cli.RunCheckedAsync(
            "delete VM",
            options,
            IncusCommandBuilder.Prefix(options, "delete", name, "--force"),
            stdin: null,
            options.OperationTimeout,
            ct).ConfigureAwait(false);

    private async Task DeleteVerifiedOwnedInstanceAsync(
        IncusSandboxOptions options,
        string name,
        string expectedKind,
        string? expectedBakeToken,
        CancellationToken ct)
    {
        var managed = await _cli.RunCheckedAsync(
            "verify managed instance before delete",
            options,
            IncusCommandBuilder.Prefix(options, "config", "get", name, ManagedKey),
            stdin: null,
            options.OperationTimeout,
            ct,
            heavyOperation: false,
            maxStdoutBytes: 64,
            maxStderrBytes: 4096).ConfigureAwait(false);
        var kind = await _cli.RunCheckedAsync(
            "verify instance kind before delete",
            options,
            IncusCommandBuilder.Prefix(options, "config", "get", name, KindKey),
            stdin: null,
            options.OperationTimeout,
            ct,
            heavyOperation: false,
            maxStdoutBytes: 64,
            maxStderrBytes: 4096).ConfigureAwait(false);
        if (!string.Equals(managed.Stdout.Trim(), "true", StringComparison.Ordinal)
            || !string.Equals(kind.Stdout.Trim(), expectedKind, StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to delete an Incus instance whose ownership metadata changed.");
        if (expectedBakeToken is not null)
        {
            var token = await _cli.RunCheckedAsync(
                "verify baseline bake token before delete",
                options,
                IncusCommandBuilder.Prefix(options, "config", "get", name, BakeTokenKey),
                stdin: null,
                options.OperationTimeout,
                ct,
                heavyOperation: false,
                maxStdoutBytes: 128,
                maxStderrBytes: 4096).ConfigureAwait(false);
            if (!string.Equals(token.Stdout.Trim(), expectedBakeToken, StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to delete an Incus baseline candidate whose bake token changed.");
        }
        await DeleteInstanceAsync(options, name, ct).ConfigureAwait(false);
        await WaitForInstanceAbsenceAsync(options, name, ct).ConfigureAwait(false);
    }

    private async Task WaitForInstanceAbsenceAsync(
        IncusSandboxOptions options,
        string name,
        CancellationToken ct)
    {
        using var timeoutCancellation = new CancellationTokenSource(options.OperationTimeout, _timeProvider);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCancellation.Token);
        try
        {
            while (await FindInstanceAsync(options, name, deadline.Token).ConfigureAwait(false) is not null)
                await Task.Delay(options.ReadinessPollInterval, _timeProvider, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeoutCancellation.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Incus reported deleting instance '{name}', but exact absence was not observed within the configured deadline.",
                ex);
        }
    }

    private async Task<bool> TryDeleteOwnedInstanceAsync(
        IncusSandboxOptions options,
        string name,
        string kind,
        CancellationToken ct)
    {
        try
        {
            if (!await IsOwnedInstanceAsync(options, name, kind, ct).ConfigureAwait(false))
                return false;
            await DeleteVerifiedOwnedInstanceAsync(options, name, kind, expectedBakeToken: null, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to clean up owned Incus instance {InstanceName}", name);
            return false;
        }
    }

    private async Task<bool> TryDeleteBakeCandidateAsync(
        IncusSandboxOptions options,
        string candidateName,
        string bakeToken)
    {
        try
        {
            var candidate = await FindInstanceAsync(options, candidateName, CancellationToken.None).ConfigureAwait(false);
            if (candidate is null)
                return false;
            if (!IsOwned(candidate, BaselineKind)
                || !string.Equals(GetConfig(candidate.Config, BakeTokenKey), bakeToken, StringComparison.Ordinal))
            {
                _log.LogError(
                    "Refusing to clean baseline candidate {CandidateName}: ownership token does not match this bake",
                    candidateName);
                return false;
            }
            await DeleteVerifiedOwnedInstanceAsync(
                options,
                candidateName,
                BaselineKind,
                bakeToken,
                CancellationToken.None).ConfigureAwait(false);
            _uncertainBaselines.TryRemove(candidateName, out _);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to clean Incus baseline candidate {CandidateName}", candidateName);
            return false;
        }
    }

    private async Task<bool> IsOwnedInstanceAsync(
        IncusSandboxOptions options,
        string name,
        string kind,
        CancellationToken ct)
    {
        IncusInputValidation.ValidateInstanceName(name, nameof(name));
        var instance = await FindInstanceAsync(options, name, ct).ConfigureAwait(false);
        return instance is not null && IsOwned(instance, kind);
    }

    private async Task<IncusInstanceInfo?> FindInstanceAsync(
        IncusSandboxOptions options,
        string name,
        CancellationToken ct)
    {
        IncusInputValidation.ValidateInstanceName(name, nameof(name));
        return (await ListInstancesAsync(options, ct).ConfigureAwait(false))
            .SingleOrDefault(instance => string.Equals(instance.Name, name, StringComparison.Ordinal));
    }

    private async Task<IReadOnlyList<IncusInstanceInfo>> ListInstancesAsync(
        IncusSandboxOptions options,
        CancellationToken ct)
    {
        var result = await _cli.RunCheckedAsync(
            "instance list",
            options,
            IncusCommandBuilder.Prefix(options, "list", "--format=json"),
            stdin: null,
            options.OperationTimeout,
            ct,
            heavyOperation: false).ConfigureAwait(false);
        return ParseInstances(result.Stdout);
    }

    private async Task<bool> SnapshotExistsAsync(
        IncusSandboxOptions options,
        string name,
        string snapshot,
        CancellationToken ct)
    {
        var result = await _cli.RunCheckedAsync(
            "snapshot list",
            options,
            IncusCommandBuilder.Prefix(options, "snapshot", "list", name, "--format=json"),
            stdin: null,
            options.OperationTimeout,
            ct,
            heavyOperation: false).ConfigureAwait(false);
        using var document = JsonDocument.Parse(result.Stdout);
        return document.RootElement.EnumerateArray().Any(element =>
            element.TryGetProperty("name", out var value)
            && string.Equals(value.GetString(), snapshot, StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> BuildRootExec(
        IncusSandboxOptions options,
        string name,
        IReadOnlyList<string> command,
        string? workingDirectory = null) =>
        IncusCommandBuilder.BuildRootExec(options, name, command, workingDirectory);

    private IncusSandboxOptions ReadOptions()
    {
        var options = ReadValidatedOptions();
        if (!string.Equals(options.ProjectName, _lifecycleProjectName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Incus ProjectName is a provider lifecycle identity and cannot change at runtime. Restart CodeyBox to apply this change.");
        }

        var stagingRootPath = ResolveStagingRootPath(options);
        if (!string.Equals(stagingRootPath, _lifecycleStagingRootPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The effective Incus StagingDirectory is a provider lifecycle identity and cannot change at runtime. Restart CodeyBox to apply this change.");
        }

        return options;
    }

    private IncusSandboxOptions ReadValidatedOptions()
    {
        var supplied = _optionsAccessor()
            ?? throw new InvalidOperationException("Incus options accessor returned null.");
        var options = IncusInputSnapshot.CaptureOptions(supplied);
        var errors = IncusSandboxOptions.Validate(options);
        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid Incus configuration: " + string.Join(" ", errors));
        IncusCloudInit.ValidateExtraFragment(options.ExtraCloudInit);
        return options;
    }

    internal static string? ResolveBridge(IncusSandboxOptions options, string? profileName)
    {
        if (profileName is null || profileName.Length == 0)
            return null;
        if (profileName.Length > 63)
            throw new ArgumentException("The Incus network profile name exceeds 63 characters.", nameof(profileName));
        if (string.IsNullOrWhiteSpace(profileName))
            return null;
        IncusInputValidation.ValidateDeviceName(profileName, nameof(profileName));
        if (!options.NetworkProfiles.TryGetValue(profileName, out var bridge))
            throw new InvalidOperationException("No Incus bridge is configured for the requested network profile.");
        IncusInputValidation.ValidateBridgeName(bridge, nameof(profileName));
        return bridge;
    }

    private static void ValidateResourceLimits(SandboxResourceLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.CpuCount is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(limits), "CPU count must be between 1 and 256 when supplied.");
        if (limits.MemoryBytes is { } memory
            && (memory < 256L * 1024 * 1024 || memory > 2L * 1024 * 1024 * 1024 * 1024))
            throw new ArgumentOutOfRangeException(nameof(limits), "Memory must be between 256 MiB and 2 TiB when supplied.");
        if (limits.DiskBytes is { } disk
            && (disk < 2L * 1024 * 1024 * 1024 || disk > 16L * 1024 * 1024 * 1024 * 1024))
            throw new ArgumentOutOfRangeException(nameof(limits), "Disk must be between 2 GiB and 16 TiB when supplied.");
        if (limits.WallClock is { } wallClock
            && (wallClock <= TimeSpan.Zero || wallClock > TimeSpan.FromDays(7)))
            throw new ArgumentOutOfRangeException(nameof(limits), "Wall-clock limit must be positive and at most seven days.");
    }

    internal static string ResolveImage(IncusSandboxOptions options, string imageReference)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (imageReference is null || imageReference.Length == 0)
            return options.DefaultImage;
        _ = IncusInputValidation.GetBoundedUtf8ByteCount(
            imageReference,
            4096,
            nameof(imageReference),
            "Incus image reference");
        return string.IsNullOrWhiteSpace(imageReference)
            || string.Equals(imageReference, "ignored", StringComparison.Ordinal)
                ? options.DefaultImage
                : imageReference;
    }

    private string ResolveStagingRoot(IncusSandboxOptions options)
    {
        var path = ResolveStagingRootPath(options);
        IncusMountStaging.EnsureOwnedStagingRoot(path);
        return path;
    }

    private string ResolveStagingRootPath(IncusSandboxOptions options) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(options.StagingDirectory)
            ? ResolveDefaultStagingRoot()
            : options.StagingDirectory);

    private string ResolveDefaultStagingRoot()
    {
        var stateHome = _environmentVariableReader("XDG_STATE_HOME");
        if (stateHome is not null && IsBoundedAbsoluteEnvironmentPath(stateHome))
            return Path.Combine(stateHome, "codeybox", "incus-staging");
        var home = _environmentVariableReader("HOME");
        if (home is not null && IsBoundedAbsoluteEnvironmentPath(home))
            return Path.Combine(home, ".local", "state", "codeybox", "incus-staging");
        throw new InvalidOperationException(
            "Incus StagingDirectory must be configured when neither XDG_STATE_HOME nor HOME is an absolute path.");
    }

    private static bool IsBoundedAbsoluteEnvironmentPath(string? value)
    {
        if (value is null || value.Length is < 1 or > 4096)
            return false;
        try
        {
            _ = IncusInputValidation.GetBoundedUtf8ByteCount(
                value,
                4096,
                nameof(value),
                "Incus staging environment path");
        }
        catch (ArgumentException)
        {
            return false;
        }
        return !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value);
    }

    private static void CreateSecurePrivateDirectory(string path)
    {
        if (Directory.Exists(path) || File.Exists(path))
            throw new IOException("Refusing to reuse an existing Incus sandbox staging directory.");
        Directory.CreateDirectory(path);
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Incus staging directories cannot be symbolic links or reparse points.");
        IncusMountStaging.SetPrivateMode(path);
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            const UnixFileMode forbidden =
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((mode & forbidden) != 0)
                throw new InvalidOperationException("Incus staging directories must not be accessible by group or other users.");
        }
    }

    private string CreateInstanceName(IncusSandboxOptions options)
    {
        const int virtiofsProjectAndInstanceBudget = 63;
        const int randomSuffixLength = 20;
        var suffix = NextGuid("sandbox instance name").ToString("N")[..randomSuffixLength];
        var normalized = NormalizedPrefix(options.InstanceNamePrefix);
        var maximumInstanceLength = virtiofsProjectAndInstanceBudget - options.ProjectName.Length;
        var maximumPrefixLength = maximumInstanceLength - suffix.Length;
        if (maximumPrefixLength < 1)
            throw new InvalidOperationException("The Incus project name leaves no safe virtiofs socket-path space for an instance name.");
        if (normalized.Length > maximumPrefixLength)
            normalized = normalized[..maximumPrefixLength];
        var name = normalized + suffix;
        IncusInputValidation.ValidateInstanceName(name, "generated instance name");
        return name;
    }

    private Guid NextGuid(string purpose)
    {
        var value = _newGuid();
        if (value == Guid.Empty)
            throw new InvalidOperationException($"The injected GUID source returned an empty value for {purpose}.");
        return value;
    }

    private static string BuildMountDeviceName(int index)
    {
        if (index is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(index));
        return $"m{index:D3}";
    }

    internal static string BuildMountDeviceNameForVerification(int index) => BuildMountDeviceName(index);

    private static string CreateBakeCandidateName(string baselineName, string bakeToken)
    {
        var baselineHash = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(baselineName)))[..10];
        return $"{IncusBaselineNaming.BakeCandidatePrefix}{baselineHash}-{bakeToken[..20]}";
    }

    private static string NormalizedPrefix(string prefix) => prefix.ToLowerInvariant();

    private void MarkActive(string name)
    {
        if (!_activeNames.TryAdd(name, true))
            throw new InvalidOperationException("Incus sandbox is already active or being adopted in this process.");
    }

    private void MarkInactive(string name)
    {
        _activeNames.TryRemove(name, out _);
        _activeOwners.TryRemove(name, out _);
    }

    private static bool ProjectListContains(string json, string projectName)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("Incus project inventory must be a JSON array.");

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty("name", out var nameElement)
                || nameElement.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(nameElement.GetString()))
            {
                throw new JsonException(
                    "Every Incus project inventory entry must contain a non-empty string property named 'name'.");
            }
            if (string.Equals(nameElement.GetString(), projectName, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static IncusStoragePoolInfo? ParseStoragePool(string json, string poolName)
    {
        using var document = JsonDocument.Parse(json);
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (!element.TryGetProperty("name", out var name)
                || !string.Equals(name.GetString(), poolName, StringComparison.Ordinal))
                continue;
            var config = ParseConfig(element);
            return new IncusStoragePoolInfo(
                poolName,
                element.TryGetProperty("driver", out var driver) ? driver.GetString() ?? string.Empty : string.Empty,
                config);
        }
        return null;
    }

    internal static IReadOnlyList<IncusInstanceInfo> ParseInstances(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("Incus instance inventory must be a JSON array.");

        var result = new List<IncusInstanceInfo>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty("name", out var nameElement)
                || nameElement.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(nameElement.GetString()))
            {
                throw new JsonException(
                    "Every Incus instance inventory entry must contain a non-empty string property named 'name'.");
            }
            if (!element.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(typeElement.GetString()))
            {
                throw new JsonException(
                    "Every Incus instance inventory entry must contain a non-empty string property named 'type'.");
            }
            var name = nameElement.GetString()!;
            var status = element.TryGetProperty("status", out var statusElement)
                && statusElement.ValueKind == JsonValueKind.String
                ? statusElement.GetString() ?? string.Empty
                : string.Empty;
            var type = typeElement.GetString()!;
            result.Add(new IncusInstanceInfo(name, status, type, ParseConfig(element)));
        }
        return result;
    }

    /// <summary>
    /// Reads an inventory entry's config map. A missing or non-object <c>config</c> yields an EMPTY
    /// map rather than throwing.
    /// </summary>
    /// <remarks>
    /// Incus lists instances that are mid-create or mid-delete without a materialised config. Throwing
    /// on those aborted the ENTIRE inventory parse, which is disproportionate in both directions:
    /// ownership is positive-only (<see cref="IsOwned"/> requires <c>managed=true</c> plus a matching
    /// kind), so a config-less entry could never have been ours anyway — while the exception failed
    /// whatever work item happened to trigger the listing, including mid-audit, and broke the reaper
    /// sweeps. An empty map preserves the safety property exactly (we still act only on entries we
    /// positively identify as ours) and a transient entry that is genuinely ours is picked up by the
    /// next sweep once its config materialises.
    /// </remarks>
    private static Dictionary<string, string> ParseConfig(JsonElement element)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!element.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var property in config.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                throw new JsonException("Incus inventory config values must be strings.");
            result[property.Name] = property.Value.GetString() ?? string.Empty;
        }
        return result;
    }

    private static bool IsOwned(IncusInstanceInfo instance, string kind) =>
        string.Equals(instance.Type, "virtual-machine", StringComparison.Ordinal)
        && GetConfig(instance.Config, ManagedKey) == "true"
        && GetConfig(instance.Config, KindKey) == kind;

    private static string? GetConfig(IReadOnlyDictionary<string, string> config, string key) =>
        config.TryGetValue(key, out var value) ? value : null;

    private static DateTimeOffset? ParseCreatedAt(IReadOnlyDictionary<string, string> config) =>
        config.TryGetValue(CreatedAtKey, out var created)
        && DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;

    internal sealed record IncusInstanceInfo(
        string Name,
        string Status,
        string Type,
        IReadOnlyDictionary<string, string> Config);

    private sealed record IncusStoragePoolInfo(
        string Name,
        string Driver,
        IReadOnlyDictionary<string, string> Config);
}
