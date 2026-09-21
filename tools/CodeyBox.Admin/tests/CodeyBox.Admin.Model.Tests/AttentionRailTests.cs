using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

/// <summary>
/// The rail: which items make it, how they stack, that their leaders do not
/// cross, what a decision card says, where a new dependent previews, and
/// that stale urgency stays on the rail rather than seizing the camera.
/// </summary>
public sealed class AttentionRailTests
{
    private static readonly FleetMapOptions Options = new();

    private static (FleetSnapshot Snapshot, FleetProjection Projection, FleetMapLayout Layout) Build(IReadOnlyList<AdminWorkItem> items)
    {
        var snapshot = Fixtures.Snapshot(items);
        var projection = FleetProjectionBuilder.Project(snapshot);
        var layout = FleetMapBuilder.DeriveInitial(snapshot.Items.ToList(), projection.Chains.ToList(), Options);
        return (snapshot, projection, layout);
    }

    [Fact]
    public void OnlyItemsNeedingAPerson_MakeTheRail_StackedByNodePosition()
    {
        var (snapshot, projection, layout) = Build([
            Fixtures.Item("a-fail", state: "Failed"),
            Fixtures.Item("b-run", state: "Working"),
            Fixtures.Item("c-ask", state: "NeedsOperatorInput"),
            Fixtures.Item("d-audit", state: "AuditFailed"),
            Fixtures.Item("e-done", state: "Done"),
            Fixtures.Item("f-blocked", dependsOn: ["ghost"], dependsOnSatisfied: false),
        ]);

        var cards = AttentionRail.Build(snapshot, projection.Attention, layout, Options.RailMaxCards);

        Assert.Equal(3, cards.Count);
        Assert.All(cards, c => Assert.True(TerminalVisibility.NeedsYou(c.State)));
        for (var i = 1; i < cards.Count; i++)
        {
            Assert.True(cards[i - 1].NodeY <= cards[i].NodeY, "stacked top-to-bottom by node");
            Assert.Equal(i, cards[i].Order);
        }
        Assert.Equal(cards.Count, cards.Select(c => c.Channel).Distinct().Count()); // a default routing; the renderer re-routes from screen positions
        Assert.Single(cards, c => c.IsTop);
        Assert.True(cards.Single(c => c.IsTop).Score >= cards.Max(c => c.Score));
        Assert.All(cards, c => Assert.False(string.IsNullOrWhiteSpace(c.Reason)));
    }

    [Fact]
    public void TooManyCards_KeepsTheMostUrgent()
    {
        var items = new List<AdminWorkItem>();
        for (var i = 0; i < 20; i++)
        {
            items.Add(Fixtures.Item($"f-{i:D2}", state: i % 2 == 0 ? "Failed" : "NeedsOperatorInput"));
        }
        var (snapshot, projection, layout) = Build(items);

        var cards = AttentionRail.Build(snapshot, projection.Attention, layout, 5);

        Assert.Equal(5, cards.Count);
        var cutoff = cards.Min(c => c.Score);
        var dropped = projection.Attention.Where(a => cards.All(c => c.ItemId != a.ItemId) && TerminalVisibility.NeedsYou(items.Single(i => i.Id == a.ItemId).State));
        Assert.All(dropped, a => Assert.True(a.Score <= cutoff));
    }

