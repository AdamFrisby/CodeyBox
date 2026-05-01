using System.Text.Json;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Best-effort parser for Claude Code's <c>--output-format stream-json</c> stdout.
/// Returns null when the output is not in NDJSON format (e.g. from <c>--print</c>).
///
/// Per-tool durations are not available from buffered output (no per-event timestamps
/// in the stream-json format). This parser extracts tool call names and counts only.
/// Callers use the counts to emit <c>agent.tool_call.*</c> rows with <c>duration_ms = 0</c>
/// and count in metadata, and to emit <c>agent.thinking_aggregate</c> equal to the full
/// exec duration. See docs/timings.md §"Tool call counts".
/// </summary>
public static class AgentStreamJsonParser
{
    public sealed record ParseResult(
        IReadOnlyDictionary<string, int> ToolCallCounts,
        string? FinalText);

    /// <summary>
    /// Attempts to parse <paramref name="stdout"/> as Claude stream-json NDJSON.
    /// Returns null if the content is not recognisable as stream-json.
    /// Malformed individual lines are silently skipped.
    /// </summary>
    public static ParseResult? TryParse(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;

        // Quick guard: the first non-empty line must start with '{' to be NDJSON.
        var firstNonEmpty = stdout.AsSpan().TrimStart();
        if (firstNonEmpty.IsEmpty || firstNonEmpty[0] != '{') return null;

        var toolCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        string? finalText = null;

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) continue;
                var type = typeProp.GetString();

                if (type == "assistant")
                {
                    // Extract tool_use items from message.content array.
                    if (!root.TryGetProperty("message", out var msg)) continue;
                    if (!msg.TryGetProperty("content", out var content)) continue;
                    if (content.ValueKind != JsonValueKind.Array) continue;

                    foreach (var item in content.EnumerateArray())
                    {
                        if (!item.TryGetProperty("type", out var itemType)) continue;
                        if (itemType.GetString() != "tool_use") continue;
                        if (!item.TryGetProperty("name", out var nameProp)) continue;
                        var toolName = nameProp.GetString() ?? "unknown";
                        toolCounts[toolName] = toolCounts.TryGetValue(toolName, out var c) ? c + 1 : 1;
                    }
                }
                else if (type == "result")
                {
                    if (root.TryGetProperty("result", out var resultProp))
                        finalText = resultProp.GetString();
                }
            }
            catch (JsonException)
            {
                // Skip malformed JSON lines — best-effort, never throw.
            }
            catch (InvalidOperationException)
            {
                // Skip lines where a JSON element type doesn't match expected.
            }
        }

        // Only return a result if we found at least something recognisable.
        if (toolCounts.Count == 0 && finalText is null) return null;
        return new ParseResult(toolCounts, finalText);
    }
}
