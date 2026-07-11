using System.Collections.Concurrent;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Api;

/// <summary>
/// Routes new sandbox work to a live provider selection while retaining every
/// provider activated during the process lifetime for inventory and cleanup.
/// Concrete provider registrations and the allowed selector set belong to the
/// API composition root; this router deals only in opaque provider IDs and
/// provider-neutral capabilities.
/// </summary>
/// <remarks>
/// A selected-provider failure is always propagated. The router never retries
/// an operation through another provider.
/// </remarks>
internal sealed class ReloadableSandboxProvider :
    ISandboxProvider,
    IActiveSandboxProvider,
    IActiveSandboxProgressProvider,
    IDiskGuardedSandboxProvider,
    ISuspendingSandboxProvider,
    IBaselineImageResolver,
    IBaselineImageProvisioner,
    IResourceMetricsCapturingProvider
{
    internal const int MaximumProviderIdLength = 128;
    internal const int MaximumRetainedInventoryProviders = 8;
    private const int MaximumManagedResourceNameLength = 256;

    private readonly Func<string> _selectedProviderId;
    private readonly Func<IReadOnlyCollection<string>> _retainedInventoryProviderIds;
    private readonly ILogger<ReloadableSandboxProvider> _log;
    private readonly IReadOnlyList<ProviderEntry> _providers;
    private readonly IReadOnlyDictionary<string, ProviderEntry> _providersById;
    private readonly ConcurrentDictionary<string, bool> _activatedProviders = new(StringComparer.Ordinal);

    internal ReloadableSandboxProvider(
        Func<string> selectedProviderId,
        Func<IReadOnlyCollection<string>> retainedInventoryProviderIds,
        IReadOnlyList<ProviderRegistration> registrations,
        ILogger<ReloadableSandboxProvider> log)
    {
        ArgumentNullException.ThrowIfNull(selectedProviderId);
        ArgumentNullException.ThrowIfNull(retainedInventoryProviderIds);
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(log);
        if (registrations.Count == 0)
            throw new ArgumentException("At least one reloadable sandbox provider is required.", nameof(registrations));

        _selectedProviderId = selectedProviderId;
        _retainedInventoryProviderIds = retainedInventoryProviderIds;
        _log = log;

        var providers = new List<ProviderEntry>(registrations.Count);
        var providersById = new Dictionary<string, ProviderEntry>(StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            ArgumentNullException.ThrowIfNull(registration);
            ValidateRegisteredProviderId(registration.Id);
            ArgumentNullException.ThrowIfNull(registration.Factory);
            ArgumentNullException.ThrowIfNull(registration.OwnsBaselineRef);
            var entry = new ProviderEntry(
                registration.Id,
                registration.Factory,
                registration.OwnsBaselineRef);
            if (!providersById.TryAdd(entry.Id, entry))
                throw new ArgumentException("Reloadable sandbox provider IDs must be unique.", nameof(registrations));
            providers.Add(entry);
        }

        _providers = providers.AsReadOnly();
        _providersById = providersById;
        _ = SelectedProvider;
    }

    public string Name => SelectedProvider.Provider.Name;

    public SandboxAgentOutputTransportKind AgentOutputTransportKind =>
        SelectedProvider.Provider.AgentOutputTransportKind;

    public SandboxBatchLaunchMode BatchLaunchMode =>
        SelectedProvider.Provider.BatchLaunchMode;

    public bool CapturesResourceMetrics =>
        SelectedProvider.ResourceMetrics.CapturesResourceMetrics;

    internal ISandboxProvider GetProvider(string providerId) =>
        ProviderByScopedId(providerId).Provider;

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var selected = SelectedProvider;
        var sandbox = await selected.Provider
            .CreateAsync(TranslateForeignBaselinePin(selected, spec), ct)
            .ConfigureAwait(false);
        return await RequireCreatedSandboxOwnerAsync(selected, sandbox).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
    {
        var listed = new List<ManagedSandboxInfo>();
        var failures = new List<Exception>();

        foreach (var provider in ActivatedProviders)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var providerSandboxes = await provider.Provider.ListAllManagedAsync(ct).ConfigureAwait(false);
                listed.AddRange(providerSandboxes.Select(sandbox =>
                    sandbox with { LifecycleProviderId = provider.Id }));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(ex);
                _log.LogWarning(
                    "Reloadable provider {ProviderId} failed to list managed sandboxes; inventory is partial ({FailureType})",
                    provider.Id,
                    ex.GetType().Name);
            }
        }

        if (failures.Count != 0)
        {
            throw new AggregateException(
                "A reloadable sandbox provider failed to list managed sandboxes; refusing to return partial lifecycle inventory.",
                failures);
        }

        return listed;
    }

    public async Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        ValidateManagedResourceName(name);
        var owner = await RequireOwnedProviderAsync(name, ct).ConfigureAwait(false);
        await owner.Provider.DisposeLeakedAsync(name, ct).ConfigureAwait(false);
    }

    public async Task DisposeLeakedAsync(ManagedSandboxInfo sandbox, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        var owner = await RequireOwnedProviderAsync(sandbox, ct).ConfigureAwait(false);
        await owner.Provider.DisposeLeakedAsync(sandbox.Name, ct).ConfigureAwait(false);
    }

    public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes() =>
        ActivatedProviders
            .SelectMany(static provider => provider.Active.SnapshotActiveSandboxes())
            .ToArray();

    public IReadOnlyList<ActiveSandboxProgress> SnapshotActiveSandboxProgress() =>
        ActivatedProviders
            .SelectMany(static provider => provider.Progress.SnapshotActiveSandboxProgress())
            .ToArray();

    public IReadOnlyList<DiskGuardSample> SampleDiskGuardState() =>
        ActivatedProviders
            .SelectMany(static provider => provider.DiskGuard.SampleDiskGuardState())
            .ToArray();

    public async Task ResumeSandboxAsync(string name, CancellationToken ct)
    {
        ValidateManagedResourceName(name);
        var owner = await RequireSuspendingOwnerAsync(name, ct).ConfigureAwait(false);
        await owner.Capability.ResumeSandboxAsync(name, ct).ConfigureAwait(false);
    }

    public async Task ResumeSandboxAsync(ManagedSandboxInfo sandbox, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        var owner = await RequireOwnedProviderAsync(sandbox, ct).ConfigureAwait(false);
        var suspending = RequireSuspendingCapability(owner);
        await suspending.ResumeSandboxAsync(sandbox.Name, ct).ConfigureAwait(false);
    }

    public async Task<int?> WaitForAdoptedAgentCompletionAsync(
        string vmName,
        string agentLogPath,
        Action<string>? logSink,
        TimeSpan? deadline,
        CancellationToken ct)
    {
        var owner = await RequireSuspendingOwnerAsync(vmName, ct).ConfigureAwait(false);
        return await owner.Capability.WaitForAdoptedAgentCompletionAsync(
            vmName,
            agentLogPath,
            logSink,
            deadline,
            ct).ConfigureAwait(false);
    }

    public async Task<bool> PushSuspendedVmCheckpointRefAsync(
        string vmName,
        string workingDir,
        string refName,
        string commitMessage,
        CancellationToken ct)
    {
        var owner = await RequireSuspendingOwnerAsync(vmName, ct).ConfigureAwait(false);
        return await owner.Capability.PushSuspendedVmCheckpointRefAsync(
            vmName,
            workingDir,
            refName,
            commitMessage,
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ReconcileStuckSandboxesAsync(
        IReadOnlySet<string> liveSuspendedNames,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(liveSuspendedNames);
        var reconciled = new HashSet<string>(StringComparer.Ordinal);

        foreach (var provider in ActivatedProviders)
        {
            if (provider.Suspending is not { } suspending)
                continue;

            var managed = await provider.Provider.ListAllManagedAsync(ct).ConfigureAwait(false);
            var ownedNames = managed
                .Select(static sandbox => sandbox.Name)
                .ToHashSet(StringComparer.Ordinal);
            var scopedLiveNames = liveSuspendedNames
                .Where(ownedNames.Contains)
                .ToHashSet(StringComparer.Ordinal);
            var providerReconciled = await suspending
                .ReconcileStuckSandboxesAsync(scopedLiveNames, ct)
                .ConfigureAwait(false);
            reconciled.UnionWith(providerReconciled);
        }

        return reconciled.ToArray();
    }

    public string? ResolveBaselineRef(string? profileName, SandboxProfileFlavor flavor) =>
        SelectedProvider.BaselineResolver.ResolveBaselineRef(profileName, flavor);

    public async Task<IReadOnlyList<BaselineImageInfo>> ListBaselineImagesAsync(CancellationToken ct)
    {
        var inventory = await ReadBaselineInventoryAsync(ct).ConfigureAwait(false);
        return inventory.Images;
    }

    public async Task DisposeBaselineImageAsync(string name, CancellationToken ct)
    {
        ValidateManagedResourceName(name);
        var inventory = await ReadBaselineInventoryAsync(ct).ConfigureAwait(false);
        inventory.Owners.TryGetValue(name, out var candidates);
        var owner = RequireSingleOwner(candidates, "baseline image");
        await owner.BaselineResolver.DisposeBaselineImageAsync(name, ct).ConfigureAwait(false);
    }

    public Task<string?> EnsureBaselineImageAsync(
        string profileName,
        SandboxProfileFlavor flavor,
        string? pinnedBaselineRef,
        CancellationToken ct)
    {
        var selected = SelectedProvider;
        var translated = TranslateForeignBaselinePin(
            selected,
            profileName,
            flavor,
            pinnedBaselineRef);
        return selected.BaselineProvisioner.EnsureBaselineImageAsync(
            profileName,
            flavor,
            translated,
            ct);
    }

    private async Task<BaselineInventory> ReadBaselineInventoryAsync(CancellationToken ct)
    {
        var listed = new List<BaselineImageInfo>();
        var owners = new Dictionary<string, List<ProviderEntry>>(StringComparer.Ordinal);
        var failures = new List<Exception>();

        foreach (var provider in ActivatedProviders)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var providerBaselines = await provider.BaselineResolver
                    .ListBaselineImagesAsync(ct)
                    .ConfigureAwait(false);
                foreach (var baseline in providerBaselines)
                {
                    listed.Add(baseline);
                    AddOwner(owners, baseline.Name, provider);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(ex);
                _log.LogWarning(
                    "Reloadable provider {ProviderId} failed to list baseline images; inventory is partial ({FailureType})",
                    provider.Id,
                    ex.GetType().Name);
            }
        }

        if (failures.Count != 0)
        {
            throw new AggregateException(
                "A reloadable sandbox provider failed to list baseline images; refusing to return partial baseline inventory.",
                failures);
        }

        return new BaselineInventory(listed, FreezeOwners(owners));
    }

    private SandboxSpec TranslateForeignBaselinePin(ProviderEntry selected, SandboxSpec spec)
    {
        var translated = TranslateForeignBaselinePin(
            selected,
            spec.Network.ProfileName,
            spec.Flavor,
            spec.BaselineImageRef);
        return string.Equals(translated, spec.BaselineImageRef, StringComparison.Ordinal)
            ? spec
            : spec with { BaselineImageRef = translated };
    }

    private string? TranslateForeignBaselinePin(
        ProviderEntry selected,
        string? profileName,
        SandboxProfileFlavor flavor,
        string? pinnedBaselineRef)
    {
        if (string.IsNullOrEmpty(pinnedBaselineRef)
            || selected.OwnsBaselineRef(pinnedBaselineRef))
        {
            return pinnedBaselineRef;
        }

        var foreignOwners = _providers
            .Where(provider => !ReferenceEquals(provider, selected)
                && provider.OwnsBaselineRef(pinnedBaselineRef))
            .ToArray();
        if (foreignOwners.Length == 0)
            return pinnedBaselineRef;
        if (foreignOwners.Length != 1)
        {
            throw new InvalidOperationException(
                "Multiple reloadable providers claim the queued baseline reference namespace.");
        }

        var translated = selected.BaselineResolver.ResolveBaselineRef(profileName, flavor);
        if (translated is null)
        {
            throw new InvalidOperationException(
                "The selected sandbox provider cannot translate a baseline pin owned by a previous provider for this profile and flavor.");
        }

        _log.LogInformation(
            "Translated queued baseline pin from {PreviousProvider} to {SelectedProvider} during sandbox-provider cutover",
            foreignOwners[0].Id,
            selected.Id);
        return translated;
    }

    private ProviderEntry SelectedProvider =>
        Activate(ProviderByConfiguredId(_selectedProviderId()));

    private IReadOnlyList<ProviderEntry> ActivatedProviders
    {
        get
        {
            _ = SelectedProvider;
            var retainedProviderIds = _retainedInventoryProviderIds()
                ?? throw new InvalidOperationException("The retained sandbox-provider inventory selector returned null.");
            if (retainedProviderIds.Count > MaximumRetainedInventoryProviders)
            {
                throw new InvalidOperationException(
                    "The retained sandbox-provider inventory exceeds the configured safety bound.");
            }
            foreach (var providerId in retainedProviderIds)
                _ = Activate(ProviderByConfiguredId(providerId));
            return _providers
                .Where(provider => _activatedProviders.ContainsKey(provider.Id))
                .ToArray();
        }
    }

    private ProviderEntry ProviderByConfiguredId(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)
            || providerId.Length > MaximumProviderIdLength)
        {
            throw new InvalidOperationException("The live sandbox provider selector is invalid.");
        }
        var normalized = providerId.Trim().ToLowerInvariant();
        if (!_providersById.TryGetValue(normalized, out var provider))
            throw new InvalidOperationException("The live sandbox provider selector is outside the registered reloadable set.");
        return provider;
    }

    private ProviderEntry ProviderByScopedId(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)
            || providerId.Length > MaximumProviderIdLength
            || !_providersById.TryGetValue(providerId, out var provider))
        {
            throw new InvalidOperationException("The managed sandbox snapshot has an unknown lifecycle provider.");
        }
        return Activate(provider);
    }

    private ProviderEntry Activate(ProviderEntry provider)
    {
        _activatedProviders.TryAdd(provider.Id, true);
        return provider;
    }

    private async Task<ProviderEntry> RequireOwnedProviderAsync(string name, CancellationToken ct)
    {
        ValidateManagedResourceName(name);
        var owners = new List<ProviderEntry>();
        foreach (var provider in ActivatedProviders)
        {
            var managed = await provider.Provider.ListAllManagedAsync(ct).ConfigureAwait(false);
            if (managed.Any(sandbox => string.Equals(sandbox.Name, name, StringComparison.Ordinal)))
                owners.Add(provider);
        }
        return RequireSingleOwner(owners.ToArray(), "managed sandbox");
    }

    private async Task<ProviderEntry> RequireOwnedProviderAsync(
        ManagedSandboxInfo sandbox,
        CancellationToken ct)
    {
        ValidateManagedResourceName(sandbox.Name);
        if (sandbox.LifecycleProviderId is null)
            return await RequireOwnedProviderAsync(sandbox.Name, ct).ConfigureAwait(false);

        var owner = ProviderByScopedId(sandbox.LifecycleProviderId);
        var managed = await owner.Provider.ListAllManagedAsync(ct).ConfigureAwait(false);
        if (!managed.Any(item => string.Equals(item.Name, sandbox.Name, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The scoped sandbox provider no longer reports the requested managed sandbox.");
        }
        return owner;
    }

    private async Task<SuspendingOwner> RequireSuspendingOwnerAsync(
        string name,
        CancellationToken ct)
    {
        var owner = await RequireOwnedProviderAsync(name, ct).ConfigureAwait(false);
        return new SuspendingOwner(owner, RequireSuspendingCapability(owner));
    }

    private static ISuspendingSandboxProvider RequireSuspendingCapability(ProviderEntry owner) =>
        owner.Suspending
        ?? throw new NotSupportedException(
            "The sandbox's owning provider does not support stopped-session resume.");

    private static ProviderEntry RequireSingleOwner(ProviderEntry[]? candidates, string resourceKind)
    {
        if (candidates is not { Length: > 0 })
            throw new InvalidOperationException($"No reloadable provider reported the requested {resourceKind}.");
        if (candidates.Length != 1)
        {
            throw new InvalidOperationException(
                $"Multiple reloadable providers reported the requested {resourceKind}; use a provider-scoped snapshot.");
        }
        return candidates[0];
    }

    private static void AddOwner(
        Dictionary<string, List<ProviderEntry>> owners,
        string name,
        ProviderEntry provider)
    {
        if (!owners.TryGetValue(name, out var entries))
        {
            entries = [];
            owners[name] = entries;
        }
        if (!entries.Contains(provider))
            entries.Add(provider);
    }

    private static Dictionary<string, ProviderEntry[]> FreezeOwners(
        Dictionary<string, List<ProviderEntry>> owners) =>
        owners.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToArray(),
            StringComparer.Ordinal);

    private static void ValidateRegisteredProviderId(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)
            || providerId.Length > MaximumProviderIdLength
            || !char.IsAsciiLetterOrDigit(providerId[0])
            || providerId.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')))
        {
            throw new ArgumentException(
                "Reloadable sandbox provider IDs must be bounded lowercase ASCII identifiers.",
                nameof(providerId));
        }
    }

    private static void ValidateManagedResourceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Length > MaximumManagedResourceNameLength
            || name.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Managed sandbox resource names must be non-empty, bounded, and contain no control characters.",
                nameof(name));
        }
    }

    private static async Task<ISandbox> RequireCreatedSandboxOwnerAsync(
        ProviderEntry selected,
        ISandbox sandbox)
    {
        IProviderOwnedSandbox? owner;
        try
        {
            owner = SandboxCapability.Find<IProviderOwnedSandbox>(sandbox);
        }
        catch (Exception validationError)
        {
            var contextual = new InvalidOperationException(
                "The selected provider returned a sandbox whose ownership capability could not be resolved.",
                validationError);
            await DisposeRejectedSandboxAsync(sandbox, contextual).ConfigureAwait(false);
            throw contextual;
        }

        if (owner is not null
            && string.Equals(owner.ProviderId, selected.Id, StringComparison.Ordinal))
        {
            return sandbox;
        }

        var mismatch = new InvalidOperationException(
            "The selected provider returned a sandbox without its exact registered ownership identity.");
        await DisposeRejectedSandboxAsync(sandbox, mismatch).ConfigureAwait(false);
        throw mismatch;
    }

    private static async Task DisposeRejectedSandboxAsync(ISandbox sandbox, Exception validationError)
    {
        try
        {
            await sandbox.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception cleanupError)
        {
            throw new AggregateException(
                "A reloadable provider returned an invalid sandbox and cleanup also failed.",
                validationError,
                cleanupError);
        }
    }

    private static T RequireCapability<T>(ISandboxProvider provider, string providerId)
        where T : class =>
        provider as T
        ?? throw new ArgumentException(
            $"The {providerId} provider does not expose required reloadable-provider capability {typeof(T).Name}.",
            nameof(provider));

    internal sealed record ProviderRegistration(
        string Id,
        Func<ISandboxProvider> Factory,
        Func<string, bool> OwnsBaselineRef);

    private sealed class ProviderEntry
    {
        private readonly Lazy<Capabilities> _capabilities;
        private readonly Func<string, bool> _ownsBaselineRef;

        internal ProviderEntry(
            string id,
            Func<ISandboxProvider> factory,
            Func<string, bool> ownsBaselineRef)
        {
            Id = id;
            _ownsBaselineRef = ownsBaselineRef;
            _capabilities = new Lazy<Capabilities>(
                () => CreateCapabilities(id, factory()),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        internal string Id { get; }
        internal ISandboxProvider Provider => _capabilities.Value.Provider;
        internal IActiveSandboxProvider Active => _capabilities.Value.Active;
        internal IActiveSandboxProgressProvider Progress => _capabilities.Value.Progress;
        internal IDiskGuardedSandboxProvider DiskGuard => _capabilities.Value.DiskGuard;
        internal ISuspendingSandboxProvider? Suspending => _capabilities.Value.Suspending;
        internal IBaselineImageResolver BaselineResolver => _capabilities.Value.BaselineResolver;
        internal IBaselineImageProvisioner BaselineProvisioner => _capabilities.Value.BaselineProvisioner;
        internal IResourceMetricsCapturingProvider ResourceMetrics => _capabilities.Value.ResourceMetrics;
        internal bool OwnsBaselineRef(string baselineRef) => _ownsBaselineRef(baselineRef);

        private static Capabilities CreateCapabilities(string id, ISandboxProvider provider)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (!string.Equals(provider.Name, id, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "A reloadable sandbox provider's registered ID must exactly match its Name.",
                    nameof(provider));
            }
            return new Capabilities(
                provider,
                RequireCapability<IActiveSandboxProvider>(provider, id),
                RequireCapability<IActiveSandboxProgressProvider>(provider, id),
                RequireCapability<IDiskGuardedSandboxProvider>(provider, id),
                provider as ISuspendingSandboxProvider,
                RequireCapability<IBaselineImageResolver>(provider, id),
                RequireCapability<IBaselineImageProvisioner>(provider, id),
                RequireCapability<IResourceMetricsCapturingProvider>(provider, id));
        }

        private sealed record Capabilities(
            ISandboxProvider Provider,
            IActiveSandboxProvider Active,
            IActiveSandboxProgressProvider Progress,
            IDiskGuardedSandboxProvider DiskGuard,
            ISuspendingSandboxProvider? Suspending,
            IBaselineImageResolver BaselineResolver,
            IBaselineImageProvisioner BaselineProvisioner,
            IResourceMetricsCapturingProvider ResourceMetrics);
    }

    private sealed record SuspendingOwner(
        ProviderEntry Provider,
        ISuspendingSandboxProvider Capability);

    private sealed record BaselineInventory(
        IReadOnlyList<BaselineImageInfo> Images,
        IReadOnlyDictionary<string, ProviderEntry[]> Owners);
}
