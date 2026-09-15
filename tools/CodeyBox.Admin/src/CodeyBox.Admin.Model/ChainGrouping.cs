using System.Text;

namespace CodeyBox.Admin.Model;

/// <summary>
/// Recognises the numbered batch-title series operators file work under
/// ("Deployment verification 3/3", "Decompose PipelineRunner #1",
/// "Test selection (RTS) 4/7") so batches join a chain even when the
/// dependency edges were never wired. Only explicit series markers count —
/// a bare trailing number ("Fix login 2") is coincidental numbering and
/// never joins.
/// </summary>
public static class TitleSeries
{
    /// <summary>
    /// Tries to split <paramref name="title"/> into its series key and
    /// display prefix. Returns false for titles without a series marker.
    /// Pure over its input; bounded by the caller's title-length cap.
    /// </summary>
    public static bool TryParse(string? title, out string key, out string displayPrefix)
    {
        key = string.Empty;
        displayPrefix = string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var text = title.Trim();
        string? prefix = TryStripHashMarker(text)
            ?? TryStripFractionMarker(text)
            ?? TryStripParenthesizedFractionMarker(text);
        if (prefix is null)
        {
            return false;
        }

        displayPrefix = CollapseWhitespace(prefix);
        if (displayPrefix.Length == 0)
        {
            return false;
        }

        key = displayPrefix.ToUpperInvariant();
        return true;
    }

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var inGap = true;
        foreach (var ch in value.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!inGap)
                {
                    builder.Append(' ');
                    inGap = true;
                }
            }
            else
            {
                builder.Append(ch);
                inGap = false;
            }
        }
        return builder.ToString();
    }

    private static bool IsDigit(char ch) => ch is >= '0' and <= '9';

    // "Decompose PipelineRunner #1": trailing digits, then '#', then whitespace + prefix.
    private static string? TryStripHashMarker(string text)
    {
        var end = text.Length;
        while (end > 0 && IsDigit(text[end - 1]))
        {
            end--;
        }
        if (end == text.Length || end == 0 || text[end - 1] != '#')
        {
            return null;
        }
        var hash = end - 1;
        if (hash == 0 || !char.IsWhiteSpace(text[hash - 1]))
        {
            return null;
        }
        return text[..hash];
    }

    // "Deployment verification 3/3", "Test selection (RTS) 4/7": trailing N/M.
    private static string? TryStripFractionMarker(string text)
    {
        var end = text.Length;
        while (end > 0 && IsDigit(text[end - 1]))
        {
            end--;
        }
        if (end == text.Length)
        {
            return null;
        }
        var slash = end;
        while (slash > 0 && char.IsWhiteSpace(text[slash - 1]))
        {
            slash--;
        }
        if (slash == 0 || text[slash - 1] != '/')
        {
            return null;
        }
        var numeratorEnd = slash - 1;
        while (numeratorEnd > 0 && char.IsWhiteSpace(text[numeratorEnd - 1]))
        {
            numeratorEnd--;
        }
        var numeratorStart = numeratorEnd;
        while (numeratorStart > 0 && IsDigit(text[numeratorStart - 1]))
        {
            numeratorStart--;
        }
        if (numeratorStart == numeratorEnd)
        {
            return null;
        }
        if (numeratorStart == 0 || !char.IsWhiteSpace(text[numeratorStart - 1]))
        {
            return null;
        }
        return text[..numeratorStart];
    }

    // "Foo (2/5)": trailing parenthesized N/M, whitespace before '(' optional.
    private static string? TryStripParenthesizedFractionMarker(string text)
    {
        if (!text.EndsWith(")", StringComparison.Ordinal))
        {
            return null;
        }
        var inner = text[..^1].TrimEnd();
        var end = inner.Length;
        while (end > 0 && IsDigit(inner[end - 1]))
        {
            end--;
        }
        if (end == inner.Length)
        {
            return null;
        }
        var slash = end;
        while (slash > 0 && char.IsWhiteSpace(inner[slash - 1]))
        {
            slash--;
        }
        if (slash == 0 || inner[slash - 1] != '/')
        {
            return null;
        }
        var numeratorEnd = slash - 1;
        while (numeratorEnd > 0 && char.IsWhiteSpace(inner[numeratorEnd - 1]))
        {
            numeratorEnd--;
        }
        var numeratorStart = numeratorEnd;
        while (numeratorStart > 0 && IsDigit(inner[numeratorStart - 1]))
        {
            numeratorStart--;
        }
        if (numeratorStart == numeratorEnd)
        {
            return null;
        }
        var open = numeratorStart;
        while (open > 0 && char.IsWhiteSpace(inner[open - 1]))
        {
            open--;
        }
        if (open == 0 || inner[open - 1] != '(')
        {
            return null;
        }
        return text[..(open - 1)];
    }
}

/// <summary>
/// A group of work items that belong together: the connected component of
/// the dependency graph, unioned with numbered title-series membership, over
/// one shared disjoint-set structure — so a chain that is half explicit
/// edges and half title series is still one chain.
/// </summary>
public sealed record WorkChain
{
    public required string Id { get; init; }

