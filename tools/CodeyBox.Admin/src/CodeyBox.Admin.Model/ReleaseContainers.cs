namespace CodeyBox.Admin.Model;

/// <summary>A release as the map needs it.</summary>
public sealed record ReleaseInfo
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string State { get; init; }

    /// <summary>Blocking findings outstanding on the latest release audit iteration (0 when none, or unknown).</summary>
    public int BlockingFindings { get; init; }

    /// <summary>Items spawned to remediate release-level findings.</summary>
    public IReadOnlyList<string> RemediationItemIds { get; init; } = [];
}

/// <summary>A release drawn as a container around the lanes and nodes that make it up.</summary>
public sealed record ReleaseFrame
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string State { get; init; }

    /// <summary>Items in the release, on the map or not.</summary>
    public required int Total { get; init; }

    public required int Done { get; init; }

    public required int Blocking { get; init; }

    public required double X { get; init; }

    public required double Y { get; init; }

    public required double W { get; init; }

    public required double H { get; init; }

    /// <summary>Member ids currently on the map.</summary>
    public IReadOnlyList<string> MemberIds { get; init; } = [];

    /// <summary>Remediation items that are on the map (drawn as a dotted relation from the frame).</summary>
    public IReadOnlyList<string> RemediationOnMap { get; init; } = [];
}

/// <summary>
/// Release containers: the tier above lanes in the nesting
/// <c>release &gt; chain &gt; item &gt; stage</c>. A frame is the bounding box
/// of the release's members on the map (the layout keeps a release's lanes
/// adjacent), carrying name, state and the counts that say whether it is
/// close. Items with no release are simply not in a container. An empty or
/// fully landed release folds away. Pure: no I/O, no clock.
/// </summary>
public static class ReleaseContainers
{
    public static readonly IReadOnlySet<string> SettledReleaseStates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Released", "Closed", "Abandoned",
    };

    /// <param name="stateById">State of every known item (on the map or not) so totals are honest.</param>
    /// <param name="releaseByItem">Release id per known item.</param>
    public static IReadOnlyList<ReleaseFrame> Build(
        IReadOnlyList<ReleaseInfo> releases,
        IReadOnlyDictionary<string, string?> releaseByItem,
        IReadOnlyDictionary<string, string> stateById,
        FleetMapLayout layout,
        FleetMapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(releaseByItem);
        ArgumentNullException.ThrowIfNull(stateById);
        ArgumentNullException.ThrowIfNull(layout);
        options ??= new FleetMapOptions();
        var frames = new List<ReleaseFrame>();
        foreach (var release in (releases ?? []).Where(r => r is not null && !string.IsNullOrEmpty(r.Id)).OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            var memberIds = releaseByItem
                .Where(kv => string.Equals(kv.Value, release.Id, StringComparison.Ordinal))
                .Select(kv => kv.Key)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
            var onMap = memberIds.Where(layout.Nodes.ContainsKey).ToList();
            if (onMap.Count == 0)
            {
                continue; // nothing to contain: an empty (or fully folded) release is not drawn
            }
            var total = memberIds.Count;
            var done = memberIds.Count(id => stateById.TryGetValue(id, out var s) && ItemStates.Succeeded.Contains(s));
            var allSettled = memberIds.All(id => stateById.TryGetValue(id, out var s) && ItemStates.IsTerminal(s));
            if (allSettled && SettledReleaseStates.Contains(release.State))
            {
                continue; // landed and shipped: history, not a container
            }
            var nodes = onMap.Select(id => layout.Nodes[id]).ToList();
            var padX = options.NodeWidth / 2 + options.NodeHeight / 2;
            var padTop = options.NodeHeight / 2 + options.NodeHeight * 0.55; // room for the lane label and the frame's own header
            var padBottom = options.NodeHeight / 2 + options.NodeHeight / 4;
            var minX = nodes.Min(n => n.X) - padX;
            var maxX = nodes.Max(n => n.X) + padX;
            var minY = nodes.Min(n => n.Y) - padTop;
            var maxY = nodes.Max(n => n.Y) + padBottom;
            frames.Add(new ReleaseFrame
            {
                Id = release.Id,
                Name = string.IsNullOrWhiteSpace(release.Name) ? release.Id : release.Name.Trim(),
                State = release.State,
                Total = total,
                Done = done,
                Blocking = Math.Max(0, release.BlockingFindings),
                X = minX,
                Y = minY,
                W = maxX - minX,
                H = maxY - minY,
                MemberIds = onMap,
                RemediationOnMap = (release.RemediationItemIds ?? []).Where(layout.Nodes.ContainsKey).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList(),
            });
        }
        return frames;
    }
}
