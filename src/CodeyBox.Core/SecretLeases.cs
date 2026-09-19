namespace CodeyBox.Core;

/// <summary>
/// Lifecycle state of a leased secret handle. The record never carries the
/// secret value itself — only the lease identity the issuer can be told
/// about later (renew / revoke). Values live only in sandbox materialisation
/// or behind a broker endpoint.
/// </summary>
public enum SecretLeaseStatus
{
    Active = 0,
    Revoked = 1,
    RevocationFailed = 2,
}

/// <summary>
/// Persistent handle for one issued secret lease. Carries identity only:
/// there is deliberately no value field, so a store dump, log line, or
/// reconciliation report can never leak the credential. A brokered lease
/// additionally names the non-secret endpoint the workload calls instead
/// of receiving the value at all.
/// </summary>
public sealed record SecretLeaseRecord
{
    /// <summary>Issuer-assigned lease identifier. Logged freely; it is identity, not secret.</summary>
    public required string LeaseId { get; init; }

    /// <summary>Provider that issued the lease (<see cref="ILeaseCapableSecretProvider.ProviderId"/>).</summary>
    public required string ProviderId { get; init; }

    /// <summary>Work item the lease was issued for. Revocation is scoped to this identity.</summary>
    public required Guid WorkItemId { get; init; }

    /// <summary>Secret group the grant authorisation was decided on. Kept for audit.</summary>
    public required string Group { get; init; }

    /// <summary>Sandbox variable (or brokered endpoint variable) this lease materialises as.</summary>
    public required string SandboxEnvVar { get; init; }

    /// <summary>Phase scope the lease was issued for (work, rework, audit-agent, ...).</summary>
    public required string Scope { get; init; }

    /// <summary>When the issuer considers this lease expired. Renewal must happen before this instant.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>True when the value never enters the guest: the sandbox receives only <see cref="Endpoint"/>.</summary>
    public bool Brokered { get; init; }

    /// <summary>
    /// Non-secret endpoint the workload calls for a brokered lease. Never a
    /// secret: authentication at the endpoint is by sandbox identity, not by
    /// a token carried in the environment.
    /// </summary>
    public string? Endpoint { get; init; }

    public SecretLeaseStatus Status { get; init; } = SecretLeaseStatus.Active;

    /// <summary>How many renew/revoke attempts have been made. Bounds retry loops.</summary>
    public int AttemptCount { get; init; }

    /// <summary>Last issuer error (message only, never a secret) for failed renewals/revocations.</summary>
    public string? LastError { get; init; }
}

/// <summary>
/// Material returned by a lease-capable issuer. Either carries a short-lived
/// value (classic lease) or names a broker endpoint (the value never enters
/// the guest). Exactly one of <see cref="Value"/> / brokered endpoint applies.
/// </summary>
public sealed record LeasedSecretMaterial
{
    /// <summary>Issuer-assigned lease handle. Survives the value; used for renew/revoke.</summary>
    public required string LeaseId { get; init; }

    /// <summary>Short-lived value for classic leases; null for brokered leases.</summary>
    public string? Value { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>True when the value never enters the guest.</summary>
    public bool Brokered { get; init; }

    /// <summary>Non-secret endpoint for brokered leases.</summary>
    public string? Endpoint { get; init; }
}

/// <summary>
/// Capability a secret issuer declares when it can do more than return a
/// static value: the issued secret carries a lease handle the orchestrator
/// can renew while the phase runs and must revoke on teardown. Providers
/// offering only static values simply do not implement this interface and
/// keep working unchanged (honest degradation).
/// </summary>
public interface ILeaseCapableSecretProvider
{
    /// <summary>Stable provider identity, recorded on the lease and used for revocation routing.</summary>
    string ProviderId { get; }

    /// <summary>True when this provider can issue <paramref name="secret"/> as a lease.</summary>
    bool CanIssue(ProjectSandboxSecret secret);

    /// <summary>
    /// Issues a short-lived secret (or brokered endpoint) for
    /// <paramref name="secret"/> after the caller has verified the grant.
    /// The provider must not consult grants itself: authorisation stays with
    /// the manager so leasing can never become a route to an ungranted secret.
    /// </summary>
    Task<LeasedSecretMaterial> IssueAsync(
        ProjectSandboxSecret secret,
        Guid workItemId,
        string scope,
        TimeSpan requestedTtl,
        CancellationToken ct = default);

