using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class PlannedItemAuditRebalanceTests
{
    private static AuditFinding Error(string auditor, string title = "t") =>
        new(auditor, AuditSeverity.Error, title, "d");

    private static AuditFinding Warning(string auditor) =>
        new(auditor, AuditSeverity.Warning, "w", "d");

    private static readonly string[] ArchitectureAdvisory = ["architecture:llm-review"];

    [Fact]
    public void Unplanned_ItemNeverDemotes_ArchitectureErrorStillBlocks()
    {
        var findings = new[] { Error("architecture:llm-review"), Error("security:llm-review") };

        var blocking = PlannedItemAuditRebalance.SelectBlocking(
            findings, AuditSeverity.Error, itemWasPlanned: false,
            rebalanceEnabled: true, ArchitectureAdvisory);

        Assert.Equal(2, blocking.Count);
    }

    [Fact]
    public void Planned_DemotesConfiguredAuditor_KeepsOthersBlocking()
    {
        var findings = new[]
        {
            Error("architecture:llm-review"),
            Error("security:llm-review"),
            Error("completeness:llm-review"),
        };

        var blocking = PlannedItemAuditRebalance.SelectBlocking(
            findings, AuditSeverity.Error, itemWasPlanned: true,
            rebalanceEnabled: true, ArchitectureAdvisory);

        Assert.DoesNotContain(blocking, f => f.AuditorName == "architecture:llm-review");
        Assert.Contains(blocking, f => f.AuditorName == "security:llm-review");
        Assert.Contains(blocking, f => f.AuditorName == "completeness:llm-review");
        Assert.Equal(2, blocking.Count);
    }

    [Fact]
    public void Planned_MatchesAuditorNameCaseInsensitively()
    {
        var findings = new[] { Error("Architecture:LLM-Review") };

        var blocking = PlannedItemAuditRebalance.SelectBlocking(
            findings, AuditSeverity.Error, itemWasPlanned: true,
            rebalanceEnabled: true, ArchitectureAdvisory);

        Assert.Empty(blocking);
    }

    [Fact]
    public void Planned_RebalanceDisabled_NoDemotion()
    {
        var findings = new[] { Error("architecture:llm-review") };

        var blocking = PlannedItemAuditRebalance.SelectBlocking(
            findings, AuditSeverity.Error, itemWasPlanned: true,
            rebalanceEnabled: false, ArchitectureAdvisory);

        Assert.Single(blocking);
    }

    [Fact]
    public void Planned_EmptyAdvisoryList_NoDemotion()
    {
        var findings = new[] { Error("architecture:llm-review") };

        var blocking = PlannedItemAuditRebalance.SelectBlocking(
            findings, AuditSeverity.Error, itemWasPlanned: true,
            rebalanceEnabled: true, advisoryAuditorNames: []);

        Assert.Single(blocking);
    }

    [Fact]
    public void SubThresholdFindings_NeverBlock_RegardlessOfPlanned()
    {
        // FailingSeverity = Error, so Warnings never block even from a
        // non-demoted auditor.
        var findings = new[] { Warning("security:llm-review"), Error("security:llm-review") };

        var blocking = PlannedItemAuditRebalance.SelectBlocking(
            findings, AuditSeverity.Error, itemWasPlanned: true,
            rebalanceEnabled: true, ArchitectureAdvisory);

        Assert.Single(blocking);
        Assert.Equal(AuditSeverity.Error, blocking[0].Severity);
    }

    [Fact]
    public void DemotedFindingsRemainInCallerList_OnlyBlockingSubsetShrinks()
    {
        // The function returns only the blocking subset; the caller keeps the full
        // findings list for reporting, so advisory (demoted) findings are still
        // surfaced as non-blocking.
        var findings = new[] { Error("architecture:llm-review"), Error("tests:mutation-rigor") };

        var blocking = PlannedItemAuditRebalance.SelectBlocking(
            findings, AuditSeverity.Error, itemWasPlanned: true,
            rebalanceEnabled: true, ArchitectureAdvisory);

        Assert.Single(blocking);
        Assert.Equal("tests:mutation-rigor", blocking[0].AuditorName);
        // Original list is untouched.
        Assert.Equal(2, findings.Length);
    }
}
