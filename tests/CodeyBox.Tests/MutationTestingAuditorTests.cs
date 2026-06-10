using CodeyBox.Audit;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class MutationTestingAuditorTests
{
    /// <summary>
    /// Routes sandbox.ExecAsync to per-argv handlers so the test can stub
    /// `git diff --name-only` deterministically without hitting an actual
    /// repository. Per-argv dispatch lets tests model the
    /// origin/&lt;base&gt;...HEAD fast path and the &lt;base&gt;...HEAD
    /// fallback independently — needed so the auditor's fail-closed path
    /// (both diffs non-zero) is exercisable.
    /// </summary>
    private sealed class StubSandbox : ISandbox
    {
        public string Id => "stub-mutation";
        public string OriginDiffStdout { get; init; } = "";
        public int OriginDiffExitCode { get; init; }
        public string FallbackDiffStdout { get; init; } = "";
        public int FallbackDiffExitCode { get; init; }
        public string Stderr { get; init; } = "";
        public List<IReadOnlyList<string>> Calls { get; } = new();

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Calls.Add(exec.Argv);
            // The auditor's argv shape ends with the revspec. We look for the
            // "origin/" prefix on the final positional argument to dispatch.
            var revspec = exec.Argv.LastOrDefault() ?? "";
            if (revspec.StartsWith("origin/", StringComparison.Ordinal))
                return Task.FromResult(new SandboxExecResult(OriginDiffExitCode, OriginDiffStdout, Stderr));
            return Task.FromResult(new SandboxExecResult(FallbackDiffExitCode, FallbackDiffStdout, Stderr));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Convenience wrapper that emits the same NUL-delimited stdout for both
    /// the origin/&lt;base&gt; and &lt;base&gt; argv variants — matches the
    /// `git diff --name-only -z` shape the auditor parses.
    /// </summary>
    private static StubSandbox SandboxWithFiles(params string[] files)
    {
        var stdout = string.Join('\0', files) + (files.Length > 0 ? "\0" : "");
        return new StubSandbox
        {
            OriginDiffStdout = stdout,
            FallbackDiffStdout = stdout,
        };
    }

    private sealed class FakeMutationRunner : IMutationRunner
    {
        public MutationRunReport NextReport { get; set; } =
            new(100.0, 100.0, [], TimeSpan.Zero);
        public int Calls { get; private set; }
        public IReadOnlyList<string>? LastScopedFiles { get; private set; }
        public TimeSpan LastBudget { get; private set; }
        public Exception? Throw { get; set; }
        public Func<CancellationToken, Task>? OnRun { get; set; }

        public async Task<MutationRunReport> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            IReadOnlyList<string> changedFiles,
            TimeSpan budget,
            CancellationToken ct = default)
        {
            Calls++;
            LastScopedFiles = changedFiles;
            LastBudget = budget;
            if (OnRun is not null) await OnRun(ct);
            if (Throw is not null) throw Throw;
            return NextReport;
        }
    }

    private static AuditContext Ctx(string? projectId = "alpha") =>
        new(WorkItemId.New(), WorkBranch: "feature/x", BaseBranch: "main",
            Iteration: 1, OriginalPrompt: "do x", ProjectId: projectId);

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
        var sandbox = SandboxWithFiles("src/Foo.cs", "src/Bar.cs");
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

        // Ratchet was raised to the current overall score on green, under the
        // project-prefixed default key.
        var stored = await ratchet.TryGetAsync("alpha:main");
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
        var sandbox = SandboxWithFiles("src/Foo.cs");
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
    /// Isolates the mutant-emission branch from the threshold-emission branch:
    /// one surviving mutant with an above-threshold score must still fail the
    /// audit and produce a single mutant finding (no threshold finding).
    /// </summary>
    [Fact]
    public async Task SurvivingMutant_AtHighScore_OnlyMutantFindingEmitted()
    {
        var sandbox = SandboxWithFiles("src/Foo.cs");
        var survivor = new SurvivingMutant("src/Foo.cs", 7, "NegateConditional", "!x → x");
        var runner = new FakeMutationRunner
        {
            NextReport = new MutationRunReport(95.0, 95.0, [survivor], TimeSpan.Zero),
        };
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true, ChangedCodeThresholdPercent = 80 },
            runner,
            new InMemoryMutationRatchetStore());

        var result = await auditor.RunAsync(sandbox, "/work", Ctx());

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("surviving mutant", finding.Title);
        Assert.DoesNotContain(result.Findings, f => f.Title.Contains("below threshold"));
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
        var sandbox = SandboxWithFiles("src/Foo.cs");
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
        var sandbox = SandboxWithFiles("src/Foo.cs");
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
        var sandbox = SandboxWithFiles("src/Foo.cs");
        var ratchet = new InMemoryMutationRatchetStore();
        await ratchet.SaveAsync("alpha:main", 90.0);

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
        var sandbox = SandboxWithFiles("src/Foo.cs");
        var ratchet = new InMemoryMutationRatchetStore();
        await ratchet.SaveAsync("alpha:main", 90.0);

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
        var sandbox = SandboxWithFiles("src/Foo.cs");
        var ratchet = new InMemoryMutationRatchetStore();
        await ratchet.SaveAsync("alpha:main", 90.0);

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
        Assert.Equal(90.0, await ratchet.TryGetAsync("alpha:main"));
    }

    [Fact]
    public async Task NoBaseline_FirstRunRecordsCurrent_NoRegressionFinding()
    {
        var sandbox = SandboxWithFiles("src/Foo.cs");
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
        Assert.Equal(70.0, await ratchet.TryGetAsync("alpha:main"));
    }

    [Fact]
    public async Task DiffFilteredByExtensions_OnlyMatchedFilesScoped()
    {
        var sandbox = SandboxWithFiles("src/Foo.cs", "src/Bar.py", "README.md", "src/Baz.Cs");
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
        var sandbox = SandboxWithFiles("src/Foo.cs", "tests/FooTests.cs", "src/Bar.cs");
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
        var sandbox = SandboxWithFiles("README.md", "src/Bar.py");
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
        var sandbox = SandboxWithFiles("src/Foo.cs");
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

    /// <summary>
    /// Cancellation must propagate as <see cref="OperationCanceledException"/>
    /// rather than being silently converted into an Error finding — the
    /// pipeline relies on the OCE to wrap the phase as a cancellation.
    /// </summary>
    [Fact]
    public async Task Cancellation_Propagates_NotConvertedToFinding()
    {
        var sandbox = SandboxWithFiles("src/Foo.cs");
        using var cts = new CancellationTokenSource();
        var runner = new FakeMutationRunner
        {
            OnRun = ct =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
        };
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true },
            runner,
            new InMemoryMutationRatchetStore());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => auditor.RunAsync(sandbox, "/work", Ctx(), cts.Token));
    }

    /// <summary>
    /// Fail-closed: when BOTH git diff invocations exit non-zero the audit
    /// must produce an Error finding rather than silently passing as "no
    /// changed files". A broken base ref or shallow clone would otherwise
    /// green-light the gate this feature exists to make un-gameable.
    /// </summary>
    [Fact]
    public async Task GitDiffFails_BothInvocations_EmitsErrorFinding()
    {
        var sandbox = new StubSandbox
        {
            OriginDiffExitCode = 128,
            FallbackDiffExitCode = 128,
            Stderr = "fatal: bad revision 'origin/main...HEAD'",
        };
        var runner = new FakeMutationRunner();
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true },
            runner,
            new InMemoryMutationRatchetStore());

        var result = await auditor.RunAsync(sandbox, "/work", Ctx());

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("enumerate changed files", finding.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bad revision", finding.Description);
        Assert.Equal(0, runner.Calls);
    }

    /// <summary>
    /// Fallback path: the origin/&lt;base&gt; revspec fails (no remote
    /// fetched) but the local &lt;base&gt; revspec succeeds. The auditor
    /// should still scope the files from the second diff and run the engine.
    /// </summary>
    [Fact]
    public async Task GitDiffFallback_OriginMissing_LocalSucceeds_ScopesFromFallback()
    {
        var sandbox = new StubSandbox
        {
            OriginDiffExitCode = 128,
            OriginDiffStdout = "",
            FallbackDiffExitCode = 0,
            FallbackDiffStdout = "src/Foo.cs\0",
        };
        var runner = new FakeMutationRunner();
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true },
            runner,
            new InMemoryMutationRatchetStore());

        var result = await auditor.RunAsync(sandbox, "/work", Ctx());

        Assert.True(result.Passed);
        Assert.Equal(1, runner.Calls);
        Assert.Equal(new[] { "src/Foo.cs" }, runner.LastScopedFiles!.ToArray());
    }

    /// <summary>
    /// Custom RatchetKey override branch — the auditor must read and write
    /// the explicitly-configured key rather than the BaseBranch-derived
    /// default, so trunk-vs-release-branch baselines can diverge cleanly.
    /// </summary>
    [Fact]
    public async Task CustomRatchetKey_OverridesProjectBaseBranchDefault()
    {
        var sandbox = SandboxWithFiles("src/Foo.cs");
        var ratchet = new InMemoryMutationRatchetStore();
        await ratchet.SaveAsync("release-2026Q2", 80.0);
        var runner = new FakeMutationRunner
        {
            NextReport = new MutationRunReport(100.0, 90.0, [], TimeSpan.Zero),
        };
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions
            {
                Enabled = true,
                ChangedCodeThresholdPercent = 80,
                RatchetKey = "release-2026Q2",
            },
            runner,
            ratchet);

        var result = await auditor.RunAsync(sandbox, "/work", Ctx());

        Assert.True(result.Passed);
        // The default key (alpha:main) must NOT be written — the override branch
        // is the one that should be exercised.
        Assert.Null(await ratchet.TryGetAsync("alpha:main"));
        Assert.Equal(90.0, await ratchet.TryGetAsync("release-2026Q2"));
    }

    /// <summary>
    /// Multi-project default-key isolation. Two projects sharing the same
    /// singleton ratchet store and both targeting 'main' must NOT collide:
    /// project-A's high score is project-B's untouched baseline.
    /// </summary>
    [Fact]
    public async Task DefaultRatchetKey_IsScopedPerProject()
    {
        var sandbox = SandboxWithFiles("src/Foo.cs");
        var ratchet = new InMemoryMutationRatchetStore();
        var runner = new FakeMutationRunner
        {
            NextReport = new MutationRunReport(100.0, 92.0, [], TimeSpan.Zero),
        };
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true },
            runner,
            ratchet);

        await auditor.RunAsync(sandbox, "/work", Ctx(projectId: "alpha"));
        Assert.Equal(92.0, await ratchet.TryGetAsync("alpha:main"));
        Assert.Null(await ratchet.TryGetAsync("beta:main"));

        runner.NextReport = new MutationRunReport(100.0, 60.0, [], TimeSpan.Zero);
        var result = await auditor.RunAsync(sandbox, "/work", Ctx(projectId: "beta"));
        // Project beta has its own baseline — no regression even though 60 < 92.
        Assert.True(result.Passed);
        Assert.Equal(60.0, await ratchet.TryGetAsync("beta:main"));
        Assert.Equal(92.0, await ratchet.TryGetAsync("alpha:main"));
    }

    /// <summary>
    /// BudgetMinutes (int, config-friendly) must be the value the runner
    /// receives. A regression that handed the runner TimeSpan.Zero or the
    /// wrong field would let the runner abort mutants instantly.
    /// </summary>
    [Fact]
    public async Task BudgetMinutes_IsHandedToRunner()
    {
        var sandbox = SandboxWithFiles("src/Foo.cs");
        var runner = new FakeMutationRunner();
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true, BudgetMinutes = 42 },
            runner,
            new InMemoryMutationRatchetStore());

        await auditor.RunAsync(sandbox, "/work", Ctx());

        Assert.Equal(TimeSpan.FromMinutes(42), runner.LastBudget);
    }

    /// <summary>
    /// BudgetMinutes &lt;= 0 is clamped to 1 minute so a misconfigured 0 in
    /// appsettings.json cannot hand the runner a zero or negative budget.
    /// </summary>
    [Fact]
    public void BudgetMinutes_ZeroOrNegative_ClampedToOne()
    {
        Assert.Equal(TimeSpan.FromMinutes(1),
            new MutationTestingAuditorOptions { BudgetMinutes = 0 }.Budget);
        Assert.Equal(TimeSpan.FromMinutes(1),
            new MutationTestingAuditorOptions { BudgetMinutes = -5 }.Budget);
    }

    /// <summary>
    /// Out-of-scope survivor: a runner that surfaces a mutant whose file is
    /// not in the scoped list (contract violation) must be defended against
    /// — the auditor drops the finding rather than flooding the rework loop
    /// with out-of-diff noise.
    /// </summary>
    [Fact]
    public async Task SurvivingMutant_OutsideScope_IsDropped()
    {
        var sandbox = SandboxWithFiles("src/Foo.cs");
        var inScope = new SurvivingMutant("src/Foo.cs", 1, "M", "in-diff");
        var outOfScope = new SurvivingMutant("src/Elsewhere.cs", 9, "M", "out-of-diff");
        var runner = new FakeMutationRunner
        {
            NextReport = new MutationRunReport(85.0, 85.0, [inScope, outOfScope], TimeSpan.Zero),
        };
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true, ChangedCodeThresholdPercent = 80 },
            runner,
            new InMemoryMutationRatchetStore());

        var result = await auditor.RunAsync(sandbox, "/work", Ctx());

        Assert.False(result.Passed);
        var mutant = Assert.Single(result.Findings);
        Assert.Equal("src/Foo.cs:1", mutant.Location);
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
