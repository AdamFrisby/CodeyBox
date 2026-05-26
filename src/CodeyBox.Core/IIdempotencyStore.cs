namespace CodeyBox.Core;

/// <summary>
/// Cached response captured the first time a request was processed with a
/// given <c>Idempotency-Key</c>. <see cref="BodyHash"/> is a SHA-256 hex digest
/// of the original request body; replays with a matching key but different
/// body must be rejected with 409.
/// </summary>
public sealed record IdempotencyEntry(
    string Key,
    string BodyHash,
    int ResponseStatus,
    byte[] ResponseBody,
    string ResponseContentType,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Outcome of looking up an <c>Idempotency-Key</c>.
/// </summary>
public enum IdempotencyLookupOutcome
{
    /// <summary>No row exists or the row has expired. Caller should process the request normally.</summary>
    Miss,
    /// <summary>A row exists and the body hash matches; the cached response should be replayed verbatim.</summary>
    Hit,
    /// <summary>A row exists but the body hash differs; the caller must reject with 409.</summary>
    Conflict,
}

public readonly record struct IdempotencyLookupResult(IdempotencyLookupOutcome Outcome, IdempotencyEntry? Entry);

/// <summary>
/// Durable cache of (Idempotency-Key → response) records used by the API's
/// <c>IdempotencyMiddleware</c>. Entries are append-only within their 24-hour
/// TTL; mutation is intentionally not exposed — every successful write of a new
/// key creates a fresh row.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Looks up <paramref name="key"/>. Returns <see cref="IdempotencyLookupOutcome.Hit"/>
    /// only when the row is unexpired AND its stored body hash matches
    /// <paramref name="bodyHash"/>; mismatched hash maps to <see cref="IdempotencyLookupOutcome.Conflict"/>.
    /// Expired rows surface as <see cref="IdempotencyLookupOutcome.Miss"/> so a
    /// fresh request reuses the key without colliding.
    /// </summary>
    Task<IdempotencyLookupResult> LookupAsync(
        string key,
        string bodyHash,
        DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>
    /// Persists a new cached response. Idempotent: re-inserting the same key
    /// with the same body hash within the TTL is a no-op.
    /// </summary>
    Task PutAsync(IdempotencyEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Best-effort sweep of expired rows. Called from background maintenance;
    /// not on the request path.
    /// </summary>
    Task<int> DeleteExpiredAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
