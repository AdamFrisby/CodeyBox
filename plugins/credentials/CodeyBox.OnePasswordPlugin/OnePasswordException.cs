using CodeyBox.Core;

namespace CodeyBox.OnePasswordPlugin;

/// <summary>
/// How a 1Password call failed. The backend-unreachable, unauthorised,
/// rate-limited, throttled, and server-error shapes are
/// <see cref="IsInfrastructure"/>: they describe the secret backend, never
/// a verdict on the work item's diff. The lease manager already treats
/// every issue/renew/revoke failure this way (the item runs without the
/// secret; renewal and revocation retry on the sweep); this classification
/// keeps that mapping explicit and testable for any future caller.
/// </summary>
public enum OnePasswordFailureKind
{
    /// <summary>DNS, connection, TLS, or timeout reaching Connect; or the <c>op</c> CLI timing out.</summary>
    Unreachable,
    /// <summary>HTTP 401/403: the Connect or service-account token is rejected; or <c>op</c> reports an auth failure.</summary>
    Unauthorized,
    /// <summary>HTTP 429: the backend rate limit (service accounts are rate-limited).</summary>
    RateLimited,
    /// <summary>HTTP 503 with a retry signal, or 429 with Retry-After: slow down, retry later.</summary>
    Throttled,
    /// <summary>HTTP 5xx: the backend errored; or <c>op</c> exited non-zero without a more specific cause.</summary>
    BackendError,
    /// <summary>
    /// A 2xx response that is not the documented shape, or an empty value
    /// where one was promised (version drift). Treated as infrastructure:
    /// the backend answered unexpectedly.
    /// </summary>
    InvalidResponse,
    /// <summary>HTTP 404: the named vault, item, or field is absent; or <c>op</c> reports the reference is not found.</summary>
    NotFound,
    /// <summary>Operator configuration cannot work (bad URL, missing mapping or credential, unknown lease, missing CLI).</summary>
    Misconfigured,
}

/// <summary>
/// Typed failure for every 1Password backend call. Messages carry only
/// safe fields — HTTP status, host, vault/item/field names, lease ids
/// (identity, never values) — truncated to <see cref="MaxMessageChars"/>,
/// so persisting <c>Message</c> (as the lease store does for revocation
/// failures) can never leak a credential. Use
/// <see cref="IsInfrastructure"/> — never the message text — to decide
/// retry/verdict handling.
/// </summary>
public sealed class OnePasswordException : Exception
{
    /// <summary>Maximum characters kept in a message (server text is truncated).</summary>
    public const int MaxMessageChars = 300;

    public OnePasswordFailureKind Kind { get; }

    /// <summary>HTTP status that produced this failure, when one exists.</summary>
    public int? StatusCode { get; }

    /// <summary>Seconds from a Retry-After response header, when present.</summary>
    public int? RetryAfterSeconds { get; }

    /// <summary>
    /// True for backend-side failures: unreachable, unauthorised,
    /// rate-limited, throttled, server errors, and malformed success
    /// responses. These must never become a verdict on the work item's
    /// diff. False for operator configuration problems and absent
    /// vaults/items/fields.
    /// </summary>
    public bool IsInfrastructure => Kind is OnePasswordFailureKind.Unreachable
        or OnePasswordFailureKind.Unauthorized
        or OnePasswordFailureKind.RateLimited
        or OnePasswordFailureKind.Throttled
        or OnePasswordFailureKind.BackendError
        or OnePasswordFailureKind.InvalidResponse;

    /// <summary>
    /// Work-item failure kind for callers that must record one. Mirrors the
    /// host <c>WorkItemFailureKinds</c> vocabulary without referencing
    /// pipeline code: infrastructure for backend faults, configuration for
    /// operator/absent-secret faults. Either way the lease-issue path never
    /// fails the item — the manager runs it without the secret.
    /// </summary>
    public string FailureKindForWorkItem => IsInfrastructure
        ? WorkItemFailureKinds.Infrastructure
        : WorkItemFailureKinds.Configuration;

    public OnePasswordException(
        OnePasswordFailureKind kind, string message, int? statusCode = null, int? retryAfterSeconds = null)
        : base(Truncate(message))
    {
        Kind = kind;
        StatusCode = statusCode;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public OnePasswordException(
        OnePasswordFailureKind kind, string message, Exception inner, int? statusCode = null)
        : base(Truncate(message), inner)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    private static string Truncate(string message)
    {
        if (string.IsNullOrEmpty(message))
            return "1Password request failed.";
        return message.Length <= MaxMessageChars ? message : message[..MaxMessageChars];
    }

    /// <summary>Builds an exception from an HTTP status with safe context only.</summary>
    public static OnePasswordException FromStatus(
        int statusCode, string operation, string detail, int? retryAfterSeconds = null)
    {
        var kind = statusCode switch
        {
            401 or 403 => OnePasswordFailureKind.Unauthorized,
            404 => OnePasswordFailureKind.NotFound,
            429 => OnePasswordFailureKind.RateLimited,
            503 when retryAfterSeconds.HasValue => OnePasswordFailureKind.Throttled,
            >= 500 => OnePasswordFailureKind.BackendError,
            >= 400 => OnePasswordFailureKind.Misconfigured,
            _ => OnePasswordFailureKind.InvalidResponse,
        };
        return new OnePasswordException(
            kind,
            $"1Password {operation} failed with HTTP {statusCode}: {detail}",
            statusCode,
            retryAfterSeconds);
    }
}
