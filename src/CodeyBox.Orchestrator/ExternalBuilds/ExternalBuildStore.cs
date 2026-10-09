using CodeyBox.Core.ExternalBuilds;

namespace CodeyBox.Orchestrator.ExternalBuilds;

/// <summary>
/// Durable store for external-build records. Record state transitions go
/// through <see cref="IExternalBuildStore.TryClaimAsync"/> compare-and-set on
/// (state, fence) so concurrent writers cannot corrupt, lose, or
/// cross-contaminate builds. Delivery acknowledgement, park records, duration
/// samples, and retention deletes are idempotent single-effect writes, not
/// state transitions. There is no blind record overwrite: route every state
/// mutation through TryClaimAsync.
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
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);
    Task<IReadOnlyList<ExternalBuildRecord>> ListUnackedTerminalsAsync(CancellationToken ct = default);
    Task MarkDeliveredAsync(string buildId, CancellationToken ct = default);
    Task SaveParkAsync(ExternalBuildParkRecord park, CancellationToken ct = default);
    Task<ExternalBuildParkRecord?> GetParkAsync(string buildId, CancellationToken ct = default);
    Task RemoveParkAsync(string buildId, CancellationToken ct = default);
    Task RecordSampleAsync(ExternalBuildDurationSample sample, CancellationToken ct = default);
    /// <summary>
    /// Bounded history read: only samples at or after <paramref name="since"/>
    /// (when supplied) are returned, newest first, capped at
    /// <paramref name="limit"/> rows (when supplied). Callers must pass both
    /// so a large table is never loaded whole into memory.
    /// </summary>
    Task<IReadOnlyList<ExternalBuildDurationSample>> ListSamplesAsync(DateTimeOffset? since = null, int? limit = null, CancellationToken ct = default);
    /// <summary>
    /// Deletes samples older than <paramref name="cutoff"/>, then trims
    /// oldest-first so at most <paramref name="maxRows"/> remain. Returns
    /// the number of rows deleted. Idempotent.
    /// </summary>
    Task<int> PruneHistoryAsync(DateTimeOffset cutoff, int maxRows, CancellationToken ct = default);
}

/// <summary>Persisted park wait: reason/estimate/sample count survive restart.</summary>
public sealed record ExternalBuildParkRecord(
    string BuildId,
    string WorkItemId,
    string Phase,
    int Iteration,
    int Attempt,
    string Reason,
    long? EstimateTicks,
    int SampleCount,
    DateTimeOffset ParkedAt,
    string CheckpointId);

public sealed class InMemoryExternalBuildStore : IExternalBuildStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ExternalBuildRecord> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExternalBuildParkRecord> _parks = new(StringComparer.Ordinal);
    private readonly List<ExternalBuildDurationSample> _samples = [];

    public Task CreateAsync(ExternalBuildRecord record, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (record.IdempotencyKey is not null && record.IdempotencyBodyHash is not null
                && _records.Values.Any(r =>
                    string.Equals(r.ProjectId, record.ProjectId, StringComparison.Ordinal)
                    && string.Equals(r.IdempotencyKey, record.IdempotencyKey, StringComparison.Ordinal)
                    && string.Equals(r.IdempotencyBodyHash, record.IdempotencyBodyHash, StringComparison.Ordinal)))
                throw new ExternalBuildConflictException(
                    $"Duplicate external build for idempotency key '{record.IdempotencyKey}'.");
            _records[record.Id] = record;
        }
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

    public Task<IReadOnlyList<ExternalBuildRecord>> ListUnackedTerminalsAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<ExternalBuildRecord>>(
            _records.Values
                .Where(r => ExternalBuildLifecycle.IsTerminal(r.State) && !r.DeliveryAcked)
                .ToList());
    }

    public Task MarkDeliveredAsync(string buildId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_records.TryGetValue(buildId, out var cur) && !cur.DeliveryAcked)
                _records[buildId] = cur with { DeliveryAcked = true };
        }
        return Task.CompletedTask;
    }

    public Task SaveParkAsync(ExternalBuildParkRecord park, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(park);
        lock (_gate) _parks[park.BuildId] = park;
        return Task.CompletedTask;
    }

    public Task<ExternalBuildParkRecord?> GetParkAsync(string buildId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_parks.TryGetValue(buildId, out var p) ? p : null);
    }

    public Task RemoveParkAsync(string buildId, CancellationToken ct = default)
    {
        lock (_gate) _parks.Remove(buildId);
        return Task.CompletedTask;
    }

    public Task RecordSampleAsync(ExternalBuildDurationSample sample, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        lock (_gate) _samples.Add(sample);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ExternalBuildDurationSample>> ListSamplesAsync(DateTimeOffset? since = null, int? limit = null, CancellationToken ct = default)
    {
        if (limit is <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (_gate) return Task.FromResult<IReadOnlyList<ExternalBuildDurationSample>>(
            _samples
                .Where(s => since is null || s.CompletedAt >= since.Value)
                .OrderByDescending(s => s.CompletedAt)
                .Take(limit ?? int.MaxValue)
                .ToList());
    }

    public Task<int> PruneHistoryAsync(DateTimeOffset cutoff, int maxRows, CancellationToken ct = default)
    {
        if (maxRows < 0) throw new ArgumentOutOfRangeException(nameof(maxRows));
        lock (_gate)
        {
            var removed = _samples.RemoveAll(s => s.CompletedAt < cutoff);
            var excess = _samples.Count - maxRows;
            if (excess > 0)
            {
                var drop = _samples
                    .OrderBy(s => s.CompletedAt)
                    .Take(excess)
                    .ToList();
                foreach (var s in drop) _samples.Remove(s);
                removed += drop.Count;
            }
            return Task.FromResult(removed);
        }
    }
}
