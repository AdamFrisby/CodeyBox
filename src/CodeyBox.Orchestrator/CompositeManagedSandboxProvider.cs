using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Read/dispose-only provider used by lifecycle services that need to sweep
/// sandboxes owned by more than one execution fleet.
/// </summary>
public sealed class CompositeManagedSandboxProvider : IManagedSandboxLifecycle
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

    public async Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
    {
        var result = new List<ManagedSandboxInfo>();
        var failures = new List<Exception>();
        foreach (var provider in _providers)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                result.AddRange(await provider.ListAllManagedAsync(ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(ex);
            }
        }

        if (result.Count == 0 && failures.Count == _providers.Count && failures.Count > 0)
            throw new AggregateException("Every managed sandbox provider failed to list sandboxes.", failures);

        return result;
    }

    public async Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        var failures = new List<Exception>();
        foreach (var provider in _providers)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await provider.DisposeLeakedAsync(name, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(ex);
            }
        }

        if (failures.Count == _providers.Count && failures.Count > 0)
            throw new AggregateException($"Every managed sandbox provider failed to dispose leaked sandbox '{name}'.", failures);
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<ISandboxProvider>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();

        public bool Equals(ISandboxProvider? x, ISandboxProvider? y) => ReferenceEquals(x, y);

        public int GetHashCode(ISandboxProvider obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
