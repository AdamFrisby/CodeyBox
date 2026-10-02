using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Thrown when a disposal cannot be verified because a provider that might own
/// the sandbox could not confirm its full inventory. Deferring — not a
/// disposal failure — so the leak reaper keeps the sandbox listed and retries
/// on a later sweep instead of emitting per-sandbox failure events.
/// </summary>
public sealed class SandboxInventoryVerificationException : InvalidOperationException
{
    public SandboxInventoryVerificationException(string providerId, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ProviderId = providerId;
    }

    /// <summary>Composite provider id that could not verify its inventory.</summary>
    public string ProviderId { get; }
}

/// <summary>
/// Read/dispose-only provider used by lifecycle services that need to sweep
/// sandboxes owned by more than one execution fleet.
/// </summary>
public sealed class CompositeManagedSandboxProvider : IManagedSandboxLifecycle
{
    private const string NestedProviderIdPrefix = "nested:";
    private static readonly TimeSpan DefaultInventoryFailureWarningInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MinimumInventoryFailureWarningInterval = TimeSpan.FromMinutes(1);
    private readonly IReadOnlyList<ProviderEntry> _providers;
    private readonly IReadOnlyDictionary<string, ProviderEntry> _providersById;
    private readonly Func<IManagedSandboxLifecycle, bool>? _shouldInventory;
    private readonly Func<SandboxLeakOptions>? _optionsAccessor;
    private readonly ILogger<CompositeManagedSandboxProvider> _log;
    private readonly TimeProvider _time;
    private readonly object _lastListLock = new();
    private Dictionary<string, ProviderEntry[]> _lastReportedByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastInventoryFailureWarning = new(StringComparer.Ordinal);

    public CompositeManagedSandboxProvider(IEnumerable<IManagedSandboxLifecycle> providers)
        : this(providers, shouldInventory: null, optionsAccessor: null, log: null, timeProvider: null)
    {
    }

    /// <param name="providers">Constituent lifecycles, deduplicated by reference.</param>
    /// <param name="shouldInventory">
    /// Live predicate selecting which providers participate in listing and
    /// disposal verification. Providers it rejects are skipped entirely: never
    /// listed, never queried for verification, and never blocking disposal of
    /// other providers' sandboxes. Evaluated on every operation so catalog
    /// edits take effect without rebuilding this composite. Null inventories
    /// every provider. A predicate that throws is treated as approval for that
    /// provider (fail closed).
    /// </param>
    /// <param name="optionsAccessor">Live leak options for the failure-warning throttle.</param>
    /// <param name="log">Sink for the rate-limited inventory-failure warning.</param>
    /// <param name="timeProvider">Clock for the warning throttle.</param>
    public CompositeManagedSandboxProvider(
        IEnumerable<IManagedSandboxLifecycle> providers,
        Func<IManagedSandboxLifecycle, bool>? shouldInventory,
        Func<SandboxLeakOptions>? optionsAccessor = null,
        ILogger<CompositeManagedSandboxProvider>? log = null,
        TimeProvider? timeProvider = null)
    {
        var lifecycles = providers
            .Where(static p => p is not null)
            .Distinct(ReferenceEqualityComparer.Instance)
            .ToArray();

        var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _providers = lifecycles
            .Select(provider =>
            {
                var count = nameCounts.TryGetValue(provider.Name, out var current) ? current + 1 : 1;
                nameCounts[provider.Name] = count;
                var id = count == 1 ? provider.Name : $"{provider.Name}#{count}";
                return new ProviderEntry(id, provider);
            })
            .ToArray();
        _providersById = _providers.ToDictionary(static p => p.Id, StringComparer.Ordinal);
        _shouldInventory = shouldInventory;
        _optionsAccessor = optionsAccessor;
        _log = log ?? NullLogger<CompositeManagedSandboxProvider>.Instance;
        _time = timeProvider ?? TimeProvider.System;
    }

    public string Name => "composite-managed";

    public bool MightOwnSandbox(string name, string? hostId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _providers.Any(p => IsInventoried(p) && SafeMightOwn(p.Lifecycle, name, hostId));
    }

    public IReadOnlyList<IManagedSandboxLifecycle> Providers => _providers.Select(static p => p.Lifecycle).ToArray();

    public async Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
        await ListManagedInventoryAsync(ct).ConfigureAwait(false);

