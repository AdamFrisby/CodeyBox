using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>Shared fakes for sandbox placement tests (acquirer + pipeline wiring).</summary>
internal sealed class PlacementFakeSandboxProvider : ISandboxProvider
{
    private readonly List<SandboxSpec> _specs = new();

    public PlacementFakeSandboxProvider(string name, IReadOnlyList<string>? declaredCapabilities = null)
    {
        Name = name;
        DeclaredCapabilities = declaredCapabilities ?? [];
    }

    public string Name { get; }

    public IReadOnlyList<string> DeclaredCapabilities { get; }

    public IReadOnlyList<SandboxSpec> Specs
    {
        get { lock (_specs) return _specs.ToList(); }
    }

    public int CreateCount
    {
        get { lock (_specs) return _specs.Count; }
    }

    public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        lock (_specs) _specs.Add(spec);
        return Task.FromResult<ISandbox>(new PlacementFakeSandbox(Name + "-sandbox"));
    }

    public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

    public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class PlacementFakeSandbox : ISandbox
{
    public PlacementFakeSandbox(string id) => Id = id;

    public string Id { get; }

    public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        => Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class PlacementFakeSandboxProviderRegistry : ISandboxProviderRegistry
{
    private readonly Dictionary<string, ISandboxProvider> _byKind;

    public PlacementFakeSandboxProviderRegistry(IEnumerable<ISandboxProvider> providers, Func<ISandboxProvider, string>? kindOf = null)
    {
        _byKind = new Dictionary<string, ISandboxProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider);
            _byKind[(kindOf?.Invoke(provider) ?? provider.Name).Trim().ToLowerInvariant()] = provider;
        }
    }

    public ISandboxProvider Resolve(SandboxMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (string.IsNullOrWhiteSpace(member.ProviderKind))
            throw new InvalidOperationException($"Sandbox member '{member.MemberId}' names no sandbox provider kind.");
        return EnsureKind(member.ProviderKind.Trim());
    }

    public ISandboxProvider EnsureKind(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
            throw new InvalidOperationException("Cannot ensure a blank sandbox provider kind.");
        if (_byKind.TryGetValue(kind.Trim().ToLowerInvariant(), out var provider))
            return provider;
        throw new InvalidOperationException($"Unregistered sandbox provider kind '{kind}'.");
    }

    public IReadOnlyList<SandboxProviderRegistration> ListRegistered() =>
        _byKind
            .OrderBy(static kvp => kvp.Key, StringComparer.Ordinal)
            .Select(static kvp => new SandboxProviderRegistration(kvp.Key, kvp.Value))
            .ToList();
}

internal static class SandboxPlacementTestMembers
{
    public static SandboxMember Member(
        string memberId,
        string providerKind,
        IReadOnlyList<string>? capabilities = null,
        IReadOnlyList<string>? networkProfiles = null,
        IReadOnlyList<string>? credentials = null,
        int preferenceScore = 100,
        int capacity = 8) =>
        new()
        {
            MemberId = memberId,
            ProviderKind = providerKind,
            Capacity = capacity,
            Capabilities = capabilities ?? [],
            NetworkProfiles = networkProfiles ?? [],
            Credentials = credentials ?? [],
            PreferenceScore = preferenceScore,
        };

    public static SandboxClassesSnapshot Snapshot(params SandboxMember[] members) =>
        new(
        [
            new SandboxClass
            {
                Id = "default",
                DisplayName = "Test",
                Members = members.ToList(),
            },
        ]);

    public static SandboxSpec Spec() => new() { ImageReference = "test-image" };
}
