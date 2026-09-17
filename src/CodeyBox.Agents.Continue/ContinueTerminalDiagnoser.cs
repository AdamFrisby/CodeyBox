using System.Text.Json;

namespace CodeyBox.Agents.Continue;

/// <summary>
/// Pure extraction of Continue's terminal run error from <c>cn --print</c>
/// stdout.
///
/// <para>The CLI exits 0 on provider failures with the cause in a
/// single-line <c>{"status":"error","message":"…"}</c> envelope (verified
/// against @continuedev/cli 1.5.47: a $0-spend-limit key against a paid
/// model yields <c>403 Key limit exceeded (total limit)…</c>; the
/// onboarding-gate interceptor failure shares the envelope shape). Success
/// output is the model's plain text (which may legitimately be empty when
/// the work landed in files), so only the exact envelope — an object with
/// string <c>status</c> equal to <c>error</c> and a non-blank string
/// <c>message</c> — is lifted. The last surviving envelope wins.</para>
///
/// <para>Returns null when no terminal error is present. Never throws:
/// malformed lines are skipped so a half-written stream still yields
/// whatever terminal signal it contains. Output is capped at
/// <see cref="MaxDiagnosticChars"/> so the pipeline's audit/webhook sinks
/// never balloon on a verbose provider error body.</para>
/// </summary>
internal static class ContinueTerminalDiagnoser
{
    internal const int MaxDiagnosticChars = 500;

    internal static string? TryExtractTerminalError(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;

        string? last = null;

        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || !line.StartsWith('{'))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (TryExtractErrorMessage(doc.RootElement) is { } error)
                    last = error;
            }
            catch (JsonException)
            {
                // Half-written or interleaved chatter — keep scanning.
            }
        }

        return last;
    }

    private static string? TryExtractErrorMessage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (!root.TryGetProperty("status", out var status)
            || status.ValueKind != JsonValueKind.String
            || !string.Equals(status.GetString(), "error", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!root.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(message.GetString()))
            return null;

        var trimmed = message.GetString()!.Trim();
        return trimmed.Length <= MaxDiagnosticChars
            ? trimmed
            : trimmed[..MaxDiagnosticChars] + "…";
    }
}