    [Fact]
    public void AssignChannels_FindsACrossingFreeRouting_WhereverTheNodesSit()
    {
        // Nodes below their cards and nodes above their cards each admit a
        // crossing-free routing, and the greedy insertion finds it.
        var below = new List<(double CardY, double NodeY)> { (40, 100), (120, 380), (200, 620) };
        var above = new List<(double CardY, double NodeY)> { (300, 60), (380, 140), (460, 220) };
        foreach (var set in new[] { below, above })
        {
            var channels = AssignChannels(set);
            Assert.Equal(0, AttentionRail.CountCrossings(channels));
            Assert.Equal(set.Count, channels.Select(c => c.Channel).Distinct().Count());
        }

        // A tangled set has no crossing-free routing at all (brute force over
        // every permutation says three is the floor); greedy reaches the floor.
        var mixed = new List<(double CardY, double NodeY)> { (60, 500), (140, 30), (220, 700), (300, 260) };
        var optimum = Permutations([0, 1, 2, 3])
            .Min(perm => AttentionRail.CountCrossings(mixed.Select((l, i) => (l.CardY, l.NodeY, perm[i])).ToList()));
        Assert.Equal(3, optimum);
        Assert.Equal(optimum, AttentionRail.CountCrossings(AssignChannels(mixed)));

        // A naive "lower cards nearer the rail" rule crosses in the second case; the assignment does not.
        var naive = above.Select((l, i) => (l.CardY, l.NodeY, Channel: above.Count - 1 - i)).ToList();
        Assert.True(AttentionRail.CountCrossings(naive) > 0);

        Assert.Equal(AssignChannels(mixed), AssignChannels(mixed)); // deterministic
        Assert.Empty(AttentionRail.AssignChannels([]));
    }

    private static IEnumerable<int[]> Permutations(int[] items)
    {
        if (items.Length <= 1)
        {
            yield return items;
            yield break;
        }
        for (var i = 0; i < items.Length; i++)
        {
            var rest = items.Where((_, j) => j != i).ToArray();
            foreach (var tail in Permutations(rest))
            {
                yield return new[] { items[i] }.Concat(tail).ToArray();
            }
        }
    }

    private static List<(double CardY, double NodeY, int Channel)> AssignChannels(List<(double CardY, double NodeY)> leaders)
    {
        var channels = AttentionRail.AssignChannels(leaders);
        return leaders.Select((l, i) => (l.CardY, l.NodeY, channels[i])).ToList();
    }

    [Fact]
    public void BuiltCards_RouteWithoutCrossings_OnASpreadOutFleet()
    {
        var items = new List<AdminWorkItem> { Fixtures.Item("hub", state: "Working") };
        for (var i = 0; i < 12; i++)
        {
            items.Add(Fixtures.Item($"d-{i:D2}", state: i % 3 == 0 ? "AuditFailed" : "Queued", dependsOn: ["hub"]));
        }
        var (snapshot, projection, layout) = Build(items);
        var cards = AttentionRail.Build(snapshot, projection.Attention, layout, Options.RailMaxCards);
        Assert.True(cards.Count >= 3);

        // Cards stack downwards with a fixed pitch; nodes are where the layout put them.
        var positions = cards.Select(c => (CardY: 30.0 + c.Order * 70, NodeY: c.NodeY)).ToList();
        var channels = AttentionRail.AssignChannels(positions);
        var leaders = positions.Select((p, i) => (p.CardY, p.NodeY, channels[i])).ToList();
        Assert.Equal(0, AttentionRail.CountCrossings(leaders, channelGap: 8, nodeEdgeX: -2000));
    }

    [Fact]
    public void DecisionBrief_AuditFailedWithNoFindings_SaysParkTextIsNotEvidence()
    {
        var brief = DecisionBriefBuilder.Build("AuditFailed", null, "audit gate: 0 blocking", 3, 6, [], null);

        Assert.Equal("Audit failed after 3 of 6 audit iterations.", brief.Headline);
        Assert.Empty(brief.Findings);
        Assert.Contains("park text is not evidence", brief.FindingsNote);
        Assert.Equal("audit gate: 0 blocking", brief.LastError);
        Assert.Contains(brief.Actions, a => a.Key == "retryWork");
        Assert.Contains(brief.Actions, a => a.Key == "delegate");
        Assert.DoesNotContain(brief.Actions, a => a.Key == "addDependent");
    }

