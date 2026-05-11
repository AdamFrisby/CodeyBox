using System.Text.Json;
using System.Text.RegularExpressions;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

public sealed record QuotaDetection(QuotaFailureKind Kind, DateTimeOffset? ResetAt = null);

public static class QuotaFailureDetector
{
    private static readonly (string Pattern, QuotaFailureKind Kind)[] Patterns =
    [
        // Codex / ChatGPT
        ("hit your usage limit", QuotaFailureKind.LimitReached),
        ("hit your limit", QuotaFailureKind.LimitReached),
        // Anthropic / OpenAI rate limits
        ("rate_limit_exceeded", QuotaFailureKind.RateLimitExceeded),
        // Google / Gemini Code Assist
        ("RESOURCE_EXHAUSTED", QuotaFailureKind.RateLimitExceeded),
        ("exceeded the rate limit", QuotaFailureKind.RateLimitExceeded),
        ("quota exceeded", QuotaFailureKind.RateLimitExceeded),
        // Gemini per-model wall: "[API Error: You have exhausted your capacity on this model. ...]"
        ("exhausted your capacity", QuotaFailureKind.LimitReached),
        // Auth
        ("API Error: 401", QuotaFailureKind.Unauthorized),
    ];

    // Matches the duration tail of common reset/retry phrasings:
    //   "reset after 21h41m24s", "will reset after 5m17s",
    //   "reset in 30m", "retry after 1h", "try again after 2h30m".
    // The duration pieces are individually optional but at least one must
    // match; the surrounding code rejects the all-zero case.
    private static readonly Regex ResetAfterRegex = new(
        @"(?:reset(?:s|ting)?(?:\s+will\s+reset)?\s+after|reset\s+in|retry\s+after|try\s+again\s+after|available\s+(?:in|after))\s+(?:(\d+)\s*h)?\s*(?:(\d+)\s*m)?\s*(?:(\d+)\s*s)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static QuotaDetection? Detect(string? stderr, string? stdout = null)
    {
        if (string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout))
            return null;

        // Stream-json events embedded in stdout often carry the error message
        // inside an `error.message` (or `result`) field rather than at the top
        // level. Extract those messages first so substring patterns match
        // against the structured payload's text, not the JSON envelope.
        var streamMessages = ExtractStreamJsonErrorMessages(stdout);

        foreach (var (pattern, kind) in Patterns)
        {
            var inStderr = !string.IsNullOrEmpty(stderr) && stderr.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            var inStdout = !string.IsNullOrEmpty(stdout) && stdout.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            var inStream = streamMessages.Any(m => m.Contains(pattern, StringComparison.OrdinalIgnoreCase));

            if (inStderr || inStdout || inStream)
            {
                // Prefer the structured stream-json error text for reset parsing —
                // it's the cleanest source of "after Xh Ym Zs" intervals. Fall
                // back to stderr, then unstructured stdout.
                var resetSources = new List<string>(streamMessages.Count + 2);
                resetSources.AddRange(streamMessages);
                if (!string.IsNullOrEmpty(stderr)) resetSources.Add(stderr);
                if (!string.IsNullOrEmpty(stdout)) resetSources.Add(stdout);

                return new QuotaDetection(kind, TryParseResetAt(resetSources));
            }
        }

        return null;
    }

    private static DateTimeOffset? TryParseResetAt(IEnumerable<string> sources)
    {
        foreach (var source in sources)
        {
            if (string.IsNullOrEmpty(source)) continue;
            var match = ResetAfterRegex.Match(source);
            if (!match.Success) continue;

            var h = 0;
            var m = 0;
            var s = 0;

            if (match.Groups[1].Success && int.TryParse(match.Groups[1].Value, out var hv)) h = Math.Min(hv, 10_000);
            if (match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var mv)) m = Math.Min(mv, 10_000);
            if (match.Groups[3].Success && int.TryParse(match.Groups[3].Value, out var sv)) s = Math.Min(sv, 10_000);

            if (h > 0 || m > 0 || s > 0)
                return DateTimeOffset.UtcNow.Add(new TimeSpan(h, m, s));
        }