    public IReadOnlyList<string> ItemIds { get; init; } = [];

    /// <summary>
    /// Series display prefix when every marked member shares one; otherwise null.
    /// </summary>
    public string? SeriesPrefix { get; init; }
}

internal sealed class DisjointSet
{
    private readonly Dictionary<string, string> _parent;
    private readonly Dictionary<string, int> _rank;

    public DisjointSet(IEnumerable<string> members)
    {
        _parent = new Dictionary<string, string>(StringComparer.Ordinal);
        _rank = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            if (_parent.ContainsKey(member))
            {
                continue;
            }
            _parent[member] = member;
            _rank[member] = 0;
        }
    }

    public bool Contains(string member) => _parent.ContainsKey(member);

    public void Union(string left, string right)
    {
        var rootLeft = Find(left);
        var rootRight = Find(right);
        if (rootLeft == rootRight)
        {
            return;
        }
        if (_rank[rootLeft] < _rank[rootRight])
        {
            _parent[rootLeft] = rootRight;
        }
        else if (_rank[rootLeft] > _rank[rootRight])
        {
            _parent[rootRight] = rootLeft;
        }
        else
        {
            _parent[rootRight] = rootLeft;
            _rank[rootLeft]++;
        }
    }

    public string Find(string member)
    {
        var root = member;
        while (_parent[root] != root)
        {
            root = _parent[root];
        }
        var current = member;
        while (_parent[current] != root)
        {
            var next = _parent[current];
            _parent[current] = root;
            current = next;
        }
        return root;
    }
}

/// <summary>Builds <see cref="WorkChain"/> groups from one snapshot of items.</summary>
public static class ChainGrouping
{
    /// <summary>
    /// Groups items into chains over one disjoint-set structure fed by both
    /// explicit dependency edges and title-series membership. Deterministic:
    /// chains sort by earliest creation time, then id; members sort by id.
    /// </summary>
    public static IReadOnlyList<WorkChain> BuildChains(
        IReadOnlyList<AdminWorkItem> items,
        AdminModelOptions? options = null)
    {
        options ??= new AdminModelOptions();
        var byId = new Dictionary<string, AdminWorkItem>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item is null || string.IsNullOrEmpty(item.Id) || byId.ContainsKey(item.Id))
            {
                continue;
            }
            byId[item.Id] = item;
            if (byId.Count >= FleetSnapshot.MaxItems)
            {
                break;
            }
        }

        var sets = new DisjointSet(byId.Keys);
        foreach (var item in byId.Values)
        {
            foreach (var dep in item.DependsOn ?? [])
            {
                if (dep is not null && sets.Contains(dep))
                {
                    sets.Union(item.Id, dep);
                }
            }
        }

        var seriesGroups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var displayByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in byId.Values)
        {
            var title = item.Title;
            if (title is not null && title.Length > options.MaxTitleParseLength)
            {
                continue;
            }
            if (!TitleSeries.TryParse(title, out var key, out var display))
            {
                continue;
            }
            if (!seriesGroups.TryGetValue(key, out var group))
            {
                group = [];
                seriesGroups[key] = group;
                displayByKey[key] = display;
            }
            group.Add(item.Id);
        }
        foreach (var group in seriesGroups.Values)
        {
            for (var i = 1; i < group.Count; i++)
            {
                sets.Union(group[0], group[i]);
            }
        }

        var components = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var id in byId.Keys)
        {
            var root = sets.Find(id);
            if (!components.TryGetValue(root, out var members))
            {
                members = [];
                components[root] = members;
            }
            members.Add(id);
        }

        var chains = new List<WorkChain>(components.Count);
        foreach (var members in components.Values)
        {
            members.Sort(StringComparer.Ordinal);
            string? seriesPrefix = null;
            var distinctKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in members)
            {
                if (TitleSeries.TryParse(byId[id].Title, out var key, out _))
                {
                    distinctKeys.Add(key);
                }
            }
            if (distinctKeys.Count == 1)
            {
                var only = distinctKeys.First();
                seriesPrefix = displayByKey.GetValueOrDefault(only);
            }
            chains.Add(new WorkChain
            {
                Id = "chain-" + members[0],
                ItemIds = members,
                SeriesPrefix = seriesPrefix,
            });
        }

        chains.Sort((left, right) =>
        {
            var leftEarliest = EarliestCreated(byId, left);
            var rightEarliest = EarliestCreated(byId, right);
            var order = leftEarliest.CompareTo(rightEarliest);
            return order != 0
                ? order
                : string.Compare(left.Id, right.Id, StringComparison.Ordinal);
        });
        return chains;
    }

    private static DateTimeOffset EarliestCreated(
        Dictionary<string, AdminWorkItem> byId, WorkChain chain)
    {
        var earliest = DateTimeOffset.MaxValue;
        foreach (var id in chain.ItemIds)
        {
            if (byId.TryGetValue(id, out var item) && item.CreatedAt < earliest)
            {
                earliest = item.CreatedAt;
            }
        }
        return earliest;
    }
}
