using CodeyBox.Audit;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the Stryker JSON report parser: the real schemaVersion-2 shape
/// (observed against dotnet-stryker 4.16.0), every status bucket, the
/// score formula, and fail-closed behavior on missing/invalid/truncated
/// payloads. Parser fixtures are static JSON excerpts — they pin the
/// parsing contract only; engine integration is proven separately by
/// <c>StrykerMutationIntegrationTests</c> against the real tool.
/// </summary>
public sealed class StrykerReportParserTests
{
    private const string WeakReport = """
        {
          "schemaVersion": 2,
          "thresholds": { "high": 80, "low": 60 },
          "projectRoot": "/work/src/SampleCalc",
          "files": {
            "/work/src/SampleCalc/Calc.cs": {
              "language": "cs",
              "source": "namespace SampleCalc;",
              "mutants": [
                {
                  "id": "0",
                  "mutatorName": "Equality mutation",
                  "replacement": "x != 0",
                  "location": { "start": { "line": 5, "column": 45 }, "end": { "line": 5, "column": 50 } },
                  "status": "Killed",
                  "coveredBy": ["a"], "killedBy": ["a"]
                },
                {
                  "id": "1",
                  "mutatorName": "Equality mutation",
                  "replacement": "x >= 0",
                  "location": { "start": { "line": 5, "column": 45 }, "end": { "line": 5, "column": 50 } },
                  "status": "Survived",
                  "coveredBy": ["a"], "killedBy": []
                },
                {
                  "id": "2",
                  "mutatorName": "Arithmetic mutation",
                  "replacement": "a - b",
                  "location": { "start": { "line": 7, "column": 44 }, "end": { "line": 7, "column": 49 } },
                  "status": "Killed",
                  "coveredBy": ["b"], "killedBy": ["b"]
                }
              ]
            }
          }
        }
        """;

    [Fact]
    public void RealShapedReport_ParsesBucketsAndScore()
    {
        var result = StrykerReportParser.TryParse(WeakReport);

        Assert.True(result.Success, result.Error);
        var report = result.Report!;
        Assert.Equal(3, report.Mutants.Count);
        Assert.Equal(2, report.Killed);
        Assert.Equal(1, report.Survived);
        Assert.Equal(0, report.Timeout);
        Assert.Equal(0, report.NoCoverage);
        Assert.Equal(0, report.Ignored);
        Assert.Equal(0, report.Errored);
        Assert.Equal(2, report.SchemaVersion);
        Assert.Equal("/work/src/SampleCalc", report.ReportProjectRoot);
        Assert.Equal(["/work/src/SampleCalc/Calc.cs"], report.Files);
        // 2 detected / 3 scorable — matches Stryker's own "final mutation score is 66.67%".
        Assert.Equal(200.0 / 3.0, report.ScorePercent!.Value, precision: 9);
        var survivor = Assert.Single(report.Mutants, m => m.Status == "Survived");
        Assert.Equal(5, survivor.Line);
        Assert.Equal("Equality mutation", survivor.Mutator);
        Assert.Equal("x >= 0", survivor.Replacement);
        Assert.Equal(1, survivor.CoveredBy);
    }

    [Fact]
    public void StatusBuckets_ScoreCountsTimeoutDetected_ExcludesIgnoredAndErrors()
    {
        const string json = """
            {
              "schemaVersion": 2,
              "files": {
                "a.cs": { "mutants": [
                  { "id": "0", "mutatorName": "M", "location": { "start": { "line": 1, "column": 1 } }, "status": "Killed" },
                  { "id": "1", "mutatorName": "M", "location": { "start": { "line": 2, "column": 1 } }, "status": "Timeout" },
                  { "id": "2", "mutatorName": "M", "location": { "start": { "line": 3, "column": 1 } }, "status": "Survived" },
                  { "id": "3", "mutatorName": "M", "location": { "start": { "line": 4, "column": 1 } }, "status": "NoCoverage" },
                  { "id": "4", "mutatorName": "M", "location": { "start": { "line": 5, "column": 1 } }, "status": "Ignored" },
                  { "id": "5", "mutatorName": "M", "location": { "start": { "line": 6, "column": 1 } }, "status": "CompileError" },
                  { "id": "6", "mutatorName": "M", "location": { "start": { "line": 7, "column": 1 } }, "status": "RuntimeError" }
                ] }
              }
            }
            """;

        var result = StrykerReportParser.TryParse(json);

        Assert.True(result.Success, result.Error);
        var report = result.Report!;
        Assert.Equal(1, report.Killed);
        Assert.Equal(1, report.Timeout);
        Assert.Equal(1, report.Survived);
        Assert.Equal(1, report.NoCoverage);
        Assert.Equal(1, report.Ignored);
        Assert.Equal(2, report.Errored);
        // (1 killed + 1 timeout) / (killed + survived + timeout + nocoverage) = 2/4.
        Assert.Equal(50.0, report.ScorePercent!.Value, precision: 9);
    }

