using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Direct input→output tests of the pure audit-check payload core: conclusion
/// mapping, SHA attribution, location validation, batching, and redaction.
/// </summary>
public sealed class AuditCheckPayloadBuilderTests
{
    private static readonly AuditCheckPublicationOptions TestOptions = new()
    {
        Enabled = true,
        MaxAnnotationsPerBatch = 50,
        MaxAnnotationBatches = 2,
        MaxSummaryChars = 32_768,
        MaxAnnotationMessageChars = 4_096,
        MaxAnnotationTitleChars = 120,
        MaxAnnotationLineSpan = 20,
        MaxOmittedFindingsInSummary = 20,
    };

    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static AuditReportFinding Finding(
        string title = "Null dereference",
        string severity = "error",
        string[]? files = null,
        int[]? lines = null,
        string message = "Possible null dereference.")
        => new(
            Guid.NewGuid().ToString("N"),
            severity,
            title,
            message,
            files ?? ["src/Widget.cs"],
            lines ?? [42]);

    private static AuditCheckPublicationRequest Request(
        AuditCheckVerdict verdict,
        AuditCheckUnavailabilityReason? reason = null,
        IReadOnlyList<AuditReportFinding>? findings = null,
        AuditCheckLifecycle lifecycle = AuditCheckLifecycle.Completed)
        => new()
        {
            Owner = "myorg",
            Repository = "myrepo",
            HeadSha = Sha,
            WorkItemId = "work-1",
            Target = AuditTarget.Code,
            Iteration = 3,
            Attempt = 1,
            Scope = "aggregate",
            CheckName = "codeybox-audit",
            ExternalId = "codeybox/work-1/code/3/1/aggregate/" + Sha,
            Lifecycle = lifecycle,
            Verdict = verdict,
            UnavailabilityReason = reason,
            Findings = findings ?? [],
        };

    [Theory]
    [InlineData(AuditCheckVerdict.Passed, null, "success")]
    [InlineData(AuditCheckVerdict.Failed, null, "failure")]
    [InlineData(AuditCheckVerdict.NotRun, AuditCheckUnavailabilityReason.Missing, "skipped")]
    [InlineData(AuditCheckVerdict.NotRun, AuditCheckUnavailabilityReason.Skipped, "skipped")]
    [InlineData(AuditCheckVerdict.NotRun, AuditCheckUnavailabilityReason.UnsupportedForge, "skipped")]
    [InlineData(AuditCheckVerdict.NotRun, AuditCheckUnavailabilityReason.Cancelled, "cancelled")]
    [InlineData(AuditCheckVerdict.NotRun, AuditCheckUnavailabilityReason.InfrastructureFailed, "action_required")]
    public void ToConclusionString_MapsVerdictsHonestly(
        AuditCheckVerdict verdict, AuditCheckUnavailabilityReason? reason, string expected)
    {
        Assert.Equal(expected, AuditCheckConclusionMapper.ToConclusionString(
            AuditCheckLifecycle.Completed, verdict, reason));
    }

    [Fact]
    public void ToConclusionString_NonCompletedLifecycle_HasNoConclusion()
    {
        Assert.Null(AuditCheckConclusionMapper.ToConclusionString(
            AuditCheckLifecycle.Queued, AuditCheckVerdict.Passed, null));
        Assert.Null(AuditCheckConclusionMapper.ToConclusionString(
            AuditCheckLifecycle.InProgress, AuditCheckVerdict.Failed, null));
        Assert.Equal("queued", AuditCheckConclusionMapper.ToStatusString(AuditCheckLifecycle.Queued));
        Assert.Equal("in_progress", AuditCheckConclusionMapper.ToStatusString(AuditCheckLifecycle.InProgress));
        Assert.Equal("completed", AuditCheckConclusionMapper.ToStatusString(AuditCheckLifecycle.Completed));
    }

