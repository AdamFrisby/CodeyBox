using System.Net;

namespace CodeyBox.Ec2SandboxPlugin;

/// <summary>
/// Broad classification of an EC2 Query API failure. Quota, conflict, and
/// throttle classes are infrastructure signals — the cloud could not or would
/// not perform the requested operation — and never a verdict on a work item's
/// diff. The provider maps these onto provisioning deferrals while preserving
/// the unavailable/deferred/quota/error distinctions.
/// </summary>
public enum Ec2FailureKind
{
    /// <summary>Transport-level failure: DNS, TLS, connect, or a truncated/over-bound response.</summary>
    Unreachable,

    /// <summary>
    /// 401, or an auth-shaped EC2 code (AuthFailure, InvalidClientTokenId,
    /// ExpiredToken, SignatureDoesNotMatch, InvalidAccessKeyId): the signing
    /// identity is missing or rejected.
    /// </summary>
    Unauthorized,

    /// <summary>403 that is not auth-shaped: the identity lacks permission for this operation.</summary>
    Forbidden,

    /// <summary>404, or a NotFound-shaped EC2 code: the named resource does not exist (or was deleted).</summary>
    NotFound,

    /// <summary>409, or a duplicate-state EC2 code: the resource already exists under another owner.</summary>
    Conflict,

    /// <summary>Quota, limit, or capacity exhaustion (the InstanceLimitExceeded / Insufficient* family).</summary>
    QuotaExhausted,

    /// <summary>429 or a throttling EC2 code; no retry hint is carried on this protocol.</summary>
    Throttled,

    /// <summary>5xx — the service itself failed.</summary>
    ServerError,

    /// <summary>A response the client could not interpret (shape drift) or a refused URL.</summary>
    Unexpected,
}

