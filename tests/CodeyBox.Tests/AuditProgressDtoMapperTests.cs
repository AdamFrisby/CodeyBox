using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for the pure audit-progress DTO mapper: description truncation in the list view,
/// full detail when uncapped, and unicode-safe truncation.
/// </summary>
public sealed class AuditProgressDtoMapperTests
{
    private static StoredAuditProgress Row(string description) => new(
        Id: "row-1",
        WorkItemId: WorkItemId.New(),
        WorkAttemptKey: "",
        RecordedAt: DateTimeOffset.UtcNow,
        Progress: new AuditProgressRecord(
            Iteration: 1,
            MaxIterations: 3,
            BlockingFindings: 1,
            NonBlockingFindings: 0,
            BlockingFindingIds: ["b1"],
            BlockingFindingsDetails:
            [
                new AuditProgressFinding("sec", AuditSeverity.Error, "T", description, "src/A.cs:1"),
            ],
            Findings:
            [
                new AuditProgressFinding("sec", AuditSeverity.Error, "T", description, "src/A.cs:1"),
            ],
            WorkBranchTip: null));

    [Fact]
    public void ToDto_TruncatesLongDescription_ReportsFullLength_AndFlagsRow()
    {
        var full = new string('x', 100);
        var dto = AuditProgressDtoMapper.ToDto(Row(full), maxDescriptionChars: 10);

        var finding = Assert.Single(dto.Findings);
        Assert.Equal(10, finding.Description.Length);
        Assert.True(finding.DescriptionTruncated);
        Assert.Equal(100, finding.DescriptionLength);           // full length preserved for the UI
        Assert.True(dto.Truncated);                              // record flagged → UI fetches detail
        // The blocking-findings copy is truncated identically.
        Assert.True(Assert.Single(dto.BlockingFindingsDetails).DescriptionTruncated);
    }

    [Fact]
    public void ToDto_ShortDescription_IsNotTruncated()
    {
        var dto = AuditProgressDtoMapper.ToDto(Row("short"), maxDescriptionChars: 1000);

        var finding = Assert.Single(dto.Findings);
        Assert.Equal("short", finding.Description);
        Assert.False(finding.DescriptionTruncated);
        Assert.Equal(5, finding.DescriptionLength);
        Assert.False(dto.Truncated);
    }

    [Fact]
    public void ToDto_NullCap_ReturnsFullDescription()
    {
        var full = new string('y', 5000);
        var dto = AuditProgressDtoMapper.ToDto(Row(full), maxDescriptionChars: null);

        var finding = Assert.Single(dto.Findings);
        Assert.Equal(5000, finding.Description.Length);
        Assert.False(finding.DescriptionTruncated);
        Assert.False(dto.Truncated);
    }

    [Fact]
    public void SafeTruncate_DoesNotSplitASurrogatePair()
    {
        // "aaaaa" + 😀 (U+1F600 = surrogate pair) → 7 UTF-16 code units; index 5 is a high surrogate.
        var s = new string('a', 5) + "\U0001F600";
        Assert.Equal(7, s.Length);

        var truncated = AuditProgressDtoMapper.SafeTruncate(s, 6);

        Assert.Equal(5, truncated.Length);                      // backed off the half-pair
        Assert.Equal("aaaaa", truncated);
        Assert.False(char.IsHighSurrogate(truncated[^1]));      // no dangling lone surrogate
    }

    [Fact]
    public void SafeTruncate_ReturnsInputWhenWithinLimit()
    {
        Assert.Equal("abc", AuditProgressDtoMapper.SafeTruncate("abc", 3));
        Assert.Equal("abc", AuditProgressDtoMapper.SafeTruncate("abc", 100));
        Assert.Equal(string.Empty, AuditProgressDtoMapper.SafeTruncate("abc", 0));
    }
}
