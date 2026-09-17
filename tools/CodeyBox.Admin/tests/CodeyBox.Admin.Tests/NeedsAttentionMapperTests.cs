using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Evidence mapping: only blocking (Error) findings surface with their
/// auditor, the latest code iteration wins, and the trend reads the latest
/// work-attempt partition's complete rows — infra interruptions never count
/// as rework iterations.
/// </summary>
public sealed class NeedsAttentionMapperTests
{
    private static AuditReportIterationDto Iteration(
        string target, int iteration, params (string Auditor, string Id, string Severity)[] findings) => new()
        {
            Target = target,
            Iteration = iteration,
            Auditors = findings
                .GroupBy(f => f.Auditor)
                .Select(g => new AuditReportAuditorDto
                {
                    Name = g.Key,
                    Findings = g.Select(f => new AuditReportFindingDto
                    {
                        Id = f.Id,
                        Severity = f.Severity,
                        Title = $"title-{f.Id}",
                    }).ToList(),
                }).ToList(),
        };

    private static AuditProgressRowDto Row(int iteration, string status, string[] ids, string key = "") => new()
    {
        Id = $"row-{iteration}-{status}",
        WorkAttemptKey = key,
        Iteration = iteration,
        MaxIterations = 5,
        Status = status,
        BlockingFindings = ids.Length,
        BlockingFindingIds = [.. ids],
        RecordedAt = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero).AddMinutes(iteration),
    };

    [Fact]
    public void OnlyErrorFindingsFromLatestCodeIteration_SurfaceWithAuditor()
    {
        var reports = new AuditReportsDto
        {
            WorkItemId = "x",
            Iterations =
            [
                Iteration("code", 1, ("security", "old", "Error")),
                Iteration("code", 2,
                    ("security", "f1", "Error"),
                    ("tests", "f2", "Warning"),
                    ("style", "f3", "Info")),
            ],
        };

        var findings = NeedsAttentionEvidenceMapper.LatestBlockingFindings(reports);

        var single = Assert.Single(findings);
        Assert.Equal("f1", single.Id);
        Assert.Equal("security", single.Auditor);
    }

    [Fact]
    public void EmptyReports_YieldNoFindings()
    {
        Assert.Empty(NeedsAttentionEvidenceMapper.LatestBlockingFindings(null));
        Assert.Empty(NeedsAttentionEvidenceMapper.LatestBlockingFindings(new AuditReportsDto()));
    }

    [Fact]
    public void BlockingHistory_ReadsLatestPartitionCompleteRowsOnly()
    {
        var progress = new AuditProgressListDto
        {
            WorkItemId = "x",
            Progress =
            [
                Row(1, "complete", ["stale"], key: "attempt-1"),
                Row(1, "complete", ["a", "b"], key: "attempt-2"),
                Row(2, "incomplete", ["infra-noise"], key: "attempt-2"),
                Row(2, "complete", ["a"], key: "attempt-2"),
            ],
        };

        var history = NeedsAttentionEvidenceMapper.BlockingHistory(progress);

        Assert.Equal(2, history.Count);
        Assert.Equal(["a", "b"], history[0].OrderBy(id => id).ToList());
        Assert.Equal(["a"], history[1].ToList());
    }

    [Fact]
    public void EmptyProgress_YieldsNoHistory()
    {
        Assert.Empty(NeedsAttentionEvidenceMapper.BlockingHistory(null));
        Assert.Empty(NeedsAttentionEvidenceMapper.BlockingHistory(new AuditProgressListDto()));
    }

    [Fact]
    public void ScoreAll_CoversTerminalItems()
    {
        WorkItemDto Item(string id, string state) => new()
        {
            Id = id,
            Title = id,
            State = state,
            CreatedAt = new DateTimeOffset(2026, 9, 15, 11, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero),
        };
        var scores = NeedsAttentionEvidenceMapper.ScoreAll(
            [Item("failed", "Failed"), Item("parked", "NeedsOperatorInput")],
            new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero));

        Assert.True(scores.ContainsKey("failed"));
        Assert.True(scores.ContainsKey("parked"));
        Assert.True(scores["failed"] > scores["parked"]);
    }
}
