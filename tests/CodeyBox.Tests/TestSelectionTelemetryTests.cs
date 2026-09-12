using CodeyBox.Audit.Shell;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Data.Sqlite;

namespace CodeyBox.Tests;

/// <summary>
/// Per-run test-selection telemetry: the pure computer, the
/// <c>csharp:test-pass</c> runner attachment (shadow and full-suite paths),
/// and the audit-report store round-trip including the pre-telemetry
/// backward-read path.
/// </summary>
public sealed class TestSelectionTelemetryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-ts-telemetry-{Guid.NewGuid():N}.db");
    private readonly SqliteAuditReportStore _store;

    public TestSelectionTelemetryTests()
    {
        using var workItems = new SqliteWorkItemStore(_dbPath);
        _store = new SqliteAuditReportStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static TestSelectionShadowRecord ShadowRecord(
        bool wasFullSuite,
        string assessment,
        string detail,
        int totalSelected = 0,
        int totalDeselected = 0)
        => new(
            SelectorName: CoverageTestSelector.SelectorName,
            Mode: TestSelectionMode.CoverageShadow.ToString(),
            BaseRef: "main",
            WasFullSuite: wasFullSuite,
            SelectionDetail: detail,
            SelectedTests: [],
            TotalSelected: totalSelected,
            DeselectedTests: [],
            TotalDeselected: totalDeselected,
            FailedTests: [],
            TotalFailed: 0,
            UnsafeSkips: [],
            Assessment: assessment,
            WouldBeArgv: "dotnet test --no-build --filter ...");

    [Fact]
    public void FromShadowRecord_NarrowedRun_ReportsCountsFractionLayersAndNoFallbacks()
    {
        var record = ShadowRecord(
            wasFullSuite: false,
            assessment: TestSelectionShadowRecord.AssessmentSafe,
            detail: "coverage: 3 test(s)",
            totalSelected: 3,
            totalDeselected: 1);

        var telemetry = TestSelectionTelemetryComputer.FromShadowRecord(record, universeCount: 4);

        Assert.Equal(3, telemetry.SelectedCount);
        Assert.Equal(4, telemetry.TotalCount);
        Assert.Equal(0.25, telemetry.EstimatedSavedFraction, precision: 9);
        Assert.Equal(CoverageTestSelector.SelectorName, telemetry.Selector);
        Assert.Equal(
            [ProjectGraphTestSelector.SelectorName, CoverageTestSelector.SelectorName],
            telemetry.Layers);
        Assert.Equal(TestSelectionShadowRecord.AssessmentSafe, telemetry.Assessment);
        Assert.Empty(telemetry.Fallbacks);
    }

    [Fact]
    public void FromShadowRecord_FullSuiteFallback_SelectsUniverseWithZeroSavingsAndFallback()
    {
        var record = ShadowRecord(
            wasFullSuite: true,
            assessment: TestSelectionShadowRecord.AssessmentFullSuite,
            detail: "coverage: full suite (no per-test coverage baseline is available) | baseline: nope");

        var telemetry = TestSelectionTelemetryComputer.FromShadowRecord(record, universeCount: 4);

        Assert.Equal(4, telemetry.SelectedCount);
        Assert.Equal(4, telemetry.TotalCount);
        Assert.Equal(0.0, telemetry.EstimatedSavedFraction);
        Assert.Equal(TestSelectionShadowRecord.AssessmentFullSuite, telemetry.Assessment);
        var fallback = Assert.Single(telemetry.Fallbacks);
        Assert.Contains("no per-test coverage baseline", fallback);
    }

    [Fact]
    public void FromShadowRecord_UnknownUniverse_ReportsZeroCounts()
    {
        var record = ShadowRecord(
            wasFullSuite: false,
            assessment: TestSelectionShadowRecord.AssessmentUnverifiable,
            detail: "coverage: 2 test(s) | baseline: commit 'x' with 0 recorded test(s)",
            totalSelected: 2);

        var telemetry = TestSelectionTelemetryComputer.FromShadowRecord(record, universeCount: 0);

        Assert.Equal(0, telemetry.SelectedCount);
        Assert.Equal(0, telemetry.TotalCount);
        Assert.Equal(0.0, telemetry.EstimatedSavedFraction);
        Assert.Equal(TestSelectionShadowRecord.AssessmentUnverifiable, telemetry.Assessment);
        Assert.NotEmpty(telemetry.Fallbacks);
    }

    [Fact]
    public void FullSuiteWithoutShadow_ReportsKillSwitchState()
    {
        var telemetry = TestSelectionTelemetryComputer.FullSuiteWithoutShadow(
            TestSelectionMode.All.ToString());

        Assert.Equal(TestSelectionMode.All.ToString(), telemetry.Mode);
        Assert.Equal("none", telemetry.Selector);
        Assert.Empty(telemetry.Layers);
        Assert.Equal(0, telemetry.SelectedCount);
        Assert.Equal(0, telemetry.TotalCount);
        Assert.Equal(0.0, telemetry.EstimatedSavedFraction);
        Assert.Equal(TestSelectionShadowRecord.AssessmentFullSuite, telemetry.Assessment);
        Assert.NotEmpty(telemetry.Fallbacks);
    }

    [Theory]
    [InlineData("coverage", "project-graph,coverage")]
    [InlineData("project-graph", "project-graph")]
    [InlineData("none", "")]
    [InlineData("", "")]
    public void LayersForSelector_MapsSelectorToLayers(string selector, string expectedCsv)
    {
        var expected = string.IsNullOrEmpty(expectedCsv)
            ? []
            : expectedCsv.Split(',');
        Assert.Equal(expected, TestSelectionTelemetryComputer.LayersForSelector(selector));
    }

    [Fact]
    public void FromShadowRecord_TruncatesLongDetail()
    {
        var record = ShadowRecord(
            wasFullSuite: true,
            assessment: TestSelectionShadowRecord.AssessmentFullSuite,
            detail: new string('x', TestSelectionTelemetryComputer.MaxDetailChars + 100));

        var telemetry = TestSelectionTelemetryComputer.FromShadowRecord(record, universeCount: 1);

        Assert.Equal(TestSelectionTelemetryComputer.MaxDetailChars + 1, telemetry.Detail.Length);
    }

    [Fact]
    public async Task ShadowRunner_AttachesNarrowedTelemetry()
    {
        var sink = new InMemoryTestSelectionShadowSink();
        var sandbox = TelemetrySandbox("Passed! - Failed: 0, Passed: 4");
        var auditor = TelemetryRunner(sink, TestSelectionMode.CoverageShadow);

        var result = await auditor.RunAsync(sandbox, "/work", TelemetryContext());

        Assert.True(result.Passed);
        Assert.NotNull(result.TestSelection);
        var telemetry = result.TestSelection;
        Assert.Equal(TestSelectionMode.CoverageShadow.ToString(), telemetry.Mode);
        Assert.Equal(CoverageTestSelector.SelectorName, telemetry.Selector);
        Assert.Equal(4, telemetry.TotalCount);
        Assert.Equal(3, telemetry.SelectedCount);
        Assert.Equal(0.25, telemetry.EstimatedSavedFraction, precision: 9);
        Assert.Equal(TestSelectionShadowRecord.AssessmentSafe, telemetry.Assessment);
        Assert.Empty(telemetry.Fallbacks);
    }

    [Fact]
    public async Task ShadowRunner_UnsafeSkip_SurfacesInTelemetryAssessment()
    {
        var sink = new InMemoryTestSelectionShadowSink();
        var sandbox = TelemetrySandbox("Failed Ns.Foo.UnrelatedTests [1 s]\nFailed! - Failed: 1, Passed: 3");
        var auditor = TelemetryRunner(sink, TestSelectionMode.CoverageShadow);

        var result = await auditor.RunAsync(sandbox, "/work", TelemetryContext());

        Assert.NotNull(result.TestSelection);
        var telemetry = result.TestSelection;
        Assert.Equal(TestSelectionShadowRecord.AssessmentUnsafe, telemetry.Assessment);
        Assert.Equal(3, telemetry.SelectedCount);
        Assert.Equal(4, telemetry.TotalCount);
    }

    [Fact]
    public async Task AllModeRunner_AttachesFullSuiteTelemetry()
    {
        var sink = new InMemoryTestSelectionShadowSink();
        var sandbox = TelemetrySandbox("Passed!");
        var auditor = TelemetryRunner(sink, TestSelectionMode.All);

        var result = await auditor.RunAsync(sandbox, "/work", TelemetryContext());

        Assert.True(result.Passed);
        Assert.Empty(sink.Records);
        Assert.NotNull(result.TestSelection);
        var telemetry = result.TestSelection;
        Assert.Equal(TestSelectionMode.All.ToString(), telemetry.Mode);
        Assert.Equal("none", telemetry.Selector);
        Assert.Equal(0, telemetry.SelectedCount);
        Assert.Equal(0, telemetry.TotalCount);
        Assert.Equal(TestSelectionShadowRecord.AssessmentFullSuite, telemetry.Assessment);
    }

    [Fact]
    public async Task Store_RoundTripsTelemetry()
    {
        var telemetry = TestSelectionTelemetryComputer.FullSuiteWithoutShadow(
            TestSelectionMode.CoverageShadow.ToString());
        var report = TelemetryReport("wi-ts-roundtrip", telemetry);
        await CreateAsync(report);

        var got = Assert.Single(await _store.GetByWorkItemAsync("wi-ts-roundtrip"));
        Assert.NotNull(got.TestSelection);
        var roundTripped = got.TestSelection;
        Assert.Equal(telemetry.Mode, roundTripped.Mode);
        Assert.Equal(telemetry.Selector, roundTripped.Selector);
        Assert.Equal(telemetry.SelectedCount, roundTripped.SelectedCount);
        Assert.Equal(telemetry.TotalCount, roundTripped.TotalCount);
        Assert.Equal(telemetry.EstimatedSavedFraction, roundTripped.EstimatedSavedFraction);
        Assert.Equal(telemetry.Assessment, roundTripped.Assessment);
        Assert.Equal(telemetry.Fallbacks, roundTripped.Fallbacks);
        Assert.Equal(telemetry.Detail, roundTripped.Detail);
    }

    [Fact]
    public async Task Store_PreTelemetryRow_ReadsBackNull()
    {
        var report = TelemetryReport("wi-ts-legacy", testSelection: null);
        await CreateAsync(report);

        var got = Assert.Single(await _store.GetByWorkItemAsync("wi-ts-legacy"));
        Assert.Null(got.TestSelection);
    }

    private static AuditReport TelemetryReport(string workItemId, TestSelectionTelemetry? testSelection) => new()
    {
        Id = Guid.NewGuid().ToString(),
        WorkItemId = workItemId,
        Iteration = 1,
        Target = AuditTarget.Code,
        AuditorName = "csharp:test-pass",
        AuditorKind = "shell",
        WorstSeverity = "none",
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        EndedAt = DateTimeOffset.UtcNow,
        DurationMs = 120_000,
        Findings = [],
        RawOutput = null,
        TestSelection = testSelection,
    };

    private async Task CreateAsync(AuditReport report)
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO work_items
                (id, project_id, title, prompt, work_timeout_ticks, merge_timeout_ticks,
                 push_upstream, state, created_at, updated_at)
            VALUES
                ($id, 'test-project', 'test', 'test', $workTimeout, $mergeTimeout,
                 1, 0, $now, $now);
            """;
        cmd.Parameters.AddWithValue("$id", report.WorkItemId);
        cmd.Parameters.AddWithValue("$workTimeout", TimeSpan.FromMinutes(30).Ticks);
        cmd.Parameters.AddWithValue("$mergeTimeout", TimeSpan.FromMinutes(15).Ticks);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
        await _store.CreateAsync(report);
    }

    private static DotnetTestAuditor TelemetryRunner(
        InMemoryTestSelectionShadowSink sink, TestSelectionMode mode)
        => new(new DotnetTestAuditorOptions
        {
            Name = "csharp:test-pass",
            BaseArgv = ["dotnet", "test", "--no-build"],
            Shadow = new TestSelectionShadowConfig
            {
                Selector = new CoverageTestSelector(
                    new ProjectGraphTestSelector(),
                    () => new CoverageTestSelectionOptions(),
                    TimeProvider.System),
                Sink = sink,
                ModeAccessor = () => mode,
                OptionsAccessor = () => new CoverageTestSelectionOptions(),
            },
        });

    private static TelemetryFakeSandbox TelemetrySandbox(string testOutput)
    {
        var baseline = $$"""
            {
              "format": "codeybox-test-selection-baseline/1",
              "commit": "abc123",
              "producedAtUtc": "{{DateTimeOffset.UtcNow.AddHours(-1):O}}",
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
        const string diff =
            "diff --git a/src/Foo/Bar.cs b/src/Foo/Bar.cs\n" +
            "+++ b/src/Foo/Bar.cs\n" +
            "@@ -0,0 +10,1 @@\n" +
            "+var x = 1;\n";
        return new TelemetryFakeSandbox(exec =>
        {
            if (exec.Argv.Count > 0 && exec.Argv[0] == "git")
                return new SandboxExecResult(0, diff, "");
            if (exec.Argv.Count > 0 && exec.Argv[0] == "cat")
                return new SandboxExecResult(0, baseline, "");
            return new SandboxExecResult(0, testOutput, "");
        });
    }

    private static AuditContext TelemetryContext()
        => new(WorkItemId.New(), "work", "main", 1, "prompt");

    private sealed class TelemetryFakeSandbox(Func<SandboxExec, SandboxExecResult> onExec) : ISandbox
    {
        private readonly Func<SandboxExec, SandboxExecResult> _onExec = onExec;
        public string Id => "fake";
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => Task.FromResult(_onExec(exec));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
