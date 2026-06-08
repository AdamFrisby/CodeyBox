using CodeyBox.Audit;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class MutationTestingAuditorTests
{
    /// <summary>
    /// Routes sandbox.ExecAsync to per-argv handlers so the test can stub
    /// `git diff --name-only` deterministically without hitting an actual
    /// repository.
    /// </summary>
    private sealed class StubSandbox : ISandbox
    {
        public string Id => "stub-mutation";
        public string DiffNameOnly { get; init; } = "";
        public int DiffExitCode { get; init; }
        public List<IReadOnlyList<string>> Calls { get; } = new();

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Calls.Add(exec.Argv);
            return Task.FromResult(new SandboxExecResult(DiffExitCode, DiffNameOnly, ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeMutationRunner : IMutationRunner
    {
        public MutationRunReport NextReport { get; set; } =
            new(100.0, 100.0, [], TimeSpan.Zero);
        public int Calls { get; private set; }
        public IReadOnlyList<string>? LastScopedFiles { get; private set; }
        public Exception? Throw { get; set; }

        public Task<MutationRunReport> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            IReadOnlyList<string> changedFiles,
            TimeSpan budget,
            CancellationToken ct = default)
        {
            Calls++;
            LastScopedFiles = changedFiles;
            if (Throw is not null) throw Throw;
            return Task.FromResult(NextReport);
        }
    }

    private static AuditContext Ctx() =>
        new(WorkItemId.New(), WorkBranch: "feature/x", BaseBranch: "main",
            Iteration: 1, OriginalPrompt: "do x");

    [Fact]
    public async Task Disabled_AuditorIsInert_NoRunnerCall()
    {
        var runner = new FakeMutationRunner();
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = false },
            runner,
            new InMemoryMutationRatchetStore());

        var result = await auditor.RunAsync(new StubSandbox(), "/work", Ctx());

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task NullRunnerWired_EmitsWarning_DoesNotBlockMerge()
    {
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true },
            new NullMutationRunner(),
            new InMemoryMutationRatchetStore());

        var result = await auditor.RunAsync(new StubSandbox(), "/work", Ctx());

        Assert.True(result.Passed);
        Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, result.Findings[0].Severity);
        Assert.Contains("engine not wired", result.Findings[0].Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChangedCodeAllKilled_Passes_RatchetUpdated()
    {
        var sandbox = new StubSandbox { DiffNameOnly = "src/Foo.cs\nsrc/Bar.cs\n" };
        var runner = new FakeMutationRunner
        {
            NextReport = new MutationRunReport(
                ChangedCodeMutationScorePercent: 100.0,
                OverallMutationScorePercent: 92.0,
                SurvivingMutantsInChangedCode: [],
                Duration: TimeSpan.FromSeconds(30)),
        };
        var ratchet = new InMemoryMutationRatchetStore();
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true, ChangedCodeThresholdPercent = 80 },
            runner,
            ratchet);

        var result = await auditor.RunAsync(sandbox, "/work", Ctx());

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
        Assert.Equal(1, runner.Calls);
        Assert.Equal(new[] { "src/Foo.cs", "src/Bar.cs" }, runner.LastScopedFiles!.ToArray());

        // Ratchet was raised to the current overall score on green.
        var stored = await ratchet.TryGetAsync("main");
        Assert.Equal(92.0, stored);
    }

    /// <summary>
    /// Kill-the-mutant gate: a surviving mutant in changed code is the
    /// un-gameable conformance condition. A no-assert test would let the
    /// mutant survive and this test fixes that survivor as an Error finding.
    /// </summary>
    [Fact]
    public async Task SurvivingMutantInChangedCode_IsFlagged_AsError()
    {
        var sandbox = new StubSandbox { DiffNameOnly = "src/Foo.cs\n" };
        var survivor = new SurvivingMutant("src/Foo.cs", 42, "ConditionalBoundary",
            "Replaced '<' with '<=' on line 42");
        var runner = new FakeMutationRunner
        {
            NextReport = new MutationRunReport(
                ChangedCodeMutationScorePercent: 50.0,
                OverallMutationScorePercent: 80.0,
                SurvivingMutantsInChangedCode: [survivor],
                Duration: TimeSpan.FromSeconds(30)),
        };
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true, ChangedCodeThresholdPercent = 80 },
            runner,
            new InMemoryMutationRatchetStore());

        var result = await auditor.RunAsync(sandbox, "/work", Ctx());

        Assert.False(result.Passed);
        var mutantFinding = Assert.Single(result.Findings, f => f.Title.Contains("surviving mutant"));
        Assert.Equal(AuditSeverity.Error, mutantFinding.Severity);
        Assert.Equal("src/Foo.cs:42", mutantFinding.Location);
        Assert.Contains("ConditionalBoundary", mutantFinding.Title);
    }

    /// <summary>
    /// Companion to the kill-the-mutant test: the same changed code with a
    /// proper assertion kills the mutant, so the auditor reports passed.
    /// This is the survives-mutant-flagged vs killed-passes pair the
    /// acceptance criterion calls for.
    /// </summary>
    [Fact]
    public async Task SameCodeWithRealAssertion_KillsMutant_Passes()
    {
        var sandbox = new StubSandbox { DiffNameOnly = "src/Foo.cs\n" };
        var runner = new FakeMutationRunner
        {
            NextReport = new MutationRunReport(
                ChangedCodeMutationScorePercent: 100.0,
                OverallMutationScorePercent: 95.0,
                SurvivingMutantsInChangedCode: [],
                Duration: TimeSpan.FromSeconds(30)),
        };
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true, ChangedCodeThresholdPercent = 80 },
            runner,
            new InMemoryMutationRatchetStore());

        var result = await auditor.RunAsync(sandbox, "/work", Ctx());

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ChangedCodeBelowThreshold_NoSurvivorsListed_StillFails()
    {
        // The runner only listed an empty survivor list (perhaps engine
        // limitation), but the aggregate score is below threshold.
        var sandbox = new StubSandbox { DiffNameOnly = "src/Foo.cs\n" };
        var runner = new FakeMutationRunner
        {
            NextReport = new MutationRunReport(70.0, 90.0, [], TimeSpan.Zero),
        };
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true, ChangedCodeThresholdPercent = 80 },
            runner,
            new InMemoryMutationRatchetStore());

        var result = await auditor.RunAsync(sandbox, "/work", Ctx());

        Assert.False(result.Passed);
        var thresholdFinding = Assert.Single(result.Findings, f =>
            f.Title.Contains("below threshold"));
        Assert.Equal(AuditSeverity.Error, thresholdFinding.Severity);
        Assert.Contains("70.0", thresholdFinding.Description);
        Assert.Contains("80.0", thresholdFinding.Description);
    }

    [Fact]
    public async Task OverallScoreRegression_BeyondTolerance_IsFlagged()
    {
        var sandbox = new StubSandbox { DiffNameOnly = "src/Foo.cs\n" };
        var ratchet = new InMemoryMutationRatchetStore();
        await ratchet.SaveAsync("main", 90.0);

        var runner = new FakeMutationRunner
        {
            NextReport = new MutationRunReport(100.0, 80.0, [], TimeSpan.Zero),
        };
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions
            {
                Enabled = true,
                ChangedCodeThresholdPercent = 80,
                RatchetTolerancePercent = 0.5,
            },
            runner,
            ratchet);

        var result = await auditor.RunAsync(sandbox, "/work", Ctx());

        Assert.False(result.Passed);
        var regression = Assert.Single(result.Findings, f => f.Title.Contains("regressed"));
        Assert.Equal(AuditSeverity.Error, regression.Severity);
        Assert.Contains("90.0", regression.Description);
        Assert.Contains("80.0", regression.Description);
    }

    [Fact]
    public async Task OverallScoreWithinTolerance_DoesNotRegress()
    {
        var sandbox = new StubSandbox { DiffNameOnly = "src/Foo.cs\n" };
        var ratchet = new InMemoryMutationRatchetStore();
        await ratchet.SaveAsync("main", 90.0);

        var runner = new FakeMutationRunner
        {
            NextReport = new MutationRunReport(100.0, 89.7, [], TimeSpan.Zero),
        };
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions
            {
                Enabled = true,
                ChangedCodeThresholdPercent = 80,
                RatchetTolerancePercent = 0.5,
            },
            runner,
            ratchet);

        var result = await auditor.RunAsync(sandbox, "/work", Ctx());

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task FailingRun_DoesNotLowerRatchet()
    {
        // Acceptance: "overall score can't regress" — and a failing run that
        // happens to compute a high overall score must not silently raise
        // (or lower) the baseline either, because the rework loop may fix
        // the survivors before the next iteration. Only green runs persist.
        var sandbox = new StubSandbox { DiffNameOnly = "src/Foo.cs\n" };
        var ratchet = new InMemoryMutationRatchetStore();
        await ratchet.SaveAsync("main", 90.0);

        var survivor = new SurvivingMutant("src/Foo.cs", 5, "ArithmeticOperator", "+ → -");
        var runner = new FakeMutationRunner
        {
            NextReport = new MutationRunReport(50.0, 70.0, [survivor], TimeSpan.Zero),
        };
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true, ChangedCodeThresholdPercent = 80 },
            runner,
            ratchet);

        var result = await auditor.RunAsync(sandbox, "/work", Ctx());

        Assert.False(result.Passed);
        // Baseline must remain unchanged after a failing audit.
        Assert.Equal(90.0, await ratchet.TryGetAsync("main"));
    }

    [Fact]
    public async Task NoBaseline_FirstRunRecordsCurrent_NoRegressionFinding()
    {
        var sandbox = new StubSandbox { DiffNameOnly = "src/Foo.cs\n" };
        var ratchet = new InMemoryMutationRatchetStore();
        var runner = new FakeMutationRunner
        {
            NextReport = new MutationRunReport(100.0, 70.0, [], TimeSpan.Zero),
        };
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true, ChangedCodeThresholdPercent = 80 },
            runner,
            ratchet);

        var result = await auditor.RunAsync(sandbox, "/work", Ctx());

        Assert.True(result.Passed);
        Assert.Equal(70.0, await ratchet.TryGetAsync("main"));
    }

    [Fact]
    public async Task DiffFilteredByExtensions_OnlyMatchedFilesScoped()
    {
        var sandbox = new StubSandbox
        {
            DiffNameOnly = "src/Foo.cs\nsrc/Bar.py\nREADME.md\nsrc/Baz.Cs\n",
        };
        var runner = new FakeMutationRunner();
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true, FileExtensions = [".cs"] },
            runner,
            new InMemoryMutationRatchetStore());

        var result = await auditor.RunAsync(sandbox, "/work", Ctx());

        Assert.True(result.Passed);
        Assert.Equal(new[] { "src/Foo.cs", "src/Baz.Cs" }, runner.LastScopedFiles!.ToArray());
    }

    [Fact]
    public async Task ExcludedPathPrefixes_DropTestFiles()
    {
        var sandbox = new StubSandbox
        {
            DiffNameOnly = "src/Foo.cs\ntests/FooTests.cs\nsrc/Bar.cs\n",
        };
        var runner = new FakeMutationRunner();
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions
            {
                Enabled = true,
                FileExtensions = [".cs"],
                ExcludePathPrefixes = ["tests/"],
            },
            runner,
            new InMemoryMutationRatchetStore());

        await auditor.RunAsync(sandbox, "/work", Ctx());

        Assert.Equal(new[] { "src/Foo.cs", "src/Bar.cs" }, runner.LastScopedFiles!.ToArray());
    }

    [Fact]
    public async Task NoMatchingChangedFiles_SkipsRunner()
    {
        var sandbox = new StubSandbox { DiffNameOnly = "README.md\nsrc/Bar.py\n" };
        var runner = new FakeMutationRunner();
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true, FileExtensions = [".cs"] },
            runner,
            new InMemoryMutationRatchetStore());

        var result = await auditor.RunAsync(sandbox, "/work", Ctx());

        Assert.True(result.Passed);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task RunnerThrows_AuditFailsWithErrorFinding()
    {
        var sandbox = new StubSandbox { DiffNameOnly = "src/Foo.cs\n" };
        var runner = new FakeMutationRunner { Throw = new InvalidOperationException("engine OOM") };
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true },
            runner,
            new InMemoryMutationRatchetStore());

        var result = await auditor.RunAsync(sandbox, "/work", Ctx());

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("engine OOM", finding.Description);
    }

    [Fact]
    public void Capabilities_AreNone()
    {
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions(),
            new NullMutationRunner(),
            new InMemoryMutationRatchetStore());
        Assert.Equal(AuditCapabilities.None, auditor.Required);
        Assert.Equal("tool", auditor.Kind);
    }
}
