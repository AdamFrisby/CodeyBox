using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
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
    public async Task RetryFromWork_NoChangesOnBrokenCSharpBranch_FailsWithBuildErrorNotNoChanges()
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
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "build.fail", "broken\n", "broken prior attempt");

        tp.Agent.WorkPlan.Enqueue(new FileWrite("build.fail", "broken\n"));

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("work left the branch non-compiling", final.LastError);
        Assert.Contains("error CS1061", final.LastError);
        Assert.Equal("build", final.FailureKind);
        Assert.DoesNotContain("no changes", final.LastError, StringComparison.OrdinalIgnoreCase);
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
    public async Task AuditRework_NewCommitThatBreaksRequiredBuild_FailsWithReworkBuildError()
    {
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
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("rework left the branch non-compiling", final.LastError);
        Assert.Contains("error CS1061", final.LastError);
        Assert.Equal("build", final.FailureKind);
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
            new PipelineOptions { SandboxImageReference = "ignored" },
            auditReports: null,
            NullLogger<SandboxRequiredBuildVerifier>.Instance);

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
            new PipelineOptions { SandboxImageReference = "ignored" },
            auditReports: null,
            NullLogger<SandboxRequiredBuildVerifier>.Instance);
        var verification = await verifier.VerifyAsync(new RequiredBuildVerificationRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
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
            new PipelineOptions { SandboxImageReference = "ignored" },
            auditReports: null,
            NullLogger<SandboxRequiredBuildVerifier>.Instance);

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
        var captureStore = new CapturingAuditReportStore();
        var verifier = new SandboxRequiredBuildVerifier(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance),
            throwingHost,
            new PipelineOptions { SandboxImageReference = "ignored" },
            captureStore,
            NullLogger<SandboxRequiredBuildVerifier>.Instance);

        var item = NewItem("feature/marker-inspection-fails") with { State = WorkItemState.WorkComplete };
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = gitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "ok.txt", "ok\n", "branch exists");

        var result = await verifier.VerifyAsync(new RequiredBuildVerificationRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            RepositoryId = repoId,
            BaseBranch = item.BaseBranch,
            WorkBranch = item.WorkBranch!,
            Phase = "audit",
        }, CancellationToken.None);

        Assert.Equal(RequiredBuildVerificationStatus.Unavailable, result.Status);
        Assert.Contains("could not verify required build", result.Reason);
        Assert.Contains("git ls-tree blew up", result.Reason);
        Assert.Empty(captureStore.Reports);

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
        var captureStore = new CapturingAuditReportStore();
        var verifier = new SandboxRequiredBuildVerifier(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance),
            brokenHost,
            new PipelineOptions { SandboxImageReference = "ignored" },
            captureStore,
            NullLogger<SandboxRequiredBuildVerifier>.Instance);

        var item = NewItem("feature/isolated-clone-fails") with { State = WorkItemState.WorkComplete };
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = gitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "ok.txt", "ok\n", "branch exists");

        var result = await verifier.VerifyAsync(new RequiredBuildVerificationRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            RepositoryId = repoId,
            BaseBranch = item.BaseBranch,
            WorkBranch = item.WorkBranch!,
            Phase = "audit",
        }, CancellationToken.None);

        Assert.Equal(RequiredBuildVerificationStatus.Unavailable, result.Status);
        Assert.Contains("could not verify required build", result.Reason);
        Assert.Contains("isolated build repository", result.Reason);
        Assert.Contains("disk full while preparing isolated clone", result.Reason);
        Assert.Empty(captureStore.Reports);
    }

    [Fact]
    public async Task SandboxRequiredBuildVerifier_GitCloneInsideSandboxFails_ReturnsUnavailable()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var captureStore = new CapturingAuditReportStore();
        var verifier = new SandboxRequiredBuildVerifier(
            new GitFailingSandboxProvider("fatal: simulated git clone failure inside sandbox"),
            gitHost,
            new PipelineOptions { SandboxImageReference = "ignored" },
            captureStore,
            NullLogger<SandboxRequiredBuildVerifier>.Instance);

        var item = NewItem("feature/sandbox-clone-fails") with { State = WorkItemState.WorkComplete };
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = gitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "ok.txt", "ok\n", "branch exists");

        var result = await verifier.VerifyAsync(new RequiredBuildVerificationRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            RepositoryId = repoId,
            BaseBranch = item.BaseBranch,
            WorkBranch = item.WorkBranch!,
            Phase = "audit",
        }, CancellationToken.None);

        Assert.Equal(RequiredBuildVerificationStatus.Unavailable, result.Status);
        Assert.Contains("could not verify required build", result.Reason);
        Assert.Contains("git", result.Reason);
        Assert.Contains("simulated git clone failure", result.Reason);
        Assert.Empty(captureStore.Reports);
    }

    [Fact]
    public async Task SandboxRequiredBuildVerifier_FailingBuild_PersistsAuditReportWithErrorFinding()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnet = await CreateFakeDotnetAsync();
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var captureStore = new CapturingAuditReportStore();
        var verifier = new SandboxRequiredBuildVerifier(
            new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment),
            gitHost,
            new PipelineOptions { SandboxImageReference = "ignored" },
            captureStore,
            NullLogger<SandboxRequiredBuildVerifier>.Instance);

        var item = NewItem("feature/audit-report-fail") with { State = WorkItemState.WorkComplete };
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await CommitToBareBranchAsync(
            gitHost.GetRepoPath(repoId),
            item.WorkBranch!,
            "build.fail",
            "broken\n",
            "broken branch");

        var result = await verifier.VerifyAsync(new RequiredBuildVerificationRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            RepositoryId = repoId,
            BaseBranch = item.BaseBranch,
            WorkBranch = item.WorkBranch!,
            Phase = "audit",
            Iteration = 3,
        }, CancellationToken.None);

        Assert.Equal(RequiredBuildVerificationStatus.Failed, result.Status);
        var report = Assert.Single(captureStore.Reports);
        Assert.Equal(RequiredBuildGateIdentity.AuditorName, report.AuditorName);
        Assert.Equal("shell", report.AuditorKind);
        Assert.Equal(AuditSeverity.Error.ToString(), report.WorstSeverity);
        Assert.Equal(3, report.Iteration);
        Assert.Equal(item.Id.ToString(), report.WorkItemId);
        Assert.NotNull(report.RawOutput);
        Assert.Contains("error CS1061", report.RawOutput);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(AuditSeverity.Error.ToString(), finding.Severity);
        Assert.Contains("required build failed", finding.Title);
        Assert.StartsWith("f-", finding.Id);
    }

    [Fact]
    public async Task SandboxRequiredBuildVerifier_PassingBuild_PersistsAuditReportWithSeverityNone()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnet = await CreateFakeDotnetAsync();
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var captureStore = new CapturingAuditReportStore();
        var verifier = new SandboxRequiredBuildVerifier(
            new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment),
            gitHost,
            new PipelineOptions { SandboxImageReference = "ignored" },
            captureStore,
            NullLogger<SandboxRequiredBuildVerifier>.Instance);

        var item = NewItem("feature/audit-report-pass") with { State = WorkItemState.WorkComplete };
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
            RepositoryId = repoId,
            BaseBranch = item.BaseBranch,
            WorkBranch = item.WorkBranch!,
            Phase = "audit",
            Iteration = 7,
        }, CancellationToken.None);

        Assert.Equal(RequiredBuildVerificationStatus.Passed, result.Status);
        var report = Assert.Single(captureStore.Reports);
        Assert.Equal(RequiredBuildGateIdentity.AuditorName, report.AuditorName);
        Assert.Equal("shell", report.AuditorKind);
        Assert.Equal("none", report.WorstSeverity);
        Assert.Equal(7, report.Iteration);
        Assert.Empty(report.Findings);
        Assert.NotNull(report.RawOutput);
        Assert.Contains("Build succeeded", report.RawOutput);
    }

    [Fact]
    public async Task SandboxRequiredBuildVerifier_WorkDeletesRootSolution_FailsEvenWhenLeafCsprojRemains()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnet = await CreateFakeDotnetAsync();
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var captureStore = new CapturingAuditReportStore();
        var verifier = new SandboxRequiredBuildVerifier(
            new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment),
            gitHost,
            new PipelineOptions { SandboxImageReference = "ignored" },
            captureStore,
            NullLogger<SandboxRequiredBuildVerifier>.Instance);

        var item = NewItem("feature/work-deletes-root-keeps-leaf") with { State = WorkItemState.WorkComplete };
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = gitHost.GetRepoPath(repoId);
        await ReplaceBaseMarkersWithLeafProjectAsync(barePath, item.WorkBranch!);

        var result = await verifier.VerifyAsync(new RequiredBuildVerificationRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            RepositoryId = repoId,
            BaseBranch = item.BaseBranch,
            WorkBranch = item.WorkBranch!,
            Phase = "audit",
            Iteration = 1,
        }, CancellationToken.None);

        Assert.Equal(RequiredBuildVerificationStatus.Failed, result.Status);
        Assert.Contains("deleted or moved", result.Output);
        Assert.Contains("CodeyBox.slnx", result.Output);
        Assert.Contains("tests/CodeyBox.Tests.csproj", result.Output);
        var report = Assert.Single(captureStore.Reports);
        Assert.Equal(AuditSeverity.Error.ToString(), report.WorstSeverity);
        Assert.Equal(1, report.Iteration);
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
    }

    private sealed class BrokenIsolatedCloneGitHost(IGitHost inner, string message) : DelegatingGitHost(inner)
    {
        public override Task<string> CreateIsolatedMergeCloneAsync(string repositoryId, WorkItemId workItemId, CancellationToken ct = default)
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
        public virtual Task<IReadOnlyList<GitChangedPath>> GetChangedPathsAsync(string repositoryId, string fromTreeish, string toTreeish, CancellationToken ct = default)
            => Inner.GetChangedPathsAsync(repositoryId, fromTreeish, toTreeish, ct);
        public virtual Task<string> GetUnifiedDiffAsync(string repositoryId, string fromTreeish, string toTreeish, string path, CancellationToken ct = default)
            => Inner.GetUnifiedDiffAsync(repositoryId, fromTreeish, toTreeish, path, ct);
    }
}
