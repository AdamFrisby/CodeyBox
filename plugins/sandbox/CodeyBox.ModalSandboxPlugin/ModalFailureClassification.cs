using System.Net;

namespace CodeyBox.ModalPlugin;

/// <summary>Service-side failure signalled by the Modal control plane or transport.</summary>
public sealed class ModalApiException : Exception
{
    public ModalApiException(HttpStatusCode? statusCode, string errorClass, string detail, TimeSpan? retryAfter = null)
        : base($"modal api failure: status={(statusCode.HasValue ? (int)statusCode.Value : "transport")} errorClass={errorClass} detail={detail}")
    {
        StatusCode = statusCode;
        ErrorClass = errorClass;
        Detail = detail;
        RetryAfter = retryAfter;
    }

    public ModalApiException(HttpStatusCode? statusCode, string errorClass, string detail, Exception inner)
        : base($"modal api failure: status={(statusCode.HasValue ? (int)statusCode.Value : "transport")} errorClass={errorClass} detail={detail}", inner)
    {
        StatusCode = statusCode;
        ErrorClass = errorClass;
        Detail = detail;
        RetryAfter = null;
    }

    public HttpStatusCode? StatusCode { get; }

    public string ErrorClass { get; }

    public string Detail { get; }

    /// <summary>Server-advised backoff from the Retry-After response header, when present.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// Pure classification of Modal service-side failures. Every failure the
/// service reports — unreachable, unauthorised, throttled, quota-exhausted,
/// server error — is infrastructure: it says the guest could not be provided
/// or observed, never that the work item's diff failed. Callers convert the
/// returned <see cref="ModalInfrastructureFailure"/> into the pipeline's
/// deferral/unavailable outcomes; a non-zero guest exit code is NOT a service
/// failure and flows back as an ordinary exec result.
/// </summary>
public static class ModalFailureClassification
{
    /// <summary>Classifies an HTTP status (or transport failure when null) as infrastructure.</summary>
    public static ModalInfrastructureFailure Classify(HttpStatusCode? status, string operation, TimeSpan? retryAfter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        var errorClass = status switch
        {
            null => "unreachable",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "unauthorised",
            HttpStatusCode.PaymentRequired => "quota-exhausted",
            HttpStatusCode.TooManyRequests => "throttled",
            HttpStatusCode.NotFound => "not-found",
            HttpStatusCode.Conflict => "conflict",
            HttpStatusCode.RequestTimeout => "timeout",
            _ when (int)status >= 500 => "server-error",
            _ when (int)status is 408 or 425 or 429 => "throttled",
            _ => "request-rejected",
        };

        var recheckIn = errorClass switch
        {
            "throttled" => TimeSpan.FromMinutes(2),
            "quota-exhausted" => TimeSpan.FromMinutes(5),
            "unreachable" or "timeout" or "server-error" => TimeSpan.FromSeconds(30),
            "conflict" => TimeSpan.FromSeconds(30),
            "unauthorised" => TimeSpan.FromMinutes(5),
            _ => TimeSpan.FromSeconds(30),
        };

        if (retryAfter is { } hint && hint > TimeSpan.Zero && hint < TimeSpan.FromHours(1))
        {
            recheckIn = hint > recheckIn ? hint : recheckIn;
        }

        return new ModalInfrastructureFailure(operation, errorClass, recheckIn);
    }

    /// <summary>True when the exception already proves infrastructure (never a diff verdict).</summary>
    public static bool IsInfrastructure(Exception ex) =>
        ex is ModalApiException or HttpRequestException or TimeoutException or TaskCanceledException;
}

/// <summary>Pure record describing one infrastructure failure and its backoff.</summary>
public sealed record ModalInfrastructureFailure(string Operation, string ErrorClass, TimeSpan RecheckIn);
