namespace CodeyBox.Agents.Vibe;

/// <summary>
/// Pure extraction of vibe's terminal run error from the CLI's stderr (and,
/// defensively, stdout).
///
/// <para>Vibe reports run failures as <c>Error: …</c> lines on stderr and
/// exits nonzero (verified against vibe 2.25.4):</para>
/// <list type="bullet">
/// <item><description>A missing provider key exits 1 with
/// <c>Error: Missing OPENROUTER_API_KEY environment variable for openrouter
/// provider. Set the environment variable (e.g. in ~/.vibe/.env or your
/// shell), or run `vibe --setup` once interactively.</c> — no history frames
/// follow.</description></item>
/// <item><description>A rejected provider call exits 1 with a multi-line
/// <c>Error: API error from openrouter (model: …): LLM backend error …</c>
/// body (status, provider_message, body_excerpt) — stdout carries only the
/// user-echo history entry.</description></item>
/// </list>
///
/// <para>Returns the first <c>Error:</c> line (stderr first, then stdout) so
/// the pipeline's no-changes branch can park quota/auth give-ups instead of
/// dead-lettering them as "produced no changes". Returns null when no
/// terminal error is present (a healthy run prints only history entries).
/// Never throws. Output is capped at <see cref="MaxDiagnosticChars"/> so the
/// pipeline's audit/webhook sinks never balloon on a verbose provider error
/// body.</para>
/// </summary>
internal static class VibeTerminalDiagnoser
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

            // Vibe's failure signal is an `Error: …` line. History-entry JSON
            // lines (user echo, assistant text) never carry this prefix, so
            // scanning for it cannot misattribute model output.
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
