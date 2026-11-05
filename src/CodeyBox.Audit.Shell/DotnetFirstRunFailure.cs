using System.Text.RegularExpressions;
using CodeyBox.Core;

namespace CodeyBox.Audit.Shell;

/// <summary>
/// Recognizes .NET CLI first-run infrastructure failures in a failed
/// <c>dotnet</c> command's combined output and routes them to
/// <see cref="AuditUnavailableException"/> so they surface as infrastructure —
/// retried without recording a finding against the diff — instead of the
/// generic blocking "command exited 1" result.
///
/// <para>Two signatures are recognized, both proving the failure happened in
/// dotnet first-use (<c>DotnetFirstTimeUseConfigurer</c>) before MSBuild ever
/// evaluated the tree:</para>
/// <list type="number">
/// <item>The NuGet first-use named-mutex crash —
/// <c>System.IO.IOException: The system cannot open the device or file
/// specified. : 'NuGet-Migrations'</c> (and the PAL errno variants from
/// dotnet/runtime#135726, #136802, #136236) raised out of
/// <c>Mutex..ctor</c> → <c>MigrationRunner.Run</c> →
/// <c>DotnetFirstTimeUseConfigurer.Configure</c>.</item>
/// <item>The SDK first-run banner (<c>Welcome to .NET …</c>) followed by a
/// non-zero exit with no compiler/analyzer/MSBuild diagnostic — first-use ran
/// during this audit and the toolchain failed before producing a verdict.
/// The banner never prints on a warmed CLI home, so its presence is itself
/// the first-use evidence.</item>
/// </list>
///
/// <para>Either signature is only honored when NO genuine code-failure signal
/// is present: any <c>error XXXX:</c> diagnostic (compiler, analyzer,
/// MSBuild, NuGet), a bare <c>MSBnnnn:</c> line, a <c>Build FAILED.</c>
/// summary, or a restore failure means the command DID evaluate the tree and
/// its verdict stands — a co-occurring first-run signature must never mask
/// it.</para>
/// </summary>
internal static class DotnetFirstRunFailure
{
    // The mutex name is constant across SDK versions and every PAL failure
    // variant; the stack-frame markers distinguish the first-use crash from
    // incidental output that merely mentions the name.
    private static readonly Regex NuGetMigrationsMutexRegex = new(
        @"NuGet-Migrations",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FirstRunStackRegex = new(
        @"\bMutex\b|MigrationRunner|DotnetFirstTimeUse",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // The .NET SDK first-run banner header — version-agnostic so SDK upgrades
    // cannot silently drop the signature.
    private static readonly Regex FirstRunBannerRegex = new(
        @"^\s*Welcome to \.NET\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    // Genuine toolchain verdicts about the code: any `error CODE:` diagnostic
    // line — compiler (CS), analyzer (IDE/CA/WHITESPACE/…), MSBuild (MSB),
    // NuGet (NU) — a bare MSBuild fatal (`MSB1009:` prints without an
    // "error" prefix), a failed-build summary, or a restore failure. Their
    // presence means the command evaluated the tree, so a co-occurring
    // first-run signature must not mask the real verdict.
    private static readonly Regex CodeFailureSignalRegex = new(
        @"error\s+[A-Za-z][A-Za-z0-9_]*\d*\s*:|^\s*MSB\d{4}\s*:|^\s*Build FAILED\.|Restore failed",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>
    /// Throws <see cref="AuditUnavailableException"/> when
    /// <paramref name="context"/> shows a dotnet first-run infrastructure
    /// failure. Returns normally for non-dotnet argv, missing output, no
    /// first-run signature, or output carrying a genuine code-failure signal.
    /// The exception is deliberately non-deterministic: the same command can
    /// succeed once the runtime dirs and migration state are warmed, so the
    /// pipeline keeps it on the transient infrastructure path.
    /// </summary>
    /// <exception cref="AuditUnavailableException">
    /// Thrown when the output proves dotnet first-use failed before the
    /// command could evaluate the code.
    /// </exception>
    internal static void ThrowIfInfrastructureFailure(AuditResultClassificationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!DotnetCliHomeConventions.IsDotnetInvocation(context.Argv))
            return;
        var output = context.CombinedOutput;
        if (string.IsNullOrEmpty(output))
            return;

        var signal = FirstRunSignal(output);
        if (signal is null || CodeFailureSignalRegex.IsMatch(output))
            return;

        throw new AuditUnavailableException(
            $"could-not-verify: dotnet first-run infrastructure failure for '{context.AuditorName}' " +
            $"(exit {context.Result.ExitCode}): {signal} " +
            $"(command: {string.Join(' ', context.ExecutedArgv)})",
            context.Result.ExitCode,
            output);
    }

    private static string? FirstRunSignal(string output)
    {
        if (NuGetMigrationsMutexRegex.IsMatch(output)
            && FirstRunStackRegex.IsMatch(output))
        {
            return FirstMatchingLine(output, NuGetMigrationsMutexRegex);
        }

        if (FirstRunBannerRegex.IsMatch(output))
        {
            return FirstMatchingLine(output, FirstRunBannerRegex);
        }

        return null;
    }

    private static string FirstMatchingLine(string output, Regex regex)
    {
        foreach (var match in regex.EnumerateMatches(output.AsSpan()))
        {
            var start = match.Index;
            while (start > 0 && output[start - 1] is not ('\r' or '\n'))
                start--;
            var end = match.Index + match.Length;
            while (end < output.Length && output[end] is not ('\r' or '\n'))
                end++;
            var line = output[start..end].Trim();
            if (line.Length == 0)
                continue;
            return line.Length <= 240 ? line : line[..240] + "...";
        }

        return "(dotnet first-run failure)";
    }
}
