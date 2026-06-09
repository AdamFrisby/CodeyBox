using System.Text.Json;
using System.Text.RegularExpressions;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Antigravity;

/// <summary>
/// Recognises quota / rate-limit / lockout failures emitted by the Google
/// Antigravity CLI (<c>agy</c>) and the underlying cloudcode-pa gateway.
///
/// <para>The CLI's quota story is request-based and AGGRESSIVELY VOLATILE:
/// AI Pro caps the subscription on a WEEKLY refresh with up to a 7-day
/// lockout when the cap trips, so the detector MUST surface the reset
/// timestamp the gateway provides — otherwise items get stuck in a churn
/// loop because the orchestrator can't tell whether to park them in
/// <c>WaitingForQuotaReset</c> or trip the breaker. We accept both
/// duration-tail phrasings (parsed via
/// <see cref="QuotaResetParser"/>) and explicit ISO-8601 "lockout reset"
/// fields that the gateway has been observed to emit; both flow into the
/// same <see cref="QuotaDetection.ResetAt"/>.</para>
///
/// Sources scanned:
/// <list type="bullet">
///   <item>stderr / stdout text (e.g. <c>RESOURCE_EXHAUSTED</c>,
///         <c>quota exceeded</c>, <c>weekly limit reached</c>,
///         <c>account locked until</c>, <c>API Error: 401</c>).</item>
///   <item>NDJSON error envelopes: <c>{"type":"result","status":"error", ...}</c>
///         and <c>{"type":"error", ...}</c>, including the gateway's
///         <c>quota_metadata.lockout_until</c> hint when present.</item>
/// </list>
/// </summary>
public sealed class AntigravityQuotaFailureDetector : IAgentQuotaFailureDetector
{
    public AgentKind Kind => AgentKind.Antigravity;

    // Order matters: more specific shapes (LimitReached for lockouts) before
    // the generic RateLimitExceeded so we don't mis-classify a hard weekly
    // cap as a transient rate-limit.
    private static readonly (string Pattern, QuotaFailureKind Kind)[] Patterns =
    [
        ("account locked", QuotaFailureKind.LimitReached),
        ("weekly limit reached", QuotaFailureKind.LimitReached),
        ("weekly quota exceeded", QuotaFailureKind.LimitReached),
        ("lockout in effect", QuotaFailureKind.LimitReached),
        ("exhausted your weekly", QuotaFailureKind.LimitReached),
        ("RESOURCE_EXHAUSTED", QuotaFailureKind.RateLimitExceeded),
        ("rate limit exceeded", QuotaFailureKind.RateLimitExceeded),
        ("quota exceeded", QuotaFailureKind.RateLimitExceeded),
        ("too many requests", QuotaFailureKind.RateLimitExceeded),
        ("API Error: 401", QuotaFailureKind.Unauthorized),
        ("API Error: 403", QuotaFailureKind.Unauthorized),
    ];

    // Matches "lockout until 2026-06-16T12:34:56Z" / "locked until 2026-06-16T12:34:56+00:00".
    private static readonly Regex LockoutUntilRegex = new(
        @"(?:lockout\s+until|locked\s+until|reset(?:s)?\s+at|available\s+at)\s+(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2})?(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public QuotaDetection? Detect(string? stderr, string? stdout)
    {
        if (string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout))
            return null;

        var streamMessages = ExtractStreamJsonErrorMessages(stdout);
        var structuredReset = ExtractStructuredLockoutReset(stdout);

        foreach (var (pattern, kind) in Patterns)
        {
            var inStderr = !string.IsNullOrEmpty(stderr) && stderr.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            var inStdout = !string.IsNullOrEmpty(stdout) && stdout.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            var inStream = streamMessages.Any(m => m.Contains(pattern, StringComparison.OrdinalIgnoreCase));

            if (!inStderr && !inStdout && !inStream) continue;

            var resetSources = new List<string?>(streamMessages.Count + 2);
            resetSources.AddRange(streamMessages);
            if (!string.IsNullOrEmpty(stderr)) resetSources.Add(stderr);
            if (!string.IsNullOrEmpty(stdout)) resetSources.Add(stdout);
            var reset = structuredReset
                ?? TryParseAbsoluteReset(stderr)
                ?? TryParseAbsoluteReset(stdout)
                ?? QuotaResetParser.TryParseResetAt(resetSources);
            return new QuotaDetection(kind, reset);
        }

        return null;
    }

