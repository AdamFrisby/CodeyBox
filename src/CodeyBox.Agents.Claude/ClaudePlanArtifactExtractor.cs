using System.Text;
using System.Text.Json;

namespace CodeyBox.Agents.Claude;

/// <summary>
/// Pulls the agent-visible plan text out of Claude's stream-json NDJSON
/// envelope. Returns <c>null</c> when no stream-json events were observed so
/// the orchestrator can fall back to feeding the raw stdout to the
/// plan-artifact parser unchanged.
/// </summary>
public static class ClaudePlanArtifactExtractor
{
    public static string? Extract(string? rawStdout)
    {
        if (string.IsNullOrWhiteSpace(rawStdout))
            return null;

        var assistantText = new StringBuilder();
        var resultText = new StringBuilder();
        var sawStreamEvent = false;
        foreach (var rawLine in rawStdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!rawLine.StartsWith('{'))
                continue;

            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(rawLine);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("type", out var typeProp)
                    || typeProp.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var type = typeProp.GetString();
                if (type == "assistant")
                {
                    sawStreamEvent = true;
                    if (root.TryGetProperty("message", out var message))
                        AppendContentText(message, assistantText);
                    else
                        AppendContentText(root, assistantText);
                }
                else if (type == "result")
                {
                    sawStreamEvent = true;
                    if (root.TryGetProperty("result", out var result)
                        && result.ValueKind == JsonValueKind.String)
                    {
                        AppendTextPart(resultText, result.GetString());
                    }
                }
            }
        }

        if (!sawStreamEvent)
            return null;
        if (assistantText.Length > 0)
            return assistantText.ToString();
        if (resultText.Length > 0)
            return resultText.ToString();
        return string.Empty;
    }

    private static void AppendContentText(JsonElement container, StringBuilder destination)
    {
        if (!container.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object)
                continue;
            if (part.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                AppendTextPart(destination, text.GetString());
            }
        }
    }

    private static void AppendTextPart(StringBuilder destination, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;
        if (destination.Length > 0)
            destination.AppendLine();
        destination.Append(value);
    }
}
