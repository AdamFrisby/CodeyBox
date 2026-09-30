using System.Net;
using CodeyBox.Core;

namespace CodeyBox.BoxLiteSandboxPlugin;

/// <summary>
/// Broad classification of a BoxLite service-side failure. Every class is an
/// infrastructure signal — the service could not or would not perform the
/// requested operation — and never a verdict on the work item's diff. The
/// provider maps these onto <see cref="SandboxProvisioningDeferredException"/>
/// deferrals at create time and <c>ExecutionUnavailable</c> exec results.
/// </summary>
public enum BoxLiteFailureKind
{
    /// <summary>Transport-level failure: daemon socket down, TLS, connect, or a truncated response.</summary>
    Unreachable,

    /// <summary>401 — the API token is missing, invalid, or expired.</summary>
    Unauthorized,

    /// <summary>403 — the token is valid but not permitted for this operation.</summary>
    Forbidden,

    /// <summary>404 — the named VM/snapshot/exec does not exist (or was deleted).</summary>
    NotFound,

    /// <summary>409/422 — the request conflicts with service-side state.</summary>
    Conflict,

    /// <summary>429 — rate limited; <see cref="BoxLiteApiException.RetryAfter"/> may carry the hint.</summary>
    Throttled,

    /// <summary>402 — quota exhausted (too many live VMs, disk budget spent).</summary>
    QuotaExhausted,

    /// <summary>5xx — the daemon itself failed.</summary>
    ServerError,

    /// <summary>A response the client could not interpret (shape drift).</summary>
    Unexpected,
}

/// <summary>
/// Typed failure raised by the BoxLite client for every non-success response
/// or transport error. Carries enough structure for the provider to classify
/// the failure as infrastructure without scraping the message text.
/// </summary>
public sealed class BoxLiteApiException : Exception
{
    public BoxLiteApiException(
        BoxLiteFailureKind kind,
        string operation,
        string detail,
        HttpStatusCode? statusCode = null,
        TimeSpan? retryAfter = null,
        Exception? innerException = null)
        : base($"BoxLite {operation} failed ({Classify(kind, statusCode)}): {detail}", innerException)
    {
        Kind = kind;
        Operation = operation;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public BoxLiteFailureKind Kind { get; }
    public string Operation { get; }
    public HttpStatusCode? StatusCode { get; }
    public TimeSpan? RetryAfter { get; }

    /// <summary>True when retrying after a delay may succeed (throttle, 5xx, transport).</summary>
    public bool IsTransient =>
        Kind is BoxLiteFailureKind.Unreachable or BoxLiteFailureKind.Throttled or BoxLiteFailureKind.ServerError;

    /// <summary>
    /// Maps an HTTP status onto the failure taxonomy. A 402 is quota
    /// exhaustion; a 403 whose body names quota/limit is also quota-shaped.
    /// </summary>
    public static BoxLiteFailureKind FromStatus(HttpStatusCode status, string? responseBody)
    {
        var quotaShaped = responseBody is not null
            && (responseBody.Contains("quota", StringComparison.OrdinalIgnoreCase)
                || responseBody.Contains("limit exceeded", StringComparison.OrdinalIgnoreCase)
                || responseBody.Contains("insufficient", StringComparison.OrdinalIgnoreCase));
        return status switch
        {
            HttpStatusCode.Unauthorized => BoxLiteFailureKind.Unauthorized,
            HttpStatusCode.PaymentRequired => BoxLiteFailureKind.QuotaExhausted,
            HttpStatusCode.Forbidden => quotaShaped ? BoxLiteFailureKind.QuotaExhausted : BoxLiteFailureKind.Forbidden,
            HttpStatusCode.NotFound => BoxLiteFailureKind.NotFound,
            HttpStatusCode.Conflict or HttpStatusCode.UnprocessableContent => BoxLiteFailureKind.Conflict,
            HttpStatusCode.TooManyRequests => BoxLiteFailureKind.Throttled,
            _ when (int)status >= 500 => BoxLiteFailureKind.ServerError,
            _ => BoxLiteFailureKind.Unexpected,
        };
    }

    private static string Classify(BoxLiteFailureKind kind, HttpStatusCode? status) =>
        status is { } code ? $"{kind} HTTP {(int)code}" : kind.ToString();
}
