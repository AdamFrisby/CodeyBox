using System.Net;

namespace CodeyBox.E2bSandboxPlugin;

/// <summary>
/// Pure classification of E2B service-side failures. Every failure the
/// service reports — unreachable, unauthorised, throttled, quota-exhausted,
/// conflict, server error — is infrastructure: it says the guest could not be
/// provided or observed, never that the work item's diff failed. Callers
/// convert the returned <see cref="E2bInfrastructureFailure"/> into the
/// pipeline's deferral/unavailable exceptions; a non-zero guest exit code is
/// NOT a service failure and flows back as an ordinary exec result.
/// </summary>
public static class E2bFailureClassification
{
    /// <summary>Classifies an HTTP status (or transport failure when null) as infrastructure.</summary>
    public static E2bInfrastructureFailure Classify(HttpStatusCode? status, string operation)
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
            HttpStatusCode.PaymentRequired => "quota-exhausted",
            _ when (int)status >= 500 => "server-error",
            _ when (int)status is 408 or 425 or 429 => "throttled",
            _ => "request-rejected",
        };

        var recheckIn = errorClass switch
        {
            "throttled" => TimeSpan.FromMinutes(2),
            "quota-exhausted" => TimeSpan.FromMinutes(5),
            "unauthorised" => TimeSpan.FromMinutes(5),
            "unreachable" or "timeout" or "server-error" => TimeSpan.FromSeconds(30),
            "conflict" => TimeSpan.FromSeconds(30),
            _ => TimeSpan.FromSeconds(30),
        };

        // 4xx shapes other than throttling/quota are still infrastructure
        // (operator or quota misconfiguration), but the longer recheck avoids
        // hot-spinning a persistently rejected request; only the message —
        // never the verdict — carries that distinction.
        return new E2bInfrastructureFailure(operation, errorClass, recheckIn);
    }

    /// <summary>True when the exception already proves infrastructure (never a diff verdict).</summary>
    public static bool IsInfrastructure(Exception ex) =>
        ex is E2bApiException or HttpRequestException or TimeoutException or TaskCanceledException;
}

/// <summary>Pure record describing one infrastructure failure and its backoff.</summary>
public sealed record E2bInfrastructureFailure(string Operation, string ErrorClass, TimeSpan RecheckIn);
