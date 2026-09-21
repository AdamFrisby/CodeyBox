namespace CodeyBox.Admin.Model;

/// <summary>A loop with its drawing tier and label placement, in stage-index units.</summary>
public sealed record RoutedLoop
{
    public required StageLoop Loop { get; init; }

    /// <summary>Stage index the arc leaves from (0 = plan … 5 = landed).</summary>
    public required int FromIndex { get; init; }

    public required int ToIndex { get; init; }

    /// <summary>0 is the tier nearest the stage row; higher tiers sit further above it.</summary>
    public required int Tier { get; init; }

    /// <summary>Label centre along the stage axis, in stage-index units.</summary>
    public required double LabelCenter { get; init; }

    /// <summary>Half the label's width, in stage-index units (pitch = 1).</summary>
    public required double LabelHalfWidth { get; init; }
}

/// <summary>
/// Routes an item's backward loops so that no two arcs, and no two labels,
/// collide: each loop occupies an interval on the stage axis (its span plus
/// its label's extent, plus a little air); loops whose intervals intersect go
/// on different tiers. Greedy interval colouring, shortest spans first, so
/// the common case — one rework loop, one interruption — uses one tier each
/// only when they actually touch. Pure and deterministic.
/// </summary>
public static class StageLoopRouter
{
    /// <summary>Estimated width of one label character, as a fraction of the stage pitch.</summary>
    public const double CharWidthInPitches = 0.085;

    /// <summary>Air kept between neighbouring labels/arcs on one tier, in pitches.</summary>
    public const double Air = 0.08;

    /// <summary>Half-extent a self-loop occupies on its own stage, in pitches.</summary>
    public const double SelfLoopHalfSpan = 0.3;

    public static IReadOnlyList<RoutedLoop> Route(IReadOnlyList<StageLoop> loops)
    {
        ArgumentNullException.ThrowIfNull(loops);
        var candidates = loops
            .Where(l => l is not null)
            .Select(l => new
            {
                Loop = l,
                From = (int)l.From,
                To = (int)l.To,
                Span = Math.Abs((int)l.From - (int)l.To),
            })
            .OrderBy(c => c.Span)
            .ThenBy(c => Math.Min(c.From, c.To))
            .ThenBy(c => c.Loop.Kind)
            .ToList();

        var tiers = new List<List<(double Lo, double Hi)>>();
        var result = new List<RoutedLoop>(candidates.Count);
        foreach (var c in candidates)
        {
            var center = (c.From + c.To) / 2.0;
            var halfLabel = Math.Max(0.15, c.Loop.Label.Length * CharWidthInPitches / 2);
            var lo = Math.Min(Math.Min(c.From, c.To) - (c.Span == 0 ? SelfLoopHalfSpan : 0), center - halfLabel) - Air;
            var hi = Math.Max(Math.Max(c.From, c.To) + (c.Span == 0 ? SelfLoopHalfSpan : 0), center + halfLabel) + Air;

            var tier = 0;
            while (tier < tiers.Count && tiers[tier].Any(o => o.Lo < hi && lo < o.Hi))
            {
                tier++;
            }
            if (tier == tiers.Count)
            {
                tiers.Add([]);
            }
            tiers[tier].Add((lo, hi));
            result.Add(new RoutedLoop
            {
                Loop = c.Loop,
                FromIndex = c.From,
                ToIndex = c.To,
                Tier = tier,
                LabelCenter = center,
                LabelHalfWidth = halfLabel,
            });
        }
        return result;
    }
}
