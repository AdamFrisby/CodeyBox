using CodeyBox.Audit;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the Stryker-era auditor contract deltas: scoped (changed-files-
/// only) reports never touch the overall baseline, configuration digests
/// isolate baselines across engine configurations, explicit no-evidence
/// outcomes map to pass-with-info (or a blocking finding for missing test
/// coverage), and sandbox deferrals / transport loss / cancellation propagate
/// instead of becoming findings.
/// </summary>
public sealed class MutationTestingAuditorStrykerTests
{
    private sealed class StubSandbox : ISandbox
    {
        public string Id => "stub-mutation-stryker";
        public string DiffStdout { get; init; } = "src/Foo.cs\0";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            Task.FromResult(new SandboxExecResult(0, DiffStdout, ""));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeMutationRunner : IMutationRunner
    {
        public MutationRunReport NextReport { get; set; } =
            new(100.0, 100.0, [], TimeSpan.Zero);
        public Exception? Throw { get; set; }

        public Task<MutationRunReport> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            IReadOnlyList<string> changedFiles,
            TimeSpan budget,
            CancellationToken ct = default) =>
            Throw is not null ? Task.FromException<MutationRunReport>(Throw) : Task.FromResult(NextReport);
    }

    private static AuditContext Ctx(string? projectId = "alpha") =>
        new(WorkItemId.New(), WorkBranch: "feature/x", BaseBranch: "main",
            Iteration: 1, OriginalPrompt: "do x", ProjectId: projectId);

    private static MutationRunReport ScopedPass(
        double changed = 100.0, string? digest = "abc123") =>
        new(changed, null, [], TimeSpan.Zero,
            RawOutput: "provenance",
            Status: MutationRunStatus.Completed,
            Scope: MutationRunScope.ChangedFilesOnly,
            ConfigDigest: digest,
            ToolVersion: "4.16.0");

    [Fact]
    public async Task ScopedPass_DoesNotReadOrWriteBaseline()
    {
        var ratchet = new InMemoryMutationRatchetStore();
        await ratchet.SaveAsync("alpha:main~abc123", 95.0);
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true, ChangedCodeThresholdPercent = 80 },
            new FakeMutationRunner { NextReport = ScopedPass() },
            ratchet);

        var result = await auditor.RunAsync(new StubSandbox(), "/work", Ctx());

        Assert.True(result.Passed);
        // The scoped evidence must not regress against — or advance — the baseline.
        Assert.DoesNotContain(result.Findings, f => f.Title.Contains("regressed"));
        Assert.Equal(95.0, await ratchet.TryGetAsync("alpha:main~abc123"));
        var info = Assert.Single(result.Findings, f => f.Title.Contains("overall mutation score unavailable"));
        Assert.Equal(AuditSeverity.Info, info.Severity);
    }

    [Fact]
    public async Task ScopedPass_WithNoBaseline_LeavesNoBaseline()
    {
        var ratchet = new InMemoryMutationRatchetStore();
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true },
            new FakeMutationRunner { NextReport = ScopedPass() },
            ratchet);

        var result = await auditor.RunAsync(new StubSandbox(), "/work", Ctx());

        Assert.True(result.Passed);
        Assert.Null(await ratchet.TryGetAsync("alpha:main~abc123"));
        Assert.Null(await ratchet.TryGetAsync("alpha:main"));
    }

    [Fact]
    public async Task ScopedFailingRun_DoesNotTouchBaseline()
    {
        var ratchet = new InMemoryMutationRatchetStore();
        await ratchet.SaveAsync("alpha:main~abc123", 95.0);
        var survivor = new SurvivingMutant("src/Foo.cs", 5, "Arithmetic mutation", "detail");
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true, ChangedCodeThresholdPercent = 80 },
            new FakeMutationRunner
            {
                NextReport = ScopedPass(changed: 50.0) with
                {
                    SurvivingMutantsInChangedCode = [survivor],
                },
            },
            ratchet);

        var result = await auditor.RunAsync(new StubSandbox(), "/work", Ctx());

        Assert.False(result.Passed);
        Assert.Equal(95.0, await ratchet.TryGetAsync("alpha:main~abc123"));
    }

    [Fact]
    public async Task DifferentDigests_IsolateBaselines()
    {
        var ratchet = new InMemoryMutationRatchetStore();
        var full = new MutationRunReport(100.0, 70.0, [], TimeSpan.Zero,
            Scope: MutationRunScope.FullProject, ConfigDigest: "digest-one");
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true },
            new FakeMutationRunner { NextReport = full },
            ratchet);

        var result = await auditor.RunAsync(new StubSandbox(), "/work", Ctx());

        Assert.True(result.Passed);
        Assert.Equal(70.0, await ratchet.TryGetAsync("alpha:main~digest-one"));
        Assert.Null(await ratchet.TryGetAsync("alpha:main"));

        // A run under a different engine configuration compares against its
        // own baseline — 60 is not a regression against digest-one's 70.
        var other = full with { OverallMutationScorePercent = 60.0, ConfigDigest = "digest-two" };
        var auditor2 = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true },
            new FakeMutationRunner { NextReport = other },
            ratchet);
        var result2 = await auditor2.RunAsync(new StubSandbox(), "/work", Ctx());

        Assert.True(result2.Passed);
        Assert.Equal(60.0, await ratchet.TryGetAsync("alpha:main~digest-two"));
        Assert.Equal(70.0, await ratchet.TryGetAsync("alpha:main~digest-one"));
    }

    [Fact]
    public async Task LegacyReport_WithoutDigest_KeepsUndigestedKey()
    {
        var ratchet = new InMemoryMutationRatchetStore();
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true },
            new FakeMutationRunner
            {
                NextReport = new MutationRunReport(100.0, 92.0, [], TimeSpan.Zero),
            },
            ratchet);

        var result = await auditor.RunAsync(new StubSandbox(), "/work", Ctx());

        Assert.True(result.Passed);
        Assert.Equal(92.0, await ratchet.TryGetAsync("alpha:main"));
    }

    [Fact]
    public async Task NoApplicableCode_PassesWithInfo()
    {
        var ratchet = new InMemoryMutationRatchetStore();
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true },
            new FakeMutationRunner
            {
                NextReport = new MutationRunReport(
                    null, null, [], TimeSpan.Zero,
                    Status: MutationRunStatus.NoApplicableCode,
                    StatusDetail: "only test code changed",
                    Scope: MutationRunScope.ChangedFilesOnly),
            },
            ratchet);

        var result = await auditor.RunAsync(new StubSandbox(), "/work", Ctx());

        Assert.True(result.Passed);
        var info = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Info, info.Severity);
        Assert.Contains("no mutable code", info.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await ratchet.TryGetAsync("alpha:main"));
    }

    [Fact]
    public async Task UnsupportedProject_PassesWithInfo()
    {
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true },
            new FakeMutationRunner
            {
                NextReport = new MutationRunReport(
                    null, null, [], TimeSpan.Zero,
                    Status: MutationRunStatus.UnsupportedProject,
                    StatusDetail: "no .NET projects",
                    Scope: MutationRunScope.ChangedFilesOnly),
            },
            new InMemoryMutationRatchetStore());

        var result = await auditor.RunAsync(new StubSandbox(), "/work", Ctx());

        Assert.True(result.Passed);
        var info = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Info, info.Severity);
        Assert.Contains("unsupported project", info.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoCoveringTests_BlocksMerge()
    {
        var ratchet = new InMemoryMutationRatchetStore();
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true },
            new FakeMutationRunner
            {
                NextReport = new MutationRunReport(
                    null, null, [], TimeSpan.Zero,
                    Status: MutationRunStatus.NoCoveringTests,
                    StatusDetail: "SampleCalc.csproj has no covering test project",
                    Scope: MutationRunScope.ChangedFilesOnly),
            },
            ratchet);

        var result = await auditor.RunAsync(new StubSandbox(), "/work", Ctx());

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("no covering test project", finding.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await ratchet.TryGetAsync("alpha:main"));
    }

    [Fact]
    public async Task CompletedWithoutChangedScore_FailsClosed()
    {
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true },
            new FakeMutationRunner
            {
                NextReport = new MutationRunReport(
                    null, null, [], TimeSpan.Zero,
                    Status: MutationRunStatus.Completed,
                    Scope: MutationRunScope.ChangedFilesOnly),
            },
            new InMemoryMutationRatchetStore());

        var result = await auditor.RunAsync(new StubSandbox(), "/work", Ctx());

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("no changed-code score", finding.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(StrykerFailureKind.Build, "does not build")]
    [InlineData(StrykerFailureKind.Test, "does not pass")]
    [InlineData(StrykerFailureKind.Timeout, "budget exceeded")]
    [InlineData(StrykerFailureKind.Report, "no usable report")]
    [InlineData(StrykerFailureKind.Tool, "tool failure")]
    public async Task RunnerFailureKinds_MapToDistinctErrorFindings(
        StrykerFailureKind kind, string expectedTitleFragment)
    {
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true },
            new FakeMutationRunner
            {
                Throw = new StrykerRunFailedException("boom") { Kind = kind },
            },
            new InMemoryMutationRatchetStore());

        var result = await auditor.RunAsync(new StubSandbox(), "/work", Ctx());

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains(expectedTitleFragment, finding.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(result.Findings, f => f.Title.Contains("below threshold"));
    }

    [Fact]
    public async Task ToolMissing_MapsToProvisioningFinding()
    {
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true },
            new FakeMutationRunner
            {
                Throw = new StrykerToolMissingException("Stryker tool unavailable: expected 4.16.0"),
            },
            new InMemoryMutationRatchetStore());

        var result = await auditor.RunAsync(new StubSandbox(), "/work", Ctx());

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Contains("not provisioned", finding.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SandboxDeferral_Propagates_NotConvertedToFinding()
    {
        var deferral = new SandboxProvisioningDeferredException(
            "test", "exec", "capacity", "no room", TimeSpan.FromMinutes(1));
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true },
            new FakeMutationRunner { Throw = deferral },
            new InMemoryMutationRatchetStore());

        var thrown = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => auditor.RunAsync(new StubSandbox(), "/work", Ctx()));

        Assert.Same(deferral, thrown);
    }

    [Fact]
    public async Task ExecutionTransportLoss_Propagates_NotConvertedToFinding()
    {
        var loss = new SandboxExecutionUnavailableException(1);
        var auditor = new MutationTestingAuditor(
            new MutationTestingAuditorOptions { Enabled = true },
            new FakeMutationRunner { Throw = loss },
            new InMemoryMutationRatchetStore());

        await Assert.ThrowsAsync<SandboxExecutionUnavailableException>(
            () => auditor.RunAsync(new StubSandbox(), "/work", Ctx()));
    }
}
