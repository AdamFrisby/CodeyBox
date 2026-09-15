using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

/// <summary>
/// Activity derivation: blocked names its blocker; slot-waiting is distinct
/// from dependency-blocking; Queued no longer means "fine".
/// </summary>
public sealed class ActivityTests
{
    [Fact]
    public void BlockedItem_NamesItsBlocker()
    {
        var snapshot = Fixtures.Snapshot([
            Fixtures.Item("parent", state: "Working"),
            Fixtures.Item("child", dependsOn: ["parent"]),
        ]);

        var activities = ActivityAnalyzer.AnalyzeAll(snapshot);

        var child = activities["child"];
        Assert.Equal(ActivityKind.BlockedByDependency, child.Kind);
        var blocker = Assert.Single(child.Blockers);
        Assert.Equal("parent", blocker.Id);
        Assert.Equal("Working", blocker.State);
        Assert.Contains("parent", child.Summary);
    }

    [Fact]
    public void FailedParent_BlocksDependent_NamesItFailed()
    {
        var snapshot = Fixtures.Snapshot([
            Fixtures.Item("parent", state: "Failed"),
            Fixtures.Item("child", dependsOn: ["parent"]),
        ]);

        var activities = ActivityAnalyzer.AnalyzeAll(snapshot);

        Assert.Equal(ActivityKind.Failed, activities["parent"].Kind);
        var child = activities["child"];
        Assert.Equal(ActivityKind.BlockedByDependency, child.Kind);
        Assert.Equal("Failed", Assert.Single(child.Blockers).State);
    }

    [Fact]
    public void DoneParent_UnblocksDependent()
    {
        var snapshot = Fixtures.Snapshot([
            Fixtures.Item("parent", state: "Done"),
            Fixtures.Item("child", dependsOn: ["parent"]),
        ]);

        var child = ActivityAnalyzer.AnalyzeAll(snapshot)["child"];

        Assert.NotEqual(ActivityKind.BlockedByDependency, child.Kind);
    }

    [Fact]
    public void SlotWait_IsDistinctFromDependencyBlock()
    {
        var snapshot = Fixtures.Snapshot(
            [
                Fixtures.Item("waiting", dependsOn: []),
                Fixtures.Item("blocked", dependsOn: ["ghost"], dependsOnSatisfied: false),
            ],
            workers: new AdminWorkerCapacity { GlobalMaxConcurrent = 1, GlobalRunning = 1 });

        var activities = ActivityAnalyzer.AnalyzeAll(snapshot);

        Assert.Equal(ActivityKind.WaitingForSlot, activities["waiting"].Kind);
        Assert.Empty(activities["waiting"].Blockers);
        Assert.Equal(ActivityKind.BlockedByDependency, activities["blocked"].Kind);
        Assert.NotEmpty(activities["blocked"].Blockers);
    }

    [Fact]
    public void FreeSlot_WithNoDeps_IsReady()
    {
        var snapshot = Fixtures.Snapshot([Fixtures.Item("ready")]);

        Assert.Equal(ActivityKind.Ready, ActivityAnalyzer.AnalyzeAll(snapshot)["ready"].Kind);
    }

    [Fact]
    public void PausedAgent_BlocksQueuedItem()
    {
        var snapshot = Fixtures.Snapshot(
            [Fixtures.Item("queued", agent: "Claude")],
            agents: [new AdminAgentStatus { Agent = "Claude", Paused = true, PauseReason = "cost review" }]);

        var activity = ActivityAnalyzer.AnalyzeAll(snapshot)["queued"];

        Assert.Equal(ActivityKind.BlockedByAgentAvailability, activity.Kind);
        Assert.Contains("Claude", activity.Summary);
    }

    [Fact]
    public void ExhaustedQuota_BlocksQueuedItem()
    {
        var snapshot = Fixtures.Snapshot(
            [Fixtures.Item("queued", agent: "Codex")],
            agents:
            [
                new AdminAgentStatus
                {
                    Agent = "Codex",
                    QuotaAvailablePct = 0,
                    QuotaResetAt = Fixtures.Now.AddHours(2),
                },
            ]);

        var activity = ActivityAnalyzer.AnalyzeAll(snapshot)["queued"];

        Assert.Equal(ActivityKind.BlockedByAgentAvailability, activity.Kind);
        Assert.Contains("quota", activity.Summary);
    }

    [Fact]
    public void UnknownQuota_NeverBlocks()
    {
        var snapshot = Fixtures.Snapshot(
            [Fixtures.Item("queued", agent: "Codex")],
            agents: [new AdminAgentStatus { Agent = "Codex" }]);

        Assert.Equal(ActivityKind.Ready, ActivityAnalyzer.AnalyzeAll(snapshot)["queued"].Kind);
    }

    [Fact]
    public void ParkedStates_ReportParkReason()
    {
        var snapshot = Fixtures.Snapshot([
            Fixtures.Item("q", state: "WaitingForQuotaReset"),
            Fixtures.Item("o", state: "NeedsOperatorInput"),
        ]);

        var activities = ActivityAnalyzer.AnalyzeAll(snapshot);

        Assert.Equal(ActivityKind.Parked, activities["q"].Kind);
        Assert.Equal("WaitingForQuotaReset", activities["q"].ParkReason);
        Assert.Equal(ActivityKind.Parked, activities["o"].Kind);
        Assert.Equal("NeedsOperatorInput", activities["o"].ParkReason);
    }

    [Fact]
    public void InFlightStates_AreRunning()
    {
        var snapshot = Fixtures.Snapshot([
            Fixtures.Item("w", state: "Working"),
            Fixtures.Item("a", state: "Auditing"),
            Fixtures.Item("m", state: "Merging"),
        ]);

        var activities = ActivityAnalyzer.AnalyzeAll(snapshot);

        Assert.Equal(ActivityKind.Running, activities["w"].Kind);
        Assert.Equal(ActivityKind.Running, activities["a"].Kind);
        Assert.Equal(ActivityKind.Running, activities["m"].Kind);
    }

    [Fact]
    public void PerAgentCap_Full_CountsAsWaitingForSlot()
    {
        var snapshot = Fixtures.Snapshot(
            [Fixtures.Item("queued", agent: "Claude")],
            workers: new AdminWorkerCapacity
            {
                GlobalMaxConcurrent = 8,
                GlobalRunning = 1,
                PerAgentCaps = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Claude"] = 1,
                },
                PerAgentRunning = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Claude"] = 1,
                },
            });

        Assert.Equal(
            ActivityKind.WaitingForSlot,
            ActivityAnalyzer.AnalyzeAll(snapshot)["queued"].Kind);
    }
}
