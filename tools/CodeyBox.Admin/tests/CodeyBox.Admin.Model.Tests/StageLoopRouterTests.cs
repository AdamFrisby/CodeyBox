using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

/// <summary>
/// Loop routing: arcs and labels that would collide go on different tiers;
/// arcs that cannot collide share a tier; a label wider than its span claims
/// the extra room; and the result is deterministic.
/// </summary>
public sealed class StageLoopRouterTests
{
    private static StageLoop Loop(PipelineStage from, PipelineStage to, StageLoopKind kind, int count, string? label = null) => new()
    {
        From = from,
        To = to,
        Kind = kind,
        Count = count,
        Label = label ?? $"{kind.ToString().ToLowerInvariant()} ×{count}",
    };

    private static RoutedLoop Find(IReadOnlyList<RoutedLoop> routed, PipelineStage from, PipelineStage to) =>
        routed.Single(r => r.Loop.From == from && r.Loop.To == to);

    [Fact]
    public void LoopsSharingAStage_GoOnDifferentTiers()
    {
        var routed = StageLoopRouter.Route(
        [
            Loop(PipelineStage.Audit, PipelineStage.Work, StageLoopKind.Rework, 3),
            Loop(PipelineStage.Work, PipelineStage.Work, StageLoopKind.Interruption, 1),
        ]);

        var rework = Find(routed, PipelineStage.Audit, PipelineStage.Work);
        var interrupted = Find(routed, PipelineStage.Work, PipelineStage.Work);
        Assert.NotEqual(rework.Tier, interrupted.Tier);
        Assert.Equal(0, Math.Min(rework.Tier, interrupted.Tier));
    }

    [Fact]
    public void SelfLoop_ConflictsWithAnArcArrivingAtItsStage()
    {
        var routed = StageLoopRouter.Route(
        [
            Loop(PipelineStage.Work, PipelineStage.Work, StageLoopKind.Interruption, 2),
            Loop(PipelineStage.Audit, PipelineStage.Work, StageLoopKind.Interruption, 1),
        ]);

        Assert.NotEqual(
            Find(routed, PipelineStage.Work, PipelineStage.Work).Tier,
            Find(routed, PipelineStage.Audit, PipelineStage.Work).Tier);
    }

    [Fact]
    public void DisjointLoops_ShareTierZero()
    {
        var routed = StageLoopRouter.Route(
        [
            Loop(PipelineStage.Work, PipelineStage.Work, StageLoopKind.Interruption, 1, "×1"),
            Loop(PipelineStage.Merge, PipelineStage.Merge, StageLoopKind.Conflict, 1, "×1"),
        ]);

        Assert.All(routed, r => Assert.Equal(0, r.Tier));
    }

    [Fact]
    public void WideLabel_ClaimsRoomBeyondItsSpan_AndPushesANeighbourUp()
    {
        // Two self-loops on adjacent stages: short labels fit side by side,
        // long labels would overlap, so the second takes the next tier.
        var shortLabels = StageLoopRouter.Route(
        [
            Loop(PipelineStage.Work, PipelineStage.Work, StageLoopKind.Interruption, 1, "×1"),
            Loop(PipelineStage.Audit, PipelineStage.Audit, StageLoopKind.Interruption, 1, "×1"),
        ]);
        Assert.All(shortLabels, r => Assert.Equal(0, r.Tier));

        var longLabels = StageLoopRouter.Route(
        [
            Loop(PipelineStage.Work, PipelineStage.Work, StageLoopKind.Interruption, 2, "interrupted ×2"),
            Loop(PipelineStage.Audit, PipelineStage.Audit, StageLoopKind.Interruption, 1, "interrupted ×1"),
        ]);
        Assert.Equal([0, 1], longLabels.Select(r => r.Tier).OrderBy(t => t).ToList());
        Assert.All(longLabels, r => Assert.True(r.LabelHalfWidth > 0.5));
    }

    [Fact]
    public void ShortestSpansTakeTheLowestTiers_AndRoutingIsDeterministic()
    {
        var loops = new List<StageLoop>
        {
            Loop(PipelineStage.Merge, PipelineStage.Work, StageLoopKind.Retry, 1),
            Loop(PipelineStage.Audit, PipelineStage.Work, StageLoopKind.Rework, 2),
            Loop(PipelineStage.Work, PipelineStage.Work, StageLoopKind.Interruption, 1),
        };

        var first = StageLoopRouter.Route(loops);
        var second = StageLoopRouter.Route(loops.AsEnumerable().Reverse().ToList());

        // Shortest span first: the self-loop takes tier 0; the return edge that
        // also touches work sits above it; the long retry above both.
        Assert.Equal(0, Find(first, PipelineStage.Work, PipelineStage.Work).Tier);
        Assert.Equal(1, Find(first, PipelineStage.Audit, PipelineStage.Work).Tier);
        Assert.Equal(2, Find(first, PipelineStage.Merge, PipelineStage.Work).Tier);
        foreach (var r in first)
        {
            var same = Find(second, r.Loop.From, r.Loop.To);
            Assert.Equal(r.Tier, same.Tier);
            Assert.Equal(r.LabelCenter, same.LabelCenter);
        }
    }

    [Fact]
    public void EmptyInput_RoutesToNothing()
    {
        Assert.Empty(StageLoopRouter.Route([]));
    }
}
