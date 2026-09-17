using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

/// <summary>
/// The "what needs me?" queue: every covered state appears with its
/// findings, zero-findings parks are labelled, infra reads differently from
/// rejection, the repeat/shrink signal is computed, and cancel warns.
/// </summary>
public sealed class NeedsAttentionTests
{
    private static NeedsAttentionItem Item(
        string id,
        string state,
        string? failureKind = null,
        IReadOnlyList<AttentionFinding>? findings = null,
        IReadOnlyList<IReadOnlyList<string>>? history = null,
        IReadOnlyList<string>? dependants = null,
        int attempts = 1) => new()
        {
            Id = id,
            Title = $"Work {id}",
            State = state,
            FailureKind = failureKind,
            BlockingFindings = findings ?? [],
            BlockingHistory = history ?? [],
            DependantIds = dependants ?? [],
            AttemptsMade = attempts,
        };

    private static Dictionary<string, double> Scores(params (string Id, double Score)[] entries) =>
        entries.ToDictionary(e => e.Id, e => e.Score, StringComparer.Ordinal);

    [Theory]
    [InlineData("Failed")]
    [InlineData("AuditFailed")]
    [InlineData("MergeConflictResolutionFailed")]
    [InlineData("AbandonedAfterRecoveryAttempts")]
    [InlineData("NeedsOperatorInput")]
    [InlineData("WaitingForQuotaReset")]
    [InlineData("WaitingForAgentResume")]
    [InlineData("WaitingForTransientRetry")]
    public void EveryCoveredState_AppearsInQueue(string state)
    {
        var queue = NeedsAttentionQueue.Build(
            [Item("x", state)],
            Scores(("x", 50)));

        var entry = Assert.Single(queue);
        Assert.Equal("x", entry.Item.Id);
        Assert.NotEmpty(entry.Headline);
        Assert.NotEmpty(entry.TrendSummary);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("AuditFailed")]
    [InlineData("MergeConflictResolutionFailed")]
    public void CoveredTerminalStates_ShowBlockingFindingsWithAuditors(string state)
    {
        var findings = new[]
        {
            new AttentionFinding { Id = "f1", Auditor = "security", Severity = "error", Title = "SQLi" },
            new AttentionFinding { Id = "f2", Auditor = "tests", Severity = "error", Title = "Red suite" },
        };
        var queue = NeedsAttentionQueue.Build(
            [Item("x", state, findings: findings)],
            Scores(("x", 50)));

        var entry = Assert.Single(queue);
        Assert.Equal(2, entry.Item.BlockingFindings.Count);
        Assert.Contains(entry.Item.BlockingFindings, f => f.Auditor == "security");
        Assert.Contains(entry.Item.BlockingFindings, f => f.Auditor == "tests");
    }

