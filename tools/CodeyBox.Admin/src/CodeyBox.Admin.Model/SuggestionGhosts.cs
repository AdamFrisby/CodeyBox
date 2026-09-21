namespace CodeyBox.Admin.Model;

/// <summary>An open suggestion as the map needs it: enough to decide from the ghost.</summary>
public sealed record GhostSuggestion
{
    public required string Id { get; init; }

    /// <summary>The item that produced it — the node the ghost attaches to.</summary>
    public required string ParentId { get; init; }

    public required string Title { get; init; }

    public string Rationale { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public string Severity { get; init; } = string.Empty;

    public string Effort { get; init; } = string.Empty;

    public IReadOnlyList<string> Files { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A ghost placed where the promoted item would land.</summary>
public sealed record GhostPlacement
{
    public required GhostSuggestion Suggestion { get; init; }

    public required double X { get; init; }

    public required double Y { get; init; }

    /// <summary>0 = most severe (then newest) among its parent's ghosts.</summary>
    public required int Rank { get; init; }
}

/// <summary>One parent's ghosts: the ones shown, and how many are folded behind them.</summary>
public sealed record GhostGroup
{
    public required string ParentId { get; init; }

    public required IReadOnlyList<GhostPlacement> Shown { get; init; }

    public required int Folded { get; init; }
}

/// <summary>Operator choice over suggestion ghosts. Persisted client-side.</summary>
public sealed record SuggestionGhostOptions
{
    public bool Show { get; init; } = true;

    /// <summary>Ghosts drawn per parent before the rest fold into "+N more".</summary>
    public int MaxPerParent { get; init; } = 3;

    /// <summary>Lowest severity drawn: "minor" (all), "notable", or "important".</summary>
    public string MinSeverity { get; init; } = "minor";

    public static SuggestionGhostOptions Default { get; } = new();
}

/// <summary>
/// Places open suggestions on the map as ghost nodes beside the item that
/// produced them — provisional boxes, pre-filled, sitting exactly where the
/// promoted item would land, joined by the edge that would exist.
/// Volume: a ghost is drawn only when its parent is on the map (the settled
/// horizon already bounds that), most severe first, at most
/// <see cref="SuggestionGhostOptions.MaxPerParent"/> per parent with the rest
/// folded into a count. Dismissed and promoted suggestions are never ghosts;
/// a promoted one is the real node it became. Pure: no I/O, no clock.
/// </summary>
public static class SuggestionGhosts
{
    public static int SeverityRank(string? severity) => severity?.ToLowerInvariant() switch
    {
        "important" or "critical" or "blocking" or "error" => 3,
        "notable" or "high" or "warning" => 2,
        "minor" or "low" or "info" => 1,
        _ => 0,
    };

    public static IReadOnlyList<GhostGroup> Place(
        IReadOnlyList<GhostSuggestion> open,
        FleetMapLayout layout,
        IReadOnlyList<AdminWorkItem> items,
        SuggestionGhostOptions? options = null,
        FleetMapOptions? mapOptions = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        options ??= SuggestionGhostOptions.Default;
        if (!options.Show || open is null || open.Count == 0)
        {
            return [];
        }
        var minRank = SeverityRank(options.MinSeverity);
        var cap = Math.Max(1, options.MaxPerParent);
        var groups = new List<GhostGroup>();
        foreach (var byParent in open
                     .Where(s => s is not null && !string.IsNullOrEmpty(s.Id) && !string.IsNullOrEmpty(s.ParentId))
                     .Where(s => layout.Nodes.ContainsKey(s.ParentId))
                     .Where(s => SeverityRank(s.Severity) >= minRank)
                     .GroupBy(s => s.ParentId, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var ordered = byParent
                .OrderByDescending(s => SeverityRank(s.Severity))
                .ThenByDescending(s => s.CreatedAt)
                .ThenBy(s => s.Id, StringComparer.Ordinal)
                .ToList();
            var shown = ordered.Take(cap).ToList();
            var slots = FleetMapBuilder.PreviewDependents(layout, items, byParent.Key, shown.Count, mapOptions);
            var placements = new List<GhostPlacement>(shown.Count);
            for (var i = 0; i < shown.Count && i < slots.Count; i++)
            {
                placements.Add(new GhostPlacement { Suggestion = shown[i], X = slots[i].X, Y = slots[i].Y, Rank = i });
            }
            groups.Add(new GhostGroup { ParentId = byParent.Key, Shown = placements, Folded = ordered.Count - placements.Count });
        }
        return groups;
    }
}
