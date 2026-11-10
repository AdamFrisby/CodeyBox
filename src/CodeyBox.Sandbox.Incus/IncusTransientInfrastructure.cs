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
    // Daemon-restart connectivity losses. "Lost connection to the event
    // listener" covers both the provisioning variant ("...and failed to
    // reconnect: websocket: close 1006 ...") and the bare exec-path variant;
    // the websocket signature catches the same drop reported without the
    // prefix. "unix.socket: connect:" scopes dial failures ("connection
    // refused", "no such file or directory") to the local incus daemon
    // socket — that path+op pair only appears in the CLI's daemon endpoint.
    public const string DaemonConnectionLostSignature = "Lost connection to the event listener";
    public const string DaemonWebsocketCloseSignature = "websocket: close 1006";
    public const string DaemonSocketSignature = "unix.socket: connect:";

    public const string BeginTransactionFaultClass = "incus-db-transaction-deadline";
    public const string CliDeadlineFaultClass = "incus-cli-deadline";
    public const string TeardownUnmountFaultClass = "incus-teardown-unmount";
    public const string ExecCleanupFaultClass = "incus-exec-cleanup-unverified";
    public const string DaemonConnectionLostFaultClass = "incus-daemon-connection-lost";
    public const string DaemonUnreachableFaultClass = "incus-daemon-unreachable";

    private const int MaxScannedChars = 8 * 1024;
    private const int MaxExceptionChainDepth = 8;

    public static IReadOnlyList<string> DefaultSignatures { get; } =
    [
        BeginTransactionSignature,
        CliDeadlineSignature,
        UnmountingSignature,
        UnmountSignature,
        ControlFileCleanupSignature,
        DaemonConnectionLostSignature,
        DaemonWebsocketCloseSignature,
        DaemonSocketSignature,
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

    /// <summary>
    /// True when <paramref name="fault"/> is a lost-daemon-connection class —
    /// the only transient faults worth waiting out in-band (bounded, via
    /// <see cref="IncusSandboxOptions.DaemonReadyTimeout"/>) before retrying
    /// the failed CLI step. Other transient faults (deadline misses, teardown
    /// unmount races) keep their existing defer-and-recheck handling.
    /// </summary>
    public static bool IsDaemonConnectivityFault(IncusTransientInfrastructureFault? fault) =>
        fault is not null
        && (fault.FaultClass == DaemonConnectionLostFaultClass
            || fault.FaultClass == DaemonUnreachableFaultClass);

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
            DaemonConnectionLostSignature => new(DaemonConnectionLostFaultClass, matchedSignature, IsTeardown: false),
            DaemonWebsocketCloseSignature => new(DaemonConnectionLostFaultClass, matchedSignature, IsTeardown: false),
            DaemonSocketSignature => new(DaemonUnreachableFaultClass, matchedSignature, IsTeardown: false),
            _ => new("incus-transient-infrastructure", matchedSignature, IsTeardown: false),
        };
}
