using CodeyBox.Audit;
using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

[Collection("Pipeline integration")]
public sealed class BuildScriptAuditorTests : IDisposable
{
    private readonly string _workspace;

    public BuildScriptAuditorTests()
        => _workspace = Directory.CreateTempSubdirectory("codeybox-build-script-audit-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task PresentAndPasses_ReturnsPassedWithCapturedOutput()
    {
        var sandbox = new StubSandbox(exec => IsPresenceCheck(exec)
            ? new SandboxExecResult(0, "", "")
            : new SandboxExecResult(0, "build ok\n", ""));

        var result = await new BuildScriptAuditor().RunAsync(
            sandbox,
            "/work/repo",
            Ctx());

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
        Assert.Equal("build ok\n", result.RawOutput);
        var presence = Assert.Single(sandbox.Executed, IsPresenceCheck);
        Assert.Equal("/work/repo", presence.WorkingDirectory);
        var build = Assert.Single(sandbox.Executed, IsBuildExecution);
        Assert.Equal("/work/repo", build.WorkingDirectory);
        Assert.Equal(BuildScriptAuditor.OutputCaptureMaxBytes, build.MaxStdoutBytes);
        Assert.Equal(BuildScriptAuditor.OutputCaptureMaxBytes, build.MaxStderrBytes);
        Assert.False(build.KillOnOutputLimit);
    }

    [Fact]
    public async Task PresentAndFails_EmitsBlockingBuildFailedFindingWithOutput()
    {
        var sandbox = new StubSandbox(exec => IsPresenceCheck(exec)
            ? new SandboxExecResult(0, "", "")
            : new SandboxExecResult(2, "compile stdout\n", "compile stderr\n"));

        var result = await new BuildScriptAuditor().RunAsync(
            sandbox,
            "/work/repo",
            Ctx());

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Equal("build failed", finding.Title);
        Assert.Contains("build.sh exited with code 2", finding.Description);
        Assert.DoesNotContain("compile stdout", finding.Description);
        Assert.DoesNotContain("compile stderr", finding.Description);
        Assert.Contains("audit raw output", finding.Description);
        Assert.Contains("compile stdout", result.RawOutput);
        Assert.Contains("compile stderr", result.RawOutput);
    }

    [Fact]
    public async Task AbsentAndOptional_SkipsWithoutBlocking()
    {
        var sandbox = new StubSandbox(exec =>
        {
            if (IsPresenceCheck(exec))
                return new SandboxExecResult(1, "", "");
            return new SandboxExecResult(99, "should not run", "");
        });

        var result = await new BuildScriptAuditor().RunAsync(
            sandbox,
            "/work/repo",
            Ctx(required: false));

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
        Assert.Equal("build.sh absent; auditor skipped", result.RawOutput);
        Assert.False(result.BuildTestGateEvidenceVerified);
        Assert.DoesNotContain(sandbox.Executed, IsBuildExecution);
        var onlyExec = Assert.Single(sandbox.Executed);
        Assert.True(IsPresenceCheck(onlyExec));
    }

    [Fact]
    public async Task AbsentAndRequired_IsBlockingMissingScriptFinding()
    {
        var sandbox = new StubSandbox(exec => IsPresenceCheck(exec)
            ? new SandboxExecResult(1, "", "")
            : new SandboxExecResult(99, "should not run", ""));

        var result = await new BuildScriptAuditor().RunAsync(
            sandbox,
            "/work/repo",
            Ctx(required: true));

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Equal("build.sh missing", finding.Title);
        Assert.DoesNotContain(sandbox.Executed, exec => exec.Argv.FirstOrDefault() == "git");
    }

    [Fact]
    public async Task AbsentOnWorkBranchAndOptional_DoesNotProbeBaseBranch()
    {
        var sandbox = new StubSandbox(exec =>
        {
            if (IsPresenceCheck(exec))
                return new SandboxExecResult(1, "", "");
            return new SandboxExecResult(99, "should not run", "");
        });

        var result = await new BuildScriptAuditor().RunAsync(
            sandbox,
            "/work/repo",
            Ctx(required: false));

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
        Assert.DoesNotContain(sandbox.Executed, IsBuildExecution);
        Assert.DoesNotContain(sandbox.Executed, exec => exec.Argv.FirstOrDefault() == "git");
    }

    [Theory]
    [InlineData(126)]
    [InlineData(127)]
    public async Task BuildScriptExit126Or127_ThrowsCouldNotVerifyInsteadOfFinding(int exitCode)
    {
        var sandbox = new StubSandbox(exec => IsPresenceCheck(exec)
            ? new SandboxExecResult(0, "", "")
            : new SandboxExecResult(exitCode, "", "command not found\n"));

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(() =>
            new BuildScriptAuditor().RunAsync(
                sandbox,
                "/work/repo",
                Ctx()));

        Assert.Contains("could-not-verify", ex.Message);
        Assert.Contains("build.sh could not execute", ex.Message);
        Assert.Contains($"exit {exitCode}", ex.Message);
        Assert.Equal(exitCode, ex.ExitCode);
        Assert.NotNull(ex.Output);
        Assert.Contains("command not found", ex.Output);
    }

    [Fact]
    public async Task Timeout_ThrowsCouldNotVerifyInsteadOfFinding()
    {
        var sandbox = new TimeoutSandbox();

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(() =>
            new BuildScriptAuditor(new BuildScriptAuditorOptions { TimeoutSeconds = 1 }).RunAsync(
                sandbox,
                "/work/repo",
                Ctx()));

        Assert.Contains("could-not-verify", ex.Message);
        Assert.Contains("timed out", ex.Message);
        Assert.Contains(sandbox.Executed, argv => argv.SequenceEqual(["sh", "-c", "./build.sh"]));
    }

    [Fact]
    public async Task BuildExecThrows_ThrowsCouldNotVerifyInsteadOfFinding()
    {
        var sandbox = new ThrowOnBuildSandbox();

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(() =>
            new BuildScriptAuditor().RunAsync(
                sandbox,
                "/work/repo",
                Ctx()));

        Assert.Contains("could-not-verify", ex.Message);
        Assert.Contains("could not execute", ex.Message);
    }

    [Theory]
    [InlineData(126)]
    [InlineData(127)]
    public async Task PresenceProbeCannotExecute_ThrowsCouldNotVerifyInsteadOfSkip(int exitCode)
    {
        var sandbox = new StubSandbox(exec => IsPresenceCheck(exec)
            ? new SandboxExecResult(exitCode, "", "shell unavailable")
            : new SandboxExecResult(99, "should not run", ""));

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(() =>
            new BuildScriptAuditor().RunAsync(
                sandbox,
                "/work/repo",
                Ctx(required: false)));

        Assert.Contains("could-not-verify", ex.Message);
        Assert.Contains($"exit {exitCode}", ex.Message);
    }

    [Fact]
    public async Task PresenceProbeUnexpectedNonOneExit_ThrowsCouldNotVerifyInsteadOfSkip()
    {
        var sandbox = new StubSandbox(exec => IsPresenceCheck(exec)
            ? new SandboxExecResult(2, "", "test command failed")
            : new SandboxExecResult(99, "should not run", ""));

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(() =>
            new BuildScriptAuditor().RunAsync(
                sandbox,
                "/work/repo",
                Ctx(required: false)));

        Assert.Contains("could-not-verify", ex.Message);
        Assert.Contains("presence check", ex.Message);
        Assert.Contains("exit 2", ex.Message);
        Assert.DoesNotContain(sandbox.Executed, IsBuildExecution);
    }

    [Fact]
    public async Task PresenceProbeExecutionUnavailable_ThrowsCouldNotVerifyInsteadOfSkip()
    {
        var sandbox = new StubSandbox(exec => IsPresenceCheck(exec)
            ? new SandboxExecResult(1, "", "provider unavailable", ExecutionUnavailable: true)
            : new SandboxExecResult(99, "should not run", ""));

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(() =>
            new BuildScriptAuditor().RunAsync(
                sandbox,
                "/work/repo",
                Ctx(required: false)));

        Assert.Contains("could-not-verify", ex.Message);
        Assert.Contains("presence check", ex.Message);
        Assert.DoesNotContain(sandbox.Executed, IsBuildExecution);
    }

    [Fact]
    public async Task PresenceProbeThrows_ThrowsCouldNotVerifyInsteadOfSkip()
    {
        var sandbox = new ThrowOnPresenceSandbox();

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(() =>
            new BuildScriptAuditor().RunAsync(
                sandbox,
                "/work/repo",
                Ctx(required: false)));

        Assert.Contains("could-not-verify", ex.Message);
        Assert.Contains("check for ./build.sh", ex.Message);
    }

    [Fact]
    public async Task ProviderExecUnavailableResult_ThrowsCouldNotVerifyInsteadOfBuildFinding()
    {
        var sandbox = new StubSandbox(exec => IsPresenceCheck(exec)
            ? new SandboxExecResult(0, "", "")
            : new SandboxExecResult(1, "", "sandbox provider unavailable", ExecutionUnavailable: true));

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(() =>
            new BuildScriptAuditor().RunAsync(
                sandbox,
                "/work/repo",
                Ctx()));

        Assert.Contains("could-not-verify", ex.Message);
        Assert.Contains("build.sh could not execute", ex.Message);
    }

    [Fact]
    public async Task OutputLimitExceededWithNonZeroExit_EmitsBuildFinding()
    {
        var sandbox = new StubSandbox(exec => IsPresenceCheck(exec)
            ? new SandboxExecResult(0, "", "")
            : new SandboxExecResult(137, "partial stdout", "", StdoutLimitExceeded: true));

        var result = await new BuildScriptAuditor().RunAsync(
            sandbox,
            "/work/repo",
            Ctx());

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("build failed", finding.Title);
        Assert.Contains("build.sh exited with code 137", finding.Description);
        Assert.Contains("Output beyond", finding.Description);
        Assert.DoesNotContain("partial stdout", finding.Description);
        Assert.Contains("stdout truncated", result.RawOutput);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OutputLimitExceededWithExitZero_Passes(bool stdoutLimitExceeded, bool stderrLimitExceeded)
    {
        var sandbox = new StubSandbox(exec => IsPresenceCheck(exec)
            ? new SandboxExecResult(0, "", "")
            : new SandboxExecResult(
                0,
                stdoutLimitExceeded ? "partial stdout" : "",
                stderrLimitExceeded ? "partial stderr" : "",
                StdoutLimitExceeded: stdoutLimitExceeded,
                StderrLimitExceeded: stderrLimitExceeded));

        var result = await new BuildScriptAuditor().RunAsync(
            sandbox,
            "/work/repo",
            Ctx());

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
        Assert.Contains(stdoutLimitExceeded ? "stdout truncated" : "stderr truncated", result.RawOutput);
    }

    [Fact]
    public async Task TimeoutOptionsAccessor_EvaluatedPerRun()
    {
        var calls = 0;
        var auditor = new BuildScriptAuditor(() =>
        {
            calls++;
            return new BuildScriptAuditorOptions { TimeoutSeconds = 30 + calls };
        });
        var sandbox = new StubSandbox(exec => IsPresenceCheck(exec)
            ? new SandboxExecResult(0, "", "")
            : new SandboxExecResult(0, "ok", ""));

        await auditor.RunAsync(sandbox, "/work/repo", Ctx());
        await auditor.RunAsync(sandbox, "/work/repo", Ctx());

        Assert.Equal(2, calls);
    }

    [Fact]
    public void BuildScriptAuditor_IsCredentialFreeAndIsolated()
    {
        var auditor = new BuildScriptAuditor();

        Assert.Equal(AuditCapabilities.None, auditor.Required);
        Assert.Equal(AuditorRole.BuildTestGate, auditor.Role);
        Assert.Equal(BuildTestGateEvidence.Build, auditor.BuildTestGateEvidence);
        Assert.True(((IAuditSandboxIsolation)auditor).RequiresFreshSandbox);
    }

    [Fact]
    public async Task Pipeline_PassingBuildScriptAloneDoesNotUnlockLlmPanel()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var llm = new BuildScriptRecordingLlmAuditor();
        var projectAudit = new ProjectAudit
        {
            MaxIterations = 1,
            AuditTypes = ["scripted"],
            StopOnFirstFailure = false,
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new BuildScriptAuditor(new BuildScriptAuditorOptions { TimeoutSeconds = 5 }), llm],
            projectAudit: projectAudit,
            credentials: AuditCredentials(),
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);

        var item = NewItem("feature/build-script-only") with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await CommitBuildScriptToBareBranchAsync(
            tp.GitHost.GetRepoPath(repoId),
            item.WorkBranch!,
            """
            #!/bin/sh
            echo build ok
            exit 0
            """);

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Contains("build/test gate", final.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(llm.SeenIterations);
    }

    [Fact]
    public async Task Pipeline_OptionalMissingBuildScriptDoesNotProvideBuildEvidence_WhenTestGatePasses()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var testGate = new PassingTestGateAuditor();
        var llm = new BuildScriptRecordingLlmAuditor();
        var projectAudit = new ProjectAudit
        {
            MaxIterations = 1,
            AuditTypes = ["scripted"],
            StopOnFirstFailure = false,
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new BuildScriptAuditor(new BuildScriptAuditorOptions { TimeoutSeconds = 5 }), testGate, llm],
            projectAudit: projectAudit,
            credentials: AuditCredentials(),
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);

