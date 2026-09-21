using System.Text.RegularExpressions;

namespace CodeyBox.Admin.Model;

/// <summary>
/// Splits a pasted plan into chain items. Work is authored as a numbered
/// plan somewhere else and then transcribed, so the parser looks for the
/// seams a human already put in the text — ATX headings, numbered markers
/// ("1.", "2)", "Step 3:", "3/7"), horizontal rules, and explicit
/// "depends on: 1, 3" lines — and defaults to a straight line when there
/// is none. Prose with no structure yields exactly one item, never a bad
/// chain, and text before the first marker is never dropped: it becomes
/// the first item's preamble.
///
/// The parser also reports how confident it is that the text is a plan
/// at all (<see cref="PlanConfidence"/>): a long single prompt with
/// "## Context" and "## Acceptance criteria" sections has headings too,
/// and filing that as a two-item chain would be worse than filing a plan
/// as one item. The composer defaults from the confidence; the operator
/// decides.
///
/// Pure over its input; all bounds are enforced before buffering.
/// </summary>
public static partial class PlanChainParser
{
    /// <summary>Maximum pasted characters examined; the rest is ignored.</summary>
    public const int MaxInputLength = 400_000;

    /// <summary>Maximum items produced; further markers stay body text.</summary>
    public const int MaxItems = 200;

    /// <summary>Maximum title characters kept per item.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>Maximum body characters kept per item.</summary>
    public const int MaxBodyLength = 40_000;

