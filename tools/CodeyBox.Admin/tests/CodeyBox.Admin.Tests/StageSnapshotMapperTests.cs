using System.Text.Json;
using CodeyBox.Admin.Model;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// The per-item surfaces map onto the stage-pipeline input at the edge: only
/// state transitions are read from the timeline, a missing surface stays
/// null (unknown, never empty), and the frame carries the open item.
/// </summary>
public sealed class StageSnapshotMapperTests
{
    private static TimelineEntryDto Entry(string kind, string json, int minute) => new()
    {
        OccurredAt = new DateTimeOffset(2026, 9, 21, 7, minute, 0, TimeSpan.Zero),
        Kind = kind,
        Summary = kind,
        Details = JsonDocument.Parse(json).RootElement.Clone(),
    };

    [Fact]
    public void Mapper_ReadsOnlyStateTransitions_WithFromToAndWorker()
    {
        var item = new WorkItemDto { Id = "x", State = "Auditing", Agent = "codex", WorkAgent = "copilot" };
        var timeline = new WorkItemTimelineDto
        {
            WorkItemId = "x",
            Entries =
            [
                Entry("state_transition", """{"from":null,"to":"Queued","title":"t"}""", 0),
                Entry("state_transition", """{"from":"Queued","to":"Working","workerId":7317}""", 1),
                Entry("agent_started", """{"agent":"copilot","phase":"work"}""", 2),
                Entry("state_transition", """{"phase":"audit (auto-pick)"}""", 3),
                Entry("auditor_run", """{"name":"csharp:build","iteration":1}""", 4),
            ],
        };
        var progress = new AuditProgressListDto
        {
            WorkItemId = "x",
            Progress = [new AuditProgressRowDto { Iteration = 1, MaxIterations = 6, Status = "in_progress" }],
        };

        var snapshot = StageSnapshotMapper.ToStageSnapshot(item, timeline, progress);

        Assert.NotNull(snapshot.Transitions);
        Assert.Equal(2, snapshot.Transitions!.Count);
        Assert.Null(snapshot.Transitions[0].From);
        Assert.Equal("Queued", snapshot.Transitions[0].To);
        Assert.Equal(7317, snapshot.Transitions[1].WorkerId);
        Assert.Equal("copilot", snapshot.WorkAgent);
        var iteration = Assert.Single(snapshot.AuditIterations!);
        Assert.Equal("in_progress", iteration.Status);

        var pipeline = ItemStagePipelineBuilder.Build(snapshot);
        Assert.Equal(StageHistory.Full, pipeline.History);
        Assert.Equal(PipelineStage.Audit, pipeline.Current);
        Assert.Equal("1/6 running", pipeline.Stages.Single(s => s.Stage == PipelineStage.Audit).Detail);
    }

    [Fact]
    public void Mapper_MissingSurfaces_StayNull()
    {
        var item = new WorkItemDto { Id = "x", State = "Reworking", Agent = "codex" };

        var snapshot = StageSnapshotMapper.ToStageSnapshot(item, null, null);

        Assert.Null(snapshot.Transitions);
        Assert.Null(snapshot.AuditIterations);
        Assert.Equal("codex", snapshot.WorkAgent);
        Assert.Equal(StageHistory.StateOnly, ItemStagePipelineBuilder.Build(snapshot).History);
    }

    [Fact]
    public void FramePayload_CarriesTheOpenItemsPipeline_AndLoadingIsHonest()
    {
        var now = new DateTimeOffset(2026, 9, 21, 7, 0, 0, TimeSpan.Zero);
        var items = new List<AdminWorkItem>
        {
            new() { Id = "a", Title = "A", State = "Reworking", Agent = "Claude", CreatedAt = now.AddHours(-1), UpdatedAt = now },
            new() { Id = "b", Title = "B", State = "Queued", Agent = "Codex", CreatedAt = now.AddHours(-1), UpdatedAt = now, DependsOn = ["a"] },
        };
        var options = new FleetMapOptions();
        var snapshot = new FleetSnapshot { Now = now, Items = items };
        var projection = FleetProjectionBuilder.Project(snapshot);
        var layout = FleetMapBuilder.DeriveInitial(items, projection.Chains.ToList(), options);
        var camera = CameraDirector.OpenItem(CameraDirector.Initial(layout, now, null, options), "a", layout, now, options);
        var badges = MapNodeStyler.StyleAll(snapshot, projection.Activities, options.FullDetailZoom, options);

        var loading = FleetMapFrameBuilder.BuildPayload(
            snapshot, projection, layout, badges, camera, [], options,
            new Dictionary<string, OpenItemPayload> { ["a"] = new OpenItemPayload { Id = "a", Status = "loading" } });
        Assert.Contains("\"history\":\"loading\"", loading, StringComparison.Ordinal);
        Assert.Contains("\"open\":\"a\"", loading, StringComparison.Ordinal);
        Assert.Contains("\"waiting\":1", loading, StringComparison.Ordinal);
        Assert.Contains("\"actions\":[\"addDependent\",\"cancel\"]", loading, StringComparison.Ordinal);

        var pipeline = ItemStagePipelineBuilder.Build(new StageSnapshot
        {
            ItemId = "a",
            State = "Reworking",
            Transitions =
            [
                new StageTransition { At = now.AddMinutes(-30), To = "Queued" },
                new StageTransition { At = now.AddMinutes(-29), From = "Queued", To = "Working", WorkerId = 1 },
                new StageTransition { At = now.AddMinutes(-20), From = "Working", To = "WorkComplete" },
                new StageTransition { At = now.AddMinutes(-19), From = "WorkComplete", To = "Auditing" },
                new StageTransition { At = now.AddMinutes(-10), From = "Auditing", To = "Reworking" },
                new StageTransition { At = now.AddMinutes(-8), From = "Reworking", To = "Auditing" },
                new StageTransition { At = now.AddMinutes(-2), From = "Auditing", To = "Reworking" },
            ],
        });
        var ready = FleetMapFrameBuilder.BuildPayload(
            snapshot, projection, layout, badges, camera, [], options,
            new Dictionary<string, OpenItemPayload> { ["a"] = new OpenItemPayload { Id = "a", Status = "ready", Pipeline = pipeline } });
        Assert.Contains("\"history\":\"full\"", ready, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"rework\",\"count\":2", ready, StringComparison.Ordinal); // two Auditing → Reworking hops
        Assert.Contains("\"tier\":0", ready, StringComparison.Ordinal);
        Assert.Contains("\"current\":\"work\"", ready, StringComparison.Ordinal);
        Assert.NotEqual(loading, ready);

        // Same inputs, same bytes: the idle gate holds with an open item too.
        Assert.Equal(ready, FleetMapFrameBuilder.BuildPayload(
            snapshot, projection, layout, badges, camera, [], options,
            new Dictionary<string, OpenItemPayload> { ["a"] = new OpenItemPayload { Id = "a", Status = "ready", Pipeline = pipeline } }));
    }
}
