namespace CodeyBox.Agents.Devin;

/// <summary>
/// Pure extraction of devin's terminal run error from the CLI's stderr (and,
/// defensively, stdout).
///
/// <para>Devin reports run failures as <c>Error: …</c> lines on stderr and
/// exits nonzero (verified against devin 3000.11.1: an unauthenticated
/// invocation exits 1 with <c>Error: Not logged in. Run `devin auth login`
/// …</c>; a print-mode run without
/// <c>--respect-workspace-trust false</c> in an untrusted directory fails the
/// same way).</para>
///
/// <para>Returns the first <c>Error:</c> line (stderr first, then stdout) so
/// the pipeline's no-changes branch can park quota/auth give-ups instead of
/// dead-lettering them as "produced no changes". Returns null when no terminal
/// error is present. Never throws. Output is capped at
/// <see cref="MaxDiagnosticChars"/> so the pipeline's audit/webhook sinks never
/// balloon on a verbose error body.</para>
/// </summary>
internal static class DevinTerminalDiagnoser
{
    internal const int MaxDiagnosticChars = 500;

    internal static string? TryExtractTerminalError(string? stderr, string? stdout)
        => TryExtractFromStream(stderr) ?? TryExtractFromStream(stdout);

    private static string? TryExtractFromStream(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            // Devin's failure signal is an `Error: …` line; plain-text model
            // output never carries this prefix.
            if (line.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                return Truncate(line);
        }

        return null;
    }

    private static string Truncate(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= MaxDiagnosticChars
            ? trimmed
            : trimmed[..MaxDiagnosticChars] + "…";
    }
}