    /// <summary>Extends the lease; returns the new expiry.</summary>
    Task<DateTimeOffset> RenewAsync(string leaseId, CancellationToken ct = default);

    /// <summary>Makes the leased credential stop working. Must be idempotent.</summary>
    Task RevokeAsync(string leaseId, CancellationToken ct = default);
}

/// <summary>
/// Capability a credential plugin declares when an issued
/// <see cref="AgentCredential"/> carries a lease handle the orchestrator
/// must revoke on teardown. Static-only providers do not implement this
/// and are unaffected.
/// </summary>
public interface ILeaseCapableCredentialProvider : ICredentialProvider
{
    /// <summary>Stable provider identity matching <see cref="AgentCredential.LeaseProviderId"/>.</summary>
    string LeaseProviderId { get; }

    /// <summary>Extends the credential lease; returns the new expiry.</summary>
    Task<DateTimeOffset> RenewCredentialAsync(string leaseId, CancellationToken ct = default);

    /// <summary>Revokes the credential lease. Must be idempotent.</summary>
    Task RevokeCredentialAsync(string leaseId, CancellationToken ct = default);
}

/// <summary>
/// Hot-reloadable operational knobs for secret leasing. All values are
/// plain data so the orchestrator reads them through
/// <c>IOptionsMonitor</c> without restarts.
/// </summary>
public sealed class SecretLeasingOptions
{
    /// <summary>Config section name.</summary>
    public const string SectionName = "SecretLeasing";

    /// <summary>
    /// How far before expiry a lease is renewed. Default 5 minutes: a
    /// 20-minute lease in a 240-minute phase is renewed ~12 times.
    /// </summary>
    public TimeSpan RenewBeforeExpiry { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How often the background sweep checks for due renewals and terminal revocations.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Per-issue TTL requested from the issuer. Default 20 minutes.</summary>
    public TimeSpan DefaultLeaseTtl { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>Per revoke-attempt timeout.</summary>
    public TimeSpan RevocationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum recorded revocation attempts before a lease parks as failed loudly.</summary>
    public int MaxRevocationAttempts { get; set; } = 5;

    /// <summary>
    /// Absolute cap on a lease's lifetime measured from issuance. Bounds
    /// renewal even when the work item's own deadline is unknown to the
    /// sweep (default 480 minutes = the maximum work-phase budget).
    /// </summary>
    public TimeSpan MaxLeaseLifetime { get; set; } = TimeSpan.FromMinutes(480);

    /// <summary>Maximum characters accepted in an issuer-assigned lease id.</summary>
    public const int MaxLeaseIdLength = 256;
}

/// <summary>
/// Pure renewal/validation policy for secret leases. Kept pure so the
/// sweep, the manager, and tests share one decision core.
/// </summary>
public static class SecretLeasePolicy
{
    /// <summary>
    /// True when <paramref name="now"/> is within <paramref name="renewBefore"/>
    /// of <paramref name="expiresAt"/> (or past it): the lease needs renewal.
    /// An already-expired lease still returns true so the sweep retries
    /// renewal while the work item is live rather than abandoning it.
    /// </summary>
    public static bool ShouldRenew(DateTimeOffset now, DateTimeOffset expiresAt, TimeSpan renewBefore)
        => now + renewBefore >= expiresAt;

    /// <summary>
    /// Caps a renewed expiry at the work item's own deadline: renewal must
    /// never extend a lease indefinitely past the work item's lifetime.
    /// </summary>
    public static DateTimeOffset CapAtItemDeadline(DateTimeOffset renewedExpiry, DateTimeOffset itemDeadline)
        => renewedExpiry < itemDeadline ? renewedExpiry : itemDeadline;

    /// <summary>Validates an issuer-assigned lease id: non-empty, bounded, no control characters.</summary>
    public static string ValidateLeaseId(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Lease id must be non-empty.", parameterName);
        if (value!.Length > SecretLeasingOptions.MaxLeaseIdLength)
            throw new ArgumentException(
                $"Lease id exceeds {SecretLeasingOptions.MaxLeaseIdLength} characters.", parameterName);
        if (value.Any(char.IsControl))
            throw new ArgumentException("Lease id must not contain control characters.", parameterName);
        return value;
    }
}