    [Fact]
    public void DecisionBrief_CarriesFindings_CapsThem_AndFlagsUnfetched()
    {
        var findings = Enumerable.Range(0, 6).Select(i => new DecisionFinding("csharp:test-pass", "error", $"Test {i} failed")).ToList();
        var brief = DecisionBriefBuilder.Build("AuditFailed", null, null, 1, null, findings, null);
        Assert.Equal(DecisionBriefBuilder.MaxFindings, brief.Findings.Count);
        Assert.Contains("+2 more", brief.FindingsNote);
        Assert.Equal("Audit failed after 1 audit iteration.", brief.Headline);

        var unfetched = DecisionBriefBuilder.Build("Failed", null, "boom", null, null, null, null);
        Assert.Equal("Findings not fetched yet.", unfetched.FindingsNote);
        Assert.Equal("Failed during work.", unfetched.Headline);
    }

    [Fact]
    public void DecisionBrief_Question_CarriesTheQuestionAndAnAnswerAction()
    {
        var activity = new ItemActivity { ItemId = "q", Kind = ActivityKind.Parked, Summary = "Parked: waiting for operator input.", ParkReason = "NeedsOperatorInput" };
        var brief = DecisionBriefBuilder.Build("NeedsOperatorInput", activity, null, null, null, [], "Which database should the plugin target?");

        Assert.Equal("The agent asked you a question.", brief.Headline);
        Assert.Equal("Which database should the plugin target?", brief.OpenQuestion);
        Assert.Null(brief.FindingsNote);
        Assert.Contains(brief.Actions, a => a.Key == "answer");
        Assert.Contains(brief.Actions, a => a.Key == "cancel");
    }

    [Fact]
    public void PreviewDependent_LandsWhereTheRealDependentWould()
    {
        var items = new List<AdminWorkItem> { Fixtures.Item("hub", state: "Working"), Fixtures.Item("d-1", dependsOn: ["hub"]), Fixtures.Item("d-2", dependsOn: ["hub"]) };
        var layout = FleetMapBuilder.DeriveInitial(items, ChainGrouping.BuildChains(items).ToList(), Options);

        var preview = FleetMapBuilder.PreviewDependent(layout, items, "hub", Options);
        Assert.NotNull(preview);

        var real = items.Append(Fixtures.Item("d-3", dependsOn: ["hub"])).ToList();
        var refreshed = FleetMapBuilder.Update(layout, real, ChainGrouping.BuildChains(real).ToList(), Options);
        Assert.Equal(refreshed.Nodes["d-3"].X, preview!.X);
        Assert.Equal(refreshed.Nodes["d-3"].Y, preview.Y);
        Assert.True(preview.X > layout.Nodes["hub"].X);

        Assert.Null(FleetMapBuilder.PreviewDependent(layout, items, "nope", Options));
        Assert.Equal(layout.Nodes.Count, FleetMapBuilder.Update(layout, items, ChainGrouping.BuildChains(items).ToList(), Options).Nodes.Count); // no ghost leaks
    }

    [Fact]
    public void StaleFailure_StaysOnTheRail_ButDoesNotSeizeTheCamera()
    {
        var t0 = Fixtures.Now;
        var items = new List<AdminWorkItem>
        {
            Fixtures.Item("old-fail", state: "Failed", updatedAt: t0.AddDays(-5)),
            Fixtures.Item("run", state: "Working", updatedAt: t0),
        };
        var (snapshot, projection, layout) = Build(items);
        var view = new CameraViewSize(1600, 900);

        var cards = AttentionRail.Build(snapshot, projection.Attention, layout, Options.RailMaxCards);
        Assert.Single(cards, c => c.ItemId == "old-fail");

        var withGuard = CameraDirector.Next(CameraDirector.Initial(layout, t0, view, Options), projection, layout, t0, view, Options, snapshot: snapshot);
        Assert.NotEqual("old-fail", withGuard.FocusId);
        Assert.Null(withGuard.HoldReason);

        var fresh = Build([Fixtures.Item("new-fail", state: "Failed", updatedAt: t0.AddMinutes(-10)), Fixtures.Item("run", state: "Working")]);
        var seized = CameraDirector.Next(CameraDirector.Initial(fresh.Layout, t0, view, Options), fresh.Projection, fresh.Layout, t0, view, Options, snapshot: fresh.Snapshot);
        Assert.Equal("new-fail", seized.FocusId);
    }
}
