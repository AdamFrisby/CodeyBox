using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Claude;

/// <summary>
/// Recognises quota / rate-limit failures emitted by the Claude Code CLI.
///
/// Sources scanned:
/// <list type="bullet">
///   <item>stderr / stdout text (e.g. <c>rate_limit_exceeded</c>, <c>usage_limit</c>).</item>
///   <item>Stream-json error events: <c>{"type":"result","is_error":true,"result":"..."}</c>
///         (with optional <c>subtype:"error"</c>).</item>
///   <item>Stream-json rate-limit events: <c>{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":...,"rateLimitType":"five_hour"|"seven_day"|"weekly"}}</c>.
///         Emitted by the host CLI when the user's 5h or weekly subscription
///         window is rejected mid-run; no <c>rate_limit_exceeded</c> string
///         appears in the line, so the structured envelope is the only signal.</item>
/// </list>
///
/// <para><b>401 / Unauthorized is deliberately NOT classified as a quota
/// failure here.</b> Anthropic's single-use refresh tokens, combined with the
/// host <c>claude</c> CLI rotating concurrently with in-VM CLI invocations,
/// produce intermittent 401s even though the user's subscription is fully
/// available. Treating those as <c>QuotaFailureKind.Unauthorized</c> would
/// trip the observed-failure breaker and pin Claude as unusable for the full
/// breaker window, defeating the fallback chain. 401s are surfaced separately
/// via <see cref="IsUnauthorizedSignal"/> so callers can audit-log them as
/// auth/transient events without recording them as quota events. The
/// shared-OAuth race itself is largely closed by stripping the refresh_token
/// from the bundle materialised into the VM (see
/// <c>ClaudeOAuthFileCredentialProvider</c>); this stopgap covers the residual
/// expired-access-token case.</para>
///
/// <para>Provider-specific stream-json scoping (see
/// <see cref="IsTerminalNonQuotaCrash"/> and
/// <see cref="ScopeStdoutForQuotaDetection"/>) also lives here so the
/// orchestrator's dispatch path stays provider-agnostic — Codex / Gemini
/// detectors are never run through Claude's NDJSON walker.</para>
/// </summary>
public sealed class ClaudeQuotaFailureDetector : IAgentQuotaFailureDetector
{
    public AgentKind Kind => AgentKind.Claude;

    /// <summary>
    /// stderr/stdout substring marking a Claude CLI 401 / Unauthorized response.
    /// Centralised so the detector and any audit-log emitter agree on the
    /// pattern.
    /// </summary>
    internal const string UnauthorizedSignal = "API Error: 401";

    /// <summary>
    /// Substring that appears in the Claude API 400 "thinking blocks cannot be
    /// modified" error body. Used as a backstop when the stream-json envelope
    /// is missing <c>api_error_status</c> but the inner message still names
    /// the invariant violation.
    /// </summary>
    internal const string ThinkingBlockSignature =
        "blocks in the latest assistant message cannot be modified";

    private static readonly (string Pattern, QuotaFailureKind Kind)[] Patterns =
    [
        ("rate_limit_exceeded", QuotaFailureKind.RateLimitExceeded),
        ("HTTP 429", QuotaFailureKind.RateLimitExceeded),
        ("status 429", QuotaFailureKind.RateLimitExceeded),
        ("API Error: 429", QuotaFailureKind.RateLimitExceeded),
        ("429 Too Many Requests", QuotaFailureKind.RateLimitExceeded),
        ("usage_limit", QuotaFailureKind.LimitReached),
        ("hit your usage limit", QuotaFailureKind.LimitReached),
        ("hit your limit", QuotaFailureKind.LimitReached),
    ];

