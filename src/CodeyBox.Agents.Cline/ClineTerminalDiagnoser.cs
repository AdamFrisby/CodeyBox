using System.Text.Json;

namespace CodeyBox.Agents.Cline;

/// <summary>
/// Pure extraction of cline's terminal run error from <c>--json</c> stdout
/// and stderr.
///
/// <para>Cline exits non-zero on terminal failures (verified against 3.0.62:
/// a bad OpenRouter key exits 1 with an <c>agent_event error</c> frame
/// carrying <c>User not found.</c>, a <c>$0</c>-key paid-model refusal exits
/// 1 with <c>Key limit exceeded (total limit)…</c>, and the default-provider
/// miss exits 1 with <c>Unauthorized: … re-authenticate your Cline
/// account.</c>). The failure is reported three ways and the first hit wins:
/// the <c>agent_event</c> error frame's <c>error.message</c>, the error
/// <c>run_result</c>'s <c>text</c>, or a stderr
/// <c>{"type":"error","message":"…"}</c> line (the CLI echoes the terminal
/// error there; a missing-prompt refusal surfaces ONLY on stderr).</para>
///
/// <para>Returns null when no terminal error is present (a healthy run ends
/// in <c>run_result finishReason: completed</c> with the final text).
/// Never throws: malformed lines are skipped so a half-written stream still
/// yields whatever terminal signal it contains. Output is capped at
/// <see cref="MaxDiagnosticChars"/> so the pipeline's audit/webhook sinks
/// never balloon on a verbose provider error body.</para>
/// </summary>
internal static class ClineTerminalDiagnoser
{
    internal const int MaxDiagnosticChars = 500;

    internal static string? TryExtractTerminalError(string? stdout, string? stderr)
    {
        if (TryExtractFromStdout(stdout) is { } stdoutError)
            return stdoutError;

        return TryExtractFromStderr(stderr);
    }

    private static string? TryExtractFromStdout(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;

        string? runResultError = null;
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || !line.StartsWith('{'))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                // The per-iteration error frame carries the precise cause;
                // prefer it over the terminal run_result text.
                if (TryExtractAgentErrorMessage(root) is { } agentError)
                    return agentError;
                // An error run_result repeats the cause as text; remember it
                // in case no agent_event error frame was captured.
                if (runResultError is null
                    && TryExtractRunResultError(root) is { } resultError)
                {
                    runResultError = resultError;
                }
            }
            catch (JsonException)
            {
                // Half-written or interleaved chatter — keep scanning.
            }
        }

        return runResultError;
    }

    private static string? TryExtractAgentErrorMessage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (!IsStringEqual(root, "type", "agent_event"))
            return null;
        if (!root.TryGetProperty("event", out var inner)
            || inner.ValueKind != JsonValueKind.Object)
            return null;
        if (!IsStringEqual(inner, "type", "error"))
            return null;
        if (!inner.TryGetProperty("error", out var error)
            || error.ValueKind != JsonValueKind.Object)
            return null;
        if (!error.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(message.GetString()))
            return null;

        return Truncate(message.GetString()!);
    }

    private static string? TryExtractRunResultError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (!IsStringEqual(root, "type", "run_result"))
            return null;
        if (!IsStringEqual(root, "finishReason", "error"))
            return null;
        if (!root.TryGetProperty("text", out var text)
            || text.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(text.GetString()))
            return null;

        return Truncate(text.GetString()!);
    }

    private static string? TryExtractFromStderr(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return null;

        foreach (var rawLine in stderr.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || !line.StartsWith('{'))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    continue;
                if (!IsStringEqual(root, "type", "error"))
                    continue;
                if (!root.TryGetProperty("message", out var message)
                    || message.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(message.GetString()))
                    continue;

                return Truncate(message.GetString()!);
            }
            catch (JsonException)
            {
                // Half-written line — keep scanning.
            }
        }

        return null;
    }

    private static bool IsStringEqual(JsonElement root, string name, string expected) =>
        root.TryGetProperty(name, out var prop)
        && prop.ValueKind == JsonValueKind.String
        && string.Equals(prop.GetString(), expected, StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= MaxDiagnosticChars
            ? trimmed
            : trimmed[..MaxDiagnosticChars] + "…";
    }
}
