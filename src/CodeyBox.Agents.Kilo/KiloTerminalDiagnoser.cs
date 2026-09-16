using System.Text.Json;

namespace CodeyBox.Agents.Kilo;

/// <summary>
/// Pure extraction of kilo's terminal run error from
/// <c>run --auto --format json</c> stdout.
///
/// <para>Kilo exits 1 on model/auth failures with the cause in a
/// <c>type: "error"</c> event frame (verified against @kilocode/cli 7.7.2:
/// a missing key exits 1 with
/// <c>{"type":"error",…,"error":{"name":"APIError","data":{"message":"No
/// cookie auth credentials found","statusCode":401,…}}}</c>, and an
/// unseeded <c>models</c> map exits 1 with a generic
/// <c>Unexpected server error. Check server logs for details.</c> frame
/// followed by the specific <c>Model not found: …</c> frame). The
/// <c>message</c> is read from <c>error.data.message</c> (falling back to
/// <c>error.message</c> and a top-level <c>message</c> for forward
/// compatibility); the content-free generic server-error frame is skipped
/// in favour of the specific cause. The last surviving frame wins — it is
/// the terminal one.</para>
///
/// <para>Returns null when no terminal error is present (a healthy run's
/// frames are <c>step_start</c>/<c>text</c>/<c>step_finish</c>). Never
/// throws: malformed lines are skipped so a half-written stream still yields
/// whatever terminal signal it contains. Output is capped at
/// <see cref="MaxDiagnosticChars"/> so the pipeline's audit/webhook sinks
/// never balloon on a verbose provider error body.</para>
/// </summary>
internal static class KiloTerminalDiagnoser
{
    internal const int MaxDiagnosticChars = 500;

    /// <summary>
    /// Generic server-error frame carrying no cause (the sandbox has no
    /// server logs to check). Skipped so the specific companion frame wins.
    /// Matched exactly (case-insensitive) — a provider error that merely
    /// mentions server logs alongside a real cause is kept.
    /// </summary>
    internal const string GenericServerErrorMessage =
        "Unexpected server error. Check server logs for details.";

    internal static string? TryExtractTerminalError(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;

        string? lastGeneric = null;
        string? lastSpecific = null;

        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || !line.StartsWith('{'))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (TryExtractErrorMessage(doc.RootElement) is { } error)
                {
                    if (string.Equals(error, GenericServerErrorMessage, StringComparison.OrdinalIgnoreCase))
                        lastGeneric = error;
                    else
                        lastSpecific = error;
                }
            }
            catch (JsonException)
            {
                // Half-written or interleaved chatter — keep scanning.
            }
        }

        return lastSpecific ?? lastGeneric;
    }

    private static string? TryExtractErrorMessage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (!root.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || !string.Equals(type.GetString(), "error", StringComparison.OrdinalIgnoreCase))
            return null;

        // Live shape: error.data.message. Fall back to error.message, then a
        // top-level message, so a future CLI that flattens the envelope still
        // classifies instead of dead-lettering as "produced no changes".
        if (root.TryGetProperty("error", out var nested)
            && nested.ValueKind == JsonValueKind.Object)
        {
            if (nested.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("message", out var dataMessage)
                && dataMessage.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(dataMessage.GetString()))
            {
                return Truncate(dataMessage.GetString()!);
            }

            if (nested.TryGetProperty("message", out var nestedMessage)
                && nestedMessage.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(nestedMessage.GetString()))
            {
                return Truncate(nestedMessage.GetString()!);
            }
        }

        if (root.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(message.GetString()))
        {
            return Truncate(message.GetString()!);
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