        var item = NewItem("feature/optional-build-script-missing") with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await CommitFileToBareBranchAsync(
            tp.GitHost.GetRepoPath(repoId),
            item.WorkBranch!,
            "work.txt",
            "work complete\n");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Contains("build/test gate", final.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([1], testGate.SeenIterations);
        Assert.Empty(llm.SeenIterations);
    }

    [Fact]
    public void ProjectAuditorComposer_RequiredBuildScriptCannotBeExcluded()
    {
        var composer = new ProjectAuditorComposer(
            new PresetCatalog(),
            new IAuditor[] { new BuildScriptAuditor() },
            NullLogger<ProjectAuditorComposer>.Instance);
        var project = new Project
        {
            Id = new ProjectId("required-build-script"),
            DisplayName = "Required Build Script",
            RepositoryUrl = "https://example.com/repo.git",
            Audit = new ProjectAudit
            {
                Languages = [],
                AuditTypes = [],
                BuildScriptRequired = true,
                ExcludedAuditors = [BuildScriptAuditor.AuditorName],
            },
        };

        var auditors = composer.Compose(project, new StubAgent());

        Assert.Contains(auditors, a => a.Name == BuildScriptAuditor.AuditorName);
    }

    [Fact]
    public async Task Pipeline_Exit127_FailsAsInfrastructureWithoutPersistedFinding()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var reports = new CapturingAuditReportStore();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new BuildScriptAuditor(new BuildScriptAuditorOptions { TimeoutSeconds = 5 })],
            maxAuditIterations: 1,
            auditReportStore: reports,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);

        var item = NewItem("feature/build-script-exit-127") with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await CommitBuildScriptToBareBranchAsync(
            tp.GitHost.GetRepoPath(repoId),
            item.WorkBranch!,
            """
            #!/bin/sh
            echo missing tool >&2
            exit 127
            """);

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal("infrastructure", final.FailureKind);
        Assert.Contains("could-not-verify", final.LastError);
        Assert.Contains("build.sh could not execute", final.LastError);
        Assert.DoesNotContain(reports.Reports, r => r.AuditorName == BuildScriptAuditor.AuditorName);
        Assert.NotEqual(WorkItemState.AuditPassed, final.State);
    }

    [Fact]
    public async Task Pipeline_RequiredMissingBuildScript_FailsAuditEndToEnd()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var reports = new CapturingAuditReportStore();
        var projectAudit = new ProjectAudit
        {
            MaxIterations = 1,
            AuditTypes = ["scripted"],
            BuildScriptRequired = true,
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new BuildScriptAuditor(new BuildScriptAuditorOptions { TimeoutSeconds = 5 })],
            maxAuditIterations: 1,
            projectAudit: projectAudit,
            auditReportStore: reports,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);

        var item = NewItem("feature/build-script-required-missing") with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await CommitFileToBareBranchAsync(
            tp.GitHost.GetRepoPath(repoId),
            item.WorkBranch!,
            "work.txt",
            "work complete\n");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Contains("build.sh missing", final.LastError);
        var report = Assert.Single(reports.Reports, r => r.AuditorName == BuildScriptAuditor.AuditorName);
        Assert.Contains(report.Findings, f => f.Title == "build.sh missing");
    }

    [Fact]
    public async Task Pipeline_BuildScriptCannotTamperWithLaterPromptRevisionAuditor()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var reports = new CapturingAuditReportStore();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors:
            [
                new BuildScriptAuditor(new BuildScriptAuditorOptions { TimeoutSeconds = 5 }),
                new PromptRevisionTrailerAuditor(),
            ],
            maxAuditIterations: 1,
            auditReportStore: reports,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);

        var item = NewItem("feature/build-script-tamper") with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await CommitBuildScriptToBareBranchAsync(
            tp.GitHost.GetRepoPath(repoId),
            item.WorkBranch!,
            """
            #!/bin/sh
            git config user.email audit@example.invalid
            git config user.name Audit
            git commit --allow-empty -m "tampered audit clone

            CodeyBox-Prompt-Revision: 1"
            echo tampered audit clone
            """);

        await tp.Store.CreateAsync(item);
        await tp.Store.RecordIterationDispatchAsync(item.Id, 1, 1, DateTimeOffset.UtcNow, CancellationToken.None);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Contains($"missing {CodeyBoxTrailers.PromptRevisionTrailerKey}", final.LastError);
        Assert.Contains(reports.Reports, r =>
            r.AuditorName == BuildScriptAuditor.AuditorName &&
            r.WorstSeverity == "none" &&
            r.RawOutput!.Contains("tampered audit clone"));
        var trailerReport = Assert.Single(reports.Reports, r => r.AuditorName == PromptRevisionTrailerAuditor.AuditorName);
        Assert.Contains(trailerReport.Findings, f => f.Title.Contains($"missing {CodeyBoxTrailers.PromptRevisionTrailerKey}"));
    }

    [Fact]
    public async Task Pipeline_BuildScriptCannotPushToDurableWorkBranch()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var reports = new CapturingAuditReportStore();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors:
            [
                new BuildScriptAuditor(new BuildScriptAuditorOptions { TimeoutSeconds = 5 }),
                new PromptRevisionTrailerAuditor(),
            ],
            maxAuditIterations: 1,
            auditReportStore: reports,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);

        var item = NewItem("feature/build-script-origin-push") with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await CommitBuildScriptToBareBranchAsync(
            barePath,
            item.WorkBranch!,
            $"""
            #!/bin/sh
            set -eu
            git config user.email audit@example.invalid
            git config user.name Audit
            printf tampered > tampered-by-build.txt
            git add tampered-by-build.txt
            git commit -m "tampered durable branch"
            git push origin HEAD:{item.WorkBranch}
            echo pushed tampered commit
            """);
        var originalTip = (await TestSupport.RunGit(barePath, "rev-parse", item.WorkBranch!)).stdout.Trim();

        await tp.Store.CreateAsync(item);
        await tp.Store.RecordIterationDispatchAsync(item.Id, 1, 1, DateTimeOffset.UtcNow, CancellationToken.None);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        var durableTip = (await TestSupport.RunGit(barePath, "rev-parse", item.WorkBranch!)).stdout.Trim();
        Assert.Equal(originalTip, durableTip);
        var showTampered = await TestSupport.RunGitNoThrow(barePath, "show", $"{item.WorkBranch}:tampered-by-build.txt");
        Assert.NotEqual(0, showTampered.code);
        var buildReport = Assert.Single(reports.Reports, r => r.AuditorName == BuildScriptAuditor.AuditorName);
        Assert.NotNull(buildReport.RawOutput);
        Assert.Contains("pushed tampered commit", buildReport.RawOutput!);
    }

    [Fact]
    public async Task ProjectRepository_MapsBuildScriptRequired_DefaultsAndProjectOverride()
    {
        var options = new ProjectsOptions
        {
            Defaults = new ProjectDefaultsConfig
            {
                Audit = new ProjectAuditConfig
                {
                    BuildScriptRequired = true,
                    Languages = [],
                    AuditTypes = [],
                },
            },
            Projects =
            [
                new ProjectConfig
                {
                    Id = "inherits",
                    RepositoryUrl = "https://example.com/inherits.git",
                },
                new ProjectConfig
                {
                    Id = "overrides",
                    RepositoryUrl = "https://example.com/overrides.git",
                    Audit = new ProjectAuditConfig
                    {
                        BuildScriptRequired = false,
                        Languages = [],
                        AuditTypes = [],
                    },
                },
            ],
        };
        using var repo = new ProjectRepository(
            new StaticOptionsMonitor<ProjectsOptions>(options),
            NullLogger<ProjectRepository>.Instance);

        var inherits = await repo.GetAsync(new ProjectId("inherits"));
        var overrides = await repo.GetAsync(new ProjectId("overrides"));

        Assert.True(inherits!.Audit.BuildScriptRequired);
        Assert.False(overrides!.Audit.BuildScriptRequired);
    }

    [Fact]
    public async Task ProjectRepository_ProfileCanDisableInheritedBuildScriptRequirement()
    {
        var options = new ProjectsOptions
        {
            Defaults = new ProjectDefaultsConfig
            {
                Audit = new ProjectAuditConfig
                {
                    BuildScriptRequired = true,
                    Languages = [],
                    AuditTypes = [],
                },
            },
            Projects =
            [
                new ProjectConfig
                {
                    Id = "profiled",
                    RepositoryUrl = "https://example.com/profiled.git",
                    Audit = new ProjectAuditConfig
                    {
                        Profile = "legacy",
                        Profiles = new Dictionary<string, ProjectAuditConfig>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["legacy"] = new()
                            {
                                BuildScriptRequired = false,
                                Languages = [],
                                AuditTypes = [],
                            },
                        },
                    },
                },
            ],
        };
        using var repo = new ProjectRepository(
            new StaticOptionsMonitor<ProjectsOptions>(options),
            NullLogger<ProjectRepository>.Instance);

        var project = await repo.GetAsync(new ProjectId("profiled"));
        var resolved = project!.Audit.ResolveProfile();

        Assert.True(project.Audit.BuildScriptRequired);
        Assert.False(resolved.BuildScriptRequired);
    }

    private static AuditContext Ctx(bool required = false) => new(
        WorkItemId.New(),
        WorkBranch: "work",
        BaseBranch: "main",
        Iteration: 1,
        OriginalPrompt: "prompt",
        BuildScriptRequired: required);

    private static ConstantCredentialProvider AuditCredentials()
        => new(new AgentCredential(
            AgentKind.Claude,
            new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "test-key" },
            new Dictionary<string, string>()));

    private static WorkItem NewItem(string workBranch) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "build script audit",
        Prompt = "change",
        BaseBranch = "main",
        WorkBranch = workBranch,
        PushUpstream = false,
    };

    private static bool IsPresenceCheck(SandboxExec exec)
        => exec.Argv.SequenceEqual(["sh", "-c", "test -f ./build.sh"]);

    private static bool IsBuildExecution(SandboxExec exec)
        => exec.Argv.SequenceEqual(["sh", "-c", "./build.sh"]);

    private static async Task CommitBuildScriptToBareBranchAsync(
        string barePath,
        string branch,
        string script)
    {
        var clone = Directory.CreateTempSubdirectory("codeybox-build-script-branch-").FullName;
        try
        {
            await TestSupport.RunGit(clone, "clone", barePath, ".");
            await TestSupport.RunGit(clone, "config", "user.email", "test@example.invalid");
            await TestSupport.RunGit(clone, "config", "user.name", "Test");
            await TestSupport.RunGit(clone, "checkout", "-B", branch, "origin/main");

            var path = Path.Combine(clone, "build.sh");
            await File.WriteAllTextAsync(path, script.Replace("\r\n", "\n", StringComparison.Ordinal));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            await TestSupport.RunGit(clone, "add", "build.sh");
            await TestSupport.RunGit(clone, "commit", "-m", "add build script");
            await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{branch}");
        }
        finally
        {
            try { Directory.Delete(clone, recursive: true); } catch { }
        }
    }

    private static async Task CommitFileToBareBranchAsync(
        string barePath,
        string branch,
        string relativePath,
        string contents)
    {
        var clone = Directory.CreateTempSubdirectory("codeybox-build-script-branch-").FullName;
        try
        {
            await TestSupport.RunGit(clone, "clone", barePath, ".");
            await TestSupport.RunGit(clone, "config", "user.email", "test@example.invalid");
            await TestSupport.RunGit(clone, "config", "user.name", "Test");
            await TestSupport.RunGit(clone, "checkout", "-B", branch, "origin/main");

            var path = Path.Combine(clone, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, contents);

            await TestSupport.RunGit(clone, "add", relativePath);
            await TestSupport.RunGit(clone, "commit", "-m", "work complete");
            await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{branch}");
        }
        finally
        {
            try { Directory.Delete(clone, recursive: true); } catch { }
        }
    }

    private sealed class StubSandbox : ISandbox
    {
        private readonly Func<SandboxExec, SandboxExecResult> _handler;
        public List<SandboxExec> Executed { get; } = [];

        public StubSandbox(Func<SandboxExec, SandboxExecResult> handler)
            => _handler = handler;

        public string Id => "stub";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Executed.Add(exec);
            return Task.FromResult(_handler(exec));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TimeoutSandbox : ISandbox
    {
        public List<IReadOnlyList<string>> Executed { get; } = [];
        public string Id => "timeout";

        public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Executed.Add(exec.Argv.ToArray());
            if (IsPresenceCheck(exec))
                return new SandboxExecResult(0, "", "");

            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new SandboxExecResult(0, "should not run", "");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowOnBuildSandbox : ISandbox
    {
        public string Id => "throw-build";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (IsPresenceCheck(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            throw new InvalidOperationException("sandbox exec failed");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowOnPresenceSandbox : ISandbox
    {
        public string Id => "throw-presence";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => throw new InvalidOperationException("presence probe failed");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubAgent : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Claude;

        public Task<AgentResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false)
            => Task.FromResult(new AgentResult(true, "ok", "", null));
    }

    private sealed class CapturingAuditReportStore : IAuditReportStore
    {
        public List<AuditReport> Reports { get; } = [];

        public Task CreateAsync(AuditReport report, CancellationToken ct = default)
        {
            Reports.Add(report);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditReport>> GetByWorkItemAsync(
            string workItemId,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AuditReport>>(
                Reports.Where(r => r.WorkItemId == workItemId).ToList());

        public Task<string?> GetRawOutputAsync(
            string workItemId,
            int iteration,
            string auditorName,
            CancellationToken ct = default)
            => Task.FromResult(Reports.FirstOrDefault(r =>
                    r.WorkItemId == workItemId &&
                    r.Iteration == iteration &&
                    r.AuditorName == auditorName)
                ?.RawOutput);

        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
        {
            var removed = Reports.RemoveAll(r => r.StartedAt < cutoff);
            return Task.FromResult(removed);
        }
    }

    private sealed class BuildScriptRecordingLlmAuditor : IAuditor
    {
        public string Name => "quality:llm-review";
        public string Kind => "llm";
        public AuditCapabilities Required => AuditCapabilities.AgentCredentials | AuditCapabilities.Network;
        public List<int> SeenIterations { get; } = [];

        public Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
        {
            SeenIterations.Add(context.Iteration);
            return Task.FromResult(new AuditResult(true, []));
        }
    }

    private sealed class PassingTestGateAuditor : IAuditor
    {
        public string Name => "custom:test-pass";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public AuditorRole Role => AuditorRole.BuildTestGate;
        public BuildTestGateEvidence BuildTestGateEvidence => BuildTestGateEvidence.Test;
        public List<int> SeenIterations { get; } = [];

        public Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
        {
            SeenIterations.Add(context.Iteration);
            return Task.FromResult(new AuditResult(true, []));
        }
    }
}
