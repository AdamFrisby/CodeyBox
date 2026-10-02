using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Core;

/// <summary>
/// A sandbox provider wrapper whose admission gate derives from the member
/// catalog: each constructed kind's gate target becomes the summed capacity
/// of the members naming it (see
/// <see cref="ISandboxProviderRegistry.SyncKindCapacities"/>).
/// </summary>
public interface IKindCapacityReloadable
{
    /// <summary>Current admission gate target.</summary>
    int MaxConcurrentSandboxes { get; }

    /// <summary>
    /// Applies a catalog-derived kind capacity. Implementations no-op when
    /// the target is unchanged.
    /// </summary>
    void ApplyKindCapacityReload(int newKindCapacity, string kind);
}

/// <summary>
/// Composition-root <see cref="ISandboxProviderRegistry"/>: builds each
/// provider kind on first use (exactly once per kind, via a locked
/// <see cref="Lazy{T}"/>) and shares the instance across every member that
/// names the kind. Lookup is keyed by normalised kind (trimmed, lowercase,
/// ordinal ignore-case), so registration order never affects resolution.
/// <see cref="KnownKinds"/> covers the built-in kinds (see
/// <see cref="HostPlatformSupport.AllProviderIds"/>) plus host-registered
/// plugin-contributed kinds. Unknown or blank kinds fail closed — a member
/// is never silently re-pointed at another provider.
/// Shared by the orchestrator and the executor host so both resolve through
/// one implementation.
/// </summary>
public sealed class SandboxProviderRegistry : ISandboxProviderRegistry
{
    private readonly Func<string, ISandboxProvider> _buildKind;
    private readonly ILogger<SandboxProviderRegistry> _log;
    private readonly ConcurrentDictionary<string, Lazy<ISandboxProvider>> _byKind =
        new(StringComparer.OrdinalIgnoreCase);

    public SandboxProviderRegistry(
        Func<string, ISandboxProvider> buildKind,
        ILogger<SandboxProviderRegistry>? log = null,
        IReadOnlySet<string>? pluginKinds = null)
    {
        ArgumentNullException.ThrowIfNull(buildKind);
        _buildKind = buildKind;
        _log = log ?? NullLogger<SandboxProviderRegistry>.Instance;
        var known = new HashSet<string>(HostPlatformSupport.AllProviderIds, StringComparer.OrdinalIgnoreCase);
        if (pluginKinds is not null)
            known.UnionWith(pluginKinds);
        KnownKinds = known;
    }

    /// <inheritdoc/>
    public IReadOnlySet<string> KnownKinds { get; }

    /// <inheritdoc/>
    public ISandboxProvider Resolve(SandboxMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (string.IsNullOrWhiteSpace(member.ProviderKind))
            throw new InvalidOperationException(
                $"Sandbox member '{member.MemberId}' names no sandbox provider kind; refusing to resolve.");
        return EnsureKind(member.ProviderKind.Trim());
    }

    /// <inheritdoc/>
    public ISandboxProvider EnsureKind(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
            throw new InvalidOperationException("Cannot ensure a blank sandbox provider kind.");
        var normalized = kind.Trim().ToLowerInvariant();
        var lazy = _byKind.GetOrAdd(
            normalized,
            static (key, build) => new Lazy<ISandboxProvider>(
                () => build(key),
                LazyThreadSafetyMode.ExecutionAndPublication),
            _buildKind);
        try
        {
            return lazy.Value;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to build sandbox provider kind '{Kind}'.", normalized);
            throw;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<SandboxProviderRegistration> ListRegistered() =>
        _byKind
            .Where(kvp => kvp.Value.IsValueCreated)
            .OrderBy(static kvp => kvp.Key, StringComparer.Ordinal)
            .Select(static kvp => new SandboxProviderRegistration(kvp.Key, kvp.Value.Value))
            .ToList();

    /// <inheritdoc/>
    public void SyncKindCapacities(IReadOnlyList<SandboxClass> catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var sums = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sandboxClass in catalog)
        {
            foreach (var member in sandboxClass.Members)
            {
                if (!seen.Add(member.MemberId))
                    continue;
                var kind = member.ProviderKind.Trim().ToLowerInvariant();
                var sum = sums.TryGetValue(kind, out var current) ? current : 0;
                sums[kind] = Math.Min((long)int.MaxValue, sum + member.Capacity);
            }
        }

        foreach (var registration in ListRegistered())
        {
            if (!sums.TryGetValue(registration.Kind, out var sum) || sum < 1)
                continue;
            var target = sum >= int.MaxValue ? int.MaxValue : (int)sum;
            if (registration.Provider is IKindCapacityReloadable wrapper
                && wrapper.MaxConcurrentSandboxes != target)
                wrapper.ApplyKindCapacityReload(target, registration.Kind);
        }
    }
}
