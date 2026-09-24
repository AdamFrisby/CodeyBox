using System.Text.Json;

namespace CodeyBox.Agents.Devin;

/// <summary>
/// Pure extraction of the shim's terminal outcome from a captured
/// <c>devin.acp</c> NDJSON stream. The shim emits exactly one terminal
/// envelope per run — <c>turn_complete</c> (session/prompt answered with a
/// stopReason), <c>turn_error</c> (the agent returned a JSON-RPC error for
/// the prompt), or <c>fatal</c> (spawn/handshake/protocol failure) — so the
/// last terminal envelope found is authoritative.
///
/// <para>Used to lift a typed failure into
/// <see cref="AgentResult.TerminalDiagnostic"/> (and to confirm a run that
/// exited 0 actually reported a turn outcome) instead of flattening every
/// ACP upset to "agent exited N". Never throws; malformed lines are skipped.
/// Output is capped at <see cref="MaxDiagnosticChars"/> so a verbose error
/// body cannot balloon the pipeline's audit/webhook sinks.</para>
/// </summary>
internal static class DevinAcpOutcome
{
    internal const int MaxDiagnosticChars = 500;
    internal const string EnvelopeType = "devin.acp";

    internal enum TerminalEvent
    {
        None,
        TurnComplete,
        TurnError,
        Fatal,
    }

    internal sealed record Outcome(TerminalEvent Event, string? Diagnostic);

    /// <summary>
    /// Returns the terminal outcome the shim reported, or
    /// <see cref="TerminalEvent.None"/> when the stream carries no terminal
    /// envelope (e.g. the shim was killed mid-turn, or stdout was truncated
    /// by an output cap before the terminal line).
    /// </summary>
    internal static Outcome Extract(string? stdout)
    {
        TerminalEvent lastEvent = TerminalEvent.None;
        string? lastDiagnostic = null;

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            foreach (var rawLine in stdout.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] != '{')
                    continue;

                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }
                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object
                        || !root.TryGetProperty("type", out var typeEl)
                        || typeEl.ValueKind != JsonValueKind.String
                        || typeEl.GetString() != EnvelopeType
                        || !root.TryGetProperty("event", out var eventEl)
                        || eventEl.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    switch (eventEl.GetString())
                    {
                        case "turn_complete":
                            lastEvent = TerminalEvent.TurnComplete;
                            lastDiagnostic = null;
                            break;
                        case "turn_error":
                            lastEvent = TerminalEvent.TurnError;
                            lastDiagnostic = Describe("turn error", root);
                            break;
                        case "fatal":
                            lastEvent = TerminalEvent.Fatal;
                            lastDiagnostic = Describe("fatal", root);
                            break;
                    }
                }
            }
        }

        return new Outcome(lastEvent, lastDiagnostic);
    }

    private static string Describe(string prefix, JsonElement root)
    {
        var message = root.TryGetProperty("message", out var messageEl)
            && messageEl.ValueKind == JsonValueKind.String
            ? messageEl.GetString()
            : null;
        var stage = root.TryGetProperty("stage", out var stageEl)
            && stageEl.ValueKind == JsonValueKind.String
            ? stageEl.GetString()
            : null;

        var text = stage is null
            ? $"devin acp {prefix}: {message ?? "unknown"}"
            : $"devin acp {prefix} during {stage}: {message ?? "unknown"}";
        return text.Length <= MaxDiagnosticChars
            ? text
            : text[..MaxDiagnosticChars] + "…";
    }
}