    [Fact]
    public void BuildAnnotations_EmptyFindingsWithPassedVerdict_MapsToSuccess()
    {
        // An empty findings list with an explicit Passed verdict is success:
        // the verdict input (report-row existence upstream) is the coverage
        // proof, not the count. The mapper never infers coverage from counts.
        var conclusion = AuditCheckConclusionMapper.ToConclusionString(
            AuditCheckLifecycle.Completed, AuditCheckVerdict.Passed, null);
        Assert.Equal("success", conclusion);
        var (annotations, omitted) = AuditCheckPayloadBuilder.BuildAnnotations([], TestOptions);
        Assert.Empty(annotations);
        Assert.Empty(omitted);
    }

    [Fact]
    public void BuildAnnotations_NotRunVerdict_DisclosesNoCoverage()
    {
        var summary = AuditCheckPayloadBuilder.BuildSummary(
            Request(AuditCheckVerdict.NotRun, AuditCheckUnavailabilityReason.Missing), [], 0, false, TestOptions);
        Assert.Contains("not successful coverage", summary, StringComparison.Ordinal);
        Assert.Contains("audit did not run", summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/abs/path.cs")]
    [InlineData("../escape.cs")]
    [InlineData("a/../../escape.cs")]
    [InlineData("C:\\win\\path.cs")]
    [InlineData("src/\0null.cs")]
    public void BuildAnnotations_UntrustedPaths_AreOmittedNeverAnnotated(string path)
    {
        var finding = Finding(files: [path], lines: [10]);
        var (annotations, omitted) = AuditCheckPayloadBuilder.BuildAnnotations([finding], TestOptions);
        Assert.Empty(annotations);
        var entry = Assert.Single(omitted);
        Assert.Contains("untrusted path", entry.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildAnnotations_BlankPaths_ReportNoLocation(string path)
    {
        var finding = Finding(files: [path], lines: [10]);
        var (annotations, omitted) = AuditCheckPayloadBuilder.BuildAnnotations([finding], TestOptions);
        Assert.Empty(annotations);
        Assert.Contains("no file location", Assert.Single(omitted).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAnnotations_MultipleFiles_AreOmittedNeverAnnotated()
    {
        var finding = Finding(files: ["a.cs", "b.cs"], lines: [1]);
        var (annotations, omitted) = AuditCheckPayloadBuilder.BuildAnnotations([finding], TestOptions);
        Assert.Empty(annotations);
        Assert.Contains("multiple", Assert.Single(omitted).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAnnotations_NoFilesOrLines_AreOmittedWithReasons()
    {
        var noFile = Finding(files: [], lines: [5]);
        var noLine = Finding(files: ["ok.cs"], lines: []);
        var zeroLine = Finding(files: ["ok.cs"], lines: [0, -3]);
        var (annotations, omitted) = AuditCheckPayloadBuilder.BuildAnnotations([noFile, noLine, zeroLine], TestOptions);
        Assert.Empty(annotations);
        Assert.Equal(3, omitted.Count);
    }

    [Fact]
    public void BuildAnnotations_WideSpan_IsOmitted()
    {
        var finding = Finding(files: ["big.cs"], lines: [1, 500]);
        var (annotations, omitted) = AuditCheckPayloadBuilder.BuildAnnotations([finding], TestOptions);
        Assert.Empty(annotations);
        Assert.Contains("span", Assert.Single(omitted).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAnnotations_ValidFinding_ProducesValidatedAnnotation()
    {
        var (annotations, omitted) = AuditCheckPayloadBuilder.BuildAnnotations(
            [Finding(files: ["./src/Widget.cs"], lines: [42, 44])], TestOptions);
        Assert.Empty(omitted);
        var annotation = Assert.Single(annotations);
        Assert.Equal("src/Widget.cs", annotation.Path);
        Assert.Equal(42, annotation.StartLine);
        Assert.Equal(44, annotation.EndLine);
        Assert.Equal("failure", annotation.Level);
    }

    [Theory]
    [InlineData("error", "failure")]
    [InlineData("warning", "warning")]
    [InlineData("info", "notice")]
    [InlineData("weird-level", "notice")]
    public void BuildAnnotations_Severity_MapsToAnnotationLevel(string severity, string level)
    {
        var (annotations, _) = AuditCheckPayloadBuilder.BuildAnnotations(
            [Finding(severity: severity)], TestOptions);
        Assert.Equal(level, Assert.Single(annotations).Level);
    }

    [Fact]
    public void BuildAnnotations_SecretInFinding_IsRedacted()
    {
        var finding = Finding(
            message: "Leaked token ghp_XYZabc789012345678901234567890 in config.",
            lines: [7]);
        var (annotations, _) = AuditCheckPayloadBuilder.BuildAnnotations([finding], TestOptions);
        var annotation = Assert.Single(annotations);
        Assert.DoesNotContain("ghp_", annotation.Message, StringComparison.Ordinal);
        Assert.Contains("***", annotation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSummary_OmittedFindings_AppearInSummaryNotAsAnnotations()
    {
        var request = Request(AuditCheckVerdict.Failed, findings: [Finding(files: [], lines: [1])]);
        var (_, omitted) = AuditCheckPayloadBuilder.BuildAnnotations(request.Findings, TestOptions);
        Assert.Single(omitted);
        var summary = AuditCheckPayloadBuilder.BuildSummary(request, omitted, 0, false, TestOptions);
        Assert.Contains("Null dereference", summary, StringComparison.Ordinal);
        Assert.Contains("no file location", summary, StringComparison.Ordinal);
        Assert.Contains("work-1", summary, StringComparison.Ordinal);
        Assert.Contains(Sha, summary, StringComparison.Ordinal);
        Assert.Contains("does not approve merging", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ChunkBatches_BoundedBatches_PreservesRemainderAsOverflow()
    {
        var findings = Enumerable.Range(1, 120)
            .Select(i => Finding(title: $"Finding {i}", files: [$"src/File{i}.cs"], lines: [i]))
            .ToList();
        var (annotations, omitted) = AuditCheckPayloadBuilder.BuildAnnotations(findings, TestOptions);
        Assert.Empty(omitted);
        var (batches, overflow) = AuditCheckPayloadBuilder.ChunkBatches(annotations, TestOptions);
        Assert.Equal(2, batches.Count);
        Assert.All(batches, b => Assert.True(b.Count <= 50));
        Assert.Equal(20, overflow);

        var request = Request(AuditCheckVerdict.Failed, findings: findings);
        var summary = AuditCheckPayloadBuilder.BuildSummary(request, [], overflow, false, TestOptions);
        Assert.Contains("20 annotation(s) exceed the bounded batch budget", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSummary_BatchUncertain_DisclosesUncertainty()
    {
        var summary = AuditCheckPayloadBuilder.BuildSummary(
            Request(AuditCheckVerdict.Passed), [], 0, true, TestOptions);
        Assert.Contains("could not be reconciled", summary, StringComparison.Ordinal);
        Assert.Contains("authoritative", summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc123")]
    [InlineData("0123456789abcdef0123456789abcdef0123456X")]
    [InlineData("0123456789abcdef0123456789abcdef0123456")]
    public void ValidateHeadSha_RejectsNonExactCommits(string sha)
    {
        Assert.Throws<AuditCheckValidationException>(() => AuditCheckPayloadBuilder.ValidateHeadSha(sha));
    }

    [Fact]
    public void ValidateHeadSha_AcceptsExactCommit()
    {
        AuditCheckPayloadBuilder.ValidateHeadSha(Sha);
        AuditCheckPayloadBuilder.ValidateHeadSha(Sha.ToUpperInvariant());
    }

    [Fact]
    public void BuildExternalId_RejectsOverlongCorrelation()
    {
        var longScope = new string('s', 300);
        Assert.Throws<ArgumentException>(() => AuditCheckPayloadBuilder.BuildExternalId(
            "work-1", AuditTarget.Code, 1, 1, longScope, Sha));
    }

    [Fact]
    public void BuildTitle_IsBoundedAndRedacted()
    {
        var request = Request(AuditCheckVerdict.Failed) with { Scope = new string('x', 400) };
        var title = AuditCheckPayloadBuilder.BuildTitle(request);
        Assert.True(title.Length <= AuditCheckPayloadBuilder.MaxTitleChars);
    }
}
