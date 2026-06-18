using CodeyBox.Audit.Llm;
using CodeyBox.Audit.Presets;
using CodeyBox.Audit.Shell;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

// ── BuildTestGate ordering & short-circuit ────────────────────────────────────

/// <summary>
/// The LLM auditor panel's prompt frame asserts "automated CI built the
/// project and ran the full test suite, and reported no build errors and no
/// test failures." For that claim to stay true on every iteration, two
/// invariants must hold:
///
///   * BuildTestGate-role auditors MUST run before any LLM auditor.
///   * A BuildTestGate blocking finding MUST skip the LLM panel for that
///     iteration (even when <c>StopOnFirstFailure</c> is false, the panel's
///     default).
///
/// These tests pin both invariants by driving the live audit loop with a
/// scripted gate auditor and a recording LLM auditor.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class BuildTestGateOrderingTests : IDisposable
{
    private readonly string _workspace;
    public BuildTestGateOrderingTests() => _workspace = Directory.CreateTempSubdirectory("codeybox-gate-").FullName;
    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    [Fact]
    public async Task BuildTestGateFails_LlmPanelIsSkippedThisIteration_StopOnFirstFailureOff()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        // Iter 1: gate fails (no test) → LLM panel must be skipped.
        // Iter 2: gate passes → LLM panel runs and passes → Done.
        var gate = new RoleStampedScriptedAuditor(
            "csharp:test-pass",
            AuditorRole.BuildTestGate,
        [
            new AuditOutcome(false, [new AuditFinding(
                "csharp:test-pass", AuditSeverity.Error,
                "tests failed", "MyTests.SomeTest failed")]),
            new AuditOutcome(true, []),
        ]);

        var llmRunsSeen = 0;
        var llm = new RecordingLlmAuditor("security:llm-review", _ => Interlocked.Increment(ref llmRunsSeen));

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            auditors: [gate, llm],
            projectAudit: new ProjectAudit
            {
                MaxIterations = 3,
                AuditTypes = ["scripted"],
                StopOnFirstFailure = false,
            },
            credentials: AuditCredentials());
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v2-after-rework"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.True(
            final!.State == WorkItemState.Done,
            $"expected Done, got {final.State}: {final.LastError}");

        // The LLM auditor must have run exactly once (iter 2), never iter 1.
        Assert.Equal(1, llmRunsSeen);
        Assert.Equal([2], llm.SeenIterations);
        Assert.Equal([1, 2], gate.SeenIterations);
    }

    [Fact]
    public async Task BuildTestGateReturnsNonPassingInfoFinding_LlmPanelIsSkipped()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var gate = new RoleStampedScriptedAuditor(
            "csharp:test-pass",
            AuditorRole.BuildTestGate,
        [
            new AuditOutcome(false, [new AuditFinding(
                "csharp:test-pass", AuditSeverity.Info,
                "tool not installed in sandbox: dotnet",
                "The auditor command was not run because 'dotnet' is not available in the audit sandbox.")]),
        ]);

        var llmRunsSeen = 0;
        var llm = new RecordingLlmAuditor("security:llm-review", _ => Interlocked.Increment(ref llmRunsSeen));

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            auditors: [gate, llm],
            maxAuditIterations: 1,
            credentials: AuditCredentials());
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        // A non-passing BuildTestGate cannot allow a green audit even when the
        // auditor's own finding was informational. The pipeline synthesizes a
        // blocking gate finding so the item cannot merge without verified tests.
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Contains("build/test gate", final.LastError, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, llmRunsSeen);
        Assert.Empty(llm.SeenIterations);
        Assert.Equal([1], gate.SeenIterations);
    }

    [Fact]
    public async Task BuildTestGatePassedTrueWithErrorFinding_LlmPanelIsSkippedAndAuditBlocks()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var gate = new RoleStampedScriptedAuditor(
            "csharp:test-pass",
            AuditorRole.BuildTestGate,
        [
            new AuditOutcome(true, [new AuditFinding(
                "csharp:test-pass", AuditSeverity.Error,
                "tests failed despite passed flag", "The test command reported a failure.")]),
        ]);

        var llmRunsSeen = 0;
        var llm = new RecordingLlmAuditor("security:llm-review", _ => Interlocked.Increment(ref llmRunsSeen));

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            auditors: [gate, llm],
            maxAuditIterations: 1,
            credentials: AuditCredentials());
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Equal(0, llmRunsSeen);
        Assert.Empty(llm.SeenIterations);
        Assert.Equal([1], gate.SeenIterations);
    }

    [Fact]
    public async Task BuildOnlyGateDoesNotUnlockLlmPanel_AndNonLlmAuditorsStillRun()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var executionOrder = new List<string>();
        var orderLock = new object();
        void Record(string n) { lock (orderLock) executionOrder.Add(n); }

        var buildOnlyGate = new RoleStampedScriptedAuditor(
            "csharp:build-WaE",
            AuditorRole.BuildTestGate,
            [new AuditOutcome(true, [])],
            onRun: _ => Record("build"),
            gateEvidence: BuildTestGateEvidence.Build);
        var format = new RoleStampedScriptedAuditor(
            "csharp:format-check",
            AuditorRole.None,
            [new AuditOutcome(true, [])],
            onRun: _ => Record("format"));
        var llmRuns = 0;
        var llm = new RecordingLlmAuditor("security:llm-review", _ => Interlocked.Increment(ref llmRuns));

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [buildOnlyGate, format, llm],
            maxAuditIterations: 1,
            credentials: AuditCredentials(),
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Contains("build/test gate", final.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["build", "format"], executionOrder);
        Assert.Equal(0, llmRuns);
    }

    [Fact]
    public async Task TestOnlyGateDoesNotUnlockLlmPanel()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var testOnlyGate = new RoleStampedScriptedAuditor(
            "custom:test-pass",
            AuditorRole.BuildTestGate,
            [new AuditOutcome(true, [])],
            gateEvidence: BuildTestGateEvidence.Test);
        var llmRuns = 0;
        var llm = new RecordingLlmAuditor("security:llm-review", _ => Interlocked.Increment(ref llmRuns));

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [testOnlyGate, llm],
            maxAuditIterations: 1,
            credentials: AuditCredentials(),
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Contains("build/test gate", final.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([1], testOnlyGate.SeenIterations);
        Assert.Equal(0, llmRuns);
    }

    [Fact]
    public async Task UnverifiedGateBlocksLlmPanel_EvenWhenOtherGateProvidesEvidence()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var build = new RoleStampedScriptedAuditor(
            "custom:build",
            AuditorRole.BuildTestGate,
            [new AuditOutcome(true, [])],
            gateEvidence: BuildTestGateEvidence.Build);
        var unverifiedTest = new RoleStampedScriptedAuditor(
            "csharp:test-pass",
            AuditorRole.BuildTestGate,
            [new AuditOutcome(true, [], BuildTestGateEvidenceVerified: false)],
            gateEvidence: BuildTestGateEvidence.Test);
        var secondTest = new RoleStampedScriptedAuditor(
            "custom:test-pass",
            AuditorRole.BuildTestGate,
            [new AuditOutcome(true, [])],
            gateEvidence: BuildTestGateEvidence.Test);
        var llmRuns = 0;
        var llm = new RecordingLlmAuditor("security:llm-review", _ => Interlocked.Increment(ref llmRuns));

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [build, unverifiedTest, secondTest, llm],
            maxAuditIterations: 1,
            credentials: AuditCredentials(),
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Contains("did not verify", final.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([1], build.SeenIterations);
        Assert.Equal([1], unverifiedTest.SeenIterations);
        Assert.Equal([1], secondTest.SeenIterations);
        Assert.Equal(0, llmRuns);
    }

    [Fact]
    public async Task TimedOutBuildTestGateReturnsIncompleteVerdict_AndSkipsLlmPanel()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gate = new HangingBuildTestGateAuditor("csharp:test-pass");
        var llmRuns = 0;
        var llm = new RecordingLlmAuditor("security:llm-review", _ => Interlocked.Increment(ref llmRuns));
        var tuning = new PipelineTuningSnapshot(new PipelineTuningOptions
        {
            AuditorIdleTimeout = TimeSpan.FromMilliseconds(100),
        });

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [gate, llm],
            maxAuditIterations: 1,
            credentials: AuditCredentials(),
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            pipelineTuning: tuning);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("incomplete auditor", final.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("csharp:test-pass", final.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, gate.RunCount);
        Assert.Equal(0, llmRuns);
        Assert.Empty(llm.SeenIterations);
    }

    [Fact]
    public async Task FailedBuildTestGateSkipsOnlyLlmPanel_WhenStopOnFirstFailureOff()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var formatRuns = 0;
        var gate = new RoleStampedScriptedAuditor(
            "csharp:test-pass",
            AuditorRole.BuildTestGate,
            [new AuditOutcome(false, [new AuditFinding(
                "csharp:test-pass",
                AuditSeverity.Error,
                "tests failed",
                "UnitTests.Fail failed")])],
            gateEvidence: BuildTestGateEvidence.Test);
        var format = new RoleStampedScriptedAuditor(
            "csharp:format-check",
            AuditorRole.None,
            [new AuditOutcome(true, [])],
            onRun: _ => Interlocked.Increment(ref formatRuns));
        var llmRuns = 0;
        var llm = new RecordingLlmAuditor("security:llm-review", _ => Interlocked.Increment(ref llmRuns));

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [gate, format, llm],
            projectAudit: new ProjectAudit
            {
                MaxIterations = 1,
                AuditTypes = ["scripted"],
                StopOnFirstFailure = false,
            },
            credentials: AuditCredentials(),
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Equal(1, formatRuns);
        Assert.Equal(0, llmRuns);
    }

    [Fact]
    public async Task FailedBuildTestGateStopsRemainingAuditors_WhenStopOnFirstFailureOn()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var formatRuns = 0;
        var gate = new RoleStampedScriptedAuditor(
            "csharp:test-pass",
            AuditorRole.BuildTestGate,
            [new AuditOutcome(false, [new AuditFinding(
                "csharp:test-pass",
                AuditSeverity.Error,
                "tests failed",
                "UnitTests.Fail failed")])],
            gateEvidence: BuildTestGateEvidence.Test);
        var format = new RoleStampedScriptedAuditor(
            "csharp:format-check",
            AuditorRole.None,
            [new AuditOutcome(true, [])],
            onRun: _ => Interlocked.Increment(ref formatRuns));
        var llmRuns = 0;
        var llm = new RecordingLlmAuditor("security:llm-review", _ => Interlocked.Increment(ref llmRuns));

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [gate, format, llm],
            projectAudit: new ProjectAudit
            {
                MaxIterations = 1,
                AuditTypes = ["scripted"],
                StopOnFirstFailure = true,
            },
            credentials: AuditCredentials(),
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Equal(0, formatRuns);
        Assert.Equal(0, llmRuns);
    }

    [Fact]
    public async Task FailedBuildTestGateHonorsDeclaredShortCircuit_WhenEnabled()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var laterToolRuns = 0;
        var gate = new RoleStampedScriptedAuditor(
            "csharp:build-WaE",
            AuditorRole.BuildTestGate,
            [new AuditOutcome(false, [new AuditFinding(
                "csharp:build-WaE",
                AuditSeverity.Error,
                "build failed",
                "compile error")])],
            onRun: _ => { },
            gateEvidence: BuildTestGateEvidence.Build,
            canShortCircuitOnBlockingFinding: true);
        var laterTool = new RoleStampedScriptedAuditor(
            "csharp:format-check",
            AuditorRole.None,
            [new AuditOutcome(true, [])],
            onRun: _ => Interlocked.Increment(ref laterToolRuns));
        var llmRuns = 0;
        var llm = new RecordingLlmAuditor("security:llm-review", _ => Interlocked.Increment(ref llmRuns));

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [gate, laterTool, llm],
            projectAudit: new ProjectAudit
            {
                MaxIterations = 1,
                AuditTypes = ["scripted"],
                StopOnFirstFailure = false,
            },
            credentials: AuditCredentials(),
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Equal(0, laterToolRuns);
        Assert.Equal(0, llmRuns);
    }

    [Fact]
    public async Task FailedBuildTestGateSkipsLlmButRunsNonLlmTools_WhenShortCircuitRoutingDisabled()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var laterToolRuns = 0;
        var gate = new RoleStampedScriptedAuditor(
            "csharp:build-WaE",
            AuditorRole.BuildTestGate,
            [new AuditOutcome(false, [new AuditFinding(
                "csharp:build-WaE",
                AuditSeverity.Error,
                "build failed",
                "compile error")])],
            onRun: _ => { },
            gateEvidence: BuildTestGateEvidence.Build,
            canShortCircuitOnBlockingFinding: true);
        var laterTool = new RoleStampedScriptedAuditor(
            "csharp:format-check",
            AuditorRole.None,
            [new AuditOutcome(true, [])],
            onRun: _ => Interlocked.Increment(ref laterToolRuns));
        var llmRuns = 0;
        var llm = new RecordingLlmAuditor("security:llm-review", _ => Interlocked.Increment(ref llmRuns));
        var tuning = new PipelineTuningSnapshot(new PipelineTuningOptions
        {
            AuditShortCircuitEnabled = false,
        });

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [gate, laterTool, llm],
            projectAudit: new ProjectAudit
            {
                MaxIterations = 1,
                AuditTypes = ["scripted"],
                StopOnFirstFailure = false,
            },
            credentials: AuditCredentials(),
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            pipelineTuning: tuning);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Equal(1, laterToolRuns);
        Assert.Equal(0, llmRuns);
    }

    [Fact]
    public async Task FailedBuildTestGateSkipsUnmarkedLlmAuditor()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gate = new RoleStampedScriptedAuditor(
            "csharp:test-pass",
            AuditorRole.BuildTestGate,
            [new AuditOutcome(false, [new AuditFinding(
                "csharp:test-pass",
                AuditSeverity.Error,
                "tests failed",
                "UnitTests.Fail failed")])],
            gateEvidence: BuildTestGateEvidence.BuildAndTest);
        var llmRuns = 0;
        var llm = new UnmarkedRecordingLlmAuditor("plugin:llm-review", _ => Interlocked.Increment(ref llmRuns));

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [gate, llm],
            projectAudit: new ProjectAudit
            {
                MaxIterations = 1,
                AuditTypes = ["scripted"],
                StopOnFirstFailure = false,
            },
            credentials: AuditCredentials(),
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Equal([1], gate.SeenIterations);
        Assert.Equal(0, llmRuns);
    }

    [Fact]
    public async Task MissingTestGateRejectsLlmPanel()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var llmRuns = 0;
        var llm = new RecordingLlmAuditor("security:llm-review", _ => Interlocked.Increment(ref llmRuns));

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [llm],
            maxAuditIterations: 1,
            credentials: AuditCredentials(),
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Contains("build/test gate", final.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, llmRuns);
    }

    [Fact]
    public async Task RequiredBuildGateFailureRunsBeforeAndSkipsLlmPanel()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var requiredBuild = new TestRequiredBuildVerifier(
            RequiredBuildProbeResult.Applies,
            RequiredBuildVerificationResult.Failed(1, "compile failed"));
        var llmRuns = 0;
        var llm = new RecordingLlmAuditor("security:llm-review", _ => Interlocked.Increment(ref llmRuns));

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [llm],
            maxAuditIterations: 1,
            credentials: AuditCredentials(),
            requiredBuildVerifier: requiredBuild);

        var item = NewItem() with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await TestSupport.RunGit(
            barePath,
            "update-ref",
            $"refs/heads/{item.WorkBranch}",
            "refs/heads/main");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.True(
            final!.State == WorkItemState.AuditFailed,
            $"expected AuditFailed, got {final.State}: {final.LastError}");
        Assert.Equal(1, requiredBuild.VerifyCalls);
        Assert.Equal(0, llmRuns);
        Assert.Contains("required build failed", final.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RequiredBuildGateFailureStillRunsNonGatedToolAuditors()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var requiredBuild = new TestRequiredBuildVerifier(
            RequiredBuildProbeResult.Applies,
            RequiredBuildVerificationResult.Failed(1, "compile failed"));
        var toolRuns = 0;
        var nonGatedTool = new RoleStampedScriptedAuditor(
            "security:gitleaks",
            AuditorRole.None,
            [new AuditOutcome(true, [])],
            onRun: _ => Interlocked.Increment(ref toolRuns));
        var llmRuns = 0;
        var llm = new RecordingLlmAuditor("security:llm-review", _ => Interlocked.Increment(ref llmRuns));

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [nonGatedTool, llm],
            maxAuditIterations: 1,
            credentials: AuditCredentials(),
            requiredBuildVerifier: requiredBuild);

        var item = NewItem() with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await TestSupport.RunGit(
            barePath,
            "update-ref",
            $"refs/heads/{item.WorkBranch}",
            "refs/heads/main");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.True(
            final!.State == WorkItemState.AuditFailed,
            $"expected AuditFailed, got {final.State}: {final.LastError}");
        Assert.Equal(1, requiredBuild.VerifyCalls);
        Assert.Equal(1, toolRuns);
        Assert.Equal(0, llmRuns);
        Assert.Contains("required build failed", final.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RequiredBuildGatePassAloneDoesNotUnlockLlmPanelWithoutTestGate()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var requiredBuild = new TestRequiredBuildVerifier(
            RequiredBuildProbeResult.Applies,
            RequiredBuildVerificationResult.Passed(0, "compile ok"));
        var llmRuns = 0;
        var llm = new RecordingLlmAuditor("security:llm-review", _ => Interlocked.Increment(ref llmRuns));

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [llm],
            maxAuditIterations: 1,
            credentials: AuditCredentials(),
            requiredBuildVerifier: requiredBuild);

        var item = NewItem() with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await TestSupport.RunGit(
            barePath,
            "update-ref",
            $"refs/heads/{item.WorkBranch}",
            "refs/heads/main");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.True(
            final!.State == WorkItemState.AuditFailed,
            $"expected AuditFailed, got {final.State}: {final.LastError}");
        Assert.Equal(1, requiredBuild.VerifyCalls);
        Assert.Equal(0, llmRuns);
        Assert.Contains("build/test gate", final.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildTestGateOrdersAheadOfOtherToolAuditors_AndAheadOfLlmPanel()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var executionOrder = new List<string>();
        var orderLock = new object();
        void Record(string n) { lock (orderLock) executionOrder.Add(n); }

        // Three auditors:
        //   - csharp:format-check (Role.None, tool) — registered first
        //   - csharp:test-pass    (BuildTestGate, tool) — registered second
        //   - security:llm-review (LLM, registered last)
        // After ordering by tier, execution order must be: gate → format → llm.
        var format = new RoleStampedScriptedAuditor(
            "csharp:format-check", AuditorRole.None,
            [new AuditOutcome(true, [])],
            onRun: _ => Record("format"));
        var gate = new RoleStampedScriptedAuditor(
            "csharp:test-pass", AuditorRole.BuildTestGate,
            [new AuditOutcome(true, [])],
            onRun: _ => Record("gate"));
        var llm = new RecordingLlmAuditor("security:llm-review", _ => Record("llm"));

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            auditors: [format, gate, llm],
            maxAuditIterations: 1,
            credentials: AuditCredentials());
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.True(
            final!.State == WorkItemState.Done,
            $"expected Done, got {final.State}: {final.LastError}");
        Assert.Equal(["gate", "format", "llm"], executionOrder);
    }

    [Fact]
    public async Task BuildTestGateStillRunsBeforeLlm_WhenShortCircuitRoutingDisabledAndLlmRegisteredFirst()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var executionOrder = new List<string>();
        var orderLock = new object();
        void Record(string n) { lock (orderLock) executionOrder.Add(n); }

        var llm = new RecordingLlmAuditor("security:llm-review", _ => Record("llm"));
        var gate = new RoleStampedScriptedAuditor(
            "csharp:test-pass",
            AuditorRole.BuildTestGate,
            [new AuditOutcome(true, [])],
            onRun: _ => Record("gate"));
        var tuning = new PipelineTuningSnapshot(new PipelineTuningOptions
        {
            AuditShortCircuitEnabled = false,
        });

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [llm, gate],
            maxAuditIterations: 1,
            credentials: AuditCredentials(),
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            pipelineTuning: tuning);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.True(
            final!.State == WorkItemState.Done,
            $"expected Done, got {final.State}: {final.LastError}");
        Assert.Equal(["gate", "llm"], executionOrder);
    }

    [Fact]
    public async Task NonGateBlockingFinding_DoesNotSkipLlmPanel()
    {
        // Confirms the gate is scoped to BuildTestGate role specifically: a
        // tool auditor without the role that produces a blocking finding does
        // NOT skip the LLM panel (the prompt frame's CI claim is only about
        // build+test, not lint/format).
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var gate = new RoleStampedScriptedAuditor(
            "csharp:test-pass", AuditorRole.BuildTestGate,
            [new AuditOutcome(true, [])]);
        var format = new RoleStampedScriptedAuditor(
            "csharp:format-check", AuditorRole.None,
            [new AuditOutcome(false, [new AuditFinding(
                "csharp:format-check", AuditSeverity.Error,
                "formatting needed", "x")])]);
        var llmRuns = 0;
        var llm = new RecordingLlmAuditor("security:llm-review", _ => Interlocked.Increment(ref llmRuns));

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            auditors: [format, gate, llm],
            maxAuditIterations: 1,
            credentials: AuditCredentials());
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        // LLM panel still runs even though a non-gate tool auditor blocked.
        Assert.Equal(1, llmRuns);
    }

    [Fact]
    public async Task MarkerInterfaceGatesCredentialFreeNonLlmAuditor()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var markedRuns = 0;
        var marked = new MarkedToolReviewAuditor("custom:design-review", _ => Interlocked.Increment(ref markedRuns));

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [marked],
            maxAuditIterations: 1,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Contains("build/test gate", final.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, markedRuns);
    }

    [Fact]
    public async Task CredentialedNonLlmAuditorDoesNotRequireBuildTestGate()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var credentialedRuns = 0;
        var credentialed = new CredentialedToolAuditor("plugin:credentialed-scan", _ => Interlocked.Increment(ref credentialedRuns));

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [credentialed],
            maxAuditIterations: 1,
            credentials: AuditCredentials(),
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.True(
            final!.State == WorkItemState.Done,
            $"expected Done, got {final.State}: {final.LastError}");
        Assert.Equal(1, credentialedRuns);
    }

    [Fact]
    public async Task PassingBuildAndTestGatesStillAllowLlmToFlagLowQualityDiff()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var build = new ShellCommandAuditor(new ShellCommandAuditorOptions
        {
            Name = "csharp:build-WaE",
            Argv = ["dotnet", "build", "tests/FeatureFlagsTests.csproj", "--no-incremental", "/warnaserror"],
            Role = AuditorRole.BuildTestGate,
            BuildTestGateEvidence = BuildTestGateEvidence.Build,
        });
        var test = new ShellCommandAuditor(new ShellCommandAuditorOptions
        {
            Name = "csharp:test-pass",
            Argv = ["dotnet", "test", "tests/FeatureFlagsTests.csproj", "--no-build"],
            Role = AuditorRole.BuildTestGate,
            BuildTestGateEvidence = BuildTestGateEvidence.Test,
        });
        var llm = new LowQualityPipelineLlmAuditor();

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [build, test, llm],
            maxAuditIterations: 1,
            credentials: AuditCredentials(),
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);
        tp.Agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            await ExecOk(sandbox, workingDirectory, ["mkdir", "-p", "src", "tests"], ct);
            await WriteFile(sandbox, workingDirectory, "src/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """, ct);
            await WriteFile(sandbox, workingDirectory, "src/FeatureFlags.cs", """
                namespace App;

                public static class FeatureFlags
                {
                    public static bool AddFeatureFlagX(IReadOnlyDictionary<string, string> config)
                        => config.TryGetValue("FeatureX", out var value) && value == "enabled";
                }
                """, ct);
            await WriteFile(sandbox, workingDirectory, "tests/FeatureFlagsTests.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                    <IsPackable>false</IsPackable>
                    <IsTestProject>true</IsTestProject>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                    <PackageReference Include="xunit" Version="2.9.3" />
                    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                  </ItemGroup>
                  <ItemGroup>
                    <ProjectReference Include="../src/App.csproj" />
                    <Using Include="Xunit" />
                  </ItemGroup>
                </Project>
                """, ct);
            await WriteFile(sandbox, workingDirectory, "tests/FeatureFlagsTests.cs", """
                using App;

                public sealed class FeatureFlagsTests
                {
                    [Fact]
                    public void EnabledBranch_ReturnsTrue()
                        => Assert.True(FeatureFlags.AddFeatureFlagX(
                            new Dictionary<string, string> { ["FeatureX"] = "enabled" }));
                }
                """, ct);
        };
        tp.Agent.WorkResults.Enqueue(new AgentResult(true, "ok", null, null));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.True(
            final!.State == WorkItemState.AuditFailed,
            $"expected AuditFailed, got {final.State}: {final.LastError}");
        Assert.Equal([1], llm.SeenIterations);
        Assert.Contains("new code path is never wired", final.LastError, StringComparison.OrdinalIgnoreCase);

        static async Task WriteFile(
            ISandbox sandbox,
            string workingDirectory,
            string relativePath,
            string contents,
            CancellationToken ct)
        {
            var result = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "cat > \"$0\"", $"{workingDirectory}/{relativePath}"],
                Stdin = contents,
            }, ct);
            if (!result.Success)
                throw new InvalidOperationException($"failed to write {relativePath}: {result.Stderr}");
        }

        static async Task ExecOk(
            ISandbox sandbox,
            string workingDirectory,
            IReadOnlyList<string> argv,
            CancellationToken ct)
        {
            var result = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = argv,
                WorkingDirectory = workingDirectory,
            }, ct);
            if (!result.Success)
                throw new InvalidOperationException($"command failed: {string.Join(' ', argv)}: {result.Stderr}");
        }
    }

    [Theory]
    [InlineData("node", "package.json", "{\"scripts\":{\"test\":\"node -e 0\"}}\n", "npm test")]
    [InlineData("python", "pyproject.toml", "[project]\nname = \"sample\"\nversion = \"0.1.0\"\n", "pytest ")]
    public async Task BuiltInNodeAndPythonPassingTestGateDoesNotUnlockLlmPanelWithoutBuildEvidence(
        string language,
        string markerFile,
        string markerContents,
        string expectedTestCommand)
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace, $"seed-{language}");
        await File.WriteAllTextAsync(Path.Combine(seed, markerFile), markerContents);
        await TestSupport.RunGit(seed, "add", markerFile);
        await TestSupport.RunGit(seed, "commit", "-m", $"add {language} marker");

        var fakeTools = await CreateFakeAuditToolsAsync();
        var llmRuns = 0;
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            projectAudit: new ProjectAudit
            {
                MaxIterations = 1,
                Languages = [language],
                LanguagesConfigured = true,
                AuditTypes = ["quality"],
                MaxLlmAuditorParallelism = 1,
            },
            presetCatalogOverride: new PresetCatalog(),
            sandboxProvider: new PathInjectingSandboxProvider(fakeTools.Path, fakeTools.Environment),
            credentials: AuditCredentials(),
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);
        tp.Agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            var auditDir = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["test", "-d", "audit"],
                WorkingDirectory = workingDirectory,
            }, ct);
            if (!auditDir.Success)
                return;

            Interlocked.Increment(ref llmRuns);
            await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "printf '%s' '{\"passed\":true,\"findings\":[]}' > audit/result.json"],
                WorkingDirectory = workingDirectory,
            }, ct);
        };
        tp.Agent.WorkPlan.Enqueue(new FileWrite("change.txt", "v1"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("audit-agent-touch.txt", "ok"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.True(
            final!.State == WorkItemState.AuditFailed,
            $"expected AuditFailed, got {final.State}: {final.LastError}");
        Assert.Contains("build/test gate", final.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, llmRuns);
        var toolLog = await File.ReadAllLinesAsync(fakeTools.LogPath);
        Assert.Contains(expectedTestCommand, toolLog);
    }

    private async Task<FakeAuditTools> CreateFakeAuditToolsAsync()
    {
        var bin = Path.Combine(_workspace, "fake-audit-tools-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(bin);
        var log = Path.Combine(_workspace, "fake-audit-tools-" + Guid.NewGuid().ToString("N")[..8] + ".log");
        foreach (var tool in new[] { "prettier", "eslint", "npm", "ruff", "mypy", "pyright", "pytest" })
        {
            var path = Path.Combine(bin, tool);
            await File.WriteAllTextAsync(path, """
                #!/bin/sh
                printf '%s %s\n' "$(basename "$0")" "$*" >> "$CODEYBOX_FAKE_AUDIT_TOOL_LOG"
                exit 0
                """);
            MakeExecutable(path);
        }

        return new FakeAuditTools(
            bin + Path.PathSeparator + "/usr/bin:/bin",
            log,
            new Dictionary<string, string> { ["CODEYBOX_FAKE_AUDIT_TOOL_LOG"] = log });
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

    private static ConstantCredentialProvider AuditCredentials()
        => new(new AgentCredential(
            AgentKind.Claude,
            new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "test-key" },
            new Dictionary<string, string>()));

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "gate test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = "feature/x",
        PushUpstream = false,
    };

    private sealed record FakeAuditTools(
        string Path,
        string LogPath,
        IReadOnlyDictionary<string, string> Environment);

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

            var argv = exec.Argv;
            if (argv.Count > 0 && !Path.IsPathRooted(argv[0]))
            {
                var toolPath = path
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Select(dir => Path.Combine(dir, argv[0]))
                    .FirstOrDefault(File.Exists);
                if (toolPath is not null)
                    argv = [toolPath, .. argv.Skip(1)];
            }

            return inner.ExecAsync(exec with { Argv = argv, ExtraEnvironment = env }, ct);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

// ── Anti-bias: low-quality diff still produces findings ───────────────────────

/// <summary>
/// The shared LLM prompt frame (Defaults/llm-prompt-frame.yaml) tells the
/// auditor that automated CI already built + tested the project so it should
/// not re-run. To prevent that note from biasing the reviewer toward
/// "tests pass = code is fine", the frame also includes an explicit
/// anti-bias disclaimer instructing the reviewer to judge correctness,
/// completeness, and design from the diff and surrounding code.
///
/// This test pins that the disclaimer survives in the rendered prompt and
/// drives the auditor to still produce findings on a low-quality diff.
/// It uses a simulated agent that mirrors the documented intent: when the
/// disclaimer is present in the prompt, the reviewer flags problems in a
/// trivial-but-unwired-and-undertested change.
/// </summary>
public sealed class LlmAuditorAntiBiasOnLowQualityDiffTests
{
    private const string AntiBiasMarker = LlmReviewAuditor.AntiBiasMarker;
    private const string CiAlreadyRanMarker = LlmReviewAuditor.CiAlreadyRanMarker;
    private const string DoNotRunBuildOrTestsMarker = LlmReviewAuditor.DoNotRunBuildOrTestsMarker;

    [Fact]
    public async Task FrameTemplate_AntiBiasDisclaimer_SurvivesPromptRendering_AndFindingsSurface()
    {
        // Frame is the SHIPPED one loaded from the embedded preset catalog —
        // any regression that removes the disclaimer, the CI-note, or the
        // operative "Do NOT run any build or test commands" instruction will
        // fail this test rather than silently un-gating the bias or letting
        // panel auditors re-run the deterministic suite.
        var frameTemplate = new PresetCatalog().LlmPromptFrameTemplate;
        Assert.Contains(CiAlreadyRanMarker, frameTemplate, StringComparison.Ordinal);
        Assert.Contains(DoNotRunBuildOrTestsMarker, frameTemplate, StringComparison.Ordinal);
        Assert.Contains(AntiBiasMarker, frameTemplate, StringComparison.Ordinal);

        var runner = new LowQualityDiffReviewRunner();
        var auditor = new LlmReviewAuditor(new LlmReviewAuditorOptions
        {
            Name = "quality:llm-review",
            Agent = runner,
            ReviewFocus = "- code quality, wiring, test coverage",
            FrameTemplate = frameTemplate,
        });
        var ctx = new AuditContext(
            WorkItemId.New(),
            WorkBranch: "codeybox/test",
            BaseBranch: "main",
            Iteration: 1,
            OriginalPrompt: "Add a feature flag for X.",
            AuditRunner: runner);
        var sandbox = new LowQualityDiffSandbox();

        var result = await auditor.RunAsync(sandbox, "/work", ctx);

        // The CI note, the anti-rerun directive, and the anti-bias disclaimer
        // all reached the agent prompt.
        Assert.Contains(CiAlreadyRanMarker, runner.ObservedPrompt, StringComparison.Ordinal);
        Assert.Contains(DoNotRunBuildOrTestsMarker, runner.ObservedPrompt, StringComparison.Ordinal);
        Assert.Contains(AntiBiasMarker, runner.ObservedPrompt, StringComparison.Ordinal);

        // The deterministic reviewer inspects the low-quality diff exposed by
        // the sandbox. The anti-bias sentence alone is not enough to make it
        // fail: removing the diff context would produce a passing verdict.
        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
        Assert.Contains(result.Findings, f => f.Severity == AuditSeverity.Error);
        Assert.Contains(sandbox.Executed, e => e.Argv.Count >= 2 && e.Argv[0] == "git" && e.Argv[1] == "diff");
        Assert.DoesNotContain(sandbox.Executed, IsBuildOrTestCommand);
    }

    /// <summary>
    /// Stub reviewer for the prompt-frame wiring contract. It reads the diff
    /// from the sandbox and reports the unwired/undertested change only when
    /// both the anti-bias note and the low-quality diff are present.
    /// </summary>
    private sealed class LowQualityDiffReviewRunner : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Claude;
        public string ObservedPrompt { get; private set; } = string.Empty;

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
        {
            ObservedPrompt = prompt;
            var diff = sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "diff", "main...codeybox/test", "--", "src/FeatureFlags.cs", "tests/FeatureFlagsTests.cs"],
                WorkingDirectory = workingDirectory,
            }, ct).GetAwaiter().GetResult().Stdout;

            var lowQualityDiff =
                diff.Contains("public static bool AddFeatureFlagX", StringComparison.Ordinal)
                && !diff.Contains("services.AddSingleton", StringComparison.Ordinal)
                && !diff.Contains("DisabledBranch_ReturnsFalse", StringComparison.Ordinal);
            var shouldReport = prompt.Contains(AntiBiasMarker, StringComparison.Ordinal) && lowQualityDiff;
            var resultJson = shouldReport
                ? """
                  {"passed":false,"findings":[
                    {"severity":"error","title":"new code path is never wired to a caller","description":"AddFeatureFlagX is defined but no consumer invokes it; tests cover it directly only","location":"src/FeatureFlags.cs:42"},
                    {"severity":"error","title":"no test covers the disabled branch","description":"only the enabled branch is asserted; disabled path is unobserved","location":"tests/FeatureFlagsTests.cs:10"}
                  ]}
                  """
                : """
                  {"passed":true,"findings":[]}
                  """;

            if (sandbox is LowQualityDiffSandbox writable)
            {
                writable.ResultJson = resultJson;
            }
            return Task.FromResult(new AgentResult(true, "ok", "review complete", null));
        }
    }

    private sealed class LowQualityDiffSandbox : ISandbox
    {
        public string Id => "low-quality-diff-sandbox";
        public string ResultJson { get; set; } = "{\"passed\":true,\"findings\":[]}";
        public List<SandboxExec> Executed { get; } = [];

        private const string LowQualityDiff = """
            diff --git a/src/FeatureFlags.cs b/src/FeatureFlags.cs
            new file mode 100644
            index 0000000..1111111
            --- /dev/null
            +++ b/src/FeatureFlags.cs
            @@ -0,0 +1,7 @@
            +namespace App;
            +
            +public static class FeatureFlags
            +{
            +    public static bool AddFeatureFlagX(IConfiguration config)
            +        => config["FeatureX"] == "enabled";
            +}
            diff --git a/tests/FeatureFlagsTests.cs b/tests/FeatureFlagsTests.cs
            new file mode 100644
            index 0000000..2222222
            --- /dev/null
            +++ b/tests/FeatureFlagsTests.cs
            @@ -0,0 +1,7 @@
            +public sealed class FeatureFlagsTests
            +{
            +    [Fact]
            +    public void EnabledBranch_ReturnsTrue()
            +        => Assert.True(FeatureFlags.AddFeatureFlagX(Config("enabled")));
            +}
            """;

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Executed.Add(exec);
            if (exec.Argv.Count > 0 && exec.Argv[0] == "cat")
                return Task.FromResult(new SandboxExecResult(0, ResultJson, ""));
            if (exec.Argv.Count >= 2 && exec.Argv[0] == "git" && exec.Argv[1] == "diff")
                return Task.FromResult(new SandboxExecResult(0, LowQualityDiff, ""));
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static bool IsBuildOrTestCommand(SandboxExec exec)
    {
        var joined = string.Join(' ', exec.Argv);
        return joined.Contains("dotnet build", StringComparison.Ordinal)
            || joined.Contains("dotnet test", StringComparison.Ordinal)
            || joined.Contains("npm test", StringComparison.Ordinal)
            || joined.Contains("pytest", StringComparison.Ordinal)
            || joined.Contains("cargo test", StringComparison.Ordinal)
            || joined.Contains("go test", StringComparison.Ordinal);
    }
}

// ── Test helpers (file-scoped) ────────────────────────────────────────────────

file sealed record AuditOutcome(
    bool Passed,
    IReadOnlyList<AuditFinding> Findings,
    bool? BuildTestGateEvidenceVerified = null);

/// <summary>
/// Drives a scripted sequence of outcomes per iteration and lets the test
/// stamp <see cref="IAuditor.Role"/> directly — required to exercise the
/// pipeline's BuildTestGate ordering and short-circuit logic without going
/// through the YAML preset loader.
/// </summary>
file sealed class RoleStampedScriptedAuditor : IAuditor
{
    private readonly Queue<AuditOutcome> _plan;
    private readonly Action<int>? _onRun;
    private readonly BuildTestGateEvidence _gateEvidence;

    public RoleStampedScriptedAuditor(
        string name,
        AuditorRole role,
        IEnumerable<AuditOutcome> plan,
        Action<int>? onRun = null,
        BuildTestGateEvidence gateEvidence = BuildTestGateEvidence.BuildAndTest,
        bool canShortCircuitOnBlockingFinding = false)
    {
        Name = name;
        Role = role;
        _plan = new Queue<AuditOutcome>(plan);
        _onRun = onRun;
        _gateEvidence = gateEvidence;
        CanShortCircuitOnBlockingFinding = canShortCircuitOnBlockingFinding;
    }

    public string Name { get; }
    public string Kind => "tool";
    public AuditCapabilities Required => AuditCapabilities.None;
    public AuditorRole Role { get; }
    public bool CanShortCircuitOnBlockingFinding { get; }
    public BuildTestGateEvidence BuildTestGateEvidence => Role == AuditorRole.BuildTestGate
        ? _gateEvidence
        : BuildTestGateEvidence.None;
    public List<int> SeenIterations { get; } = [];

    public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
    {
        if (_plan.Count == 0)
            throw new InvalidOperationException($"no plan entries left for {Name}");
        SeenIterations.Add(context.Iteration);
        _onRun?.Invoke(context.Iteration);
        var outcome = _plan.Dequeue();
        return Task.FromResult(new AuditResult(
            outcome.Passed,
            outcome.Findings)
        {
            BuildTestGateEvidenceVerified = outcome.BuildTestGateEvidenceVerified,
        });
    }
}

file sealed class MarkedToolReviewAuditor : IAuditor, IRequiresPassedBuildTestGate
{
    private readonly Action<int> _onRun;

    public MarkedToolReviewAuditor(string name, Action<int> onRun)
    {
        Name = name;
        _onRun = onRun;
    }

    public string Name { get; }
    public string Kind => "tool";
    public AuditCapabilities Required => AuditCapabilities.None;

    public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
    {
        _onRun(context.Iteration);
        return Task.FromResult(new AuditResult(true, []));
    }
}

file sealed class HangingBuildTestGateAuditor : IAuditor
{
    public HangingBuildTestGateAuditor(string name) => Name = name;

    public string Name { get; }
    public string Kind => "tool";
    public AuditCapabilities Required => AuditCapabilities.None;
    public AuditorRole Role => AuditorRole.BuildTestGate;
    public BuildTestGateEvidence BuildTestGateEvidence => BuildTestGateEvidence.BuildAndTest;
    public int RunCount { get; private set; }

    public async Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        RunCount++;
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return new AuditResult(true, []);
    }
}

file sealed class CredentialedToolAuditor : IAuditor
{
    private readonly Action<int> _onRun;

    public CredentialedToolAuditor(string name, Action<int> onRun)
    {
        Name = name;
        _onRun = onRun;
    }

    public string Name { get; }
    public string Kind => "tool";
    public AuditCapabilities Required => AuditCapabilities.AgentCredentials;

    public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
    {
        _onRun(context.Iteration);
        return Task.FromResult(new AuditResult(true, []));
    }
}

/// <summary>
/// Stand-in for a production-shaped LLM auditor (Kind="llm" with agent
/// credentials and network required). Always passes; records each iteration on
/// which it was invoked, so tests can assert it was skipped.
/// </summary>
file sealed class RecordingLlmAuditor : IAuditor, IRequiresPassedBuildTestGate
{
    private readonly Action<int> _onRun;

    public RecordingLlmAuditor(string name, Action<int> onRun)
    {
        Name = name;
        _onRun = onRun;
    }

    public string Name { get; }
    public string Kind => "llm";
    public AuditCapabilities Required => AuditCapabilities.AgentCredentials | AuditCapabilities.Network;
    public List<int> SeenIterations { get; } = [];

    public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
    {
        SeenIterations.Add(context.Iteration);
        _onRun(context.Iteration);
        return Task.FromResult(new AuditResult(true, []));
    }
}

file sealed class UnmarkedRecordingLlmAuditor : IAuditor
{
    private readonly Action<int> _onRun;

    public UnmarkedRecordingLlmAuditor(string name, Action<int> onRun)
    {
        Name = name;
        _onRun = onRun;
    }

    public string Name { get; }
    public string Kind => "llm";
    public AuditCapabilities Required => AuditCapabilities.AgentCredentials | AuditCapabilities.Network;
    public List<int> SeenIterations { get; } = [];

    public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
    {
        SeenIterations.Add(context.Iteration);
        _onRun(context.Iteration);
        return Task.FromResult(new AuditResult(true, []));
    }
}

file sealed class LowQualityPipelineLlmAuditor : IAuditor, IRequiresPassedBuildTestGate
{
    public string Name => "quality:llm-review";
    public string Kind => "llm";
    public AuditCapabilities Required => AuditCapabilities.AgentCredentials | AuditCapabilities.Network;
    public List<int> SeenIterations { get; } = [];

    public async Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
    {
        SeenIterations.Add(context.Iteration);
        var feature = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "cat src/FeatureFlags.cs 2>/dev/null || true"],
            WorkingDirectory = workingDirectory,
        }, ct);
        var tests = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "cat tests/FeatureFlagsTests.cs 2>/dev/null || true"],
            WorkingDirectory = workingDirectory,
        }, ct);

        var lowQuality = feature.Stdout.Contains("AddFeatureFlagX", StringComparison.Ordinal)
            && !feature.Stdout.Contains("services.AddSingleton", StringComparison.Ordinal)
            && !tests.Stdout.Contains("DisabledBranch_ReturnsFalse", StringComparison.Ordinal);

        return lowQuality
            ? new AuditResult(false,
                [
                    new AuditFinding(
                        Name,
                        AuditSeverity.Error,
                        "new code path is never wired to a caller",
                        "AddFeatureFlagX is defined but no consumer invokes it; tests cover it directly only",
                        "src/FeatureFlags.cs:5"),
                ])
            : new AuditResult(true, []);
    }
}
