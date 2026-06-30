using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Read/dispose-only provider used by lifecycle services that need to sweep
/// sandboxes owned by more than one execution fleet.
/// </summary>
public sealed class CompositeManagedSandboxProvider : ISandboxProvider
{
    private readonly IReadOnlyList<ISandboxProvider> _providers;

    public CompositeManagedSandboxProvider(IEnumerable<ISandboxProvider> providers)
    {
        _providers = providers
            .Where(static p => p is not null)
            .Distinct(ReferenceEqualityComparer.Instance)
            .ToArray();
    }

    public string Name => "composite-managed";

    public IReadOnlyList<ISandboxProvider> Providers => _providers;

    public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default) =>
        throw new NotSupportedException("CompositeManagedSandboxProvider is lifecycle-only and cannot create sandboxes.");

    public async Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
    {
        var result = new List<ManagedSandboxInfo>();
        foreach (var provider in _providers)
        {
            ct.ThrowIfCancellationRequested();
            result.AddRange(await provider.ListAllManagedAsync(ct).ConfigureAwait(false));
        }
        return result;
    }

    public async Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        foreach (var provider in _providers)
        {
            ct.ThrowIfCancellationRequested();
            await provider.DisposeLeakedAsync(name, ct).ConfigureAwait(false);
        }
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<ISandboxProvider>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();

        public bool Equals(ISandboxProvider? x, ISandboxProvider? y) => ReferenceEquals(x, y);

        public int GetHashCode(ISandboxProvider obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
