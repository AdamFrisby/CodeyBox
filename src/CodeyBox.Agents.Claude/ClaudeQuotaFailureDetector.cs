using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Claude;

/// <summary>
/// Recognises quota / rate-limit / auth failures emitted by the Claude Code CLI.
///
/// Sources scanned:
/// <list type="bullet">
///   <item>stderr / stdout text (e.g. <c>rate_limit_exceeded</c>, <c>API Error: 401</c>).</item>
///   <item>Stream-json error events: <c>{"type":"result","is_error":true,"result":"..."}</c>
///         (with optional <c>subtype:"error"</c>).</item>
/// </list>
/// </summary>
public sealed class ClaudeQuotaFailureDetector : IAgentQuotaFailureDetector
{
    public AgentKind Kind => AgentKind.Claude;

    private static readonly (string Pattern, QuotaFailureKind Kind)[] Patterns =
    [
        ("rate_limit_exceeded", QuotaFailureKind.RateLimitExceeded),
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
    /// Walks NDJSON lines and returns inner error messages from Claude's
    /// stream-json error result events.
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
                if (!root.TryGetProperty("type", out var typeProp)) continue;
                if (typeProp.GetString() != "result") continue;

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
                if (!isError) continue;

                AddIfNonEmpty(messages, ReadString(root, "result"));
                AddIfNonEmpty(messages, ReadString(root, "message"));
                if (root.TryGetProperty("error", out var errorProp))
                {
                    if (errorProp.ValueKind == JsonValueKind.Object)
                        AddIfNonEmpty(messages, ReadString(errorProp, "message"));
                    else if (errorProp.ValueKind == JsonValueKind.String)
                        AddIfNonEmpty(messages, errorProp.GetString());
                }
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