    /// <summary>
    /// Returns true when the captured streams carry the Claude 401 marker.
    /// Used to emit a distinguishing audit-log line without classifying the
    /// failure as a quota event — see the class summary for the rationale.
    /// </summary>
    public static bool IsUnauthorizedSignal(string? stderr, string? stdout)
    {
        if (!string.IsNullOrEmpty(stderr) && stderr.Contains(UnauthorizedSignal, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrEmpty(stdout) && stdout.Contains(UnauthorizedSignal, StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var msg in ExtractStreamJsonErrorMessages(stdout))
        {
            if (msg.Contains(UnauthorizedSignal, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public void EmitAdvisoryAuditEvents(string? stderr, string? stdout, string phase, string? sandboxName)
    {
        if (IsUnauthorizedSignal(stderr, stdout))
            AuditLog.ClaudeUnauthorizedObserved(phase, sandboxName);
    }

    /// <summary>
    /// Returns true when the stream-json terminal result carries a 4xx API
    /// status other than 429 (which is the only quota signal in that range),
    /// or when the terminal result / stderr names the thinking-block invariant
    /// violation. The orchestrator uses this to short-circuit quota
    /// classification for non-quota crashes.
    /// </summary>
    public bool IsTerminalNonQuotaCrash(string? stderr, string? stdout)
    {
        if (TryGetTerminalStreamError(stdout, out var terminal))
        {
            if (terminal.ApiErrorStatus is int status
                && status is >= 400 and < 500
                && status != 429)
            {
                return true;
            }

            if (ContainsThinkingBlockSignature(terminal.Message))
                return true;
        }

        if (!string.IsNullOrEmpty(stderr) && ContainsThinkingBlockSignature(stderr))
            return true;

        return false;
    }

    /// <summary>
    /// Narrows <paramref name="stdout"/> to the terminal NDJSON error line when
    /// the buffer is Claude stream-json. Prevents stale <c>rate_limit_exceeded</c>
    /// text from earlier events in a long multi-turn run from false-positiving
    /// the final failure. Non-NDJSON / empty input is returned unchanged so
    /// stderr-style buffers still flow through <see cref="Detect"/> intact.
    ///
    /// When a terminal <c>result</c> error line coexists with a
    /// <c>rate_limit_event</c> rejection line (CLI emits the latter just before
    /// exiting on a 5h / weekly window cap), both are preserved so the rejection
    /// signal isn't dropped by the narrowing step.
    /// </summary>
    public string? ScopeStdoutForQuotaDetection(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return stdout;

        if (!TryGetTerminalStreamError(stdout, out var terminal))
            return stdout;

        var rejections = ExtractRateLimitRejections(stdout);
        if (rejections.Count == 0)
            return terminal.RawLine;

        return terminal.RawLine + "\n" + rejections[^1].RawLine;
    }

    public QuotaDetection? Detect(string? stderr, string? stdout)
    {
        if (string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout))
            return null;

        var streamMessages = ExtractStreamJsonErrorMessages(stdout);

        foreach (var (pattern, kind) in Patterns)
        {
            var inStderr = !string.IsNullOrEmpty(stderr) && stderr.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            var inStdout = !string.IsNullOrEmpty(stdout) && stdout.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            var inStream = streamMessages.Any(m => m.Contains(pattern, StringComparison.OrdinalIgnoreCase));

            if (inStderr || inStdout || inStream)
            {
                var resetSources = new List<string?>(streamMessages.Count + 2);
                resetSources.AddRange(streamMessages);
                if (!string.IsNullOrEmpty(stderr)) resetSources.Add(stderr);
                if (!string.IsNullOrEmpty(stdout)) resetSources.Add(stdout);
                return new QuotaDetection(kind, QuotaResetParser.TryParseResetAt(resetSources));
            }
        }

        // Structured rate-limit-event rejection (5h / weekly window): the line
        // carries no "rate_limit_exceeded" text — the only quota signal is the
        // envelope {type:"rate_limit_event", rate_limit_info:{status:"rejected", ...}}.
        // claude exhausts its 5h window routinely, and before this branch the
        // failure classified as "other", hard-Failing the work item instead of
        // routing it through cross-agent fallback / WaitingForQuotaReset.
        var rejections = ExtractRateLimitRejections(stdout);
        if (rejections.Count > 0)
        {
            var resetAt = rejections.Select(r => r.ResetAt).FirstOrDefault(r => r is not null);
            return new QuotaDetection(QuotaFailureKind.RateLimitExceeded, resetAt);
        }

        return null;
    }

    internal static bool ContainsThinkingBlockSignature(string? text) =>
        !string.IsNullOrEmpty(text)
        && (text.Contains(ThinkingBlockSignature, StringComparison.OrdinalIgnoreCase)
            || text.Contains("`thinking`", StringComparison.Ordinal)
            || text.Contains("`redacted_thinking`", StringComparison.Ordinal));

    /// <summary>
    /// Walks NDJSON lines and returns inner error messages from Claude's
    /// stream-json error result events.
    /// </summary>
    internal static IReadOnlyList<string> ExtractStreamJsonErrorMessages(string? stdout)
    {
        var entries = EnumerateTerminalErrorEntries(stdout);
        if (entries.Count == 0) return Array.Empty<string>();

        var messages = new List<string>();
        foreach (var entry in entries)
        {
            AddIfNonEmpty(messages, entry.Result);
            AddIfNonEmpty(messages, entry.Message);
            AddIfNonEmpty(messages, entry.ErrorObjectMessage);
        }
        return messages;
    }

    internal static bool TryGetTerminalStreamError(string? stdout, out TerminalStreamError terminal)
    {
        terminal = default!;
        var entries = EnumerateTerminalErrorEntries(stdout);
        if (entries.Count == 0) return false;

        var last = entries[^1];
        var message = last.Result ?? last.Message ?? last.ErrorObjectMessage;
        terminal = new TerminalStreamError(last.RawLine, message, last.ApiErrorStatus);
        return true;
    }

    /// <summary>
    /// Walks NDJSON lines and returns every <c>rate_limit_event</c> with
    /// <c>rate_limit_info.status == "rejected"</c> and a recognised quota-scope
    /// <c>rateLimitType</c> (<c>five_hour</c>, <c>seven_day</c>, <c>weekly</c>).
    /// The full set is returned in source order so <see cref="Detect"/> can pick
    /// the first resetsAt and <see cref="ScopeStdoutForQuotaDetection"/> can
    /// preserve the latest line.
    /// </summary>
    internal static IReadOnlyList<RateLimitRejection> ExtractRateLimitRejections(string? stdout)
    {
        var empty = Array.Empty<RateLimitRejection>();
        if (string.IsNullOrWhiteSpace(stdout)) return empty;

        var first = stdout.AsSpan().TrimStart();
        if (first.IsEmpty || first[0] != '{') return empty;

        List<RateLimitRejection>? rejections = null;
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            if (!TryParseRateLimitEvent(line, out var rejection)) continue;
            rejections ??= new List<RateLimitRejection>();
            rejections.Add(rejection);
        }
        return (IReadOnlyList<RateLimitRejection>?)rejections ?? empty;
    }

    private static bool TryParseRateLimitEvent(string line, out RateLimitRejection rejection)
    {
        rejection = default!;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp)
                || typeProp.ValueKind != JsonValueKind.String
                || typeProp.GetString() != "rate_limit_event")
            {
                return false;
            }

            if (!root.TryGetProperty("rate_limit_info", out var info)
                || info.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var status = ReadString(info, "status");
            if (!string.Equals(status, "rejected", StringComparison.OrdinalIgnoreCase))
                return false;

            var rateLimitType = ReadString(info, "rateLimitType");
            if (!IsQuotaScope(rateLimitType))
                return false;

            DateTimeOffset? resetAt = null;
            if (info.TryGetProperty("resetsAt", out var resetsProp))
            {
                switch (resetsProp.ValueKind)
                {
                    case JsonValueKind.Number when resetsProp.TryGetInt64(out var unix):
                        resetAt = TryFromUnixSeconds(unix);
                        break;
                    case JsonValueKind.String when long.TryParse(resetsProp.GetString(), out var unixStr):
                        resetAt = TryFromUnixSeconds(unixStr);
                        break;
                }
            }

            rejection = new RateLimitRejection(line, resetAt, rateLimitType);
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static DateTimeOffset? TryFromUnixSeconds(long unix)
    {
        // DateTimeOffset.FromUnixTimeSeconds throws on out-of-range; treat any
        // bad payload as a missing reset hint rather than failing classification.
        try { return DateTimeOffset.FromUnixTimeSeconds(unix); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static bool IsQuotaScope(string? rateLimitType)
    {
        if (string.IsNullOrEmpty(rateLimitType)) return false;
        return rateLimitType.Equals("five_hour", StringComparison.OrdinalIgnoreCase)
            || rateLimitType.Equals("seven_day", StringComparison.OrdinalIgnoreCase)
            || rateLimitType.Equals("weekly", StringComparison.OrdinalIgnoreCase);
    }

    private static List<StreamJsonErrorEntry> EnumerateTerminalErrorEntries(string? stdout)
    {
        var empty = new List<StreamJsonErrorEntry>(0);
        if (string.IsNullOrWhiteSpace(stdout)) return empty;

        var first = stdout.AsSpan().TrimStart();
        if (first.IsEmpty || first[0] != '{') return empty;

        List<StreamJsonErrorEntry>? entries = null;
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            if (!TryParseErrorEntry(line, out var entry)) continue;
            entries ??= new List<StreamJsonErrorEntry>();
            entries.Add(entry);
        }
        return entries ?? empty;
    }

    private static bool TryParseErrorEntry(string line, out StreamJsonErrorEntry entry)
    {
        entry = default!;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp)
                || typeProp.GetString() != "result")
            {
                return false;
            }

            var isError = false;
            if (root.TryGetProperty("is_error", out var isErrorProp)
                && isErrorProp.ValueKind == JsonValueKind.True)
            {
                isError = true;
            }
            if (root.TryGetProperty("subtype", out var subtypeProp)
                && string.Equals(subtypeProp.GetString(), "error", StringComparison.OrdinalIgnoreCase))
            {
                isError = true;
            }
            if (root.TryGetProperty("status", out var statusProp)
                && string.Equals(statusProp.GetString(), "error", StringComparison.OrdinalIgnoreCase))
            {
                isError = true;
            }
            if (!isError) return false;

            var result = ReadString(root, "result");
            var message = ReadString(root, "message");
            string? errorObjectMessage = null;
            if (root.TryGetProperty("error", out var errorProp))
            {
                if (errorProp.ValueKind == JsonValueKind.Object)
                    errorObjectMessage = ReadString(errorProp, "message");
                else if (errorProp.ValueKind == JsonValueKind.String)
                    errorObjectMessage = errorProp.GetString();
            }

            int? apiStatus = null;
            if (root.TryGetProperty("api_error_status", out var statusElement))
            {
                apiStatus = statusElement.ValueKind switch
                {
                    JsonValueKind.Number when statusElement.TryGetInt32(out var code) => code,
                    JsonValueKind.String when int.TryParse(statusElement.GetString(), out var parsed) => parsed,
                    _ => null,
                };
            }

            entry = new StreamJsonErrorEntry(line, result, message, errorObjectMessage, apiStatus);
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static string? ReadString(JsonElement node, string property)
        => node.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static void AddIfNonEmpty(List<string> list, string? value)
    {
        if (!string.IsNullOrEmpty(value)) list.Add(value);
    }

    internal sealed record TerminalStreamError(string RawLine, string? Message, int? ApiErrorStatus);

    internal sealed record RateLimitRejection(string RawLine, DateTimeOffset? ResetAt, string? RateLimitType);

    private sealed record StreamJsonErrorEntry(
        string RawLine,
        string? Result,
        string? Message,
        string? ErrorObjectMessage,
        int? ApiErrorStatus);
}
