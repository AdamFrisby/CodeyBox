using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Dependency picker ordering: same-chain first, then same-project, then
/// everything else — newest first within each band — plus substring search
/// over enough identity to recognise a candidate.
/// </summary>
public sealed class DependencyProximityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static DependencyCandidate Item(
        string id, string project, string title, int ageHours, string state = "Queued") =>
        new(id, project, title, state, T0.AddHours(ageHours));

    [Fact]
    public void Order_SameChainThenSameProjectThenRest_NewestFirstInBand()
    {
        var chainMember = Item("chain-1", "other", "Chain sibling", 50);
        var sameNew = Item("same-new", "proj-1", "Same project new", 5);
        var sameOld = Item("same-old", "proj-1", "Same project old", 1);
        var other = Item("other-1", "proj-2", "Elsewhere", 10);

        var ordered = DependencyProximity.Order(
            [other, sameOld, chainMember, sameNew], "proj-1", new HashSet<string> { "chain-1" });

        Assert.Equal(
            ["chain-1", "same-new", "same-old", "other-1"],
            ordered.Select(c => c.Id));
    }

    [Fact]
    public void Order_SameProjectBeatsNewerForeignItem()
    {
        var oldSame = Item("old-same", "proj-1", "Old local", 1);
        var brandNewForeign = Item("new-foreign", "proj-9", "Shiny elsewhere", 100);

        var ordered = DependencyProximity.Order([brandNewForeign, oldSame], "proj-1");

        Assert.Equal(["old-same", "new-foreign"], ordered.Select(c => c.Id));
    }

    [Fact]
    public void Order_IsDeterministicOnTimestampTies()
    {
        var a = Item("id-a", "proj-1", "A", 5);
        var b = Item("id-b", "proj-1", "B", 5);

        var first = DependencyProximity.Order([b, a], "proj-1");
        var second = DependencyProximity.Order([a, b], "proj-1");

        Assert.Equal(first.Select(c => c.Id), second.Select(c => c.Id));
    }

    [Theory]
    [InlineData("migrate", true)]
    [InlineData("MIGRATE", true)]
    [InlineData("aabbccdd", true)]
    [InlineData("JIRA-9", true)]
    [InlineData("unrelated", false)]
    public void Matches_SubstringOverTitleIdAndExternalId(string query, bool expected)
    {
        var candidate = new DependencyCandidate(
            "aabbccdd-0000-0000-0000-000000000001",
            "proj-1",
            "Migrate the database",
            "Queued",
            T0,
            "JIRA-911");

        Assert.Equal(expected, DependencyProximity.Matches(candidate, query));
    }

    [Fact]
    public void Matches_BlankQuery_MatchesEverything()
    {
        var candidate = Item("x", "proj-1", "Whatever", 1);

        Assert.True(DependencyProximity.Matches(candidate, null));
        Assert.True(DependencyProximity.Matches(candidate, "   "));
    }

    [Fact]
    public void DisplayTitle_CapsLengthButKeepsFullTextForTooltip()
    {
        var title = "Fix the thing " + new string('y', 200);

        var display = DependencyProximity.DisplayTitle(title);

        Assert.True(display.Length < title.Length);
        Assert.EndsWith("…", display);
    }
}