/// <summary>
/// Typed failure raised by the EC2 Query client for every non-success
/// response or transport error. Carries the vendor error code so the caller
/// can classify the failure as infrastructure without scraping message text.
/// Signing secrets are never included.
/// </summary>
public sealed class Ec2ApiException : Exception
{
    /// <summary>Creates a typed EC2 failure.</summary>
    public Ec2ApiException(
        Ec2FailureKind kind,
        string operation,
        string detail,
        HttpStatusCode? statusCode = null,
        string? errorCode = null,
        Exception? innerException = null)
        : base($"EC2 {operation} failed ({Classify(kind, statusCode, errorCode)}): {detail}", innerException)
    {
        Kind = kind;
        Operation = operation;
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    /// <summary>Failure taxonomy.</summary>
    public Ec2FailureKind Kind { get; }

    /// <summary>Client operation that failed (e.g. "run instances").</summary>
    public string Operation { get; }

    /// <summary>HTTP status when the failure came from a response.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>
    /// Vendor error code from the <c>&lt;Code&gt;</c> element when present
    /// (e.g. <c>InvalidAMIID.NotFound</c>, <c>InstanceLimitExceeded</c>,
    /// <c>RequestThrottled</c>).
    /// </summary>
    public string? ErrorCode { get; }

    /// <summary>
    /// True when the server may have acted despite the failure (transport cut,
    /// timeout, throttle, or 5xx): the caller must reconcile by stable
    /// client-token identity before resubmitting, never blindly retry a create.
    /// </summary>
    public bool MayHaveCreated => Kind is Ec2FailureKind.Unreachable
        or Ec2FailureKind.Throttled
        or Ec2FailureKind.ServerError;

    /// <summary>
    /// True when retrying after a delay may succeed: transport failures,
    /// conflicts with transient server-side state, throttles, and 5xx.
    /// Quota exhaustion is reported separately via <see cref="IsQuota"/> — it
    /// needs operator capacity action, not a blind retry.
    /// </summary>
    public bool IsTransient => Kind is Ec2FailureKind.Unreachable
        or Ec2FailureKind.Conflict
        or Ec2FailureKind.Throttled
        or Ec2FailureKind.ServerError;

    /// <summary>True when the failure means cloud quota/limits/capacity are exhausted.</summary>
    public bool IsQuota => Kind == Ec2FailureKind.QuotaExhausted;

    /// <summary>
    /// Maps an HTTP status plus the vendor error code/message onto the failure
    /// taxonomy. Any quota/capacity-shaped code wins over the status: EC2
    /// reports exhausted limits across several statuses, and a limit breach
    /// must defer (operator capacity action) rather than fail the item.
    /// </summary>
    public static Ec2FailureKind FromStatus(
        HttpStatusCode status, string? errorCode, string? responseBody)
    {
        if (IsQuotaShaped(errorCode) || IsQuotaShaped(responseBody))
            return Ec2FailureKind.QuotaExhausted;
        if (IsThrottled(errorCode))
            return Ec2FailureKind.Throttled;
        if (IsAuthShaped(errorCode))
            return Ec2FailureKind.Unauthorized;
        if (IsNotFoundShaped(errorCode))
            return Ec2FailureKind.NotFound;
        if (IsConflictShaped(errorCode))
            return Ec2FailureKind.Conflict;
        return status switch
        {
            HttpStatusCode.Unauthorized => Ec2FailureKind.Unauthorized,
            HttpStatusCode.Forbidden => Ec2FailureKind.Forbidden,
            HttpStatusCode.NotFound => Ec2FailureKind.NotFound,
            HttpStatusCode.Conflict => Ec2FailureKind.Conflict,
            HttpStatusCode.TooManyRequests => Ec2FailureKind.Throttled,
            _ when (int)status >= 500 => Ec2FailureKind.ServerError,
            _ => Ec2FailureKind.Unexpected,
        };
    }

    internal static bool IsQuotaShaped(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return text.Contains("InstanceLimitExceeded", StringComparison.OrdinalIgnoreCase)
            || text.Contains("VolumeLimitExceeded", StringComparison.OrdinalIgnoreCase)
            || text.Contains("RequestLimitExceeded", StringComparison.OrdinalIgnoreCase)
            || text.Contains("LimitExceeded", StringComparison.OrdinalIgnoreCase)
            || text.Contains("InsufficientInstanceCapacity", StringComparison.OrdinalIgnoreCase)
            || text.Contains("InsufficientFreeAddressesInSubnet", StringComparison.OrdinalIgnoreCase)
            || text.Contains("InsufficientCapacity", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Unsupported", StringComparison.OrdinalIgnoreCase)
            || text.Contains("quota", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsThrottled(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return false;
        return code.Contains("Throttl", StringComparison.OrdinalIgnoreCase)
            || code.Contains("PriorRequestNotComplete", StringComparison.Ordinal)
            || code.Contains("RequestThrottled", StringComparison.Ordinal);
    }

    internal static bool IsAuthShaped(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return false;
        return code.Contains("AuthFailure", StringComparison.Ordinal)
            || code.Contains("InvalidClientTokenId", StringComparison.Ordinal)
            || code.Contains("ExpiredToken", StringComparison.Ordinal)
            || code.Contains("SignatureDoesNotMatch", StringComparison.Ordinal)
            || code.Contains("InvalidAccessKeyId", StringComparison.Ordinal)
            || code.Contains("MissingAction", StringComparison.Ordinal);
    }

    internal static bool IsNotFoundShaped(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return false;
        return code.EndsWith(".NotFound", StringComparison.Ordinal)
            || code.Equals("InvalidInstanceID.NotFound", StringComparison.Ordinal)
            || code.Equals("InvalidAMIID.NotFound", StringComparison.Ordinal)
            || code.Equals("InvalidGroup.NotFound", StringComparison.Ordinal)
            || code.Equals("InvalidKeyPair.NotFound", StringComparison.Ordinal)
            || code.Equals("InvalidAllocationID.NotFound", StringComparison.Ordinal)
            || code.Equals("InvalidAssociationID.NotFound", StringComparison.Ordinal)
            || code.Equals("InvalidVolume.NotFound", StringComparison.Ordinal)
            || code.Equals("InvalidSubnetID.NotFound", StringComparison.Ordinal);
    }

    internal static bool IsConflictShaped(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return false;
        return code.Contains(".Duplicate", StringComparison.Ordinal)
            || code.Equals("OperationInProgress", StringComparison.Ordinal)
            || code.Equals("IncorrectState", StringComparison.Ordinal);
    }

    private static string Classify(
        Ec2FailureKind kind, HttpStatusCode? status, string? errorCode)
    {
        var parts = new List<string> { kind.ToString() };
        if (status is { } code)
            parts.Add($"HTTP {(int)code}");
        if (!string.IsNullOrEmpty(errorCode))
            parts.Add(errorCode);
        return string.Join(" ", parts);
    }
}
