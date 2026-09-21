using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

/// <summary>
/// When settled work leaves the map: never while something in flight builds
/// on it, otherwise by the operator's horizon; failures that need a decision
/// never leave on their own.
/// </summary>
public sealed class TerminalVisibilityTests
{
    private static readonly DateTimeOffset Now = Fixtures.Now;

    private static AdminWorkItem At(string id, string state, double hoursAgo, IReadOnlyList<string>? dependsOn = null) =>
        Fixtures.Item(id, state: state, dependsOn: dependsOn, updatedAt: Now.AddHours(-hoursAgo));

    [Fact]
    public void LiveWork_AlwaysStays()
    {
        var kept = TerminalVisibility.Filter([At("q", "Queued", 500), At("w", "Working", 500)], Now, new TerminalVisibilityOptions { ShowSettled = false });
        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void SettledAncestorsOfLiveWork_Stay_EvenWhenOld_ThroughOtherSettledItems()
    {
        var items = new List<AdminWorkItem>
        {
            At("root", "Done", 700),
            At("mid", "Done", 600, ["root"]),
            At("live", "Queued", 1, ["mid"]),
            At("orphan", "Done", 600),
        };

        var kept = TerminalVisibility.Filter(items, Now, new TerminalVisibilityOptions { Horizon = TimeSpan.FromDays(3) });

        var ids = kept.Select(i => i.Id).ToHashSet();
        Assert.Contains("root", ids);
        Assert.Contains("mid", ids);
        Assert.Contains("live", ids);
        Assert.DoesNotContain("orphan", ids);
    }

    [Fact]
    public void Horizon_FoldsOldSettledItems_KeepsRecentOnes_NullNeverHides()
    {
        var items = new List<AdminWorkItem> { At("fresh", "Done", 2), At("stale", "Done", 100), At("cancelled", "Cancelled", 100) };

        var threeDays = TerminalVisibility.Filter(items, Now, new TerminalVisibilityOptions { Horizon = TimeSpan.FromDays(3) });
        Assert.Equal(["fresh"], threeDays.Select(i => i.Id).ToList());

        var never = TerminalVisibility.Filter(items, Now, new TerminalVisibilityOptions { Horizon = null });
        Assert.Equal(3, never.Count);

        var hidden = TerminalVisibility.Filter(items, Now, new TerminalVisibilityOptions { ShowSettled = false });
        Assert.Empty(hidden);
    }

    [Fact]
    public void FailuresThatNeedADecision_NeverFoldAway()
    {
        var items = new List<AdminWorkItem>
        {
            At("f", "Failed", 900), At("af", "AuditFailed", 900), At("mc", "MergeConflictResolutionFailed", 900),
            At("ab", "AbandonedAfterRecoveryAttempts", 900), At("q", "NeedsOperatorInput", 900),
        };

        var kept = TerminalVisibility.Filter(items, Now, new TerminalVisibilityOptions { ShowSettled = false, Horizon = TimeSpan.FromHours(1) });

        Assert.Equal(5, kept.Count);
        Assert.All(kept, i => Assert.True(TerminalVisibility.NeedsYou(i.State)));
    }

    [Fact]
    public void DefaultIsThreeDays_AndSettledMeansDoneOrCancelled()
    {
        Assert.Equal(TimeSpan.FromDays(180), TerminalVisibilityOptions.Default.Horizon);
        Assert.True(TerminalVisibilityOptions.Default.ShowSettled);
        Assert.True(TerminalVisibility.IsSettled("Done"));
        Assert.True(TerminalVisibility.IsSettled("Cancelled"));
        Assert.True(TerminalVisibility.IsSettled("NoActionRequired"));
        Assert.False(TerminalVisibility.IsSettled("Failed"));
        Assert.False(TerminalVisibility.IsSettled("Queued"));
    }

    [Fact]
    public void FoldingAnAncestor_DoesNotMoveTheLiveNode()
    {
        var options = new FleetMapOptions();
        var before = new List<AdminWorkItem> { At("done", "Done", 2), At("live", "Queued", 1, ["done"]) };
        var layout = FleetMapBuilder.DeriveInitial(before, ChainGrouping.BuildChains(before).ToList(), options);
        Assert.Equal(1, layout.Nodes["live"].Depth);

        // The done item leaves the snapshot (horizon passed, and "live" no longer lists it once the orchestrator prunes).
        var after = new List<AdminWorkItem> { At("live", "Queued", 1, ["done"]) };
        var refreshed = FleetMapBuilder.Update(layout, after, ChainGrouping.BuildChains(after).ToList(), options);

        // Its chain id changes (it is a singleton now); its place does not.
        Assert.Equal(layout.Nodes["live"] with { ChainId = refreshed.Nodes["live"].ChainId }, refreshed.Nodes["live"]);
    }
}
