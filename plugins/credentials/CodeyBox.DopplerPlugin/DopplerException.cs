using CodeyBox.Core;
using CodeyBox.PluginSdk.Credentials;

namespace CodeyBox.DopplerPlugin;

/// <summary>
/// How a Doppler call failed. The backend-unreachable, unauthorised,
/// rate-limited, throttled, and server-error shapes are
/// <see cref="IsInfrastructure"/>: they describe the secret backend, never
/// a verdict on the work item's diff. The lease manager already treats
/// every issue/renew/revoke failure this way (the item runs without the
/// secret; renewal and revocation retry on the sweep); this classification
/// keeps that mapping explicit and testable for any future caller.
/// </summary>
public enum DopplerFailureKind
{
    /// <summary>DNS, connection, TLS, or timeout reaching the Doppler API.</summary>
    Unreachable,
    /// <summary>HTTP 401/403: the service token or identity token is rejected.</summary>
    Unauthorized,
    /// <summary>HTTP 429: Doppler rate limits (reads/min, secret reads/min).</summary>
    RateLimited,
    /// <summary>HTTP 503 with a retry signal, or 429 with Retry-After: slow down, retry later.</summary>
    Throttled,
    /// <summary>HTTP 5xx: the backend errored.</summary>
    BackendError,
    /// <summary>
    /// A 2xx response that is not the documented shape (version drift).
    /// Treated as infrastructure: the backend answered unexpectedly.
    /// </summary>
    InvalidResponse,
    /// <summary>HTTP 404: the named project, config, or secret is absent.</summary>
    NotFound,
    /// <summary>Operator configuration cannot work (bad URL, missing mapping, unknown lease).</summary>
    Misconfigured,
}

/// <summary>
/// Typed failure for every Doppler backend call. Messages carry only
/// safe fields — HTTP status, host, project/config/secret names, lease ids
/// (identity, never values) — truncated to <see cref="MaxMessageChars"/>,
/// so persisting <c>Message</c> (as the lease store does for revocation
/// failures) can never leak a credential. Use
/// <see cref="IsInfrastructure"/> — never the message text — to decide
/// retry/verdict handling.
/// </summary>
public sealed class DopplerException : Exception
{
    /// <summary>Maximum characters kept in a message (server text is truncated).</summary>
    public const int MaxMessageChars = 300;

    public DopplerFailureKind Kind { get; }

    /// <summary>HTTP status that produced this failure, when one exists.</summary>
    public int? StatusCode { get; }

    /// <summary>Seconds from a Retry-After response header, when present.</summary>
    public int? RetryAfterSeconds { get; }

    /// <summary>
    /// True for backend-side failures: unreachable, unauthorised,
    /// rate-limited, throttled, server errors, and malformed success
    /// responses. These must never become a verdict on the work item's
    /// diff. False for operator configuration problems and absent
    /// projects/configs/secrets.
    /// </summary>
    public bool IsInfrastructure => Kind is DopplerFailureKind.Unreachable
        or DopplerFailureKind.Unauthorized
        or DopplerFailureKind.RateLimited
        or DopplerFailureKind.Throttled
        or DopplerFailureKind.BackendError
        or DopplerFailureKind.InvalidResponse;

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

    public DopplerException(
        DopplerFailureKind kind, string message, int? statusCode = null, int? retryAfterSeconds = null)
        : base(Truncate(message))
    {
        Kind = kind;
        StatusCode = statusCode;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public DopplerException(
        DopplerFailureKind kind, string message, Exception inner, int? statusCode = null)
        : base(Truncate(message), inner)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    private static string Truncate(string message)
        => CredentialMessages.Truncate(message, "Doppler request failed.", MaxMessageChars);

    /// <summary>Builds an exception from an HTTP status with safe context only.</summary>
    public static DopplerException FromStatus(
        int statusCode, string operation, string detail, int? retryAfterSeconds = null)
    {
        var kind = statusCode switch
        {
            401 or 403 => DopplerFailureKind.Unauthorized,
            404 => DopplerFailureKind.NotFound,
            429 => DopplerFailureKind.RateLimited,
            503 when retryAfterSeconds.HasValue => DopplerFailureKind.Throttled,
            >= 500 => DopplerFailureKind.BackendError,
            >= 400 => DopplerFailureKind.Misconfigured,
            _ => DopplerFailureKind.InvalidResponse,
        };
        return new DopplerException(
            kind,
            $"Doppler {operation} failed with HTTP {statusCode}: {detail}",
            statusCode,
            retryAfterSeconds);
    }
}
