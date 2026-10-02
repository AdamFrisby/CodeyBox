using System.Text.Json;
using CodeyBox.Admin.Web.Models;

namespace CodeyBox.Admin.Web.Services;

/// <summary>
/// Turns a proposal's reviewed change set into the "what this will change"
/// statement the panel shows beside approve/reject. Parsing is defensive:
/// an unfamiliar shape falls back to the tool name rather than a blank
/// line, so review never silently understates a change.
/// </summary>
public static class MajordomoChangeSummarizer
{
    /// <summary>One human line per planned change, titles resolved via <paramref name="titleFor"/>.</summary>
    public static IReadOnlyList<string> Summarize(MajordomoProposalDto proposal, Func<string, string?> titleFor)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(titleFor);
        try
        {
            if (proposal.ReviewedChangeSet is not { } root || root.ValueKind != JsonValueKind.Object)
                return Fallback(proposal);
            if (!TryGet(root, "changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                return Fallback(proposal);
            var lines = new List<string>();
            foreach (var change in changes.EnumerateArray())
            {
                if (change.ValueKind != JsonValueKind.Object)
                    continue;
                lines.Add(Describe(change, titleFor));
            }
            return lines.Count == 0 ? Fallback(proposal) : lines;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return Fallback(proposal);
        }
    }

    /// <summary>Every work-item id the change set names, for title links under the summary.</summary>
    public static IReadOnlyList<string> ItemIds(MajordomoProposalDto proposal)
    {
        try
        {
            if (proposal.ReviewedChangeSet is not { } root || root.ValueKind != JsonValueKind.Object)
                return [];
            if (!TryGet(root, "changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                return [];
            var ids = new List<string>();
            foreach (var change in changes.EnumerateArray())
            {
                if (change.ValueKind != JsonValueKind.Object)
                    continue;
                if (TryGet(change, "id", out var id) && id.ValueKind == JsonValueKind.String
                    && id.GetString() is { Length: > 0 } single)
                    ids.Add(single);
                if (TryGet(change, "parent_id", out var parent) && parent.ValueKind == JsonValueKind.String
                    && parent.GetString() is { Length: > 0 } parentId)
                    ids.Add(parentId);
                if (TryGet(change, "ids", out var many) && many.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in many.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } member)
                            ids.Add(member);
                }
            }
            return ids.Distinct(StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> Fallback(MajordomoProposalDto proposal) =>
        [$"Run {proposal.Tool} (see the arguments on the proposal)"];

    private static string Describe(JsonElement change, Func<string, string?> titleFor)
    {
        var kind = TryGet(change, "kind", out var kindElement) && kindElement.ValueKind == JsonValueKind.String
            ? kindElement.GetString() ?? ""
            : "";
        return kind switch
        {
            "create_item" => TryGet(change, "item", out var item)
                ? $"Create “{ItemTitle(item)}”"
                : "Create a work item",
            "create_chain" => DescribeChain(change),
            "update_item" => $"Update {Ref(change, "id", titleFor)}",
            "cancel_item" => $"Cancel {Ref(change, "id", titleFor)}",
            "cancel_dependents" => DescribeCascade(change, titleFor),
            "retry_item" => TryGet(change, "from", out var from) && from.ValueKind == JsonValueKind.String
                ? $"Retry {Ref(change, "id", titleFor)} from {from.GetString()}"
                : $"Retry {Ref(change, "id", titleFor)}",
            _ => kind.Length == 0 ? "Run the proposed call" : $"Run {kind}",
        };
    }

    private static string DescribeChain(JsonElement change)
    {
        if (!TryGet(change, "nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
            return "Create a dependent chain";
        var titles = new List<string>();
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind == JsonValueKind.Object && TryGet(node, "item", out var item))
                titles.Add(ItemTitle(item));
        }
        return titles.Count == 0
            ? "Create a dependent chain"
            : $"Create a chain of {titles.Count}: {string.Join(" → ", titles.Select(t => $"“{t}”"))}";
    }

    private static string DescribeCascade(JsonElement change, Func<string, string?> titleFor)
    {
        var parent = Ref(change, "parent_id", titleFor);
        var count = TryGet(change, "ids", out var ids) && ids.ValueKind == JsonValueKind.Array
            ? ids.GetArrayLength()
            : 0;
        if (count == 0)
            return $"Cancel {parent}";
        var plural = count == 1 ? string.Empty : "s";
        return $"Cancel {parent} and {count} dependent{plural}";
    }

    private static string Ref(JsonElement change, string property, Func<string, string?> titleFor)
    {
        if (TryGet(change, property, out var id) && id.ValueKind == JsonValueKind.String
            && id.GetString() is { Length: > 0 } raw)
        {
            return $"“{titleFor(raw) ?? raw}”";
        }
        return "the item";
    }

    private static string ItemTitle(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Object
            && TryGet(item, "title", out var title)
            && title.ValueKind == JsonValueKind.String
            && title.GetString() is { Length: > 0 } text)
            return text;
        return "(untitled item)";
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
            return true;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }
}
