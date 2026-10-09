using CodeyBox.Core.ExternalBuilds;

namespace CodeyBox.Orchestrator.ExternalBuilds;

/// <summary>
/// Durable store for external-build records. All mutating paths use
/// compare-and-set on (state, fence) so concurrent writers cannot corrupt,
/// lose, or cross-contaminate builds.
/// </summary>
public interface IExternalBuildStore
{
    Task CreateAsync(ExternalBuildRecord record, CancellationToken ct = default);
    Task<ExternalBuildRecord?> GetAsync(string buildId, CancellationToken ct = default);
    Task<ExternalBuildRecord?> GetByIdempotencyAsync(string projectId, string idempotencyKey, string bodyHash, CancellationToken ct = default);
    Task<ExternalBuildRecord?> GetByProviderRunAsync(string providerId, string providerRunId, CancellationToken ct = default);
    Task<IReadOnlyList<ExternalBuildRecord>> ListActiveAsync(string projectId, CancellationToken ct = default);
    Task<int> CountActiveAsync(string projectId, CancellationToken ct = default);
    Task<int> CountActiveForProviderAsync(string providerId, CancellationToken ct = default);
    Task<bool> TryClaimAsync(string buildId, ExternalBuildState expectedState, string? expectedFence, ExternalBuildRecord updated, CancellationToken ct = default);
    Task UpdateAsync(ExternalBuildRecord record, CancellationToken ct = default);
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}

public sealed class InMemoryExternalBuildStore : IExternalBuildStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ExternalBuildRecord> _records = new(StringComparer.Ordinal);

    public Task CreateAsync(ExternalBuildRecord record, CancellationToken ct = default)
    {
        lock (_gate) _records[record.Id] = record;
        return Task.CompletedTask;
    }

    public Task<ExternalBuildRecord?> GetAsync(string buildId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_records.TryGetValue(buildId, out var r) ? r : null);
    }

    public Task<ExternalBuildRecord?> GetByIdempotencyAsync(string projectId, string idempotencyKey, string bodyHash, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_records.Values.FirstOrDefault(r =>
            string.Equals(r.ProjectId, projectId, StringComparison.Ordinal)
            && string.Equals(r.IdempotencyKey, idempotencyKey, StringComparison.Ordinal)
            && string.Equals(r.IdempotencyBodyHash, bodyHash, StringComparison.Ordinal)));
    }

    public Task<ExternalBuildRecord?> GetByProviderRunAsync(string providerId, string providerRunId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_records.Values.FirstOrDefault(r =>
            string.Equals(r.Target.ProviderId, providerId, StringComparison.Ordinal)
            && string.Equals(r.ProviderRunId, providerRunId, StringComparison.Ordinal)));
    }

    public Task<IReadOnlyList<ExternalBuildRecord>> ListActiveAsync(string projectId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<ExternalBuildRecord>>(
            _records.Values.Where(r => string.Equals(r.ProjectId, projectId, StringComparison.Ordinal)
                && !ExternalBuildLifecycle.IsTerminal(r.State)).ToList());
    }

    public Task<int> CountActiveAsync(string projectId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_records.Values.Count(r =>
            string.Equals(r.ProjectId, projectId, StringComparison.Ordinal)
            && !ExternalBuildLifecycle.IsTerminal(r.State)));
    }

    public Task<int> CountActiveForProviderAsync(string providerId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_records.Values.Count(r =>
            string.Equals(r.Target.ProviderId, providerId, StringComparison.Ordinal)
            && !ExternalBuildLifecycle.IsTerminal(r.State)));
    }

    public Task<bool> TryClaimAsync(string buildId, ExternalBuildState expectedState, string? expectedFence, ExternalBuildRecord updated, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(buildId, out var cur)) return Task.FromResult(false);
            if (cur.State != expectedState) return Task.FromResult(false);
            if (!string.Equals(cur.FenceOwner, expectedFence, StringComparison.Ordinal)) return Task.FromResult(false);
            _records[buildId] = updated;
            return Task.FromResult(true);
        }
    }

    public Task UpdateAsync(ExternalBuildRecord record, CancellationToken ct = default)
    {
        lock (_gate) _records[record.Id] = record;
        return Task.CompletedTask;
    }

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var dead = _records.Values
                .Where(r => r.CreatedAt < cutoff && ExternalBuildLifecycle.IsTerminal(r.State))
                .Select(r => r.Id).ToList();
            foreach (var id in dead) _records.Remove(id);
            return Task.FromResult(dead.Count);
        }
    }
}
