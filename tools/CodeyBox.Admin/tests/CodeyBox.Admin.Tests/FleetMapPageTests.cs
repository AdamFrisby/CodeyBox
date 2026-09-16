using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using CodeyBox.Admin.Model;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using FleetMapPage = CodeyBox.Admin.Web.Components.Pages.FleetMap;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Fleet map page, mapper, and frame-payload tests: the map renders from
/// package 2's model, and a quiet fleet produces byte-identical frames so the
/// render loop costs nothing between state changes.
/// </summary>
public sealed class FleetMapPageTests : BunitContext
{
    private sealed class FixedMapOptions : IOptionsMonitor<FleetMapOptions>
    {
        public FleetMapOptions CurrentValue { get; } = new();
        public FleetMapOptions Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<FleetMapOptions, string?> listener) => new Nop();
        private sealed class Nop : IDisposable { public void Dispose() { } }
    }

    private static WorkItemDto Item(
        string id,
        string state = "Queued",
        string agent = "Claude",
        List<string>? dependsOn = null,
        int? auditIterations = null) => new()
        {
            Id = id,
            ProjectId = "proj-1",
            Title = $"Work {id}",
            Agent = agent,
            State = state,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            DependsOn = dependsOn ?? [],
            DependsOnSatisfied = dependsOn is null,
            AuditIterations = auditIterations,
        };

    private FleetMapPageTests Setup(FakeApiClient fake)
    {
        Services.AddSingleton<ICodeyBoxApiClient>(fake);
        Services.AddSingleton<IOptionsMonitor<FleetMapOptions>>(new FixedMapOptions());
        JSInterop.Mode = JSRuntimeMode.Loose;
        return this;
    }

    [Fact]
    public void MapPage_RendersCanvasToolbarLegendAndTextEquivalent()
    {
        Setup(new FakeApiClient([Item("a1", "Working"), Item("b1", "Queued")]));

        var cut = Render<FleetMapPage>();

        cut.Markup.Contains("fleet-map-canvas").ShouldBeTrue();
        cut.Markup.Contains("Auto-follow").ShouldBeTrue();
        cut.Markup.Contains("Blocked").ShouldBeTrue();
        cut.Markup.Contains("/work-items/a1").ShouldBeTrue();
        cut.Markup.Contains("/work-items/b1").ShouldBeTrue();
    }

    [Fact]
    public void MapPage_ManualViewport_SuspendsAutoWithWayBack()
    {
        Setup(new FakeApiClient([Item("a1", "Working")]));
        var cut = Render<FleetMapPage>();

        cut.Instance.OnManualViewport(10, 20, 1.5);
        cut.Render();

        cut.Markup.Contains("Manual view").ShouldBeTrue();
        cut.Markup.Contains("Resume auto-follow").ShouldBeTrue();
    }

    [Fact]
    public void Mapper_ExcludesTerminalItems()
    {
        var snapshot = FleetMapSnapshotMapper.ToSnapshot(
            [
                Item("live-1", "Working"),
                Item("live-2", "Queued"),
                Item("gone-1", "Done"),
                Item("gone-2", "Failed"),
                Item("gone-3", "Cancelled"),
                Item("gone-4", "AuditFailed"),
            ],
            null, null, null, null, DateTimeOffset.UtcNow);

        var ids = snapshot.Items.Select(i => i.Id).ToList();
        Assert.Equal(["live-1", "live-2"], ids);
    }

    [Fact]
    public void Mapper_AttemptCount_PrefersLargestSignal()
    {
        var dto = Item("r1", "Working", auditIterations: 3);
        dto.UpstreamPushAttempts = 1;

        var snapshot = FleetMapSnapshotMapper.ToSnapshot(
            [dto], null, null, null, null, DateTimeOffset.UtcNow);

        Assert.Equal(3, Assert.Single(snapshot.Items).AttemptCount);
    }

    [Fact]
    public void Mapper_ToleratesNullsAndBoundsDeps()
    {
        var dto = Item("x", "Working", agent: "");
        dto.Title = "";
        dto.DependsOn = Enumerable.Range(0, 200).Select(i => $"dep-{i}").ToList();

        var snapshot = FleetMapSnapshotMapper.ToSnapshot(
            [dto, null!], null, null, null, null, DateTimeOffset.UtcNow);

        var mapped = Assert.Single(snapshot.Items);
        Assert.True(mapped.DependsOn.Count <= 64);
        var empty = FleetMapSnapshotMapper.ToSnapshot(
            null, null, null, null, null, DateTimeOffset.UtcNow);
        Assert.Empty(empty.Items);
    }

    [Fact]
    public void FramePayload_IdenticalSnapshots_AreByteIdentical()
    {
        var now = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        var first = BuildFrame(now);
        var second = BuildFrame(now);

        Assert.Equal(first, second);
    }

    [Fact]
    public void FramePayload_ChangedSnapshot_Differs()
    {
        var now = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        var options = new FleetMapOptions();
        var items = new List<AdminWorkItem>
        {
            new() { Id = "a", Title = "A", State = "Queued", Agent = "Claude", CreatedAt = now.AddHours(-1), UpdatedAt = now },
        };
        var before = FrameFor(items, null, now, options);
        var afterItems = new List<AdminWorkItem>
        {
            new() { Id = "a", Title = "A", State = "Working", Agent = "Claude", CreatedAt = now.AddHours(-1), UpdatedAt = now },
        };
        var after = FrameFor(afterItems, SnapshotOf(items, now), now, options);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void SeventyItemFleet_IdleRepaintProducesIdenticalPayload()
    {
        var now = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        var options = new FleetMapOptions();
        var items = new List<AdminWorkItem>();
        for (var i = 0; i < 78; i++)
        {
            var id = $"item-{i:D3}";
            items.Add(new AdminWorkItem
            {
                Id = id,
                Title = $"Work {i}",
                State = i % 5 == 0 ? "Working" : "Queued",
                Agent = "Claude",
                CreatedAt = now.AddHours(-1),
                UpdatedAt = now.AddMinutes(-5),
                DependsOn = i % 4 == 3 && i > 0 ? [$"item-{(i - 1):D3}"] : [],
            });
        }

        // First paint derives everything; the immediate repaint of the same
        // fleet must be a byte-identical frame with no transition events —
        // that is the near-zero idle cost, asserted structurally.
        var snapshot = SnapshotOf(items, now);
        var projection = FleetProjectionBuilder.Project(snapshot);
        var layout = FleetMapBuilder.DeriveInitial(items, projection.Chains.ToList(), options);
        var camera = CameraDirector.Initial(layout, now, new CameraViewSize(1600, 900), options);
        camera = CameraDirector.Next(camera, projection, layout, now, new CameraViewSize(1600, 900), options);
        var activities = ActivityAnalyzer.AnalyzeAll(snapshot);
        var first = FleetMapFrameBuilder.BuildPayload(
            snapshot, projection, layout,
            MapNodeStyler.StyleAll(snapshot, activities, camera.Viewport.Zoom, options),
            camera, MapTransitionDetector.Detect(null, null, snapshot, activities, projection.Chains.ToList(), options));

        var relayout = FleetMapBuilder.Update(layout, items, projection.Chains.ToList(), options);
        var recamera = CameraDirector.Next(camera, projection, relayout, now, new CameraViewSize(1600, 900), options);
        var requiet = MapTransitionDetector.Detect(
            snapshot, activities, snapshot, activities, projection.Chains.ToList(), options);
        var second = FleetMapFrameBuilder.BuildPayload(
            snapshot, projection, relayout,
            MapNodeStyler.StyleAll(snapshot, activities, recamera.Viewport.Zoom, options),
            recamera, requiet);

        Assert.Equal(first, second);
        Assert.Empty(requiet);
        Assert.Contains("\"items\":78", second, StringComparison.Ordinal);
    }

    [Fact]
    public void NodeTones_StayInsideTheSharedVocabulary()
    {
        // Contract with the stylesheet: the canvas palette mirrors the
        // .chip--{tone} classes in admin.css and StatusVocabulary tones.
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "active", "review", "queued", "rework", "wait", "fail", "done", "muted",
        };
        var options = new FleetMapOptions();
        var now = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new FleetSnapshot
        {
            Now = now,
            Items =
            [
                new AdminWorkItem { Id = "run", State = "Working", CreatedAt = now, UpdatedAt = now },
                new AdminWorkItem { Id = "ready", State = "Queued", CreatedAt = now, UpdatedAt = now },
                new AdminWorkItem { Id = "blocked", State = "Queued", DependsOn = ["ghost"], DependsOnSatisfied = false, CreatedAt = now, UpdatedAt = now },
                new AdminWorkItem { Id = "parked", State = "NeedsOperatorInput", CreatedAt = now, UpdatedAt = now },
                new AdminWorkItem { Id = "failed", State = "Failed", CreatedAt = now, UpdatedAt = now },
                new AdminWorkItem { Id = "weird", State = "SomethingNew", CreatedAt = now, UpdatedAt = now },
            ],
        };
        var badges = MapNodeStyler.StyleAll(snapshot, ActivityAnalyzer.AnalyzeAll(snapshot), 1.0, options);

        Assert.Equal(6, badges.Count);
        foreach (var badge in badges.Values)
        {
            Assert.Contains(badge.Tone, allowed);
        }
    }

    private static string BuildFrame(DateTimeOffset now)
    {
        var items = new List<AdminWorkItem>
        {
            new() { Id = "a", Title = "A", State = "Working", Agent = "Claude", CreatedAt = now.AddHours(-1), UpdatedAt = now },
            new() { Id = "b", Title = "B", State = "Queued", Agent = "Codex", CreatedAt = now.AddHours(-1), UpdatedAt = now, DependsOn = ["a"] },
        };
        return FrameFor(items, null, now, new FleetMapOptions());
    }

    private static FleetSnapshot SnapshotOf(List<AdminWorkItem> items, DateTimeOffset now) =>
        new() { Now = now, Items = items };

    private static string FrameFor(
        List<AdminWorkItem> items, FleetSnapshot? previous, DateTimeOffset now, FleetMapOptions options)
    {
        var snapshot = SnapshotOf(items, now);
        var projection = FleetProjectionBuilder.Project(snapshot);
        var layout = previous is null
            ? FleetMapBuilder.DeriveInitial(items, projection.Chains.ToList(), options)
            : FleetMapBuilder.Update(
                FleetMapBuilder.DeriveInitial(previous.Items.ToList(), projection.Chains.ToList(), options),
                items, projection.Chains.ToList(), options);
        var camera = CameraDirector.Initial(layout, now, new CameraViewSize(1600, 900), options);
        var activities = ActivityAnalyzer.AnalyzeAll(snapshot);
        var prevActivities = previous is null ? null : ActivityAnalyzer.AnalyzeAll(previous);
        return FleetMapFrameBuilder.BuildPayload(
            snapshot, projection, layout,
            MapNodeStyler.StyleAll(snapshot, activities, camera.Viewport.Zoom, options),
            camera,
            MapTransitionDetector.Detect(previous, prevActivities, snapshot, activities, projection.Chains.ToList(), options));
    }
}

/// <summary>Minimal xUnit-style assertions without new test dependencies.</summary>
file static class MarkupAssertions
{
    public static void ShouldBeTrue(this bool value)
    {
        Assert.True(value);
    }
}
