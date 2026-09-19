namespace CodeyBox.Core;

/// <summary>
/// Durable store for <see cref="SecretLeaseRecord"/> handles. Stores identity
/// only — never secret values — so an orchestrator restart cannot orphan live
/// leases: the sweep re-reads outstanding handles from this store and resumes
/// renewal/revocation. Implementations must be safe for concurrent sweep and
/// pipeline use.
/// </summary>
public interface ISecretLeaseStore
{
    Task UpsertAsync(SecretLeaseRecord lease, CancellationToken ct = default);

    Task<SecretLeaseRecord?> GetAsync(string leaseId, CancellationToken ct = default);

    /// <summary>Every lease still believed live (Active or RevocationFailed).</summary>
    Task<IReadOnlyList<SecretLeaseRecord>> ListOutstandingAsync(CancellationToken ct = default);

    Task<IReadOnlyList<SecretLeaseRecord>> ListForWorkItemAsync(Guid workItemId, CancellationToken ct = default);

    /// <summary>
    /// Records a renewed expiry. Only applies when the lease is still
    /// <see cref="SecretLeaseStatus.Active"/>; returns false otherwise so a
    /// concurrent revocation cannot be silently resurrected by a late renewal.
    /// </summary>
    Task<bool> TryUpdateExpiryAsync(string leaseId, DateTimeOffset expiresAt, CancellationToken ct = default);

    Task MarkRevokedAsync(string leaseId, CancellationToken ct = default);

    /// <summary>
    /// Records a failed revocation attempt with the issuer's message (never a
    /// secret). The lease stays outstanding so the sweep retries it; the
    /// message makes the failure loud and auditable.
    /// </summary>
    Task MarkRevocationFailedAsync(string leaseId, string error, CancellationToken ct = default);
}