    /// <summary>
    /// Reads <c>quota_metadata.lockout_until</c> / <c>resetsAt</c> /
    /// <c>retry_at</c> from a structured error envelope in <paramref name="stdout"/>.
    /// Returns null when no structured envelope exists or no parseable
    /// timestamp is present. Surfaces the gateway-provided absolute reset so a
    /// 7-day lockout parks the work item until then instead of churning.
    /// </summary>
    internal static DateTimeOffset? ExtractStructuredLockoutReset(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;
        var first = stdout.AsSpan().TrimStart();
        if (first.IsEmpty || first[0] != '{') return null;

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var reset = TryReadResetFromNode(root);
                if (reset is not null) return reset;
                if (root.TryGetProperty("error", out var err)
                    && err.ValueKind == JsonValueKind.Object)
                {
                    reset = TryReadResetFromNode(err);
                    if (reset is not null) return reset;
                }
            }
            catch (JsonException) { }
            catch (InvalidOperationException) { }
        }
        return null;
    }

    private static DateTimeOffset? TryReadResetFromNode(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object) return null;
        if (node.TryGetProperty("quota_metadata", out var meta)
            && meta.ValueKind == JsonValueKind.Object)
        {
            var reset = ReadIso(meta, "lockout_until")
                ?? ReadIso(meta, "reset_at")
                ?? ReadIso(meta, "resetsAt");
            if (reset is not null) return reset;
        }
        return ReadIso(node, "lockout_until")
            ?? ReadIso(node, "retry_at")
            ?? ReadIso(node, "resetsAt")
            ?? ReadIso(node, "reset_at");
    }

    private static DateTimeOffset? ReadIso(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.String when DateTimeOffset.TryParse(prop.GetString(), out var parsed) => parsed,
            JsonValueKind.Number when prop.TryGetInt64(out var unix) => TryFromUnixSeconds(unix),
            _ => null,
        };
    }

    private static DateTimeOffset? TryFromUnixSeconds(long unix)
    {
        try { return DateTimeOffset.FromUnixTimeSeconds(unix); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    internal static DateTimeOffset? TryParseAbsoluteReset(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var match = LockoutUntilRegex.Match(text);
        if (!match.Success) return null;
        return DateTimeOffset.TryParse(match.Groups[1].Value, out var parsed) ? parsed : null;
    }

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
                if (!root.TryGetProperty("type", out var typeProp)) continue;
                var t = typeProp.GetString();
                if (t != "result" && t != "error") continue;

                if (t == "result")
                {
                    var isError = false;
                    if (root.TryGetProperty("status", out var statusProp)
                        && string.Equals(statusProp.GetString(), "error", StringComparison.OrdinalIgnoreCase))
                        isError = true;
                    if (root.TryGetProperty("is_error", out var isErrorProp)
                        && isErrorProp.ValueKind == JsonValueKind.True)
                        isError = true;
                    if (!isError) continue;
                }

                if (root.TryGetProperty("error", out var errorProp))
                {
                    if (errorProp.ValueKind == JsonValueKind.Object)
                        AddIfNonEmpty(messages, ReadString(errorProp, "message"));
                    else if (errorProp.ValueKind == JsonValueKind.String)
                        AddIfNonEmpty(messages, errorProp.GetString());
                }

                AddIfNonEmpty(messages, ReadString(root, "message"));
                AddIfNonEmpty(messages, ReadString(root, "result"));
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
}
