using System.Text.Json;

namespace CodeyBox.Agents.Cmd;

/// <summary>
/// Pure extraction of cmd's terminal run error from its
/// <c>-p --output-format json</c> stdout plus the plaintext stderr channel,
/// including the headless permission-gate signal.
///
/// <para>Cmd exits non-zero on terminal run errors (verified against
/// command-code 1.54.2: an unknown model id exits 1 with a
/// <c>run_error</c> event plus a <c>subtype: "error"</c> result line
/// carrying <c>error: "Error: 400 …"</c>; a paid model on a
/// $0-spend-limit key exits 4 with <c>error: "Error: 403 Key limit exceeded
/// (total limit)…"</c>; max-turns exits 8 with <c>subtype:
/// "max_turns"</c> and the <c>Warning: Reached maximum conversation
/// turns…</c> line on stderr). The failure surfaces three ways, checked in
/// order:</para>
/// <list type="bullet">
/// <item><description>The terminal <c>type: "result"</c> line: a non-success
/// <c>subtype</c> lifts <c>error</c> (falling back to the
/// <c>stopReason</c>), and a <c>subtype: "success"</c> with empty
/// <c>finalText</c> lifts the <c>no-response</c> marker so an empty reply
/// is distinguishable from a healthy one. A healthy success carries
/// non-empty <c>finalText</c> and yields null. The last result line wins —
/// it is the terminal one.</description></item>
/// <item><description>A <c>run_error</c> event frame
/// (<c>error: {name, message}</c>) — the in-stream companion of the result
/// error, kept as a fallback for truncated captures missing the terminal
/// line.</description></item>
/// <item><description>Plaintext stderr lines: <c>Error: …</c> pre-harness
/// failures (config-shape rejections, which exit 1 with EMPTY stdout so no
/// JSON frame exists — including the bare
/// <c>… has no adjustable reasoning effort</c> refusal, the one recorded
/// stderr shape without an <c>Error:</c> prefix) and the
/// <c>Warning: Reached maximum conversation turns…</c> line. The
/// <c>"isn't declared under provider"</c> advisory is deliberately
/// unmatched — the CLI sends undeclared ids anyway, so it is not a
/// failure.</description></item>
/// </list>
///
/// <para>Separately, the headless permission-gate signal: a run that ended
/// with <c>tool_hook_blocked</c> events (headless mode blocking
/// writes/edits/shell — <c>Tool "…" requires permissions. Use --yolo…</c>)
/// exits 0 with <c>subtype: "success"</c> but cannot have changed anything.
/// <see cref="TryExtractPermissionBlock"/> counts those frames so the
/// pipeline can distinguish "blocked by the permission gate" from "the
/// model declined to act" instead of misreporting the run as a successful
/// empty result. The runner always passes <c>--yolo</c>, so a non-zero
/// count means the flag was lost (stale image, wrapper) — a configuration
/// failure, never a model outcome.</para>
///
/// <para>Returns null when no terminal error is present. Never throws:
/// malformed lines are skipped so a half-written stream still yields
/// whatever terminal signal it contains. Output is capped at
/// <see cref="MaxDiagnosticChars"/> so the pipeline's audit/webhook sinks
/// never balloon on a verbose provider error body.</para>
/// </summary>
internal static class CmdTerminalDiagnoser
{
    internal const int MaxDiagnosticChars = 500;

    /// <summary>
    /// Marker for the hook-output text the CLI emits when headless mode
    /// blocks a write/edit/shell tool call (verified live). The tool name
    /// varies; the stable core is the permissions sentence.
    /// </summary>
    internal const string PermissionRequiresMarker = "requires permissions. Use --yolo";

    internal static string? TryExtractTerminalError(string? stdout, string? stderr)
    {
        if (TryExtractResultError(stdout) is { } resultError)
            return resultError;

        if (TryExtractRunError(stdout) is { } runError)
            return runError;

        return TryExtractStderrError(stderr);
    }

