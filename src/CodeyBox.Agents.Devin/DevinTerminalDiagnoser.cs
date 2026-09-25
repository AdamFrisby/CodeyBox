using System.Text.Json;
using CodeyBox.Agents;

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
/// <para>On the ACP dispatch path the shim folds its whole stderr surface
/// into <c>codeybox.stderr</c> envelopes on stdout (see
/// <c>Resources/devin-acp-client.py</c>), so the <c>Error:</c> line arrives
/// as envelope <c>text</c>, not a raw line — both shapes are checked.
/// Returns the first <c>Error:</c> line (stderr first, then stdout) so
/// the pipeline's no-changes branch can park quota/auth give-ups instead of
/// dead-lettering them as "produced no changes". Returns null when no terminal
/// error is present. Never throws. Output is capped at
/// <see cref="MaxDiagnosticChars"/> so the pipeline's audit/webhook sinks never
/// balloon on a verbose error body.</para>
/// </summary>
internal static class DevinTerminalDiagnoser
{
    /// <summary>
    /// The cap applied to every diagnostic string lifted into
    /// <c>AgentResult.TerminalDiagnostic</c> — shared by
    /// <see cref="DevinAcpOutcome"/> so both extractors bound the same sink
    /// identically.
    /// </summary>
    internal const int MaxDiagnosticChars = 500;

    internal static string? TryExtractTerminalError(string? stderr, string? stdout)
        => TryExtractFromStream(stderr) ?? TryExtractFromStream(stdout);

    /// <summary>
    /// Trims and caps a diagnostic string at <see cref="MaxDiagnosticChars"/>,
    /// appending an ellipsis when truncated.
    /// </summary>
    internal static string TruncateDiagnostic(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= MaxDiagnosticChars
            ? trimmed
            : trimmed[..MaxDiagnosticChars] + "…";
    }

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
            // output never carries this prefix. ACP dispatches deliver the
            // line inside a codeybox.stderr envelope, so unwrap those first
            // (the inner text is trimmed to match the raw-line semantics).
            var candidate = TryUnwrapStderrEnvelope(line)?.TrimStart() ?? line;
            if (candidate.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                return TruncateDiagnostic(candidate);
        }

        return null;
    }

    /// <summary>
    /// If <paramref name="line"/> is a <c>codeybox.stderr</c> envelope,
    /// returns its <c>text</c> payload; otherwise null. Malformed JSON is
    /// treated as a plain line, never an error. The envelope shape is read
    /// through <see cref="CliAgentRunnerBase.TryReadStderrEnvelope"/>, the
    /// shared reader for the contract's write side.
    /// </summary>
    private static string? TryUnwrapStderrEnvelope(string line)
    {
        if (line[0] != '{')
            return null;

        try
        {
            using var doc = JsonDocument.Parse(line);
            return CliAgentRunnerBase.TryReadStderrEnvelope(doc.RootElement, out var text)
                ? text
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
