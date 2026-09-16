using System.Text.Json;

namespace CodeyBox.Agents.Autohand;

/// <summary>
/// Pure extraction of autohand's terminal run error from
/// <c>--output-format stream-json</c> stdout.
///
/// <para>Autohand both exits non-zero AND (on some paths) exits 0 with the
/// failure only in the event stream (verified against autohand-cli 0.9.7:
/// a bad OpenRouter key exits 1 with
/// <c>{"type":"error","message":"Authentication failed. Please verify your
/// OpenRouter API key in ~/.autohand/config.json.\nUser not found."}</c>,
/// while a cancelled/non-completing command exits 0 with
/// <c>{"type":"error","message":"Command did not complete
/// successfully."}</c>). The first <c>type: "error"</c> frame with a
/// non-blank string <c>message</c> wins.</para>
///
/// <para>Returns null when no terminal error is present (a healthy run's
/// frames are <c>tool_start</c>/<c>tool_end</c>/<c>file_modified</c> plus a
/// terminal <c>type: "result"</c> with <c>content</c>). Never throws:
/// malformed lines are skipped so a half-written stream still yields whatever
/// terminal signal it contains. Output is capped at
/// <see cref="MaxDiagnosticChars"/> so the pipeline's audit/webhook sinks
/// never balloon on a verbose provider error body.</para>
/// </summary>
internal static class AutohandTerminalDiagnoser
{
    internal const int MaxDiagnosticChars = 500;

    internal static string? TryExtractTerminalError(string? stdout)
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
                if (TryExtractErrorMessage(doc.RootElement) is { } error)
                    return error;
            }
            catch (JsonException)
            {
                // Half-written or interleaved chatter — keep scanning.
            }
        }

        return null;
    }

    private static string? TryExtractErrorMessage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (!root.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || !string.Equals(type.GetString(), "error", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!root.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(message.GetString()))
            return null;

        return Truncate(message.GetString()!);
    }

    private static string Truncate(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= MaxDiagnosticChars
            ? trimmed
            : trimmed[..MaxDiagnosticChars] + "…";
    }
}
