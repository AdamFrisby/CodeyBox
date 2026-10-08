using System.Net;

namespace CodeyBox.HetznerSandboxPlugin;

/// <summary>
/// Broad classification of a Hetzner Cloud API failure. Quota, conflict, and
/// throttle classes are infrastructure signals — the cloud could not or would
/// not perform the requested operation — and never a verdict on a work item's
/// diff. The provider maps these onto provisioning deferrals.
/// </summary>
public enum HetznerFailureKind
{
    /// <summary>Transport-level failure: DNS, TLS, connect, or a truncated/over-bound response.</summary>
    Unreachable,

    /// <summary>401 — the API token is missing, invalid, or expired.</summary>
    Unauthorized,

    /// <summary>403 — the token is valid but not permitted for this operation/project.</summary>
    Forbidden,

    /// <summary>404 — the named server/image/type/location resource does not exist (or was deleted).</summary>
    NotFound,

    /// <summary>409 — the request conflicts with server-side state (e.g. duplicate name).</summary>
    Conflict,

    /// <summary>Quota or absolute limit exhausted (vendor <c>resource_limit_exceeded</c> family).</summary>
    QuotaExhausted,

    /// <summary>429 — rate limited; <see cref="HetznerApiException.RetryAfter"/> carries the hint.</summary>
    Throttled,

    /// <summary>5xx — the service itself failed.</summary>
    ServerError,

    /// <summary>A response the client could not interpret (shape drift) or a refused URL.</summary>
    Unexpected,
}

/// <summary>
/// Typed failure raised by the Hetzner Cloud client for every non-success
/// response or transport error. Carries the vendor error code so the caller
/// can classify the failure as infrastructure without scraping message text.
/// The API token is never included.
/// </summary>
public sealed class HetznerApiException : Exception
{
    /// <summary>Creates a typed Hetzner Cloud failure.</summary>
    public HetznerApiException(
        HetznerFailureKind kind,
        string operation,
        string detail,
        HttpStatusCode? statusCode = null,
        string? errorCode = null,
        TimeSpan? retryAfter = null,
        Exception? innerException = null)
        : base($"Hetzner {operation} failed ({Classify(kind, statusCode, errorCode)}): {detail}", innerException)
    {
        Kind = kind;
        Operation = operation;
        StatusCode = statusCode;
        ErrorCode = errorCode;
        RetryAfter = retryAfter;
    }

    /// <summary>Failure taxonomy.</summary>
    public HetznerFailureKind Kind { get; }

    /// <summary>Client operation that failed (e.g. "create server").</summary>
    public string Operation { get; }

    /// <summary>HTTP status when the failure came from a response.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>
    /// Vendor error code from the <c>{"error": {"code": …}}</c> body when
    /// present (e.g. <c>not_found</c>, <c>uniqueness_error</c>,
    /// <c>rate_limit_exceeded</c>, <c>resource_limit_exceeded</c>).
    /// </summary>
    public string? ErrorCode { get; }

    /// <summary>Retry hint from the <c>Retry-After</c> response header.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>
    /// True when the server may have acted despite the failure (transport cut,
    /// timeout, throttle, or 5xx): the caller must reconcile by stable
    /// request identity before resubmitting, never blindly retry a create.
    /// </summary>
    public bool MayHaveCreated => Kind is HetznerFailureKind.Unreachable
        or HetznerFailureKind.Throttled
        or HetznerFailureKind.ServerError;

    /// <summary>
    /// True when retrying after a delay may succeed: transport failures,
    /// conflicts with transient server-side state, throttles, and 5xx.
    /// Quota exhaustion is reported separately via <see cref="IsQuota"/> — it
    /// needs operator capacity action, not a blind retry.
    /// </summary>
    public bool IsTransient => Kind is HetznerFailureKind.Unreachable
        or HetznerFailureKind.Conflict
        or HetznerFailureKind.Throttled
        or HetznerFailureKind.ServerError;

    /// <summary>True when the failure means cloud quota/limits are exhausted.</summary>
    public bool IsQuota => Kind == HetznerFailureKind.QuotaExhausted;

    /// <summary>
    /// Maps an HTTP status plus the vendor error code/message onto the failure
    /// taxonomy. Any quota-shaped code or message wins over the status: the
    /// vendor reports exhausted limits across several statuses, and a limit
    /// breach must defer (operator capacity action) rather than fail the item.
    /// </summary>
    public static HetznerFailureKind FromStatus(
        HttpStatusCode status, string? errorCode, string? responseBody)
    {
        if (IsQuotaShaped(errorCode) || IsQuotaShaped(responseBody))
            return HetznerFailureKind.QuotaExhausted;
        return status switch
        {
            HttpStatusCode.Unauthorized => HetznerFailureKind.Unauthorized,
            HttpStatusCode.Forbidden => HetznerFailureKind.Forbidden,
            HttpStatusCode.NotFound => HetznerFailureKind.NotFound,
            HttpStatusCode.Conflict => HetznerFailureKind.Conflict,
            HttpStatusCode.UnprocessableEntity => HetznerFailureKind.Unexpected,
            HttpStatusCode.TooManyRequests => HetznerFailureKind.Throttled,
            _ when (int)status >= 500 => HetznerFailureKind.ServerError,
            _ => HetznerFailureKind.Unexpected,
        };
    }

    internal static bool IsQuotaShaped(string? text)
    {
        if (text is null)
            return false;
        if (text.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || text.Contains("resource_limit", StringComparison.OrdinalIgnoreCase)
            || text.Contains("insufficient", StringComparison.OrdinalIgnoreCase)
            || text.Contains("no_servers_available", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // A bare limit_exceeded marker means exhausted capacity — but the
        // match must be token-exact: "rate_limit_exceeded" is throttling, not
        // quota, and a substring match would misclassify every 429.
        return TokenEquals(text, "limit_exceeded");
    }

    private static bool TokenEquals(string text, string token)
    {
        foreach (var part in text.Split(
            [' ', '\t', '\r', '\n', '.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '{', '}'],
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string Classify(
        HetznerFailureKind kind, HttpStatusCode? status, string? errorCode)
    {
        var parts = new List<string> { kind.ToString() };
        if (status is { } code)
            parts.Add($"HTTP {(int)code}");
        if (!string.IsNullOrEmpty(errorCode))
            parts.Add(errorCode);
        return string.Join(" ", parts);
    }
}
