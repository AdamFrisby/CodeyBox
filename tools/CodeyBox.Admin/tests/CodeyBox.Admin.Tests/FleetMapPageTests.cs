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
            Prompt = $"Prompt for {id}",
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
        cut.Markup.Contains("a1").ShouldBeTrue();
        cut.Markup.Contains("b1").ShouldBeTrue();
    }

    [Fact]
    public async Task MapPage_ManualViewport_SuspendsAutoWithWayBack()
    {
        Setup(new FakeApiClient([Item("a1", "Working")]));
        var cut = Render<FleetMapPage>();

        // Panned far from any node at a zoom below the open band: manual, nothing focused.
        await cut.InvokeAsync(() => cut.Instance.OnManualViewport(5000, 5000, 0.8));

        cut.Markup.Contains("✋ Manual").ShouldBeTrue();
        cut.Markup.Contains("Manual view").ShouldBeTrue();
        cut.Markup.Contains("Resume auto-follow").ShouldBeTrue();
    }

    [Fact]
    public async Task MapPage_ManualZoomOverANode_FocusesIt_WithoutASecondPanel()
    {
        Setup(new FakeApiClient([Item("a1", "Working")]));
        var cut = Render<FleetMapPage>();

        // The single node sits at the layout origin; zoom past the open band over it.
        await cut.InvokeAsync(() => cut.Instance.OnManualViewport(0, 0, 3.0));

        cut.Markup.Contains("At “Work a1”").ShouldBeTrue();
        cut.Markup.Contains("fm-inspector").ShouldBeFalse(); // the bubble is the item's one on-canvas representation
        cut.Markup.Contains("fm-overlay").ShouldBeFalse();

        await cut.InvokeAsync(() => cut.Instance.OnCloseItem());
        cut.Markup.Contains("At “Work a1”").ShouldBeFalse();
    }

    [Fact]
    public async Task MapPage_NodeActivated_FocusesTheItem_AndDetailOpensTheOverlay()
    {
        Setup(new FakeApiClient([Item("a1", "Working"), Item("b1", "Queued", dependsOn: ["a1"])]));
        var cut = Render<FleetMapPage>();

        await cut.InvokeAsync(() => cut.Instance.OnNodeActivated("b1"));
        cut.Markup.Contains("At “Work b1”").ShouldBeTrue();
        cut.Markup.Contains("fm-chain-item--open").ShouldBeTrue();

        await cut.InvokeAsync(() => cut.Instance.OnNodeAction("b1", "detail"));
        cut.WaitForAssertion(() => Assert.Contains("fm-overlay", cut.Markup));
        cut.WaitForAssertion(() => Assert.Contains("Work b1", cut.Markup));
        cut.Markup.Contains("+ Add dependent").ShouldBeTrue();

        // Escape closes the overlay first, the focus second.
        await cut.InvokeAsync(() => cut.Instance.OnCloseItem());
        cut.Markup.Contains("fm-overlay").ShouldBeFalse();
        cut.Markup.Contains("At “Work b1”").ShouldBeTrue();
    }

    [Fact]
    public async Task MapPage_AddDependent_CreatesAnItemThatDependsOnTheParent()
    {
        var parent = Item("a1", "Working");
        parent.ProjectId = "proj-9";
        parent.Agent = "codex";
        var fake = new FakeApiClient([parent]);
        CreateWorkItemRequest? sent = null;
        fake.CreateHandler = req =>
        {
            sent = req;
            var created = Item("new-1", "Queued", agent: req.Agent ?? "", dependsOn: req.DependsOn);
            created.Title = req.Title;
            created.ProjectId = req.ProjectId;
            return created;
        };
        Setup(fake);
        var cut = Render<FleetMapPage>();

        await cut.InvokeAsync(() => cut.Instance.OnNodeAction("a1", "addDependent"));
        cut.Markup.Contains("New item, depending on “Work a1”").ShouldBeTrue();

        cut.Find("input[type=text]").Input("Follow-up: wire the auditor");
        cut.Find("textarea").Input("Do the follow-up work once the base lands.");
        await cut.InvokeAsync(() => cut.FindAll("button").Single(b => b.TextContent.Contains("Create dependent")).Click());

        cut.WaitForAssertion(() => Assert.NotNull(sent));
        Assert.Equal("proj-9", sent!.ProjectId);
        Assert.Equal(["a1"], sent.DependsOn);
        Assert.Equal("codex", sent.Agent);
        Assert.Equal("Follow-up: wire the auditor", sent.Title);
        cut.WaitForAssertion(() => Assert.Contains("Created “Follow-up: wire the auditor”", cut.Markup));
        // The new item is on the map (chain list) and the camera went to it.
        cut.WaitForAssertion(() => Assert.Contains("Follow-up: wire the auditor</button>", cut.Markup));
        cut.WaitForAssertion(() => Assert.Contains("At “Follow-up: wire the auditor”", cut.Markup));
    }

    [Fact]
    public async Task MapPage_ConfirmedCancel_CallsTheApi_AndUnknownActionsAreIgnored()
    {
        var fake = new FakeApiClient([Item("a1", "Working")]);
        Setup(fake);
        var cut = Render<FleetMapPage>();

        await cut.InvokeAsync(() => cut.Instance.OnNodeAction("a1", "cancel"));
        cut.Markup.Contains("Cancel this item?").ShouldBeTrue();
        Assert.Empty(fake.CancelledIds);

        await cut.InvokeAsync(() => cut.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel" && b.ClassList.Contains("fm-btn--danger")).Click());
        cut.WaitForAssertion(() => Assert.Equal(["a1"], fake.CancelledIds));

        await cut.InvokeAsync(() => cut.Instance.OnNodeAction("a1", "retryWork")); // not admitted while Working
        cut.Markup.Contains("Retry from work?").ShouldBeFalse();
        await cut.InvokeAsync(() => cut.Instance.OnNodeAction("ghost", "cancel"));
        Assert.Single(fake.CancelledIds);
    }

    [Fact]
    public void Mapper_WithVisibilityOptions_KeepsRelevantAndRecentSettledWork_AndNeedsYouItems()
    {
        var now = DateTimeOffset.UtcNow;
        var oldDone = Item("old-done", "Done");
        oldDone.UpdatedAt = now.AddDays(-30);
        var ancestor = Item("anc", "Done");
        ancestor.UpdatedAt = now.AddDays(-30);
        var fresh = Item("fresh", "Done");
        fresh.UpdatedAt = now.AddHours(-2);
        var failed = Item("failed", "AuditFailed");
        failed.UpdatedAt = now.AddDays(-30);

        var snapshot = FleetMapSnapshotMapper.ToSnapshot(
            [oldDone, ancestor, fresh, failed, Item("live", "Queued", dependsOn: ["anc"])],
            null, null, null, null, now, new TerminalVisibilityOptions { Horizon = TimeSpan.FromDays(3) });

        Assert.Equal(["anc", "failed", "fresh", "live"], snapshot.Items.Select(i => i.Id).OrderBy(i => i, StringComparer.Ordinal).ToList());

        // The default horizon is 180 days: a month-old landing is history worth showing.
        var wide = FleetMapSnapshotMapper.ToSnapshot([oldDone, fresh, Item("live", "Queued")], null, null, null, null, now, TerminalVisibilityOptions.Default);
        Assert.Contains("old-done", wide.Items.Select(i => i.Id));

        var legacy = FleetMapSnapshotMapper.ToSnapshot([oldDone, fresh, failed, Item("live", "Queued")], null, null, null, null, now);
        Assert.Equal(["live"], legacy.Items.Select(i => i.Id).ToList());
    }

    [Fact]
    public void MapPage_Rail_ShowsNeedsYouItems_TitleLed_AndSaysWhenEmpty()
    {
        var quiet = new FakeApiClient([Item("a1", "Working")]);
        Setup(quiet);
        var calm = Render<FleetMapPage>();
        calm.Markup.Contains("Nothing needs you.").ShouldBeTrue();
        calm.Markup.Contains("fm-rail-card").ShouldBeFalse();
    }

    [Fact]
    public void MapPage_Rail_CardLeadsWithTheTitle_AndOffersTheDecision()
    {
        var failed = Item("f1", "AuditFailed");
        failed.Title = "Plugin foundation 1/3: per-plugin Enabled state gating";
        Setup(new FakeApiClient([failed, Item("a1", "Working")]));
        var cut = Render<FleetMapPage>();

        cut.Markup.Contains("fm-rail-card").ShouldBeTrue();
        cut.Markup.Contains("data-rail-id=\"f1\"").ShouldBeTrue();
        cut.Markup.Contains("Plugin foundation 1/3: per-plugin Enabled state gating</button>").ShouldBeTrue();
        cut.Markup.Contains("retry from work").ShouldBeTrue();
        // The id is present only as a copy affordance, never as the card's handle.
        cut.Markup.Contains("data-copy=\"f1\"").ShouldBeTrue();
        cut.Markup.Contains("<code class=\"ident-code\" title=\"f1\">f1</code>").ShouldBeFalse();
    }

    [Fact]
    public async Task MapPage_SettledItems_AreOnTheMap_AndCounted()
    {
        var done = Item("d1", "Done");
        done.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1);
        Setup(new FakeApiClient([done, Item("q1", "Queued", dependsOn: ["d1"])]));
        var cut = Render<FleetMapPage>();

        cut.WaitForAssertion(() => Assert.Contains("settled", cut.Markup));
        cut.Markup.Contains("fm-chain-item--settled").ShouldBeTrue();
        // Settled work folding away is not an event: no pulse for a removed terminal item.
        await cut.InvokeAsync(() => cut.Instance.OnManualViewport(0, 0, 0.8));
        cut.Markup.Contains("Work d1").ShouldBeTrue();
    }

    [Fact]
    public async Task MapPage_SuggestionsBecomeGhosts_OnLiveParents_AndPromoteUsesTheApi()
    {
        var parent = Item("p1", "Working");
        var fake = new FakeApiClient([parent, Item("q1", "Queued", dependsOn: ["p1"])]);
        fake.SuggestionsOverride =
        [
            new SuggestionDto { Id = "sug-1", SourceWorkItemId = "p1", Title = "Add a regression test for the parser", Severity = "important", Category = "test-coverage", EstimatedEffort = "small", Rationale = "The parser has no coverage.", State = "open", CreatedAt = DateTimeOffset.UtcNow },
            new SuggestionDto { Id = "sug-2", SourceWorkItemId = "not-on-map", Title = "Elsewhere", Severity = "minor", State = "open", CreatedAt = DateTimeOffset.UtcNow },
            new SuggestionDto { Id = "sug-3", SourceWorkItemId = "p1", Title = "Old and dismissed", Severity = "important", State = "dismissed", CreatedAt = DateTimeOffset.UtcNow },
        ];
        Setup(fake);
        var cut = Render<FleetMapPage>();

        cut.WaitForAssertion(() => Assert.Contains("suggested here", cut.Markup));
        Assert.Contains("<b>1</b> suggested here", cut.Markup); // only the open one whose parent is on the map

        await cut.InvokeAsync(() => cut.Instance.OnGhostAction("sug-1", "promote"));
        cut.Markup.Contains("Promote “Add a regression test for the parser”").ShouldBeTrue();
        cut.Markup.Contains("The parser has no coverage.").ShouldBeTrue();
        cut.Markup.Contains("depending on “Work p1”").ShouldBeTrue();

        await cut.InvokeAsync(() => cut.FindAll("button").Single(b => b.TextContent.Trim() == "Promote to work item").Click());
        cut.WaitForAssertion(() => Assert.Contains("Promoted “Add a regression test for the parser”", cut.Markup));

        await cut.InvokeAsync(() => cut.Instance.OnGhostAction("sug-2", "dismiss")); // parent not on the map: still dismissable? no — not a ghost, ignored
        cut.Markup.Contains("Dismiss “Elsewhere”").ShouldBeTrue(); // it is an open suggestion the page knows; dismissing it is allowed from the list
        await cut.InvokeAsync(() => cut.FindAll("button").Single(b => b.TextContent.Trim() == "Dismiss" && b.ClassList.Contains("fm-btn--danger")).Click());
        cut.WaitForAssertion(() => Assert.Equal(["sug-2"], fake.DismissedSuggestionIds));
    }

    [Fact]
    public void FramePayload_CarriesGhostsAndReleaseFrames()
    {
        var now = new DateTimeOffset(2026, 9, 22, 7, 0, 0, TimeSpan.Zero);
        var items = new List<AdminWorkItem>
        {
            new() { Id = "a", Title = "A", State = "Working", Agent = "codex", CreatedAt = now.AddHours(-1), UpdatedAt = now, ReleaseId = "rel-1" },
            new() { Id = "b", Title = "B", State = "Queued", Agent = "codex", CreatedAt = now.AddHours(-1), UpdatedAt = now, DependsOn = ["a"], ReleaseId = "rel-1" },
            new() { Id = "c", Title = "C", State = "Queued", Agent = "codex", CreatedAt = now.AddHours(-1), UpdatedAt = now },
        };
        var options = new FleetMapOptions();
        var snapshot = new FleetSnapshot { Now = now, Items = items };
        var projection = FleetProjectionBuilder.Project(snapshot);
        var layout = FleetMapBuilder.DeriveInitial(items, projection.Chains.ToList(), options);
        var camera = CameraDirector.Initial(layout, now, null, options);
        var badges = MapNodeStyler.StyleAll(snapshot, projection.Activities, options.FullDetailZoom, options);
        var ghosts = SuggestionGhosts.Place(
            [new GhostSuggestion { Id = "s1", ParentId = "a", Title = "Suggested thing", Severity = "notable", CreatedAt = now }],
            layout, items, null, options);
        var frames = ReleaseContainers.Build(
            [new ReleaseInfo { Id = "rel-1", Name = "Autumn", State = "Open", BlockingFindings = 1, RemediationItemIds = ["c"] }],
            items.ToDictionary(i => i.Id, i => i.ReleaseId, StringComparer.Ordinal),
            items.ToDictionary(i => i.Id, i => i.State, StringComparer.Ordinal),
            layout, options);

        var payload = FleetMapFrameBuilder.BuildPayload(snapshot, projection, layout, badges, camera, [], options, null, null, ghosts, frames);

        Assert.Contains("\"ghosts\":[{\"id\":\"s1\",\"parent\":\"a\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"releases\":[{\"id\":\"rel-1\",\"name\":\"Autumn\",\"state\":\"Open\",\"total\":2,\"done\":0,\"blocking\":1", payload, StringComparison.Ordinal);
        Assert.Contains("\"remediation\":[\"c\"]", payload, StringComparison.Ordinal);
        // The "+ dependent" preview for "a" sits after its ghost, not on it.
        var ghostX = ghosts.Single().Shown.Single().X;
        var ghostY = ghosts.Single().Shown.Single().Y;
        Assert.DoesNotContain($"\"ghost\":{{\"x\":{ghostX},\"y\":{ghostY}}}", payload, StringComparison.Ordinal);
        // Same inputs, same bytes.
        Assert.Equal(payload, FleetMapFrameBuilder.BuildPayload(snapshot, projection, layout, badges, camera, [], options, null, null, ghosts, frames));
    }

    [Fact]
    public void FramePayload_CarriesBreaks_TicksWithInstants_AndSpacedLandings()
    {
        var now = new DateTimeOffset(2026, 9, 22, 7, 0, 0, TimeSpan.Zero);
        var items = new List<AdminWorkItem>
        {
            new() { Id = "a", Title = "First landing", State = "Done", Agent = "codex", CreatedAt = now.AddHours(-5), UpdatedAt = now.AddHours(-2.2) },
            new() { Id = "b", Title = "Second landing", State = "Done", Agent = "codex", CreatedAt = now.AddHours(-5), UpdatedAt = now.AddHours(-2), DependsOn = ["a"] },
            new() { Id = "old", Title = "Long ago", State = "Done", Agent = "codex", CreatedAt = now.AddDays(-3), UpdatedAt = now.AddHours(-40), DependsOn = ["a"] },
            new() { Id = "c", Title = "Running now", State = "Working", Agent = "codex", CreatedAt = now.AddHours(-1), UpdatedAt = now, DependsOn = ["a"] },
        };
        var options = new FleetMapOptions();
        var snapshot = new FleetSnapshot { Now = now, Items = items };
        var projection = FleetProjectionBuilder.Project(snapshot);
        var layout = FleetMapBuilder.DeriveInitial(items, projection.Chains.ToList(), options, now);
        var camera = CameraDirector.Initial(layout, now, null, options);
        var badges = MapNodeStyler.StyleAll(snapshot, projection.Activities, options.FullDetailZoom, options);

        var payload = FleetMapFrameBuilder.BuildPayload(snapshot, projection, layout, badges, camera, [], options);

        Assert.Contains("\"breaks\":[{", payload, StringComparison.Ordinal);
        Assert.Contains("\"label\":\"2d\",\"exact\":\"1d 13h\"", payload, StringComparison.Ordinal); // 37.8 hours of quiet, said out loud
        Assert.Contains("\"at\":\"2026-09-22T05:00:00", payload, StringComparison.Ordinal); // past ticks carry the instant
        Assert.True(layout.Nodes["a"].Spaced); // 12 minutes behind b in the same lane: a card's width apart
        Assert.Contains("\"spaced\":true", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("\"clusters\"", payload, StringComparison.Ordinal); // clusters are a rendering, not a layout object
        Assert.Equal(payload, FleetMapFrameBuilder.BuildPayload(snapshot, projection, layout, badges, camera, [], options));
    }

    [Fact]
    public async Task MapPage_ZoomingAcrossTheWholeRange_MovesNoNode()
    {
        // The real invariant, end to end: whatever the camera does — every
        // zoom from the smallest fit to far past item zoom — the frame's node
        // positions are the same bytes.
        var a = Item("a", "Done"); a.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-3);
        var b = Item("b", "Done", dependsOn: ["a"]); b.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2.9);
        var old = Item("old", "Done"); old.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-30);
        var c = Item("c", "Working", dependsOn: ["a"]);
        var d = Item("d", "Queued", dependsOn: ["c"]);
        Setup(new FakeApiClient([a, b, old, c, d]));
        JSInterop.Setup<bool>("codeyboxFleetMap.init", _ => true).SetResult(true);
        JSInterop.Setup<bool>("codeyboxFleetMap.render", _ => true).SetResult(true);
        var cut = Render<FleetMapPage>();
        cut.WaitForAssertion(() => Assert.Contains("Work a", cut.Markup));

        string Positions()
        {
            var payload = (string)JSInterop.Invocations.Last(i => i.Identifier == "codeyboxFleetMap.render").Arguments[1]!;
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            return string.Join(";", doc.RootElement.GetProperty("nodes").EnumerateArray()
                .Select(n => n.GetProperty("id").GetString() + "@" + n.GetProperty("x").GetRawText() + "," + n.GetProperty("y").GetRawText()));
        }

        await cut.InvokeAsync(() => cut.Instance.OnManualViewport(0, 0, 0.08));
        var baseline = Positions();
        Assert.Contains("a@", baseline, StringComparison.Ordinal);
        foreach (var zoom in new[] { 0.1, 0.13, 0.17, 0.2, 0.25, 0.33, 0.4, 0.5, 0.66, 0.8, 0.95, 1.0, 1.3, 1.6, 2.0, 2.2, 2.6, 3.0, 4.0, 6.0, 0.08 })
        {
            await cut.InvokeAsync(() => cut.Instance.OnManualViewport(-1000 * zoom, 300, zoom));
            Assert.Equal(baseline, Positions());
        }
        await cut.InvokeAsync(() => cut.Instance.OnNodeActivated("b"));
        Assert.Equal(baseline, Positions());
    }

    [Fact]
    public void FramePayload_ACameraMove_ChangesNoNodePosition()
    {
        // Zoom is a view transform: the frame's node positions are the same
        // bytes whatever the camera does — zoomed out, zoomed in, opened on an item.
        var now = new DateTimeOffset(2026, 9, 22, 7, 0, 0, TimeSpan.Zero);
        var items = new List<AdminWorkItem>
        {
            new() { Id = "done", Title = "Done", State = "Done", Agent = "codex", CreatedAt = now.AddHours(-5), UpdatedAt = now.AddHours(-2) },
            new() { Id = "run", Title = "Run", State = "Working", Agent = "codex", CreatedAt = now.AddHours(-1), UpdatedAt = now, DependsOn = ["done"] },
            new() { Id = "q1", Title = "Q1", State = "Queued", Agent = "codex", CreatedAt = now.AddHours(-1), UpdatedAt = now, DependsOn = ["run"] },
            new() { Id = "q2", Title = "Q2", State = "Queued", Agent = "codex", CreatedAt = now.AddHours(-1), UpdatedAt = now, DependsOn = ["run"] },
        };
        var options = new FleetMapOptions();
        var snapshot = new FleetSnapshot { Now = now, Items = items };
        var projection = FleetProjectionBuilder.Project(snapshot);
        var layout = FleetMapBuilder.DeriveInitial(items, projection.Chains.ToList(), options, now);
        var badges = MapNodeStyler.StyleAll(snapshot, projection.Activities, options.FullDetailZoom, options);
        var camera = CameraDirector.Initial(layout, now, null, options);

        string Positions(CameraState cam)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(FleetMapFrameBuilder.BuildPayload(snapshot, projection, layout, badges, cam, [], options));
            return string.Join(";", doc.RootElement.GetProperty("nodes").EnumerateArray()
                .Select(n => n.GetProperty("id").GetString() + "@" + n.GetProperty("x").GetRawText() + "," + n.GetProperty("y").GetRawText()));
        }

        var zoomedOut = CameraDirector.ApplyManual(camera, new CameraViewport { CenterX = 0, CenterY = 0, Zoom = 0.1 }, now, layout, options);
        var zoomedIn = CameraDirector.ApplyManual(camera, new CameraViewport { CenterX = 0, CenterY = 0, Zoom = 3.0 }, now, layout, options);
        var opened = CameraDirector.OpenItem(camera, "run", layout, now, options);
        Assert.Equal(Positions(camera), Positions(zoomedOut));
        Assert.Equal(Positions(camera), Positions(zoomedIn));
        Assert.Equal(Positions(camera), Positions(opened));
        Assert.Contains("@0,", Positions(camera)); // and the running item does sit at now
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

    public static void ShouldBeFalse(this bool value)
    {
        Assert.False(value);
    }
}
