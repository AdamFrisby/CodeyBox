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

    /// <summary>
    /// Extracts agy's <b>terminal</b> error region from its cumulative glog: the
    /// slice from the LAST known quota/auth marker to the end of the log, but only
    /// when that marker falls inside the tail window (the final
    /// <paramref name="windowLines"/> content lines). Returns <c>null</c> when no
    /// marker sits in the tail window.
    ///
    /// <para>agy aborts a run immediately after writing its terminal error, so the
    /// error that actually ended the run sits at the very end of the glog. This is
    /// what lets the caller surface ONLY the terminal failure to the classifier-
    /// facing <c>result.Stderr</c> without folding the whole cumulative log: an
    /// earlier <c>RESOURCE_EXHAUSTED</c> that agy retried past (and then logged many
    /// more lines after) falls outside the tail window and is excluded, so it can't
    /// falsely park/bench the member; a genuinely terminal 429/401 sits inside the
    /// window and is surfaced. The reset hint (e.g. <c>Resets in 8m14s</c>) rides on
    /// the same terminal line, so <see cref="Detect"/>'s reset parsing still works
    /// off the folded region.</para>
    /// </summary>
    public static string? ExtractTerminalErrorRegion(string? glog, int windowLines = 25)
    {
        if (string.IsNullOrEmpty(glog)) return null;

        var lines = glog.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var end = lines.Length;
        while (end > 0 && lines[end - 1].Length == 0) end--; // drop trailing blank lines
        if (end == 0) return null;

        var windowStart = Math.Max(0, end - windowLines);
        var lastMarker = -1;
        for (var i = windowStart; i < end; i++)
        {
            if (LineContainsQuotaOrAuthMarker(lines[i]))
                lastMarker = i;
        }
        if (lastMarker < 0) return null;

        return string.Join("\n", lines[lastMarker..end]);
    }

    private static bool LineContainsQuotaOrAuthMarker(string line)
    {
        foreach (var (pattern, _) in Patterns)
        {
            if (line.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

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
