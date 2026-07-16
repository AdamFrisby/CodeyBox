using CodeyBox.Audit;

namespace CodeyBox.Tests;

public sealed class CoverageGateEvaluatorTests
{
    private static IReadOnlyDictionary<string, IReadOnlySet<int>> Changed(
        string file, params int[] lines)
        => new Dictionary<string, IReadOnlySet<int>>(StringComparer.Ordinal)
        {
            [file] = new SortedSet<int>(lines),
        };

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>> Coverage(
        string file, params (int Line, int Hits)[] entries)
        => new Dictionary<string, IReadOnlyDictionary<int, int>>(StringComparer.Ordinal)
        {
            [file] = entries.ToDictionary(e => e.Line, e => e.Hits),
        };

    [Fact]
    public void ChangedCoveredLine_ProducesNoFinding()
    {
        var result = CoverageGateEvaluator.Evaluate(
            Changed("src/Foo.cs", 10),
            Coverage("src/Foo.cs", (10, 3)),
            []);

        Assert.Empty(result.UncoveredLines);
        Assert.Empty(result.AppliedExclusions);
    }

    [Fact]
    public void ChangedUncoveredLine_IsReported()
    {
        var result = CoverageGateEvaluator.Evaluate(
            Changed("src/Foo.cs", 11),
            Coverage("src/Foo.cs", (11, 0)),
            []);

        var uncovered = Assert.Single(result.UncoveredLines);
        Assert.Equal("src/Foo.cs", uncovered.File);
        Assert.Equal(11, uncovered.Line);
    }

    [Fact]
    public void ExcludedWithJustification_SuppressesAndLogs()
    {
        var exclusion = new CoverageExclusion
        {
            File = "src/Foo.cs",
            Line = 11,
            Justification = "generated code",
        };

        var result = CoverageGateEvaluator.Evaluate(
            Changed("src/Foo.cs", 11),
            Coverage("src/Foo.cs", (11, 0)),
            [exclusion]);

        Assert.Empty(result.UncoveredLines);
        var applied = Assert.Single(result.AppliedExclusions);
        Assert.Equal("src/Foo.cs", applied.File);
        Assert.Equal(11, applied.Line);
        Assert.Equal("generated code", applied.Justification);
        Assert.Empty(result.UnusedExclusions);
        Assert.Empty(result.InvalidExclusions);
    }

    [Fact]
    public void UnchangedUncoveredLine_IsIgnored()
    {
        // Line 11 is uncovered but NOT changed; only changed line 10 (covered)
        // is in scope. Nothing gates.
        var result = CoverageGateEvaluator.Evaluate(
            Changed("src/Foo.cs", 10),
            Coverage("src/Foo.cs", (10, 1), (11, 0)),
            []);

        Assert.Empty(result.UncoveredLines);
    }

    [Fact]
    public void ChangedNonExecutableLine_IsIgnored()
    {
        // Line 12 is changed but absent from the coverage report (comment/brace)
        // → not executable → not gated.
        var result = CoverageGateEvaluator.Evaluate(
            Changed("src/Foo.cs", 12),
            Coverage("src/Foo.cs", (10, 1), (11, 0)),
            []);

        Assert.Empty(result.UncoveredLines);
    }

    [Fact]
    public void ExclusionWithoutJustification_DoesNotSuppressAndIsReportedInvalid()
    {
        var exclusion = new CoverageExclusion
        {
            File = "src/Foo.cs",
            Line = 11,
            Justification = "   ",
        };

        var result = CoverageGateEvaluator.Evaluate(
            Changed("src/Foo.cs", 11),
            Coverage("src/Foo.cs", (11, 0)),
            [exclusion]);

        // Line still gates AND the missing justification is surfaced.
        Assert.Single(result.UncoveredLines);
        Assert.Single(result.InvalidExclusions);
        Assert.Empty(result.AppliedExclusions);
    }

    [Fact]
    public void UnusedExclusion_IsReported()
    {
        var exclusion = new CoverageExclusion
        {
            File = "src/Foo.cs",
            Line = 99,
            Justification = "was untestable, now removed",
        };

        var result = CoverageGateEvaluator.Evaluate(
            Changed("src/Foo.cs", 11),
            Coverage("src/Foo.cs", (11, 0)),
            [exclusion]);

        Assert.Single(result.UncoveredLines);
        var unused = Assert.Single(result.UnusedExclusions);
        Assert.Equal(99, unused.Line);
    }

    [Fact]
    public void RangeExclusion_CoversEveryLineInRange()
    {
        var exclusion = new CoverageExclusion
        {
            File = "src/Gen.cs",
            Line = 20,
            LineEnd = 22,
            Justification = "generated block",
        };

        var result = CoverageGateEvaluator.Evaluate(
            Changed("src/Gen.cs", 20, 21, 22),
            Coverage("src/Gen.cs", (20, 0), (21, 0), (22, 0)),
            [exclusion]);

        Assert.Empty(result.UncoveredLines);
        Assert.Equal(3, result.AppliedExclusions.Count);
    }

    [Fact]
    public void Results_AreSortedDeterministically()
    {
        var changed = new Dictionary<string, IReadOnlySet<int>>(StringComparer.Ordinal)
        {
            ["src/B.cs"] = new SortedSet<int> { 5 },
            ["src/A.cs"] = new SortedSet<int> { 30, 2 },
        };
        var coverage = new Dictionary<string, IReadOnlyDictionary<int, int>>(StringComparer.Ordinal)
        {
            ["src/B.cs"] = new Dictionary<int, int> { [5] = 0 },
            ["src/A.cs"] = new Dictionary<int, int> { [2] = 0, [30] = 0 },
        };

        var result = CoverageGateEvaluator.Evaluate(changed, coverage, []);

        Assert.Collection(result.UncoveredLines,
            u => Assert.Equal(("src/A.cs", 2), (u.File, u.Line)),
            u => Assert.Equal(("src/A.cs", 30), (u.File, u.Line)),
            u => Assert.Equal(("src/B.cs", 5), (u.File, u.Line)));
    }
}
