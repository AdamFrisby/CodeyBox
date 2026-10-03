using System.Net.Http;
using System.Net.Sockets;
using CodeyBox.Core;

namespace CodeyBox.TartSandboxPlugin;

/// <summary>One failed Tart CLI (or guest SSH) invocation.</summary>
public sealed class TartCliException : Exception
{
    public TartCliException(string executable, IReadOnlyList<string> argv, int? exitCode, string errorClass, string detail)
        : base($"tart cli failure: executable={executable} exit={(exitCode.HasValue ? exitCode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "transport")} errorClass={errorClass} detail={detail}")
    {
        Executable = executable;
        Argv = argv;
        ExitCode = exitCode;
        ErrorClass = errorClass;
        Detail = detail;
    }

    public TartCliException(string executable, IReadOnlyList<string> argv, int? exitCode, string errorClass, string detail, Exception inner)
        : base($"tart cli failure: executable={executable} exit={(exitCode.HasValue ? exitCode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "transport")} errorClass={errorClass} detail={detail}", inner)
    {
        Executable = executable;
        Argv = argv;
        ExitCode = exitCode;
        ErrorClass = errorClass;
        Detail = detail;
    }

    public string Executable { get; }

    public IReadOnlyList<string> Argv { get; }

    public int? ExitCode { get; }

    public string ErrorClass { get; }

    public string Detail { get; }
}

/// <summary>
/// Pure classification of Tart host-side failures. Every failure the Tart CLI
/// or the guest SSH transport reports — missing binary, unreachable guest,
/// unauthorised credentials, rejected requests, timeouts, server-side errors —
/// is infrastructure: it says the guest could not be provided or observed,
/// never that the work item's diff failed. Callers convert the returned
/// <see cref="TartInfrastructureFailure"/> into the pipeline's
/// deferral/unavailable exceptions; a non-zero guest exit code is NOT a
/// CLI failure and flows back as an ordinary exec result.
/// </summary>
public static class TartFailureClassification
{
    /// <summary>
    /// Builds a CLI exception whose error class is the shared taxonomy when
    /// the process exited (classified from its exit code and stderr), or the
    /// caller's explicit transport class when it never ran.
    /// </summary>
    public static TartCliException ForExit(
        string executable,
        IReadOnlyList<string> argv,
        int exitCode,
        string stderrExcerpt,
        Exception? inner = null)
    {
        var detail = LastLine(stderrExcerpt);
        var errorClass = ClassifyCore(exitCode, stderrExcerpt);
        return inner is null
            ? new TartCliException(executable, argv, exitCode, errorClass, detail)
            : new TartCliException(executable, argv, exitCode, errorClass, detail, inner);
    }

    /// <summary>Taxonomy of infrastructure classes a deferred exception may carry.</summary>
    public static bool IsInfrastructureClass(string errorClass) =>
        errorClass is "unreachable" or "unauthorised" or "throttled" or "conflict"
            or "not-found" or "timeout" or "quota-exceeded" or "request-rejected"
            or "unsupported-host" or "server-error";

    /// <summary>Classifies a CLI exit (or transport failure when null) as infrastructure.</summary>
    public static TartInfrastructureFailure Classify(int? exitCode, string operation, string stderrExcerpt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        stderrExcerpt ??= string.Empty;

        var errorClass = ClassifyCore(exitCode, stderrExcerpt);
        var recheckIn = errorClass switch
        {
            "throttled" => TimeSpan.FromMinutes(2),
            "unreachable" or "timeout" or "server-error" => TimeSpan.FromSeconds(30),
            "conflict" => TimeSpan.FromSeconds(30),
            "unauthorised" => TimeSpan.FromMinutes(5),
            "unsupported-host" => TimeSpan.FromMinutes(5),
            _ => TimeSpan.FromSeconds(30),
        };

        return new TartInfrastructureFailure(operation, errorClass, recheckIn);
    }

    internal static string ClassifyCore(int? exitCode, string stderrExcerpt)
    {
        if (exitCode is null)
            return "unreachable";

        if (ContainsAny(stderrExcerpt, "permission denied", "authentication failed", "unauthorized", "unauthorised", "incorrect password", "no authentication"))
            return "unauthorised";
        if (ContainsAny(stderrExcerpt, "rate limit", "too many requests", "429"))
            return "throttled";
        if (ContainsAny(stderrExcerpt, "already exists", "already in use", "conflict"))
            return "conflict";
        if (ContainsAny(stderrExcerpt, "not found", "no such", "does not exist", "unknown vm", "unknown virtual machine"))
            return "not-found";
        if (ContainsAny(stderrExcerpt, "timed out", "timeout", "connection refused", "no route to host", "host is down"))
            return "timeout";
        if (ContainsAny(stderrExcerpt, "no space left", "disk quota", "out of memory", "cannot allocate"))
            return "quota-exceeded";

        return exitCode.Value switch
        {
            127 => "unreachable",
            255 => "unreachable",
            _ when exitCode.Value >= 128 => "unreachable",
            _ when exitCode.Value != 0 => "request-rejected",
            _ => "request-rejected",
        };
    }

    /// <summary>True when the exception already proves infrastructure (never a diff verdict).</summary>
    public static bool IsInfrastructure(Exception ex) =>
        ex is TartCliException
            or SocketException
            or HttpRequestException
            or IOException
            or TimeoutException
            or TaskCanceledException
            or OperationCanceledException;

    /// <summary>
    /// Converts a CLI failure into the pipeline's provisioning-deferred
    /// exception. The taxonomy class the exception already carries wins;
    /// otherwise the exit and stderr are classified. The wait is the longer
    /// of the provider backoff and the classification's suggestion, so a
    /// throttled host is not hot-looped.
    /// </summary>
    public static SandboxProvisioningDeferredException ToDeferred(TartCliException ex, string operation, TimeSpan recheckIn)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var classified = Classify(ex.ExitCode, operation, ex.Detail);
        var errorClass = IsInfrastructureClass(ex.ErrorClass) ? ex.ErrorClass : classified.ErrorClass;
        var wait = TimeSpan.FromTicks(Math.Max(
            recheckIn <= TimeSpan.Zero ? 0 : recheckIn.Ticks,
            classified.RecheckIn.Ticks));
        return new SandboxProvisioningDeferredException(
            TartSandboxOptions.ProviderKind,
            operation,
            errorClass,
            ex.Detail,
            wait,
            innerException: ex);
    }

    /// <summary>Converts a CLI failure during exec/file observation into execution-unavailable.</summary>
    public static SandboxExecutionUnavailableException ToUnavailable(TartCliException ex) =>
        new(ex.ExitCode ?? -1);

    private static string LastLine(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? string.Empty;
    }

    private static bool ContainsAny(string haystack, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

/// <summary>Pure record describing one infrastructure failure and its backoff.</summary>
public sealed record TartInfrastructureFailure(string Operation, string ErrorClass, TimeSpan RecheckIn);
