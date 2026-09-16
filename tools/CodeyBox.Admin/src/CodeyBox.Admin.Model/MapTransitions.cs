namespace CodeyBox.Admin.Model;

/// <summary>Something that happened between two snapshots — and only that.</summary>
public enum MapTransitionKind
{
    /// <summary>An item's lifecycle state changed.</summary>
    StateChanged,
    /// <summary>A dependency-blocked item became dispatchable or running.</summary>
    Unblocked,
    /// <summary>Every previously non-terminal member of a chain reached Done/NoActionRequired.</summary>
    ChainCompleted,
    /// <summary>An item appeared.</summary>
    ItemAdded,
    /// <summary>An item disappeared.</summary>
    ItemRemoved,
}

/// <summary>One meaningful change. The renderer animates exactly these — never anything else.</summary>
public sealed record MapTransition
{
    public required MapTransitionKind Kind { get; init; }

    public required string ItemId { get; init; }

    public string? ChainId { get; init; }

    /// <summary>Human reading, e.g. "Queued → Working" or "chain-abc completed".</summary>
    public required string Detail { get; init; }
}

/// <summary>
/// Pure diff between two snapshots. Identical snapshots yield zero events, so
/// the renderer can skip all animation work when nothing happened — the idle
/// map costs nothing. A null <paramref name="previous"/> (first frame) also
/// yields zero events: appearing on screen for the first time is not motion.
/// Bounded output; deterministic order.
/// </summary>
public static class MapTransitionDetector
{
    /// <summary>Diffs two snapshots. Never throws on odd input.</summary>
    public static IReadOnlyList<MapTransition> Detect(
        FleetSnapshot? previous,
        IReadOnlyDictionary<string, ItemActivity>? previousActivities,
        FleetSnapshot current,
        IReadOnlyDictionary<string, ItemActivity> currentActivities,
        IReadOnlyList<WorkChain>? chains = null,
        FleetMapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(currentActivities);
        options ??= new FleetMapOptions();
        if (previous is null)
        {
            return [];
        }

        var max = Math.Max(1, options.MaxTransitionsPerDiff);
        var events = new List<MapTransition>();
        var prevById = Index(previous.Items);
        var currById = Index(current.Items);
        var chainOf = ChainIndex(chains);

        foreach (var (id, curr) in currById)
        {
            if (events.Count >= max)
            {
                break;
            }
            if (!prevById.TryGetValue(id, out var prev))
            {
                events.Add(new MapTransition
                {
                    Kind = MapTransitionKind.ItemAdded,
                    ItemId = id,
                    ChainId = chainOf.GetValueOrDefault(id),
                    Detail = $"appeared in {curr.State}",
                });
                continue;
            }
            if (!string.Equals(prev.State, curr.State, StringComparison.Ordinal))
            {
                events.Add(new MapTransition
                {
                    Kind = MapTransitionKind.StateChanged,
                    ItemId = id,
                    ChainId = chainOf.GetValueOrDefault(id),
                    Detail = $"{prev.State} → {curr.State}",
                });
            }
            if (IsUnblocked(previousActivities, currentActivities, id))
            {
                events.Add(new MapTransition
                {
                    Kind = MapTransitionKind.Unblocked,
                    ItemId = id,
                    ChainId = chainOf.GetValueOrDefault(id),
                    Detail = "dependency landed — dispatchable",
                });
            }
        }

        foreach (var (id, prev) in prevById)
        {
            if (events.Count >= max)
            {
                break;
            }
            if (!currById.ContainsKey(id))
            {
                events.Add(new MapTransition
                {
                    Kind = MapTransitionKind.ItemRemoved,
                    ItemId = id,
                    ChainId = chainOf.GetValueOrDefault(id),
                    Detail = $"left the map from {prev.State}",
                });
            }
        }

        AddChainCompletions(prevById, currById, chains, events, max);

        events.Sort(static (a, b) =>
        {
            var order = string.Compare(a.ItemId, b.ItemId, StringComparison.Ordinal);
            return order != 0 ? order : a.Kind.CompareTo(b.Kind);
        });
        return events.Count <= max ? events : events[..max];
    }

    private static bool IsUnblocked(
        IReadOnlyDictionary<string, ItemActivity>? previousActivities,
        IReadOnlyDictionary<string, ItemActivity> currentActivities,
        string id)
    {
        if (previousActivities is null
            || !previousActivities.TryGetValue(id, out var prev)
            || !currentActivities.TryGetValue(id, out var curr))
        {
            return false;
        }
        return prev.Kind == ActivityKind.BlockedByDependency
            && curr.Kind is ActivityKind.Ready or ActivityKind.WaitingForSlot or ActivityKind.Running;
    }

    private static void AddChainCompletions(
        Dictionary<string, AdminWorkItem> prevById,
        Dictionary<string, AdminWorkItem> currById,
        IReadOnlyList<WorkChain>? chains,
        List<MapTransition> events,
        int max)
    {
        if (chains is null)
        {
            return;
        }
        foreach (var chain in chains)
        {
            if (events.Count >= max || chain?.ItemIds is null || chain.ItemIds.Count == 0)
            {
                continue;
            }
            var hadWork = false;
            var allResolved = true;
            foreach (var id in chain.ItemIds)
            {
                if (id is null || !currById.TryGetValue(id, out var curr))
                {
                    continue;
                }
                var wasTerminal = prevById.TryGetValue(id, out var prev)
                    && ItemStates.IsTerminal(prev.State);
                if (!wasTerminal)
                {
                    hadWork = true;
                }
                if (!ItemStates.Succeeded.Contains(curr.State ?? string.Empty))
                {
                    allResolved = false;
                    break;
                }
            }
            if (hadWork && allResolved)
            {
                events.Add(new MapTransition
                {
                    Kind = MapTransitionKind.ChainCompleted,
                    ItemId = chain.ItemIds.OrderBy(i => i, StringComparer.Ordinal).First(),
                    ChainId = chain.Id,
                    Detail = $"{chain.SeriesPrefix ?? chain.Id} completed",
                });
            }
        }
    }

    private static Dictionary<string, AdminWorkItem> Index(IReadOnlyList<AdminWorkItem>? items)
    {
        var byId = new Dictionary<string, AdminWorkItem>(StringComparer.Ordinal);
        if (items is null)
        {
            return byId;
        }
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
        return byId;
    }

    private static Dictionary<string, string> ChainIndex(IReadOnlyList<WorkChain>? chains)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        if (chains is null)
        {
            return index;
        }
        foreach (var chain in chains)
        {
            if (chain?.ItemIds is null || chain.Id is null)
            {
                continue;
            }
            foreach (var id in chain.ItemIds)
            {
                if (id is not null)
                {
                    index.TryAdd(id, chain.Id);
                }
            }
        }
        return index;
    }
}
