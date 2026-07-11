using System.Collections.Concurrent;
using System.Globalization;
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
///
/// <para>
/// Host prerequisites: Incus 6.3 or newer (7.0 LTS recommended), Linux
/// kernel 5.6 or newer for openat2-backed restricted disk paths, KVM, Rust
/// <c>virtiofsd</c>, membership of the CodeyBox service identity in
/// <c>incus-admin</c>, a dedicated non-default Incus project, and a
/// pre-created ZFS or Btrfs pool. A missing project is created with exact
/// CodeyBox ownership/schema markers and restrictions; an existing project is
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
    internal const string SandboxKind = "sandbox";
    internal const string BaselineKind = "baseline";
    internal const string ReadySnapshot = "ready";

    private readonly Func<IncusSandboxOptions> _optionsAccessor;
    private readonly ILogger<IncusSandboxProvider> _log;
    private readonly IncusCliRunner _cli;
    private readonly ITimingStore? _timings;
    private readonly ISandboxResourceUsageStore? _resourceUsageStore;
    private readonly IDiskSpaceProbe _diskProbe;
    private readonly SemaphoreSlim _hostPreflightLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _baselineLocks = new(StringComparer.Ordinal);
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
        : this(() => options, log, timings, new IncusCliProcessRunner(), resourceUsageStore)
    {
    }

    public IncusSandboxProvider(
        Func<IncusSandboxOptions> optionsAccessor,
        ILogger<IncusSandboxProvider> log,
        ITimingStore? timings = null,
        ISandboxResourceUsageStore? resourceUsageStore = null)
        : this(optionsAccessor, log, timings, new IncusCliProcessRunner(), resourceUsageStore)
    {
    }

    internal IncusSandboxProvider(
        Func<IncusSandboxOptions> optionsAccessor,
        ILogger<IncusSandboxProvider> log,
        ITimingStore? timings,
        IProcessRunner runner,
        ISandboxResourceUsageStore? resourceUsageStore = null,
        IDiskSpaceProbe? diskProbe = null)
    {
        _optionsAccessor = optionsAccessor ?? throw new ArgumentNullException(nameof(optionsAccessor));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _timings = timings;
        _resourceUsageStore = resourceUsageStore;
        _diskProbe = diskProbe ?? new DefaultDiskSpaceProbe();
        _cli = new IncusCliRunner(runner);
        _ = ReadOptions();
    }

    public string Name => ProviderId;
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
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Flavor == SandboxProfileFlavor.Graphical)
            throw new NotSupportedException("The Incus provider currently supports headless work/audit/merge sandboxes only.");
        ValidateResourceLimits(spec.Limits);
        IncusInputValidation.ValidateAbsoluteGuestPath(spec.WorkingDirectory, nameof(spec.WorkingDirectory));
        IncusSandbox.ValidateEnvironment(spec.Environment, nameof(spec));
        spec = SandboxConventions.WithTimingEnvironment(spec);
        IncusSandbox.ValidateEnvironment(spec.Environment, nameof(spec));
        var options = ReadOptions();
        IncusHostIdentity.ValidateHostMountIdentity(options, spec.Mounts);
        await EnsureHostPreflightAsync(options, ct).ConfigureAwait(false);
        var bridge = ResolveBridge(options, spec.Network.ProfileName);
        var image = ResolveImage(options, spec.ImageReference);
        var name = CreateInstanceName(options);
        var stagingRoot = ResolveStagingRoot(options);
        var sandboxRoot = Path.Combine(stagingRoot, name);
        var timingStore = _timings is not null && spec.TimingWorkItemId.HasValue ? _timings : null;
        var timingItem = spec.TimingWorkItemId.GetValueOrDefault();
        var timingPhase = spec.TimingPhase ?? "work";
        string? baselineRef = null;
        var instanceBecameVisible = false;
        var stagingInitialized = false;

        MarkActive(name);
        try
        {
            CreateSecurePrivateDirectory(sandboxRoot);
            IncusMountStaging.InitializeOwnedTree(sandboxRoot, name, DateTimeOffset.UtcNow);
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
                var expected = IncusBaselineNaming.DeriveBaselineName(options, spec.Network.ProfileName!, spec.Flavor);
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

            await using (var mountTiming = await TimingScope.BeginAsync(
                timingStore, timingItem, timingPhase, "vm.mount", log: _log).ConfigureAwait(false))
            {
                await ApplyMountDevicesAsync(options, name, mountPlan.Mounts, ct).ConfigureAwait(false);
                await VerifyDeviceTopologyAsync(options, name, bridge, mountPlan.Mounts, ct).ConfigureAwait(false);
            }

            await using (var startTiming = await TimingScope.BeginAsync(
                timingStore, timingItem, timingPhase, "vm.start", log: _log).ConfigureAwait(false))
            {
                await StartAndWaitAsync(options, name, runCloudInit: true, ct).ConfigureAwait(false);
            }
            if (!canUseBaseline)
                await RunExtraRuncmdAsync(options, name, ct).ConfigureAwait(false);
            await ApplyGuestTmpfsMountsAsync(options, name, mountPlan.Mounts, ct).ConfigureAwait(false);
            await WaitForMountsAsync(options, name, mountPlan.Mounts, ct).ConfigureAwait(false);
            await CreateGuestLinksAsync(options, name, mountPlan.GuestLinks, ct).ConfigureAwait(false);

            var sandbox = new IncusSandbox(
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
                MarkInactive);
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

    public string? ResolveBaselineRef(string? profileName, SandboxProfileFlavor flavor)
    {
        var options = ReadOptions();
        if (!options.UseBaselineImages || string.IsNullOrWhiteSpace(profileName))
            return null;
        IncusInputValidation.ValidateDeviceName(profileName, nameof(profileName));
        if (!options.NetworkProfiles.ContainsKey(profileName))
            return null;
        if (flavor == SandboxProfileFlavor.Graphical)
            return null;
        return IncusBaselineNaming.DeriveBaselineName(options, profileName, flavor);
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
        if (string.IsNullOrWhiteSpace(baselineRef) || baselineRef.Length > 63)
            return false;
        try { IncusInputValidation.ValidateInstanceName(baselineRef, nameof(baselineRef)); }
        catch (ArgumentException) { return false; }
        if (!IncusBaselineNaming.TryNormalizeEffectivePrefix(baselineNamePrefix, out var prefix)
            || IncusBaselineNaming.OverlapsBakeCandidateNamespace(prefix))
        {
            return false;
        }
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
        var expected = IncusBaselineNaming.DeriveBaselineName(options, profileName, flavor);
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
        string liveBaselineName,
        string? pinnedBaselineRef,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pinnedBaselineRef)
            || string.Equals(pinnedBaselineRef, liveBaselineName, StringComparison.Ordinal))
        {
            return await EnsureBaselineAsync(
                options,
                profileName,
                flavor,
                liveBaselineName,
                ct).ConfigureAwait(false);
        }

        IncusInputValidation.ValidateInstanceName(pinnedBaselineRef, nameof(pinnedBaselineRef));
        var pinned = await FindInstanceAsync(options, pinnedBaselineRef, ct).ConfigureAwait(false);
        if (pinned is null)
        {
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
            var baselineHash = IncusBaselineNaming.ComputeConfigHash(options, profileName, flavor);
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
            var bakeToken = Guid.NewGuid().ToString("N");
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
                await VerifyDeviceTopologyAsync(options, candidateName, bridge, [], ct).ConfigureAwait(false);
                await StartAndWaitAsync(options, candidateName, runCloudInit: true, ct).ConfigureAwait(false);
                await RunExtraRuncmdAsync(options, candidateName, ct).ConfigureAwait(false);
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
                        _uncertainBaselines.TryAdd(candidateName, DateTimeOffset.UtcNow);
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

    private async Task StartAndWaitAsync(
        IncusSandboxOptions options,
        string name,
        bool runCloudInit,
        CancellationToken ct)
    {
        await _cli.RunCheckedAsync(
            "start VM",
            options,
            IncusCommandBuilder.Prefix(options, "start", name),
            stdin: null,
            options.VmStartTimeout,
            ct).ConfigureAwait(false);
        await WaitForAgentAsync(options, name, options.VmStartTimeout, ct).ConfigureAwait(false);
        await PrepareRuntimeDirectoryAsync(options, name, ct).ConfigureAwait(false);
        if (runCloudInit)
            await WaitForCloudInitAsync(options, name, ct).ConfigureAwait(false);
        await _cli.RunCheckedAsync(
            "verify Incus guest exec wrapper",
            options,
            BuildRootExec(options, name, ["test", "-x", IncusCloudInit.ExecWrapperPath]),
            stdin: null,
            options.OperationTimeout,
            ct,
            heavyOperation: false,
            maxStdoutBytes: 4096,
            maxStderrBytes: 4096).ConfigureAwait(false);
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

    private async Task WaitForAgentAsync(
        IncusSandboxOptions options,
        string name,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var probe = await _cli.RunAllowFailureAsync(
                options,
                BuildRootExec(options, name, ["/bin/true"]),
                stdin: null,
                options.OperationTimeout,
                ct,
                heavyOperation: false,
                maxStdoutBytes: 4096,
                maxStderrBytes: 4096).ConfigureAwait(false);
            if (probe.Success)
                return;
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException($"Incus VM '{name}' did not expose its guest agent within {timeout.TotalSeconds:F0} seconds.");
            await Task.Delay(options.ReadinessPollInterval, ct).ConfigureAwait(false);
        }
    }

    private async Task PrepareRuntimeDirectoryAsync(IncusSandboxOptions options, string name, CancellationToken ct)
    {
        await _cli.RunCheckedAsync(
            "prepare guest runtime directory",
            options,
            BuildRootExec(options, name,
            [
                "install", "-d", "-m", "0700",
                "-o", options.GuestUserId.ToString(CultureInfo.InvariantCulture),
                "-g", options.GuestGroupId.ToString(CultureInfo.InvariantCulture),
                IncusCloudInit.RuntimeDirectory,
            ]),
            stdin: null,
            options.OperationTimeout,
            ct,
            heavyOperation: false).ConfigureAwait(false);
        await _cli.RunCheckedAsync(
            "prepare guest exec control directory",
            options,
            BuildRootExec(options, name,
            [
                "install", "-d", "-m", "0700",
                "-o", "0", "-g", "0",
                IncusCloudInit.ControlDirectory,
            ]),
            stdin: null,
            options.OperationTimeout,
            ct,
            heavyOperation: false).ConfigureAwait(false);
        await _cli.RunCheckedAsync(
            "verify guest exec isolation utilities",
            options,
            BuildRootExec(options, name, ["test", "-x", "/usr/bin/setpriv", "-a", "-x", "/usr/bin/setsid"]),
            stdin: null,
            options.OperationTimeout,
            ct,
            heavyOperation: false).ConfigureAwait(false);
    }

    private async Task WaitForMountsAsync(
        IncusSandboxOptions options,
        string name,
        IReadOnlyList<IncusPreparedMount> mounts,
        CancellationToken ct)
    {
        for (var index = 0; index < mounts.Count; index++)
        {
            var mount = mounts[index];
            VerifyPinnedMountSource(options, mount);
            var deadline = DateTimeOffset.UtcNow + options.MountReadyTimeout;
            var lastReadinessStage = "filesystem type";
            while (true)
            {
                var findMount = await _cli.RunAllowFailureAsync(
                    options,
                    IncusCommandBuilder.BuildExec(
                        options,
                        name,
                        ["findmnt", "-n", "-o", "FSTYPE", "--target", mount.GuestPath]),
                    stdin: null,
                    options.OperationTimeout,
                    ct,
                    heavyOperation: false,
                    maxStdoutBytes: 4096,
                    maxStderrBytes: 4096).ConfigureAwait(false);
                var expectedFilesystem = mount.TmpfsSizeBytes.HasValue ? "tmpfs" : "virtiofs";
                var ready = findMount.Success
                    && string.Equals(findMount.Stdout.Trim(), expectedFilesystem, StringComparison.Ordinal);
                lastReadinessStage =
                    $"filesystem type (exit={findMount.ExitCode}, match={ready})";
                if (ready && mount.HostSource is not null)
                {
                    var readable = await _cli.RunAllowFailureAsync(
                        options,
                        IncusCommandBuilder.BuildExec(options, name, ["test", "-r", mount.GuestPath]),
                        stdin: null,
                        options.OperationTimeout,
                        ct,
                        heavyOperation: false,
                        maxStdoutBytes: 128,
                        maxStderrBytes: 4096).ConfigureAwait(false);
                    var traversable = await _cli.RunAllowFailureAsync(
                        options,
                        IncusCommandBuilder.BuildExec(options, name, ["test", "-x", mount.GuestPath]),
                        stdin: null,
                        options.OperationTimeout,
                        ct,
                        heavyOperation: false,
                        maxStdoutBytes: 128,
                        maxStderrBytes: 4096).ConfigureAwait(false);
                    ready = readable.Success && traversable.Success;
                    lastReadinessStage =
                        $"configured guest access (readExit={readable.ExitCode}, traverseExit={traversable.ExitCode})";
                }
                if (ready && mount.HostSource is not null)
                {
                    var mountOptions = await _cli.RunAllowFailureAsync(
                        options,
                        IncusCommandBuilder.BuildExec(
                            options,
                            name,
                            ["findmnt", "-n", "-o", "OPTIONS", "--target", mount.GuestPath]),
                        stdin: null,
                        options.OperationTimeout,
                        ct,
                        heavyOperation: false,
                        maxStdoutBytes: 4096,
                        maxStderrBytes: 4096).ConfigureAwait(false);
                    var optionSet = mountOptions.Stdout
                        .Trim()
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToHashSet(StringComparer.Ordinal);
                    var expectedAccess = mount.ReadOnly ? "ro" : "rw";
                    ready = mountOptions.Success && optionSet.Contains(expectedAccess);
                    lastReadinessStage =
                        $"mount access mode (exit={mountOptions.ExitCode}, expected={expectedAccess}, match={ready})";
                }
                if (ready && mount.HostSource is { } hostSource)
                {
                    var deviceName = BuildMountDeviceName(index);
                    var source = await _cli.RunAllowFailureAsync(
                        options,
                        IncusCommandBuilder.Prefix(options, "config", "device", "get", name, deviceName, "source"),
                        stdin: null,
                        options.OperationTimeout,
                        ct,
                        heavyOperation: false,
                        // The configured host source is path-validated but can
                        // legitimately exceed a small diagnostic buffer once
                        // it includes the private staging and sandbox names.
                        maxStdoutBytes: options.MaxCliStdoutBytes,
                        maxStderrBytes: 4096).ConfigureAwait(false);
                    var bus = await _cli.RunAllowFailureAsync(
                        options,
                        IncusCommandBuilder.Prefix(options, "config", "device", "get", name, deviceName, "io.bus"),
                        stdin: null,
                        options.OperationTimeout,
                        ct,
                        heavyOperation: false,
                        maxStdoutBytes: 128,
                        maxStderrBytes: 4096).ConfigureAwait(false);
                    ready = source.Success
                        && bus.Success
                        && string.Equals(source.Stdout.Trim(), hostSource, StringComparison.Ordinal)
                        && string.Equals(bus.Stdout.Trim(), "virtiofs", StringComparison.Ordinal);
                    lastReadinessStage =
                        $"device metadata (sourceExit={source.ExitCode}, sourceMatch={string.Equals(source.Stdout.Trim(), hostSource, StringComparison.Ordinal)}, " +
                        $"busExit={bus.ExitCode}, busMatch={string.Equals(bus.Stdout.Trim(), "virtiofs", StringComparison.Ordinal)})";
                    if (ready && mount.ReadinessProbe is { } probe)
                    {
                        var guestProbePath = $"{mount.GuestPath.TrimEnd('/')}/{probe.RelativeFilePath}";
                        var guestHash = await _cli.RunAllowFailureAsync(
                            options,
                            IncusCommandBuilder.BuildExec(options, name, ["sha256sum", "--", guestProbePath]),
                            stdin: null,
                            options.OperationTimeout,
                            ct,
                            heavyOperation: false,
                            maxStdoutBytes: 512,
                            maxStderrBytes: 4096).ConfigureAwait(false);
                        var guestHashText = guestHash.Stdout.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                        ready = guestHash.Success
                            && string.Equals(guestHashText, probe.ExpectedSha256, StringComparison.Ordinal);
                        lastReadinessStage =
                            $"host-to-guest identity hash (guestExit={guestHash.ExitCode}, match={string.Equals(guestHashText, probe.ExpectedSha256, StringComparison.Ordinal)})";
                    }
                    if (ready && mount.PinnedHostDirectory is { } pinnedDirectory)
                    {
                        var inode = await _cli.RunAllowFailureAsync(
                            options,
                            IncusCommandBuilder.BuildExec(
                                options,
                                name,
                                ["stat", "-Lc", "%i", "--", mount.GuestPath]),
                            stdin: null,
                            options.OperationTimeout,
                            ct,
                            heavyOperation: false,
                            maxStdoutBytes: 128,
                            maxStderrBytes: 4096).ConfigureAwait(false);
                        ready = inode.Success
                            && ulong.TryParse(
                                inode.Stdout.Trim(),
                                NumberStyles.None,
                                CultureInfo.InvariantCulture,
                                out var guestInode)
                            && guestInode == pinnedDirectory.Identity.Inode;
                        lastReadinessStage =
                            $"host-to-guest directory identity (guestExit={inode.ExitCode}, match={ready})";
                    }
                }
                if (ready)
                {
                    VerifyPinnedMountSource(options, mount);
                    break;
                }
                if (DateTimeOffset.UtcNow >= deadline)
                    throw new TimeoutException(
                        $"Incus mount '{mount.GuestPath}' did not pass its {lastReadinessStage} readiness check within the configured deadline.");
                await Task.Delay(options.ReadinessPollInterval, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task ApplyGuestTmpfsMountsAsync(
        IncusSandboxOptions options,
        string name,
        IReadOnlyList<IncusPreparedMount> mounts,
        CancellationToken ct)
    {
        foreach (var mount in mounts)
        {
            if (mount.TmpfsSizeBytes is not { } size)
                continue;
            await _cli.RunCheckedAsync(
                "create guest tmpfs mount point",
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
            var mountOptions = string.Join(',',
                $"size={size.ToString(CultureInfo.InvariantCulture)}",
                "mode=0700",
                $"uid={options.GuestUserId.ToString(CultureInfo.InvariantCulture)}",
                $"gid={options.GuestGroupId.ToString(CultureInfo.InvariantCulture)}");
            await _cli.RunCheckedAsync(
                "mount guest tmpfs",
                options,
                BuildRootExec(options, name, ["mount", "-t", "tmpfs", "-o", mountOptions, "tmpfs", mount.GuestPath]),
                stdin: null,
                options.OperationTimeout,
                ct,
                heavyOperation: false).ConfigureAwait(false);
        }
    }

    private async Task CreateGuestLinksAsync(
        IncusSandboxOptions options,
        string name,
        IReadOnlyList<IncusGuestLink> links,
        CancellationToken ct)
    {
        foreach (var link in links)
        {
            IncusInputValidation.ValidateAbsoluteGuestPath(link.Target, nameof(link.Target));
            IncusInputValidation.ValidateAbsoluteGuestPath(link.LinkPath, nameof(link.LinkPath));
            var parent = link.LinkPath[..link.LinkPath.LastIndexOf('/')];
            if (parent.Length == 0)
                parent = "/";
            await _cli.RunCheckedAsync(
                "create guest file-mount parent",
                options,
                IncusCommandBuilder.BuildExec(options, name, ["mkdir", "-p", "--", parent]),
                stdin: null,
                options.OperationTimeout,
                ct,
                heavyOperation: false).ConfigureAwait(false);
            await _cli.RunCheckedAsync(
                "create guest file-mount link",
                options,
                IncusCommandBuilder.BuildExec(options, name, ["ln", "-s", "--", link.Target, link.LinkPath]),
                stdin: null,
                options.OperationTimeout,
                ct,
                heavyOperation: false).ConfigureAwait(false);
        }
    }

    private async Task ApplyMountDevicesAsync(
        IncusSandboxOptions options,
        string name,
        IReadOnlyList<IncusPreparedMount> mounts,
        CancellationToken ct)
    {
        for (var index = 0; index < mounts.Count; index++)
        {
            var mount = mounts[index];
            if (mount.TmpfsSizeBytes.HasValue)
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

    private static void VerifyPinnedMountSource(
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

    private static void AppendCreationMetadata(
        ICollection<string> argv,
        string kind,
        string? baselineRef,
        string? baselineHash)
    {
        AddConfig(argv, ManagedKey, "true");
        AddConfig(argv, KindKey, kind);
        AddConfig(argv, CreatedAtKey, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options.OperationTimeout);
        try
        {
            while (await FindInstanceAsync(options, name, deadline.Token).ConfigureAwait(false) is not null)
                await Task.Delay(options.ReadinessPollInterval, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
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
        IReadOnlyList<string> command)
    {
        IncusInputValidation.ValidateInstanceName(name, nameof(name));
        if (command.Count == 0 || command.Any(item => item.Contains('\0')))
            throw new ArgumentException("Root exec command is empty or contains NUL.", nameof(command));
        var argv = IncusCommandBuilder.Prefix(options, "exec", name, "--");
        argv.AddRange(command);
        return argv;
    }

    private IncusSandboxOptions ReadOptions()
    {
        var options = _optionsAccessor()
            ?? throw new InvalidOperationException("Incus options accessor returned null.");
        var errors = IncusSandboxOptions.Validate(options);
        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid Incus configuration: " + string.Join(" ", errors));
        IncusCloudInit.ValidateExtraFragment(options.ExtraCloudInit);
        return options;
    }

    private static string? ResolveBridge(IncusSandboxOptions options, string? profileName)
    {
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

    internal static string ResolveImage(IncusSandboxOptions options, string imageReference) =>
        string.IsNullOrWhiteSpace(imageReference)
        || string.Equals(imageReference, "ignored", StringComparison.Ordinal)
            ? options.DefaultImage
            : imageReference;

    private static string ResolveStagingRoot(IncusSandboxOptions options)
    {
        var path = ResolveStagingRootPath(options);
        IncusMountStaging.EnsureOwnedStagingRoot(path);
        return path;
    }

    private static string ResolveStagingRootPath(IncusSandboxOptions options) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(options.StagingDirectory)
            ? ResolveDefaultStagingRoot()
            : options.StagingDirectory);

    private static string ResolveDefaultStagingRoot()
    {
        var stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrWhiteSpace(stateHome) && Path.IsPathFullyQualified(stateHome))
            return Path.Combine(stateHome, "codeybox", "incus-staging");
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home) && Path.IsPathFullyQualified(home))
            return Path.Combine(home, ".local", "state", "codeybox", "incus-staging");
        throw new InvalidOperationException(
            "Incus StagingDirectory must be configured when neither XDG_STATE_HOME nor HOME is an absolute path.");
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

    private static string CreateInstanceName(IncusSandboxOptions options)
    {
        const int virtiofsProjectAndInstanceBudget = 63;
        const int randomSuffixLength = 20;
        var suffix = Guid.NewGuid().ToString("N")[..randomSuffixLength];
        var normalized = NormalizedPrefix(options.InstanceNamePrefix);
        var maximumInstanceLength = virtiofsProjectAndInstanceBudget - options.ProjectName.Length;
        var maximumPrefixLength = maximumInstanceLength - suffix.Length;
        if (maximumPrefixLength < 1)
            throw new InvalidOperationException("The Incus project name leaves no safe virtiofs socket-path space for an instance name.");
        if (normalized.Length > maximumPrefixLength)
            normalized = normalized[..maximumPrefixLength];
        return normalized + suffix;
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

    private void MarkActive(string name) => _activeNames[name] = true;

    private void MarkInactive(string name)
    {
        _activeNames.TryRemove(name, out _);
        _activeOwners.TryRemove(name, out _);
    }

    private static bool ProjectListContains(string json, string projectName)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Any(element =>
            element.TryGetProperty("name", out var name)
            && string.Equals(name.GetString(), projectName, StringComparison.Ordinal));
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

    private static IReadOnlyList<IncusInstanceInfo> ParseInstances(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<IncusInstanceInfo>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (!element.TryGetProperty("name", out var nameElement) || nameElement.GetString() is not { } name)
                continue;
            var status = element.TryGetProperty("status", out var statusElement)
                ? statusElement.GetString() ?? string.Empty
                : string.Empty;
            var type = element.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString() ?? string.Empty
                : string.Empty;
            result.Add(new IncusInstanceInfo(name, status, type, ParseConfig(element)));
        }
        return result;
    }

    private static Dictionary<string, string> ParseConfig(JsonElement element)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!element.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var property in config.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
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

    private sealed record IncusInstanceInfo(
        string Name,
        string Status,
        string Type,
        IReadOnlyDictionary<string, string> Config);

    private sealed record IncusStoragePoolInfo(
        string Name,
        string Driver,
        IReadOnlyDictionary<string, string> Config);
}
