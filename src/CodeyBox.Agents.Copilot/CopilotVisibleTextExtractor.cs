using System.Text;
using System.Text.Json;

namespace CodeyBox.Agents.Copilot;

/// <summary>
/// Unwraps the Copilot CLI's <c>--output-format json</c> envelope so the
/// orchestrator's plain-text parsers (plan-artifact, check-and-act verdict)
/// see the agent-visible answer text rather than raw NDJSON: the
/// concatenated <c>assistant.message</c> contents. Returns null when the
/// capture carries no JSON events so the caller feeds the raw stdout to its
/// consumer unchanged.
/// </summary>
internal static class CopilotVisibleTextExtractor
{
    public static string? Extract(string? rawStdout)
    {
        if (string.IsNullOrWhiteSpace(rawStdout))
            return null;

        var text = new StringBuilder();
        var sawEvent = false;
        foreach (var rawLine in rawStdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] != '{')
                continue;

            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(line);
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
                    || typeProp.ValueKind != JsonValueKind.String
                    || !CopilotTurnOutcome.IsCopilotEventType(typeProp.GetString()))
                {
                    continue;
                }

                sawEvent = true;
                if (string.Equals(typeProp.GetString(), "assistant.message", StringComparison.OrdinalIgnoreCase)
                    && root.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(content.GetString()))
                {
                    if (text.Length > 0)
                        text.Append('\n');
                    text.Append(content.GetString());
                }
            }
        }

        if (!sawEvent)
            return null;
        return text.Length > 0 ? text.ToString() : string.Empty;
    }
}
