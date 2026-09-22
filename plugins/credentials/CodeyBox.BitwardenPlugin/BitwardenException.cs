using CodeyBox.Core;

namespace CodeyBox.BitwardenPlugin;

/// <summary>
/// How a Bitwarden Secrets Manager call failed. The backend-unreachable,
/// unauthorised, rate-limited, throttled, and server-error shapes are
/// <see cref="IsInfrastructure"/>: they describe the secret backend, never
/// a verdict on the work item's diff. The lease manager already treats
/// every issue/renew/revoke failure this way (the item runs without the
/// secret; renewal and revocation retry on the sweep); this classification
/// keeps that mapping explicit and testable for any future caller.
/// </summary>
public enum BitwardenFailureKind
{
    /// <summary>DNS, connection, TLS, or timeout reaching the identity or API origin.</summary>
    Unreachable,
    /// <summary>HTTP 401/403, or the identity server rejecting the machine-account credential (invalid_client).</summary>
    Unauthorized,
    /// <summary>HTTP 429: the backend rate limit.</summary>
    RateLimited,
    /// <summary>HTTP 503 with a Retry-After signal: slow down, retry later.</summary>
    Throttled,
    /// <summary>HTTP 5xx: the backend errored.</summary>
    BackendError,
    /// <summary>
    /// A 2xx response that is not the documented shape, an empty value
    /// where one was promised, or a token with no usable lifetime (version
    /// drift). Treated as infrastructure: the backend answered unexpectedly.
    /// </summary>
    InvalidResponse,
    /// <summary>HTTP 404: the named secret is absent, or no secret matches the mapped key.</summary>
    NotFound,
    /// <summary>Operator configuration cannot work (bad URL, missing mapping or credential, unknown lease).</summary>
    Misconfigured,
}

/// <summary>
/// Typed failure for every Bitwarden backend call. Messages carry only
/// safe fields — HTTP status, host, secret ids/keys, organisation and
/// project ids, lease ids (identity, never values) — truncated to
/// <see cref="MaxMessageChars"/>, so persisting <c>Message</c> (as the lease
/// store does for revocation failures) can never leak a credential. Use
/// <see cref="IsInfrastructure"/> — never the message text — to decide
/// retry/verdict handling.
/// </summary>
public sealed class BitwardenException : Exception
{
    /// <summary>Maximum characters kept in a message (server text is truncated).</summary>
    public const int MaxMessageChars = 300;

    public BitwardenFailureKind Kind { get; }

    /// <summary>HTTP status that produced this failure, when one exists.</summary>
    public int? StatusCode { get; }

    /// <summary>Seconds from a Retry-After response header, when present.</summary>
    public int? RetryAfterSeconds { get; }

    /// <summary>
    /// True for backend-side failures: unreachable, unauthorised,
    /// rate-limited, throttled, server errors, and malformed success
    /// responses. These must never become a verdict on the work item's
    /// diff. False for operator configuration problems and absent secrets.
    /// </summary>
    public bool IsInfrastructure => Kind is BitwardenFailureKind.Unreachable
        or BitwardenFailureKind.Unauthorized
        or BitwardenFailureKind.RateLimited
        or BitwardenFailureKind.Throttled
        or BitwardenFailureKind.BackendError
        or BitwardenFailureKind.InvalidResponse;

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

    public BitwardenException(
        BitwardenFailureKind kind, string message, int? statusCode = null, int? retryAfterSeconds = null)
        : base(Truncate(message))
    {
        Kind = kind;
        StatusCode = statusCode;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public BitwardenException(
        BitwardenFailureKind kind, string message, Exception inner, int? statusCode = null)
        : base(Truncate(message), inner)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    private static string Truncate(string message)
    {
        if (string.IsNullOrEmpty(message))
            return "Bitwarden request failed.";
        // Flatten control characters: messages flow into host logs and the
        // persisted lease store, so embedded newlines must not forge log
        // lines regardless of which caller constructed the text.
        var flat = message.Replace('\n', ' ').Replace('\r', ' ');
        return flat.Length <= MaxMessageChars ? flat : flat[..MaxMessageChars];
    }

    /// <summary>Builds an exception from an HTTP status with safe context only.</summary>
    public static BitwardenException FromStatus(
        int statusCode, string operation, string detail, int? retryAfterSeconds = null)
    {
        var kind = statusCode switch
        {
            401 or 403 => BitwardenFailureKind.Unauthorized,
            404 => BitwardenFailureKind.NotFound,
            429 => BitwardenFailureKind.RateLimited,
            503 when retryAfterSeconds.HasValue => BitwardenFailureKind.Throttled,
            >= 500 => BitwardenFailureKind.BackendError,
            >= 400 => BitwardenFailureKind.Misconfigured,
            _ => BitwardenFailureKind.InvalidResponse,
        };
        return new BitwardenException(
            kind,
            $"Bitwarden {operation} failed with HTTP {statusCode}: {detail}",
            statusCode,
            retryAfterSeconds);
    }
}