    public async Task<ManagedSandboxInventory> ListManagedInventoryAsync(CancellationToken ct)
    {
        var result = new List<ManagedSandboxInfo>();
        var failures = new List<Exception>();
        var unverifiableProviders = new HashSet<ProviderEntry>();
        var reportedByName = new Dictionary<string, List<ProviderEntry>>(StringComparer.Ordinal);
        var inventoriedHostIds = new HashSet<string>(StringComparer.Ordinal);
        var completedProviderCount = 0;
        var inventoriedProviderCount = 0;
        var allProviderInventoriesComplete = true;
        foreach (var provider in _providers)
        {
            if (!IsInventoried(provider))
                continue;
            inventoriedProviderCount++;
            ct.ThrowIfCancellationRequested();
            try
            {
                var inventory = await provider.Lifecycle.ListManagedInventoryAsync(ct).ConfigureAwait(false);
                completedProviderCount++;
                if (!inventory.IsComplete)
                {
                    allProviderInventoriesComplete = false;
                    unverifiableProviders.Add(provider);
                    NoteInventoryFailure(provider.Id, "partial inventory");
                }
                foreach (var hostId in inventory.InventoriedHostIds)
                    inventoriedHostIds.Add(hostId);

                foreach (var info in inventory)
                {
                    // A lifecycle can itself be a composite (the production
                    // admission wrapper around a reloadable provider router is
                    // one). Preserve that inner route in the opaque
                    // provider ID instead of flattening both backends to the
                    // same outer provider.
                    var scopedProviderId = info.LifecycleProviderId is null
                        ? provider.Id
                        : EncodeNestedProviderId(provider.Id, info.LifecycleProviderId);
                    var scoped = info with { LifecycleProviderId = scopedProviderId };
                    result.Add(scoped);
                    if (!reportedByName.TryGetValue(scoped.Name, out var entries))
                    {
                        entries = new List<ProviderEntry>();
                        reportedByName[scoped.Name] = entries;
                    }
                    if (!entries.Contains(provider))
                        entries.Add(provider);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(ex);
                unverifiableProviders.Add(provider);
                NoteInventoryFailure(provider.Id, ex.Message);
            }
        }

        if (result.Count == 0 && failures.Count == inventoriedProviderCount && failures.Count > 0)
            throw new AggregateException("Every managed sandbox provider failed to list sandboxes.", failures);

        lock (_lastListLock)
        {
            var merged = reportedByName.ToDictionary(
                static kvp => kvp.Key,
                static kvp => kvp.Value.ToArray(),
                StringComparer.Ordinal);
            // A provider that could not verify its inventory may hide names it
            // previously reported, so its last-known entries stay sticky until
            // it reports a complete inventory again. Providers that completed
            // keep only their fresh entries — a complete view proves absence.
            if (unverifiableProviders.Count > 0)
            {
                foreach (var (name, previous) in _lastReportedByName)
                {
                    foreach (var entry in previous)
                    {
                        if (!unverifiableProviders.Contains(entry))
                            continue;
                        if (!merged.TryGetValue(name, out var current))
                        {
                            merged[name] = [entry];
                        }
                        else if (!current.Contains(entry))
                        {
                            merged[name] = [.. current, entry];
                        }
                    }
                }
            }
            _lastReportedByName = merged;
        }

        return new ManagedSandboxInventory(
            result,
            isComplete: inventoriedProviderCount > 0
                && completedProviderCount == inventoriedProviderCount
                && allProviderInventoriesComplete,
            inventoriedHostIds: inventoriedHostIds);
    }

    public async Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        var reporters = await RequireNotOwnedByLiveWorkAsync(name, hostId: null, ownerProviderId: null, ct).ConfigureAwait(false);

        ProviderEntry[]? candidates = reporters.Length > 0 ? reporters : LastReportedCandidates(name);
        candidates = FilterInventoried(candidates);

        if (candidates is not { Length: > 0 })
        {
            if (InventoriedEntries().Count == 1)
            {
                await InventoriedEntries()[0].Lifecycle.DisposeLeakedAsync(name, ct).ConfigureAwait(false);
                return;
            }

            throw new InvalidOperationException($"No managed sandbox provider reported leaked sandbox '{name}' in the latest list.");
        }

        if (candidates.Length > 1)
            throw new InvalidOperationException($"Leaked sandbox '{name}' was reported by multiple providers; dispose using the provider-scoped snapshot.");

        await candidates[0].Lifecycle.DisposeLeakedAsync(name, ct).ConfigureAwait(false);
    }

