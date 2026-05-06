using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Parses <c>&lt;codeybox-question id="..."&gt;...&lt;/codeybox-question&gt;</c> blocks
/// from agent stdout. Malformed blocks are ignored with a warning.
/// </summary>
public static partial class QuestionParser
{
    // id must be alphanumeric + hyphens/underscores, ≤ 64 chars.
    private static readonly Regex IdPattern =
        new(@"^[a-zA-Z0-9_-]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Multi-line tolerant match of <codeybox-question id="...">...</codeybox-question>.
    [GeneratedRegex(
        @"<codeybox-question\s+id=""([^""]{1,64})""\s*>([\s\S]*?)</codeybox-question>",
        RegexOptions.CultureInvariant)]
    private static partial Regex BlockPattern();

    /// <summary>
    /// Extracts all valid question blocks from <paramref name="stdout"/>.
    /// Returns an empty list when none are found or stdout is null/empty.
    /// </summary>
    public static IReadOnlyList<ParsedQuestion> Parse(string? stdout, ILogger log)
    {
        if (string.IsNullOrEmpty(stdout)) return [];

        var searchText = BuildQuestionSearchText(stdout);
        var results = new List<ParsedQuestion>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in BlockPattern().Matches(searchText))
        {
            var rawId = m.Groups[1].Value;
            var rawText = m.Groups[2].Value.Trim();

            if (!IdPattern.IsMatch(rawId))
            {
                log.LogWarning(
                    "codeybox-question: invalid id '{Id}' (must be alphanumeric/hyphen/underscore, ≤ 64 chars); ignoring",
                    rawId.ReplaceLineEndings("\\n"));
                continue;
            }

            if (string.IsNullOrWhiteSpace(rawText))
            {
                log.LogWarning("codeybox-question id='{Id}': empty question text; ignoring", rawId);
                continue;
            }

            if (!seenIds.Add(rawId))
            {
                // Duplicate within same stdout — only count once.
                continue;
            }

            // Redact any accidentally embedded secret tokens.
            var redactedText = RawOutputRedactor.Redact(rawText);

            // Truncate to a reasonable maximum.
            const int MaxTextChars = 4000;
            if (redactedText.Length > MaxTextChars)
                redactedText = redactedText[..MaxTextChars] + " [truncated]";

            results.Add(new ParsedQuestion(rawId, redactedText));
        }

        return results;
    }

    private static string BuildQuestionSearchText(string stdout)
    {
        var extractedJsonText = ExtractJsonStringValues(stdout);
        return extractedJsonText.Length == 0
            ? stdout
            : stdout + "\n" + extractedJsonText;
    }

    private static string ExtractJsonStringValues(string stdout)
    {
        var builder = new StringBuilder();
        foreach (var rawLine in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (rawLine.Length == 0 || rawLine[0] is not ('{' or '['))
                continue;

            try
            {
                using var document = JsonDocument.Parse(rawLine);
                AppendAssistantTextValues(document.RootElement, builder);
            }
            catch (JsonException)
            {
                // Plain agent stdout and malformed stream lines are ignored here;
                // the existing plain-text parser still handles literal blocks.
            }
        }

        return builder.ToString();
    }

    private static void AppendAssistantTextValues(JsonElement element, StringBuilder builder)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                AppendAssistantTextValues(item, builder);
            return;
        }

        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("type", out var type) ||
            !StringEquals(type, "assistant"))
            return;

        if (element.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.Object &&
            (!message.TryGetProperty("role", out var role) || StringEquals(role, "assistant")) &&
            message.TryGetProperty("content", out var content))
        {
            AppendTextContent(content, builder);
            return;
        }

        if (element.TryGetProperty("content", out var directContent))
            AppendTextContent(directContent, builder);

        if (element.TryGetProperty("text", out var directText) &&
            directText.ValueKind == JsonValueKind.String)
            AppendString(directText, builder);
    }

    private static void AppendTextContent(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                AppendString(element, builder);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    AppendTextContent(item, builder);
                break;
            case JsonValueKind.Object:
                if (element.TryGetProperty("type", out var type) &&
                    !StringEquals(type, "text"))
                    break;
                if (element.TryGetProperty("text", out var text) &&
                    text.ValueKind == JsonValueKind.String)
                    AppendString(text, builder);
                break;
        }
    }

    private static bool StringEquals(JsonElement element, string value) =>
        element.ValueKind == JsonValueKind.String &&
        string.Equals(element.GetString(), value, StringComparison.OrdinalIgnoreCase);

    private static void AppendString(JsonElement element, StringBuilder builder)
    {
        var value = element.GetString();
        if (!string.IsNullOrEmpty(value))
            builder.AppendLine(value);
    }
}

/// <summary>
/// A successfully parsed question block, ready for persistence.
/// </summary>
public sealed record ParsedQuestion(string QuestionId, string QuestionText);
