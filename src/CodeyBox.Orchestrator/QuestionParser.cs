using System.Text.RegularExpressions;
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

        var results = new List<ParsedQuestion>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in BlockPattern().Matches(stdout))
        {
            var rawId = m.Groups[1].Value;
            var rawText = m.Groups[2].Value.Trim();

            if (!IdPattern.IsMatch(rawId))
            {
                log.LogWarning(
                    "codeybox-question: invalid id '{Id}' (must be alphanumeric/hyphen/underscore, ≤ 64 chars); ignoring",
                    rawId);
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
}

/// <summary>
/// A successfully parsed question block, ready for persistence.
/// </summary>
public sealed record ParsedQuestion(string QuestionId, string QuestionText);
