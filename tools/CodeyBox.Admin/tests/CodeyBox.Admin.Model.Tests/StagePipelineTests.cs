using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

/// <summary>
/// The zoomed-in level: an item on the circuit plan → work → audit ⇒ merge →
/// landed, derived from its recorded transitions. Audit is a gate: its fail
/// path is a counted return edge to work, never a stage. Interruptions are
/// annotated as themselves and never counted as rework, parked states do not
/// move the item, and a missing timeline is reported as unknown history.
/// </summary>
public sealed class StagePipelineTests
{
    private static readonly DateTimeOffset T0 = Fixtures.Now;

    private static StageTransition Hop(int minute, string? from, string to, int? worker = null) => new()
    {
        At = T0.AddMinutes(minute),
        From = from,
        To = to,
        WorkerId = worker,
    };

    private static StageNode Stage(ItemStagePipeline p, PipelineStage s) => p.Stages.Single(n => n.Stage == s);

    [Fact]
    public void ThereAreFiveStages_AndReworkIsNotOneOfThem()
    {
        var pipeline = ItemStagePipelineBuilder.Build(new StageSnapshot { ItemId = "x", State = "Queued", Transitions = [Hop(0, null, "Queued")] });
        Assert.Equal(
            [PipelineStage.Plan, PipelineStage.Work, PipelineStage.Audit, PipelineStage.Merge, PipelineStage.Landed],
            pipeline.Stages.Select(s => s.Stage).ToList());
        Assert.Equal(PipelineStage.Work, ItemStagePipelineBuilder.StageOf("Reworking"));
        Assert.Equal(PipelineStage.Merge, ItemStagePipelineBuilder.StageOf("ReworkingForConflict"));
    }

    [Fact]
    public void StraightThrough_VisitsEachStageOnce_NoReturns()
    {
        var pipeline = ItemStagePipelineBuilder.Build(new StageSnapshot
        {
            ItemId = "x",
            State = "Merging",
            Transitions =
            [
                Hop(0, null, "Queued"),
                Hop(1, "Queued", "Working", 3),
                Hop(2, "Working", "Working"),
                Hop(20, "Working", "WorkComplete"),
                Hop(21, "WorkComplete", "Auditing"),
                Hop(30, "Auditing", "AuditPassed"),
                Hop(31, "AuditPassed", "Merging"),
            ],
            AuditIterations = [new StageAuditIteration { Iteration = 1, MaxIterations = 6, Status = "complete", BlockingFindings = 0 }],
        });

        Assert.Equal(StageHistory.Full, pipeline.History);
        Assert.Equal(PipelineStage.Merge, pipeline.Current);
        Assert.Empty(pipeline.Loops);
        Assert.Equal(StageStatus.NotReached, Stage(pipeline, PipelineStage.Plan).Status);
        Assert.Equal(StageStatus.Visited, Stage(pipeline, PipelineStage.Work).Status);
        Assert.Equal(StageStatus.Visited, Stage(pipeline, PipelineStage.Audit).Status);
        Assert.Equal(StageStatus.Current, Stage(pipeline, PipelineStage.Merge).Status);
        Assert.Equal(StageStatus.NotReached, Stage(pipeline, PipelineStage.Landed).Status);
        Assert.Equal(1, Stage(pipeline, PipelineStage.Audit).Visits);
        Assert.Equal("1/6 · 0 blk", Stage(pipeline, PipelineStage.Audit).Detail);
        Assert.Contains("No returns", pipeline.Summary);
    }

