namespace CodeyBox.Admin.Model;

/// <summary>How much an item needs a human right now, 0–100.</summary>
public sealed record AttentionScore
{
    public required string ItemId { get; init; }

    public required double Score { get; init; }

    /// <summary>Why, in operator terms. Never empty.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];
}

/// <summary>Chain-level roll-up: the chain is as urgent as its most urgent member.</summary>
public sealed record ChainAttention
{
    public required string ChainId { get; init; }

    public required double Score { get; init; }

    public IReadOnlyList<string> ItemIds { get; init; } = [];

    /// <summary>Member carrying the peak score (ties break by id).</summary>
    public required string TopItemId { get; init; }
}

/// <summary>
/// Ranks what needs a human right now. Failures and parks outrank everything;
/// a long-running healthy item scores low however long it has been running
/// (its score is hard-capped, not a function of unbounded duration).
/// </summary>
public static class AttentionRanker
{
    /// <summary>Scores every item in the snapshot, highest first.</summary>
    public static IReadOnlyList<AttentionScore> RankItems(
        FleetSnapshot snapshot,
        IReadOnlyDictionary<string, ItemActivity> activities,
        AdminModelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(activities);
        options ??= new AdminModelOptions();

        var byId = new Dictionary<string, AdminWorkItem>(StringComparer.Ordinal);
        foreach (var item in snapshot.Items ?? [])
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

        var scores = new List<AttentionScore>(byId.Count);
        foreach (var item in byId.Values)
        {
            if (!activities.TryGetValue(item.Id, out var activity))
            {
                activity = ActivityAnalyzer.Analyze(item, snapshot, options);
            }
            scores.Add(ScoreItem(item, activity, snapshot, byId, options));
        }
        scores.Sort(static (left, right) =>
        {
            var order = right.Score.CompareTo(left.Score);
            return order != 0
                ? order
                : string.Compare(left.ItemId, right.ItemId, StringComparison.Ordinal);
        });
        return scores;
    }

    /// <summary>Rolls item scores up to chains, highest first.</summary>
    public static IReadOnlyList<ChainAttention> RankChains(
        IReadOnlyList<WorkChain> chains,
        IReadOnlyList<AttentionScore> itemScores)
    {
        ArgumentNullException.ThrowIfNull(chains);
        ArgumentNullException.ThrowIfNull(itemScores);
        var byId = itemScores.ToDictionary(s => s.ItemId, StringComparer.Ordinal);
        var ranked = new List<ChainAttention>(chains.Count);
        foreach (var chain in chains)
        {
            var members = chain?.ItemIds ?? [];
            var topId = string.Empty;
            var topScore = 0.0;
            foreach (var id in members)
            {
                var score = byId.TryGetValue(id, out var entry) ? entry.Score : 0.0;
                if (topId.Length == 0
                    || score > topScore
                    || (score == topScore && string.Compare(id, topId, StringComparison.Ordinal) < 0))
                {
                    topId = id;
                    topScore = score;
                }
            }
            if (topId.Length == 0)
            {
                continue;
            }
            ranked.Add(new ChainAttention
            {
                ChainId = chain!.Id,
                Score = topScore,
                ItemIds = members,
                TopItemId = topId,
            });
        }
        ranked.Sort(static (left, right) =>
        {
            var order = right.Score.CompareTo(left.Score);
            return order != 0
                ? order
                : string.Compare(left.ChainId, right.ChainId, StringComparison.Ordinal);
        });
        return ranked;
    }

    private static AttentionScore ScoreItem(
        AdminWorkItem item,
        ItemActivity activity,
        FleetSnapshot snapshot,
        Dictionary<string, AdminWorkItem> byId,
        AdminModelOptions options)
    {
        var now = snapshot.Now;
        var ageHours = Math.Max(0, (now - item.CreatedAt).TotalHours);
        var (score, reasons) = activity.Kind switch
        {
            ActivityKind.Failed => (Math.Min(100, 90 + Math.Min(10, ageHours / 24)),
                new[] { $"Failed in {item.State}." }),
            ActivityKind.Parked => ScoreParked(item, activity, ageHours),
            ActivityKind.BlockedByAgentAvailability => (55.0,
                new[] { activity.Summary }),
            ActivityKind.BlockedByDependency => ScoreBlocked(item, activity, byId, options),
            ActivityKind.WaitingForSlot => (Math.Min(options.WaitingAttentionCap, 20 + Math.Min(10, ageHours)),
                new[] { "Dispatchable; waiting for a free worker slot." }),
            ActivityKind.Ready => (10.0,
                new[] { "Queued and dispatchable." }),
            ActivityKind.Running => (Math.Min(options.RunningAttentionCap, 5 + Math.Min(5, ageHours / 24)),
                new[] { $"Running ({item.State})." }),
            ActivityKind.Cancelled or ActivityKind.Succeeded => (0.0,
                new[] { $"Terminal ({item.State}); needs nothing." }),
            _ => (40.0,
                new[] { activity.Summary }),
        };
        return new AttentionScore
        {
            ItemId = item.Id,
            Score = Math.Clamp(score, 0, 100),
            Reasons = reasons,
        };
    }

    private static (double, string[]) ScoreParked(AdminWorkItem item, ItemActivity activity, double ageHours)
    {
        var staleness = Math.Min(5, ageHours / 24);
        return activity.ParkReason switch
        {
            "NeedsOperatorInput" => (Math.Min(100, 85 + staleness),
                new[] { "Parked: the agent asked for operator input." }),
            "WaitingForAgentResume" => (Math.Min(100, 75 + staleness),
                new[] { "Parked: its agents are paused — resume one to release it." }),
            "WaitingForQuotaReset" => (Math.Min(100, 70 + staleness),
                new[] { "Parked: waiting for agent quota to reset." }),
            "WaitingForTransientRetry" => (Math.Min(100, 60 + staleness),
                new[] { "Parked: backing off before a transient retry." }),
            _ => (Math.Min(100, 65 + staleness),
                new[] { $"Parked ({item.State})." }),
        };
    }

    private static (double, string[]) ScoreBlocked(
        AdminWorkItem item,
        ItemActivity activity,
        Dictionary<string, AdminWorkItem> byId,
        AdminModelOptions options)
    {
        var reasons = new List<string> { activity.Summary };
        var deadEnd = false;
        foreach (var blocker in activity.Blockers)
        {
            if (byId.TryGetValue(blocker.Id, out var blockerItem)
                && ItemStates.IsFailedTerminal(blockerItem.State))
            {
                deadEnd = true;
                reasons.Add($"{blocker.Id} failed — retry it before this item can move.");
            }
        }
        var score = 50 + (deadEnd ? 10 : 0);
        return (Math.Min(options.BlockedAttentionCap, score), reasons.ToArray());
    }
}