    public async Task DisposeLeakedAsync(ManagedSandboxInfo sandbox, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        if (sandbox.LifecycleProviderId is null)
        {
            await DisposeLeakedAsync(sandbox.Name, ct).ConfigureAwait(false);
            return;
        }

        var outerProviderId = sandbox.LifecycleProviderId;
        if (TryDecodeNestedProviderId(
                sandbox.LifecycleProviderId,
                out var decodedOuterProviderId,
                out _))
        {
            outerProviderId = decodedOuterProviderId;
        }

        if (!_providersById.TryGetValue(outerProviderId, out var provider))
            throw new InvalidOperationException($"Unknown managed sandbox provider '{sandbox.LifecycleProviderId}' for leaked sandbox '{sandbox.Name}'.");

        if (!IsInventoried(provider))
            throw new SandboxInventoryVerificationException(
                provider.Id,
                $"Refusing to dispose managed sandbox '{sandbox.Name}': provider '{provider.Id}' is not inventoried by the current sandbox configuration and its inventory cannot be verified.");

        _ = await RequireNotOwnedByLiveWorkAsync(sandbox.Name, sandbox.HostId, outerProviderId, ct).ConfigureAwait(false);

        string? innerProviderId = null;
        if (TryDecodeNestedProviderId(
                sandbox.LifecycleProviderId,
                out _,
                out var decodedInnerProviderId))
        {
            innerProviderId = decodedInnerProviderId;
        }

        // Strip this composite's scope and pass the inner snapshot through.
        // Calling the name-only overload here would make a nested composite
        // rediscover ownership and become ambiguous when two backends use the
        // same configured instance name.
        await provider.Lifecycle.DisposeLeakedAsync(
            sandbox with { LifecycleProviderId = innerProviderId },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Destructive-disposal guard. The leak sweep's <c>IsTrackedActive</c> flag
    /// is a snapshot of a per-provider in-memory registry that can silently
    /// lose (or never gain) an entry, so it must not be the only thing
    /// standing between a running agent and VM deletion. Before routing any
    /// disposal this re-verifies the name against live phase/worker state:
    /// every constituent lifecycle's active-work snapshot, plus a fresh
    /// managed inventory where ANY provider reporting the name as
    /// tracked-active vetoes the delete.
    ///
    /// Inventory-completeness failures are scoped per provider. A provider
    /// whose inventory is incomplete or unavailable (e.g. an unreachable
    /// executor host on a multi-host backend) vetoes disposal only of
    /// sandboxes it might own — its own provider-scoped snapshots, and
    /// unscoped names its namespace could claim — since a partial view can
    /// hide the tracked-active entry that proves the VM live. A failing
    /// unrelated provider never blocks disposal of another healthy provider's
    /// leaks: the sweep retries the deferred sandbox on its next pass rather
    /// than deleting an unverifiable VM.
    /// </summary>
    /// <param name="ownerProviderId">
    /// Composite id of the provider that reported the sandbox, for
    /// provider-scoped disposals; null for name-only disposals whose owner is
    /// unknown and which therefore stay fail-closed on any unverifiable
    /// provider that could own the name.
    /// </param>
    /// <returns>
    /// The lifecycles whose fresh inventory reported <paramref name="name"/> —
    /// the same evidence the last sweep's reported-by-name map holds, but
    /// current, so a caller that has not swept recently can still route.
    /// </returns>
    private async Task<ProviderEntry[]> RequireNotOwnedByLiveWorkAsync(
        string name,
        string? hostId,
        string? ownerProviderId,
        CancellationToken ct)
    {
        var liveNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in _providers)
        {
            if (provider.Lifecycle is IActiveSandboxProvider active)
            {
                foreach (var entry in active.SnapshotActiveSandboxes())
                    liveNames.Add(entry.Sandbox.Id);
            }
            if (provider.Lifecycle is IActiveSandboxProgressProvider progress)
            {
                foreach (var entry in progress.SnapshotActiveSandboxProgress())
                    liveNames.Add(entry.SandboxId);
            }
        }
        if (liveNames.Contains(name))
        {
            throw new InvalidOperationException(
                $"Refusing to dispose managed sandbox '{name}': a live work phase still owns it.");
        }

        var reporters = new List<ProviderEntry>();
        var failedProviders = new List<(ProviderEntry Entry, Exception Error)>();
        var incompleteProviders = new List<(ProviderEntry Entry, ManagedSandboxInventory Inventory)>();
        foreach (var provider in _providers)
        {
            if (!IsInventoried(provider))
                continue;
            ManagedSandboxInventory inventory;
            try
            {
                inventory = await provider.Lifecycle.ListManagedInventoryAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failedProviders.Add((provider, ex));
                NoteInventoryFailure(provider.Id, ex.Message);
                continue;
            }
            if (!inventory.IsComplete)
            {
                incompleteProviders.Add((provider, inventory));
                NoteInventoryFailure(provider.Id, "partial inventory");
            }
            foreach (var info in inventory)
            {
                if (!string.Equals(info.Name, name, StringComparison.Ordinal))
                    continue;
                // One provider can legitimately list the same name more than
                // once (e.g. the remote multipass provider keys active entries
                // by (host, name)); dedupe so a single reporter cannot later
                // read as "multiple providers" to the routing ambiguity check.
                if (!reporters.Contains(provider))
                    reporters.Add(provider);
                if (info.IsTrackedActive
                    && (hostId is null || string.Equals(info.HostId, hostId, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(
                        $"Refusing to dispose managed sandbox '{name}': provider '{provider.Id}' reports it tracked-active.");
                }
            }
        }

        if (ownerProviderId is not null)
            ThrowIfOwnerUnverifiable(name, hostId, ownerProviderId, failedProviders, incompleteProviders);
        else
            ThrowIfUnscopedUnverifiable(name, hostId, failedProviders, incompleteProviders);

        foreach (var (entry, inventory) in incompleteProviders)
        {
            if (string.Equals(entry.Id, ownerProviderId, StringComparison.Ordinal))
                continue;
            if (ReportsName(inventory, name)
                || (SafeMightOwn(entry.Lifecycle, name, hostId) && PreviouslyReportedBy(entry, name)))
            {
                throw new SandboxInventoryVerificationException(
                    entry.Id,
                    $"Refusing to dispose managed sandbox '{name}': provider '{entry.Id}' could not verify its full inventory.");
            }
        }
        foreach (var (entry, error) in failedProviders)
        {
            if (string.Equals(entry.Id, ownerProviderId, StringComparison.Ordinal))
                continue;
            if (SafeMightOwn(entry.Lifecycle, name, hostId) && PreviouslyReportedBy(entry, name))
            {
                throw new SandboxInventoryVerificationException(
                    entry.Id,
                    $"Refusing to dispose managed sandbox '{name}': provider '{entry.Id}' could not verify its full inventory.",
                    error);
            }
        }
        return reporters.ToArray();
    }

    private static void ThrowIfOwnerUnverifiable(
        string name,
        string? hostId,
        string ownerProviderId,
        List<(ProviderEntry Entry, Exception Error)> failedProviders,
        List<(ProviderEntry Entry, ManagedSandboxInventory Inventory)> incompleteProviders)
    {
        foreach (var (entry, error) in failedProviders)
        {
            if (string.Equals(entry.Id, ownerProviderId, StringComparison.Ordinal))
            {
                throw new SandboxInventoryVerificationException(
                    entry.Id,
                    $"Refusing to dispose managed sandbox '{name}': provider '{entry.Id}' could not verify its full inventory.",
                    error);
            }
        }
        foreach (var (entry, inventory) in incompleteProviders)
        {
            if (string.Equals(entry.Id, ownerProviderId, StringComparison.Ordinal)
                && (hostId is null || !inventory.InventoriedHostIds.Contains(hostId)))
            {
                throw new SandboxInventoryVerificationException(
                    entry.Id,
                    $"Refusing to dispose managed sandbox '{name}': provider '{entry.Id}' could not verify its full inventory.");
            }
        }
    }

    private static void ThrowIfUnscopedUnverifiable(
        string name,
        string? hostId,
        List<(ProviderEntry Entry, Exception Error)> failedProviders,
        List<(ProviderEntry Entry, ManagedSandboxInventory Inventory)> incompleteProviders)
    {
        foreach (var (entry, error) in failedProviders)
        {
            if (SafeMightOwn(entry.Lifecycle, name, hostId))
            {
                throw new SandboxInventoryVerificationException(
                    entry.Id,
                    $"Refusing to dispose managed sandbox '{name}': provider '{entry.Id}' could not verify its full inventory.",
                    error);
            }
        }
        foreach (var (entry, inventory) in incompleteProviders)
        {
            if ((hostId is null || !inventory.InventoriedHostIds.Contains(hostId))
                && SafeMightOwn(entry.Lifecycle, name, hostId))
            {
                throw new SandboxInventoryVerificationException(
                    entry.Id,
                    $"Refusing to dispose managed sandbox '{name}': provider '{entry.Id}' could not verify its full inventory.");
            }
        }
    }

    private static bool ReportsName(ManagedSandboxInventory inventory, string name)
    {
        foreach (var info in inventory)
        {
            if (string.Equals(info.Name, name, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private bool PreviouslyReportedBy(ProviderEntry entry, string name)
    {
        lock (_lastListLock)
        {
            return _lastReportedByName.TryGetValue(name, out var candidates)
                && Array.IndexOf(candidates, entry) >= 0;
        }
    }

    private bool IsInventoried(ProviderEntry entry)
    {
        var predicate = _shouldInventory;
        if (predicate is null)
            return true;
        try
        {
            return predicate(entry.Lifecycle);
        }
        catch
        {
            return true;
        }
    }

    private IReadOnlyList<ProviderEntry> InventoriedEntries()
    {
        var result = new List<ProviderEntry>(_providers.Count);
        foreach (var provider in _providers)
        {
            if (IsInventoried(provider))
                result.Add(provider);
        }
        return result;
    }

    private ProviderEntry[]? FilterInventoried(ProviderEntry[]? candidates)
    {
        if (candidates is null)
            return null;
        var result = new List<ProviderEntry>(candidates.Length);
        foreach (var candidate in candidates)
        {
            if (IsInventoried(candidate))
                result.Add(candidate);
        }
        return result.Count == 0 ? null : result.ToArray();
    }

    private static bool SafeMightOwn(IManagedSandboxLifecycle lifecycle, string name, string? hostId)
    {
        try
        {
            return lifecycle.MightOwnSandbox(name, hostId);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Rate-limited inventory-failure warning: repeated failures from one
    /// provider emit one warning per interval, not one per sandbox per sweep.
    /// </summary>
    private void NoteInventoryFailure(string providerId, string reason)
    {
        var interval = _optionsAccessor?.Invoke()?.InventoryFailureWarningInterval
            ?? DefaultInventoryFailureWarningInterval;
        if (interval < MinimumInventoryFailureWarningInterval)
            interval = MinimumInventoryFailureWarningInterval;
        var now = _time.GetUtcNow();
        lock (_lastListLock)
        {
            if (_lastInventoryFailureWarning.TryGetValue(providerId, out var last)
                && now - last < interval)
            {
                return;
            }
            _lastInventoryFailureWarning[providerId] = now;
        }
        _log.LogWarning(
            "CompositeManagedSandboxProvider: provider '{ProviderId}' could not verify its full inventory ({Reason}); deferring disposals it might own until its inventory recovers",
            providerId,
            reason);
    }

    private ProviderEntry[]? LastReportedCandidates(string name)
    {
        lock (_lastListLock)
        {
            return _lastReportedByName.TryGetValue(name, out var candidates) ? candidates : null;
        }
    }

    private static string EncodeNestedProviderId(string outerProviderId, string innerProviderId) =>
        $"{NestedProviderIdPrefix}{outerProviderId.Length}:{outerProviderId}{innerProviderId}";

    private static bool TryDecodeNestedProviderId(
        string providerId,
        out string outerProviderId,
        out string innerProviderId)
    {
        outerProviderId = string.Empty;
        innerProviderId = string.Empty;
        if (!providerId.StartsWith(NestedProviderIdPrefix, StringComparison.Ordinal))
            return false;

        var lengthStart = NestedProviderIdPrefix.Length;
        var lengthEnd = providerId.IndexOf(':', lengthStart);
        if (lengthEnd <= lengthStart
            || !int.TryParse(providerId.AsSpan(lengthStart, lengthEnd - lengthStart), out var outerLength)
            || outerLength <= 0)
        {
            return false;
        }

        var outerStart = lengthEnd + 1;
        if (outerStart > providerId.Length - outerLength
            || outerStart + outerLength == providerId.Length)
        {
            return false;
        }

        outerProviderId = providerId.Substring(outerStart, outerLength);
        innerProviderId = providerId[(outerStart + outerLength)..];
        return true;
    }

    private sealed record ProviderEntry(string Id, IManagedSandboxLifecycle Lifecycle);

    private sealed class ReferenceEqualityComparer : IEqualityComparer<IManagedSandboxLifecycle>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();

        public bool Equals(IManagedSandboxLifecycle? x, IManagedSandboxLifecycle? y) => ReferenceEquals(x, y);

        public int GetHashCode(IManagedSandboxLifecycle obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