    /// <summary>Parses <paramref name="text"/> into a chain preview.</summary>
    public static ParsedPlan Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ParsedPlan([], false);
        }

        var bounded = text.Length > MaxInputLength ? text[..MaxInputLength] : text;
        var lines = bounded.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var starts = FindItemStarts(lines);
        if (starts.Count == 0)
        {
            return SingleItem(bounded.Trim());
        }

        // Anything before the first seam is a preamble: a lone "# Plan
        // title" names the plan, and a paragraph of prose ahead of the
        // numbering is context. Either way it belongs to the first item's
        // body, not on the floor.
        var preamble = new List<string>();
        var firstStartLine = starts[0].LineIndex;
        for (var i = 0; i < firstStartLine; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
            {
                preamble.Add(lines[i].TrimEnd());
            }
        }

        var leadingProse = preamble.Count > 0;
        if (starts[0].IsH1Title && starts.Count > 1)
        {
            preamble.Add(lines[firstStartLine].Trim());
            starts.RemoveAt(0);
        }

        var anyNumbered = false;
        var anyRule = false;
        var items = new List<ParsedPlanItem>(starts.Count);
        for (var i = 0; i < starts.Count && items.Count < MaxItems; i++)
        {
            var start = starts[i];
            var end = i + 1 < starts.Count ? starts[i + 1].LineIndex : lines.Length;
            var chunk = lines[start.LineIndex..end];
            var item = BuildItem(items.Count + 1, chunk, i == 0 ? preamble : null);
            if (item is not null)
            {
                items.Add(item);
                anyNumbered |= start.IsNumbered;
                anyRule |= start.AfterRule;
            }
        }

        if (items.Count == 0)
        {
            return SingleItem(bounded.Trim());
        }

        var plan = ApplyEdges(items);
        var confidence = plan.Items.Count <= 1
            ? PlanConfidence.None
            : !leadingProse && (anyNumbered || anyRule || plan.HadExplicitEdges)
                ? PlanConfidence.Structured
                : PlanConfidence.Suggested;
        return plan with { Confidence = confidence };
    }

    private static ParsedPlan SingleItem(string text)
    {
        if (text.Length == 0)
        {
            return new ParsedPlan([], false);
        }

        var newline = text.IndexOf('\n');
        var firstLine = (newline < 0 ? text : text[..newline]).Trim();
        var rest = newline < 0 ? string.Empty : text[(newline + 1)..].Trim();
        var title = Truncate(CollapseWhitespace(StripMarkerPrefix(firstLine)), MaxTitleLength);
        // Dependency lines name siblings that do not exist in a single
        // item; drop them from the body rather than filing a bad edge.
        var body = Truncate(TrimBlankEnds(string.Join('\n',
            rest.Split('\n').Where(l => !DependsOnRegex().IsMatch(l)))), MaxBodyLength);
        if (title.Length == 0)
        {
            title = body.Length == 0 ? "Untitled item" : Truncate(CollapseWhitespace(body), MaxTitleLength);
            body = string.Empty;
        }

        return new ParsedPlan([new ParsedPlanItem(1, title, body, [])], false);
    }

    private static ParsedPlan ApplyEdges(List<ParsedPlanItem> items)
    {
        var count = items.Count;
        var explicitEdges = new List<HashSet<int>>(count);
        for (var i = 0; i < count; i++)
        {
            explicitEdges.Add([]);
        }

        var anyExplicit = false;
        for (var i = 0; i < count; i++)
        {
            foreach (var dep in items[i].DependsOn)
            {
                if (dep >= 1 && dep <= count && dep != items[i].Number)
                {
                    if (explicitEdges[i].Add(dep))
                    {
                        anyExplicit = true;
                    }
                }
            }
        }

        if (!anyExplicit)
        {
            var chained = new List<ParsedPlanItem>(count);
            for (var i = 0; i < count; i++)
            {
                var deps = i == 0 ? [] : new List<int> { items[i - 1].Number };
                chained.Add(items[i] with { DependsOn = deps });
            }

            return new ParsedPlan(chained, false);
        }

        var resolved = new List<ParsedPlanItem>(count);
        for (var i = 0; i < count; i++)
        {
            resolved.Add(items[i] with
            {
                DependsOn = explicitEdges[i].OrderBy(d => d).ToList(),
            });
        }

        return new ParsedPlan(resolved, true);
    }

    private sealed record ItemStart(int LineIndex, bool IsH1Title, bool IsNumbered, bool AfterRule);

    private static List<ItemStart> FindItemStarts(string[] lines)
    {
        var starts = new List<ItemStart>();
        // A plan that uses rules as seams opens with its first section, not
        // a preamble: the rule after the opening lines says they were one.
        var pendingRule = lines.Any(l => RuleRegex().IsMatch(l.Trim()));
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var trimmed = line.Trim();
            if (RuleRegex().IsMatch(trimmed))
            {
                // A rule is a seam, not content: the next non-blank line
                // opens an item even when it carries no marker of its own.
                pendingRule = true;
                continue;
            }

            var heading = HeadingRegex().Match(trimmed);
            if (heading.Success)
            {
                var headingText = trimmed[heading.Groups[1].Length..].TrimStart();
                starts.Add(new ItemStart(
                    i,
                    heading.Groups[1].Value.Length == 1,
                    NumberedRegex().IsMatch(headingText),
                    pendingRule));
                pendingRule = false;
                continue;
            }

            if (NumberedRegex().IsMatch(line))
            {
                starts.Add(new ItemStart(i, false, true, pendingRule));
                pendingRule = false;
                continue;
            }

            if (pendingRule)
            {
                starts.Add(new ItemStart(i, false, false, true));
                pendingRule = false;
            }
        }

        return starts;
    }

    private static ParsedPlanItem? BuildItem(int number, string[] chunk, List<string>? preamble)
    {
        var titleLine = chunk.Length == 0 ? string.Empty : chunk[0].Trim();
        var title = Truncate(CollapseWhitespace(StripMarkerPrefix(titleLine)), MaxTitleLength);

        var bodyLines = new List<string>();
        if (preamble is { Count: > 0 })
        {
            bodyLines.AddRange(preamble);
            bodyLines.Add(string.Empty);
        }

        var deps = new List<int>();
        for (var i = 1; i < chunk.Length; i++)
        {
            var line = chunk[i];
            if (RuleRegex().IsMatch(line.Trim()))
            {
                continue;
            }

            var depMatch = DependsOnRegex().Match(line);
            if (depMatch.Success)
            {
                deps.AddRange(ParseDepNumbers(depMatch.Groups[1].Value));
                continue;
            }

            bodyLines.Add(line);
        }

        var body = Truncate(TrimBlankEnds(string.Join('\n', bodyLines)), MaxBodyLength);
        if (title.Length == 0 && body.Length == 0)
        {
            return null;
        }

        if (title.Length == 0)
        {
            title = Truncate(CollapseWhitespace(body), MaxTitleLength);
        }

        return new ParsedPlanItem(number, title, body, deps);
    }

    private static IEnumerable<int> ParseDepNumbers(string fragment)
    {
        foreach (Match m in DepNumberRegex().Matches(fragment))
        {
            if (int.TryParse(m.Groups[1].Value, out var n))
            {
                yield return n;
            }
        }
    }

    private static string StripMarkerPrefix(string line)
    {
        var heading = HeadingRegex().Match(line);
        if (heading.Success)
        {
            line = line[heading.Groups[1].Length..].TrimStart();
        }

        var numbered = TitleNumberPrefixRegex().Match(line);
        if (numbered.Success)
        {
            line = line[numbered.Length..].Trim();
        }

        return line.TrimEnd(':').Trim();
    }

    private static string CollapseWhitespace(string value) =>
        WhitespaceRegex().Replace(value, " ").Trim();

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max].TrimEnd();

    private static string TrimBlankEnds(string value)
    {
        var lines = value.Split('\n');
        var start = 0;
        while (start < lines.Length && string.IsNullOrWhiteSpace(lines[start]))
        {
            start++;
        }

        var end = lines.Length;
        while (end > start && string.IsNullOrWhiteSpace(lines[end - 1]))
        {
            end--;
        }

        return string.Join('\n', lines[start..end]);
    }

    [GeneratedRegex(@"^(#{1,6})\s+\S", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\s*(?:\(?\d{1,3}\)?[.)\]:]\s+|Step\s+\d{1,3}\s*[:.\-–—]?\s+|\d{1,3}\s*/\s*\d{1,3}\s*[:.\-–—]?\s+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NumberedRegex();

    [GeneratedRegex(@"^(?:\(?\d{1,3}\)?[.)\]:]|Step\s+\d{1,3}\s*[:.\-–—]?|\d{1,3}\s*/\s*\d{1,3}\s*[:.\-–—]?)\s*", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TitleNumberPrefixRegex();

    [GeneratedRegex(@"^\s*(?:-{3,}|\*{3,}|_{3,})\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex RuleRegex();

    [GeneratedRegex(@"^\s*(?:depends?\s+on|requires?|blocked\s+by|after)\s*:\s*(.+?)\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DependsOnRegex();

    [GeneratedRegex(@"(?:^|[\s,;(\[])(?:#|item\s+|step\s+)?(\d{1,3})(?=[\s,;.)\]]|$)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DepNumberRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
