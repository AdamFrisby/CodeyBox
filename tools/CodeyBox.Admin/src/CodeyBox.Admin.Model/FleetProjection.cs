namespace CodeyBox.Admin.Model;

/// <summary>
/// One projection of the fleet: chains, per-item activity, vitals, and
/// attention ranking — all derived from a single <see cref="FleetSnapshot"/>.
/// Nothing here reads the network. The wall is assembled from surfaces that
/// already exist, because a second screen that gathers its own copy of the
/// fleet's state would double the load on an orchestrator that is busy doing
/// the actual work, and would then disagree with the first screen.
/// </summary>
public sealed record FleetProjection
{
    public required IReadOnlyList<WorkChain> Chains { get; init; }

    public required IReadOnlyDictionary<string, ItemActivity> Activities { get; init; }

    public required IReadOnlyList<FleetVital> Vitals { get; init; }

    /// <summary>Per-item attention, highest first.</summary>
    public required IReadOnlyList<AttentionScore> Attention { get; init; }

    /// <summary>Per-chain attention, highest first.</summary>
    public required IReadOnlyList<ChainAttention> ChainAttention { get; init; }
}

/// <summary>Entry point: projects one snapshot into one screen.</summary>
public static class FleetProjectionBuilder
{
    /// <summary>
    /// Builds the full projection. Pure: the same snapshot and options always
    /// produce the same screen. Performs no I/O and reads no clock — the
    /// caller supplies <c>Now</c> inside the snapshot.
    /// </summary>
    public static FleetProjection Project(
        FleetSnapshot snapshot,
        AdminModelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        options ??= new AdminModelOptions();

        var chains = ChainGrouping.BuildChains(snapshot.Items ?? [], options);
        var activities = ActivityAnalyzer.AnalyzeAll(snapshot, options);
        var vitals = VitalsEvaluator.Evaluate(snapshot, options);
        var attention = AttentionRanker.RankItems(snapshot, activities, options);
        var chainAttention = AttentionRanker.RankChains(chains, attention);

        return new FleetProjection
        {
            Chains = chains,
            Activities = activities,
            Vitals = vitals,
            Attention = attention,
            ChainAttention = chainAttention,
        };
    }
}
