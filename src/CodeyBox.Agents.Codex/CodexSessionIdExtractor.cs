using System.Text.Json;

namespace CodeyBox.Agents.Codex;

/// <summary>
/// Pulls the Codex CLI resume id from structured <c>codex exec --json</c>
/// output. Current Codex builds persist a UUID session id in session metadata;
/// older/event-oriented shapes may expose thread or conversation identifiers.
/// </summary>
internal static class CodexSessionIdExtractor
{
    internal const int MaxScannedBytes = 64 * 1024;
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

            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] != '{')
                continue;

            if (++jsonLinesParsed > MaxScannedLines)
                break;

            try
            {
                using var doc = JsonDocument.Parse(line.ToString());
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    continue;
                if (TryExtractFromObject(doc.RootElement, allowGenericId: false) is { } id)
                    return id;
            }
            catch (JsonException)
            {
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

    private static string? TryExtractFromObject(JsonElement root, bool allowGenericId)
    {
        if (allowGenericId && TryGetValidId(root, "id") is { } id)
            return id;
        if (TryGetValidId(root, "session_id") is { } sessionId)
            return sessionId;
        if (TryGetValidId(root, "sessionId") is { } camelSessionId)
            return camelSessionId;
        if (TryGetValidId(root, "thread_id") is { } threadId)
            return threadId;
        if (TryGetValidId(root, "conversation_id") is { } conversationId)
            return conversationId;

        var childMayUseGenericId = IsSessionMetadata(root) || IsThreadStarted(root);
        if (root.TryGetProperty("payload", out var payload)
            && payload.ValueKind == JsonValueKind.Object
            && TryExtractFromObject(payload, childMayUseGenericId) is { } payloadId)
            return payloadId;

        if (root.TryGetProperty("msg", out var msg)
            && msg.ValueKind == JsonValueKind.Object
            && TryExtractFromObject(msg, childMayUseGenericId) is { } msgId)
            return msgId;

        return null;
    }

    private static bool IsSessionMetadata(JsonElement root)
        => TryReadType(root) is { } type
            && string.Equals(type, "session_meta", StringComparison.OrdinalIgnoreCase);

    private static bool IsThreadStarted(JsonElement root)
        => TryReadType(root) is { } type
            && (string.Equals(type, "thread.started", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "thread.created", StringComparison.OrdinalIgnoreCase));

    private static string? TryReadType(JsonElement root)
        => root.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
            ? type.GetString()
            : null;

    private static string? TryGetValidId(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { Length: > 0 } id)
            return null;

        return IsValidResumeId(id) ? id : null;
    }

    private static bool IsValidResumeId(string id)
    {
        if (Guid.TryParseExact(id, "D", out _))
            return true;
        if (id.Length > 200)
            return false;

        // Reject ids that begin with '-' so a model-controllable JSON line
        // cannot smuggle a clap flag through the positional session-id
        // argument of `codex exec resume`.
        if (id[0] == '-')
            return false;

        foreach (var c in id)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
                return false;
        }

        return true;
    }
}