        return null;
    }

    /// <summary>
    /// Walks NDJSON lines in <paramref name="stdout"/> and returns the inner
    /// error messages from any stream-json event that signals failure.
    /// Recognised shapes (across claude / codex / gemini):
    /// <list type="bullet">
    ///   <item>Gemini: <c>{"type":"result","status":"error","error":{"message":"..."}}</c></item>
    ///   <item>Claude: <c>{"type":"result","is_error":true,"result":"..."}</c></item>
    ///   <item>Codex: <c>{"type":"error","message":"..."}</c> or
    ///         <c>{"msg":{"type":"error","message":"..."}}</c></item>
    /// </list>
    /// Returns an empty list when stdout is not NDJSON or contains no error events.
    /// Never throws — malformed lines are skipped silently.
    /// </summary>
    internal static IReadOnlyList<string> ExtractStreamJsonErrorMessages(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return Array.Empty<string>();

        var first = stdout.AsSpan().TrimStart();
        if (first.IsEmpty || first[0] != '{') return Array.Empty<string>();

        var messages = new List<string>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                // Codex wraps events in {"msg": {...}} — unwrap and re-evaluate.
                var node = root;
                if (node.ValueKind == JsonValueKind.Object
                    && node.TryGetProperty("msg", out var msg)
                    && msg.ValueKind == JsonValueKind.Object)
                {
                    node = msg;
                }

                if (!node.TryGetProperty("type", out var typeProp)) continue;
                var type = typeProp.GetString();

                if (type == "error")
                {
                    AddIfNonEmpty(messages, ReadString(node, "message"));
                    continue;
                }

                if (type != "result") continue;

                var isError = false;
                if (node.TryGetProperty("status", out var statusProp)
                    && string.Equals(statusProp.GetString(), "error", StringComparison.OrdinalIgnoreCase))
                {
                    isError = true;
                }
                if (node.TryGetProperty("is_error", out var isErrorProp)
                    && isErrorProp.ValueKind == JsonValueKind.True)
                {
                    isError = true;
                }
                if (node.TryGetProperty("subtype", out var subtypeProp)
                    && string.Equals(subtypeProp.GetString(), "error", StringComparison.OrdinalIgnoreCase))
                {
                    isError = true;
                }
                if (!isError) continue;

                if (node.TryGetProperty("error", out var errorProp))
                {
                    if (errorProp.ValueKind == JsonValueKind.Object)
                        AddIfNonEmpty(messages, ReadString(errorProp, "message"));
                    else if (errorProp.ValueKind == JsonValueKind.String)
                        AddIfNonEmpty(messages, errorProp.GetString());
                }

                AddIfNonEmpty(messages, ReadString(node, "result"));
                AddIfNonEmpty(messages, ReadString(node, "message"));
            }
            catch (JsonException) { }
            catch (InvalidOperationException) { }
        }

        return messages;
    }

    private static string? ReadString(JsonElement node, string property)
        => node.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static void AddIfNonEmpty(List<string> list, string? value)
    {
        if (!string.IsNullOrEmpty(value)) list.Add(value);
    }

    public static async Task RecordIfQuotaFailureAsync(
        IQuotaFailureStore? store,
        AgentKind agent,
        string? modelId,
        string? summary,
        string? stderr,
        DateTimeOffset observedAt,
        TimeSpan retention,
        CancellationToken ct,
        ProjectId? projectId = null,
        string? stdout = null)
    {
        if (store is null)
            return;

        if (!string.Equals(summary?.Trim(), "agent exited 1", StringComparison.OrdinalIgnoreCase))
            return;

        var detection = Detect(stderr, stdout);
        if (detection is null)
            return;

        if (projectId is { } scopedProject)
            await store.RecordForProjectAsync(agent, modelId, scopedProject, detection.Kind, observedAt, ct);
        else
            await store.RecordAsync(agent, modelId, detection.Kind, observedAt, ct);

        await store.PruneOlderThanAsync(observedAt - retention, ct);
    }
}
