using System.Text.Json;

namespace CodeyBox.Agents.Goose;

/// <summary>
/// Pure extraction of goose's terminal run error from
/// <c>--output-format stream-json</c> stdout.
///
/// <para>Goose exits 0 even when the provider call fails (verified against
/// goose 1.50.1: an OpenRouter 401 exits 0 with the cause only in the event
/// stream). The failure surfaces two ways:</para>
/// <list type="bullet">
/// <item><description>A structured assistant message whose
/// <c>content</c> array holds a <c>{type: "error", kind: …,
/// message: …}</c> block (e.g. kind <c>authentication</c> with
/// <c>Authentication failed for https://openrouter.ai/… Status: 401
/// Unauthorized…</c>). The first one wins.</description></item>
/// <item><description>A plaintext pre-session line,
/// <c>error: Error Configuration value not found: OPENROUTER_API_KEY.</c>,
/// emitted when goose cannot even start the run (exit 1, no JSON frames
/// follow).</description></item>
/// </list>
///
/// <para>Returns null when no terminal error is present (a healthy run's
/// chunks carry <c>thinking</c>/<c>text</c> content and the terminal
/// <c>complete</c> frame carries no error). Never throws: malformed lines
/// are skipped so a half-written stream still yields whatever terminal
/// signal it contains. Output is capped at <see cref="MaxDiagnosticChars"/>
/// so the pipeline's audit/webhook sinks never balloon on a verbose
/// provider error body.</para>
/// </summary>
internal static class GooseTerminalDiagnoser
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
            if (line.Contains("Configuration value not found", StringComparison.OrdinalIgnoreCase))
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
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return null;
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            if (!item.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || !string.Equals(type.GetString(), "error", StringComparison.OrdinalIgnoreCase))
                continue;

            if (item.TryGetProperty("message", out var errorMessage)
                && errorMessage.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(errorMessage.GetString()))
            {
                return Truncate(errorMessage.GetString()!);
            }

            var kind = item.TryGetProperty("kind", out var kindProp) && kindProp.ValueKind == JsonValueKind.String
                ? kindProp.GetString()
                : null;
            return Truncate(string.IsNullOrWhiteSpace(kind)
                ? "goose run ended with an error content block (no message)"
                : $"goose run ended with error kind={kind} (no message)");
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
