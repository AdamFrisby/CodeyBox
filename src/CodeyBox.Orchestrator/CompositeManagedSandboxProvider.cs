using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Read/dispose-only provider used by lifecycle services that need to sweep
/// sandboxes owned by more than one execution fleet.
/// </summary>
public sealed class CompositeManagedSandboxProvider : IManagedSandboxLifecycle
{
    private const string NestedProviderIdPrefix = "nested:";
    private readonly IReadOnlyList<ProviderEntry> _providers;
    private readonly IReadOnlyDictionary<string, ProviderEntry> _providersById;
    private readonly object _lastListLock = new();
    private Dictionary<string, ProviderEntry[]> _lastReportedByName = new(StringComparer.Ordinal);

    public CompositeManagedSandboxProvider(IEnumerable<IManagedSandboxLifecycle> providers)
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
    }

    public string Name => "composite-managed";

    public IReadOnlyList<IManagedSandboxLifecycle> Providers => _providers.Select(static p => p.Lifecycle).ToArray();

    public async Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
    {
        var result = new List<ManagedSandboxInfo>();
        var failures = new List<Exception>();
        var reportedByName = new Dictionary<string, List<ProviderEntry>>(StringComparer.Ordinal);
        foreach (var provider in _providers)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var listed = await provider.Lifecycle.ListAllManagedAsync(ct).ConfigureAwait(false);
                foreach (var info in listed)
                {
                    // A lifecycle can itself be a composite (the production
                    // admission wrapper around the Multipass/Incus cutover
                    // router is one). Preserve that inner route in the opaque
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
            }
        }

        if (result.Count == 0 && failures.Count == _providers.Count && failures.Count > 0)
            throw new AggregateException("Every managed sandbox provider failed to list sandboxes.", failures);

        lock (_lastListLock)
        {
            _lastReportedByName = reportedByName.ToDictionary(
                static kvp => kvp.Key,
                static kvp => kvp.Value.ToArray(),
                StringComparer.Ordinal);
        }

        return result;
    }

    public async Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        ProviderEntry[] candidates;
        lock (_lastListLock)
        {
            _lastReportedByName.TryGetValue(name, out candidates!);
        }

        if (candidates is not { Length: > 0 })
        {
            if (_providers.Count == 1)
            {
                await _providers[0].Lifecycle.DisposeLeakedAsync(name, ct).ConfigureAwait(false);
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
        string? innerProviderId = null;
        if (TryDecodeNestedProviderId(
                sandbox.LifecycleProviderId,
                out var decodedOuterProviderId,
                out var decodedInnerProviderId))
        {
            outerProviderId = decodedOuterProviderId;
            innerProviderId = decodedInnerProviderId;
        }

        if (!_providersById.TryGetValue(outerProviderId, out var provider))
            throw new InvalidOperationException($"Unknown managed sandbox provider '{sandbox.LifecycleProviderId}' for leaked sandbox '{sandbox.Name}'.");

        // Strip this composite's scope and pass the inner snapshot through.
        // Calling the name-only overload here would make a nested composite
        // rediscover ownership and become ambiguous when two backends use the
        // same configured instance name.
        await provider.Lifecycle.DisposeLeakedAsync(
            sandbox with { LifecycleProviderId = innerProviderId },
            ct).ConfigureAwait(false);
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
