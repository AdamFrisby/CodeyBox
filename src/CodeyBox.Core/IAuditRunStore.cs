namespace CodeyBox.Core;

/// <summary>
/// Durable store for standalone audit runs. Runs are owned by their project;
/// every read path re-verifies project ownership at the moment of action.
/// </summary>
public interface IAuditRunStore
{
    Task CreateAsync(AuditRunRecord run, CancellationToken ct = default);
    Task<AuditRunRecord?> GetAsync(string runId, CancellationToken ct = default);
    Task<AuditRunRecord?> GetByIdempotencyAsync(string projectId, string idempotencyKey, string bodyHash, CancellationToken ct = default);
    Task<AuditRunRecord?> GetByIdempotencyKeyAsync(string projectId, string idempotencyKey, CancellationToken ct = default);
    Task<IReadOnlyList<AuditRunRecord>> ListAsync(string? projectId, int limit, CancellationToken ct = default);
    Task<bool> TryUpdateStateAsync(string runId, AuditRunState expected, AuditRunRecord updated, CancellationToken ct = default);
    Task UpdateAsync(AuditRunRecord run, CancellationToken ct = default);
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);
    Task<int> CountActiveAsync(CancellationToken ct = default);
}

/// <summary>
/// Full machine-readable artifact content, stored separately from truncated
/// RawOutput. Bounded, digested, and scoped to the owning project.
/// </summary>
public interface IAuditRunArtifactStore
{
    Task PutAsync(string runId, string name, string mediaType, byte[] content, CancellationToken ct = default);
    Task<(byte[] Content, string MediaType)?> GetAsync(string runId, string name, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListAsync(string runId, CancellationToken ct = default);
    Task DeleteRunAsync(string runId, CancellationToken ct = default);
}

/// <summary>
/// Resolves a requested ref to an immutable SHA under the project's
/// repository/credential policy. The built-in implementation accepts only
/// explicit 40-hex SHAs; richer resolvers plug in here.
/// </summary>
public interface IStandaloneAuditRefResolver
{
    Task<RefResolveResult> ResolveAsync(string projectId, string requestedRef, CancellationToken ct = default);
}

public sealed record RefResolveResult(bool Ok, string? Sha = null, string? Error = null)
{
    public static RefResolveResult Resolved(string sha) => new(true, sha);
    public static RefResolveResult Failed(string error) => new(false, null, error);
}

/// <summary>In-memory store for tests and single-node dev use.</summary>
public sealed class InMemoryAuditRunStore : IAuditRunStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AuditRunRecord> _runs = new(StringComparer.Ordinal);

    public Task CreateAsync(AuditRunRecord run, CancellationToken ct = default)
    {
        lock (_gate) _runs[run.Id] = run;
        return Task.CompletedTask;
    }

    public Task<AuditRunRecord?> GetAsync(string runId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_runs.TryGetValue(runId, out var r) ? r : null);
    }

    public Task<AuditRunRecord?> GetByIdempotencyAsync(string projectId, string idempotencyKey, string bodyHash, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_runs.Values.FirstOrDefault(r =>
            string.Equals(r.ProjectId, projectId, StringComparison.Ordinal)
            && string.Equals(r.IdempotencyKey, idempotencyKey, StringComparison.Ordinal)
            && string.Equals(r.IdempotencyBodyHash, bodyHash, StringComparison.Ordinal)));
    }

    public Task<AuditRunRecord?> GetByIdempotencyKeyAsync(string projectId, string idempotencyKey, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_runs.Values.FirstOrDefault(r =>
            string.Equals(r.ProjectId, projectId, StringComparison.Ordinal)
            && string.Equals(r.IdempotencyKey, idempotencyKey, StringComparison.Ordinal)));
    }

    public Task<IReadOnlyList<AuditRunRecord>> ListAsync(string? projectId, int limit, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var q = _runs.Values.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(projectId))
                q = q.Where(r => string.Equals(r.ProjectId, projectId, StringComparison.Ordinal));
            return Task.FromResult<IReadOnlyList<AuditRunRecord>>(
                q.OrderByDescending(r => r.CreatedAt).Take(Math.Max(1, limit)).ToList());
        }
    }

    public Task<bool> TryUpdateStateAsync(string runId, AuditRunState expected, AuditRunRecord updated, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var cur) || cur.State != expected)
                return Task.FromResult(false);
            _runs[runId] = updated;
            return Task.FromResult(true);
        }
    }

    public Task UpdateAsync(AuditRunRecord run, CancellationToken ct = default)
    {
        lock (_gate) _runs[run.Id] = run;
        return Task.CompletedTask;
    }

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var dead = _runs.Values.Where(r => r.CreatedAt < cutoff && AuditRunLifecycle.IsTerminal(r.State)).Select(r => r.Id).ToList();
            foreach (var id in dead) _runs.Remove(id);
            return Task.FromResult(dead.Count);
        }
    }

    public Task<int> CountActiveAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_runs.Values.Count(r => !AuditRunLifecycle.IsTerminal(r.State)));
    }
}

/// <summary>In-memory artifact store with bounds enforced at the sink.</summary>
public sealed class InMemoryAuditRunArtifactStore : IAuditRunArtifactStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, (byte[] Content, string MediaType)>> _blobs = new(StringComparer.Ordinal);
    private readonly long _maxBytesPerArtifact;

    public InMemoryAuditRunArtifactStore(long maxBytesPerArtifact = 5L * 1024 * 1024)
    {
        _maxBytesPerArtifact = maxBytesPerArtifact;
    }

    public Task PutAsync(string runId, string name, string mediaType, byte[] content, CancellationToken ct = default)
    {
        if (content.LongLength > _maxBytesPerArtifact)
            throw new AuditRunArtifactTooLargeException(name, content.LongLength, _maxBytesPerArtifact);
        lock (_gate)
        {
            if (!_blobs.TryGetValue(runId, out var m)) _blobs[runId] = m = new Dictionary<string, (byte[], string)>(StringComparer.Ordinal);
            m[name] = (content, mediaType);
        }
        return Task.CompletedTask;
    }

    public Task<(byte[] Content, string MediaType)?> GetAsync(string runId, string name, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_blobs.TryGetValue(runId, out var m) && m.TryGetValue(name, out var v)
            ? (ValueTuple<byte[], string>?)v : null);
    }

    public Task<IReadOnlyList<string>> ListAsync(string runId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<string>>(
            _blobs.TryGetValue(runId, out var m) ? m.Keys.ToList() : []);
    }

    public Task DeleteRunAsync(string runId, CancellationToken ct = default)
    {
        lock (_gate) _blobs.Remove(runId);
        return Task.CompletedTask;
    }
}

public sealed class AuditRunArtifactTooLargeException(string name, long size, long max)
    : InvalidOperationException($"Artifact '{name}' is {size} bytes, exceeding the {max}-byte cap.")
{
    public string ArtifactName { get; } = name;
    public long SizeBytes { get; } = size;
    public long MaxBytes { get; } = max;
}
