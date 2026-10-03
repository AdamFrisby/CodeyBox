using System.Net;

namespace CodeyBox.MicrosandboxPlugin;

/// <summary>
/// Service-side failure classification for the microsandbox server. Every
/// value maps to a <c>SandboxProvisioningDeferredException</c> error class so
/// the pipeline treats a rejecting/unreachable service as an infrastructure
/// failure — never as a verdict on the work item's diff.
/// </summary>
internal enum MicrosandboxFailureKind
{
    Unauthorized,
    Throttled,
    QuotaExhausted,
    ServerError,
    Unreachable,
    Conflict,
    NotFound,
    Unexpected,
}

/// <summary>
/// Transport exception thrown by <see cref="MicrosandboxApiClient"/> for
/// non-success server responses and transport failures. Carries the
/// classified <see cref="Kind"/> plus the operation and a bounded detail
/// string (never a full response dump, which may echo operator input).
/// </summary>
internal sealed class MicrosandboxApiException : Exception
{
    public MicrosandboxFailureKind Kind { get; }
    public string Operation { get; }
    public HttpStatusCode? StatusCode { get; }

    public MicrosandboxApiException(MicrosandboxFailureKind kind, string operation, string detail)
        : base(detail)
    {
        Kind = kind;
        Operation = operation;
    }

    public MicrosandboxApiException(
        MicrosandboxFailureKind kind,
        string operation,
        string detail,
        HttpStatusCode statusCode)
        : base(detail)
    {
        Kind = kind;
        Operation = operation;
        StatusCode = statusCode;
    }

    public MicrosandboxApiException(MicrosandboxFailureKind kind, string operation, string detail, Exception inner)
        : base(detail, inner)
    {
        Kind = kind;
        Operation = operation;
    }

    /// <summary>
    /// Classifies an HTTP status plus a bounded body excerpt into a failure
    /// kind. Bodies are matched by exact keyword containment against a fixed
    /// allowlist ("quota", "capacity", "limit") — never by substring against
    /// credentials — and only to distinguish quota-shaped 402/403/429 from
    /// generic throttling/auth failures.
    /// </summary>
    public static MicrosandboxFailureKind ClassifyStatus(HttpStatusCode status, string? bodyExcerpt)
    {
        var body = bodyExcerpt ?? string.Empty;
        var quotaShaped = body.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || body.Contains("capacity", StringComparison.OrdinalIgnoreCase)
            || body.Contains("too many", StringComparison.OrdinalIgnoreCase);
        return (int)status switch
        {
            401 => MicrosandboxFailureKind.Unauthorized,
            403 when quotaShaped => MicrosandboxFailureKind.QuotaExhausted,
            403 => MicrosandboxFailureKind.Unauthorized,
            402 => MicrosandboxFailureKind.QuotaExhausted,
            429 when quotaShaped => MicrosandboxFailureKind.QuotaExhausted,
            429 => MicrosandboxFailureKind.Throttled,
            404 => MicrosandboxFailureKind.NotFound,
            409 => MicrosandboxFailureKind.Conflict,
            >= 500 => MicrosandboxFailureKind.ServerError,
            _ => MicrosandboxFailureKind.Unexpected,
        };
    }

    /// <summary>Maps a failure kind to the provisioning-deferred error class.</summary>
    public static string ToErrorClass(MicrosandboxFailureKind kind) => kind switch
    {
        MicrosandboxFailureKind.Unauthorized => "unauthorized",
        MicrosandboxFailureKind.Throttled => "throttled",
        MicrosandboxFailureKind.QuotaExhausted => "quota-exhausted",
        MicrosandboxFailureKind.Unreachable => "unreachable",
        MicrosandboxFailureKind.ServerError => "server-error",
        MicrosandboxFailureKind.Conflict => "conflict",
        MicrosandboxFailureKind.NotFound => "not-found",
        _ => "unexpected",
    };
}
