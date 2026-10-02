using CodeyBox.Core;

namespace CodeyBox.Sandbox.Incus;

/// <summary>
/// One classified transient Incus infrastructure fault. The fault is always
/// matched against an allowlisted exact signature (operator-configurable, see
/// <see cref="IncusSandboxOptions.TransientInfrastructureSignatures"/>) — never
/// by guessing across arbitrary CLI output.
/// </summary>
public sealed record IncusTransientInfrastructureFault(
    string FaultClass,
    string MatchedSignature,
    bool IsTeardown)
{
    public SandboxProvisioningDeferredException ToDeferral(
        string operation,
        string detail,
        TimeSpan recheckIn,
        Exception? innerException = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        return new SandboxProvisioningDeferredException(
            IncusSandboxProvider.ProviderId,
            operation,
            FaultClass,
            detail,
            recheckIn,
            innerException: innerException);
    }
}

/// <summary>
/// Typed transient classification for Incus control-plane faults that are
/// infrastructure contention, not work faults: incusd DB/storage deadline
/// misses, unmount/stop failures during teardown, and unverifiable guest
/// control-file cleanup after a completed exec.
/// Matching is an exact ordinal substring test against the allowlisted known
/// signatures only; an unrelated incus error never matches.
/// </summary>
public static class IncusTransientInfrastructure
{
    public const string BeginTransactionSignature = "Failed to begin transaction: context deadline exceeded";
    public const string CliDeadlineSignature = "context deadline exceeded";
    public const string UnmountingSignature = "Failed unmounting instance";
    public const string UnmountSignature = "Failed to unmount";
    public const string ControlFileCleanupSignature = "transient guest control-file cleanup could not be verified";

    public const string BeginTransactionFaultClass = "incus-db-transaction-deadline";
    public const string CliDeadlineFaultClass = "incus-cli-deadline";
    public const string TeardownUnmountFaultClass = "incus-teardown-unmount";
    public const string ExecCleanupFaultClass = "incus-exec-cleanup-unverified";

    private const int MaxScannedChars = 8 * 1024;
    private const int MaxExceptionChainDepth = 8;

    public static IReadOnlyList<string> DefaultSignatures { get; } =
    [
        BeginTransactionSignature,
        CliDeadlineSignature,
        UnmountingSignature,
        UnmountSignature,
        ControlFileCleanupSignature,
    ];

    /// <summary>
    /// Classifies <paramref name="exception"/> (walking at most a bounded
    /// inner-exception chain) against <paramref name="signatures"/>. Returns
    /// the fault on the first allowlisted signature found, most-specific
    /// signatures first regardless of configured order; null when nothing
    /// allowlisted matches. Never throws.
    /// </summary>
    public static IncusTransientInfrastructureFault? TryClassify(
        Exception? exception,
        IReadOnlyList<string>? signatures)
    {
        if (exception is null)
            return null;
        try
        {
            var current = exception;
            for (var depth = 0; depth < MaxExceptionChainDepth && current is not null; depth++)
            {
                var fault = TryClassifyMessage(current.Message, signatures);
                if (fault is not null)
                    return fault;
                current = current.InnerException;
            }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Classifies a single message against the allowlisted signatures.
    /// Never throws.
    /// </summary>
    public static IncusTransientInfrastructureFault? TryClassifyMessage(
        string? message,
        IReadOnlyList<string>? signatures)
    {
        try
        {
            if (string.IsNullOrEmpty(message))
                return null;
            var text = message.Length <= MaxScannedChars ? message : message.Substring(0, MaxScannedChars);
            foreach (var signature in OrderedSignatures(signatures))
            {
                if (text.Contains(signature, StringComparison.Ordinal))
                    return ToFault(signature);
            }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool IsTeardownFault(IncusTransientInfrastructureFault? fault) =>
        fault is not null && fault.IsTeardown;

    private static IEnumerable<string> OrderedSignatures(IReadOnlyList<string>? signatures)
    {
        if (signatures is null || signatures.Count == 0)
            return Array.Empty<string>();
        var allowlisted = new List<string>(signatures.Count);
        foreach (var candidate in signatures)
        {
            if (string.IsNullOrEmpty(candidate))
                continue;
            allowlisted.Add(candidate);
        }
        // Most-specific first so the generic CLI-deadline signature never
        // shadows the precise DB-transaction one, independent of the
        // operator's configured order.
        allowlisted.Sort(static (left, right) => right.Length.CompareTo(left.Length));
        return allowlisted;
    }

    private static IncusTransientInfrastructureFault ToFault(string matchedSignature) =>
        matchedSignature switch
        {
            BeginTransactionSignature => new(BeginTransactionFaultClass, matchedSignature, IsTeardown: false),
            CliDeadlineSignature => new(CliDeadlineFaultClass, matchedSignature, IsTeardown: false),
            UnmountingSignature => new(TeardownUnmountFaultClass, matchedSignature, IsTeardown: true),
            UnmountSignature => new(TeardownUnmountFaultClass, matchedSignature, IsTeardown: true),
            ControlFileCleanupSignature => new(ExecCleanupFaultClass, matchedSignature, IsTeardown: false),
            _ => new("incus-transient-infrastructure", matchedSignature, IsTeardown: false),
        };
}
