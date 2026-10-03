using System.Net;

namespace CodeyBox.OpenStackSandboxPlugin;

/// <summary>
/// Broad classification of an OpenStack service-side failure. Quota, conflict,
/// and throttle classes are infrastructure signals — the cloud could not or
/// would not perform the requested operation — and never a verdict on a work
/// item's diff. A later provider maps these onto provisioning deferrals.
/// </summary>
public enum OpenStackFailureKind
{
    /// <summary>Transport-level failure: DNS, TLS, connect, or a truncated/over-bound response.</summary>
    Unreachable,

    /// <summary>401 — the token is missing, invalid, or expired (the client re-authenticates once).</summary>
    Unauthorized,

    /// <summary>403 — the credential is valid but not permitted for this operation/project.</summary>
    Forbidden,

    /// <summary>404 — the named server/image/network resource does not exist (or was deleted).</summary>
    NotFound,

    /// <summary>409 — the request conflicts with server-side state (e.g. delete while building).</summary>
    Conflict,

    /// <summary>402/403-quota/413 — quota or absolute limit exhausted.</summary>
    QuotaExhausted,

    /// <summary>429 — rate limited; <see cref="OpenStackApiException.RetryAfter"/> carries the hint.</summary>
    Throttled,

    /// <summary>5xx — the service itself failed.</summary>
    ServerError,

    /// <summary>A response the client could not interpret (shape drift) or a refused URL.</summary>
    Unexpected,
}

/// <summary>
/// Typed failure raised by the OpenStack clients for every non-success response
/// or transport error. Carries the service's fault code and request id so the
/// caller can classify the failure as infrastructure without scraping message
/// text. The credential secret is never included.
/// </summary>
public sealed class OpenStackApiException : Exception
{
    /// <summary>Creates a typed OpenStack failure.</summary>
    public OpenStackApiException(
        OpenStackFailureKind kind,
        string operation,
        string detail,
        HttpStatusCode? statusCode = null,
        string? faultCode = null,
        string? requestId = null,
        TimeSpan? retryAfter = null,
        Exception? innerException = null)
        : base($"OpenStack {operation} failed ({Classify(kind, statusCode, faultCode, requestId)}): {detail}", innerException)
    {
        Kind = kind;
        Operation = operation;
        StatusCode = statusCode;
        FaultCode = faultCode;
        RequestId = requestId;
        RetryAfter = retryAfter;
    }

    /// <summary>Failure taxonomy.</summary>
    public OpenStackFailureKind Kind { get; }

    /// <summary>Client operation that failed (e.g. "create server").</summary>
    public string Operation { get; }

    /// <summary>HTTP status when the failure came from a response.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>
    /// OpenStack fault/error code from the response body when present
    /// (e.g. <c>overLimit</c>, <c>conflict</c>, <c>NeutronError</c>, <c>itemNotFound</c>).
    /// </summary>
    public string? FaultCode { get; }

    /// <summary>
    /// Service request id (<c>x-openstack-request-id</c> family) for operator
    /// correlation. Never a secret.
    /// </summary>
    public string? RequestId { get; }

    /// <summary>Retry hint: the <c>Retry-After</c> response header or an <c>overLimit</c> body value.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>
    /// True when retrying after a delay may succeed: transport failures,
    /// conflicts with transient server-side state, throttles, and 5xx.
    /// Quota exhaustion is reported separately via <see cref="IsQuota"/> — it
    /// needs operator capacity action, not a blind retry.
    /// </summary>
    public bool IsTransient => Kind is OpenStackFailureKind.Unreachable
        or OpenStackFailureKind.Conflict
        or OpenStackFailureKind.Throttled
        or OpenStackFailureKind.ServerError;

    /// <summary>True when the failure means cloud quota/limits are exhausted.</summary>
    public bool IsQuota => Kind == OpenStackFailureKind.QuotaExhausted;

    /// <summary>
    /// Maps an HTTP status onto the failure taxonomy. A 403 whose body names
    /// quota/limit/overLimit is quota-shaped — some deployments report quota
    /// breaches as 403 — and a 413 (over-limit) is always quota.
    /// </summary>
    public static OpenStackFailureKind FromStatus(HttpStatusCode status, string? responseBody) =>
        status switch
        {
            HttpStatusCode.Unauthorized => OpenStackFailureKind.Unauthorized,
            HttpStatusCode.PaymentRequired => OpenStackFailureKind.QuotaExhausted,
            HttpStatusCode.Forbidden => IsQuotaShaped(responseBody)
                ? OpenStackFailureKind.QuotaExhausted : OpenStackFailureKind.Forbidden,
            HttpStatusCode.NotFound => OpenStackFailureKind.NotFound,
            HttpStatusCode.Conflict => OpenStackFailureKind.Conflict,
            HttpStatusCode.RequestEntityTooLarge => OpenStackFailureKind.QuotaExhausted,
            HttpStatusCode.TooManyRequests => OpenStackFailureKind.Throttled,
            _ when (int)status == 413 => OpenStackFailureKind.QuotaExhausted,
            _ when (int)status >= 500 => OpenStackFailureKind.ServerError,
            _ => OpenStackFailureKind.Unexpected,
        };

    internal static bool IsQuotaShaped(string? body) => body is not null
        && (body.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || body.Contains("overlimit", StringComparison.OrdinalIgnoreCase)
            || body.Contains("over_limit", StringComparison.OrdinalIgnoreCase)
            || body.Contains("limit exceeded", StringComparison.OrdinalIgnoreCase)
            || body.Contains("insufficient", StringComparison.OrdinalIgnoreCase));

    private static string Classify(
        OpenStackFailureKind kind, HttpStatusCode? status, string? faultCode, string? requestId)
    {
        var parts = new List<string> { kind.ToString() };
        if (status is { } code)
            parts.Add($"HTTP {(int)code}");
        if (!string.IsNullOrEmpty(faultCode))
            parts.Add(faultCode);
        if (!string.IsNullOrEmpty(requestId))
            parts.Add($"request {requestId}");
        return string.Join(" ", parts);
    }
}
