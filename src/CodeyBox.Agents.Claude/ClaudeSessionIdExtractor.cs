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
///
/// <para>
/// Inspection is bounded: only the first <see cref="MaxScannedBytes"/> of
/// stdout and <see cref="MaxScannedLines"/> JSON-looking lines are inspected.
/// The init event is emitted on the CLI's very first stream-json line so a
/// small prefix is more than enough; the caps protect failure handling from
/// pathological prompts that induce massive structured output before crashing.
/// </para>
/// </summary>
internal static class ClaudeSessionIdExtractor
{
    /// <summary>
    /// Cap on the stdout prefix scanned for the init event. The Claude CLI
    /// emits the init event as its FIRST stream-json line; a 64 KiB prefix
    /// covers it many times over while bounding allocation on a crashed run
    /// with arbitrary captured output.
    /// </summary>
    internal const int MaxScannedBytes = 64 * 1024;

    /// <summary>
    /// Cap on the number of JSON-looking lines parsed inside the scanned
    /// prefix. Belt-and-braces backstop for a prefix densely packed with
    /// short JSON fragments.
    /// </summary>
    internal const int MaxScannedLines = 128;

    public static string? Extract(string? stdout)
    {
        if (string.IsNullOrEmpty(stdout))
            return null;

        var scannedSlice = stdout.AsSpan(0, Utf8PrefixCharCount(stdout, MaxScannedBytes));

        var jsonLinesParsed = 0;
        var remaining = scannedSlice;
        while (!remaining.IsEmpty)
        {
            var newlineIndex = remaining.IndexOf('\n');
            ReadOnlySpan<char> rawLine;
            if (newlineIndex < 0)
            {
                rawLine = remaining;
                remaining = default;
            }
            else
            {
                rawLine = remaining[..newlineIndex];
                remaining = remaining[(newlineIndex + 1)..];
            }

            // TrimEnd \r then Trim whitespace; equivalent to the original
            // .TrimEnd('\r').Trim() pair on the string overload.
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] != '{')
                continue;

            if (++jsonLinesParsed > MaxScannedLines)
                break;

            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(line.ToString());
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

    private static int Utf8PrefixCharCount(string value, int maxBytes)
    {
        var bytes = 0;
        for (var i = 0; i < value.Length;)
        {
            var c = value[i];
            var charCount = 1;
            int charBytes;

            if (char.IsHighSurrogate(c)
                && i + 1 < value.Length
                && char.IsLowSurrogate(value[i + 1]))
            {
                charBytes = 4;
                charCount = 2;
            }
            else if (c <= 0x7F)
            {
                charBytes = 1;
            }
            else if (c <= 0x7FF)
            {
                charBytes = 2;
            }
            else
            {
                charBytes = 3;
            }

            if (bytes + charBytes > maxBytes)
                return i;

            bytes += charBytes;
            i += charCount;
        }

        return value.Length;
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
