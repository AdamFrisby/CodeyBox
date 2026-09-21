using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

/// <summary>
/// The forecast is an ordering with a capacity count: waves from the
/// dependency graph, batches from concurrency, ties from queue position —
/// and nothing it cannot know.
/// </summary>
public sealed class QueueForecastTests
{
    private static AdminWorkItem Q(string id, long pos = 0, IReadOnlyList<string>? dependsOn = null, string state = "Queued") =>
        Fixtures.Item(id, state: state, dependsOn: dependsOn) with { QueuePosition = pos };

    [Fact]
    public void Waves_FollowTheDependencyGraph()
    {
        var items = new List<AdminWorkItem>
        {
            Q("ready"), Q("done", state: "Done"), Q("after-done", dependsOn: ["done"]),
            Q("running", state: "Working"), Q("after-running", dependsOn: ["running"]),
            Q("after-queued", dependsOn: ["ready"]), Q("deep", dependsOn: ["after-queued"]),
            Q("dead", state: "Failed"), Q("after-dead", dependsOn: ["dead"]),
        };

        var slots = QueueForecast.Rank(items, null, capacity: 10, runningNow: 1);

        Assert.Equal(0, slots["ready"].Wave);
        Assert.Equal(0, slots["after-done"].Wave);
        Assert.Equal(1, slots["after-running"].Wave);
        Assert.Equal(1, slots["after-queued"].Wave);
        Assert.Equal(2, slots["deep"].Wave);
        Assert.Equal(1, slots["after-dead"].Wave); // held behind a dead end until someone acts
        Assert.DoesNotContain("running", slots.Keys);
        Assert.DoesNotContain("done", slots.Keys);
    }

    [Fact]
    public void Batches_CutByCapacity_MinusWhatIsRunning_AndANewWaveNeverSharesABatch()
    {
        var items = new List<AdminWorkItem> { Q("a", 1), Q("b", 2), Q("c", 3), Q("d", 4), Q("e", 5, ["a"]) };

        var slots = QueueForecast.Rank(items, null, capacity: 3, runningNow: 2);

        Assert.Equal(0, slots["a"].Batch); // one free slot right now
        Assert.Equal(1, slots["b"].Batch);
        Assert.Equal(1, slots["c"].Batch);
        Assert.Equal(1, slots["d"].Batch);
        Assert.Equal(2, slots["e"].Batch); // wave 1 starts a fresh batch even though batch 1 was full anyway
        Assert.Equal(1, slots["e"].Wave);
    }

    [Fact]
    public void WithinAWave_QueuePositionOrders_AndUnschedulableGoesLast()
    {
        var items = new List<AdminWorkItem> { Q("late", 9), Q("early", 1), Q("benched", 0) };
        var activities = new Dictionary<string, ItemActivity>(StringComparer.Ordinal)
        {
            ["benched"] = new ItemActivity { ItemId = "benched", Kind = ActivityKind.BlockedByAgentAvailability, Summary = "agent paused" },
        };

        var slots = QueueForecast.Rank(items, activities, capacity: 1, runningNow: 0);

        Assert.Equal(0, slots["early"].Batch);
        Assert.Equal(1, slots["late"].Batch);
        Assert.Equal(2, slots["benched"].Batch);
        Assert.True(slots["benched"].Unschedulable);
        Assert.False(slots["early"].Unschedulable);
    }

    [Fact]
    public void CyclesAndOddInput_DoNotHang()
    {
        var items = new List<AdminWorkItem> { Q("a", dependsOn: ["b"]), Q("b", dependsOn: ["a"]), Q("c", dependsOn: ["ghost"]) };
        var slots = QueueForecast.Rank(items, null, capacity: 0, runningNow: -5);
        Assert.Equal(3, slots.Count);
        Assert.All(slots.Values, s => Assert.True(s.Batch >= 0));
    }
}
