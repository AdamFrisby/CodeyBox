using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Codex;

/// <summary>
/// Recognises quota / rate-limit / auth failures emitted by the OpenAI Codex CLI
/// (which surfaces ChatGPT/usage-limit text).
///
/// Sources scanned:
/// <list type="bullet">
///   <item>stderr / stdout text (e.g. <c>hit your usage limit</c>, <c>API Error: 401</c>).</item>
///   <item>Stream-json error events: <c>{"type":"error","message":"..."}</c>
///         and the wrapped <c>{"msg":{"type":"error","message":"..."}}</c> shape.</item>
/// </list>
/// </summary>
public sealed class CodexQuotaFailureDetector : IAgentQuotaFailureDetector
{
    public AgentKind Kind => AgentKind.Codex;

    private static readonly (string Pattern, QuotaFailureKind Kind)[] Patterns =
    [
        ("hit your usage limit", QuotaFailureKind.LimitReached),
        ("hit your limit", QuotaFailureKind.LimitReached),
        // Codex CLI relays OpenAI's API errors verbatim; rate_limit_exceeded is the
        // canonical token for both Anthropic + OpenAI 429s. Without this pattern the
        // pipeline's mid-iteration fallback (CB-12) doesn't trigger on codex 429s.
        ("rate_limit_exceeded", QuotaFailureKind.RateLimitExceeded),
        // Codex CLI sometimes prints the raw HTTP status to stderr on quota
        // exhaustion before exiting non-zero, with no other quota keywords.
        ("429 Too Many Requests", QuotaFailureKind.RateLimitExceeded),
        ("API Error: 401", QuotaFailureKind.Unauthorized),
    ];

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

        return null;
    }

    /// <summary>
    /// Walks NDJSON lines and returns inner error messages from Codex's
    /// stream-json error events, unwrapping the <c>{"msg":{...}}</c> envelope.
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
                var node = doc.RootElement;

                if (node.ValueKind == JsonValueKind.Object
                    && node.TryGetProperty("msg", out var msg)
                    && msg.ValueKind == JsonValueKind.Object)
                {
                    node = msg;
                }

                if (!node.TryGetProperty("type", out var typeProp)) continue;
                if (typeProp.GetString() != "error") continue;

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
}
