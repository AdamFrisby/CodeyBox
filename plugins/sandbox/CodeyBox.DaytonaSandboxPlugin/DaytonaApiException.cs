using System.Net;
using CodeyBox.Core;

namespace CodeyBox.DaytonaSandboxPlugin;

/// <summary>
/// Broad classification of a Daytona service-side failure. Every class is an
/// infrastructure signal — the service could not or would not perform the
/// requested operation — and never a verdict on the work item's diff. The
/// provider maps these onto <see cref="SandboxProvisioningDeferredException"/>
/// deferrals at create time and <c>ExecutionUnavailable</c> exec results.
/// </summary>
public enum DaytonaFailureKind
{
    /// <summary>Transport-level failure: DNS, TLS, connect, or a truncated response.</summary>
    Unreachable,

    /// <summary>401 — the API key is missing, invalid, or expired.</summary>
    Unauthorized,

    /// <summary>403 — the key is valid but not permitted for this operation/org.</summary>
    Forbidden,

    /// <summary>404 — the named sandbox/snapshot does not exist (or was deleted).</summary>
    NotFound,

    /// <summary>409/422 — the request conflicts with service-side state.</summary>
    Conflict,

    /// <summary>429 — rate limited; <see cref="DaytonaApiException.RetryAfter"/> may carry the hint.</summary>
    Throttled,

    /// <summary>402 — quota/billing exhausted on the organization.</summary>
    QuotaExhausted,

    /// <summary>5xx — the service itself failed.</summary>
    ServerError,

    /// <summary>A response the client could not interpret (shape drift).</summary>
    Unexpected,
}

/// <summary>
/// Typed failure raised by the Daytona clients for every non-success response
/// or transport error. Carries enough structure for the provider to classify
/// the failure as infrastructure without scraping the message text.
/// </summary>
public sealed class DaytonaApiException : Exception
{
    public DaytonaApiException(
        DaytonaFailureKind kind,
        string operation,
        string detail,
        HttpStatusCode? statusCode = null,
        TimeSpan? retryAfter = null,
        Exception? innerException = null)
        : base($"Daytona {operation} failed ({Classify(kind, statusCode)}): {detail}", innerException)
    {
        Kind = kind;
        Operation = operation;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public DaytonaFailureKind Kind { get; }
    public string Operation { get; }
    public HttpStatusCode? StatusCode { get; }
    public TimeSpan? RetryAfter { get; }

    /// <summary>True when retrying after a delay may succeed (throttle, 5xx, transport).</summary>
    public bool IsTransient =>
        Kind is DaytonaFailureKind.Unreachable or DaytonaFailureKind.Throttled or DaytonaFailureKind.ServerError;

    /// <summary>
    /// Maps an HTTP status onto the failure taxonomy. A 402 is quota
    /// exhaustion; a 403 whose body names quota/limit is also quota-shaped —
    /// some Daytona deployments report org quota breaches as 403.
    /// </summary>
    public static DaytonaFailureKind FromStatus(HttpStatusCode status, string? responseBody)
    {
        var quotaShaped = responseBody is not null
            && (responseBody.Contains("quota", StringComparison.OrdinalIgnoreCase)
                || responseBody.Contains("limit exceeded", StringComparison.OrdinalIgnoreCase)
                || responseBody.Contains("insufficient", StringComparison.OrdinalIgnoreCase));
        return status switch
        {
            HttpStatusCode.Unauthorized => DaytonaFailureKind.Unauthorized,
            HttpStatusCode.PaymentRequired => DaytonaFailureKind.QuotaExhausted,
            HttpStatusCode.Forbidden => quotaShaped ? DaytonaFailureKind.QuotaExhausted : DaytonaFailureKind.Forbidden,
            HttpStatusCode.NotFound => DaytonaFailureKind.NotFound,
            HttpStatusCode.Conflict or HttpStatusCode.UnprocessableContent => DaytonaFailureKind.Conflict,
            HttpStatusCode.TooManyRequests => DaytonaFailureKind.Throttled,
            _ when (int)status >= 500 => DaytonaFailureKind.ServerError,
            _ => DaytonaFailureKind.Unexpected,
        };
    }

    private static string Classify(DaytonaFailureKind kind, HttpStatusCode? status) =>
        status is { } code ? $"{kind} HTTP {(int)code}" : kind.ToString();
}