    [Fact]
    public void UnknownStatus_CountsAsErrored_NotAsScore()
    {
        const string json = """
            {
              "schemaVersion": 2,
              "files": {
                "a.cs": { "mutants": [
                  { "id": "0", "mutatorName": "M", "location": { "start": { "line": 1, "column": 1 } }, "status": "Killed" },
                  { "id": "1", "mutatorName": "M", "location": { "start": { "line": 2, "column": 1 } }, "status": "SeenInTheFuture" }
                ] }
              }
            }
            """;

        var result = StrykerReportParser.TryParse(json);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.Report!.Killed);
        Assert.Equal(1, result.Report.Errored);
        Assert.Equal(100.0, result.Report.ScorePercent!.Value, precision: 9);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all {{{")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"thresholds\": {}}")]
    [InlineData("{\"files\": []}")]
    public void InvalidPayloads_FailClosed_WithReason(string? json)
    {
        var result = StrykerReportParser.TryParse(json);

        Assert.False(result.Success);
        Assert.Null(result.Report);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void TruncatedJson_FailsClosed()
    {
        var truncated = WeakReport[..(WeakReport.Length / 2)];

        var result = StrykerReportParser.TryParse(truncated);

        Assert.False(result.Success);
        Assert.Contains("JSON", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllIgnoredOrEmpty_ParsesWithNullScore_RunnerDecidesEvidence()
    {
        const string json = """
            {
              "schemaVersion": 2,
              "files": {
                "a.cs": { "mutants": [
                  { "id": "0", "mutatorName": "M", "location": { "start": { "line": 1, "column": 1 } }, "status": "Ignored" }
                ] }
              }
            }
            """;

        var result = StrykerReportParser.TryParse(json);

        Assert.True(result.Success, result.Error);
        Assert.Null(result.Report!.ScorePercent);
        Assert.Equal(1, result.Report.Ignored);
        Assert.Equal(["a.cs"], result.Report.Files);
    }

    [Fact]
    public void MalformedMutant_FailsClosed()
    {
        const string json = """
            {
              "schemaVersion": 2,
              "files": {
                "a.cs": { "mutants": [
                  { "id": "0", "status": "Killed" }
                ] }
              }
            }
            """;

        var result = StrykerReportParser.TryParse(json);

        Assert.False(result.Success);
        Assert.Contains("malformed mutant", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ZeroLineNumber_FailsClosed()
    {
        const string json = """
            {
              "schemaVersion": 2,
              "files": {
                "a.cs": { "mutants": [
                  { "id": "0", "mutatorName": "M", "location": { "start": { "line": 0, "column": 1 } }, "status": "Killed" }
                ] }
              }
            }
            """;

        var result = StrykerReportParser.TryParse(json);

        Assert.False(result.Success);
    }

    [Fact]
    public void MissingCoveredBy_DefaultsToZero()
    {
        const string json = """
            {
              "schemaVersion": 2,
              "files": {
                "a.cs": { "mutants": [
                  { "id": "0", "mutatorName": "M", "location": { "start": { "line": 1, "column": 1 } }, "status": "Survived" }
                ] }
              }
            }
            """;

        var result = StrykerReportParser.TryParse(json);

        Assert.True(result.Success, result.Error);
        Assert.Equal(0, result.Report!.Mutants[0].CoveredBy);
    }

    [Fact]
    public void EmptyFiles_ParsesWithFileKeysAndNullScore()
    {
        const string json = """{ "schemaVersion": 2, "files": { "a.cs": { "mutants": [] } } } """;

        var result = StrykerReportParser.TryParse(json);

        Assert.True(result.Success, result.Error);
        Assert.Null(result.Report!.ScorePercent);
        Assert.Equal(["a.cs"], result.Report.Files);
    }

    [Fact]
    public void ControlCharactersInMutant_FailClosed()
    {
        // Report strings echo attacker-influenceable repo content (mutated
        // source text) and flow into finding Titles/Descriptions, hence the
        // rework prompt: control characters (newlines, ANSI escapes) must
        // fail the parse, never embed.
        var hostile = new[]
        {
            """{ "schemaVersion": 2, "files": { "a.cs": { "mutants": [ { "id": "0", "mutatorName": "Bad\nmutator", "location": { "start": { "line": 1, "column": 1 } }, "status": "Survived" } ] } } }""",
            """{ "schemaVersion": 2, "files": { "a.cs": { "mutants": [ { "id": "0", "mutatorName": "M", "replacement": "x\u001b[31m", "location": { "start": { "line": 1, "column": 1 } }, "status": "Survived" } ] } } }""",
            """{ "schemaVersion": 2, "files": { "a.cs": { "mutants": [ { "id": "0", "mutatorName": "M", "location": { "start": { "line": 1, "column": 1 } }, "status": "Survived\nKilled" } ] } } }""",
        };

        foreach (var json in hostile)
        {
            var result = StrykerReportParser.TryParse(json);

            Assert.False(result.Success);
            Assert.Null(result.Report);
        }
    }
}
