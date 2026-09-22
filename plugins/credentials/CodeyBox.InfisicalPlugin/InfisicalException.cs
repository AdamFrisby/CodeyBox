using CodeyBox.Core;
using CodeyBox.PluginSdk.Credentials;

namespace CodeyBox.InfisicalPlugin;

/// <summary>
/// How an Infisical call failed. The backend-unreachable, unauthorised,
/// rate-limited, throttled, and server-error shapes are
/// <see cref="IsInfrastructure"/>: they describe the secret backend, never
/// a verdict on the work item's diff. The lease manager already treats
/// every issue/renew/revoke failure this way (the item runs without the
/// secret; renewal and revocation retry on the sweep); this classification
/// keeps that mapping explicit and testable for any future caller.
/// </summary>
public enum InfisicalFailureKind
{
    /// <summary>DNS, connection, TLS, or timeout reaching the Infisical site.</summary>
    Unreachable,
    /// <summary>HTTP 401/403: the machine identity or token is rejected.</summary>
    Unauthorized,
    /// <summary>HTTP 429: Infisical Cloud rate limits (read/secret limits).</summary>
    RateLimited,
    /// <summary>HTTP 503 with a retry signal: slow down, retry later.</summary>
    Throttled,
    /// <summary>HTTP 5xx: the backend errored.</summary>
    BackendError,
    /// <summary>
    /// A 2xx response that is not the documented shape (version drift).
    /// Treated as infrastructure: the backend answered unexpectedly.
    /// </summary>
    InvalidResponse,
    /// <summary>HTTP 404: the named secret, lease, or project is absent.</summary>
    NotFound,
    /// <summary>Operator configuration cannot work (bad URL, missing mapping, unknown lease).</summary>
    Misconfigured,
}

/// <summary>
/// Typed failure for every Infisical backend call. Messages carry only
/// safe fields — HTTP status, host, secret key names, lease ids (identity,
/// never values) — truncated to <see cref="MaxMessageChars"/>, so persisting
/// <c>Message</c> (as the lease store does for revocation failures) can
/// never leak a credential. Use <see cref="IsInfrastructure"/> — never the
/// message text — to decide retry/verdict handling.
/// </summary>
public sealed class InfisicalException : Exception
{
    /// <summary>Maximum characters kept in a message (server text is truncated).</summary>
    public const int MaxMessageChars = 300;

    public InfisicalFailureKind Kind { get; }

    /// <summary>HTTP status that produced this failure, when one exists.</summary>
    public int? StatusCode { get; }

    /// <summary>Seconds from a Retry-After response header, when present.</summary>
    public int? RetryAfterSeconds { get; }

    /// <summary>
    /// True for backend-side failures: unreachable, unauthorised,
    /// rate-limited, throttled, server errors, and malformed success
    /// responses. These must never become a verdict on the work item's
    /// diff. False for operator configuration problems and absent
    /// secrets/leases.
    /// </summary>
    public bool IsInfrastructure => Kind is InfisicalFailureKind.Unreachable
        or InfisicalFailureKind.Unauthorized
        or InfisicalFailureKind.RateLimited
        or InfisicalFailureKind.Throttled
        or InfisicalFailureKind.BackendError
        or InfisicalFailureKind.InvalidResponse;

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

    public InfisicalException(
        InfisicalFailureKind kind, string message, int? statusCode = null, int? retryAfterSeconds = null)
        : base(Truncate(message))
    {
        Kind = kind;
        StatusCode = statusCode;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public InfisicalException(
        InfisicalFailureKind kind, string message, Exception inner, int? statusCode = null)
        : base(Truncate(message), inner)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    private static string Truncate(string message)
        => CredentialMessages.Truncate(message, "Infisical request failed.", MaxMessageChars);

    /// <summary>Builds an exception from an HTTP status with safe context only.</summary>
    public static InfisicalException FromStatus(
        int statusCode, string operation, string detail, int? retryAfterSeconds = null)
    {
        var kind = statusCode switch
        {
            401 or 403 => InfisicalFailureKind.Unauthorized,
            404 => InfisicalFailureKind.NotFound,
            429 => InfisicalFailureKind.RateLimited,
            503 when retryAfterSeconds.HasValue => InfisicalFailureKind.Throttled,
            >= 500 => InfisicalFailureKind.BackendError,
            >= 400 => InfisicalFailureKind.Misconfigured,
            _ => InfisicalFailureKind.InvalidResponse,
        };
        return new InfisicalException(
            kind,
            $"Infisical {operation} failed with HTTP {statusCode}: {detail}",
            statusCode,
            retryAfterSeconds);
    }
}
