using CodeyBox.Audit;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class CoverageAuditorTests
{
    /// <summary>
    /// Routes sandbox.ExecAsync to canned responses keyed on the command shape,
    /// so the auditor can be driven end-to-end (git diff → marker probe → coverage
    /// run → find report → read report) without a real repository or dotnet.
    /// </summary>
    private sealed class StubSandbox : ISandbox
    {
        public string Id => "stub-coverage";

        public string DiffStdout { get; init; } = "";
        public int DiffExitCode { get; init; }
        public bool HasMarker { get; init; } = true;
        public int TestExitCode { get; init; }
        public string TestOutput { get; init; } = "Passed!";
        public IReadOnlyList<string> ReportFiles { get; init; } = ["/tmp/cov/coverage.cobertura.xml"];
        public string ReportXml { get; init; } = "";

        public bool TestRan { get; private set; }
        public List<IReadOnlyList<string>> Calls { get; } = new();

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Calls.Add(exec.Argv);
            var argv = exec.Argv;
            var head = argv.Count > 0 ? argv[0] : "";

            if (head == "git")
                return Result(DiffExitCode, DiffStdout);

            if (head == "dotnet")
            {
                TestRan = true;
                return Result(TestExitCode, TestOutput);
            }

            if (head == "cat")
                return Result(0, ReportXml);

            if (head == "sh")
            {
                var script = argv.Count > 2 ? argv[2] : "";
                if (script.Contains("csproj", StringComparison.Ordinal))
                    return Result(0, HasMarker ? "./App.csproj\n" : "");
                if (script.Contains("coverage.cobertura.xml", StringComparison.Ordinal))
                    return Result(0, string.Join('\n', ReportFiles) + (ReportFiles.Count > 0 ? "\n" : ""));
                // rm -rf / mkdir results dir, or anything else → succeed.
                return Result(0, "");
            }

            return Result(0, "");
        }

        private static Task<SandboxExecResult> Result(int exit, string stdout)
            => Task.FromResult(new SandboxExecResult(exit, stdout, ""));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static AuditContext Ctx() =>
        new(WorkItemId.New(), WorkBranch: "feature/x", BaseBranch: "main",
            Iteration: 1, OriginalPrompt: "do x");

    // Diff that changes src/Foo.cs lines 10 (covered) and 11 (uncovered).
    private const string ChangedFoo =
        "+++ b/src/Foo.cs\n" +
        "@@ -9,0 +10,2 @@\n" +
        "+var covered = 1;\n" +
        "+var uncovered = 2;\n";

    private const string CoverageFoo =
        """
        <coverage>
          <sources><source>/work</source></sources>
          <packages><package><classes>
            <class filename="src/Foo.cs">
              <lines>
                <line number="10" hits="2" />
                <line number="11" hits="0" />
              </lines>
            </class>
          </classes></package></packages>
        </coverage>
        """;

    private static CoverageAuditorOptions Options(string? mode = null, params CoverageExclusion[] exclusions)
        => new() { Mode = mode, Exclusions = exclusions };

    [Fact]
    public async Task ReportOnly_UncoveredChangedLine_InfoFindingAndPasses()
    {
        var sandbox = new StubSandbox { DiffStdout = ChangedFoo, ReportXml = CoverageFoo };
        var auditor = new CoverageAuditor(Options(mode: "report-only"));

        var result = await auditor.RunAsync(sandbox, "/work", Ctx());

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Info, finding.Severity);
        Assert.Equal("src/Foo.cs:11", finding.Location);
        Assert.True(sandbox.TestRan);
    }

    [Fact]
    public async Task Blocking_UncoveredChangedLine_ErrorFindingAndFails()
    {
        var sandbox = new StubSandbox { DiffStdout = ChangedFoo, ReportXml = CoverageFoo };
        var auditor = new CoverageAuditor(Options(mode: "blocking"));

        var result = await auditor.RunAsync(sandbox, "/work", Ctx());

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Equal("src/Foo.cs:11", finding.Location);
    }

    [Fact]
    public async Task DefaultMode_IsReportOnly()
    {
        var sandbox = new StubSandbox { DiffStdout = ChangedFoo, ReportXml = CoverageFoo };
        var auditor = new CoverageAuditor(Options(mode: null));

        var result = await auditor.RunAsync(sandbox, "/work", Ctx());

        Assert.True(result.Passed);
        Assert.Equal(AuditSeverity.Info, Assert.Single(result.Findings).Severity);
    }

    [Fact]
    public async Task Blocking_AllChangedLinesCovered_Passes()
    {
        // Only line 10 changed, which is covered.
        var diff =
            "+++ b/src/Foo.cs\n" +
            "@@ -9,0 +10 @@\n" +
            "+var covered = 1;\n";
        var sandbox = new StubSandbox { DiffStdout = diff, ReportXml = CoverageFoo };
        var auditor = new CoverageAuditor(Options(mode: "blocking"));

        var result = await auditor.RunAsync(sandbox, "/work", Ctx());

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Blocking_ExcludedWithJustification_Passes()
    {
        var sandbox = new StubSandbox { DiffStdout = ChangedFoo, ReportXml = CoverageFoo };
        var auditor = new CoverageAuditor(Options(
            mode: "blocking",
            new CoverageExclusion { File = "src/Foo.cs", Line = 11, Justification = "generated" }));

        var result = await auditor.RunAsync(sandbox, "/work", Ctx());

        Assert.True(result.Passed);
        Assert.DoesNotContain(result.Findings, f => f.Severity == AuditSeverity.Error);
        var applied = Assert.Single(result.Findings);
        Assert.Equal("coverage exclusion applied", applied.Title);
        Assert.Equal("src/Foo.cs:11", applied.Location);
    }

    [Fact]
    public async Task Blocking_ExclusionMissingJustification_StillGatesAndWarns()
    {
        var sandbox = new StubSandbox { DiffStdout = ChangedFoo, ReportXml = CoverageFoo };
        var auditor = new CoverageAuditor(Options(
            mode: "blocking",
            new CoverageExclusion { File = "src/Foo.cs", Line = 11, Justification = "" }));

        var result = await auditor.RunAsync(sandbox, "/work", Ctx());

        Assert.False(result.Passed);
        Assert.Contains(result.Findings, f => f.Severity == AuditSeverity.Error && f.Location == "src/Foo.cs:11");
        Assert.Contains(result.Findings, f =>
            f.Severity == AuditSeverity.Warning &&
            f.Title.Contains("missing justification", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NoChangedLines_PassesWithoutRunningTests()
    {
        var sandbox = new StubSandbox { DiffStdout = "" };
        var auditor = new CoverageAuditor(Options(mode: "blocking"));

        var result = await auditor.RunAsync(sandbox, "/work", Ctx());

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
        Assert.False(sandbox.TestRan);
    }

    [Fact]
    public async Task NoDotnetMarker_PassesWithoutRunningTests()
    {
        var sandbox = new StubSandbox { DiffStdout = ChangedFoo, HasMarker = false };
        var auditor = new CoverageAuditor(Options(mode: "blocking"));

        var result = await auditor.RunAsync(sandbox, "/work", Ctx());

        Assert.True(result.Passed);
        Assert.False(sandbox.TestRan);
    }

    [Fact]
    public async Task NoCoverageReport_IsUnverifiable_BlockingFails_ReportOnlyWarns()
    {
        var blocking = await new CoverageAuditor(Options(mode: "blocking"))
            .RunAsync(new StubSandbox { DiffStdout = ChangedFoo, ReportFiles = [] }, "/work", Ctx());
        Assert.False(blocking.Passed);
        Assert.Equal(AuditSeverity.Error, Assert.Single(blocking.Findings).Severity);

        var reportOnly = await new CoverageAuditor(Options(mode: "report-only"))
            .RunAsync(new StubSandbox { DiffStdout = ChangedFoo, ReportFiles = [] }, "/work", Ctx());
        Assert.True(reportOnly.Passed);
        Assert.Equal(AuditSeverity.Warning, Assert.Single(reportOnly.Findings).Severity);
    }

    [Fact]
    public async Task CoverageTestRunFails_IsUnverifiable()
    {
        var sandbox = new StubSandbox { DiffStdout = ChangedFoo, TestExitCode = 1, TestOutput = "boom" };
        var auditor = new CoverageAuditor(Options(mode: "blocking"));

        var result = await auditor.RunAsync(sandbox, "/work", Ctx());

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Contains("coverage test run failed", finding.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GitDiffFails_IsUnverifiable()
    {
        var sandbox = new StubSandbox { DiffExitCode = 128 };
        var auditor = new CoverageAuditor(Options(mode: "blocking"));

        var result = await auditor.RunAsync(sandbox, "/work", Ctx());

        Assert.False(result.Passed);
        Assert.Contains("enumerate changed lines", Assert.Single(result.Findings).Title, StringComparison.OrdinalIgnoreCase);
        Assert.False(sandbox.TestRan);
    }

    [Fact]
    public void Capabilities_AreToolNoneWithStableName()
    {
        var auditor = new CoverageAuditor(Options());
        Assert.Equal(AuditCapabilities.None, auditor.Required);
        Assert.Equal("tool", auditor.Kind);
        Assert.Equal("tests:coverage", auditor.Name);
    }

    [Fact]
    public void CoverageModeParser_ParsesRolloutValues()
    {
        Assert.Equal(CoverageMode.ReportOnly, CoverageModeParser.Parse(null));
        Assert.Equal(CoverageMode.ReportOnly, CoverageModeParser.Parse("report-only"));
        Assert.Equal(CoverageMode.ReportOnly, CoverageModeParser.Parse("nonsense"));
        Assert.Equal(CoverageMode.Blocking, CoverageModeParser.Parse("blocking"));
        Assert.Equal(CoverageMode.Blocking, CoverageModeParser.Parse("Blocking"));
    }
}
