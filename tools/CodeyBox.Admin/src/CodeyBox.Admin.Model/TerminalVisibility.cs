namespace CodeyBox.Admin.Model;

/// <summary>The operator's choice of when settled work leaves the map. Persisted client-side.</summary>
public sealed record TerminalVisibilityOptions
{
    /// <summary>Show items that landed or were cancelled at all.</summary>
    public bool ShowSettled { get; init; } = true;

    /// <summary>
    /// Settled items older than this (by last update) fold away unless
    /// something in flight still builds on them. Null never hides.
    /// Three days covers a weekend away — the map's reason to exist.
    /// </summary>
    public TimeSpan? Horizon { get; init; } = TimeSpan.FromDays(180);

    public static TerminalVisibilityOptions Default { get; } = new();
}

/// <summary>
/// Decides which terminal items stay on the map. Live work always stays.
/// Terminal failures that need a decision always stay — they are the inbox.
/// A settled item stays while something in flight depends on it, directly or
/// through other settled items (the history a chain is built on), or while it
/// is within the horizon. Pure: no clock; the caller supplies <c>now</c>.
/// </summary>
public static class TerminalVisibility
{
    public static readonly IReadOnlySet<string> NeedsYouStates = new HashSet<string>(StringComparer.Ordinal)
    {
        "Failed", "AuditFailed", "MergeConflictResolutionFailed", "AbandonedAfterRecoveryAttempts", "NeedsOperatorInput",
    };

    public static bool NeedsYou(string? state) => state is not null && NeedsYouStates.Contains(state);

    /// <summary>True for Done, NoActionRequired and Cancelled — finished, nothing to decide.</summary>
    public static bool IsSettled(string? state) =>
        state is not null && (ItemStates.Succeeded.Contains(state) || string.Equals(state, "Cancelled", StringComparison.Ordinal));

    public static IReadOnlyList<AdminWorkItem> Filter(
        IReadOnlyList<AdminWorkItem> items, DateTimeOffset now, TerminalVisibilityOptions? options = null)
    {
        options ??= TerminalVisibilityOptions.Default;
        var byId = new Dictionary<string, AdminWorkItem>(StringComparer.Ordinal);
        foreach (var item in items ?? [])
        {
            if (item is not null && !string.IsNullOrEmpty(item.Id))
            {
                byId.TryAdd(item.Id, item);
            }
            if (byId.Count >= FleetSnapshot.MaxItems)
            {
                break;
            }
        }

        // Ancestors of live work, reached through terminal items only.
        var relevant = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        foreach (var item in byId.Values)
        {
            if (!ItemStates.IsTerminal(item.State))
            {
                foreach (var dep in item.DependsOn ?? [])
                {
                    if (dep is not null)
                    {
                        stack.Push(dep);
                    }
                }
            }
        }
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!byId.TryGetValue(id, out var dep) || !ItemStates.IsTerminal(dep.State) || !relevant.Add(id))
            {
                continue;
            }
            foreach (var next in dep.DependsOn ?? [])
            {
                if (next is not null)
                {
                    stack.Push(next);
                }
            }
        }

        var kept = new List<AdminWorkItem>(byId.Count);
        foreach (var item in byId.Values)
        {
            if (!ItemStates.IsTerminal(item.State) || NeedsYou(item.State))
            {
                kept.Add(item);
                continue;
            }
            if (!options.ShowSettled)
            {
                continue;
            }
            if (relevant.Contains(item.Id)
                || options.Horizon is null
                || now - item.UpdatedAt <= options.Horizon.Value)
            {
                kept.Add(item);
            }
        }
        return kept;
    }
}
