using System.Text.Json;

namespace CodeyBox.Agents.Omp;

/// <summary>
/// Pure extraction of omp's terminal run error from its <c>-p --mode
/// json</c> stdout plus the plaintext stderr channel.
///
/// <para>OMP exits non-zero on terminal run errors (verified against omp
/// 18.2.2: a paid model on a $0-spend-limit key exits 1 with
/// <c>stopReason:"error"</c> + <c>errorMessage:"403 Key limit exceeded
/// (total limit)…"</c> in the event stream; a missing key exits 1 with the
/// bun crash <c>No API key found for anthropic.</c> on stderr and only the
/// session header on stdout). The failure surfaces two ways:</para>
/// <list type="bullet">
/// <item><description>A structured assistant message with
/// <c>stopReason: "error"</c> and <c>errorMessage</c>, repeated across
/// <c>message_end</c> / <c>turn_end</c> — the first one wins. OMP's
/// <c>agent_end</c> additionally repeats the assistant message inside a
/// <c>messages</c> array (with <c>isTerminal: true</c>) rather than pi's
/// <c>message</c> envelope, so array elements are scanned too.</description></item>
/// <item><description>A plaintext pre-session line,
/// <c>No API key found for …</c>, emitted on stderr when omp cannot even
/// start the run (no JSON error event follows).</description></item>
/// </list>
///
/// <para>Returns null when no terminal error is present (a healthy run's
/// assistant frames carry <c>stopReason:"stop"</c> with usage and no
/// <c>errorMessage</c>). Never throws: malformed lines are skipped so a
/// half-written stream still yields whatever terminal signal it contains.
/// Output is capped at <see cref="MaxDiagnosticChars"/> so the pipeline's
/// audit/webhook sinks never balloon on a verbose provider error body.
/// Structure mirrors <c>PrimeTerminalDiagnoser</c> (same pi-family wire
/// shape, same both-streams scan) with the addition of the
/// <c>messages</c>-array envelope, which is omp-specific.</para>
/// </summary>
internal static class OmpTerminalDiagnoser
{
    internal const int MaxDiagnosticChars = 500;

    internal static string? TryExtractTerminalError(string? stdout, string? stderr)
    {
        if (TryExtractStructuredError(stdout) is { } structured)
            return structured;

        foreach (var text in new[] { stdout, stderr })
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;

            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                // Pre-session plaintext failure carries no JSON framing.
                // Omp reports it on stderr (unlike pi's stdout line).
                if (line.Contains("No API key found", StringComparison.OrdinalIgnoreCase))
                    return Truncate(line);
            }
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
        // turn_end repeat it under "message"). OMP's agent_end instead
        // carries the assistant message inside a "messages" array — scan
        // those elements too. The top-level turn frames have no stopReason
        // of their own.
        if (root.TryGetProperty("message", out var nested) && nested.ValueKind == JsonValueKind.Object)
            return MessageError(nested);

        if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in messages.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object
                    && MessageError(element) is { } error)
                {
                    return error;
                }
            }

            return null;
        }

        return MessageError(root);
    }

    private static string? MessageError(JsonElement message)
    {
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

        return Truncate("omp run ended with stopReason=error (no errorMessage)");
    }

    private static string Truncate(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= MaxDiagnosticChars
            ? trimmed
            : trimmed[..MaxDiagnosticChars] + "…";
    }
}
