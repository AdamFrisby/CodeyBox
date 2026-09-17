using System.Text.Json;

namespace CodeyBox.Agents.Qwen;

/// <summary>
/// Pure extraction of qwen's terminal run error from its
/// <c>--output-format stream-json</c> stdout plus the stderr channel.
///
/// <para>Qwen exits non-zero on terminal run errors (verified against qwen
/// 0.24.0: a bogus key exits 1 with
/// <c>result/subtype:"error_during_execution"</c> + <c>is_error:true</c> and
/// <c>error:{"message":"[API Error: 401 Missing Authentication
/// header]"}</c> on stdout, plus an <c>{"error":{"type":
/// "AlreadyReportedError", "message": …}}</c> object on stderr; a paid
/// model on a $0-spend-limit key exits 1 with <c>[API Error: 403 Key limit
/// exceeded (total limit)…]</c> in the same two places). The failure
/// surfaces three ways, checked in order:</para>
/// <list type="bullet">
/// <item><description>A structured <c>result</c> frame with
/// <c>subtype: "error_during_execution"</c> or <c>is_error: true</c>,
/// carrying <c>error.message</c>. Buffered <c>--output-format json</c>
/// emits the same frames as one JSON array; stream-json emits one object
/// per line — both are scanned, and pretty-printed multi-line JSON is
/// tolerated because only <c>{</c>-starting lines are parsed.</description></item>
/// <item><description>The stderr <c>AlreadyReportedError</c> envelope's
/// <c>error.message</c>, for runs whose stdout was truncated or lost
/// (scan stdout first so a retry-bearing stderr cannot shadow the
/// session's own terminal frame).</description></item>
/// <item><description>A plaintext pre-session failure line (e.g. a missing
/// credential notice) when no JSON error event follows.</description></item>
/// </list>
///
/// <para>Returns null when no terminal error is present (a healthy run's
/// result frame carries <c>subtype:"success"</c> with <c>is_error:false</c>
/// and a text result). Never throws: malformed lines are skipped so a
/// half-written stream still yields whatever terminal signal it contains.
/// Output is capped at <see cref="MaxDiagnosticChars"/> so the pipeline's
/// audit/webhook sinks never balloon on a verbose provider error body.
/// Structure mirrors <c>OmpTerminalDiagnoser</c> (same both-streams scan,
/// same cap) with qwen's <c>error_during_execution</c> /
/// <c>AlreadyReportedError</c> envelope shapes.</para>
/// </summary>
internal static class QwenTerminalDiagnoser
{
    internal const int MaxDiagnosticChars = 500;

    internal static string? TryExtractTerminalError(string? stdout, string? stderr)
    {
        if (TryExtractStructuredError(stdout) is { } structured)
            return structured;

        if (TryExtractAlreadyReportedError(stderr) is { } reported)
            return reported;

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
                if (TryExtractResultError(doc.RootElement) is { } error)
                    return error;
            }
            catch (JsonException)
            {
                // Half-written or interleaved chatter — keep scanning.
            }
        }

        return null;
    }

    private static string? TryExtractResultError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (!root.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || !string.Equals(type.GetString(), "result", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var isError = root.TryGetProperty("is_error", out var isErrorProp)
            && isErrorProp.ValueKind == JsonValueKind.True;
        var subtype = root.TryGetProperty("subtype", out var subtypeProp)
            && subtypeProp.ValueKind == JsonValueKind.String
            ? subtypeProp.GetString()
            : null;
        var isTerminalError = isError
            || string.Equals(subtype, "error_during_execution", StringComparison.OrdinalIgnoreCase);
        if (!isTerminalError)
            return null;

        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            if (error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(message.GetString()))
            {
                return Truncate(message.GetString()!);
            }
        }

        return Truncate("qwen run ended with result subtype '" + (subtype ?? "unknown") + "' (no error message)");
    }

    private static string? TryExtractAlreadyReportedError(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return null;

        // The AlreadyReportedError envelope is pretty-printed across lines,
        // so parse the whole stream when no single line parses.
        foreach (var candidate in EnumerateJsonCandidates(stderr))
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(message.GetString()))
                {
                    return Truncate(message.GetString()!);
                }
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateJsonCandidates(string text)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length != 0 && line.StartsWith('{'))
                yield return line;
        }

        yield return text;
    }

    private static string Truncate(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= MaxDiagnosticChars
            ? trimmed
            : trimmed[..MaxDiagnosticChars] + "…";
    }
}