    [Fact]
    public void ParkedWithZeroFindings_IsLabelledExplicitly()
    {
        var queue = NeedsAttentionQueue.Build(
            [Item("x", "NeedsOperatorInput", history: [[]])],
            Scores(("x", 50)));

        var entry = Assert.Single(queue);
        Assert.Equal(FindingTrend.NoBlockingFindings, entry.Trend);
        Assert.Contains("no blocking findings", entry.TrendSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(entry.Item.BlockingFindings);
    }

    [Fact]
    public void NonNeedingStates_AreExcluded()
    {
        var queue = NeedsAttentionQueue.Build(
            [
                Item("done", "Done"),
                Item("cancelled", "Cancelled"),
                Item("queued", "Queued"),
                Item("working", "Working"),
                Item("noaction", "NoActionRequired"),
                Item("failed", "Failed"),
            ],
            Scores(("done", 99), ("cancelled", 99), ("queued", 99), ("working", 99), ("noaction", 99), ("failed", 1)));

        Assert.Equal(["failed"], queue.Select(e => e.Item.Id).ToList());
    }

    [Fact]
    public void Queue_IsOrderedByAttentionScoreHighestFirst()
    {
        var queue = NeedsAttentionQueue.Build(
            [
                Item("low", "Failed"),
                Item("high", "NeedsOperatorInput"),
                Item("mid", "AuditFailed"),
            ],
            Scores(("low", 10), ("high", 90), ("mid", 50)));

        Assert.Equal(["high", "mid", "low"], queue.Select(e => e.Item.Id).ToList());
    }

    [Theory]
    [InlineData("Failed", "infrastructure", true)]
    [InlineData("Failed", "transient", true)]
    [InlineData("Failed", "timeout", true)]
    [InlineData("Failed", "agent_unavailable", true)]
    [InlineData("AbandonedAfterRecoveryAttempts", null, true)]
    [InlineData("WaitingForQuotaReset", null, true)]
    [InlineData("AuditFailed", null, false)]
    [InlineData("Failed", "build", false)]
    [InlineData("Failed", "agent", false)]
    [InlineData("Failed", "configuration", false)]
    [InlineData("MergeConflictResolutionFailed", null, false)]
    public void InfrastructureFailure_ReadsDifferentlyFromRejectedChange(
        string state, string? failureKind, bool expectedInfrastructure)
    {
        var (headline, isInfrastructure) = FailureDescriber.Describe(state, failureKind);

        Assert.Equal(expectedInfrastructure, isInfrastructure);
        if (expectedInfrastructure)
        {
            Assert.Contains("nfrastructure", headline, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("Infrastructure failure", headline, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RepeatingFindings_SignalRetryWillNotHelp()
    {
        var history = new List<IReadOnlyList<string>>
        {
            new[] { "f1", "f2" },
            new[] { "f2", "f1" },
            new[] { "f1", "f2" },
        };
        Assert.Equal(FindingTrend.Repeating, FindingTrendAnalyzer.Compute(history));
        var summary = FindingTrendAnalyzer.Summarize(FindingTrend.Repeating, history);
        Assert.Contains("will not help", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShrinkingFindings_SignalRetryMayConverge()
    {
        var history = new List<IReadOnlyList<string>>
        {
            new[] { "f1", "f2", "f3" },
            new[] { "f1", "f2" },
            new[] { "f1" },
        };
        Assert.Equal(FindingTrend.Shrinking, FindingTrendAnalyzer.Compute(history));
        var summary = FindingTrendAnalyzer.Summarize(FindingTrend.Shrinking, history);
        Assert.Contains("shrinking", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Trend_IsComputedAcrossAttempts_AndSurfacedOnEntry()
    {
        var queue = NeedsAttentionQueue.Build(
            [Item("x", "AuditFailed", history: new List<IReadOnlyList<string>>
            {
                new[] { "f1" },
                new[] { "f1" },
            })],
            Scores(("x", 50)));

        var entry = Assert.Single(queue);
        Assert.Equal(FindingTrend.Repeating, entry.Trend);
        Assert.Contains("will not help", entry.TrendSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SinglePassHistory_IsNotRepeating()
    {
        Assert.Equal(
            FindingTrend.Cycling,
            FindingTrendAnalyzer.Compute(new List<IReadOnlyList<string>> { new[] { "f1" } }));
    }

    [Fact]
    public void EmptyHistory_HasNoEvidence()
    {
        Assert.Equal(FindingTrend.NoEvidence, FindingTrendAnalyzer.Compute([]));
        Assert.Equal(FindingTrend.NoEvidence, FindingTrendAnalyzer.Compute(null));
    }

    [Fact]
    public void CancellingWithDependants_WarnsNamingThem()
    {
        var warning = CancelWarningBuilder.Describe(["dep-b", "dep-a"]);

        Assert.NotNull(warning);
        Assert.Contains("2 dependants", warning, StringComparison.Ordinal);
        Assert.Contains("dep-a", warning, StringComparison.Ordinal);
        Assert.Contains("dep-b", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void CancellingWithoutDependants_HasNoWarning()
    {
        Assert.Null(CancelWarningBuilder.Describe([]));
        Assert.Null(CancelWarningBuilder.Describe(null));

        var queue = NeedsAttentionQueue.Build(
            [Item("lonely", "Failed")],
            Scores(("lonely", 50)));
        Assert.Null(Assert.Single(queue).CancelWarning);
    }

    [Fact]
    public void CancellingWithDependants_WarningIsOnTheEntry()
    {
        var queue = NeedsAttentionQueue.Build(
            [Item("parent", "Failed", dependants: ["child-1", "child-2"])],
            Scores(("parent", 50)));

        var warning = Assert.Single(queue).CancelWarning;
        Assert.NotNull(warning);
        Assert.Contains("child-1", warning, StringComparison.Ordinal);
        Assert.Contains("child-2", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ConflictItem_CarriesTheResolutionPath()
    {
        var queue = NeedsAttentionQueue.Build(
            [Item("c", "MergeConflictResolutionFailed")],
            Scores(("c", 50)));

        var entry = Assert.Single(queue);
        Assert.Equal("merge", entry.RecommendedRetryFrom);
        Assert.Contains("conflict", entry.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonConflictItem_HasNoExplicitRetryPhase()
    {
        var queue = NeedsAttentionQueue.Build(
            [Item("f", "Failed", failureKind: "infrastructure")],
            Scores(("f", 50)));

        Assert.Null(Assert.Single(queue).RecommendedRetryFrom);
    }

    [Fact]
    public void ParkText_IsNotEvidence_EmptyHistorySaysSo()
    {
        // An item can park with zero blocking findings: the queue must say so
        // explicitly rather than rendering an empty list.
        var queue = NeedsAttentionQueue.Build(
            [Item("x", "WaitingForQuotaReset")],
            Scores(("x", 50)));

        var entry = Assert.Single(queue);
        Assert.Equal(FindingTrend.NoEvidence, entry.Trend);
        Assert.Contains("No complete audit iteration", entry.TrendSummary, StringComparison.Ordinal);
    }
}
