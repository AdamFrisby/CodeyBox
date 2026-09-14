using System.Text.Json;

namespace CodeyBox.Agents.Pi;

/// <summary>
/// Pure extraction of pi's terminal run error from <c>--mode json</c> stdout.
///
/// <para>Pi exits 0 even when the run dies before producing output (verified
/// against pi 0.85.1: a missing API key and a provider 401 both exit 0 with
/// the cause only in the event stream). The failure surfaces two ways:</para>
/// <list type="bullet">
/// <item><description>A structured assistant message with
/// <c>stopReason: "error"</c> and <c>errorMessage</c> (e.g.
/// <c>401 {"type":"error","error":{"type":"authentication_error",…}}</c>),
/// repeated across <c>message_end</c> / <c>turn_end</c> / <c>agent_end</c> —
/// the first one wins.</description></item>
/// <item><description>A plaintext pre-session line,
/// <c>No API key found for the selected model.</c>, emitted when pi cannot
/// even start the run (no JSON error event follows).</description></item>
/// </list>
///
/// <para>Returns null when no terminal error is present (a healthy run's
/// <c>agent_end</c> carries <c>"willRetry":false</c> with no
/// <c>stopReason</c>). Never throws: malformed lines are skipped so a
/// half-written stream still yields whatever terminal signal it contains.
/// Output is capped at <see cref="MaxDiagnosticChars"/> so the pipeline's
/// audit/webhook sinks never balloon on a verbose provider error body.</para>
/// </summary>
internal static class PiTerminalDiagnoser
{
    internal const int MaxDiagnosticChars = 500;

    internal static string? TryExtractTerminalError(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;

        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            // Pre-session plaintext failure carries no JSON framing.
            if (line.Contains("No API key found", StringComparison.OrdinalIgnoreCase))
                return Truncate(line);

            if (!line.StartsWith('{'))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (TryExtractStructuredError(doc.RootElement) is { } error)
                    return error;
            }
            catch (JsonException)
            {
                // Half-written or interleaved chatter — keep scanning.
            }
        }

        return null;
    }

    private static string? TryExtractStructuredError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        // The error rides on the nested assistant message (message_end /
        // turn_end / agent_end all repeat it); the top-level turn/agent frames
        // have no stopReason of their own.
        var message = root;
        if (root.TryGetProperty("message", out var nested) && nested.ValueKind == JsonValueKind.Object)
            message = nested;

        if (!message.TryGetProperty("stopReason", out var stopReason)
            || stopReason.ValueKind != JsonValueKind.String
            || !string.Equals(stopReason.GetString(), "error", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (message.TryGetProperty("errorMessage", out var errorMessage)
            && errorMessage.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(errorMessage.GetString()))
        {
            return Truncate(errorMessage.GetString()!);
        }

        return Truncate("pi run ended with stopReason=error (no errorMessage)");
    }

    private static string Truncate(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= MaxDiagnosticChars
            ? trimmed
            : trimmed[..MaxDiagnosticChars] + "…";
    }
}
