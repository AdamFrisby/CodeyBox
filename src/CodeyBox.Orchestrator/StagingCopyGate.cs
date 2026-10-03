namespace CodeyBox.Orchestrator;

/// <summary>
/// Serializes staged-copy refreshes that resolve to the same staging leaf.
/// Transports are single-dispatch instances, so same-key concurrent duplicates
/// would otherwise interleave directory delete/copy on one leaf. Striped over
/// a fixed lock set (bounded: no per-dispatch entries to leak); collisions
/// only serialize unrelated copies briefly. Held for the copy window only —
/// never across the phase run — so it cannot deadlock a long execution.
/// </summary>
public sealed class StagingCopyGate
{
    private const int Stripes = 64;

    private readonly SemaphoreSlim[] _stripes =
        Enumerable.Range(0, Stripes).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    /// <summary>
    /// Acquires the stripe for <paramref name="stagingLeaf"/>. Dispose the
    /// lease to release.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(string stagingLeaf, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingLeaf);
        var stripe = _stripes[(uint)StringComparer.Ordinal.GetHashCode(stagingLeaf) % (uint)Stripes];
        await stripe.WaitAsync(ct).ConfigureAwait(false);
        return new Lease(stripe);
    }

    private sealed class Lease(SemaphoreSlim stripe) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            stripe.Release();
        }
    }
}
