using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

/// <summary>
/// Node rendering across zoom levels: full detail at working zoom, a short
/// label mid-zoom, a bare shape far out — and no text ever renders below the
/// readable minimum.
/// </summary>
public sealed class NodeStyleTests
{
    private static readonly FleetMapOptions Options = new();

    private static MapNodeBadge StyleAt(string state, double zoom, string id = "item-abcdef123456")
    {
        var item = Fixtures.Item(id, title: "Deploy the thing to production now please", state: state, agent: "Codex");
        var snapshot = Fixtures.Snapshot([item]);
        var activities = ActivityAnalyzer.AnalyzeAll(snapshot);
        return MapNodeStyler.Style(item, activities[id], zoom, Fixtures.Now, Options);
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(1.0)]
    public void FullDetail_AtWorkingZoomAndAbove(double zoom)
    {
        var badge = StyleAt("Working", zoom);

        Assert.Equal(NodeDetailLevel.Full, badge.Detail);
        Assert.NotNull(badge.Label);
        Assert.NotNull(badge.SubLabel);
        Assert.Contains("Codex", badge.SubLabel);
        Assert.Null(badge.ShortLabel);
        Assert.True(badge.TitleTextPx >= Options.MinReadableTextPx);
        Assert.True(badge.SubTextPx >= Options.MinReadableTextPx);
    }

    [Theory]
    [InlineData(0.9)]
    [InlineData(0.55)]
    public void CompactDetail_MidZoomKeepsShortLabel(double zoom)
    {
        var badge = StyleAt("Working", zoom);

        Assert.Equal(NodeDetailLevel.Compact, badge.Detail);
        Assert.Equal("item-abc", badge.ShortLabel);
        Assert.Null(badge.Label);
        Assert.Null(badge.SubLabel);
    }

    [Theory]
    [InlineData(0.54)]
    [InlineData(0.2)]
    public void DotDetail_FarZoomRendersNoText(double zoom)
    {
        var badge = StyleAt("Working", zoom);

        Assert.Equal(NodeDetailLevel.Dot, badge.Detail);
        Assert.Null(badge.Label);
        Assert.Null(badge.SubLabel);
        Assert.Null(badge.ShortLabel);
    }

    [Theory]
    [InlineData(0.2)]
    [InlineData(0.55)]
    [InlineData(1.0)]
    [InlineData(2.5)]
    public void NoUnreadableText_AtAnyZoom(double zoom)
    {
        var snapshot = Fixtures.Snapshot([
            Fixtures.Item("run", state: "Working"),
            Fixtures.Item("fail", state: "Failed"),
            Fixtures.Item("wait", state: "Queued"),
        ]);
        var badges = MapNodeStyler.StyleAll(snapshot, ActivityAnalyzer.AnalyzeAll(snapshot), zoom, Options);

        foreach (var badge in badges.Values)
        {
            var hasText = badge.Label is not null || badge.SubLabel is not null || badge.ShortLabel is not null;
            if (badge.Detail == NodeDetailLevel.Dot)
            {
                Assert.False(hasText);
            }
            if (hasText)
            {
                Assert.True(badge.TitleTextPx >= Options.MinReadableTextPx);
                Assert.True(badge.SubTextPx >= Options.MinReadableTextPx);
            }
        }
    }

    [Fact]
    public void Blocked_Running_AndSlotWaiting_AreVisuallyDistinct()
    {
        var blocked = StyleAt("Queued", 1.0, id: "blocked-1");
        var workers = new AdminWorkerCapacity { GlobalMaxConcurrent = 1, GlobalRunning = 1 };
        var snapshot = Fixtures.Snapshot([
            Fixtures.Item("blocked-1", state: "Queued", dependsOn: ["ghost"], dependsOnSatisfied: false),
            Fixtures.Item("running-1", state: "Working"),
            Fixtures.Item("waiting-1", state: "Queued"),
        ], workers: workers);
        var activities = ActivityAnalyzer.AnalyzeAll(snapshot);
        blocked = MapNodeStyler.Style(snapshot.Items[0], activities["blocked-1"], 1.0, Fixtures.Now, Options);
        var running = MapNodeStyler.Style(snapshot.Items[1], activities["running-1"], 1.0, Fixtures.Now, Options);
        var waiting = MapNodeStyler.Style(snapshot.Items[2], activities["waiting-1"], 1.0, Fixtures.Now, Options);

        Assert.Equal(ActivityKind.BlockedByDependency, activities["blocked-1"].Kind);
        Assert.Equal(ActivityKind.Running, activities["running-1"].Kind);
        Assert.Equal(ActivityKind.WaitingForSlot, activities["waiting-1"].Kind);
        Assert.NotEqual(blocked.Shape, running.Shape);
        Assert.NotEqual(blocked.Shape, waiting.Shape);
        Assert.NotEqual(blocked.Tone, running.Tone);
        Assert.NotEqual(blocked.Tone, waiting.Tone);
    }

    [Fact]
    public void AttemptCount_ShowsWhenKnown()
    {
        var item = Fixtures.Item("retry-1", state: "Working") with { AttemptCount = 3 };
        var snapshot = Fixtures.Snapshot([item]);
        var badge = MapNodeStyler.Style(
            item, ActivityAnalyzer.Analyze(item, snapshot), 1.0, Fixtures.Now, Options);

        Assert.Contains("try 3", badge.SubLabel);
    }

    [Fact]
    public void FailedNode_CarriesUrgencyRing_RunningDoesNot()
    {
        Assert.True(StyleAt("Failed", 1.0).HasUrgencyRing);
        Assert.False(StyleAt("Working", 1.0).HasUrgencyRing);
        Assert.False(StyleAt("Queued", 1.0).HasUrgencyRing);
    }
}
