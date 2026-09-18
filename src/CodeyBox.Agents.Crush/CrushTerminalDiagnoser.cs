namespace CodeyBox.Agents.Crush;

/// <summary>
/// Pure extraction of Crush's terminal run error from its <c>run</c> stdout
/// plus the styled-stderr channel.
///
/// <para>Crush renders terminal failures as styled <c>ERROR</c> blocks on
/// stderr with empty stdout and exit 1 (verified against
/// @charmland/crush 0.95.0: no key yields <c>No providers configured -
/// please run 'crush' to set up a provider interactively.</c>; an unknown
/// <c>-m</c> id yields <c>Failed to override models: large model "…"
/// not found.</c>; a paid model on a $0-spend-limit key yields
/// <c>Agent processing failed: failed to start agent processing stream:
/// forbidden: Key limit exceeded (total limit)…</c>; an unsupported flag
/// yields <c>Unknown flag: …</c>). Success output is the model's plain text
/// on stdout — which may legitimately be empty when the work landed in
/// files — so only lines carrying the anchored failure markers below are
/// lifted. Both streams are scanned: the markers are human rendering, not
/// a stream-guaranteed envelope, and a future build may move them.</para>
///
/// <para>Deliberately unmatched: the small-model title-generation advisory
/// (<c>Error generating title with small model; trying next</c>) — a
/// non-fatal warning on runs that still exit 0 with the reply intact, so
/// lifting it would mislabel success; and bare <c>ERROR</c> header lines
/// without a marker, which carry no cause.</para>
///
/// <para>Returns null when no terminal error is present. Never throws:
/// malformed input is skipped so a half-written capture still yields
/// whatever terminal signal it contains. Output is capped at
/// <see cref="MaxDiagnosticChars"/> so the pipeline's audit/webhook sinks
/// never balloon on a verbose provider error body.</para>
/// </summary>
internal static class CrushTerminalDiagnoser
{
    internal const int MaxDiagnosticChars = 500;

    /// <summary>
    /// Anchored failure markers (verified live — see the class doc). Matching
    /// is case-sensitive and substring-anchored to these provider-shaped
    /// sentences so model output discussing errors in reviewed repository
    /// content cannot produce a false terminal error.
    /// </summary>
    internal static readonly IReadOnlyList<string> FailureMarkers =
    [
        "No providers configured",
        "Failed to override models:",
        "Agent processing failed:",
        "Unknown flag:",
    ];

    internal static string? TryExtractTerminalError(string? stdout, string? stderr)
    {
        if (TryExtractMarkedLine(stdout) is { } stdoutError)
            return stdoutError;

        return TryExtractMarkedLine(stderr);
    }

    private static string? TryExtractMarkedLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        string? last = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            foreach (var marker in FailureMarkers)
            {
                if (line.Contains(marker, StringComparison.Ordinal))
                {
                    last = Truncate(line);
                    break;
                }
            }
        }

        return last;
    }

    private static string Truncate(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= MaxDiagnosticChars
            ? trimmed
            : trimmed[..MaxDiagnosticChars] + "…";
    }
}
