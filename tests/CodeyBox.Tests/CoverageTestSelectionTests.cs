using System.Text.Json;
using CodeyBox.Api;
using CodeyBox.Audit.Shell;
using CodeyBox.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

/// <summary>
/// Coverage for the coverage-guided test selector: the baseline format and
/// its caps, the freshness policy, both selectors' narrowing and fail-safe
/// fallbacks, the shared shadow-validation harness, the new mode wiring, and
/// the advisory-only hook in <see cref="DotnetTestAuditor"/> (full suite still
/// runs; NO test is skipped by this ticket).
/// </summary>
public sealed class CoverageTestSelectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 6, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static CoverageTestSelectionOptions FreshOptions() => new();

    private static TestSelectionBaseline Baseline(
        string commit = "abc123",
        DateTimeOffset? producedAt = null,
        Dictionary<string, string>? fileProject = null,
        Dictionary<string, IReadOnlyList<string>>? affected = null,
        Dictionary<string, BaselineTestEntry>? tests = null)
        => new(
            commit,
            producedAt ?? Now.AddHours(-1),
            new BaselineProjectGraph(
                fileProject ?? new Dictionary<string, string>(StringComparer.Ordinal),
                affected ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)),
            tests ?? new Dictionary<string, BaselineTestEntry>(StringComparer.Ordinal));

    private static TestSelectionBaseline StandardBaseline(DateTimeOffset? producedAt = null)
        => Baseline(
            producedAt: producedAt,
            fileProject: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["src/Foo/Bar.cs"] = "src/Foo/Foo.csproj",
            },
            affected: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["src/Foo/Foo.csproj"] = ["Ns.Foo.BarTests", "Ns.Foo.OtherTests"],
            },
            tests: new Dictionary<string, BaselineTestEntry>(StringComparer.Ordinal)
            {
                ["Ns.Foo.BarTests"] = new BaselineTestEntry(
                    "tests/Foo.Tests/BarTests.cs",
                    new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
                    {
                        ["src/Foo/Bar.cs"] = [10, 11, 12],
                    }),
                ["Ns.Foo.OtherTests"] = new BaselineTestEntry(
                    "tests/Foo.Tests/OtherTests.cs",
                    new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
                    {
                        ["src/Foo/Bar.cs"] = [50],
                    }),
                ["Ns.Foo.NewTests"] = new BaselineTestEntry(
                    "tests/Foo.Tests/NewTests.cs",
                    new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)),
            });

    private static TestSelectionRequest RequestFor(
        ITestRunnerAuditor runner,
        TestSelectionBaseline? baseline,
        params TestSelectionChangedFile[] files)
        => new(runner, "main", files, baseline);

    private static DotnetTestAuditor NewRunner()
        => new(new DotnetTestAuditorOptions
        {
            Name = "csharp:test-pass",
            BaseArgv = ["dotnet", "test", "--no-build"],
        });

    private static readonly BaselineReadLimits Limits =
        new(1024 * 1024, MaxTests: 10_000, MaxCoveredLines: 1_000_000);

    // ---- Baseline parser.

    [Fact]
    public void BaselineParser_ParsesValidDocument()
    {
        var json = """
            {
              "format": "codeybox-test-selection-baseline/1",
              "commit": "abc123",
              "producedAtUtc": "2026-09-12T05:00:00Z",
              "fileProject": { "src/Foo/Bar.cs": "src/Foo/Foo.csproj" },
              "projects": { "src/Foo/Foo.csproj": ["Ns.Foo.BarTests"] },
              "tests": {
                "Ns.Foo.BarTests": {
                  "file": "tests/Foo.Tests/BarTests.cs",
                  "covers": { "src/Foo/Bar.cs": [12, 10, 10, 11] }
                }
              }
            }
            """;

        var baseline = TestSelectionBaselineParser.Parse(json, Limits);

        Assert.Equal("abc123", baseline.Commit);
        Assert.Equal("src/Foo/Foo.csproj", baseline.ProjectGraph.FileProject["src/Foo/Bar.cs"]);
        Assert.Equal(["Ns.Foo.BarTests"], baseline.ProjectGraph.AffectedTestsByProject["src/Foo/Foo.csproj"]);
        // Normalised: deduplicated and sorted.
        Assert.Equal([10, 11, 12], baseline.Tests["Ns.Foo.BarTests"].Covers["src/Foo/Bar.cs"]);
    }

    [Theory]
    [InlineData("""{"format": "cobertura", "producedAtUtc": "2026-09-12T05:00:00Z"}""")]
    [InlineData("""{"producedAtUtc": "2026-09-12T05:00:00Z"}""")]
    [InlineData("""{"format": "codeybox-test-selection-baseline/1"}""")]
    [InlineData("""{"format": "codeybox-test-selection-baseline/1", "producedAtUtc": "not-a-date"}""")]
    public void BaselineParser_RejectsBadFormatOrTimestamp(string json)
        => Assert.Throws<FormatException>(() => TestSelectionBaselineParser.Parse(json, Limits));

    [Fact]
    public void BaselineParser_RejectsMalformedJson()
        => Assert.ThrowsAny<JsonException>(() => TestSelectionBaselineParser.Parse("{nope", Limits));

    [Fact]
    public void BaselineParser_EnforcesSizeCapBeforeParsing()
    {
        var ex = Assert.Throws<FormatException>(() =>
            TestSelectionBaselineParser.Parse("{}", new BaselineReadLimits(1, 10_000, 1_000_000)));
        Assert.Contains("size cap", ex.Message);
    }

    [Fact]
    public void BaselineParser_EnforcesTestCap()
    {
        var json = """{"format": "codeybox-test-selection-baseline/1", "producedAtUtc": "2026-09-12T05:00:00Z", "tests": {"A": {}, "B": {}}}""";
        var ex = Assert.Throws<FormatException>(() =>
            TestSelectionBaselineParser.Parse(json, new BaselineReadLimits(1_000_000, 1, 1_000_000)));
        Assert.Contains("test cap", ex.Message);
    }

    [Fact]
    public void BaselineParser_SkipsNonPositiveLineNumbers()
    {
        var json = """
            {"format": "codeybox-test-selection-baseline/1", "producedAtUtc": "2026-09-12T05:00:00Z",
             "tests": {"T": {"file": "f.cs", "covers": {"s.cs": [0, -3, 7]}}}}
            """;
        var baseline = TestSelectionBaselineParser.Parse(json, Limits);
        Assert.Equal([7], baseline.Tests["T"].Covers["s.cs"]);
    }

    // ---- Freshness.

    [Fact]
    public void Freshness_FreshBaseline()
    {
        var verdict = TestSelectionBaselineFreshness.Check(
            StandardBaseline(), Now, TimeSpan.FromDays(7), currentCommit: "abc123");
        Assert.True(verdict.IsFresh);
    }

    [Fact]
    public void Freshness_StaleWhenTooOld()
    {
        var verdict = TestSelectionBaselineFreshness.Check(
            StandardBaseline(Now.AddDays(-8)), Now, TimeSpan.FromDays(7), currentCommit: null);
        Assert.False(verdict.IsFresh);
        Assert.Contains("old", verdict.Reason);
    }

    [Fact]
    public void Freshness_StaleWhenDatedInFuture()
    {
        var verdict = TestSelectionBaselineFreshness.Check(
            StandardBaseline(Now.AddHours(1)), Now, TimeSpan.FromDays(7), currentCommit: null);
        Assert.False(verdict.IsFresh);
    }

    [Fact]
    public void Freshness_StaleOnCommitMismatch_MatchesWhenUnknown()
    {
        Assert.False(TestSelectionBaselineFreshness.Check(
            StandardBaseline(), Now, TimeSpan.FromDays(7), currentCommit: "other").IsFresh);
        // Unknown on either side disables the commit check (age still applies).
        Assert.True(TestSelectionBaselineFreshness.Check(
            StandardBaseline(), Now, TimeSpan.FromDays(7), currentCommit: null).IsFresh);
        Assert.True(TestSelectionBaselineFreshness.Check(
            Baseline(commit: ""), Now, TimeSpan.FromDays(7), currentCommit: "other").IsFresh);
    }

    [Fact]
    public void Freshness_NonPositiveMaxAgeIsStale()
        => Assert.False(TestSelectionBaselineFreshness.Check(
            StandardBaseline(), Now, TimeSpan.Zero, currentCommit: null).IsFresh);

    // ---- Paths and global targets.

    [Fact]
    public void Paths_NormalizeBackslashes()
        => Assert.Equal("src/Foo/Bar.cs", TestSelectionPaths.Normalize(@"src\Foo\Bar.cs"));

    [Theory]
    [InlineData("tests/Foo.Tests/BarTests.cs", true)]
    [InlineData("test/unit/x.cs", true)]
    [InlineData("src/MyTestHelper.cs", true)]
    [InlineData("src/Foo/Bar.cs", false)]
    [InlineData("src/contest/Foo.cs", false)]
    public void Paths_ProbableTestFile(string path, bool expected)
        => Assert.Equal(expected, TestSelectionPaths.IsProbableTestFile(path));

    [Theory]
    [InlineData("Directory.Build.props", true)]
    [InlineData("src/Directory.Build.props", true)]
    [InlineData("CodeyBox.slnx", true)]
    [InlineData(".github/workflows/ci.yml", true)]
    [InlineData("src/Foo/Bar.cs", false)]
    [InlineData("src/Directory.Build.props.bak", false)]
    [InlineData("src/xDirectory.Build.props", false)]
    [InlineData(".github/workflows-bak/ci.yml", false)]
    public void Options_GlobalTargets_ExactMatchOnly(string path, bool expected)
        => Assert.Equal(expected, FreshOptions().IsGlobalTarget(path));

    [Fact]
    public void Options_Validation()
    {
        Assert.True(CoverageTestSelectionOptions.IsValid(new CoverageTestSelectionOptions()));
        Assert.False(CoverageTestSelectionOptions.IsValid(null));
        Assert.False(CoverageTestSelectionOptions.IsValid(
            new CoverageTestSelectionOptions { MaxBaselineAge = TimeSpan.Zero }));
        Assert.False(CoverageTestSelectionOptions.IsValid(
            new CoverageTestSelectionOptions { GlobalDirectoryPrefixes = ["noprefix"] }));
        Assert.False(CoverageTestSelectionOptions.IsValid(
            new CoverageTestSelectionOptions { MaxDiffBytes = 0 }));
    }

    // ---- Project-graph selector.

    [Fact]
    public void ProjectGraph_SelectsAffectedTests()
    {
        var runner = NewRunner();
        var decision = new ProjectGraphTestSelector().Select(RequestFor(
            runner, StandardBaseline(),
            new TestSelectionChangedFile("src/Foo/Bar.cs", [new ChangedLineRange(10, 1)])));

        Assert.False(decision.Selection.IsAll);
        Assert.Equal(
            ["Ns.Foo.BarTests", "Ns.Foo.OtherTests"],
            [.. decision.Selection.Filters.OrderBy(f => f, StringComparer.Ordinal)]);
        Assert.StartsWith(ProjectGraphTestSelector.SelectorName, decision.Justification);
    }

    [Theory]
    [InlineData("src/Unknown/File.cs")]
    public void ProjectGraph_UnknownFile_FallsBackToFullSuite(string path)
    {
        var decision = new ProjectGraphTestSelector().Select(RequestFor(
            NewRunner(), StandardBaseline(),
            new TestSelectionChangedFile(path, [new ChangedLineRange(1, 1)])));
        Assert.True(decision.Selection.IsAll);
        Assert.Contains("no project-graph record", decision.Justification);
    }

    [Fact]
    public void ProjectGraph_FailSafePaths_FallBackToFullSuite()
    {
        var selector = new ProjectGraphTestSelector();
        var runner = NewRunner();

        // No baseline.
        Assert.True(selector.Select(RequestFor(runner, null,
            new TestSelectionChangedFile("src/Foo/Bar.cs", [new ChangedLineRange(1, 1)]))).Selection.IsAll);
        // Unknown changeset.
        Assert.True(selector.Select(RequestFor(runner, StandardBaseline())).Selection.IsAll);
        // Whole-file change (no line granularity).
        Assert.True(selector.Select(RequestFor(runner, StandardBaseline(),
            new TestSelectionChangedFile("src/Foo/Bar.cs", []))).Selection.IsAll);
    }

    // ---- Coverage selector.

    private static CoverageTestSelector NewCoverageSelector(TimeProvider? clock = null)
        => new(new ProjectGraphTestSelector(), FreshOptions, clock ?? new FixedClock(Now));

    [Fact]
    public void Coverage_SelectsIntersectingPlusMustInclude_WithinSuperset()
    {
        var runner = NewRunner();
        var decision = NewCoverageSelector().Select(RequestFor(
            runner, StandardBaseline(),
            new TestSelectionChangedFile("src/Foo/Bar.cs", [new ChangedLineRange(10, 2)])));

        Assert.False(decision.Selection.IsAll);
        var selected = decision.Selection.Filters.OrderBy(f => f, StringComparer.Ordinal).ToList();
        // Line 10-11 intersect BarTests; OtherTests rides the project-graph
        // superset; NewTests has no coverage record and is always included.
        Assert.Equal(
            ["Ns.Foo.BarTests", "Ns.Foo.NewTests", "Ns.Foo.OtherTests"],
            selected);
    }

    [Fact]
    public void Coverage_NonExecutableChange_KeepsSupersetOnly()
    {
        // Line 99 is executable nowhere, but the file is referenced — the
        // change contributes no coverage hits, so the superset floor stands.
        var decision = NewCoverageSelector().Select(RequestFor(
            NewRunner(), StandardBaseline(),
            new TestSelectionChangedFile("src/Foo/Bar.cs", [new ChangedLineRange(99, 1)])));

        Assert.False(decision.Selection.IsAll);
        Assert.Equal(
            ["Ns.Foo.BarTests", "Ns.Foo.NewTests", "Ns.Foo.OtherTests"],
            [.. decision.Selection.Filters.OrderBy(f => f, StringComparer.Ordinal)]);
    }

    [Fact]
    public void Coverage_PreservesSupersetFiltersVerbatim_NeverSelectsLess()
    {
        // A superset emitting a raw expression: the coverage selector must
        // preserve it verbatim (never less than the superset).
        var superset = new RawExpressionSelector("FullyQualifiedName~Flaky");
        var selector = new CoverageTestSelector(superset, FreshOptions, new FixedClock(Now));
        var decision = selector.Select(RequestFor(
            NewRunner(), StandardBaseline(),
            new TestSelectionChangedFile("src/Foo/Bar.cs", [new ChangedLineRange(10, 1)])));

        Assert.Contains("FullyQualifiedName~Flaky", decision.Selection.Filters);
        Assert.Contains("Ns.Foo.BarTests", decision.Selection.Filters);
        Assert.Contains("Ns.Foo.NewTests", decision.Selection.Filters);
    }

    [Fact]
    public void Coverage_SupersetFullSuite_StaysFullSuite()
    {
        var selector = new CoverageTestSelector(
            new ProjectGraphTestSelector(), FreshOptions, new FixedClock(Now));
        var decision = selector.Select(RequestFor(
            NewRunner(), StandardBaseline(),
            new TestSelectionChangedFile("src/Unknown/File.cs", [new ChangedLineRange(1, 1)])));
        Assert.True(decision.Selection.IsAll);
    }

    [Theory]
    [InlineData("Directory.Build.props")]
    [InlineData(".github/workflows/ci.yml")]
    public void Coverage_GlobalTarget_FallsBackToFullSuite(string path)
    {
        // The project graph knows these files (so the superset narrows), but
        // the coverage core still refuses: global targets always run everything.
        var baseline = Baseline(
            fileProject: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [path] = "root/Root.proj",
            },
            affected: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["root/Root.proj"] = ["T"],
            },
            tests: new Dictionary<string, BaselineTestEntry>(StringComparer.Ordinal)
            {
                ["T"] = new BaselineTestEntry(
                    "tests/T.cs",
                    new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
                    {
                        [path] = [1],
                    }),
            });
        var decision = NewCoverageSelector().Select(RequestFor(
            NewRunner(), baseline,
            new TestSelectionChangedFile(path, [new ChangedLineRange(1, 1)])));
        Assert.True(decision.Selection.IsAll);
        Assert.Contains("global target", decision.Justification);
    }

    [Fact]
    public void Coverage_FailSafePaths_FallBackToFullSuite()
    {
        var selector = NewCoverageSelector();
        var runner = NewRunner();
        var change = new TestSelectionChangedFile("src/Foo/Bar.cs", [new ChangedLineRange(10, 1)]);

        Assert.True(selector.Select(RequestFor(runner, null, change)).Selection.IsAll); // no baseline
        Assert.True(selector.Select(RequestFor(runner, StandardBaseline())).Selection.IsAll); // unknown changeset
        Assert.True(selector.Select(RequestFor(runner,
            StandardBaseline(Now.AddDays(-30)), change)).Selection.IsAll); // stale
        Assert.True(selector.Select(RequestFor(runner, StandardBaseline(),
            new TestSelectionChangedFile("src/Foo/Bar.cs", []))).Selection.IsAll); // whole-file
        Assert.True(selector.Select(RequestFor(runner, StandardBaseline(),
            new TestSelectionChangedFile("tests/Foo.Tests/BarTests.cs", [new ChangedLineRange(5, 1)]))).Selection.IsAll); // test file
        Assert.True(selector.Select(RequestFor(runner, StandardBaseline(),
            new TestSelectionChangedFile("src/Brand/New.cs", [new ChangedLineRange(1, 1)]))).Selection.IsAll); // unreferenced
    }

    private sealed class RawExpressionSelector(string filter) : ITestSelector
    {
        public TestSelectionDecision Select(TestSelectionRequest request)
            => new(new TestSelection([filter]), "raw expression superset");
    }

    // ---- Shadow harness.

    [Fact]
    public void ShadowEvaluator_DetectsUnsafeSkips()
    {
        var record = TestSelectionShadowEvaluator.Evaluate(
            "coverage", "coverage-shadow", "main",
            wasFullSuite: false,
            selectedTests: new HashSet<string>(["A"], StringComparer.Ordinal),
            universe: ["A", "B"],
            failedTests: ["B"],
            wouldBeArgv: "dotnet test --no-build --filter x",
            selectionDetail: "detail");

        Assert.Equal(TestSelectionShadowRecord.AssessmentUnsafe, record.Assessment);
        Assert.Equal(["B"], record.UnsafeSkips);
        Assert.Equal(["B"], record.DeselectedTests);
        Assert.Equal(1, record.TotalDeselected);
    }

    [Fact]
    public void ShadowEvaluator_SafeWhenDeselectedAllPass()
    {
        var record = TestSelectionShadowEvaluator.Evaluate(
            "coverage", "coverage-shadow", "main",
            wasFullSuite: false,
            selectedTests: new HashSet<string>(["A"], StringComparer.Ordinal),
            universe: ["A", "B"],
            failedTests: [],
            wouldBeArgv: "dotnet test",
            selectionDetail: "detail");

        Assert.Equal(TestSelectionShadowRecord.AssessmentSafe, record.Assessment);
        Assert.Empty(record.UnsafeSkips);
    }

    [Fact]
    public void ShadowEvaluator_FullSuiteAndUnverifiable()
    {
        var full = TestSelectionShadowEvaluator.Evaluate(
            "coverage", "coverage-shadow", "main",
            wasFullSuite: true,
            selectedTests: new HashSet<string>(StringComparer.Ordinal),
            universe: ["A"],
            failedTests: ["A"],
            wouldBeArgv: "dotnet test",
            selectionDetail: "detail");
        Assert.Equal(TestSelectionShadowRecord.AssessmentFullSuite, full.Assessment);
        Assert.Empty(full.UnsafeSkips);

        var unknown = TestSelectionShadowEvaluator.Evaluate(
            "coverage", "coverage-shadow", "main",
            wasFullSuite: false,
            selectedTests: new HashSet<string>(["A"], StringComparer.Ordinal),
            universe: [],
            failedTests: ["A"],
            wouldBeArgv: "dotnet test",
            selectionDetail: "detail");
        Assert.Equal(TestSelectionShadowRecord.AssessmentUnverifiable, unknown.Assessment);
    }

    [Fact]
    public void ShadowEvaluator_CapsLists()
    {
        var record = TestSelectionShadowEvaluator.Evaluate(
            "coverage", "coverage-shadow", "main",
            wasFullSuite: false,
            selectedTests: new HashSet<string>(StringComparer.Ordinal),
            universe: ["A", "B", "C"],
            failedTests: ["A", "B", "C"],
            wouldBeArgv: "dotnet test",
            selectionDetail: "detail",
            listCap: 2);
        Assert.Equal(3, record.TotalDeselected);
        Assert.Equal(2, record.DeselectedTests.Count);
        Assert.Equal(2, record.UnsafeSkips.Count);
    }

    [Fact]
    public void ShadowEvaluator_ResolvesBareNames_RejectsRawExpressions()
    {
        Assert.True(TestSelectionShadowEvaluator.TryResolveSelectedTests(
            new TestSelectionDecision(new TestSelection(["A"]), "j"), ["A", "B"], out var selected));
        Assert.Equal(["A"], selected);

        Assert.False(TestSelectionShadowEvaluator.TryResolveSelectedTests(
            new TestSelectionDecision(new TestSelection(["FullyQualifiedName~A"]), "j"), ["A"], out _));

        Assert.True(TestSelectionShadowEvaluator.TryResolveSelectedTests(
            new TestSelectionDecision(TestSelection.All, "j"), ["A"], out _));
    }

    // ---- Mode parsing.

    [Theory]
    [InlineData("coverage-shadow", TestSelectionMode.CoverageShadow)]
    [InlineData("Coverage-Shadow", TestSelectionMode.CoverageShadow)]
    [InlineData("  coverage_shadow  ", TestSelectionMode.CoverageShadow)]
    [InlineData("all", TestSelectionMode.All)]
    public void ModeParser_ParsesCoverageShadow(string value, TestSelectionMode expected)
    {
        Assert.True(TestSelectionModeParser.TryParse(value, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("impacted")]
    [InlineData("")]
    public void ModeParser_StillRejectsUnknownModes(string? value)
    {
        Assert.False(TestSelectionModeParser.TryParse(value, out _));
        Assert.Throws<FormatException>(() => TestSelectionModeParser.Parse(value));
    }

    // ---- DI wiring.

    [Fact]
    public void Program_ResolvesCoverageShadowSelector_AndShadowPlumbing()
    {
        using var factory = new ShadowWiringFactory(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [$"{TestSelectionOptions.SectionName}:Mode"] = "coverage-shadow",
        });

        var selector = factory.Services.GetRequiredService<ITestSelector>();
        var runner = factory.Services.GetRequiredService<ITestRunnerAuditor>();
        var decision = selector.Select(new TestSelectionRequest(runner, "main", [], baseline: null));

        // No baseline in this host → fail-safe full suite, never a throw.
        Assert.True(decision.Selection.IsAll);
        Assert.Contains(CoverageTestSelector.SelectorName, decision.Justification);

        Assert.NotNull(factory.Services.GetRequiredService<ITestSelectionShadowSink>());
        Assert.NotNull(factory.Services.GetRequiredService<TestSelectionShadowConfig>());
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CoverageTestSelectionOptions>>();
        Assert.True(CoverageTestSelectionOptions.IsValid(options.CurrentValue));
    }

    [Fact]
    public void Program_AllMode_RemainsKillSwitch()
    {
        using var factory = new ShadowWiringFactory(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [$"{TestSelectionOptions.SectionName}:Mode"] = "all",
        });
        var selector = factory.Services.GetRequiredService<ITestSelector>();
        var runner = factory.Services.GetRequiredService<ITestRunnerAuditor>();
        var decision = selector.Select(new TestSelectionRequest(runner, "main", [], baseline: null));
        Assert.True(decision.Selection.IsAll);
        Assert.Equal(RunAllTestSelector.FullSuiteJustification, decision.Justification);
    }

    [Fact]
    public void Program_RejectsInvalidCoverageOptions_FailFast()
    {
        using var factory = new ShadowWiringFactory(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [$"{CoverageTestSelectionOptions.SectionName}:MaxBaselineAge"] = "00:00:00",
        });
        var monitor = factory.Services.GetRequiredService<IOptionsMonitor<CoverageTestSelectionOptions>>();
        Assert.Throws<OptionsValidationException>(() => _ = monitor.CurrentValue);
    }

    // ---- Advisory-only hook: the full suite always runs; nothing is skipped.

    private sealed class FakeSandbox : ISandbox
    {
        private readonly Func<SandboxExec, SandboxExecResult> _onExec;
        public List<IReadOnlyList<string>> ExecutedArgv { get; } = new();

        public FakeSandbox(Func<SandboxExec, SandboxExecResult> onExec) { _onExec = onExec; }
        public string Id => "fake";
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            ExecutedArgv.Add(exec.Argv);
            return Task.FromResult(_onExec(exec));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string BaselineJson(DateTimeOffset producedAt)
        => $$"""
            {
              "format": "codeybox-test-selection-baseline/1",
              "commit": "abc123",
              "producedAtUtc": "{{producedAt:O}}",
              "fileProject": { "src/Foo/Bar.cs": "src/Foo/Foo.csproj" },
              "projects": { "src/Foo/Foo.csproj": ["Ns.Foo.BarTests", "Ns.Foo.OtherTests"] },
              "tests": {
                "Ns.Foo.BarTests": {
                  "file": "tests/Foo.Tests/BarTests.cs",
                  "covers": { "src/Foo/Bar.cs": [10, 11, 12] }
                },
                "Ns.Foo.OtherTests": {
                  "file": "tests/Foo.Tests/OtherTests.cs",
                  "covers": { "src/Foo/Bar.cs": [50] }
                },
                "Ns.Foo.NewTests": { "file": "tests/Foo.Tests/NewTests.cs", "covers": {} },
                "Ns.Foo.UnrelatedTests": {
                  "file": "tests/Other.Tests/U.cs",
                  "covers": { "src/Other.cs": [5] }
                }
              }
            }
            """;

    private const string DiffChangingLine10 =
        "diff --git a/src/Foo/Bar.cs b/src/Foo/Bar.cs\n" +
        "+++ b/src/Foo/Bar.cs\n" +
        "@@ -0,0 +10,1 @@\n" +
        "+var x = 1;\n";

    private static DotnetTestAuditor ShadowRunner(
        InMemoryTestSelectionShadowSink sink,
        TestSelectionMode mode = TestSelectionMode.CoverageShadow)
        => new(new DotnetTestAuditorOptions
        {
            Name = "csharp:test-pass",
            BaseArgv = ["dotnet", "test", "--no-build"],
            Shadow = new TestSelectionShadowConfig
            {
                Selector = new CoverageTestSelector(
                    new ProjectGraphTestSelector(), () => new CoverageTestSelectionOptions(), TimeProvider.System),
                Sink = sink,
                ModeAccessor = () => mode,
                OptionsAccessor = () => new CoverageTestSelectionOptions(),
            },
        });

    private static FakeSandbox SandboxFor(string testOutput, DateTimeOffset? producedAt = null)
    {
        var baseline = BaselineJson(producedAt ?? DateTimeOffset.UtcNow.AddHours(-1));
        return new FakeSandbox(exec =>
        {
            var argv = exec.Argv;
            if (argv.Count > 0 && argv[0] == "git")
                return new SandboxExecResult(0, DiffChangingLine10, "");
            if (argv.Count > 0 && argv[0] == "cat")
                return new SandboxExecResult(0, baseline, "");
            return new SandboxExecResult(0, testOutput, "");
        });
    }

    private static AuditContext ContextFor()
        => new(WorkItemId.New(), "work", "main", 1, "prompt");

    [Fact]
    public async Task ShadowRun_ExecutesFullSuite_EmitsSafeRecord()
    {
        var sink = new InMemoryTestSelectionShadowSink();
        var sandbox = SandboxFor("Passed! - Failed: 0, Passed: 3");
        var auditor = ShadowRunner(sink);

        var result = await auditor.RunAsync(sandbox, "/work", ContextFor());

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
        // The executed dotnet command carries NO --filter: nothing was skipped.
        var testArgv = sandbox.ExecutedArgv.Single(a => a.Count > 0 && a[1] == "test");
        Assert.DoesNotContain("--filter", testArgv);
        Assert.Equal(["dotnet", "test", "--no-build"], testArgv);

        var record = Assert.Single(sink.Records);
        Assert.Equal(TestSelectionShadowRecord.AssessmentSafe, record.Assessment);
        Assert.Empty(record.UnsafeSkips);
        Assert.Contains("--filter", record.WouldBeArgv);
        Assert.Equal(["Ns.Foo.UnrelatedTests"], record.DeselectedTests);
    }

    [Fact]
    public async Task ShadowRun_FailingDeselectedTest_RecordsUnsafeSkip()
    {
        var sink = new InMemoryTestSelectionShadowSink();
        var sandbox = SandboxFor("Failed Ns.Foo.UnrelatedTests [1 s]\nFailed! - Failed: 1, Passed: 3");
        var auditor = ShadowRunner(sink);

        await auditor.RunAsync(sandbox, "/work", ContextFor());

        var record = Assert.Single(sink.Records);
        Assert.Equal(TestSelectionShadowRecord.AssessmentUnsafe, record.Assessment);
        Assert.Equal(["Ns.Foo.UnrelatedTests"], record.UnsafeSkips);
        // ...yet the full suite still ran (no --filter executed).
        var testArgv = sandbox.ExecutedArgv.Single(a => a.Count > 0 && a[1] == "test");
        Assert.DoesNotContain("--filter", testArgv);
    }

    [Fact]
    public async Task ShadowRun_AllMode_EmitsNoRecord()
    {
        var sink = new InMemoryTestSelectionShadowSink();
        var sandbox = SandboxFor("Passed!");
        var auditor = ShadowRunner(sink, TestSelectionMode.All);

        var result = await auditor.RunAsync(sandbox, "/work", ContextFor());

        Assert.True(result.Passed);
        Assert.Empty(sink.Records);
    }

    [Fact]
    public async Task ShadowRun_MissingBaseline_RecordsFullSuite_StillPasses()
    {
        var sink = new InMemoryTestSelectionShadowSink();
        var sandbox = new FakeSandbox(exec =>
        {
            if (exec.Argv.Count > 0 && exec.Argv[0] == "git")
                return new SandboxExecResult(0, DiffChangingLine10, "");
            if (exec.Argv.Count > 0 && exec.Argv[0] == "cat")
                return new SandboxExecResult(1, "", "not found");
            return new SandboxExecResult(0, "Passed!", "");
        });
        var auditor = ShadowRunner(sink);

        var result = await auditor.RunAsync(sandbox, "/work", ContextFor());

        Assert.True(result.Passed);
        var record = Assert.Single(sink.Records);
        Assert.Equal(TestSelectionShadowRecord.AssessmentFullSuite, record.Assessment);
    }

    [Fact]
    public async Task ShadowRun_SinkFailure_SurfacesInfoFinding_DoesNotFailGate()
    {
        var sandbox = SandboxFor("Passed!");
        var auditor = new DotnetTestAuditor(new DotnetTestAuditorOptions
        {
            Name = "csharp:test-pass",
            BaseArgv = ["dotnet", "test", "--no-build"],
            Shadow = new TestSelectionShadowConfig
            {
                Selector = new CoverageTestSelector(
                    new ProjectGraphTestSelector(), () => new CoverageTestSelectionOptions(), TimeProvider.System),
                Sink = new ThrowingSink(),
                ModeAccessor = () => TestSelectionMode.CoverageShadow,
                OptionsAccessor = () => new CoverageTestSelectionOptions(),
            },
        });

        var result = await auditor.RunAsync(sandbox, "/work", ContextFor());

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Info, finding.Severity);
        Assert.Contains("shadow record not emitted", finding.Title);
    }

    private sealed class ThrowingSink : ITestSelectionShadowSink
    {
        public void Emit(TestSelectionShadowRecord record) => throw new InvalidOperationException("sink down");
    }

    // ---- Shadow IO converter.

    [Fact]
    public void ShadowIO_GroupsConsecutiveLinesIntoRanges()
    {
        var added = CodeyBox.Audit.UnifiedDiffParser.ParseAddedLines(
            "+++ b/src/Foo/Bar.cs\n@@ -0,0 +10,2 @@\n+a\n+b\n@@ -0,0 +20,1 @@\n+c\n");
        var files = TestSelectionShadowIO.ToChangedFiles(added);

        var file = Assert.Single(files);
        Assert.Equal("src/Foo/Bar.cs", file.Path);
        Assert.Equal(2, file.ChangedRanges.Count);
        Assert.Equal(new ChangedLineRange(10, 2), file.ChangedRanges[0]);
        Assert.Equal(new ChangedLineRange(20, 1), file.ChangedRanges[1]);
    }

    [Fact]
    public async Task ShadowIO_DiffExecs_AreByteCappedFromOptions()
    {
        var seenCaps = new List<int?>();
        var sandbox = new FakeSandbox(exec =>
        {
            seenCaps.Add(exec.MaxStdoutBytes);
            return new SandboxExecResult(0, DiffChangingLine10, "");
        });
        var options = new CoverageTestSelectionOptions { MaxDiffBytes = 12345 };

        var files = await TestSelectionShadowIO.GetChangedFilesAsync(
            sandbox, "/work", "main", () => options);

        Assert.Single(files);
        Assert.Single(seenCaps);
        Assert.All(seenCaps, cap => Assert.Equal(12345, cap));
    }

    [Fact]
    public async Task ShadowIO_FallbackDiffExec_IsAlsoCapped()
    {
        var seenCaps = new List<int?>();
        var calls = 0;
        var sandbox = new FakeSandbox(exec =>
        {
            seenCaps.Add(exec.MaxStdoutBytes);
            calls++;
            // First (origin/...) attempt fails so the bare-branch fallback runs.
            return calls == 1
                ? new SandboxExecResult(1, "", "no origin")
                : new SandboxExecResult(0, DiffChangingLine10, "");
        });
        var options = new CoverageTestSelectionOptions { MaxDiffBytes = 12345 };

        var files = await TestSelectionShadowIO.GetChangedFilesAsync(
            sandbox, "/work", "main", () => options);

        Assert.Single(files);
        Assert.Equal(2, seenCaps.Count);
        Assert.All(seenCaps, cap => Assert.Equal(12345, cap));
    }

    [Fact]
    public async Task ShadowIO_OverCapDiff_FallsBackToUnknownChangeset()
    {
        // Sandbox reports an over-limit diff as unsuccessful (Success is false
        // when OutputLimitExceeded); the hook must yield no files so the
        // selectors fall back to the full suite.
        var sandbox = new FakeSandbox(exec =>
            new SandboxExecResult(0, "truncated", "", StdoutLimitExceeded: true));
        var options = new CoverageTestSelectionOptions { MaxDiffBytes = 16 };

        var files = await TestSelectionShadowIO.GetChangedFilesAsync(
            sandbox, "/work", "main", () => options);

        Assert.Empty(files);
    }

    [Fact]
    public async Task ShadowIO_ThrowingOptionsAccessor_StillCapsDiff()
    {
        var seenCaps = new List<int?>();
        var sandbox = new FakeSandbox(exec =>
        {
            seenCaps.Add(exec.MaxStdoutBytes);
            return new SandboxExecResult(0, DiffChangingLine10, "");
        });
        Func<CoverageTestSelectionOptions> throwing = () => throw new InvalidOperationException("options down");

        var files = await TestSelectionShadowIO.GetChangedFilesAsync(
            sandbox, "/work", "main", throwing);

        Assert.Single(files);
        Assert.Single(seenCaps);
        Assert.All(seenCaps, cap => Assert.Equal(
            (int)CoverageTestSelectionOptions.DefaultMaxDiffBytes, cap));
    }

    /// <summary>Minimal host booting the real composition root for DI tests.</summary>
    private sealed class ShadowWiringFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-shadow-wiring-{Guid.NewGuid():N}.db");
        private readonly IReadOnlyDictionary<string, string?> _extra;

        public ShadowWiringFactory(IReadOnlyDictionary<string, string?>? extra = null)
        {
            _extra = extra ?? new Dictionary<string, string?>();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.Sources.Clear();
                var tmp = Path.GetTempPath();
                var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"test-agent-streams-{Guid.NewGuid():N}"),
                };
                foreach (var kv in _extra)
                    settings[kv.Key] = kv.Value;
                cfg.AddInMemoryCollection(settings);
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                try { File.Delete(_dbPath); } catch { /* best-effort */ }
            base.Dispose(disposing);
        }
    }
}
