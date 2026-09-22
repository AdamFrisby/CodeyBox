using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

/// <summary>
/// What a position on the axis means: an instant inside a faithful run, the
/// cut when inside one (never an invented timestamp), now, or a predicted batch.
/// </summary>
public sealed class AxisReadingTests
{
    private static readonly FleetMapOptions Options = new();
    private static readonly DateTimeOffset Now = Fixtures.Now;

    private static AdminWorkItem At(string id, string state, double hoursAgo = 0, long queuePosition = 0) =>
        Fixtures.Item(id, state: state, updatedAt: Now.AddHours(-hoursAgo)) with { QueuePosition = queuePosition };

    [Fact]
    public void ReadsInstants_Cuts_Now_AndBatches()
    {
        var items = new List<AdminWorkItem> { At("a", "Done", 1), At("b", "Done", 1.5), At("old", "Done", 30), At("run", "Working") }
            .Concat(Enumerable.Range(0, 8).Select(i => At($"q{i}", "Queued", queuePosition: i))).ToList();
        var layout = FleetMapBuilder.DeriveInitial(items, ChainGrouping.BuildChains(items).ToList(), Options, Now, capacity: 2);
        var bucket = layout.NowBucket;
        var rate = TimeAxisScale.UnitsPerMinute(Options);

        Assert.Equal(AxisZone.Now, TimeAxisScale.Read(0, layout, Options).Zone);
        Assert.Equal(AxisZone.Now, TimeAxisScale.Read(Options.NodeWidth / 4, layout, Options).Zone);

        // Between the latest landing and now: the fixed "since" cut — no instant is invented for it.
        var since = TimeAxisScale.Read(-(Options.NodeWidth + Options.BreakWidth / 2), layout, Options);
        Assert.Equal(AxisZone.Past, since.Zone);
        Assert.Null(since.At);
        Assert.True(since.Break!.SinceLatest);
        var atA = TimeAxisScale.Read(layout.Nodes["a"].X, layout, Options);
        Assert.Equal(bucket.AddHours(-1), atA.At!.Value, TimeSpan.FromSeconds(1));

        // Inside the run holding a and b (30 minutes apart: faithful).
        var run = Assert.Single(layout.Stretches, r => r.Newest == bucket.AddHours(-1));
        var mid = TimeAxisScale.Read(run.NewestX - 15 * rate, layout, Options);
        Assert.Equal(bucket.AddMinutes(-75), mid.At!.Value, TimeSpan.FromSeconds(1));
        var atB = TimeAxisScale.Read(layout.Nodes["b"].X, layout, Options);
        Assert.Equal(bucket.AddHours(-1.5), atB.At!.Value, TimeSpan.FromSeconds(1));

        // Inside the cut before "old": the cut, no instant.
        var cut = Assert.Single(layout.Breaks, b => !b.SinceLatest);
        var inCut = TimeAxisScale.Read(cut.X + cut.Width / 2, layout, Options);
        Assert.Equal(AxisZone.Past, inCut.Zone);
        Assert.Null(inCut.At);
        Assert.Same(cut, inCut.Break);

        // The forecast: nearest batch column, 0 = next.
        Assert.Equal(0, TimeAxisScale.Read(TimeAxisScale.FutureX(0, Options) + 10, layout, Options).Batch);
        Assert.Equal(2, TimeAxisScale.Read(TimeAxisScale.FutureX(2, Options) - 10, layout, Options).Batch);
        Assert.Equal(AxisZone.Future, TimeAxisScale.Read(TimeAxisScale.FutureX(2, Options), layout, Options).Zone);

        Assert.Equal(AxisZone.Now, TimeAxisScale.Read(double.NaN, layout, Options).Zone);
        Assert.Equal(AxisZone.Past, TimeAxisScale.Read(-1e9, layout, Options).Zone);
    }
}
