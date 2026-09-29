using System.Text.RegularExpressions;

namespace CodeyBox.Core;

/// <summary>
/// Single source of truth for recognizing a derived "build outputs missing"
/// test-runner refusal: a <c>--no-build</c> invocation that fails because the
/// expected assemblies were never produced (VSTest reports the absent
/// assembly as <c>The argument ... is invalid</c> for a <c>bin/</c> path, or
/// <c>The test source file ... was not found</c>).
/// Such a refusal is a consequence of the failed build gate, never a terminal
/// configuration fault in the test command itself.
/// </summary>
public static class MissingBuildOutputs
{
    private static readonly Regex BinAssemblyInvalidRegex = new(
        @"The argument\s+\S*bin[\\/]\S*\.dll\S*\s+is invalid\.|The test source file\s+\S*bin[\\/]\S*\s+was not found\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// True when <paramref name="argv"/> reuses prior build outputs (a
    /// standalone <c>--no-build</c> token) and <paramref name="output"/>
    /// shows VSTest refusing a <c>bin/</c> test assembly.
    /// </summary>
    public static bool IsMissingBuildOutputs(IReadOnlyList<string>? argv, string? output)
    {
        if (argv is null || string.IsNullOrEmpty(output))
            return false;
        if (!HasNoBuildToken(argv))
            return false;
        return BinAssemblyInvalidRegex.IsMatch(output);
    }

    /// <summary>
    /// Message-based fallback for call sites that no longer have the argv
    /// (e.g. the pipeline's <c>AuditUnavailableException</c> catch, where the
    /// command is embedded in the exception message as
    /// <c>(command: dotnet test --no-build)</c>). True when the combined
    /// message/output text carries both a <c>--no-build</c> token and a
    /// <c>bin/</c> assembly refusal.
    /// </summary>
    public static bool IsMissingBuildOutputsMessage(string? message, string? output)
    {
        var combined = string.Concat(message ?? string.Empty, "\n", output ?? string.Empty);
        if (combined.Length <= 1)
            return false;
        if (!BinAssemblyInvalidRegex.IsMatch(combined))
            return false;
        return combined.Contains("--no-build", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasNoBuildToken(IReadOnlyList<string> argv)
        => argv.Any(static a => string.Equals(a, "--no-build", StringComparison.OrdinalIgnoreCase));
}
