using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Parses and validates <c>.codeybox/suggestions.json</c> emitted by agents.
/// Invalid entries are dropped with a warning; a bad entry never fails the work item.
/// </summary>
public static class SuggestionsFileParser
{
    private static readonly HashSet<string> ValidCategories =
        ["test-coverage", "refactor", "dead-code", "security", "dependency", "docs", "other"];
    private static readonly HashSet<string> ValidSeverities =
        ["minor", "notable", "important"];
    private static readonly HashSet<string> ValidEfforts =
        ["tiny", "small", "medium", "large"];

    public static IReadOnlyList<SuggestionEntry> Parse(string? json, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            log.LogWarning("suggestions.json is not valid JSON: {Error}", ex.Message);
            return [];
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("suggestions", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
            {
                log.LogWarning("suggestions.json root must be an object with a 'suggestions' array");
                return [];
            }

            const int MaxEntries = 50;
            var results = new List<SuggestionEntry>();
            var idx = 0;
            foreach (var el in arr.EnumerateArray())
            {
                if (idx >= MaxEntries)
                {
                    log.LogWarning("suggestions.json: more than {Max} entries; ignoring the rest", MaxEntries);
                    break;
                }
                var entry = TryParseEntry(el, idx, log);
                if (entry is not null) results.Add(entry);
                idx++;
            }
            return results;
        }
    }

    private static SuggestionEntry? TryParseEntry(JsonElement el, int idx, ILogger log)
    {
        var title = GetString(el, "title");
        if (title is null)
        {
            log.LogWarning("suggestions.json[{I}]: missing required field 'title'; skipping", idx);
            return null;
        }
        if (title.Length > 120)
        {
            log.LogWarning("suggestions.json[{I}]: 'title' exceeds 120 chars ({Len}); skipping", idx, title.Length);
            return null;
        }

        var rationale = GetString(el, "rationale");
        if (rationale is null)
        {
            log.LogWarning("suggestions.json[{I}]: missing required field 'rationale'; skipping", idx);
            return null;
        }
        if (rationale.Length > 2000)
        {
            log.LogWarning("suggestions.json[{I}]: 'rationale' exceeds 2000 chars ({Len}); skipping", idx, rationale.Length);
            return null;
        }

        var category = GetString(el, "category");
        if (category is null)
        {
            log.LogWarning("suggestions.json[{I}]: missing required field 'category'; skipping", idx);
            return null;
        }
        if (!ValidCategories.Contains(category))
        {
            log.LogWarning("suggestions.json[{I}]: invalid 'category' '{V}'; skipping", idx, category);
            return null;
        }

        var severity = GetString(el, "severity");
        if (severity is null)
        {
            log.LogWarning("suggestions.json[{I}]: missing required field 'severity'; skipping", idx);
            return null;
        }
        if (!ValidSeverities.Contains(severity))
        {
            log.LogWarning("suggestions.json[{I}]: invalid 'severity' '{V}'; skipping", idx, severity);
            return null;
        }

        var effort = GetString(el, "estimatedEffort");
        if (effort is null)
        {
            log.LogWarning("suggestions.json[{I}]: missing required field 'estimatedEffort'; skipping", idx);
            return null;
        }
        if (!ValidEfforts.Contains(effort))
        {
            log.LogWarning("suggestions.json[{I}]: invalid 'estimatedEffort' '{V}'; skipping", idx, effort);
            return null;
        }

        const int MaxPathLength = 500;
        var files = new List<string>();
        if (el.TryGetProperty("filesReferenced", out var filesEl)
            && filesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in filesEl.EnumerateArray())
            {
                if (f.ValueKind != JsonValueKind.String) continue;
                var path = f.GetString();
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (path.Length > MaxPathLength)
                {
                    log.LogWarning("suggestions.json[{I}]: filesReferenced entry exceeds {Max} chars; skipping", idx, MaxPathLength);
                    continue;
                }
                if (path.StartsWith('/') || path.Contains("..") || path.Contains('\0'))
                {
                    log.LogWarning("suggestions.json[{I}]: filesReferenced entry rejected (absolute path, traversal, or NUL); skipping", idx);
                    continue;
                }
                files.Add(path);
            }
        }

        return new SuggestionEntry(title, rationale, category, severity, effort, files);
    }

    private static string? GetString(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString();
        if (string.IsNullOrWhiteSpace(s)) return null;
        return s.ReplaceLineEndings(" ");
    }
}

public sealed record SuggestionEntry(
    string Title,
    string Rationale,
    string Category,
    string Severity,
    string EstimatedEffort,
    IReadOnlyList<string> FilesReferenced);
