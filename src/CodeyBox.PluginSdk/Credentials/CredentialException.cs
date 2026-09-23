using CodeyBox.Core;

namespace CodeyBox.PluginSdk.Credentials;

/// <summary>
/// How a call to a secret backend failed, shared by every credential
/// provider plugin. The backend-unreachable, unauthorised, rate-limited,
/// throttled, and server-error shapes are
/// <see cref="CredentialException.IsInfrastructure"/>: they describe the
/// secret backend, never a verdict on the work item's diff. The lease
/// manager already treats every issue/renew/revoke failure this way (the
/// item runs without the secret; renewal and revocation retry on the
/// sweep); this classification keeps that mapping explicit and testable
/// for any future caller. One enum so the taxonomy — and what counts as
/// infrastructure — cannot fork per backend.
/// </summary>
public enum CredentialFailureKind
{
    /// <summary>DNS, connection, TLS, or timeout reaching the backend.</summary>
    Unreachable,
    /// <summary>HTTP 401/403, or the credential grant being rejected.</summary>
    Unauthorized,
    /// <summary>HTTP 429: the backend rate limit.</summary>
    RateLimited,
    /// <summary>HTTP 503 with a Retry-After signal: slow down, retry later.</summary>
    Throttled,
    /// <summary>HTTP 5xx: the backend errored.</summary>
    BackendError,
    /// <summary>
    /// A 2xx response that is not the documented shape, an empty value
    /// where one was promised, a redirect, or a token with no usable
    /// lifetime (version drift). Treated as infrastructure: the backend
    /// answered unexpectedly.
    /// </summary>
    InvalidResponse,
    /// <summary>HTTP 404: the named resource is absent.</summary>
    NotFound,
    /// <summary>Operator configuration cannot work (bad URL, missing mapping or credential, unknown lease).</summary>
    Misconfigured,
}

/// <summary>
/// Base type for every credential-backend failure. Messages carry only
/// safe fields — HTTP status, host, secret identifiers, lease ids
/// (identity, never values) — truncated to <see cref="MaxMessageChars"/>,
/// so persisting <see cref="Exception.Message"/> (as the lease store does
/// for revocation failures) can never leak a credential. Use
/// <see cref="IsInfrastructure"/> — never the message text — to decide
/// retry/verdict handling. Backends subclass this so callers can still
/// catch a backend-typed exception; the classification and message
/// discipline live here once.
/// </summary>
public abstract class CredentialException : Exception
{
    /// <summary>Maximum characters kept in a message (server text is truncated).</summary>
    public const int MaxMessageChars = 300;

    /// <summary>Display name of the backend this failure came from (e.g. "Bitwarden").</summary>
    public string Backend { get; }

    public CredentialFailureKind Kind { get; }

    /// <summary>HTTP status that produced this failure, when one exists.</summary>
    public int? StatusCode { get; }

    /// <summary>Seconds from a Retry-After response header, when present.</summary>
    public int? RetryAfterSeconds { get; }

    /// <summary>
    /// True for backend-side failures: unreachable, unauthorised,
    /// rate-limited, throttled, server errors, and malformed success
    /// responses. These must never become a verdict on the work item's
    /// diff. False for operator configuration problems and absent
    /// resources.
    /// </summary>
    public bool IsInfrastructure => Kind is CredentialFailureKind.Unreachable
        or CredentialFailureKind.Unauthorized
        or CredentialFailureKind.RateLimited
        or CredentialFailureKind.Throttled
        or CredentialFailureKind.BackendError
        or CredentialFailureKind.InvalidResponse;

    /// <summary>
    /// Work-item failure kind for callers that must record one. Mirrors the
    /// host <see cref="WorkItemFailureKinds"/> vocabulary without
    /// referencing pipeline code: infrastructure for backend faults,
    /// configuration for operator/absent-secret faults. Either way the
    /// lease-issue path never fails the item — the manager runs it without
    /// the secret.
    /// </summary>
    public string FailureKindForWorkItem => IsInfrastructure
        ? WorkItemFailureKinds.Infrastructure
        : WorkItemFailureKinds.Configuration;

    protected CredentialException(
        string backend,
        CredentialFailureKind kind,
        string message,
        int? statusCode = null,
        int? retryAfterSeconds = null)
        : base(Truncate(backend, message))
    {
        Backend = backend;
        Kind = kind;
        StatusCode = statusCode;
        RetryAfterSeconds = retryAfterSeconds;
    }

    protected CredentialException(
        string backend,
        CredentialFailureKind kind,
        string message,
        Exception inner,
        int? statusCode = null)
        : base(Truncate(backend, message), inner)
    {
        Backend = backend;
        Kind = kind;
        StatusCode = statusCode;
    }

    /// <summary>
    /// The status-to-kind mapping every backend shares: 401/403 are
    /// authorisation, 404 absence, 429 rate limiting, 503-with-Retry-After
    /// throttling, 5xx backend errors, other 4xx operator shape, and
    /// anything else an unexpected answer.
    /// </summary>
    public static CredentialFailureKind KindForStatus(int statusCode, int? retryAfterSeconds)
        => statusCode switch
        {
            401 or 403 => CredentialFailureKind.Unauthorized,
            404 => CredentialFailureKind.NotFound,
            429 => CredentialFailureKind.RateLimited,
            503 when retryAfterSeconds.HasValue => CredentialFailureKind.Throttled,
            >= 500 => CredentialFailureKind.BackendError,
            >= 400 => CredentialFailureKind.Misconfigured,
            _ => CredentialFailureKind.InvalidResponse,
        };

    private static string Truncate(string backend, string message)
        => CredentialMessages.Truncate(message, $"{backend} request failed.", MaxMessageChars);
}