    [Fact]
    public void AuditSendingItBackThreeTimes_IsOneReturnEdgeCountedThree_AndWorkVisitedFourTimes()
    {
        var transitions = new List<StageTransition>
        {
            Hop(0, null, "Queued"), Hop(1, "Queued", "Working", 1), Hop(10, "Working", "WorkComplete"), Hop(11, "WorkComplete", "Auditing"),
        };
        var minute = 12;
        for (var cycle = 0; cycle < 3; cycle++)
        {
            transitions.Add(Hop(minute++, "Auditing", "Reworking"));
            transitions.Add(Hop(minute++, "Reworking", "WorkComplete"));
            transitions.Add(Hop(minute++, "WorkComplete", "Auditing"));
        }
        var pipeline = ItemStagePipelineBuilder.Build(new StageSnapshot { ItemId = "x", State = "Auditing", Transitions = transitions });

        var edge = Assert.Single(pipeline.Loops);
        Assert.Equal(StageLoopKind.Rework, edge.Kind);
        Assert.Equal(PipelineStage.Audit, edge.From);
        Assert.Equal(PipelineStage.Work, edge.To);
        Assert.Equal(3, edge.Count);
        Assert.Equal("rework ×3", edge.Label);
        Assert.Equal(4, Stage(pipeline, PipelineStage.Work).Visits);
        Assert.Equal(4, Stage(pipeline, PipelineStage.Audit).Visits);
        Assert.Equal(StageStatus.Current, Stage(pipeline, PipelineStage.Audit).Status);
        Assert.Contains("sent it back ×3", pipeline.Summary);
    }

    [Fact]
    public void SittingInWorkForTheSecondTime_IsAtWork_WithTheReturnEdgeShowingOne()
    {
        var pipeline = ItemStagePipelineBuilder.Build(new StageSnapshot
        {
            ItemId = "x",
            State = "Reworking",
            Transitions =
            [
                Hop(0, null, "Queued"), Hop(1, "Queued", "Working", 1), Hop(5, "Working", "WorkComplete"),
                Hop(6, "WorkComplete", "Auditing"), Hop(9, "Auditing", "Reworking"),
            ],
        });

        Assert.Equal(PipelineStage.Work, pipeline.Current);
        Assert.Equal(StageStatus.Current, Stage(pipeline, PipelineStage.Work).Status);
        Assert.Equal(2, Stage(pipeline, PipelineStage.Work).Visits);
        Assert.Equal(StageStatus.Visited, Stage(pipeline, PipelineStage.Audit).Status);
        var edge = Assert.Single(pipeline.Loops);
        Assert.Equal(StageLoopKind.Rework, edge.Kind);
        Assert.Equal(1, edge.Count);
        Assert.Contains("at work (Reworking, 2nd time)", pipeline.Summary);
    }

    [Fact]
    public void Interruptions_AreNotRework()
    {
        // Real shape from the fleet: audit interrupted, item re-picked three times, then audit restarted.
        var pipeline = ItemStagePipelineBuilder.Build(new StageSnapshot
        {
            ItemId = "x",
            State = "Working",
            Transitions =
            [
                Hop(0, null, "Queued"), Hop(1, "Queued", "Working", 3), Hop(2, "Working", "Working"),
                Hop(30, "Working", "WorkComplete"), Hop(31, "WorkComplete", "Auditing"),
                Hop(60, "Auditing", "Working", 450),
                Hop(90, "Working", "Working", 1996),
                Hop(120, "Working", "Working", 3556),
            ],
        });

        Assert.DoesNotContain(pipeline.Loops, l => l.Kind == StageLoopKind.Rework);
        var back = pipeline.Loops.Single(l => l.From == PipelineStage.Audit && l.To == PipelineStage.Work);
        Assert.Equal(StageLoopKind.Interruption, back.Kind);
        var pickups = pipeline.Loops.Single(l => l.From == PipelineStage.Work && l.To == PipelineStage.Work);
        Assert.Equal(StageLoopKind.Interruption, pickups.Kind);
        Assert.Equal(2, pickups.Count);
        Assert.Contains("3 interruptions", pipeline.Summary);
    }

