using CodeyBox.Core;

namespace CodeyBox.Audit.Shell;

/// <summary>
/// Detects sandbox exec-transport failures: commands that never produced a
/// verdict because the channel carrying them dropped (e.g. an Incus exec
/// websocket closing abnormally mid-command). Such an outcome must be treated
/// as infrastructure failure — retried, then surfaced as
/// <see cref="AuditUnavailableException"/> — never as a code finding against
/// the diff under review.
/// </summary>
public static class ExecTransportFailure
{
    /// <summary>
    /// The exit code a dropped exec transport surfaces with. A program may
    /// legitimately exit with this code, so it is only meaningful together
    /// with <see cref="HasTransportDiagnostic"/> — never on its own.
    /// </summary>
    public const int TransportFailureExitCode = 255;

    /// <summary>
    /// True when <paramref name="result"/> carries a transport-drop
    /// diagnostic: the exec exit code is 255 <em>and</em> the combined output
    /// contains an abnormal-closure marker. Both conditions are required — 255
    /// is a legal program exit code, and transport-shaped text can also appear
    /// in ordinary program output.
    /// </summary>
    public static bool IsTransportFailure(SandboxExecResult result)
        => result.ExitCode == TransportFailureExitCode
           && HasTransportDiagnostic(CombinedOutput(result));

    /// <summary>
    /// True when <paramref name="output"/> contains an exec-channel
    /// abnormal-closure marker (case-insensitive): the <c>incus exec</c>
    /// websocket diagnostic reads e.g.
    /// <c>Error: websocket: close 1006 (abnormal closure): unexpected EOF</c>.
    /// </summary>
    public static bool HasTransportDiagnostic(string? output)
    {
        if (string.IsNullOrEmpty(output))
            return false;

        return output.Contains("websocket: close", StringComparison.OrdinalIgnoreCase)
            || output.Contains("abnormal closure", StringComparison.OrdinalIgnoreCase)
            || output.Contains("unexpected eof", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The first output line carrying a transport diagnostic, single-lined and
    /// bounded for exception messages. Empty when no diagnostic is present.
    /// </summary>
    public static string FirstDiagnosticLine(string? output, int maxLength = 240)
    {
        if (string.IsNullOrEmpty(output))
            return string.Empty;

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;
            if (HasTransportDiagnostic(trimmed))
                return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength] + "...";
        }

        return string.Empty;
    }

    private static string CombinedOutput(SandboxExecResult result)
        => string.IsNullOrWhiteSpace(result.Stderr)
            ? result.Stdout
            : string.IsNullOrWhiteSpace(result.Stdout)
                ? result.Stderr
                : result.Stdout + "\n" + result.Stderr;
}
