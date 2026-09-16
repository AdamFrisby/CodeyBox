using System.Text.Json;

namespace CodeyBox.Agents.Prime;

/// <summary>
/// Pure extraction of prime-agent's terminal run error from its
/// <c>-p --mode json</c> stdout plus the plaintext stderr channel.
///
/// <para>Prime exits 0 even when the run dies before producing output
/// (verified against prime-agent 0.9.5: a bad key exits 0 with
/// <c>stopReason:"error"</c> + <c>errorMessage:"401 User not found…"</c> in
/// the event stream; a missing key exits 0 with the plaintext
/// <c>No API key found for the selected model.</c> on stderr). The failure
/// surfaces two ways:</para>
/// <list type="bullet">
/// <item><description>A structured assistant message with
/// <c>stopReason: "error"</c> and <c>errorMessage</c> (e.g. the 401 / 403
/// OpenRouter refusals), repeated across <c>message_end</c> /
/// <c>turn_end</c> / <c>agent_end</c> — the first one wins.</description></item>
/// <item><description>A plaintext pre-session line,
/// <c>No API key found for the selected model.</c>, emitted on stderr when
/// prime cannot even start the run (no JSON error event follows).</description></item>
/// </list>
///
/// <para>Returns null when no terminal error is present (a healthy run's
/// assistant frames carry <c>stopReason:"stop"</c> with usage and no
/// <c>errorMessage</c>). Never throws: malformed lines are skipped so a
/// half-written stream still yields whatever terminal signal it contains.
/// Output is capped at <see cref="MaxDiagnosticChars"/> so the pipeline's
/// audit/webhook sinks never balloon on a verbose provider error body.
/// Structure mirrors <c>PiTerminalDiagnoser</c> (same wire shape) with the
/// addition of the stderr channel, where prime — unlike pi — reports its
/// pre-session failure.</para>
/// </summary>
internal static class PrimeTerminalDiagnoser
{
    internal const int MaxDiagnosticChars = 500;

    internal static string? TryExtractTerminalError(string? stdout, string? stderr)
    {
        if (TryExtractStructuredError(stdout) is { } structured)
            return structured;

        if (string.IsNullOrWhiteSpace(stderr))
            return null;

        foreach (var rawLine in stderr.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            // Pre-session plaintext failure carries no JSON framing.
            if (line.Contains("No API key found", StringComparison.OrdinalIgnoreCase))
                return Truncate(line);
        }

        return null;
    }

    private static string? TryExtractStructuredError(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;

        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || !line.StartsWith('{'))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (TryExtractMessageError(doc.RootElement) is { } error)
                    return error;
            }
            catch (JsonException)
            {
                // Half-written or interleaved chatter — keep scanning.
            }
        }

        return null;
    }

    private static string? TryExtractMessageError(JsonElement root)
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

        return Truncate("prime-agent run ended with stopReason=error (no errorMessage)");
    }

    private static string Truncate(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= MaxDiagnosticChars
            ? trimmed
            : trimmed[..MaxDiagnosticChars] + "…";
    }
}