    [Fact]
    public void ConflictRework_IsTheMergeCircuit_NotABox()
    {
        var pipeline = ItemStagePipelineBuilder.Build(new StageSnapshot
        {
            ItemId = "x",
            State = "Done",
            Transitions =
            [
                Hop(0, null, "Queued"), Hop(1, "Queued", "Working", 1), Hop(5, "Working", "WorkComplete"),
                Hop(6, "WorkComplete", "Auditing"), Hop(9, "Auditing", "AuditPassed"), Hop(10, "AuditPassed", "Merging"),
                Hop(11, "Merging", "ReworkingForConflict"), Hop(15, "ReworkingForConflict", "Merging"),
                Hop(16, "Merging", "Merged"), Hop(17, "Merged", "UpstreamPushing"), Hop(18, "UpstreamPushing", "Done"),
            ],
        });

        var edge = Assert.Single(pipeline.Loops);
        Assert.Equal(StageLoopKind.Conflict, edge.Kind);
        Assert.Equal(PipelineStage.Merge, edge.From);
        Assert.Equal(PipelineStage.Merge, edge.To);
        Assert.Equal(PipelineStage.Landed, pipeline.Current);
        Assert.Equal(StageStatus.Current, Stage(pipeline, PipelineStage.Landed).Status);
        Assert.Contains("1 conflict round", pipeline.Summary);
    }

    [Fact]
    public void ParkedState_DoesNotMoveTheItem_ButRendersParked()
    {
        var pipeline = ItemStagePipelineBuilder.Build(new StageSnapshot
        {
            ItemId = "x",
            State = "WaitingForQuotaReset",
            Transitions =
            [
                Hop(0, null, "Queued"), Hop(1, "Queued", "Working", 1), Hop(5, "Working", "WorkComplete"),
                Hop(6, "WorkComplete", "Auditing"), Hop(7, "Auditing", "WaitingForQuotaReset"),
            ],
        });

        Assert.Equal(PipelineStage.Audit, pipeline.Current);
        Assert.Equal(StageStatus.Parked, Stage(pipeline, PipelineStage.Audit).Status);
        Assert.Contains("parked at audit", pipeline.Summary);
    }

    [Fact]
    public void FailedState_MarksTheStageItFailedFrom()
    {
        var pipeline = ItemStagePipelineBuilder.Build(new StageSnapshot
        {
            ItemId = "x",
            State = "AuditFailed",
            Transitions =
            [
                Hop(0, null, "Queued"), Hop(1, "Queued", "Working", 1), Hop(5, "Working", "WorkComplete"),
                Hop(6, "WorkComplete", "Auditing"), Hop(7, "Auditing", "Reworking"), Hop(8, "Reworking", "Auditing"),
                Hop(9, "Auditing", "AuditFailed"),
            ],
        });

        Assert.Equal(PipelineStage.Audit, pipeline.Current);
        Assert.Equal(StageStatus.Failed, Stage(pipeline, PipelineStage.Audit).Status);
        Assert.Single(pipeline.Loops, l => l.Kind == StageLoopKind.Rework);
    }

    [Fact]
    public void OperatorRetry_OutOfFailure_IsARetryEdgeNotAnInterruption()
    {
        var pipeline = ItemStagePipelineBuilder.Build(new StageSnapshot
        {
            ItemId = "x",
            State = "Working",
            Transitions =
            [
                Hop(0, null, "Queued"), Hop(1, "Queued", "Working", 1), Hop(5, "Working", "WorkComplete"),
                Hop(6, "WorkComplete", "Auditing"), Hop(9, "Auditing", "AuditFailed"),
                Hop(60, "AuditFailed", "Queued"), Hop(61, "Queued", "Working", 9),
            ],
        });

        var edge = Assert.Single(pipeline.Loops);
        Assert.Equal(StageLoopKind.Retry, edge.Kind);
        Assert.Equal(PipelineStage.Audit, edge.From);
        Assert.Equal(PipelineStage.Work, edge.To);
        Assert.Equal(StageStatus.Current, Stage(pipeline, PipelineStage.Work).Status);
        Assert.Equal(StageStatus.Visited, Stage(pipeline, PipelineStage.Audit).Status);
    }

