namespace CodeyBox.GceSandboxPlugin;

/// <summary>
/// Classifies how a Compute Engine REST call failed so the provider can preserve the
/// unavailable/deferred/quota/error distinctions instead of collapsing them: quota and
/// rate-limit failures defer the work item, auth failures are configuration errors, and
/// only genuinely terminal states fail the operation.
/// </summary>
public enum GceFailureKind
{
    /// <summary>401 or a token-shaped 403: the workload identity is missing or rejected.</summary>
    Auth,
    /// <summary>403 that is not token-shaped, e.g. the identity lacks compute permission.</summary>
    Forbidden,
    /// <summary>404: the named project, zone, image, network, or instance does not exist.</summary>
    NotFound,
    /// <summary>409: the instance (or a sibling resource) already exists under another owner.</summary>
    Conflict,
    /// <summary>429 or a quota-exceeded 403/5xx: out of quota or request rate; defer and recheck.</summary>
    Quota,
    /// <summary>Other 5xx or transport failure with an unknown outcome: reconcile before resubmitting.</summary>
    Transient,
    /// <summary>Malformed, truncated, or oversized response the client refused to trust.</summary>
    Protocol,
    /// <summary>Anything else the client could classify.</summary>
    Unexpected,
}

/// <summary>
/// Typed failure from the Compute Engine REST client. Carries the operation name, the
/// HTTP status (when one was received), and the GCE error reason so callers can defer
/// on quota, fail closed on auth, and never claim success from an inconclusive status.
/// </summary>
public sealed class GceApiException : Exception
{
    public GceApiException(
        GceFailureKind kind,
        string operation,
        string detail,
        int? httpStatus = null,
        string? reason = null,
        Exception? inner = null)
        : base($"GCE {operation} failed ({kind}{(httpStatus is null ? "" : $" HTTP {httpStatus}")}): {detail}", inner)
    {
        Kind = kind;
        Operation = operation;
        Detail = detail;
        HttpStatus = httpStatus;
        Reason = reason;
    }

    public GceFailureKind Kind { get; }

    public string Operation { get; }

    public string Detail { get; }

    public int? HttpStatus { get; }

    public string? Reason { get; }
}
