using System.Net;

namespace CodeyBox.BlaxelPlugin;

/// <summary>Service-side failure signalled by the Blaxel control-plane or sandbox API, or by transport.</summary>
public sealed class BlaxelApiException : Exception
{
    public BlaxelApiException(HttpStatusCode? statusCode, string errorClass, string detail)
        : base($"blaxel api failure: status={(statusCode.HasValue ? (int)statusCode.Value : "transport")} errorClass={errorClass} detail={detail}")
    {
        StatusCode = statusCode;
        ErrorClass = errorClass;
        Detail = detail;
    }

    public BlaxelApiException(HttpStatusCode? statusCode, string errorClass, string detail, Exception inner)
        : base($"blaxel api failure: status={(statusCode.HasValue ? (int)statusCode.Value : "transport")} errorClass={errorClass} detail={detail}", inner)
    {
        StatusCode = statusCode;
        ErrorClass = errorClass;
        Detail = detail;
    }

    public HttpStatusCode? StatusCode { get; }

    public string ErrorClass { get; }

    public string Detail { get; }
}

/// <summary>
/// Pure classification of Blaxel service-side failures. Every failure the
/// service reports — unreachable, unauthorised, throttled, conflict, server
/// error — is infrastructure: it says the guest could not be provided or
/// observed, never that the work item's diff failed. Callers convert the
/// returned <see cref="BlaxelInfrastructureFailure"/> into the pipeline's
/// deferral/unavailable exceptions; a non-zero guest exit code is NOT a
/// service failure and flows back as an ordinary exec result.
/// </summary>
public static class BlaxelFailureClassification
{
    /// <summary>Classifies an HTTP status (or transport failure when null) as infrastructure.</summary>
    public static BlaxelInfrastructureFailure Classify(HttpStatusCode? status, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        var errorClass = status switch
        {
            null => "unreachable",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "unauthorised",
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
            "unreachable" or "timeout" or "server-error" => TimeSpan.FromSeconds(30),
            "conflict" => TimeSpan.FromSeconds(30),
            "unauthorised" => TimeSpan.FromMinutes(5),
            _ => TimeSpan.FromSeconds(30),
        };

        // 4xx shapes other than throttling are still infrastructure (operator or
        // quota misconfiguration), but the longer recheck avoids hot-spinning a
        // persistently rejected request; only the message — never the verdict —
        // carries that distinction.
        return new BlaxelInfrastructureFailure(operation, errorClass, recheckIn);
    }

    /// <summary>True when the exception already proves infrastructure (never a diff verdict).</summary>
    public static bool IsInfrastructure(Exception ex) =>
        ex is BlaxelApiException or HttpRequestException or TimeoutException or TaskCanceledException;
}

/// <summary>Pure record describing one infrastructure failure and its backoff.</summary>
public sealed record BlaxelInfrastructureFailure(string Operation, string ErrorClass, TimeSpan RecheckIn);