    /// <summary>
    /// Counts <c>tool_hook_blocked</c> frames carrying the headless
    /// permission-gate marker. Zero means the run was not permission-gated
    /// (or produced no JSON at all); non-zero is the exact blocked-call
    /// count. Never throws.
    /// </summary>
    internal static int CountPermissionBlockedCalls(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return 0;

        var count = 0;
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || !line.StartsWith('{'))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (IsPermissionBlockedFrame(doc.RootElement))
                    count++;
            }
            catch (JsonException)
            {
                // Half-written or interleaved chatter — keep scanning.
            }
        }

        return count;
    }

    private static string? TryExtractResultError(string? stdout)
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
                if (TryExtractResultMessage(doc.RootElement) is { } error)
                    last = error;
            }
            catch (JsonException)
            {
                // Half-written or interleaved chatter — keep scanning.
            }
        }

        return last;
    }

    private static string? TryExtractResultMessage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (!IsStringEqual(root, "type", "result"))
            return null;

        var subtype = FirstString(root, "subtype") ?? string.Empty;
        if (string.Equals(subtype, "success", StringComparison.OrdinalIgnoreCase))
        {
            // A successful run with no final text is the CLI's documented
            // "empty response" outcome (exit 9 / LE warning): surface the
            // marker so it reads as a terminal condition, not a healthy
            // empty reply.
            var finalText = FirstString(root, "finalText");
            return string.IsNullOrWhiteSpace(finalText)
                ? "cmd run ended with subtype=success but empty finalText (no response)"
                : null;
        }

        if (FirstString(root, "error") is { } error && !string.IsNullOrWhiteSpace(error))
            return Truncate(error);

        // Subtype error/max_turns/no_response without an error body (or a
        // future subtype): name the subtype so the failure is classified
        // instead of dead-lettering as "produced no changes".
        return Truncate($"cmd run ended with subtype={subtype}");
    }

    private static string? TryExtractRunError(string? stdout)
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
                if (TryExtractRunErrorMessage(doc.RootElement) is { } error)
                    last = error;
            }
            catch (JsonException)
            {
                // Half-written or interleaved chatter — keep scanning.
            }
        }

        return last;
    }

    private static string? TryExtractRunErrorMessage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (!IsStringEqual(root, "type", "event"))
            return null;
        if (!root.TryGetProperty("event", out var inner) || inner.ValueKind != JsonValueKind.Object)
            return null;
        if (!IsStringEqual(inner, "type", "run_error"))
            return null;
        if (!inner.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            return null;

        if (error.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(message.GetString()))
        {
            return Truncate(message.GetString()!);
        }

        return Truncate("cmd run_error event (no message)");
    }

    private static string? TryExtractStderrError(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return null;

        foreach (var rawLine in stderr.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            // The "isn't declared under provider" note fires on healthy
            // runs too (the CLI sends the id anyway) — never a failure.
            if (line.Contains("isn't declared under provider", StringComparison.Ordinal))
                continue;

            if (line.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Warning: Reached maximum conversation turns", StringComparison.Ordinal)
                || line.Contains("has no adjustable reasoning effort", StringComparison.Ordinal))
            {
                return Truncate(line);
            }
        }

        return null;
    }

    private static bool IsPermissionBlockedFrame(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return false;
        if (!IsStringEqual(root, "type", "event"))
            return false;
        if (!root.TryGetProperty("event", out var inner) || inner.ValueKind != JsonValueKind.Object)
            return false;
        if (!IsStringEqual(inner, "type", "tool_hook_blocked"))
            return false;
        return inner.TryGetProperty("hookOutput", out var hookOutput)
            && hookOutput.ValueKind == JsonValueKind.String
            && (hookOutput.GetString() ?? string.Empty).Contains(PermissionRequiresMarker, StringComparison.Ordinal);
    }

    private static bool IsStringEqual(JsonElement obj, string name, string expected) =>
        obj.TryGetProperty(name, out var prop)
        && prop.ValueKind == JsonValueKind.String
        && string.Equals(prop.GetString(), expected, StringComparison.Ordinal);

    private static string? FirstString(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj.TryGetProperty(name, out var prop)
                && prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }
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
