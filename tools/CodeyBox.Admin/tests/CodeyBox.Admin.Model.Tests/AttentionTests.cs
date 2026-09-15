using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

/// <summary>Attention ranking: failures and parks first, healthy runners last.</summary>
public sealed class AttentionTests
{
    private static double ScoreOf(IReadOnlyList<AttentionScore> scores, string id) =>
        Assert.Single(scores, s => s.ItemId == id).Score;

    [Fact]
    public void FailuresAndParks_OutrankEverything()
    {
        var snapshot = Fixtures.Snapshot(
            [
                Fixtures.Item("failed", state: "Failed"),
                Fixtures.Item("parked", state: "NeedsOperatorInput"),
                Fixtures.Item("blocked", dependsOn: ["ghost"], dependsOnSatisfied: false),
                Fixtures.Item("running", state: "Working",
                    createdAt: Fixtures.Now.AddDays(-3), updatedAt: Fixtures.Now.AddMinutes(-1)),
            ],
            workers: new AdminWorkerCapacity { GlobalMaxConcurrent = 8, GlobalRunning = 1 });

        var activities = ActivityAnalyzer.AnalyzeAll(snapshot);
        var scores = AttentionRanker.RankItems(snapshot, activities);

        Assert.Equal("failed", scores[0].ItemId);
        Assert.Equal("parked", scores[1].ItemId);
        Assert.True(ScoreOf(scores, "failed") > ScoreOf(scores, "parked"));
        Assert.True(ScoreOf(scores, "parked") > ScoreOf(scores, "blocked"));
        Assert.True(ScoreOf(scores, "blocked") > ScoreOf(scores, "running"));
    }

    [Fact]
    public void LongRunningHealthyItem_StaysLow()
    {
        var snapshot = Fixtures.Snapshot([
            Fixtures.Item("ancient", state: "Working",
                createdAt: Fixtures.Now.AddDays(-30), updatedAt: Fixtures.Now.AddMinutes(-1)),
            Fixtures.Item("fresh-block", dependsOn: ["ghost"], dependsOnSatisfied: false),
        ]);

        var scores = AttentionRanker.RankItems(snapshot, ActivityAnalyzer.AnalyzeAll(snapshot));

        Assert.True(
            ScoreOf(scores, "ancient") <= new AdminModelOptions().RunningAttentionCap,
            $"30-day healthy run scored {ScoreOf(scores, "ancient")}.");
        Assert.True(ScoreOf(scores, "ancient") < ScoreOf(scores, "fresh-block"));
    }

    [Fact]
    public void ChainScore_IsItsMostUrgentMember()
    {
        var snapshot = Fixtures.Snapshot([
            Fixtures.Item("a", title: "Batch 1/2", state: "Done"),
            Fixtures.Item("b", title: "Batch 2/2", state: "Failed"),
            Fixtures.Item("lonely", state: "Working"),
        ]);

        var projection = FleetProjectionBuilder.Project(snapshot);

        var batch = Assert.Single(
            projection.ChainAttention, c => c.TopItemId == "b" || c.ItemIds.Contains("a"));
        Assert.Equal(["a", "b"], batch.ItemIds);
        Assert.Equal("b", batch.TopItemId);
        Assert.Equal(ScoreOf(projection.Attention, "b"), batch.Score);
        Assert.Equal(batch.Score, projection.ChainAttention[0].Score);
    }

    [Fact]
    public void ResolvedItems_NeedNothing()
    {
        var snapshot = Fixtures.Snapshot([
            Fixtures.Item("done", state: "Done"),
            Fixtures.Item("cancelled", state: "Cancelled"),
        ]);

        var scores = AttentionRanker.RankItems(snapshot, ActivityAnalyzer.AnalyzeAll(snapshot));

        Assert.All(scores, s => Assert.Equal(0, s.Score));
    }

    [Fact]
    public void DeadEndBlocker_OutranksPlainBlock()
    {
        var snapshot = Fixtures.Snapshot([
            Fixtures.Item("dead-parent", state: "Failed"),
            Fixtures.Item("dead-child", dependsOn: ["dead-parent"]),
            Fixtures.Item("live-parent", state: "Working"),
            Fixtures.Item("live-child", dependsOn: ["live-parent"]),
        ]);

        var scores = AttentionRanker.RankItems(snapshot, ActivityAnalyzer.AnalyzeAll(snapshot));

        Assert.True(ScoreOf(scores, "dead-child") > ScoreOf(scores, "live-child"));
    }

    [Fact]
    public void EveryScore_HasAReason()
    {
        var snapshot = Fixtures.Snapshot([
            Fixtures.Item("a", state: "Failed"),
            Fixtures.Item("b", state: "Queued"),
            Fixtures.Item("c", state: "Working"),
            Fixtures.Item("d", state: "Done"),
        ]);

        var scores = AttentionRanker.RankItems(snapshot, ActivityAnalyzer.AnalyzeAll(snapshot));

        Assert.All(scores, s =>
        {
            Assert.InRange(s.Score, 0, 100);
            Assert.NotEmpty(s.Reasons);
        });
    }
}
