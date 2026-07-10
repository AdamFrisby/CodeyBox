using System.Collections.Concurrent;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeyBox.Api;

/// <summary>
/// Routes new sandbox work to the live <c>CodeyBox:SandboxProvider</c>
/// selection during a Multipass-to-Incus cutover. Existing sandbox handles
/// stay bound to the provider that created them, while inventory and cleanup
/// continue to cover both backends.
/// </summary>
/// <remarks>
/// This is deliberately an exact two-provider switch. It never retries an
/// operation against the other backend: a failure from the selected provider
/// is propagated to the caller. The immutable-options validator rejects every
/// runtime transition other than <c>multipass</c> &lt;-&gt; <c>incus</c>.
/// </remarks>
internal sealed class HotSwappableSandboxProvider :
    ISandboxProvider,
    IActiveSandboxProvider,
    IActiveSandboxProgressProvider,
    IDiskGuardedSandboxProvider,
    ISuspendingSandboxProvider,
    IBaselineImageResolver,
    IBaselineImageProvisioner,
    IResourceMetricsCapturingProvider
{
    internal const string MultipassProviderId = "multipass";
    internal const string IncusProviderId = "incus";

    private readonly IOptionsMonitor<CodeyBoxOptions> _options;
    private readonly ILogger<HotSwappableSandboxProvider> _log;
    private readonly ProviderEntry _multipass;
    private readonly ProviderEntry _incus;
    private readonly IReadOnlyList<ProviderEntry> _providers;
    private readonly ConcurrentDictionary<string, bool> _activatedProviders = new(StringComparer.Ordinal);

    public HotSwappableSandboxProvider(
        IOptionsMonitor<CodeyBoxOptions> options,
        ISandboxProvider multipass,
        ISandboxProvider incus,
        ILogger<HotSwappableSandboxProvider> log)
        : this(options, () => multipass, () => incus, log)
    {
    }

    internal HotSwappableSandboxProvider(
        IOptionsMonitor<CodeyBoxOptions> options,
        Func<ISandboxProvider> multipassFactory,
        Func<ISandboxProvider> incusFactory,
        ILogger<HotSwappableSandboxProvider> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(multipassFactory);
        ArgumentNullException.ThrowIfNull(incusFactory);
        ArgumentNullException.ThrowIfNull(log);

        _options = options;
        _log = log;
        _multipass = CreateEntry(MultipassProviderId, multipassFactory);
        _incus = CreateEntry(IncusProviderId, incusFactory);
        _providers = [_multipass, _incus];
        _ = SelectedProvider;
    }

    public string Name => SelectedProvider.Provider.Name;

    public SandboxAgentOutputTransportKind AgentOutputTransportKind =>
        SelectedProvider.Provider.AgentOutputTransportKind;

    public SandboxBatchLaunchMode BatchLaunchMode =>
        SelectedProvider.Provider.BatchLaunchMode;

    public bool CapturesResourceMetrics =>
        SelectedProvider.ResourceMetrics.CapturesResourceMetrics;

    // Explicit composition seams used by wiring tests that verify each
    // independently built provider received its required dependencies.
    internal ISandboxProvider MultipassProvider => _multipass.Provider;
    internal ISandboxProvider IncusProvider => _incus.Provider;

    public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var selected = SelectedProvider;
        return selected.Provider.CreateAsync(TranslateForeignBaselinePin(selected, spec), ct);
    }

    public async Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
    {
        var listed = new List<ManagedSandboxInfo>();
        var failures = new List<Exception>();

        var providers = ActivatedProviders;
        foreach (var provider in providers)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var providerSandboxes = await provider.Provider.ListAllManagedAsync(ct).ConfigureAwait(false);
                foreach (var sandbox in providerSandboxes)
                {
                    var scoped = sandbox with { LifecycleProviderId = provider.Id };
                    listed.Add(scoped);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(ex);
                _log.LogWarning(
                    "Cutover provider {ProviderId} failed to list managed sandboxes; inventory is partial ({FailureType})",
                    provider.Id,
                    ex.GetType().Name);
            }
        }

        if (failures.Count != 0)
        {
            throw new AggregateException(
                "A cutover sandbox provider failed to list managed sandboxes; refusing to return partial lifecycle inventory.",
                failures);
        }

        return listed;
    }

    public async Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Refresh immediately before the destructive action. This avoids
        // routing from a stale selection and lets the concrete provider perform
        // its own adjacent ownership check at the CLI sink.
        var listed = await ListAllManagedAsync(ct).ConfigureAwait(false);
        var candidates = listed
            .Where(sandbox => string.Equals(sandbox.Name, name, StringComparison.Ordinal))
            .Select(sandbox => ProviderById(sandbox.LifecycleProviderId!))
            .Distinct()
            .ToArray();
        var owner = RequireSingleOwner(candidates, "managed sandbox");
        await owner.Provider.DisposeLeakedAsync(name, ct).ConfigureAwait(false);
    }

    public async Task DisposeLeakedAsync(ManagedSandboxInfo sandbox, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        if (sandbox.LifecycleProviderId is null)
        {
            await DisposeLeakedAsync(sandbox.Name, ct).ConfigureAwait(false);
            return;
        }

        var owner = ProviderById(sandbox.LifecycleProviderId);
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

    // Incus intentionally does not claim RAM-suspend/adoption support. A
    // resume is routed to Multipass only after Multipass itself reports exact
    // ownership; an Incus or unknown ID never reaches the Multipass CLI sink.
    public async Task ResumeSandboxAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await RequireMultipassOwnershipAsync(name, ct).ConfigureAwait(false);
        await MultipassSuspending.ResumeSandboxAsync(name, ct).ConfigureAwait(false);
    }

    public async Task<int?> WaitForAdoptedAgentCompletionAsync(
        string vmName,
        string agentLogPath,
        Action<string>? logSink,
        TimeSpan? deadline,
        CancellationToken ct)
    {
        await RequireMultipassOwnershipAsync(vmName, ct).ConfigureAwait(false);
        return await MultipassSuspending.WaitForAdoptedAgentCompletionAsync(
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
        await RequireMultipassOwnershipAsync(vmName, ct).ConfigureAwait(false);
        return await MultipassSuspending.PushSuspendedVmCheckpointRefAsync(
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
        if (!_activatedProviders.ContainsKey(MultipassProviderId) && liveSuspendedNames.Count == 0)
            return [];
        _ = Activate(_multipass);
        var managed = await _multipass.Provider.ListAllManagedAsync(ct).ConfigureAwait(false);
        var ownedNames = managed.Select(static sandbox => sandbox.Name).ToHashSet(StringComparer.Ordinal);
        var scoped = liveSuspendedNames.Where(ownedNames.Contains).ToHashSet(StringComparer.Ordinal);
        return await MultipassSuspending.ReconcileStuckSandboxesAsync(scoped, ct).ConfigureAwait(false);
    }

    private async Task RequireMultipassOwnershipAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_activatedProviders.ContainsKey(IncusProviderId))
        {
            var incusManaged = await _incus.Provider.ListAllManagedAsync(ct).ConfigureAwait(false);
            if (incusManaged.Any(sandbox => string.Equals(sandbox.Name, name, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "Refusing to route an Incus-owned sandbox into a Multipass lifecycle operation.");
            }
        }
        _ = Activate(_multipass);
        var managed = await _multipass.Provider.ListAllManagedAsync(ct).ConfigureAwait(false);
        if (!managed.Any(sandbox => string.Equals(sandbox.Name, name, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Refusing a Multipass lifecycle operation for a sandbox that Multipass does not report as provider-owned.");
        }
    }

    public string? ResolveBaselineRef(string? profileName, SandboxProfileFlavor flavor)
    {
        var selected = SelectedProvider;
        return selected.BaselineResolver.ResolveBaselineRef(profileName, flavor);
    }

    public async Task<IReadOnlyList<BaselineImageInfo>> ListBaselineImagesAsync(CancellationToken ct)
    {
        var inventory = await ReadBaselineInventoryAsync(ct).ConfigureAwait(false);
        return inventory.Images;
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
                    "Cutover provider {ProviderId} failed to list baseline images; inventory is partial ({FailureType})",
                    provider.Id,
                    ex.GetType().Name);
            }
        }

        if (failures.Count != 0)
        {
            throw new AggregateException(
                "A cutover sandbox provider failed to list baseline images; refusing to return partial baseline inventory.",
                failures);
        }

        return new BaselineInventory(listed, FreezeOwners(owners));
    }

    public async Task DisposeBaselineImageAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // BaselineImageInfo predates provider-scoped routing metadata. Refresh
        // both inventories and refuse ambiguous names rather than broadcasting
        // a destructive delete across backends.
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
        pinnedBaselineRef = TranslateForeignBaselinePin(
            selected,
            profileName,
            flavor,
            pinnedBaselineRef);
        return selected.BaselineProvisioner.EnsureBaselineImageAsync(
            profileName,
            flavor,
            pinnedBaselineRef,
            ct);
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
            || selected.BaselineNamespace.OwnsBaselineRef(pinnedBaselineRef))
            return pinnedBaselineRef;

        var other = ReferenceEquals(selected, _multipass) ? _incus : _multipass;
        if (!other.BaselineNamespace.OwnsBaselineRef(pinnedBaselineRef))
            return pinnedBaselineRef;

        var translated = selected.BaselineResolver.ResolveBaselineRef(profileName, flavor);
        if (translated is null)
        {
            throw new InvalidOperationException(
                "The selected sandbox provider cannot translate a baseline pin owned by the previous cutover provider for this profile and flavor.");
        }

        _log.LogInformation(
            "Translated queued baseline pin from {PreviousProvider} to {SelectedProvider} during sandbox-provider cutover",
            other.Id,
            selected.Id);
        return translated;
    }

    private ProviderEntry SelectedProvider
    {
        get
        {
            var live = _options.CurrentValue;
            if (live.Incus?.IncludeMultipassCutoverInventory == true)
                _ = Activate(_multipass);
            var kind = live.SandboxProvider?.Trim();
            if (string.Equals(kind, MultipassProviderId, StringComparison.OrdinalIgnoreCase))
                return Activate(_multipass);
            if (string.Equals(kind, IncusProviderId, StringComparison.OrdinalIgnoreCase))
                return Activate(_incus);

            throw new InvalidOperationException(
                "The live sandbox provider selector is outside the allowed Multipass/Incus cutover set.");
        }
    }

    private ProviderEntry ProviderById(string providerId)
    {
        if (string.Equals(providerId, MultipassProviderId, StringComparison.Ordinal))
            return Activate(_multipass);
        if (string.Equals(providerId, IncusProviderId, StringComparison.Ordinal))
            return Activate(_incus);

        throw new InvalidOperationException("The managed sandbox snapshot has an unknown lifecycle provider.");
    }

    private IReadOnlyList<ProviderEntry> ActivatedProviders
    {
        get
        {
            if (_options.CurrentValue.Incus?.IncludeMultipassCutoverInventory == true)
                _ = Activate(_multipass);
            return _providers.Where(provider => _activatedProviders.ContainsKey(provider.Id)).ToArray();
        }
    }

    private ProviderEntry Activate(ProviderEntry provider)
    {
        _activatedProviders.TryAdd(provider.Id, true);
        return provider;
    }

    private ISuspendingSandboxProvider MultipassSuspending =>
        _multipass.Provider as ISuspendingSandboxProvider
        ?? throw new InvalidOperationException(
            "The activated Multipass provider does not expose suspend/resume lifecycle support.");

    private static ProviderEntry RequireSingleOwner(ProviderEntry[]? candidates, string resourceKind)
    {
        if (candidates is not { Length: > 0 })
            throw new InvalidOperationException($"No cutover provider reported the requested {resourceKind}.");
        if (candidates.Length != 1)
            throw new InvalidOperationException($"Multiple cutover providers reported the requested {resourceKind}; use a provider-scoped snapshot.");
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

    private static ProviderEntry CreateEntry(string id, Func<ISandboxProvider> factory) =>
        new(id, factory);

    private static T RequireCapability<T>(ISandboxProvider provider, string providerId)
        where T : class =>
        provider as T
        ?? throw new ArgumentException(
            $"The {providerId} provider does not expose required cutover capability {typeof(T).Name}.",
            nameof(provider));

    private sealed class ProviderEntry
    {
        private readonly Lazy<Capabilities> _capabilities;

        internal ProviderEntry(string id, Func<ISandboxProvider> factory)
        {
            Id = id;
            _capabilities = new Lazy<Capabilities>(
                () => CreateCapabilities(id, factory()),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        internal string Id { get; }
        internal ISandboxProvider Provider => _capabilities.Value.Provider;
        internal IActiveSandboxProvider Active => _capabilities.Value.Active;
        internal IActiveSandboxProgressProvider Progress => _capabilities.Value.Progress;
        internal IDiskGuardedSandboxProvider DiskGuard => _capabilities.Value.DiskGuard;
        internal IBaselineImageResolver BaselineResolver => _capabilities.Value.BaselineResolver;
        internal IBaselineRefNamespace BaselineNamespace => _capabilities.Value.BaselineNamespace;
        internal IBaselineImageProvisioner BaselineProvisioner => _capabilities.Value.BaselineProvisioner;
        internal IResourceMetricsCapturingProvider ResourceMetrics => _capabilities.Value.ResourceMetrics;

        private static Capabilities CreateCapabilities(string id, ISandboxProvider provider)
        {
            ArgumentNullException.ThrowIfNull(provider);
            return new Capabilities(
                provider,
                RequireCapability<IActiveSandboxProvider>(provider, id),
                RequireCapability<IActiveSandboxProgressProvider>(provider, id),
                RequireCapability<IDiskGuardedSandboxProvider>(provider, id),
                RequireCapability<IBaselineImageResolver>(provider, id),
                RequireCapability<IBaselineRefNamespace>(provider, id),
                RequireCapability<IBaselineImageProvisioner>(provider, id),
                RequireCapability<IResourceMetricsCapturingProvider>(provider, id));
        }

        private sealed record Capabilities(
            ISandboxProvider Provider,
            IActiveSandboxProvider Active,
            IActiveSandboxProgressProvider Progress,
            IDiskGuardedSandboxProvider DiskGuard,
            IBaselineImageResolver BaselineResolver,
            IBaselineRefNamespace BaselineNamespace,
            IBaselineImageProvisioner BaselineProvisioner,
            IResourceMetricsCapturingProvider ResourceMetrics);
    }

    private sealed record BaselineInventory(
        IReadOnlyList<BaselineImageInfo> Images,
        IReadOnlyDictionary<string, ProviderEntry[]> Owners);
}
