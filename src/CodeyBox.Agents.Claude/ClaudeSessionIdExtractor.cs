using System.Text.Json;

namespace CodeyBox.Agents.Claude;

/// <summary>
/// Pulls the Claude CLI's session identifier out of a (possibly partial)
/// <c>--output-format stream-json --verbose</c> stdout payload. The first event
/// the CLI emits on every run is a system init line of the shape
/// <c>{"type":"system","subtype":"init","session_id":"...", ...}</c>; subsequent
/// assistant / tool / result events may echo the same id, but this extractor only
/// accepts the init event so model-controlled stdout cannot spoof a resume target.
/// Both snake_case (<c>session_id</c>) and camelCase
/// (<c>sessionId</c>) shapes are accepted because internal claude CLI builds
/// have shipped both at different points.
///
/// <para>
/// All non-JSON, mid-line-truncated, schema-mismatched, or non-UUID ids are
/// ignored — the extractor is used on the stdout of a CRASHED run, which is
/// allowed to be arbitrarily malformed. The extractor never throws and is
/// allocation-cheap (one <see cref="JsonDocument"/> per JSON line, disposed
/// eagerly).
/// </para>
/// </summary>
internal static class ClaudeSessionIdExtractor
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
                if (!IsInitEvent(doc.RootElement))
                    continue;
                if (TryGetValidSessionId(doc.RootElement, "session_id") is { } snakeId)
                    return snakeId;
                if (TryGetValidSessionId(doc.RootElement, "sessionId") is { } camelId)
                    return camelId;
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

    private static bool IsInitEvent(JsonElement root)
    {
        return root.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
            && string.Equals(type.GetString(), "system", StringComparison.Ordinal)
            && root.TryGetProperty("subtype", out var subtype)
            && subtype.ValueKind == JsonValueKind.String
            && string.Equals(subtype.GetString(), "init", StringComparison.Ordinal);
    }

    private static string? TryGetValidSessionId(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { Length: > 0 } id)
        {
            return null;
        }

        return Guid.TryParseExact(id, "D", out _) ? id : null;
    }
}
