namespace CodeyBox.Admin.Model;

/// <summary>Node silhouette. Shape pairs with tone and label — colour is never the only carrier.</summary>
public enum NodeShape
{
    /// <summary>In flight and progressing.</summary>
    Circle,
    /// <summary>Queued: dispatchable now, or waiting for a slot.</summary>
    Square,
    /// <summary>Blocked (by a dependency or by agent availability).</summary>
    Diamond,
    /// <summary>Terminally failed: needs a human.</summary>
    Triangle,
    /// <summary>Parked by the orchestrator.</summary>
    Hexagon,
}

/// <summary>How much a node draws. Detail drops with zoom so the map degrades
/// to shapes and colours instead of unreadable text.</summary>
public enum NodeDetailLevel
{
    /// <summary>Title, agent, age, attempt count.</summary>
    Full,
    /// <summary>Short id label only.</summary>
    Compact,
    /// <summary>Bare shape and colour; no text at any size.</summary>
    Dot,
}

/// <summary>Everything the renderer needs for one node. All text is drawn in
/// screen space at <see cref="TitleTextPx"/> / <see cref="SubTextPx"/>, which
/// are never below the configured readable minimum — a zoomed-out map shows
/// fewer labels, never smaller ones.</summary>
public sealed record MapNodeBadge
{
    public required string ItemId { get; init; }

    public required NodeDetailLevel Detail { get; init; }

    public required NodeShape Shape { get; init; }

    /// <summary>
    /// Tone key into the admin status vocabulary (<c>chip--{tone}</c> CSS
    /// class, glyph from <c>StatusVocabulary.ForWorkItem</c>). The contract —
    /// every tone emitted here exists in the stylesheet — is asserted by the
    /// admin web tests, so the two can never drift apart.
    /// </summary>
    public required string Tone { get; init; }

    /// <summary>Full-detail title label; null unless <see cref="Detail"/> is Full.</summary>
    public string? Label { get; init; }

    /// <summary>Full-detail "agent · age · attempt n" line; null unless Full.</summary>
    public string? SubLabel { get; init; }

    /// <summary>Compact short-id label; null unless Compact.</summary>
    public string? ShortLabel { get; init; }

    public double TitleTextPx { get; init; }

    public double SubTextPx { get; init; }

    /// <summary>Failed and operator-parked nodes pulse; nothing else animates.</summary>
    public bool HasUrgencyRing { get; init; }
}

/// <summary>
/// Pure node styling over item + activity + zoom. A blocked item is a diamond,
/// a running item a circle, a slot-waiting item a square — the three states an
/// operator must tell apart at a glance never share a silhouette.
/// </summary>
public static class MapNodeStyler
{
    /// <summary>Styles every item in the snapshot at one zoom level. Bounded.</summary>
    public static IReadOnlyDictionary<string, MapNodeBadge> StyleAll(
        FleetSnapshot snapshot,
        IReadOnlyDictionary<string, ItemActivity> activities,
        double zoom,
        FleetMapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(activities);
        options ??= new FleetMapOptions();
        var result = new Dictionary<string, MapNodeBadge>(StringComparer.Ordinal);
        foreach (var item in snapshot.Items ?? [])
        {
            if (item is null || string.IsNullOrEmpty(item.Id) || result.ContainsKey(item.Id))
            {
                continue;
            }
            if (!activities.TryGetValue(item.Id, out var activity))
            {
                activity = ActivityAnalyzer.Analyze(item, snapshot, options: null);
            }
            result[item.Id] = Style(item, activity, zoom, snapshot.Now, options);
            if (result.Count >= FleetSnapshot.MaxItems)
            {
                break;
            }
        }
        return result;
    }

    /// <summary>Styles one node. Never throws on odd input; never emits text below the readable minimum.</summary>
    public static MapNodeBadge Style(
        AdminWorkItem item,
        ItemActivity activity,
        double zoom,
        DateTimeOffset now,
        FleetMapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(activity);
        options ??= new FleetMapOptions();
        var detail = zoom >= options.FullDetailZoom ? NodeDetailLevel.Full
            : zoom >= options.CompactDetailZoom ? NodeDetailLevel.Compact
            : NodeDetailLevel.Dot;
        var (shape, tone) = ShapeAndTone(activity.Kind);
        var titlePx = Math.Max(options.BaseTitleTextPx, options.MinReadableTextPx);
        var subPx = Math.Max(options.BaseSubTextPx, options.MinReadableTextPx);
        var id = item.Id ?? string.Empty;

        string? label = null;
        string? subLabel = null;
        string? shortLabel = null;
        if (detail == NodeDetailLevel.Full)
        {
            label = Truncate(string.IsNullOrWhiteSpace(item.Title) ? ShortId(id) : item.Title.Trim(), options.MaxLabelChars);
            subLabel = SubLine(item, now);
        }
        else if (detail == NodeDetailLevel.Compact)
        {
            shortLabel = ShortId(id);
        }

        return new MapNodeBadge
        {
            ItemId = id,
            Detail = detail,
            Shape = shape,
            Tone = tone,
            Label = label,
            SubLabel = subLabel,
            ShortLabel = shortLabel,
            TitleTextPx = titlePx,
            SubTextPx = subPx,
            HasUrgencyRing = activity.Kind == ActivityKind.Failed
                || string.Equals(activity.ParkReason, "NeedsOperatorInput", StringComparison.Ordinal),
        };
    }

    private static (NodeShape Shape, string Tone) ShapeAndTone(ActivityKind kind) => kind switch
    {
        ActivityKind.Running => (NodeShape.Circle, "active"),
        ActivityKind.Ready => (NodeShape.Square, "review"),
        ActivityKind.WaitingForSlot => (NodeShape.Square, "queued"),
        ActivityKind.BlockedByDependency => (NodeShape.Diamond, "rework"),
        ActivityKind.BlockedByAgentAvailability => (NodeShape.Diamond, "wait"),
        ActivityKind.Parked => (NodeShape.Hexagon, "wait"),
        ActivityKind.Failed => (NodeShape.Triangle, "fail"),
        ActivityKind.Succeeded => (NodeShape.Square, "done"),
        ActivityKind.Cancelled => (NodeShape.Square, "muted"),
        _ => (NodeShape.Square, "muted"),
    };

    private static string SubLine(AdminWorkItem item, DateTimeOffset now)
    {
        var agent = string.IsNullOrWhiteSpace(item.Agent) ? "unassigned" : item.Agent;
        var age = FormatAgeShort(now - item.CreatedAt);
        return item.AttemptCount > 0
            ? $"{agent} · {age} · try {item.AttemptCount}"
            : $"{agent} · {age}";
    }

    private static string FormatAgeShort(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }
        return age.TotalMinutes < 1 ? $"{Math.Max(0, (int)age.TotalSeconds)}s"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours}h {age.Minutes}m"
            : $"{(int)age.TotalDays}d {age.Hours}h";
    }

    private static string ShortId(string id) => id.Length >= 8 ? id[..8] : id;

    private static string Truncate(string value, int maxChars)
    {
        if (maxChars <= 0)
        {
            return string.Empty;
        }
        return value.Length <= maxChars ? value : value[..Math.Max(0, maxChars - 1)] + "…";
    }
}