    [Fact]
    public void RetryBackIntoTheSameStage_IsARetryEdge()
    {
        // Real shape: Working → Failed, then Failed → Working (worker N).
        var pipeline = ItemStagePipelineBuilder.Build(new StageSnapshot
        {
            ItemId = "x",
            State = "Working",
            Transitions =
            [
                Hop(0, null, "Queued"), Hop(1, "Queued", "Working", 336), Hop(2, "Working", "Working"),
                Hop(60, "Working", "Failed"), Hop(90, null, "None"), Hop(120, "Failed", "Working", 3023), Hop(121, "Working", "Working"),
            ],
        });

        var edge = Assert.Single(pipeline.Loops);
        Assert.Equal(StageLoopKind.Retry, edge.Kind);
        Assert.Equal(PipelineStage.Work, edge.From);
        Assert.Equal(PipelineStage.Work, edge.To);
        Assert.Equal(2, Stage(pipeline, PipelineStage.Work).Visits);
        Assert.Equal(StageStatus.Current, Stage(pipeline, PipelineStage.Work).Status);
    }

    [Fact]
    public void NoTimeline_OnlyTheCurrentStageIsAsserted()
    {
        var pipeline = ItemStagePipelineBuilder.Build(new StageSnapshot
        {
            ItemId = "x",
            State = "Merging",
            Transitions = null,
            AuditIterations = null,
        });

        Assert.Equal(StageHistory.StateOnly, pipeline.History);
        Assert.NotEmpty(pipeline.HistoryNote);
        Assert.Equal(PipelineStage.Merge, pipeline.Current);
        Assert.Equal(StageStatus.Unknown, Stage(pipeline, PipelineStage.Plan).Status);
        Assert.Equal(StageStatus.Unknown, Stage(pipeline, PipelineStage.Work).Status);
        Assert.Equal(StageStatus.Unknown, Stage(pipeline, PipelineStage.Audit).Status);
        Assert.Equal(StageStatus.Current, Stage(pipeline, PipelineStage.Merge).Status);
        Assert.Equal(StageStatus.NotReached, Stage(pipeline, PipelineStage.Landed).Status);
        Assert.Empty(pipeline.Loops);
        Assert.Contains("history unavailable", pipeline.Summary);
    }

    [Fact]
    public void QueuedItem_WithOnlyACreationTransition_IsAtWorkNotReachedElsewhere()
    {
        var pipeline = ItemStagePipelineBuilder.Build(new StageSnapshot
        {
            ItemId = "x",
            State = "Queued",
            Transitions = [Hop(0, null, "Queued")],
            AuditIterations = [],
        });

        Assert.Equal(PipelineStage.Work, pipeline.Current);
        Assert.Equal(StageStatus.Current, Stage(pipeline, PipelineStage.Work).Status);
        Assert.Equal(string.Empty, Stage(pipeline, PipelineStage.Audit).Detail);
        Assert.All(pipeline.Stages.Where(s => s.Stage != PipelineStage.Work), s => Assert.Equal(StageStatus.NotReached, s.Status));
    }

    [Fact]
    public void AuditInProgress_ReportsTheRunningIteration()
    {
        var pipeline = ItemStagePipelineBuilder.Build(new StageSnapshot
        {
            ItemId = "x",
            State = "Auditing",
            Transitions = [Hop(0, null, "Queued"), Hop(1, "Queued", "Working", 1), Hop(5, "Working", "WorkComplete"), Hop(6, "WorkComplete", "Auditing")],
            AuditIterations = [new StageAuditIteration { Iteration = 1, MaxIterations = 6, Status = "in_progress" }],
        });

        Assert.Equal("1/6 running", Stage(pipeline, PipelineStage.Audit).Detail);
    }

    [Fact]
    public void UnorderedAndOversizedInput_IsSortedAndBounded()
    {
        var transitions = new List<StageTransition> { Hop(6, "WorkComplete", "Auditing"), Hop(5, "Working", "WorkComplete"), Hop(1, "Queued", "Working", 1), Hop(0, null, "Queued") };
        for (var i = 0; i < StageSnapshot.MaxTransitions + 50; i++)
        {
            transitions.Add(Hop(100 + i, "Auditing", "Auditing"));
        }
        var pipeline = ItemStagePipelineBuilder.Build(new StageSnapshot { ItemId = "x", State = "Auditing", Transitions = transitions });

        Assert.Equal(PipelineStage.Audit, pipeline.Current);
        Assert.Equal(StageStatus.Visited, Stage(pipeline, PipelineStage.Work).Status);
        Assert.Empty(pipeline.Loops);
    }
}
