using System.Text.Json;

namespace CodeyBox.Agents.Claude;

/// <summary>
/// Pulls the Claude CLI's session identifier out of a (possibly partial)
/// <c>--output-format stream-json --verbose</c> stdout payload. The first event
/// the CLI emits on every run is a system init line of the shape
/// <c>{"type":"system","subtype":"init","session_id":"...", ...}</c>; subsequent
/// assistant / tool / result events also echo the same id under
/// <c>session_id</c>, so the extractor scans line-by-line and returns the first
/// id it finds. Both snake_case (<c>session_id</c>) and camelCase
/// (<c>sessionId</c>) shapes are accepted because internal claude CLI builds
/// have shipped both at different points.
///
/// <para>
/// All non-JSON, mid-line-truncated, or schema-mismatched lines are ignored —
/// the extractor is used on the stdout of a CRASHED run, which is allowed to be
/// arbitrarily malformed. The extractor never throws and is allocation-cheap
/// (one <see cref="JsonDocument"/> per JSON line, disposed eagerly).
/// </para>
/// </summary>
public static class ClaudeSessionIdExtractor
{
    public static string? Extract(string? stdout)
    {
        if (string.IsNullOrEmpty(stdout))
            return null;

        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] != '{')
                continue;

            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    continue;
                if (doc.RootElement.TryGetProperty("session_id", out var snake)
                    && snake.ValueKind == JsonValueKind.String
                    && snake.GetString() is { Length: > 0 } snakeId)
                {
                    return snakeId;
                }
                if (doc.RootElement.TryGetProperty("sessionId", out var camel)
                    && camel.ValueKind == JsonValueKind.String
                    && camel.GetString() is { Length: > 0 } camelId)
                {
                    return camelId;
                }
            }
            catch (JsonException)
            {
                continue;
            }
            finally
            {
                doc?.Dispose();
            }
        }

        return null;
    }
}
