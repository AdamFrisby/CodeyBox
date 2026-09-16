using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

/// <summary>
/// Journey projection over recorded histories: convergence vs. stuck must be
/// readable from the derived values, never by eyeballing rows.
/// Every test builds a fixed <see cref="JourneySnapshot"/> — no clock, no I/O.
/// </summary>
public sealed class JourneyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private static JourneyItem Item(string state = "Reworking", int? auditMax = 5) => new()
    {
        Id = "item-1",
        Title = "Fix login",
        State = state,
        Agent = "Claude",
        AuditMaxIterations = auditMax,
    };

    private static JourneyAuditIteration Iter(
        int iteration, int blocking, int max = 5, string status = "complete",
        string[]? ids = null, int minutesAfter = 0, string attempt = "") => new()
        {
            WorkAttemptKey = attempt,
            Iteration = iteration,
            MaxIterations = max,
            Status = status,
            BlockingFindings = blocking,
            NonBlockingFindings = 0,
            BlockingFindingIds = ids ?? [],
            RecordedAt = T0.AddMinutes(minutesAfter),
        };

    private static JourneySnapshot Snapshot(
        JourneyItem item,
        IReadOnlyList<JourneyAuditIteration>? iterations = null,
        IReadOnlyList<JourneyPhaseRun>? runs = null,
        IReadOnlyList<JourneyPhaseTiming>? timings = null,
        IReadOnlyList<JourneyInfraEvent>? infra = null,
        IReadOnlyList<JourneyStreamFile>? streams = null,
        bool diffAvailable = true,
        int? projectDefaultMax = null) => new()
        {
            Item = item,
            AuditIterations = iterations ?? [],
            PhaseRuns = runs ?? [],
            Timings = timings ?? [],
            InfraEvents = infra ?? [],
            StreamFiles = streams ?? [],
            DiffAvailable = diffAvailable,
            ProjectDefaultMaxIterations = projectDefaultMax,
        };

    private static JourneyPhaseRun Run(
        string phase, string agent = "Claude", string? outcome = "success",
        int startedMinutesAfter = 0, int durationMinutes = 5, int? iteration = null) => new()
        {
            Phase = phase,
            AgentKind = agent,
            StartedAt = T0.AddMinutes(startedMinutesAfter),
            EndedAt = T0.AddMinutes(startedMinutesAfter + durationMinutes),
            Iteration = iteration,
            Outcome = outcome,
        };

    // ── Convergence ──────────────────────────────────────────────────

    [Fact]
    public void Converging_ShrinkingFindingsWithNoNewOnes_ReadsConverging()
    {
        var snapshot = Snapshot(Item(), iterations:
        [
            Iter(1, 4, ids: ["f-a", "f-b", "f-c", "f-d"], minutesAfter: 10),
            Iter(2, 2, ids: ["f-a", "f-b"], minutesAfter: 20),
        ]);

        var journey = WorkItemJourneyBuilder.Build(snapshot);

        Assert.Equal(JourneyConvergence.Converging, journey.AuditCycle.Convergence);
        Assert.Equal(2, journey.AuditCycle.AttemptsUsed);
        Assert.Equal(5, journey.AuditCycle.ConfiguredMax);
        Assert.Equal(3, journey.AuditCycle.Remaining);
        var second = journey.AuditCycle.Iterations[1];
        Assert.Equal(["f-a", "f-b"], second.RecurringIds);
        Assert.Empty(second.NewIds);
        Assert.Equal(["f-c", "f-d"], second.ResolvedIds);
    }

    [Fact]
    public void Converged_ZeroBlockingOnLastIteration_ReadsConverged()
    {
        var snapshot = Snapshot(Item(state: "AuditPassed"), iterations:
        [
            Iter(1, 2, ids: ["f-a", "f-b"], minutesAfter: 10),
            Iter(2, 0, ids: [], minutesAfter: 20),
        ]);

        var journey = WorkItemJourneyBuilder.Build(snapshot);

        Assert.Equal(JourneyConvergence.Converged, journey.AuditCycle.Convergence);
    }

    // ── Stuck ────────────────────────────────────────────────────────

    [Fact]
    public void Stuck_IdenticalFindingsRepeating_ReadsStuckWithTheStuckSet()
    {
        var snapshot = Snapshot(Item(), iterations:
        [
            Iter(1, 3, ids: ["f-a", "f-b", "f-c"], minutesAfter: 10),
            Iter(2, 2, ids: ["f-a", "f-b"], minutesAfter: 20),
            Iter(3, 2, ids: ["f-a", "f-b"], minutesAfter: 30),
        ]);

        var journey = WorkItemJourneyBuilder.Build(snapshot);

        Assert.Equal(JourneyConvergence.Stuck, journey.AuditCycle.Convergence);
        Assert.Equal(["f-a", "f-b"], journey.AuditCycle.StuckFindingIds);
        Assert.Equal(2, journey.AuditCycle.StuckRunLength);
        Assert.Contains("2", journey.AuditCycle.Summary);
    }

    [Fact]
    public void Stuck_ThreeIdenticalInARow_ExtendsTheRunLength()
    {
        var snapshot = Snapshot(Item(), iterations:
        [
            Iter(1, 1, ids: ["f-a"], minutesAfter: 10),
            Iter(2, 1, ids: ["f-a"], minutesAfter: 20),
            Iter(3, 1, ids: ["f-a"], minutesAfter: 30),
        ]);

        var journey = WorkItemJourneyBuilder.Build(snapshot);

        Assert.Equal(JourneyConvergence.Stuck, journey.AuditCycle.Convergence);
        Assert.Equal(3, journey.AuditCycle.StuckRunLength);
        Assert.Equal(["f-a"], journey.AuditCycle.StuckFindingIds);
    }

    [Fact]
    public void Cycling_NewFindingsArriving_IsNotStuck()
    {
        var snapshot = Snapshot(Item(), iterations:
        [
            Iter(1, 2, ids: ["f-a", "f-b"], minutesAfter: 10),
            Iter(2, 2, ids: ["f-b", "f-c"], minutesAfter: 20),
        ]);

        var journey = WorkItemJourneyBuilder.Build(snapshot);

        Assert.Equal(JourneyConvergence.Cycling, journey.AuditCycle.Convergence);
        Assert.Empty(journey.AuditCycle.StuckFindingIds);
        var second = journey.AuditCycle.Iterations[1];
        Assert.Equal(["f-b"], second.RecurringIds);
        Assert.Equal(["f-c"], second.NewIds);
        Assert.Equal(["f-a"], second.ResolvedIds);
    }

    // ── Budget ───────────────────────────────────────────────────────

    [Fact]
    public void AtBudget_ExhaustedIterationsWithoutConvergence_ReadsAtBudget()
    {
        var snapshot = Snapshot(Item(auditMax: 3), iterations:
        [
            Iter(1, 3, max: 3, ids: ["f-a", "f-b", "f-c"], minutesAfter: 10),
            Iter(2, 2, max: 3, ids: ["f-b", "f-c"], minutesAfter: 20),
            Iter(3, 1, max: 3, ids: ["f-c"], minutesAfter: 30),
        ]);

        var journey = WorkItemJourneyBuilder.Build(snapshot);

        Assert.Equal(JourneyConvergence.AtBudget, journey.AuditCycle.Convergence);
        Assert.Equal(3, journey.AuditCycle.AttemptsUsed);
        Assert.Equal(3, journey.AuditCycle.ConfiguredMax);
        Assert.Equal(0, journey.AuditCycle.Remaining);
    }

    [Fact]
    public void Remaining_MatchesConfiguredMaximum()
    {
        var snapshot = Snapshot(Item(auditMax: 10), iterations:
        [
            Iter(1, 2, max: 10, ids: ["f-a", "f-b"], minutesAfter: 10),
            Iter(2, 1, max: 10, ids: ["f-b"], minutesAfter: 20),
        ]);

        var journey = WorkItemJourneyBuilder.Build(snapshot);

        Assert.Equal(10, journey.AuditCycle.ConfiguredMax);
        Assert.Equal(2, journey.AuditCycle.AttemptsUsed);
        Assert.Equal(8, journey.AuditCycle.Remaining);
    }

    [Fact]
    public void Remaining_FallsBackToProjectDefaultThenRowMax()
    {
        var noOverride = Snapshot(
            Item(auditMax: null),
            iterations: [Iter(1, 1, max: 7, ids: ["f-a"], minutesAfter: 10)],
            projectDefaultMax: 7);
        Assert.Equal(6, WorkItemJourneyBuilder.Build(noOverride).AuditCycle.Remaining);

        var rowFallback = Snapshot(
            Item(auditMax: null),
            iterations: [Iter(1, 1, max: 4, ids: ["f-a"], minutesAfter: 10)]);
        var built = WorkItemJourneyBuilder.Build(rowFallback);
        Assert.Equal(4, built.AuditCycle.ConfiguredMax);
        Assert.Equal(3, built.AuditCycle.Remaining);
    }

    // ── Infra is not rework ──────────────────────────────────────────

    [Fact]
    public void InfraFailures_AreNotCountedAsReworkIterations()
    {
        var snapshot = Snapshot(Item(), iterations:
        [
            Iter(1, 2, ids: ["f-a", "f-b"], minutesAfter: 10),
            Iter(2, 0, status: "incomplete", minutesAfter: 20),
            Iter(3, 1, ids: ["f-b"], minutesAfter: 30),
        ]);

        var journey = WorkItemJourneyBuilder.Build(snapshot);

        Assert.Equal(2, journey.AuditCycle.AttemptsUsed);
        Assert.Single(journey.InfraInterruptions, e => e.Kind == "incomplete-audit");
        Assert.Equal(JourneyConvergence.Converging, journey.AuditCycle.Convergence);
    }

    [Fact]
    public void InfraPhaseRuns_AreListedDistinctlyAndExcludedFromNodes()
    {
        var snapshot = Snapshot(
            Item(),
            iterations: [Iter(1, 1, ids: ["f-a"], minutesAfter: 30)],
            runs:
            [
                Run("work", outcome: "failure:infrastructure", startedMinutesAfter: 0),
                Run("work", outcome: "success", startedMinutesAfter: 20),
            ]);

        var journey = WorkItemJourneyBuilder.Build(snapshot);

        Assert.Single(journey.InfraInterruptions, e => e.Kind == "infrastructure");
        var work = Assert.Single(journey.Nodes, n => n.Phase == "work");
        Assert.Equal(2, work.Visits);
        Assert.Equal("success", work.Outcome);
    }

    // ── Conflict rework loop ─────────────────────────────────────────

    [Fact]
    public void ConflictRework_LoopedThroughMergeConflict_ShowsLoopVisits()
    {
        var snapshot = Snapshot(
            Item(state: "ReworkingForConflict"),
            iterations:
            [
                Iter(1, 1, ids: ["f-a"], minutesAfter: 10),
                Iter(2, 0, ids: [], minutesAfter: 20),
            ],
            runs:
            [
                Run("work", startedMinutesAfter: 0),
                Run("merge", startedMinutesAfter: 40),
                Run("conflict_rework", startedMinutesAfter: 60),
                Run("conflict_rework", startedMinutesAfter: 90),
            ],
            timings:
            [
                new JourneyPhaseTiming { Phase = "work", DurationMs = 60_000 },
                new JourneyPhaseTiming { Phase = "merge", DurationMs = 30_000 },
            ]);

        var journey = WorkItemJourneyBuilder.Build(snapshot);

        Assert.Equal(JourneyConvergence.Converged, journey.AuditCycle.Convergence);
        Assert.Equal(2, journey.ConflictLoop.Visits);
        var conflictNode = Assert.Single(journey.Nodes, n => n.Phase == "conflictRework");
        Assert.True(conflictNode.IsLoop);
        Assert.True(conflictNode.IsCurrent);
        Assert.Equal("conflictRework", journey.CurrentPhase);
        Assert.Contains("conflict", journey.WaitingFor, StringComparison.OrdinalIgnoreCase);
    }

    // ── Graph nodes: agents, durations, sandbox links, position ──────

    [Fact]
    public void Nodes_ShowAgentAndDurationPerPhase()
    {
        var snapshot = Snapshot(
            Item(state: "Merging"),
            runs:
            [
                Run("work", agent: "Claude", startedMinutesAfter: 0),
                Run("rework", agent: "Codex", startedMinutesAfter: 40),
            ],
            timings:
            [
                new JourneyPhaseTiming { Phase = "work", DurationMs = 120_000 },
                new JourneyPhaseTiming { Phase = "rework", DurationMs = 45_000 },
            ]);

        var journey = WorkItemJourneyBuilder.Build(snapshot);

        var work = Assert.Single(journey.Nodes, n => n.Phase == "work");
        Assert.Equal(["Claude"], work.Agents);
        Assert.Equal(120_000, work.TotalDurationMs);
        var rework = Assert.Single(journey.Nodes, n => n.Phase == "rework");
        Assert.Equal(["Codex"], rework.Agents);
        Assert.Equal("merge", journey.CurrentPhase);
        var merge = Assert.Single(journey.Nodes, n => n.Phase == "merge");
        Assert.True(merge.IsCurrent);
    }

    [Fact]
    public void SandboxEvidence_PresentOnlyWhereARetainedFileExists()
    {
        var snapshot = Snapshot(
            Item(state: "Auditing"),
            runs: [Run("work", startedMinutesAfter: 0)],
            streams:
            [
                new JourneyStreamFile { FileName = "work-01.ndjson", Phase = "work" },
            ]);

        var journey = WorkItemJourneyBuilder.Build(snapshot);

        var work = Assert.Single(journey.Nodes, n => n.Phase == "work");
        Assert.True(work.HasSandboxLink);
        Assert.Equal(["work-01.ndjson"], work.SandboxFiles);
        var audit = Assert.Single(journey.Nodes, n => n.Phase == "audit");
        Assert.False(audit.HasSandboxLink);
        Assert.Empty(audit.SandboxFiles);
    }

    [Fact]
    public void WaitingFor_ParkedStatesNameTheWait()
    {
        var parked = Item(state: "WaitingForQuotaReset") with
        {
            NextQuotaRetryAt = T0.AddMinutes(12),
        };
        var journey = WorkItemJourneyBuilder.Build(Snapshot(parked));

        Assert.Equal("work", journey.CurrentPhase);
        Assert.Contains("quota", journey.WaitingFor, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownPhaseKeys_NeverInventNodes()
    {
        var snapshot = Snapshot(
            Item(state: "Working"),
            runs: [Run("some-future-phase", startedMinutesAfter: 0)]);

        var journey = WorkItemJourneyBuilder.Build(snapshot);

        Assert.DoesNotContain(journey.Nodes, n => n.Phase == "some-future-phase");
    }
}
