using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Claude;

/// <summary>
/// Best-effort parser for Claude Code's <c>--output-format stream-json</c> stdout.
/// Returns null when the output is not in NDJSON format (e.g. from <c>--print</c>).
///
/// Per-tool durations are not available from buffered output. The capture path
/// persists stream-json losslessly and intentionally skips this immediate
/// parser; a follow-up analyzer reads the persisted files.
/// </summary>
public sealed class ClaudeToolCallCounter : IAgentToolCallCounter
{
    public AgentKind Kind => AgentKind.Claude;

    public AgentToolCallCounts? TryCount(string? bufferedStdout) => TryParse(bufferedStdout);

    /// <summary>
    /// Attempts to parse <paramref name="stdout"/> as Claude stream-json NDJSON.
    /// Returns null if the content is not recognisable as stream-json.
    /// Malformed individual lines are silently skipped.
    /// </summary>
    public static AgentToolCallCounts? TryParse(string? stdout)
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
            }
            catch (InvalidOperationException)
            {
            }
        }

        if (toolCounts.Count == 0 && finalText is null) return null;
        return new AgentToolCallCounts(toolCounts, finalText);
    }
}
