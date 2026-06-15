using CodeyBox.Audit;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using CodeyBox.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

[Collection("Pipeline integration")]
public sealed class RequiredBuildGateTests : IDisposable
{
    private readonly string _workspace;

    public RequiredBuildGateTests()
        => _workspace = Directory.CreateTempSubdirectory("codeybox-required-build-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task RetryFromWork_PreservedBrokenBranch_RunsAgentAndGatesOutput()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnet = await CreateFakeDotnetAsync();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            sandboxProvider: new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment));

        var item = NewItem("feature/broken-csharp");
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        var baseTip = (await TestSupport.RunGit(barePath, "rev-parse", "main")).stdout.Trim();
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "build.fail", "broken\n", "broken prior attempt");

        var agentInvoked = false;
        var inheritedBuildFailVisible = true;
        tp.Agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            agentInvoked = true;
            var probe = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["test", "-f", $"{workingDirectory}/build.fail"],
            }, ct);
            inheritedBuildFailVisible = probe.Success;
        };
        tp.Agent.WorkPlan.Enqueue(new FileWrite("build.fail", "broken\n"));

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("work left the branch non-compiling", final.LastError);
        Assert.Contains("error CS1061", final.LastError);
        Assert.Equal("build", final.FailureKind);
        Assert.DoesNotContain("no changes", final.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.True(agentInvoked);
        Assert.True(inheritedBuildFailVisible);
        Assert.Empty(tp.Agent.WorkPlan);
        Assert.Equal(baseTip, (await TestSupport.RunGit(barePath, "rev-parse", $"{item.WorkBranch}~1")).stdout.Trim());
    }

    [Theory]
    [InlineData("clone")]
    [InlineData("access")]
    [InlineData("sandbox")]
    public async Task BuildScriptAuditor_IsolatedSetupFailure_FailsInfrastructureWithoutPersistedFinding(
        string failurePoint)
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var reports = new CapturingAuditReportStore();
        Func<IGitHost, IGitHost>? gitHostDecorator = failurePoint switch
        {
            "clone" => inner => new BrokenIsolatedRepositoryCloneGitHost(inner, "simulated isolated clone failure"),
            "access" => inner => new BrokenIsolatedRepoAccessGitHost(inner, "simulated isolated access failure"),
            _ => null,
        };
        ISandboxProvider? sandboxProvider = failurePoint == "sandbox"
            ? new SandboxFactoryFailingSandboxProvider("simulated isolated sandbox failure")
            : null;

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new BuildScriptAuditor(new BuildScriptAuditorOptions { TimeoutSeconds = 5 })],
            maxAuditIterations: 1,
            projectAudit: new ProjectAudit
            {
                MaxIterations = 1,
                AuditTypes = ["scripted"],
            },
            auditReportStore: reports,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            gitHostDecorator: gitHostDecorator,
            sandboxProvider: sandboxProvider);

        var item = NewItem($"feature/build-script-isolated-{failurePoint}") with
        {
            State = WorkItemState.WorkComplete,
        };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await CommitToBareBranchAsync(
            tp.GitHost.GetRepoPath(repoId),
            item.WorkBranch!,
            "build.sh",
            "#!/bin/sh\nexit 0\n",
            "add build script");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal("infrastructure", final.FailureKind);
        Assert.Contains("could-not-verify", final.LastError);
        Assert.Contains("isolated audit repository setup failed", final.LastError);
        Assert.DoesNotContain(reports.Reports, r => r.AuditorName == BuildScriptAuditor.AuditorName);
    }

    [Fact]
    public async Task RetryFromWork_DefaultCodeyBoxOwnedBranchWithBrokenBuild_ResetsAndRunsCleanWork()
    {
        // retry-from-work must not let inherited non-compiling state dead-end
        // the item before the work agent runs. The reset-eligible
        // server-owned branch is reset to base and the agent gets a clean
        // work pass; the required-build gate applies only to the agent output.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnet = await CreateFakeDotnetAsync();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            sandboxProvider: new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment));

        // Use the default codeybox/{id8} branch naming so the pickup path
        // takes the IsPickupRebaseOwnedWorkBranch=true reset branch — the
        // branch suffix MUST match the work item id's first 8 chars or the
        // ownership check rejects it and the branch is preserved instead.
        var workItemId = WorkItemId.New();
        var item = NewItem($"codeybox/{workItemId.ToString()[..8]}") with { Id = workItemId };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        var baseTip = (await TestSupport.RunGit(barePath, "rev-parse", "main")).stdout.Trim();
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "build.fail", "broken\n", "broken prior attempt");

        var agentInvoked = false;
        tp.Agent.BeforeWorkAsync = (_, _, _) =>
        {
            agentInvoked = true;
            return Task.CompletedTask;
        };
        tp.Agent.WorkPlan.Enqueue(new FileWrite("fixed.txt", "clean retry\n"));

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.True(agentInvoked);
        Assert.Null(final.FailureKind);

        var workTipFile = await TestSupport.RunGitNoThrow(
            barePath, "show", $"{item.WorkBranch}:build.fail");
        Assert.NotEqual(0, workTipFile.code);
        Assert.Equal(baseTip, (await TestSupport.RunGit(barePath, "rev-parse", $"{item.WorkBranch}~1")).stdout.Trim());
        var fixedFile = await TestSupport.RunGit(barePath, "show", $"{item.WorkBranch}:fixed.txt");
        Assert.Equal("clean retry\n", fixedFile.stdout);
    }

    [Fact]
    public async Task RetryFromWork_RecoveredNonOwnedBrokenBranch_ResetsAndRunsCleanWork()
    {
        // Recovered queued items with non-owned/anomalous branches now take the
        // reset path. A broken inherited branch must not fail before the
        // agent runs; resetting to base gives the retry a clean chance.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnet = await CreateFakeDotnetAsync();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            sandboxProvider: new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment));

        var item = NewItem("feature/recovered-anomalous-broken") with { RecoveryAttempts = 1 };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        var baseTip = (await TestSupport.RunGit(barePath, "rev-parse", "main")).stdout.Trim();
        await CommitToBareBranchAsync(
            barePath, item.WorkBranch!, "build.fail", "broken\n", "broken recovered anomalous branch");
        var agentInvoked = false;
        tp.Agent.BeforeWorkAsync = (_, _, _) =>
        {
            agentInvoked = true;
            return Task.CompletedTask;
        };
        tp.Agent.WorkPlan.Enqueue(new FileWrite("fixed.txt", "clean recovered retry\n"));

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.True(agentInvoked);
        Assert.Null(final.FailureKind);

        var workTip = await TestSupport.RunGit(barePath, "rev-parse", item.WorkBranch!);
        Assert.NotEqual(baseTip, workTip.stdout.Trim());
        Assert.Equal(baseTip, (await TestSupport.RunGit(barePath, "rev-parse", $"{item.WorkBranch}~1")).stdout.Trim());
        var workTipFile = await TestSupport.RunGitNoThrow(barePath, "show", $"{item.WorkBranch}:build.fail");
        Assert.NotEqual(0, workTipFile.code);
        var fixedFile = await TestSupport.RunGit(barePath, "show", $"{item.WorkBranch}:fixed.txt");
        Assert.Equal("clean recovered retry\n", fixedFile.stdout);
    }

    [Fact]
    public async Task WorkCompletion_NewCommitThatBreaksRequiredBuild_FailsWithBuildFailureKind()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnet = await CreateFakeDotnetAsync();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            sandboxProvider: new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment));

        var item = NewItem("feature/work-build-break");
        tp.Agent.WorkPlan.Enqueue(new FileWrite("build.fail", "broken\n"));

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("work left the branch non-compiling", final.LastError);
        Assert.Contains("error CS1061", final.LastError);
        Assert.Equal("build", final.FailureKind);
    }

    [Fact]
    public async Task AuditRework_NonCompilingAtBudgetCeilingWithProgress_ParksForOperatorReview()
    {
        // A non-compiling rework no longer terminal-fails: the audit loop's
        // next iteration picks the failure up as a blocking finding via
        // RunForAuditAsync (same shape audit-discovered build failures
        // already produced). When the iteration budget exhausts with the
        // branch still non-compiling AND convergence signals visible
        // (different blocking findings between iterations, work-branch tip
        // changed), the item parks for operator review instead of
        // terminal-failing — matching the audit ceiling's park-if-progress
        // semantics.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnet = await CreateFakeDotnetAsync();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new OneTimeFailingAuditor()],
            maxAuditIterations: 2,
            sandboxProvider: new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment));

        var item = NewItem("feature/rework-build-break");
        tp.Agent.WorkPlan.Enqueue(new FileWrite("initial.txt", "initial\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("build.fail", "broken\n"));

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, final!.State);
        Assert.Contains("max iteration budget", final.LastError);
        Assert.Contains("required build failed", final.LastError);
    }

    [Fact]
    public async Task AuditRework_NonCompilingMidBudget_LoopsBackAndRecovers()
    {
        // Mid-budget non-compiling rework is recoverable: the next audit
        // iteration surfaces the build failure as a blocking finding, the
        // rework agent fixes the compile error, and a subsequent audit
        // iteration passes. The work item reaches Done — proof that the
        // loop did NOT terminal-fail at the first non-compiling rework.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnet = await CreateFakeDotnetAsync();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new OneTimeFailingAuditor()],
            maxAuditIterations: 4,
            sandboxProvider: new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment));

        var item = NewItem("feature/rework-build-recover");
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        // Call 1 (work): write initial.txt → compiles. WorkComplete.
        // Audit iter 1: OneTimeFailing fires (blocking). Build gate passes.
        //   → rework iter 2.
        // Call 2 (rework iter 2): write build.fail → non-compile. Under the
        //   new policy the gate defers instead of terminal-failing.
        // Audit iter 2: OneTimeFailing passed; build gate detects
        //   non-compile (blocking). → rework iter 3.
        // Call 3 (rework iter 3): BeforeWorkAsync deletes build.fail
        //   first, then the agent writes fixed.txt. The commit stages
        //   the deletion + addition; branch recompiles.
        // Audit iter 3: OneTimeFailing passed; build gate passes; zero
        //   blocking findings → audit passes → merge → Done.
        var callCount = 0;
        tp.Agent.BeforeWorkAsync = async (sandbox, wd, ct) =>
        {
            var n = Interlocked.Increment(ref callCount);
            if (n == 3)
            {
                await sandbox.ExecAsync(new SandboxExec
                {
                    Argv = ["rm", "-f", $"{wd}/build.fail"],
                }, ct);
            }
        };
        tp.Agent.WorkPlan.Enqueue(new FileWrite("initial.txt", "initial\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("build.fail", "broken\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("fixed.txt", "fixed\n"));

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(3, callCount);
        // No terminal build-failure trail on the item.
        Assert.True(string.IsNullOrEmpty(final.LastError) || !final.LastError.Contains("non-compiling"),
            $"expected loop-back recovery, got LastError={final.LastError}");

        var fixedFile = await TestSupport.RunGit(barePath, "show", $"{item.WorkBranch}:fixed.txt");
        Assert.Equal(0, fixedFile.code);
        Assert.Equal("fixed\n", fixedFile.stdout);
        var deletedBuildFail = await TestSupport.RunGitNoThrow(barePath, "show", $"{item.WorkBranch}:build.fail");
        Assert.NotEqual(0, deletedBuildFail.code);

        var dispatches = await tp.Store.GetIterationsAsync(item.Id);
        var workAttemptStartedAt = dispatches
            .Single(i => i.Iteration == AuditProgressIterationNumbers.WorkPhase)
            .DispatchedAt;
        var progress = await tp.Store.GetAuditProgressAsync(item.Id, workAttemptStartedAt);
        var buildFailureIteration = Assert.Single(progress, p => p.Iteration == 2);
        Assert.Contains(
            buildFailureIteration.BlockingFindingsDetails,
            f => f.AuditorName == RequiredBuildGateIdentity.AuditorName
                && f.Title.Contains("required build failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EnforceForWorkPhase_TerminalPolicy_ReworkPhaseString_StillThrowsOnBuildFailure()
    {
        // Pins the explicit-policy contract: the gate decides terminal vs
        // deferred from the RequiredBuildPolicy argument, NOT the phase
        // string. The post-act revalidation loop dispatches rework with
        // phase="rework" but has no subsequent build-gated audit iteration
        // to convert the failure into a finding, so it passes Terminal —
        // and the gate must throw even though the phase reads "rework".
        // A regression that re-introduces phase-string inference would
        // silently swallow the failure and let the item walk toward merge
        // with a broken build.
        var item = NewItem("feature/policy-terminal-pin");
        var project = NewProject(item);
        var verifier = new TestRequiredBuildVerifier(
            RequiredBuildProbeResult.Applies,
            RequiredBuildVerificationResult.Failed(1, "fake build error"));
        var gate = new RequiredBuildGate(verifier, persistReport: null);

        var ex = await Assert.ThrowsAsync<RequiredBuildFailedException>(() =>
            gate.EnforceForWorkPhaseAsync(
                item, project, repoId: "ignored",
                baseBranch: item.BaseBranch!, workBranch: item.WorkBranch!,
                agentPhase: "rework",
                policy: RequiredBuildPolicy.Terminal,
                ct: CancellationToken.None));
        Assert.Contains("rework left the branch non-compiling", ex.Message);
        Assert.Contains("fake build error", ex.Message);
    }

    [Fact]
    public async Task EnforceForWorkPhase_DeferPolicy_ReworkPhaseString_DoesNotThrowOnBuildFailure()
    {
        // The mirror of the Terminal pin: when the caller GUARANTEES a
        // subsequent build-gated audit iteration (audit-driven rework,
        // resume-after-preempt rework), the gate must NOT throw on a
        // non-compile — RunForAuditAsync picks it up as a blocking finding
        // and the audit/rework loop converges on it.
        var item = NewItem("feature/policy-defer-pin");
        var project = NewProject(item);
        var verifier = new TestRequiredBuildVerifier(
            RequiredBuildProbeResult.Applies,
            RequiredBuildVerificationResult.Failed(1, "fake build error"));
        var gate = new RequiredBuildGate(verifier, persistReport: null);

        // No exception → DeferToAuditLoop returns a deferred build failure
        // for the caller's next audit iteration to surface as a finding.
        var outcome = await gate.EnforceForWorkPhaseAsync(
            item, project, repoId: "ignored",
            baseBranch: item.BaseBranch!, workBranch: item.WorkBranch!,
            agentPhase: "rework",
            policy: RequiredBuildPolicy.DeferToAuditLoop,
            ct: CancellationToken.None);

        Assert.Equal(RequiredBuildWorkPhaseOutcome.DeferredFailure, outcome);
        Assert.Equal(1, verifier.VerifyCalls);
    }

    [Fact]
    public async Task EnforceForWorkPhase_TerminalPolicy_WorkPhaseString_ThrowsOnBuildFailure()
    {
        // Pins initial-work behavior: phase="work" + Terminal still throws.
        // This is the unchanged initial-work path; if the policy plumbing
        // ever inverts the meaning of the enum, this catches it.
        var item = NewItem("feature/policy-initial-work-pin");
        var project = NewProject(item);
        var verifier = new TestRequiredBuildVerifier(
            RequiredBuildProbeResult.Applies,
            RequiredBuildVerificationResult.Failed(1, "fake build error"));
        var gate = new RequiredBuildGate(verifier, persistReport: null);

        var ex = await Assert.ThrowsAsync<RequiredBuildFailedException>(() =>
            gate.EnforceForWorkPhaseAsync(
                item, project, repoId: "ignored",
                baseBranch: item.BaseBranch!, workBranch: item.WorkBranch!,
                agentPhase: "work",
                policy: RequiredBuildPolicy.Terminal,
                ct: CancellationToken.None));
        Assert.Contains("work left the branch non-compiling", ex.Message);
    }

    [Fact]
    public async Task AuditPass_CannotReachAuditPassed_WhenRequiredBuildFails()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnet = await CreateFakeDotnetAsync();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new PassingAuditor()],
            maxAuditIterations: 1,
            sandboxProvider: new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment));

        var item = NewItem("feature/audit-broken") with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "build.fail", "broken\n", "broken branch");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Contains("required build failed", final.LastError);
        Assert.NotEqual(WorkItemState.AuditPassed, final.State);
    }

    [Fact]
    public async Task AuditPass_CannotReachAuditPassed_WithNoAuditorsAndZeroAuditIterations_WhenRequiredBuildFails()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnet = await CreateFakeDotnetAsync();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            maxAuditIterations: 0,
            sandboxProvider: new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment));

        var item = NewItem("feature/audit-broken-no-auditors") with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "build.fail", "broken\n", "broken branch");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Contains("required build failed", final.LastError);
        Assert.NotEqual(WorkItemState.AuditPassed, final.State);
    }

    [Fact]
    public async Task AuditPass_RequiredBuildBuildsRootSolutionAndTestProject_WhenNoAuditors()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnet = await CreateFakeDotnetAsync();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            maxAuditIterations: 1,
            sandboxProvider: new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment));

        var item = NewItem("feature/audit-green-no-auditors") with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "ok.txt", "ok\n", "branch exists");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        var dotnetInvocations = await File.ReadAllLinesAsync(fakeDotnet.LogPath);
        Assert.Contains("build ./CodeyBox.slnx", dotnetInvocations);
        Assert.Contains("build ./tests/CodeyBox.Tests.csproj", dotnetInvocations);
    }

    [Fact]
    public async Task RequiredBuild_NonDotnetRepo_SkipsProbeVerifyAndPipelineAuditLoop()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var verifier = new SandboxRequiredBuildVerifier(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance),
            gitHost,
            new PipelineOptions { SandboxImageReference = "ignored" });

        var item = NewItem("feature/non-dotnet") with { State = WorkItemState.WorkComplete };
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = gitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "ok.txt", "ok\n", "branch exists");

        var probe = await verifier.ProbeAsync(new RequiredBuildProbeRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            RepositoryId = repoId,
            BaseBranch = item.BaseBranch,
            WorkBranch = item.WorkBranch!,
        }, CancellationToken.None);
        Assert.Equal(RequiredBuildProbeStatus.NotApplicable, probe.Status);

        var result = await verifier.VerifyAsync(new RequiredBuildVerificationRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            SandboxPolicy = new RequiredBuildSandboxPolicy(),
            RepositoryId = repoId,
            BaseBranch = item.BaseBranch,
            WorkBranch = item.WorkBranch!,
            Phase = "audit",
        }, CancellationToken.None);
        Assert.Equal(RequiredBuildVerificationStatus.Skipped, result.Status);

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            maxAuditIterations: 1);
        var pipelineItem = NewItem("feature/non-dotnet-pipeline") with { State = WorkItemState.WorkComplete };
        var pipelineRepoId = await tp.GitHost.EnsureRepositoryAsync(pipelineItem.Id, seed, pipelineItem.BaseBranch);
        await CommitToBareBranchAsync(
            tp.GitHost.GetRepoPath(pipelineRepoId),
            pipelineItem.WorkBranch!,
            "ok.txt",
            "ok\n",
            "branch exists");

        await tp.Store.CreateAsync(pipelineItem);
        await tp.Pipeline.RunAsync(pipelineItem, CancellationToken.None);

        var final = await tp.Store.GetAsync(pipelineItem.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
    }

    [Fact]
    public async Task AuditPass_CannotReachAuditPassed_WhenWorkBranchDeletesDotnetMarkers()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnet = await CreateFakeDotnetAsync();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            maxAuditIterations: 1,
            sandboxProvider: new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment));

        var item = NewItem("feature/deletes-dotnet-markers") with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await DeleteFromBareBranchAsync(
            tp.GitHost.GetRepoPath(repoId),
            item.WorkBranch!,
            "delete required build markers",
            "CodeyBox.slnx",
            "tests/CodeyBox.Tests.csproj");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Contains("required build failed", final.LastError);
        Assert.NotEqual(WorkItemState.AuditPassed, final.State);

        var verifier = new SandboxRequiredBuildVerifier(
            new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment),
            tp.GitHost,
            new PipelineOptions { SandboxImageReference = "ignored" });
        var verification = await verifier.VerifyAsync(new RequiredBuildVerificationRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            SandboxPolicy = new RequiredBuildSandboxPolicy(),
            RepositoryId = repoId,
            BaseBranch = item.BaseBranch,
            WorkBranch = item.WorkBranch!,
            Phase = "audit",
        }, CancellationToken.None);
        Assert.Equal(RequiredBuildVerificationStatus.Failed, verification.Status);
        Assert.Contains("deleted or moved", verification.Output);
    }

    [Fact]
    public async Task AuditPass_CannotReachAuditPassed_WhenRequiredBuildProbeCannotRun()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var verifier = new TestRequiredBuildVerifier(
            RequiredBuildProbeResult.Unavailable("git ls-tree failed"),
            RequiredBuildVerificationResult.Unavailable("verify should not run"));
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            maxAuditIterations: 1,
            requiredBuildVerifier: verifier);

        var item = NewItem("feature/probe-unavailable") with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await CommitToBareBranchAsync(
            tp.GitHost.GetRepoPath(repoId),
            item.WorkBranch!,
            "ok.txt",
            "ok\n",
            "branch exists");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal("infrastructure", final.FailureKind);
        Assert.Contains("could not verify required build", final.LastError);
        Assert.Contains("git ls-tree failed", final.LastError);
        Assert.Equal(1, verifier.ProbeCalls);
        Assert.Equal(0, verifier.VerifyCalls);
    }

    [Fact]
    public async Task AuditPass_CannotReachAuditPassed_WhenTestProjectBuildFailsAfterRootSolutionPasses()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnet = await CreateTestProjectFailingDotnetAsync();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            maxAuditIterations: 1,
            sandboxProvider: new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment));

        var item = NewItem("feature/test-project-build-fails") with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await CommitToBareBranchAsync(
            tp.GitHost.GetRepoPath(repoId),
            item.WorkBranch!,
            "ok.txt",
            "ok\n",
            "branch exists");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Contains("required build failed", final.LastError);
        var dotnetInvocations = await File.ReadAllLinesAsync(fakeDotnet.LogPath);
        Assert.Contains("build ./CodeyBox.slnx", dotnetInvocations);
        Assert.Contains("build ./tests/CodeyBox.Tests.csproj", dotnetInvocations);
    }

    [Fact]
    public async Task RequiredBuild_MaliciousBuildCannotMutateAuthoritativeBareRepository()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnet = await CreateMaliciousDotnetAsync();
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var verifier = new SandboxRequiredBuildVerifier(
            new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment),
            gitHost,
            new PipelineOptions { SandboxImageReference = "ignored" });

        var item = NewItem("feature/malicious-build") with { State = WorkItemState.WorkComplete };
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = gitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "evil.txt", "evil\n", "malicious branch");
        var (_, seedMain, _) = await TestSupport.RunGit(seed, "rev-parse", "main");
        await TestSupport.RunGit(barePath, "update-ref", "refs/heads/main", seedMain.Trim());
        var mainBefore = seedMain;

        var result = await verifier.VerifyAsync(new RequiredBuildVerificationRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            SandboxPolicy = new RequiredBuildSandboxPolicy(),
            RepositoryId = repoId,
            BaseBranch = item.BaseBranch,
            WorkBranch = item.WorkBranch!,
            Phase = "audit",
        }, CancellationToken.None);

        Assert.Equal(RequiredBuildVerificationStatus.Passed, result.Status);
        var (_, mainAfter, _) = await TestSupport.RunGit(barePath, "rev-parse", "main");
        var dotnetLog = File.Exists(fakeDotnet.LogPath)
            ? string.Join(" | ", await File.ReadAllLinesAsync(fakeDotnet.LogPath))
            : "(fake dotnet log missing)";
        Assert.True(
            string.Equals(mainBefore.Trim(), mainAfter.Trim(), StringComparison.Ordinal),
            $"authoritative main changed from {mainBefore.Trim()} to {mainAfter.Trim()}; fake dotnet log: {dotnetLog}");
    }

    [Fact]
    public async Task SandboxRequiredBuildVerifier_MarkerInspectionFailure_ReturnsUnavailable()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var throwingHost = new ThrowingListFilesGitHost(gitHost, "git ls-tree blew up");
        var verifier = new SandboxRequiredBuildVerifier(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance),
            throwingHost,
            new PipelineOptions { SandboxImageReference = "ignored" });

        var item = NewItem("feature/marker-inspection-fails") with { State = WorkItemState.WorkComplete };
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = gitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "ok.txt", "ok\n", "branch exists");

        var result = await verifier.VerifyAsync(new RequiredBuildVerificationRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            SandboxPolicy = new RequiredBuildSandboxPolicy(),
            RepositoryId = repoId,
            BaseBranch = item.BaseBranch,
            WorkBranch = item.WorkBranch!,
            Phase = "audit",
        }, CancellationToken.None);

        Assert.Equal(RequiredBuildVerificationStatus.Unavailable, result.Status);
        Assert.Contains("could not verify required build", result.Reason);
        Assert.Contains("git ls-tree blew up", result.Reason);

        var probe = await verifier.ProbeAsync(new RequiredBuildProbeRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            RepositoryId = repoId,
            BaseBranch = item.BaseBranch,
            WorkBranch = item.WorkBranch!,
        }, CancellationToken.None);
        Assert.Equal(RequiredBuildProbeStatus.Unavailable, probe.Status);
        Assert.Contains("git ls-tree blew up", probe.Reason);
    }

    [Fact]
    public async Task SandboxRequiredBuildVerifier_IsolatedRepoCreationFailure_ReturnsUnavailable()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var brokenHost = new BrokenIsolatedCloneGitHost(gitHost, "disk full while preparing isolated clone");
        var verifier = new SandboxRequiredBuildVerifier(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance),
            brokenHost,
            new PipelineOptions { SandboxImageReference = "ignored" });

        var item = NewItem("feature/isolated-clone-fails") with { State = WorkItemState.WorkComplete };
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = gitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "ok.txt", "ok\n", "branch exists");

        var result = await verifier.VerifyAsync(new RequiredBuildVerificationRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            SandboxPolicy = new RequiredBuildSandboxPolicy(),
            RepositoryId = repoId,
            BaseBranch = item.BaseBranch,
            WorkBranch = item.WorkBranch!,
            Phase = "audit",
        }, CancellationToken.None);

        Assert.Equal(RequiredBuildVerificationStatus.Unavailable, result.Status);
        Assert.Contains("could not verify required build", result.Reason);
        Assert.Contains("isolated build repository", result.Reason);
        Assert.Contains("disk full while preparing isolated clone", result.Reason);
    }

    [Fact]
    public async Task SandboxRequiredBuildVerifier_GitCloneInsideSandboxFails_ReturnsUnavailable()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var verifier = new SandboxRequiredBuildVerifier(
            new GitFailingSandboxProvider("fatal: simulated git clone failure inside sandbox"),
            gitHost,
            new PipelineOptions { SandboxImageReference = "ignored" });

        var item = NewItem("feature/sandbox-clone-fails") with { State = WorkItemState.WorkComplete };
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = gitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "ok.txt", "ok\n", "branch exists");

        var result = await verifier.VerifyAsync(new RequiredBuildVerificationRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            SandboxPolicy = new RequiredBuildSandboxPolicy(),
            RepositoryId = repoId,
            BaseBranch = item.BaseBranch,
            WorkBranch = item.WorkBranch!,
            Phase = "audit",
        }, CancellationToken.None);

        Assert.Equal(RequiredBuildVerificationStatus.Unavailable, result.Status);
        Assert.Contains("could not verify required build", result.Reason);
        Assert.Contains("git", result.Reason);
        Assert.Contains("simulated git clone failure", result.Reason);
    }

    [Fact]
    public async Task SandboxRequiredBuildVerifier_GitCloneInsideSandboxExceedsTimeout_ReturnsFailed()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var provider = new HangingRequiredBuildPreparationSandboxProvider();
        var verifier = new SandboxRequiredBuildVerifier(
            provider,
            gitHost,
            new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
                RequiredBuildVerificationTimeout = TimeSpan.FromMilliseconds(75),
            });

        var item = NewItem("feature/sandbox-clone-timeout") with { State = WorkItemState.WorkComplete };
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = gitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "ok.txt", "ok\n", "branch exists");

        using var outerCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await verifier.VerifyAsync(new RequiredBuildVerificationRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            SandboxPolicy = new RequiredBuildSandboxPolicy(),
            RepositoryId = repoId,
            BaseBranch = item.BaseBranch,
            WorkBranch = item.WorkBranch!,
            Phase = "audit",
        }, outerCts.Token);

        Assert.Equal(RequiredBuildVerificationStatus.Failed, result.Status);
        Assert.Equal(124, result.ExitCode);
        Assert.Contains("exceeded the required-build verification timeout", result.Output);
        Assert.Null(result.Reason);
        Assert.True(provider.ObservedPreparationCancellation,
            "git clone never observed cancellation, so the build timeout token was not linked into preparation");
    }

    [Fact]
    public async Task SandboxRequiredBuildVerifier_GitCheckoutInsideSandboxExceedsTimeout_ReturnsFailed()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var provider = new HangingRequiredBuildCheckoutSandboxProvider();
        var verifier = new SandboxRequiredBuildVerifier(
            provider,
            gitHost,
            new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
                RequiredBuildVerificationTimeout = TimeSpan.FromMilliseconds(75),
            });

        var item = NewItem("feature/sandbox-checkout-timeout") with { State = WorkItemState.WorkComplete };
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = gitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "ok.txt", "ok\n", "branch exists");

        using var outerCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await verifier.VerifyAsync(new RequiredBuildVerificationRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            SandboxPolicy = new RequiredBuildSandboxPolicy(),
            RepositoryId = repoId,
            BaseBranch = item.BaseBranch,
            WorkBranch = item.WorkBranch!,
            Phase = "audit",
        }, outerCts.Token);

        Assert.Equal(RequiredBuildVerificationStatus.Failed, result.Status);
        Assert.Equal(124, result.ExitCode);
        Assert.Contains("exceeded the required-build verification timeout", result.Output);
        Assert.Null(result.Reason);
        Assert.True(provider.ObservedCheckoutCancellation,
            "git checkout never observed cancellation, so the build timeout token was not linked into checkout");
    }

    [Fact]
    public async Task SandboxRequiredBuildVerifier_SandboxCreateAsyncThrows_ReturnsUnavailable()
    {
        // Coverage gap: VerifyAsync's catch-Exception arm at the sandbox
        // provisioning boundary. If ISandboxProvider.CreateAsync itself
        // throws (CI sandbox quota exhausted, hypervisor refused, image
        // pull failed), the verifier must surface that as Unavailable so
        // the item defers — never pass-by-default. Other tests drive
        // failures inside the sandbox; this one drives failure BEFORE one
        // ever materialises.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var verifier = new SandboxRequiredBuildVerifier(
            new SandboxFactoryFailingSandboxProvider("sandbox provisioning denied by quota"),
            gitHost,
            new PipelineOptions { SandboxImageReference = "ignored" });

        var item = NewItem("feature/sandbox-create-throws") with { State = WorkItemState.WorkComplete };
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = gitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "ok.txt", "ok\n", "branch exists");

        var result = await verifier.VerifyAsync(new RequiredBuildVerificationRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            SandboxPolicy = new RequiredBuildSandboxPolicy(),
            RepositoryId = repoId,
            BaseBranch = item.BaseBranch,
            WorkBranch = item.WorkBranch!,
            Phase = "audit",
        }, CancellationToken.None);

        Assert.Equal(RequiredBuildVerificationStatus.Unavailable, result.Status);
        Assert.Contains("could not verify required build", result.Reason);
        Assert.Contains("sandbox provisioning denied by quota", result.Reason);
        Assert.NotEqual(RequiredBuildVerificationStatus.Passed, result.Status);
    }

    [Fact]
    public async Task SandboxRequiredBuildVerifier_SandboxCreateAsyncProvisioningDeferred_Rethrows()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var deferred = new SandboxProvisioningDeferredException(
            provider: "multipass",
            operation: "clone",
            errorClass: "multipass-instance-lock-contention",
            detail: "clone retry exhausted",
            recheckIn: TimeSpan.FromMilliseconds(50));
        var verifier = new SandboxRequiredBuildVerifier(
            new SandboxFactoryProvisioningDeferredProvider(deferred),
            gitHost,
            new PipelineOptions { SandboxImageReference = "ignored" });

        var item = NewItem("feature/sandbox-create-deferred") with { State = WorkItemState.WorkComplete };
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = gitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "ok.txt", "ok\n", "branch exists");

        var thrown = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            verifier.VerifyAsync(new RequiredBuildVerificationRequest
            {
                WorkItemId = item.Id,
                ProjectId = item.ProjectId,
                SandboxPolicy = new RequiredBuildSandboxPolicy(),
                RepositoryId = repoId,
                BaseBranch = item.BaseBranch,
                WorkBranch = item.WorkBranch!,
                Phase = "audit",
            }, CancellationToken.None));

        Assert.Same(deferred, thrown);
    }

    [Fact]
    public async Task RequiredBuildGate_FailingBuild_PersistsAuditReportWithErrorFindingViaOrchestrator()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnet = await CreateFakeDotnetAsync();
        var captureStore = new CapturingAuditReportStore();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            maxAuditIterations: 1,
            sandboxProvider: new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment),
            auditReportStore: captureStore);

        var item = NewItem("feature/audit-report-fail") with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await CommitToBareBranchAsync(
            tp.GitHost.GetRepoPath(repoId),
            item.WorkBranch!,
            "build.fail",
            "broken\n",
            "broken branch");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var report = Assert.Single(
            captureStore.Reports,
            r => r.AuditorName == RequiredBuildGateIdentity.AuditorName);
        Assert.Equal(RequiredBuildGateIdentity.AuditorName, report.AuditorName);
        Assert.Equal("shell", report.AuditorKind);
        Assert.Equal(AuditSeverity.Error.ToString(), report.WorstSeverity);
        Assert.Equal(1, report.Iteration);
        Assert.Equal(item.Id.ToString(), report.WorkItemId);
        Assert.NotNull(report.RawOutput);
        Assert.Contains("error CS1061", report.RawOutput);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(AuditSeverity.Error.ToString(), finding.Severity);
        Assert.Contains("required build failed", finding.Title);
        Assert.StartsWith("f-", finding.Id);
    }

    [Fact]
    public async Task RequiredBuildGate_PassingBuild_PersistsAuditReportWithSeverityNoneViaOrchestrator()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnet = await CreateFakeDotnetAsync();
        var captureStore = new CapturingAuditReportStore();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            maxAuditIterations: 1,
            sandboxProvider: new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment),
            auditReportStore: captureStore);

        var item = NewItem("feature/audit-report-pass") with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await CommitToBareBranchAsync(
            tp.GitHost.GetRepoPath(repoId),
            item.WorkBranch!,
            "ok.txt",
            "ok\n",
            "branch exists");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var report = Assert.Single(
            captureStore.Reports,
            r => r.AuditorName == RequiredBuildGateIdentity.AuditorName);
        Assert.Equal(RequiredBuildGateIdentity.AuditorName, report.AuditorName);
        Assert.Equal("shell", report.AuditorKind);
        Assert.Equal("none", report.WorstSeverity);
        Assert.Equal(1, report.Iteration);
        Assert.Empty(report.Findings);
        Assert.NotNull(report.RawOutput);
        Assert.Contains("Build succeeded", report.RawOutput);
    }

    [Fact]
    public async Task RequiredBuildGate_WorkDeletesRootSolution_FailsEvenWhenLeafCsprojRemains()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnet = await CreateFakeDotnetAsync();
        var captureStore = new CapturingAuditReportStore();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            maxAuditIterations: 1,
            sandboxProvider: new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment),
            auditReportStore: captureStore);

        var item = NewItem("feature/work-deletes-root-keeps-leaf") with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await ReplaceBaseMarkersWithLeafProjectAsync(barePath, item.WorkBranch!);

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Contains("required build failed", final.LastError);

        var report = Assert.Single(
            captureStore.Reports,
            r => r.AuditorName == RequiredBuildGateIdentity.AuditorName);
        Assert.Equal(AuditSeverity.Error.ToString(), report.WorstSeverity);
        Assert.Equal(1, report.Iteration);
        Assert.NotNull(report.RawOutput);
        Assert.Contains("deleted or moved", report.RawOutput);
        Assert.Contains("CodeyBox.slnx", report.RawOutput);
        Assert.Contains("tests/CodeyBox.Tests.csproj", report.RawOutput);
    }

    [Fact]
    public async Task RequiredBuildGate_AppliesButNoBuildTargetFound_VerifierReturnsUnavailableNotPass()
    {
        // Drives the verifier's NoRequiredBuildTargetExitCode (125) branch:
        // marker inspection says the gate applies, but the sandbox build
        // script exits 125 because no buildable target was discovered
        // after checkout. The verifier must classify this as Unavailable
        // with a clear "no .NET solution or project file" reason —
        // never pass-by-default and never reach AuditPassed.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        // Sandbox provider that simulates the build script discovering no
        // target after checkout by returning exit 125 from `sh -c BUILD`.
        // Git invocations pass through to the real provider.
        var sandboxes = new ShellExitCodeOverrideSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance),
            exitCode: 125,
            stderr: "No .NET solution or project file was found after marker detection.");
        var verifier = new SandboxRequiredBuildVerifier(
            sandboxes,
            gitHost,
            new PipelineOptions { SandboxImageReference = "ignored" });

        var item = NewItem("feature/no-build-target") with { State = WorkItemState.WorkComplete };
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await CommitToBareBranchAsync(
            gitHost.GetRepoPath(repoId),
            item.WorkBranch!,
            "ok.txt",
            "ok\n",
            "branch exists");

        var result = await verifier.VerifyAsync(new RequiredBuildVerificationRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            SandboxPolicy = new RequiredBuildSandboxPolicy(),
            RepositoryId = repoId,
            BaseBranch = item.BaseBranch,
            WorkBranch = item.WorkBranch!,
            Phase = "audit",
        }, CancellationToken.None);

        Assert.Equal(RequiredBuildVerificationStatus.Unavailable, result.Status);
        Assert.Contains("could not verify required build", result.Reason);
        Assert.Contains("no .NET solution or project file", result.Reason);
        Assert.NotEqual(RequiredBuildVerificationStatus.Passed, result.Status);
    }

    [Fact]
    public async Task RequiredBuildGate_RecursiveSolutionDiscovery_BuildsNonRootSolution()
    {
        // Fallback target discovery branch: no root .slnx/.sln exists,
        // but a nested solution does. The build script must find it via
        // the recursive solution discovery branch and build it. Without
        // this test the recursive fallback could regress silently.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddNestedSolutionAsync(seed);
        var fakeDotnet = await CreateFlexibleFakeDotnetAsync();
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var verifier = new SandboxRequiredBuildVerifier(
            new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment),
            gitHost,
            new PipelineOptions { SandboxImageReference = "ignored" });

        var item = NewItem("feature/nested-solution") with { State = WorkItemState.WorkComplete };
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await CommitToBareBranchAsync(
            gitHost.GetRepoPath(repoId),
            item.WorkBranch!,
            "ok.txt",
            "ok\n",
            "branch exists");

        var result = await verifier.VerifyAsync(new RequiredBuildVerificationRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            SandboxPolicy = new RequiredBuildSandboxPolicy(),
            RepositoryId = repoId,
            BaseBranch = item.BaseBranch,
            WorkBranch = item.WorkBranch!,
            Phase = "audit",
        }, CancellationToken.None);

        Assert.Equal(RequiredBuildVerificationStatus.Passed, result.Status);
        var dotnetInvocations = await File.ReadAllLinesAsync(fakeDotnet.LogPath);
        Assert.Contains(dotnetInvocations, line => line.Contains("nested/Nested.slnx"));
    }

    [Fact]
    public async Task RequiredBuildGate_NestedSolutionOnly_StillBuildsUnregisteredTestProject()
    {
        // Regression: the build script's test-project enrichment must run
        // whenever ANY solution (root or nested) drives target discovery.
        // A nested .sln may not include every test project, so the gate
        // has to append test/*tests/*.csproj or a broken test project
        // would slip through. Prior gating on `root_solutions` skipped this
        // step for nested-only repos and let a non-compiling unregistered
        // test project still pass the required build.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddNestedSolutionWithUnregisteredTestProjectAsync(seed);
        var fakeDotnet = await CreateFlexibleFakeDotnetAsync();
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var verifier = new SandboxRequiredBuildVerifier(
            new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment),
            gitHost,
            new PipelineOptions { SandboxImageReference = "ignored" });

        var item = NewItem("feature/nested-solution-with-unregistered-tests") with { State = WorkItemState.WorkComplete };
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await CommitToBareBranchAsync(
            gitHost.GetRepoPath(repoId),
            item.WorkBranch!,
            "ok.txt",
            "ok\n",
            "branch exists");

        var result = await verifier.VerifyAsync(new RequiredBuildVerificationRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            SandboxPolicy = new RequiredBuildSandboxPolicy(),
            RepositoryId = repoId,
            BaseBranch = item.BaseBranch,
            WorkBranch = item.WorkBranch!,
            Phase = "audit",
        }, CancellationToken.None);

        Assert.Equal(RequiredBuildVerificationStatus.Passed, result.Status);
        var dotnetInvocations = await File.ReadAllLinesAsync(fakeDotnet.LogPath);
        Assert.Contains(dotnetInvocations, line => line.Contains("nested/Nested.slnx"));
        Assert.Contains(dotnetInvocations, line => line.Contains("tests/Unregistered.Tests.csproj"));
    }

    [Fact]
    public async Task RequiredBuildGate_CsprojOnlyFallback_BuildsAllCsprojWhenNoSolutionExists()
    {
        // Fallback target discovery branch: no .slnx/.sln exists at all,
        // but .csproj files do. The build script must discover them via
        // the csproj-only fallback and build each one. Without this test
        // the .csproj-only fallback branch is not exercised.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddCsprojOnlyMarkerAsync(seed);
        var fakeDotnet = await CreateFlexibleFakeDotnetAsync();
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var verifier = new SandboxRequiredBuildVerifier(
            new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment),
            gitHost,
            new PipelineOptions { SandboxImageReference = "ignored" });

        var item = NewItem("feature/csproj-only") with { State = WorkItemState.WorkComplete };
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await CommitToBareBranchAsync(
            gitHost.GetRepoPath(repoId),
            item.WorkBranch!,
            "ok.txt",
            "ok\n",
            "branch exists");

        var result = await verifier.VerifyAsync(new RequiredBuildVerificationRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            SandboxPolicy = new RequiredBuildSandboxPolicy(),
            RepositoryId = repoId,
            BaseBranch = item.BaseBranch,
            WorkBranch = item.WorkBranch!,
            Phase = "audit",
        }, CancellationToken.None);

        Assert.Equal(RequiredBuildVerificationStatus.Passed, result.Status);
        var dotnetInvocations = await File.ReadAllLinesAsync(fakeDotnet.LogPath);
        Assert.Contains(dotnetInvocations, line => line.Contains("src/Solo/Solo.csproj"));
    }

    [Fact]
    public async Task AuditPass_CannotReachAuditPassed_WhenRequiredBuildCannotRun()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnet = await CreateUnavailableDotnetAsync();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new PassingAuditor()],
            maxAuditIterations: 1,
            sandboxProvider: new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment));

        var item = NewItem("feature/audit-no-dotnet") with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "ok.txt", "ok\n", "branch exists");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("could not verify required build", final.LastError);
        Assert.Contains("dotnet is not available", final.LastError);
        Assert.Equal("infrastructure", final.FailureKind);
        Assert.NotEqual(WorkItemState.AuditPassed, final.State);
    }

    [Fact]
    public async Task WorkPhase_BuildVerifierUnavailable_FailsAsInfrastructure_NotAuditPassed()
    {
        // Coverage gap repro for the work/rework gate. Audit-time
        // Unavailable handling is exercised elsewhere; this test drives the
        // same Unavailable code path through the initial work pass so the
        // verifier-unavailable failure mode is locked in for the work phase
        // too: an Unavailable verifier during a fresh work pickup must fail
        // the item as infrastructure rather than letting work completion
        // proceed or surface a generic "no changes" error.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var verifier = new TestRequiredBuildVerifier(
            RequiredBuildProbeResult.Applies,
            RequiredBuildVerificationResult.Unavailable(
                "could not verify required build: sandbox cannot launch (CI infra)"));
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            requiredBuildVerifier: verifier);

        var item = NewItem("feature/work-verifier-unavailable");
        tp.Agent.WorkPlan.Enqueue(new FileWrite("initial.txt", "initial\n"));

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal("infrastructure", final.FailureKind);
        Assert.Contains("could not verify required build", final.LastError);
        Assert.Contains("sandbox cannot launch", final.LastError);
        Assert.NotEqual(WorkItemState.WorkComplete, final.State);
        Assert.NotEqual(WorkItemState.AuditPassed, final.State);
        Assert.True(verifier.VerifyCalls >= 1,
            $"expected the work-phase build gate to call VerifyAsync; observed {verifier.VerifyCalls}");
    }

    [Fact]
    public async Task PreemptResumeRework_NoChangeOnBrokenCheckpoint_DoesNotReachAuditPassedOrDone()
    {
        // Coverage on the resumingPreempt branch of RunAgentPhaseAsync. When
        // a Reworking item resumes from a PreemptCheckpoint and the resumed
        // agent produces no new commit, the broken branch must NOT silently
        // walk through WorkComplete → AuditPassed → merge.
        //
        // Under the unified rework-failure policy, the rework build gate no
        // longer terminal-fails — it defers to the audit loop, which picks
        // the same non-compile up as a blocking finding through
        // RunForAuditAsync. With maxAuditIterations=1 and no further
        // rework-agent capacity (the WorkPlan is empty), the audit ceiling
        // converts a single failed iteration into AuditFailed (no
        // convergence yet) rather than terminal-failing with failureKind=build.
        // The invariant under test is that the item never reaches
        // AuditPassed/Done with a broken build.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnet = await CreateFakeDotnetAsync();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            maxAuditIterations: 1,
            sandboxProvider: new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment));

        // Non-default branch name keeps the pickup-time rebase out of the
        // way (IsPickupRebaseOwnedWorkBranch matches only "codeybox/{id8}").
        // Entering at Reworking + PreemptCheckpoint forces the
        // resumingPreempt && entry==Reworking branch in PipelineRunner.
        var item = NewItem("feature/preempt-rework-broken") with
        {
            State = WorkItemState.Reworking,
        };
        item = item with { PreemptCheckpoint = $"refs/heads/codeybox/preempt/{item.Id}" };

        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await PushPreemptCheckpointWithFileAsync(
            barePath,
            item.WorkBranch!,
            item.PreemptCheckpoint!,
            "build.fail",
            "broken\n",
            "broken checkpoint awaiting resume");

        // Agent re-writes the same content the checkpoint already has, so
        // `git diff --cached --quiet` exits 0 and the resume path observes
        // shaBefore == shaAfter — the precise condition under which the
        // resumingPreempt build gate must still see the broken state.
        tp.Agent.WorkPlan.Enqueue(new FileWrite("build.fail", "broken\n"));

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        // The agent makes no commit, so the work branch tip never moves;
        // with a single audit iteration the convergence history has < 2
        // entries, so HasAuditConvergenceProgress returns false and the
        // audit ceiling MUST terminal-fail (AuditFailed) rather than park
        // for operator review. Accepting NeedsOperatorInput here would
        // make this test pass under a regression that parks every
        // non-compiling rework at the budget ceiling regardless of
        // convergence — exactly the fail-if-no-progress half of the new
        // policy that needs to be pinned.
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.NotEqual("build", final.FailureKind);
        Assert.Contains("Audit did not pass after 1 iterations", final.LastError!);
        Assert.Contains("required build failed", final.LastError!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SandboxRequiredBuildVerifier_WorkOnBase_AppliesWhenWorkBranchHasMarkers()
    {
        // Work-on-base path coverage: baseBranch == workBranch. The verifier
        // must decide applicability solely from the work-branch markers
        // (no base/work comparison) and still enforce the gate when those
        // markers are present.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var verifier = new SandboxRequiredBuildVerifier(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance),
            gitHost,
            new PipelineOptions { SandboxImageReference = "ignored" });

        var workOnBaseItem = NewItem("main") with { BaseBranch = "main" };
        var repoId = await gitHost.EnsureRepositoryAsync(workOnBaseItem.Id, seed, workOnBaseItem.BaseBranch);

        var applyingProbe = await verifier.ProbeAsync(new RequiredBuildProbeRequest
        {
            WorkItemId = workOnBaseItem.Id,
            ProjectId = workOnBaseItem.ProjectId,
            RepositoryId = repoId,
            BaseBranch = workOnBaseItem.BaseBranch,
            WorkBranch = workOnBaseItem.WorkBranch!,
        }, CancellationToken.None);
        Assert.Equal(RequiredBuildProbeStatus.Applies, applyingProbe.Status);

        // Same identity, but the seed has no .NET markers — the verifier
        // must report NotApplicable when the single (work == base) branch
        // carries no markers, never silently enforce or skip.
        var emptySeed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var emptyHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var emptyVerifier = new SandboxRequiredBuildVerifier(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance),
            emptyHost,
            new PipelineOptions { SandboxImageReference = "ignored" });
        var emptyItem = NewItem("main") with { BaseBranch = "main" };
        var emptyRepoId = await emptyHost.EnsureRepositoryAsync(emptyItem.Id, emptySeed, emptyItem.BaseBranch);
        var notApplyingProbe = await emptyVerifier.ProbeAsync(new RequiredBuildProbeRequest
        {
            WorkItemId = emptyItem.Id,
            ProjectId = emptyItem.ProjectId,
            RepositoryId = emptyRepoId,
            BaseBranch = emptyItem.BaseBranch,
            WorkBranch = emptyItem.WorkBranch!,
        }, CancellationToken.None);
        Assert.Equal(RequiredBuildProbeStatus.NotApplicable, notApplyingProbe.Status);
    }

    [Fact]
    public async Task AuditPassedResume_CannotMerge_WhenRequiredBuildFails()
    {
        // The retry-from-audit-passed path skips the audit loop. Without an
        // explicit gate it would walk a non-compiling branch into merge.
        // This test enters the pipeline at AuditPassed with a build-broken
        // work branch and asserts the runner refuses to proceed: the item
        // is demoted to AuditFailed citing the required build failure
        // instead of advancing to Merging / Merged.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnet = await CreateFakeDotnetAsync();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            maxAuditIterations: 1,
            sandboxProvider: new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment));

        var item = NewItem("feature/audit-passed-resume-broken") with { State = WorkItemState.AuditPassed };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "build.fail", "broken\n", "broken branch already past audit");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Contains("required build failed", final.LastError);
        Assert.Contains("AuditPassed resume", final.LastError);
        Assert.NotEqual(WorkItemState.Merging, final.State);
        Assert.NotEqual(WorkItemState.Merged, final.State);
        Assert.NotEqual(WorkItemState.Done, final.State);
    }

    [Fact]
    public async Task AuditPassedResume_BuildVerifierUnavailable_FailsAsInfrastructure()
    {
        // Companion test to the AuditPassed-resume build gate above: when
        // the verifier itself cannot execute, the resume path must defer
        // / fail with an infrastructure reason — never silently fall
        // through to merge against an unverified branch.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var verifier = new TestRequiredBuildVerifier(
            RequiredBuildProbeResult.Unavailable("sandbox provisioning failed"),
            RequiredBuildVerificationResult.Unavailable("verify should not run"));
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            maxAuditIterations: 1,
            requiredBuildVerifier: verifier);

        var item = NewItem("feature/audit-passed-resume-unavailable") with { State = WorkItemState.AuditPassed };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await CommitToBareBranchAsync(
            tp.GitHost.GetRepoPath(repoId),
            item.WorkBranch!,
            "ok.txt",
            "ok\n",
            "branch exists");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal("infrastructure", final.FailureKind);
        Assert.Contains("could not verify required build", final.LastError);
        Assert.NotEqual(WorkItemState.Merging, final.State);
        Assert.NotEqual(WorkItemState.Merged, final.State);
        Assert.NotEqual(WorkItemState.Done, final.State);
    }

    [Fact]
    public async Task AuditPassedResume_ProbeAppliesButVerifyUnavailable_FailsAsInfrastructure()
    {
        // Covers the missing AuditPassed-resume path where the probe says
        // the gate applies BUT VerifyAsync returns Unavailable. The other
        // resume-unavailable test covers Probe-Unavailable; this one drives
        // Verify-Unavailable so a verifier that probes successfully but
        // can't actually execute (e.g. sandbox launch failed AFTER the
        // probe) still demotes the resume to infrastructure failure
        // instead of silently advancing to merge.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var verifier = new TestRequiredBuildVerifier(
            RequiredBuildProbeResult.Applies,
            RequiredBuildVerificationResult.Unavailable(
                "could not verify required build: sandbox launch failed mid-verify"));
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            maxAuditIterations: 1,
            requiredBuildVerifier: verifier);

        var item = NewItem("feature/audit-passed-resume-verify-unavailable") with { State = WorkItemState.AuditPassed };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await CommitToBareBranchAsync(
            tp.GitHost.GetRepoPath(repoId),
            item.WorkBranch!,
            "ok.txt",
            "ok\n",
            "branch exists");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal("infrastructure", final.FailureKind);
        Assert.Contains("could not verify required build", final.LastError);
        Assert.Contains("sandbox launch failed", final.LastError);
        Assert.NotEqual(WorkItemState.Merging, final.State);
        Assert.NotEqual(WorkItemState.Merged, final.State);
        Assert.NotEqual(WorkItemState.Done, final.State);
        Assert.True(verifier.VerifyCalls >= 1,
            $"expected the AuditPassed-resume gate to call VerifyAsync; observed {verifier.VerifyCalls}");
    }

    [Fact]
    public async Task RequiredBuildVerification_BuildScriptExceedsTimeout_SurfacesAsBuildFinding_NotAuditPassed()
    {
        // The verifier timeout starts only after the sandbox is created, but
        // branch-controlled build scripts must still be bounded. This sandbox
        // lets the in-VM git preparation succeed and then hangs the build exec
        // until the build-only timeout cancels it.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var hangingProvider = new HangingRequiredBuildSandboxProvider();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            maxAuditIterations: 1,
            sandboxProvider: hangingProvider,
            pipelineOptions: new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
                RequiredBuildVerificationTimeout = TimeSpan.FromMilliseconds(75),
            });

        var item = NewItem("feature/required-build-timeout") with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await CommitToBareBranchAsync(
            tp.GitHost.GetRepoPath(repoId),
            item.WorkBranch!,
            "ok.txt",
            "ok\n",
            "branch exists");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.NotEqual("infrastructure", final.FailureKind);
        Assert.Contains("required build failed", final.LastError);
        Assert.NotEqual(WorkItemState.AuditPassed, final.State);
        Assert.NotEqual(WorkItemState.Merging, final.State);
        Assert.NotEqual(WorkItemState.Done, final.State);
        Assert.True(hangingProvider.ObservedBuildCancellation,
            "build exec never observed cancellation, so the build timeout token was not linked into the build script");

        var progress = await tp.Store.GetAuditProgressAsync(item.Id, workAttemptStartedAt: null);
        var iteration = Assert.Single(progress);
        Assert.Contains(
            iteration.BlockingFindingsDetails,
            f => f.AuditorName == RequiredBuildGateIdentity.AuditorName
                && f.Title.Contains("required build failed", StringComparison.OrdinalIgnoreCase)
                && f.Description.Contains("exceeded the required-build verification timeout", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RequiredBuildVerification_SandboxAdmissionWaitIsExcludedFromBuildTimeout()
    {
        // A saturated sandbox admission gate is queueing, not a build hang.
        // Hold the only admission token past RequiredBuildVerificationTimeout,
        // then release it; the required build should still get its full
        // build-only budget after CreateAsync returns.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var provider = SandboxAdmissionControlledProvider.Wrap(
            new PassingRequiredBuildSandboxProvider(),
            maxConcurrentSandboxes: 1,
            NullLogger.Instance);
        var timeout = TimeSpan.FromMilliseconds(75);
        var verifier = new SandboxRequiredBuildVerifier(
            provider,
            gitHost,
            new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
                RequiredBuildVerificationTimeout = timeout,
            });
        var gate = new RequiredBuildGate(verifier, persistReport: null);

        var item = NewItem("feature/admission-wait-excluded") with { State = WorkItemState.WorkComplete };
        var project = NewProject(item);
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = gitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "ok.txt", "ok\n", "branch exists");

        var occupied = await provider.CreateAsync(new SandboxSpec { ImageReference = "ignored" }, CancellationToken.None);
        try
        {
            var verifyTask = gate.EnforceForWorkPhaseAsync(
                item,
                project,
                repoId,
                item.BaseBranch!,
                item.WorkBranch!,
                agentPhase: "work",
                RequiredBuildPolicy.Terminal,
                CancellationToken.None);

            await Task.Delay(TimeSpan.FromMilliseconds(200));
            Assert.False(verifyTask.IsCompleted);

            await occupied.DisposeAsync();
            var outcome = await verifyTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(RequiredBuildWorkPhaseOutcome.PassedOrSkipped, outcome);
        }
        finally
        {
            await occupied.DisposeAsync();
        }
    }

    [Fact]
    public async Task AuditPass_CannotReachAuditPassed_WhenWorkBranchDeletesOnlyPlainSourceCsproj()
    {
        // Csproj-only base with a single plain source .csproj — deleting it
        // must fail the gate. The csproj-only fallback in the build script
        // would otherwise build whatever .csproj remains (here, nothing),
        // and the applicability check has to catch that.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddCsprojOnlyMarkerAsync(seed);
        var fakeDotnet = await CreateFlexibleFakeDotnetAsync();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            maxAuditIterations: 1,
            sandboxProvider: new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment));

        var item = NewItem("feature/deletes-plain-csproj") with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await DeleteFromBareBranchAsync(
            tp.GitHost.GetRepoPath(repoId),
            item.WorkBranch!,
            "delete the only plain source csproj",
            "src/Solo/Solo.csproj");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Contains("required build failed", final.LastError);
        Assert.NotEqual(WorkItemState.AuditPassed, final.State);
    }

    [Fact]
    public async Task AuditPass_CannotReachAuditPassed_WhenCsprojOnlyRepoDropsOneProductionProject()
    {
        // Csproj-only base (no .sln/.slnx) with multiple non-test projects:
        // deleting one production .csproj while leaving the other behind
        // must fail the gate. workHasMarkers stays true, and the deleted
        // file is neither a solution nor a test project, so unless every
        // base .csproj is "required" in csproj-only mode the verifier would
        // silently narrow the build surface to just the surviving project
        // and let a non-compiling deletion reach AuditPassed.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddTwoNonTestCsprojOnlyMarkersAsync(seed);
        var fakeDotnet = await CreateFakeDotnetAsync();
        var captureStore = new CapturingAuditReportStore();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            maxAuditIterations: 1,
            sandboxProvider: new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment),
            auditReportStore: captureStore);

        var item = NewItem("feature/drops-one-production-csproj") with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await DeleteFromBareBranchAsync(
            tp.GitHost.GetRepoPath(repoId),
            item.WorkBranch!,
            "delete one production csproj, keep the other",
            "src/Alpha/Alpha.csproj");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Contains("required build failed", final.LastError);
        Assert.NotEqual(WorkItemState.AuditPassed, final.State);

        var report = Assert.Single(
            captureStore.Reports,
            r => r.AuditorName == RequiredBuildGateIdentity.AuditorName);
        Assert.Equal(AuditSeverity.Error.ToString(), report.WorstSeverity);
        Assert.NotNull(report.RawOutput);
        Assert.Contains("deleted or moved", report.RawOutput);
        Assert.Contains("src/Alpha/Alpha.csproj", report.RawOutput);
    }

    [Fact]
    public async Task RequiredBuildGate_DefaultBaseFallback_VerifierResolvesDefaultBranchWhenBaseBranchOmitted()
    {
        // BaseBranch null/blank on the probe/verify request must trigger
        // GetDefaultBranchAsync resolution before marker comparison. A
        // regression that validated null, skipped the fallback, or compared
        // against the wrong base would not have been caught by tests that
        // always pass an explicit BaseBranch.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var recordingHost = new RecordingDefaultBranchGitHost(gitHost);
        var fakeDotnet = await CreateFakeDotnetAsync();
        var verifier = new SandboxRequiredBuildVerifier(
            new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment),
            recordingHost,
            new PipelineOptions { SandboxImageReference = "ignored" });

        var item = NewItem("feature/default-base-fallback") with { State = WorkItemState.WorkComplete };
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await CommitToBareBranchAsync(
            gitHost.GetRepoPath(repoId),
            item.WorkBranch!,
            "ok.txt",
            "ok\n",
            "branch exists");

        var probe = await verifier.ProbeAsync(new RequiredBuildProbeRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            RepositoryId = repoId,
            BaseBranch = null,
            WorkBranch = item.WorkBranch!,
        }, CancellationToken.None);
        Assert.Equal(RequiredBuildProbeStatus.Applies, probe.Status);
        Assert.True(recordingHost.GetDefaultBranchCalls >= 1,
            $"expected the verifier to call GetDefaultBranchAsync when BaseBranch was null; observed {recordingHost.GetDefaultBranchCalls}");

        recordingHost.GetDefaultBranchCalls = 0;
        var verifyResult = await verifier.VerifyAsync(new RequiredBuildVerificationRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            SandboxPolicy = new RequiredBuildSandboxPolicy(),
            RepositoryId = repoId,
            BaseBranch = null,
            WorkBranch = item.WorkBranch!,
            Phase = "audit",
        }, CancellationToken.None);
        Assert.Equal(RequiredBuildVerificationStatus.Passed, verifyResult.Status);
        Assert.True(recordingHost.GetDefaultBranchCalls >= 1,
            $"expected VerifyAsync to resolve the default branch when BaseBranch was null; observed {recordingHost.GetDefaultBranchCalls}");
    }

    [Fact]
    public async Task RequiredBuildGate_VerifyAsync_PropagatesSandboxPolicyToSandboxSpec()
    {
        // The orchestrator pre-resolves the audit-tool network profile and
        // baseline image ref and hands them to the verifier through
        // RequiredBuildSandboxPolicy. The verifier must forward those onto
        // the SandboxSpec it asks the provider to materialise — otherwise
        // the required build runs in the wrong network profile / baseline.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var fakeDotnet = await CreateFakeDotnetAsync();
        var capturingProvider = new SpecCapturingSandboxProvider(
            new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment));
        var verifier = new SandboxRequiredBuildVerifier(
            capturingProvider,
            gitHost,
            new PipelineOptions { SandboxImageReference = "ignored" });

        var item = NewItem("feature/policy-propagates") with { State = WorkItemState.WorkComplete };
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await CommitToBareBranchAsync(
            gitHost.GetRepoPath(repoId),
            item.WorkBranch!,
            "ok.txt",
            "ok\n",
            "branch exists");

        var result = await verifier.VerifyAsync(new RequiredBuildVerificationRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            SandboxPolicy = new RequiredBuildSandboxPolicy
            {
                NetworkProfile = "audit-tool-test-profile",
                BaselineImageRef = "baseline-pin:abcdef0123",
            },
            RepositoryId = repoId,
            BaseBranch = item.BaseBranch,
            WorkBranch = item.WorkBranch!,
            Phase = "audit",
        }, CancellationToken.None);

        Assert.Equal(RequiredBuildVerificationStatus.Passed, result.Status);
        var spec = Assert.Single(capturingProvider.CapturedSpecs);
        Assert.Equal("audit-tool-test-profile", spec.Network.ProfileName);
        Assert.Equal("baseline-pin:abcdef0123", spec.BaselineImageRef);
        Assert.Equal(item.Id, spec.TimingWorkItemId);
        Assert.Equal(item.Id.ToString(), spec.Environment[SandboxConventions.WorkItemIdEnvironmentVariable]);
    }

    [Fact]
    public async Task RequiredBuildGate_DotnetNotFound_PersistsAuditReportWithOutputViaOrchestrator()
    {
        // The Unavailable-with-output report-persistence branch:
        // verifier hit dotnet-not-found (or no-target), so result.Status is
        // Unavailable but result.Output carries the script's stderr. The
        // canonical persistence path must still write an AuditReport so
        // operators can see why the build gate could not run. A regression
        // that dropped this branch (e.g. by treating any Unavailable as
        // "do not persist") would let infra-degradation findings vanish.
        // The report must ALSO carry a distinct Unavailable finding so that
        // "auditor could not run" does not look like a clean pass in
        // audit-report views (worstSeverity=none would hide the failure).
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnet = await CreateUnavailableDotnetAsync();
        var captureStore = new CapturingAuditReportStore();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new PassingAuditor()],
            maxAuditIterations: 1,
            sandboxProvider: new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment),
            auditReportStore: captureStore);

        var item = NewItem("feature/dotnet-not-found-report") with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await CommitToBareBranchAsync(
            tp.GitHost.GetRepoPath(repoId),
            item.WorkBranch!,
            "ok.txt",
            "ok\n",
            "branch exists");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal("infrastructure", final.FailureKind);

        var report = Assert.Single(
            captureStore.Reports,
            r => r.AuditorName == RequiredBuildGateIdentity.AuditorName);
        Assert.Equal("shell", report.AuditorKind);
        Assert.Equal(AuditSeverity.Error.ToString(), report.WorstSeverity);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(AuditSeverity.Error.ToString(), finding.Severity);
        Assert.Contains("unavailable", finding.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("failed:", finding.Title, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(report.RawOutput);
        Assert.Contains("dotnet is not available", report.RawOutput);
    }

    [Fact]
    public async Task LocalGitHost_ListFilesEndingWithAsync_ScannedCeilingExceeded_ThrowsRatherThanProcessingUnboundedTree()
    {
        // Defensive bound on the TOTAL paths the streamed reader will inspect,
        // independent of how many actually match the suffix filter. Without
        // this cap, a branch-controlled tree could carry vastly more
        // non-matching files than matching ones and tie the pipeline worker
        // up reading git output indefinitely without ever hitting the match
        // cap — the exhaustion vector the LLM security reviewer flagged. The
        // probe runs in the audit-applicability path OUTSIDE the verification
        // timeout, so the gate cannot rely on a deadline to break the loop.
        //
        // Drop the ceiling to a small value so the test can exercise the cap
        // with a handful of files instead of the production 500k.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        for (var i = 0; i < 24; i++)
            await File.WriteAllTextAsync(Path.Combine(seed, $"junk-{i:D2}.txt"), "x");
        await TestSupport.RunGit(seed, "add", "-A");
        await TestSupport.RunGit(seed, "commit", "-m", "many non-matching files");

        var gitHost = new LocalGitHost(
            new LocalGitHostOptions
            {
                RootDirectory = Path.Combine(_workspace, "repos-scan-" + Guid.NewGuid().ToString("N")[..8]),
                ListFilesEndingScannedPathCeiling = 8,
            },
            NullLogger<LocalGitHost>.Instance);
        var item = NewItem("feature/scan-ceiling") with { BaseBranch = "main" };
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gitHost.ListFilesEndingWithAsync(repoId, item.BaseBranch!, new[] { ".csproj" }, maxResults: 8192));
        Assert.Contains("scanned more than 8 paths", ex.Message);
        Assert.Contains("too large to inspect safely", ex.Message);
    }

    [Fact]
    public async Task LocalGitHost_ListFilesEndingWithAsync_CapExceeded_ThrowsAndDoesNotReturnPartialMatches()
    {
        // Pin the streaming cap behavior of the LocalGitHost ListFilesEndingWithAsync
        // override that the required-build probe relies on. If this method
        // silently truncated instead of throwing, the verifier would inspect
        // partial marker data and could pass/skip a branch that actually has
        // a larger marker set than the cap allows — the audit failure mode the
        // probe's Unavailable fallback exists to prevent.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        const int markerCount = 12;
        const int maxResults = 5;
        for (var i = 0; i < markerCount; i++)
        {
            var projectPath = Path.Combine(seed, $"proj-{i:D2}.csproj");
            await File.WriteAllTextAsync(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
            await TestSupport.RunGit(seed, "add", $"proj-{i:D2}.csproj");
        }
        await TestSupport.RunGit(seed, "commit", "-m", "many marker files");

        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-cap-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var item = NewItem("feature/cap-overflow") with { BaseBranch = "main" };
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gitHost.ListFilesEndingWithAsync(repoId, item.BaseBranch!, new[] { ".csproj" }, maxResults));
        Assert.Contains("more than 5 matching paths", ex.Message);
        Assert.Contains("output cap exceeded", ex.Message);
    }

    [Fact]
    public async Task LocalGitHost_ListFilesEndingWithAsync_Cancelled_KillsAndReapsChildProcess()
    {
        // Pin the cancellation cleanup path in ListFilesEndingWithAsync's
        // streamed ls-tree reader. The catch arm has to Kill the child
        // before rethrowing so the finally's WaitForExitAsync can reap it
        // — without that, a cancelled probe would leave the git process
        // wedged on its stdout pipe and the wait would hang. Because the
        // finally drains stderr and waits for exit with CancellationToken.None,
        // a regression that drops the kill turns this test into a hang
        // (xUnit will time out) rather than a clean failure, so the
        // existence of this test itself is what locks the invariant in.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        const int markerCount = 16;
        for (var i = 0; i < markerCount; i++)
        {
            var projectPath = Path.Combine(seed, $"proj-{i:D2}.csproj");
            await File.WriteAllTextAsync(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
            await TestSupport.RunGit(seed, "add", $"proj-{i:D2}.csproj");
        }
        await TestSupport.RunGit(seed, "commit", "-m", "many marker files");

        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-cancel-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var item = NewItem("feature/cancel-during-read") with { BaseBranch = "main" };
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // The pre-cancelled token surfaces inside ReadLineAsync, exercising
        // the catch arm. If the method ever returns or throws, the finally
        // already drained stderr and awaited the child — so reaching this
        // assertion at all proves the kill+reap path is wired correctly.
        var call = gitHost.ListFilesEndingWithAsync(
            repoId, item.BaseBranch!, new[] { ".csproj" }, 1000, cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call);
    }

    [Fact]
    public async Task SandboxRequiredBuildVerifier_MarkerCapOverflow_ReportsUnavailable()
    {
        // End-to-end: when the underlying marker listing exceeds the per-branch
        // cap (signalled as an InvalidOperationException by IGitHost contract),
        // the verifier must convert that into Unavailable so the build gate
        // defers instead of inspecting partial data. Without this, the probe
        // would silently fall back to "no markers found" and the gate would
        // either skip or fail a branch incorrectly.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-cap-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var capExceededHost = new CapExceededListFilesGitHost(gitHost,
            "tree listing produced more than 8192 matching paths (output cap exceeded)");
        var verifier = new SandboxRequiredBuildVerifier(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance),
            capExceededHost,
            new PipelineOptions { SandboxImageReference = "ignored" });

        var item = NewItem("feature/cap-overflow-e2e") with { State = WorkItemState.WorkComplete };
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = gitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "ok.txt", "ok\n", "branch exists");

        var probe = await verifier.ProbeAsync(new RequiredBuildProbeRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            RepositoryId = repoId,
            BaseBranch = item.BaseBranch,
            WorkBranch = item.WorkBranch!,
        }, CancellationToken.None);

        Assert.Equal(RequiredBuildProbeStatus.Unavailable, probe.Status);
        Assert.Contains("output cap exceeded", probe.Reason);

        var verify = await verifier.VerifyAsync(new RequiredBuildVerificationRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            SandboxPolicy = new RequiredBuildSandboxPolicy(),
            RepositoryId = repoId,
            BaseBranch = item.BaseBranch,
            WorkBranch = item.WorkBranch!,
            Phase = "audit",
        }, CancellationToken.None);
        Assert.Equal(RequiredBuildVerificationStatus.Unavailable, verify.Status);
        Assert.Contains("output cap exceeded", verify.Reason);
    }

    [Fact]
    public async Task DefaultIGitHost_ListFilesEndingWithAsync_FiltersCaseInsensitivelyAndCallsListFilesAsyncWithNullPrefix()
    {
        // The IGitHost default implementation of ListFilesEndingWithAsync is
        // the host-agnostic fallback used by every IGitHost subtype that does
        // not override it (test fakes, future remote hosts). Cover the
        // contract end-to-end against a host whose only override is
        // ListFilesAsync: case-insensitive suffix filtering, null prefix
        // delegation, and that paths NOT matching any suffix are dropped.
        IGitHost host = new FixedListFilesGitHost(
            (repoId, treeish, prefix) =>
            {
                Assert.Null(prefix);
                return new[]
                {
                    "src/App/App.csproj",
                    "src/App/Readme.md",
                    "tests/App.Tests/App.Tests.CSPROJ", // upper-case suffix
                    "tools/dotnet.cake",
                };
            });

        var matches = await host.ListFilesEndingWithAsync(
            "repo", "tree", new[] { ".csproj" }, maxResults: 50);

        Assert.Equal(
            new[] { "src/App/App.csproj", "tests/App.Tests/App.Tests.CSPROJ" },
            matches.OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task DefaultIGitHost_ListFilesEndingWithAsync_ThrowsWhenCapExceeded()
    {
        // The default implementation must enforce the same cap contract as
        // the streaming override: throw when matches exceed maxResults so
        // callers (e.g. the build-marker probe) can treat the tree as too
        // large to inspect rather than silently truncating.
        IGitHost host = new FixedListFilesGitHost(
            (_, _, _) => Enumerable.Range(0, 12).Select(i => $"proj-{i:D2}.csproj").ToArray());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.ListFilesEndingWithAsync("repo", "tree", new[] { ".csproj" }, maxResults: 5));
        Assert.Contains("more than 5 matching paths", ex.Message);
        Assert.Contains("output cap exceeded", ex.Message);
    }

    [Fact]
    public async Task DefaultIGitHost_ListFilesEndingWithAsync_RejectsInvalidArguments()
    {
        // Pin argument validation on the default fallback so a regression in
        // either the default impl OR the LocalGitHost override fails at the
        // boundary instead of leaking past as an empty result set.
        IGitHost host = new FixedListFilesGitHost((_, _, _) => Array.Empty<string>());

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            host.ListFilesEndingWithAsync("repo", "tree", null!, maxResults: 1));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            host.ListFilesEndingWithAsync("repo", "tree", Array.Empty<string>(), maxResults: 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            host.ListFilesEndingWithAsync("repo", "tree", new[] { ".cs" }, maxResults: 0));
    }

    [Fact]
    public async Task LocalGitHost_ListFilesEndingWithAsync_RejectsInvalidArguments()
    {
        // Direct coverage of the LocalGitHost override's own argument
        // validation. The IGitHost-default test above pins the fallback
        // path; this one pins the streaming override so a regression that
        // drops or inverts the LocalGitHost-side validation (including the
        // whitespace/empty per-entry check at line 663) fails here rather
        // than silently feeding an empty suffix into the git pipe and
        // returning a partial or empty result set.
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions
            {
                RootDirectory = Path.Combine(_workspace, "repos-args-" + Guid.NewGuid().ToString("N")[..8]),
            },
            NullLogger<LocalGitHost>.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            gitHost.ListFilesEndingWithAsync("repo", "tree", null!, maxResults: 1));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            gitHost.ListFilesEndingWithAsync("repo", "tree", Array.Empty<string>(), maxResults: 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            gitHost.ListFilesEndingWithAsync("repo", "tree", new[] { ".cs" }, maxResults: 0));
        // The whitespace/empty per-entry check is unique to the LocalGitHost
        // override — the IGitHost default doesn't enforce it, so this is the
        // only path that pins the invariant.
        var emptyEntry = await Assert.ThrowsAsync<ArgumentException>(() =>
            gitHost.ListFilesEndingWithAsync("repo", "tree", new[] { string.Empty }, maxResults: 1));
        Assert.Contains("non-empty", emptyEntry.Message);
        var whitespaceEntry = await Assert.ThrowsAsync<ArgumentException>(() =>
            gitHost.ListFilesEndingWithAsync("repo", "tree", new[] { "   " }, maxResults: 1));
        Assert.Contains("non-empty", whitespaceEntry.Message);
    }

    [Fact]
    public void PipelineRunner_Constructor_ThrowsArgumentNullException_WhenRequiredBuildVerifierIsNull()
    {
        // The composition root MUST wire IRequiredBuildVerifier. A silent
        // null-as-disabled fallback would let the AuditPassed gate decay
        // back to the pre-gate behavior (a non-compiling branch could pass
        // audit because no probe was wired), so the constructor enforces
        // the contract at boot. Without this test, a future regression
        // that swaps the throw for "if (verifier is null) skip the gate"
        // would not be caught.
        var gitRoot = Path.Combine(_workspace, "repos-ctor-null-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-ctor-null-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var registry = new AgentRegistry(new[] { new ScriptedAgent(new[] { MergeStrategy.RealMerge }) });
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "ctor-null test",
            RepositoryUrl = "ignored",
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
        });
        var composer = new ProjectAuditorComposer(new ScriptedAuditorCatalog(Array.Empty<IAuditor>()));

        var ex = Assert.Throws<ArgumentNullException>(() => new PipelineRunner(
            sandboxes,
            gitHost,
            registry,
            new StaticCredentialProvider(),
            new InMemoryPullRequestService(),
            projects,
            new TestUpstreamFactory(),
            composer,
            store,
            new NullWebhookDispatcher(),
            new PipelineOptions { SandboxImageReference = "ignored" },
            NullLogger<PipelineRunner>.Instance,
            requiredBuildVerifier: null));
        Assert.Equal("requiredBuildVerifier", ex.ParamName);
        Assert.Contains("composition root", ex.Message);
    }

    [Fact]
    public async Task LocalGitHost_ListFilesAsync_NullPrefix_ReturnsEntireTree()
    {
        // Direct coverage of the LocalGitHost.ListFilesAsync change that
        // accepts null/empty pathPrefix. The required-build probe uses
        // ListFilesEndingWithAsync, not this entrypoint, so a regression
        // that validates/rejects null on this public method would not be
        // caught by the verifier tests. Pin the contract here.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await File.WriteAllTextAsync(Path.Combine(seed, "root.txt"), "root\n");
        Directory.CreateDirectory(Path.Combine(seed, "sub"));
        await File.WriteAllTextAsync(Path.Combine(seed, "sub", "leaf.txt"), "leaf\n");
        await TestSupport.RunGit(seed, "add", "root.txt", "sub/leaf.txt");
        await TestSupport.RunGit(seed, "commit", "-m", "two files at two depths");

        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-null-prefix-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var item = NewItem("feature/null-prefix") with { BaseBranch = "main" };
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);

        var allWithNull = await gitHost.ListFilesAsync(repoId, item.BaseBranch!, pathPrefix: null);
        var allWithEmpty = await gitHost.ListFilesAsync(repoId, item.BaseBranch!, pathPrefix: string.Empty);

        Assert.Contains("root.txt", allWithNull);
        Assert.Contains("sub/leaf.txt", allWithNull);
        Assert.Equal(allWithNull.OrderBy(s => s).ToArray(), allWithEmpty.OrderBy(s => s).ToArray());
    }

    [Theory]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData("node_modules")]
    public async Task SandboxRequiredBuildVerifier_MarkerUnderIgnoredDirectory_DoesNotApply(string ignoredDir)
    {
        // The verifier's marker classifier skips paths under .git, bin, obj,
        // and node_modules so generated artifacts (e.g. a .csproj copied into
        // bin/, a build cache laying down a fake .slnx) cannot trick the gate
        // into running on a non-.NET branch or picking the wrong target. Cover
        // each ignored directory: a single marker-looking path under one of
        // these dirs (and nothing else) must produce NotApplicable.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var ignoredPath = Path.Combine(seed, ignoredDir, "Spurious.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(ignoredPath)!);
        await File.WriteAllTextAsync(ignoredPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        await TestSupport.RunGit(seed, "add", $"{ignoredDir}/Spurious.csproj");
        await TestSupport.RunGit(seed, "commit", "-m", $"spurious csproj under {ignoredDir}");

        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-ignored-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var verifier = new SandboxRequiredBuildVerifier(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance),
            gitHost,
            new PipelineOptions { SandboxImageReference = "ignored" });

        var item = NewItem("feature/ignored-marker-" + ignoredDir) with { BaseBranch = "main" };
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);

        var probe = await verifier.ProbeAsync(new RequiredBuildProbeRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            RepositoryId = repoId,
            BaseBranch = item.BaseBranch,
            WorkBranch = item.BaseBranch!,
        }, CancellationToken.None);
        Assert.Equal(RequiredBuildProbeStatus.NotApplicable, probe.Status);

        var verify = await verifier.VerifyAsync(new RequiredBuildVerificationRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            SandboxPolicy = new RequiredBuildSandboxPolicy(),
            RepositoryId = repoId,
            BaseBranch = item.BaseBranch,
            WorkBranch = item.BaseBranch!,
            Phase = "audit",
        }, CancellationToken.None);
        Assert.Equal(RequiredBuildVerificationStatus.Skipped, verify.Status);
    }

    /// <summary>
    /// Minimal IGitHost stub used to exercise the IGitHost-default
    /// implementation of ListFilesEndingWithAsync. Only ListFilesAsync is
    /// implemented; the rest throw so the default fallback's dependency
    /// surface stays pinned (any future default that reaches for another
    /// IGitHost method would surface here as a test failure rather than
    /// silently widening the contract).
    /// </summary>
    private sealed class FixedListFilesGitHost : IGitHost
    {
        private readonly Func<string, string, string?, IReadOnlyList<string>> _produce;

        public FixedListFilesGitHost(Func<string, string, string?, IReadOnlyList<string>> produce)
            => _produce = produce;

        public Task<IReadOnlyList<string>> ListFilesAsync(string repositoryId, string treeish, string? pathPrefix, CancellationToken ct = default)
            => Task.FromResult(_produce(repositoryId, treeish, pathPrefix));

        // Unused for these tests; throw so an accidental reach into other
        // IGitHost methods from a future default-impl change surfaces here.
        public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, string? baseBranch, CancellationToken ct = default)
            => throw new NotSupportedException();
        public SandboxRepositoryAccess GetSandboxAccess(string repositoryId) => throw new NotSupportedException();
        public Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task PushToUpstreamAsync(string repositoryId, string upstreamUrl, string branch,
            IReadOnlyDictionary<string, string> upstreamEnv,
            UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase,
            CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<(string DiffStat, string FullDiff)> GetDiffAsync(string repositoryId, string baseBranch, string workBranch, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class CapExceededListFilesGitHost(IGitHost inner, string capMessage) : DelegatingGitHost(inner)
    {
        public override Task<IReadOnlyList<string>> ListFilesEndingWithAsync(
            string repositoryId, string treeish, IReadOnlyList<string> filenameSuffixes,
            int maxResults, CancellationToken ct = default)
            => throw new InvalidOperationException(capMessage);
    }

    private sealed class RecordingDefaultBranchGitHost(IGitHost inner) : DelegatingGitHost(inner)
    {
        public int GetDefaultBranchCalls;

        public override Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref GetDefaultBranchCalls);
            return Inner.GetDefaultBranchAsync(repositoryId, ct);
        }
    }

    private sealed class SpecCapturingSandboxProvider(ISandboxProvider inner) : ISandboxProvider
    {
        public List<SandboxSpec> CapturedSpecs { get; } = [];

        public string Name => inner.Name;

        public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            CapturedSpecs.Add(spec);
            return await inner.CreateAsync(spec, ct);
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => inner.ListAllManagedAsync(ct);

        public Task DisposeLeakedAsync(string name, CancellationToken ct)
            => inner.DisposeLeakedAsync(name, ct);
    }

    private class PassingRequiredBuildSandboxProvider : ISandboxProvider
    {
        public string Name => "passing-required-build";

        public virtual Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            _ = spec;
            _ = ct;
            return Task.FromResult<ISandbox>(new PassingRequiredBuildSandbox());
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>(Array.Empty<ManagedSandboxInfo>());

        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class HangingRequiredBuildSandboxProvider : PassingRequiredBuildSandboxProvider
    {
        public bool ObservedBuildCancellation { get; private set; }

        public override Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            _ = spec;
            _ = ct;
            return Task.FromResult<ISandbox>(new HangingRequiredBuildSandbox(() => ObservedBuildCancellation = true));
        }
    }

    private sealed class HangingRequiredBuildPreparationSandboxProvider : PassingRequiredBuildSandboxProvider
    {
        public bool ObservedPreparationCancellation { get; private set; }

        public override Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            _ = spec;
            _ = ct;
            return Task.FromResult<ISandbox>(
                new HangingRequiredBuildPreparationSandbox(() => ObservedPreparationCancellation = true));
        }
    }

    private sealed class HangingRequiredBuildCheckoutSandboxProvider : PassingRequiredBuildSandboxProvider
    {
        public bool ObservedCheckoutCancellation { get; private set; }

        public override Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            _ = spec;
            _ = ct;
            return Task.FromResult<ISandbox>(
                new HangingRequiredBuildCheckoutSandbox(() => ObservedCheckoutCancellation = true));
        }
    }

    private class PassingRequiredBuildSandbox : ISandbox
    {
        public string Id { get; } = "required-build-" + Guid.NewGuid().ToString("N")[..8];

        public virtual Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            _ = exec;
            _ = ct;
            return Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class HangingRequiredBuildPreparationSandbox(Action onPreparationCancellation) : PassingRequiredBuildSandbox
    {
        public override async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (exec.Argv.Count >= 2 && exec.Argv[0] == "git" && exec.Argv[1] == "clone")
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException)
                {
                    onPreparationCancellation();
                    throw;
                }

                return new SandboxExecResult(0, "unreachable: git clone should be cancelled before returning", string.Empty);
            }

            return await base.ExecAsync(exec, ct);
        }
    }

    private sealed class HangingRequiredBuildCheckoutSandbox(Action onCheckoutCancellation) : PassingRequiredBuildSandbox
    {
        public override async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (exec.Argv.Count >= 5
                && exec.Argv[0] == "git"
                && exec.Argv.Contains("checkout", StringComparer.Ordinal))
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException)
                {
                    onCheckoutCancellation();
                    throw;
                }

                return new SandboxExecResult(0, "unreachable: git checkout should be cancelled before returning", string.Empty);
            }

            return await base.ExecAsync(exec, ct);
        }
    }

    private sealed class HangingRequiredBuildSandbox(Action onBuildCancellation) : PassingRequiredBuildSandbox
    {
        public override async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (exec.Argv.Count > 0 && exec.Argv[0] == "sh")
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException)
                {
                    onBuildCancellation();
                    throw;
                }

                return new SandboxExecResult(0, "unreachable: build should be cancelled before returning", string.Empty);
            }

            return await base.ExecAsync(exec, ct);
        }
    }

    private async Task ReplaceBaseMarkersWithLeafProjectAsync(string barePath, string branch)
    {
        var clone = Path.Combine(_workspace, "branch-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", barePath, clone);
        await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(clone, "config", "user.name", "Test");
        await TestSupport.RunGit(clone, "checkout", "-B", branch);
        await TestSupport.RunGit(clone, "rm", "--", "CodeyBox.slnx", "tests/CodeyBox.Tests.csproj");
        var leafDir = Path.Combine(clone, "tools", "trivial");
        Directory.CreateDirectory(leafDir);
        await File.WriteAllTextAsync(Path.Combine(leafDir, "trivial.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        await TestSupport.RunGit(clone, "add", "tools/trivial/trivial.csproj");
        await TestSupport.RunGit(clone, "commit", "-m", $"swap root solution for leaf project\n\n{CodeyBoxTrailers.CoAuthoredBy}");
        await TestSupport.RunGit(clone, "push", "origin", $"{branch}:{branch}");
    }

    private async Task<FakeDotnet> CreateFakeDotnetAsync()
    {
        var bin = Path.Combine(_workspace, "fake-dotnet-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(bin);
        var dotnet = Path.Combine(bin, "dotnet");
        var log = Path.Combine(_workspace, "fake-dotnet-" + Guid.NewGuid().ToString("N")[..8] + ".log");
        await File.WriteAllTextAsync(dotnet, """
            #!/bin/sh
            printf '%s\n' "$*" >> "$CODEYBOX_FAKE_DOTNET_LOG"
            if [ "$1" != "build" ]; then
              echo "unexpected dotnet command: $*" >&2
              exit 42
            fi
            case "$2" in
              ./CodeyBox.slnx|./tests/CodeyBox.Tests.csproj) ;;
              *)
                echo "unexpected dotnet build target: $2" >&2
                exit 43
                ;;
            esac
            if [ -f build.fail ]; then
              echo "src/Broken.cs(1,1): error CS1061: 'Broken' does not contain a definition" >&2
              exit 1
            fi
            echo "Build succeeded."
            exit 0
            """);
        MakeExecutable(dotnet);
        return new FakeDotnet(
            bin + Path.PathSeparator + "/usr/bin:/bin",
            log,
            new Dictionary<string, string> { ["CODEYBOX_FAKE_DOTNET_LOG"] = log });
    }

    private async Task<FakeDotnet> CreateMaliciousDotnetAsync()
    {
        var bin = Path.Combine(_workspace, "fake-dotnet-malicious-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(bin);
        var dotnet = Path.Combine(bin, "dotnet");
        var log = Path.Combine(_workspace, "fake-dotnet-malicious-" + Guid.NewGuid().ToString("N")[..8] + ".log");
        await File.WriteAllTextAsync(dotnet, """
            #!/bin/sh
            printf '%s\n' "$*" >> "$CODEYBOX_FAKE_DOTNET_LOG"
            if [ "$1" != "build" ]; then
              echo "unexpected dotnet command: $*" >&2
              exit 42
            fi
            case "$2" in
              ./CodeyBox.slnx|./tests/CodeyBox.Tests.csproj) ;;
              *)
                echo "unexpected dotnet build target: $2" >&2
                exit 43
                ;;
            esac
            tip=$(git rev-parse HEAD)
            repo=$(git remote get-url origin)
            printf 'origin=%s\n' "$repo" >> "$CODEYBOX_FAKE_DOTNET_LOG"
            printf 'origin_target=%s\n' "$(readlink "$repo" 2>/dev/null || true)" >> "$CODEYBOX_FAKE_DOTNET_LOG"
            git -C "$repo" update-ref refs/heads/main "$tip" || exit 44
            echo "Build succeeded."
            exit 0
            """);
        MakeExecutable(dotnet);
        return new FakeDotnet(
            bin + Path.PathSeparator + "/usr/bin:/bin",
            log,
            new Dictionary<string, string> { ["CODEYBOX_FAKE_DOTNET_LOG"] = log });
    }

    private async Task<FakeDotnet> CreateTestProjectFailingDotnetAsync()
    {
        var bin = Path.Combine(_workspace, "fake-dotnet-test-fail-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(bin);
        var dotnet = Path.Combine(bin, "dotnet");
        var log = Path.Combine(_workspace, "fake-dotnet-test-fail-" + Guid.NewGuid().ToString("N")[..8] + ".log");
        await File.WriteAllTextAsync(dotnet, """
            #!/bin/sh
            printf '%s\n' "$*" >> "$CODEYBOX_FAKE_DOTNET_LOG"
            if [ "$1" != "build" ]; then
              echo "unexpected dotnet command: $*" >&2
              exit 42
            fi
            case "$2" in
              ./CodeyBox.slnx)
                echo "Root solution build succeeded."
                exit 0
                ;;
              ./tests/CodeyBox.Tests.csproj)
                echo "tests/CodeyBox.Tests.csproj(1,1): error CS1061: test project compile error" >&2
                exit 1
                ;;
              *)
                echo "unexpected dotnet build target: $2" >&2
                exit 43
                ;;
            esac
            """);
        MakeExecutable(dotnet);
        return new FakeDotnet(
            bin + Path.PathSeparator + "/usr/bin:/bin",
            log,
            new Dictionary<string, string> { ["CODEYBOX_FAKE_DOTNET_LOG"] = log });
    }

    private async Task<FakeDotnet> CreateUnavailableDotnetAsync()
    {
        var bin = Path.Combine(_workspace, "fake-dotnet-unavailable-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(bin);
        var dotnet = Path.Combine(bin, "dotnet");
        var log = Path.Combine(_workspace, "fake-dotnet-unavailable-" + Guid.NewGuid().ToString("N")[..8] + ".log");
        await File.WriteAllTextAsync(dotnet, """
            #!/bin/sh
            printf '%s\n' "$*" >> "$CODEYBOX_FAKE_DOTNET_LOG"
            echo "dotnet is not available in the sandbox PATH" >&2
            exit 127
            """);
        MakeExecutable(dotnet);
        return new FakeDotnet(
            bin + Path.PathSeparator + "/usr/bin:/bin",
            log,
            new Dictionary<string, string> { ["CODEYBOX_FAKE_DOTNET_LOG"] = log });
    }

    /// <summary>
    /// Fake dotnet that accepts any build target. Used for the recursive
    /// solution and csproj-only fallback tests where the target file name
    /// varies.
    /// </summary>
    private async Task<FakeDotnet> CreateFlexibleFakeDotnetAsync()
    {
        var bin = Path.Combine(_workspace, "fake-dotnet-flex-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(bin);
        var dotnet = Path.Combine(bin, "dotnet");
        var log = Path.Combine(_workspace, "fake-dotnet-flex-" + Guid.NewGuid().ToString("N")[..8] + ".log");
        await File.WriteAllTextAsync(dotnet, """
            #!/bin/sh
            printf '%s\n' "$*" >> "$CODEYBOX_FAKE_DOTNET_LOG"
            if [ "$1" != "build" ]; then
              echo "unexpected dotnet command: $*" >&2
              exit 42
            fi
            echo "Build succeeded."
            exit 0
            """);
        MakeExecutable(dotnet);
        return new FakeDotnet(
            bin + Path.PathSeparator + "/usr/bin:/bin",
            log,
            new Dictionary<string, string> { ["CODEYBOX_FAKE_DOTNET_LOG"] = log });
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static async Task AddDotnetSolutionMarkerAsync(string repoPath)
    {
        await File.WriteAllTextAsync(Path.Combine(repoPath, "CodeyBox.slnx"), "# solution marker for required build tests\n");
        var testProjectPath = Path.Combine(repoPath, "tests", "CodeyBox.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(testProjectPath)!);
        await File.WriteAllTextAsync(testProjectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        await TestSupport.RunGit(repoPath, "add", "CodeyBox.slnx", "tests/CodeyBox.Tests.csproj");
        await TestSupport.RunGit(repoPath, "commit", "-m", "add solution marker");
    }

    /// <summary>
    /// Adds a nested .slnx (no root solution) so the verifier's recursive
    /// solution-discovery fallback branch is exercised.
    /// </summary>
    private static async Task AddNestedSolutionAsync(string repoPath)
    {
        var nested = Path.Combine(repoPath, "nested");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "Nested.slnx"), "# nested solution marker\n");
        await TestSupport.RunGit(repoPath, "add", "nested/Nested.slnx");
        await TestSupport.RunGit(repoPath, "commit", "-m", "add nested solution");
    }

    /// <summary>
    /// Adds a nested .slnx (no root solution) plus a test project that lives
    /// under a top-level <c>tests/</c> directory and is intentionally NOT
    /// listed in the nested solution. The build script's test-project
    /// enrichment must still pick the test project up when nested-only
    /// solutions drive target discovery.
    /// </summary>
    private static async Task AddNestedSolutionWithUnregisteredTestProjectAsync(string repoPath)
    {
        var nested = Path.Combine(repoPath, "nested");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "Nested.slnx"), "# nested solution marker\n");
        var testsDir = Path.Combine(repoPath, "tests");
        Directory.CreateDirectory(testsDir);
        await File.WriteAllTextAsync(
            Path.Combine(testsDir, "Unregistered.Tests.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        await TestSupport.RunGit(repoPath, "add", "nested/Nested.slnx", "tests/Unregistered.Tests.csproj");
        await TestSupport.RunGit(repoPath, "commit", "-m", "add nested solution plus unregistered test project");
    }

    /// <summary>
    /// Adds only a .csproj (no .slnx/.sln anywhere) so the verifier's
    /// csproj-only fallback branch is exercised.
    /// </summary>
    private static async Task AddCsprojOnlyMarkerAsync(string repoPath)
    {
        var solo = Path.Combine(repoPath, "src", "Solo");
        Directory.CreateDirectory(solo);
        await File.WriteAllTextAsync(Path.Combine(solo, "Solo.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        await TestSupport.RunGit(repoPath, "add", "src/Solo/Solo.csproj");
        await TestSupport.RunGit(repoPath, "commit", "-m", "add solo csproj (no solution)");
    }

    /// <summary>
    /// Adds two non-test .csproj files at the repo root (no .slnx/.sln), so
    /// deleting one and keeping the other leaves <c>workHasMarkers=true</c>.
    /// The csproj-only-repo arm of the build script would then silently build
    /// only the remaining project unless the marker-preservation rule treats
    /// every base .csproj as required in that mode.
    /// </summary>
    private static async Task AddTwoNonTestCsprojOnlyMarkersAsync(string repoPath)
    {
        var first = Path.Combine(repoPath, "src", "Alpha");
        var second = Path.Combine(repoPath, "src", "Beta");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        await File.WriteAllTextAsync(Path.Combine(first, "Alpha.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        await File.WriteAllTextAsync(Path.Combine(second, "Beta.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        await TestSupport.RunGit(repoPath, "add", "src/Alpha/Alpha.csproj", "src/Beta/Beta.csproj");
        await TestSupport.RunGit(repoPath, "commit", "-m", "add two non-test csprojs (no solution)");
    }

    private async Task CommitToBareBranchAsync(
        string barePath,
        string branch,
        string fileName,
        string contents,
        string subject)
    {
        var clone = Path.Combine(_workspace, "branch-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", barePath, clone);
        await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(clone, "config", "user.name", "Test");
        await TestSupport.RunGit(clone, "checkout", "-B", branch);

        var path = Path.Combine(clone, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents);
        await TestSupport.RunGit(clone, "add", fileName);
        await TestSupport.RunGit(clone, "commit", "-m", $"{subject}\n\n{CodeyBoxTrailers.CoAuthoredBy}");
        await TestSupport.RunGit(clone, "push", "origin", $"{branch}:{branch}");
    }

    private async Task PushPreemptCheckpointWithFileAsync(
        string barePath,
        string workBranch,
        string checkpointRef,
        string fileName,
        string contents,
        string subject)
    {
        var clone = Path.Combine(_workspace, "checkpoint-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", barePath, clone);
        await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(clone, "config", "user.name", "Test");
        await TestSupport.RunGit(clone, "checkout", "-B", workBranch);

        var path = Path.Combine(clone, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents);
        await TestSupport.RunGit(clone, "add", "-A");
        await TestSupport.RunGit(clone, "commit", "-m", $"{subject}\n\n{CodeyBoxTrailers.CoAuthoredBy}");
        await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{checkpointRef}");
    }

    private async Task DeleteFromBareBranchAsync(
        string barePath,
        string branch,
        string subject,
        params string[] paths)
    {
        var clone = Path.Combine(_workspace, "branch-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", barePath, clone);
        await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(clone, "config", "user.name", "Test");
        await TestSupport.RunGit(clone, "checkout", "-B", branch);
        var rmArgs = new List<string> { "rm", "--" };
        rmArgs.AddRange(paths);
        await TestSupport.RunGit(clone, rmArgs.ToArray());
        await TestSupport.RunGit(clone, "commit", "-m", $"{subject}\n\n{CodeyBoxTrailers.CoAuthoredBy}");
        await TestSupport.RunGit(clone, "push", "origin", $"{branch}:{branch}");
    }

    private static WorkItem NewItem(string workBranch) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "required build gate test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = workBranch,
        PushUpstream = false,
    };

    private static Project NewProject(WorkItem item) => new()
    {
        Id = item.ProjectId,
        DisplayName = "Required-build verifier test project",
        RepositoryUrl = "ignored",
        DefaultBaseBranch = "main",
    };

    private sealed record FakeDotnet(
        string Path,
        string LogPath,
        IReadOnlyDictionary<string, string> Environment);

    private sealed class PassingAuditor : IAuditor
    {
        public string Name => "passing";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
            => Task.FromResult(new AuditResult(true, []));
    }

    private sealed class OneTimeFailingAuditor : IAuditor
    {
        private int _calls;
        public string Name => "one-time-failing";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
        {
            _ = sandbox;
            _ = workingDirectory;
            _ = context;
            _ = ct;
            if (Interlocked.Increment(ref _calls) == 1)
            {
                return Task.FromResult(new AuditResult(false,
                [
                    new AuditFinding(
                        Name,
                        AuditSeverity.Error,
                        "force rework",
                        "force one rework iteration"),
                ]));
            }

            return Task.FromResult(new AuditResult(true, []));
        }
    }

    private sealed class PathInjectingSandboxProvider(
        string path,
        IReadOnlyDictionary<string, string>? environment = null) : ISandboxProvider
    {
        private readonly ProcessSandboxProvider _inner =
            new(NullLogger<ProcessSandboxProvider>.Instance);

        public string Name => _inner.Name;

        public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => new PathInjectingSandbox(await _inner.CreateAsync(spec, ct), path, environment);

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => _inner.ListAllManagedAsync(ct);

        public Task DisposeLeakedAsync(string name, CancellationToken ct)
            => _inner.DisposeLeakedAsync(name, ct);
    }

    /// <summary>
    /// Wraps a real sandbox provider but intercepts <c>sh -c</c> invocations
    /// (i.e., the build script) and returns a fixed exit code with given
    /// stderr. Non-<c>sh</c> invocations (like <c>git clone</c>) pass through
    /// to the inner provider unchanged. Used to drive verifier branches
    /// keyed off specific build-script exit codes (e.g. 125 = no target).
    /// </summary>
    private sealed class ShellExitCodeOverrideSandboxProvider(
        ISandboxProvider inner, int exitCode, string stderr) : ISandboxProvider
    {
        public string Name => inner.Name;

        public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => new ShellExitCodeOverrideSandbox(await inner.CreateAsync(spec, ct), exitCode, stderr);

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => inner.ListAllManagedAsync(ct);

        public Task DisposeLeakedAsync(string name, CancellationToken ct)
            => inner.DisposeLeakedAsync(name, ct);
    }

    private sealed class ShellExitCodeOverrideSandbox(
        ISandbox inner, int exitCode, string stderr) : ISandbox
    {
        public string Id => inner.Id;

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (exec.Argv.Count > 0 && exec.Argv[0] == "sh")
                return Task.FromResult(new SandboxExecResult(exitCode, string.Empty, stderr));
            return inner.ExecAsync(exec, ct);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class PathInjectingSandbox(
        ISandbox inner,
        string path,
        IReadOnlyDictionary<string, string>? environment) : ISandbox
    {
        public string Id => inner.Id;

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            var env = exec.ExtraEnvironment is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(exec.ExtraEnvironment);
            env["PATH"] = path;
            if (environment is not null)
            {
                foreach (var (key, value) in environment)
                    env[key] = value;
            }
            return inner.ExecAsync(exec with { ExtraEnvironment = env }, ct);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class SandboxFactoryFailingSandboxProvider(string message) : ISandboxProvider
    {
        public string Name => "sandbox-factory-failing";

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            _ = spec;
            _ = ct;
            throw new InvalidOperationException(message);
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>(Array.Empty<ManagedSandboxInfo>());

        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class SandboxFactoryProvisioningDeferredProvider(SandboxProvisioningDeferredException exception) : ISandboxProvider
    {
        public string Name => "sandbox-factory-provisioning-deferred";

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            _ = spec;
            _ = ct;
            throw exception;
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>(Array.Empty<ManagedSandboxInfo>());

        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class GitFailingSandboxProvider(string stderr) : ISandboxProvider
    {
        public string Name => "git-failing";

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            _ = spec;
            _ = ct;
            return Task.FromResult<ISandbox>(new GitFailingSandbox(stderr));
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>(Array.Empty<ManagedSandboxInfo>());

        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class GitFailingSandbox(string stderr) : ISandbox
    {
        public string Id { get; } = "git-failing-" + Guid.NewGuid().ToString("N")[..8];

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            _ = ct;
            if (exec.Argv.Count > 0 && exec.Argv[0] == "git")
                return Task.FromResult(new SandboxExecResult(128, string.Empty, stderr));
            return Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CapturingAuditReportStore : IAuditReportStore
    {
        public List<AuditReport> Reports { get; } = [];

        public Task CreateAsync(AuditReport report, CancellationToken ct = default)
        {
            Reports.Add(report);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditReport>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AuditReport>>(Reports.Where(r => r.WorkItemId == workItemId).ToList());

        public Task<string?> GetRawOutputAsync(string workItemId, int iteration, string auditorName, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
            => Task.FromResult(0);
    }

    private sealed class ThrowingListFilesGitHost(IGitHost inner, string message) : DelegatingGitHost(inner)
    {
        public override Task<IReadOnlyList<string>> ListFilesAsync(string repositoryId, string treeish, string? pathPrefix, CancellationToken ct = default)
            => throw new InvalidOperationException(message);

        public override Task<IReadOnlyList<string>> ListFilesEndingWithAsync(
            string repositoryId, string treeish, IReadOnlyList<string> filenameSuffixes,
            int maxResults, CancellationToken ct = default)
            => throw new InvalidOperationException(message);
    }

    private sealed class BrokenIsolatedCloneGitHost(IGitHost inner, string message) : DelegatingGitHost(inner)
    {
        public override Task<string> CreateIsolatedMergeCloneAsync(string repositoryId, WorkItemId workItemId, CancellationToken ct = default)
            => throw new InvalidOperationException(message);

        public override Task<string> CreateIsolatedRepositoryCloneAsync(
            string repositoryId,
            WorkItemId lifetimeId,
            CancellationToken ct = default)
            => throw new InvalidOperationException(message);
    }

    private sealed class BrokenIsolatedRepositoryCloneGitHost(IGitHost inner, string message) : DelegatingGitHost(inner)
    {
        public override Task<string> CreateIsolatedRepositoryCloneAsync(
            string repositoryId,
            WorkItemId lifetimeId,
            CancellationToken ct = default)
            => throw new InvalidOperationException(message);
    }

    private sealed class BrokenIsolatedRepoAccessGitHost(IGitHost inner, string message) : DelegatingGitHost(inner)
    {
        public override SandboxRepositoryAccess GetIsolatedRepoSandboxAccess(string isolatedRepoHostPath)
            => throw new InvalidOperationException(message);
    }

    private abstract class DelegatingGitHost(IGitHost inner) : IGitHost
    {
        protected readonly IGitHost Inner = inner;

        public virtual Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default)
            => Inner.EnsureRepositoryAsync(id, seedFromUrl, ct);
        public virtual Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, string? baseBranch, CancellationToken ct = default)
            => Inner.EnsureRepositoryAsync(id, seedFromUrl, baseBranch, ct);
        public virtual SandboxRepositoryAccess GetSandboxAccess(string repositoryId) => Inner.GetSandboxAccess(repositoryId);
        public virtual string GetRepoPath(string repositoryId) => Inner.GetRepoPath(repositoryId);
        public virtual string GetMergeStagingRoot(string repositoryId) => Inner.GetMergeStagingRoot(repositoryId);
        public virtual SandboxRepositoryAccess GetIsolatedRepoSandboxAccess(string isolatedRepoHostPath)
            => Inner.GetIsolatedRepoSandboxAccess(isolatedRepoHostPath);
        public virtual Task<string> CreateIsolatedMergeCloneAsync(string repositoryId, WorkItemId workItemId, CancellationToken ct = default)
            => Inner.CreateIsolatedMergeCloneAsync(repositoryId, workItemId, ct);
        public virtual Task<string> CreateIsolatedRepositoryCloneAsync(
            string repositoryId,
            WorkItemId lifetimeId,
            CancellationToken ct = default)
            => Inner.CreateIsolatedRepositoryCloneAsync(repositoryId, lifetimeId, ct);
        public virtual Task RestoreIsolatedMergeCloneAsync(string repositoryId, string targetPath, CancellationToken ct = default)
            => Inner.RestoreIsolatedMergeCloneAsync(repositoryId, targetPath, ct);
        public virtual Task DisposeIsolatedMergeCloneAsync(string repositoryId, string targetPath, CancellationToken ct = default)
            => Inner.DisposeIsolatedMergeCloneAsync(repositoryId, targetPath, ct);
        public virtual Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default)
            => Inner.GetDefaultBranchAsync(repositoryId, ct);
        public virtual Task PushToUpstreamAsync(string repositoryId, string upstreamUrl, string branch,
            IReadOnlyDictionary<string, string> upstreamEnv,
            UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase,
            CancellationToken ct = default)
            => Inner.PushToUpstreamAsync(repositoryId, upstreamUrl, branch, upstreamEnv, reconcileStrategy, ct);
        public virtual Task<string?> FetchUpstreamBranchAsync(string repositoryId, string upstreamUrl, string branch,
            IReadOnlyDictionary<string, string> upstreamEnv, CancellationToken ct = default)
            => Inner.FetchUpstreamBranchAsync(repositoryId, upstreamUrl, branch, upstreamEnv, ct);
        public virtual Task SetBranchToCommitAsync(string repositoryId, string branch, string sha, CancellationToken ct = default)
            => Inner.SetBranchToCommitAsync(repositoryId, branch, sha, ct);
        public virtual Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default)
            => Inner.DisposeRepositoryAsync(repositoryId, ct);
        public virtual Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default)
            => Inner.RepositoryExistsAsync(id, ct);
        public virtual Task<bool> BranchExistsAsync(string repositoryId, string branch, CancellationToken ct = default)
            => Inner.BranchExistsAsync(repositoryId, branch, ct);
        public virtual Task<bool> BranchHasCommitsAheadAsync(string repositoryId, string baseBranch, string workBranch, CancellationToken ct = default)
            => Inner.BranchHasCommitsAheadAsync(repositoryId, baseBranch, workBranch, ct);
        public virtual Task<(string DiffStat, string FullDiff)> GetDiffAsync(string repositoryId, string baseBranch, string workBranch, CancellationToken ct = default)
            => Inner.GetDiffAsync(repositoryId, baseBranch, workBranch, ct);
        public virtual Task<GitMergeTreeResult> ComputeMergeTreeAsync(string repositoryId, string mainCommit, string workCommit, CancellationToken ct = default)
            => Inner.ComputeMergeTreeAsync(repositoryId, mainCommit, workCommit, ct);
        public virtual Task<string> ResolveCommitAsync(string repositoryId, string commitish, CancellationToken ct = default)
            => Inner.ResolveCommitAsync(repositoryId, commitish, ct);
        public virtual Task ResetWorkBranchToBaseAsync(string repositoryId, string workBranch, string baseBranch, CancellationToken ct = default)
            => Inner.ResetWorkBranchToBaseAsync(repositoryId, workBranch, baseBranch, ct);
        public virtual Task<string> ResolveTreeAsync(string repositoryId, string treeish, CancellationToken ct = default)
            => Inner.ResolveTreeAsync(repositoryId, treeish, ct);
        public virtual Task<string> ReadTextFileAsync(string repositoryId, string treeish, string path, CancellationToken ct = default)
            => Inner.ReadTextFileAsync(repositoryId, treeish, path, ct);
        public virtual Task<IReadOnlyList<string>> ListFilesAsync(string repositoryId, string treeish, string? pathPrefix, CancellationToken ct = default)
            => Inner.ListFilesAsync(repositoryId, treeish, pathPrefix, ct);
        public virtual Task<IReadOnlyList<string>> ListFilesEndingWithAsync(
            string repositoryId, string treeish, IReadOnlyList<string> filenameSuffixes,
            int maxResults, CancellationToken ct = default)
            => Inner.ListFilesEndingWithAsync(repositoryId, treeish, filenameSuffixes, maxResults, ct);
        public virtual Task<IReadOnlyList<GitChangedPath>> GetChangedPathsAsync(string repositoryId, string fromTreeish, string toTreeish, CancellationToken ct = default)
            => Inner.GetChangedPathsAsync(repositoryId, fromTreeish, toTreeish, ct);
        public virtual Task<string> GetUnifiedDiffAsync(string repositoryId, string fromTreeish, string toTreeish, string path, CancellationToken ct = default)
            => Inner.GetUnifiedDiffAsync(repositoryId, fromTreeish, toTreeish, path, ct);
    }
}
