using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

/// <summary>
/// The bound-worker signal in the projection layer: items a worker holds
/// count as running; a retried durable checkpoint waiting for a dispatch
/// slot counts as queued and classifies as waiting to resume — never running.
/// </summary>
public sealed class RunningSignalTests
{
    private static FleetVital ByName(IReadOnlyList<FleetVital> vitals, string name) =>
        vitals.Single(v => v.Name == name);

    [Fact]
    public void Vitals_WaitingResumeCountsAsQueuedNotInFlight()
    {
        var items = new List<AdminWorkItem>
        {
            Fixtures.Item("run-1", state: "Working", isRunning: true),
            Fixtures.Item("wait-1", state: "Working", isRunning: false, hasPendingResume: true),
            Fixtures.Item("wait-2", state: "Reworking", isRunning: false, hasPendingResume: true),
        };
        var snapshot = Fixtures.Snapshot(items, history: new VitalHistories());

        var vitals = VitalsEvaluator.Evaluate(snapshot);

        Assert.Equal(1, ByName(vitals, "In flight").Value);
        Assert.Equal(2, ByName(vitals, "Queue depth").Value);
    }

    [Fact]
    public void Vitals_UnheldActiveStateWithoutResumeFlagCountsAsQueued()
    {
        var items = new List<AdminWorkItem>
        {
            Fixtures.Item("wait-1", state: "Working", isRunning: false),
        };
        var snapshot = Fixtures.Snapshot(items, history: new VitalHistories());

        var vitals = VitalsEvaluator.Evaluate(snapshot);

        Assert.Equal(0, ByName(vitals, "In flight").Value);
        Assert.Equal(1, ByName(vitals, "Queue depth").Value);
    }

    [Fact]
    public void Vitals_RunningResumeStillCountsAsInFlight()
    {
        var items = new List<AdminWorkItem>
        {
            Fixtures.Item("run-1", state: "Working", isRunning: true, hasPendingResume: false),
        };
        var snapshot = Fixtures.Snapshot(items, history: new VitalHistories());

        var vitals = VitalsEvaluator.Evaluate(snapshot);

        Assert.Equal(1, ByName(vitals, "In flight").Value);
        Assert.Equal(0, ByName(vitals, "Queue depth").Value);
    }

    [Fact]
    public void Activity_WaitingResumeIsWaitingForSlotNotRunning()
    {
        var item = Fixtures.Item("wait-1", state: "Working", isRunning: false, hasPendingResume: true);
        var snapshot = Fixtures.Snapshot([item]);

        var activity = ActivityAnalyzer.Analyze(item, snapshot);

        Assert.Equal(ActivityKind.WaitingForSlot, activity.Kind);
        Assert.Contains("resume", activity.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Activity_RunningItemStaysRunning()
    {
        var item = Fixtures.Item("run-1", state: "Working", isRunning: true);
        var snapshot = Fixtures.Snapshot([item]);

        var activity = ActivityAnalyzer.Analyze(item, snapshot);

        Assert.Equal(ActivityKind.Running, activity.Kind);
        Assert.Contains("Running (Working)", activity.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Activity_UnheldBetweenTurnsStateWaitsForWorker()
    {
        var item = Fixtures.Item("wait-1", state: "WorkComplete", isRunning: false);
        var snapshot = Fixtures.Snapshot([item]);

        var activity = ActivityAnalyzer.Analyze(item, snapshot);

        Assert.Equal(ActivityKind.WaitingForSlot, activity.Kind);
        Assert.Contains("no worker holds it", activity.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Mapper_DefaultsPreserveStateBasedReading()
    {
        // Snapshots built without the signal keep the historical reading:
        // worker-occupiable states default to running.
        var item = Fixtures.Item("w-1", state: "Working");

        Assert.True(item.IsRunning);
        Assert.False(item.HasPendingResume);
        var snapshot = Fixtures.Snapshot([item]);
        Assert.Equal(ActivityKind.Running, ActivityAnalyzer.Analyze(item, snapshot).Kind);
    }
}
