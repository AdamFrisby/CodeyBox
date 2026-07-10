using CodeyBox.Agents.Cursor;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Pins the wiring fix that broke the in-VM agentic conflict resolver. The
/// pickup-rebase sandbox must have agent network and credential tmpfs scope,
/// while its creation-time environment contains no resolver credentials.
/// Direct API keys are scoped to only the active candidate process and file
/// payloads are materialised via stdin immediately before that candidate. The
/// agent-merge sandbox still carries the chosen agent credential at creation
/// time.
///
/// The new resolver-side integration tests in
/// <c>AgenticConflictResolverIntegrationTests</c> assert the resolver's own
/// semantics against an inline-constructed SandboxSpec; they cannot catch a
/// regression that reverts <c>BuildSandboxSpec</c> arguments at the merge /
/// pickup-rebase call sites. This file wraps the real
/// <see cref="ProcessSandboxProvider"/> with a recording decorator and
/// inspects the spec PipelineRunner actually built for each phase, pinning
/// both the credential and the network policy at the call sites where the
/// deadlock-breaker bug lived.
/// </summary>
[Collection("Pipeline integration")]
public sealed class PipelineRunnerSandboxWiringTests : IDisposable
{
    private const string MarkerEnvKey = "CODEYBOX_TEST_MARKER";
    private const string MarkerEnvValue = "marker-credential-present";
    private const string MarkerHost = "agent.example.invalid";
    private const string CursorAuthEnvKey = "CODEYBOX_CURSOR_AUTH_JSON";
    private const string CursorAuthJson = """{"token":"cursor-fallback-token"}""";
    private const string CodexApiKeyEnvKey = "OPENAI_API_KEY";
    private const string CodexApiKeyValue = "codex-candidate-api-key";
    private const string ClaudeApiKeyEnvKey = "ANTHROPIC_API_KEY";
    private const string ClaudeApiKeyValue = "claude-primary-api-key";
    private const string NonCandidateEnvKey = "CODEYBOX_NON_CANDIDATE_SECRET";
    private const string NonCandidateEnvValue = "must-not-enter-resolver-sandbox";
    private const string AuditDotnetShimDir = AuditReviewDotnetShim.Directory;
    private const string AuditDotnetShimNotice = AuditReviewDotnetShim.Notice;

    private readonly string _workspace = Directory.CreateTempSubdirectory(
        "codeybox-sandbox-wiring-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task PickupRebaseSandbox_IsCreatedWithAgentNetworkAndCredentialTmpfsBakingCandidateEnv()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var recorder = new RecordingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));

        // PipelineOptions's AgentAllowedHosts must be non-empty so we can tell
        // the resolver sandbox's agent-network scope apart from the audit-tool
        // network fallback.
        var pipelineOptions = new PipelineOptions
        {
            SandboxImageReference = "ignored",
            AgentAllowedHosts = [MarkerHost],
        };

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            pipelineOptions: pipelineOptions,
            credentials: new MarkerCredentialProvider(),
            sandboxProvider: recorder);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("pickup.txt", "pickup\n"));
        // RebaseExistingWorkBranchOntoFreshBaseAsync gates on two conditions:
        //   (1) workBranch matches the pickup-rebase-owned shape "codeybox/{id[..8]}"
        //       (else the function returns early before sandbox creation);
        //   (2) the resume-from path is NOT the Queued→Reset short-circuit
        //       at PipelineRunner.cs:390 — that branch never builds a sandbox.
        // State=WorkComplete with an already-pushed work-branch satisfies (2)
        // and routes through pickup-rebase to BuildSandboxSpec.
        var itemId = WorkItemId.New();
        var ownedWorkBranch = $"codeybox/{itemId.ToString()[..8]}";
        var item = NewItem(ownedWorkBranch) with
        {
            Id = itemId,
            State = WorkItemState.WorkComplete,
        };

        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "stale.txt", "stale\n", "stale prior attempt");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var pickupSpec = Assert.Single(recorder.SpecsForPhase("pickup"));
        AssertCredentialTmpfsAndOpenNetwork(pickupSpec, "pickup-rebase");
        Assert.False(pickupSpec.Environment.ContainsKey(MarkerEnvKey),
            "pickup-rebase creation environment must not expose a resolver credential globally");
    }

    [Fact]
    public async Task PickupRebaseResolver_CodexPrimaryCursorFallback_MaterialisesCursorCredential()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var primary = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Codex };
        primary.AgenticConflictResults.Enqueue(new AgentResult(
            Success: false,
            Summary: "codex resolver failed before editing",
            Stdout: null,
            Stderr: "ordinary resolver failure"));
        var cursor = new CursorAgentRunner { Binary = await InstallFakeCursorAgentAsync("cursor-fallback") };
        var classRouter = BuildResolverClassRouter(primary, cursor);
        var project = NewResolverProject(seed, AgentKind.Codex);

        var run = await RunPickupRebaseConflictAsync(
            seed,
            project,
            new ResolverCredentialProvider(),
            primary,
            [cursor],
            classRouter);

        Assert.Equal(WorkItemState.Done, run.Final.State);
        Assert.DoesNotContain("Authentication required", run.Final.LastError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Single(primary.AgenticConflictInvocations);
        Assert.Equal(
            CursorAuthJson,
            (await ReadBareBranchFileAsync(run.BarePath, run.WorkBranch, "cursor-auth-observed.json")).TrimEnd('\r', '\n'));
    }

    [Fact]
    public async Task PickupRebaseResolver_MaterialisesCandidateCredentialMounts()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var mountSource = Directory.CreateDirectory(Path.Combine(_workspace, "resolver-adjunct"));
        await File.WriteAllTextAsync(Path.Combine(mountSource.FullName, "marker.txt"), "mount-marker");
        var recorder = new RecordingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        var primary = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Codex };
        primary.AgenticConflictResults.Enqueue(new AgentResult(
            false, "codex resolver failed before editing", null, "ordinary resolver failure"));
        var cursor = new CursorAgentRunner { Binary = await InstallFakeCursorAgentAsync("cursor-mount-fallback") };
        var classRouter = BuildResolverClassRouter(primary, cursor);
        var project = NewResolverProject(seed, AgentKind.Codex);

        var run = await RunPickupRebaseConflictAsync(
            seed,
            project,
            new MountedResolverCredentialProvider(mountSource.FullName),
            primary,
            [cursor],
            classRouter,
            sandboxProvider: recorder);

        Assert.Equal(WorkItemState.Done, run.Final.State);
        var pickupSpec = Assert.Single(recorder.SpecsForPhase("pickup"));
        Assert.Contains(pickupSpec.Mounts, mount =>
            mount.SandboxPath == "/opt/codeybox/resolver-adjunct"
            && mount.HostPath == mountSource.FullName
            && mount.ReadOnly);
    }

    [Fact]
    public async Task PickupRebaseResolver_CreationEnvironmentExcludesAllCandidateCredentials()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var recorder = new RecordingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        var primary = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Codex };
        primary.AgenticConflictResults.Enqueue(new AgentResult(
            Success: false,
            Summary: "codex resolver failed before editing",
            Stdout: null,
            Stderr: "ordinary resolver failure"));
        var cursor = new CursorAgentRunner { Binary = await InstallFakeCursorAgentAsync("cursor-isolation") };
        var registeredNonCandidate = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Opencode };
        var classRouter = BuildResolverClassRouter(primary, cursor);
        var credentials = new TrackingResolverCredentialProvider();
        var project = NewResolverProject(seed, AgentKind.Codex);

        var run = await RunPickupRebaseConflictAsync(
            seed,
            project,
            credentials,
            primary,
            [cursor, registeredNonCandidate],
            classRouter,
            sandboxProvider: recorder);

        Assert.Equal(WorkItemState.Done, run.Final.State);
        Assert.DoesNotContain(AgentKind.Opencode, credentials.RequestedAgents);

        var pickupSpec = Assert.Single(recorder.SpecsForPhase("pickup"));
        Assert.False(pickupSpec.Environment.ContainsKey(CodexApiKeyEnvKey));
        Assert.False(pickupSpec.Environment.ContainsKey(CursorAuthEnvKey));
        Assert.False(pickupSpec.Environment.ContainsKey(NonCandidateEnvKey),
            "non-candidate opencode credential must not enter the resolver sandbox");
        Assert.Equal(
            CursorAuthJson,
            (await ReadBareBranchFileAsync(run.BarePath, run.WorkBranch, "cursor-auth-observed.json")).TrimEnd('\r', '\n'));
    }

    [Fact]
    public async Task PickupRebaseResolver_CursorPrimary_MaterialisesCursorCredential()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var cursor = new CursorAgentRunner { Binary = await InstallFakeCursorAgentAsync("cursor-primary") };
        var project = NewResolverProject(seed, AgentKind.Cursor, defaultAgentClass: null);

        var run = await RunPickupRebaseConflictAsync(
            seed,
            project,
            new ResolverCredentialProvider(),
            cursor,
            []);

        Assert.Equal(WorkItemState.Done, run.Final.State);
        Assert.DoesNotContain("Authentication required", run.Final.LastError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            CursorAuthJson,
            (await ReadBareBranchFileAsync(run.BarePath, run.WorkBranch, "cursor-auth-observed.json")).TrimEnd('\r', '\n'));
    }

    [Fact]
    public async Task PickupRebaseResolver_CodexPrimaryClaudeFileFallback_MaterialisesFallbackFileCredential()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var primary = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Codex };
        primary.AgenticConflictResults.Enqueue(new AgentResult(
            Success: false,
            Summary: "codex resolver failed before editing",
            Stdout: null,
            Stderr: "ordinary resolver failure"));
        var fallback = new FileCredentialAssertingResolverAgent();
        var classRouter = BuildResolverClassRouter(primary, fallback);
        var project = NewResolverProject(seed, AgentKind.Codex);

        var run = await RunPickupRebaseConflictAsync(
            seed,
            project,
            new ResolverCredentialProvider(),
            primary,
            [fallback],
            classRouter);

        Assert.Equal(WorkItemState.Done, run.Final.State);
        Assert.Single(primary.AgenticConflictInvocations);
        Assert.Equal(
            ResolverCredentialProvider.ClaudeCredentialJson,
            (await ReadBareBranchFileAsync(run.BarePath, run.WorkBranch, "file-credential-observed.json")).TrimEnd('\r', '\n'));
    }

    [Fact]
    public async Task PickupRebaseResolver_ClaudePrimaryCodexFileFallback_MaterialisesCodexFallbackFileCredential()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var primary = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Claude };
        primary.AgenticConflictResults.Enqueue(new AgentResult(
            Success: false,
            Summary: "claude resolver failed before editing",
            Stdout: null,
            Stderr: "ordinary resolver failure"));
        var fallback = new FileCredentialAssertingResolverAgent(
            AgentKind.Codex,
            "codex/auth.json",
            "codex-file-credential-observed.json");
        var classRouter = BuildResolverClassRouter(primary, fallback);
        var project = NewResolverProject(seed, AgentKind.Claude);

        var run = await RunPickupRebaseConflictAsync(
            seed,
            project,
            new ResolverCredentialProvider(),
            primary,
            [fallback],
            classRouter);

        Assert.Equal(WorkItemState.Done, run.Final.State);
        Assert.Single(primary.AgenticConflictInvocations);
        Assert.Equal(
            ResolverCredentialProvider.CodexCredentialJson,
            (await ReadBareBranchFileAsync(run.BarePath, run.WorkBranch, "codex-file-credential-observed.json")).TrimEnd('\r', '\n'));
    }

    [Fact]
    public async Task PickupRebaseResolver_CleanRebase_DoesNotExposeApiKeyOnlyPrimary()
    {
        // A clean rebase never starts a resolver CLI, so even the viable
        // primary's API key must remain absent from the sandbox-wide env.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var recorder = new RecordingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        var primary = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Claude };
        var credentials = new ApiKeyOnlyPrimaryCredentialProvider(
            AgentKind.Claude, ClaudeApiKeyEnvKey, ClaudeApiKeyValue);
        var project = NewResolverProject(seed, AgentKind.Claude, defaultAgentClass: null);

        var run = await RunPickupRebaseAsync(
            seed,
            project,
            credentials,
            primary,
            [],
            classRouter: null,
            sandboxProvider: recorder,
            workPath: "work-only.txt",
            workContents: "work branch change\n",
            mainPath: "main-only.txt",
            mainContents: "main branch change\n");

        Assert.Equal(WorkItemState.Done, run.Final.State);
        var pickupSpec = Assert.Single(recorder.SpecsForPhase("pickup"));
        Assert.False(pickupSpec.Environment.ContainsKey(ClaudeApiKeyEnvKey));
    }

    [Fact]
    public async Task PickupRebaseResolver_ApiKeyOnlyCandidate_ReceivesDirectEnvironmentWhenInvoked()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var primary = new DirectEnvironmentAssertingResolverAgent();
        var credentials = new ApiKeyOnlyPrimaryCredentialProvider(
            AgentKind.Codex, CodexApiKeyEnvKey, CodexApiKeyValue);
        var project = NewResolverProject(seed, AgentKind.Codex, defaultAgentClass: null);

        var run = await RunPickupRebaseConflictAsync(
            seed,
            project,
            credentials,
            primary,
            []);

        Assert.Equal(WorkItemState.Done, run.Final.State);
        Assert.Equal(
            CodexApiKeyValue,
            (await ReadBareBranchFileAsync(
                run.BarePath,
                run.WorkBranch,
                "direct-credential-observed.txt")).TrimEnd('\r', '\n'));
    }

    [Fact]
    public async Task PickupRebaseResolver_PreResolutionFailure_DoesNotFailCleanRebase()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var primary = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Codex };
        var fallback = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Cursor };
        var credentials = new CursorPreResolutionFailureCredentialProvider();
        var classRouter = BuildResolverClassRouter(primary, fallback);
        var project = NewResolverProject(seed, AgentKind.Codex);

        var run = await RunPickupRebaseAsync(
            seed,
            project,
            credentials,
            primary,
            [fallback],
            classRouter,
            workPath: "work-only.txt",
            workContents: "work branch change\n",
            mainPath: "main-only.txt",
            mainContents: "main branch change\n");

        Assert.Equal(WorkItemState.Done, run.Final.State);
        Assert.True(credentials.CursorRequests > 0);
        Assert.Empty(primary.AgenticConflictInvocations);
        Assert.Empty(fallback.AgenticConflictInvocations);
        Assert.Equal(
            "work branch change",
            (await ReadBareBranchFileAsync(run.BarePath, run.WorkBranch, "work-only.txt")).TrimEnd('\r', '\n'));
        Assert.Equal(
            "main branch change",
            (await ReadBareBranchFileAsync(run.BarePath, run.WorkBranch, "main-only.txt")).TrimEnd('\r', '\n'));
    }

    [Fact]
    public async Task PickupRebaseResolver_PreResolutionFailureOnConflict_AbortsWithoutMovingWorkBranch()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var primary = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Codex };
        var fallback = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Cursor };
        var credentials = new CursorPreResolutionFailureCredentialProvider();
        var classRouter = BuildResolverClassRouter(primary, fallback);
        var project = NewResolverProject(seed, AgentKind.Codex);

        var run = await RunPickupRebaseConflictAsync(
            seed,
            project,
            credentials,
            primary,
            [fallback],
            classRouter);

        Assert.Equal(WorkItemState.MergeConflictResolutionFailed, run.Final.State);
        Assert.Contains("cursor credential pre-resolution failed", run.Final.LastError, StringComparison.Ordinal);
        Assert.Empty(primary.AgenticConflictInvocations);
        Assert.Empty(fallback.AgenticConflictInvocations);
        Assert.Equal(
            "work branch change",
            (await ReadBareBranchFileAsync(run.BarePath, run.WorkBranch, "README.md")).TrimEnd('\r', '\n'));
    }

    [Fact]
    public async Task AgentMergeSandbox_IsCreatedWithAgentCredentialAndOpenNetwork()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var recorder = new RecordingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));

        var pipelineOptions = new PipelineOptions
        {
            SandboxImageReference = "ignored",
            AgentAllowedHosts = [MarkerHost],
        };

        // A clean (non-conflicting) merge is now completed entirely host-side
        // with no sandbox, so it builds no merge spec to inspect. The merge
        // sandbox is created ONLY on the agentic conflict-resolver path; induce
        // a real conflict (work writes README, the auditor advances main's
        // README during audit) so RunAgentMergePhaseAsync reaches
        // BuildSandboxSpec(... timingPhase: "merge" ...) — the call site whose
        // credential + open-network wiring this test pins.
        var auditor = new MainAdvancingAuditor(_workspace, "README.md", "main\n");
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            pipelineOptions: pipelineOptions,
            credentials: new MarkerCredentialProvider(),
            sandboxProvider: recorder);
        auditor.GitRoot = tp.GitRoot;

        tp.Agent.WorkPlan.Enqueue(new FileWrite("README.md", "work\n"));
        tp.Agent.ConflictResolutionPlan.Enqueue(_ => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["README.md"] = "main\nwork\n",
        });
        var item = NewItem("feature/merge-wiring");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        // RunAgentMergePhaseAsync builds the merge sandbox with timingPhase = "merge".
        var mergeSpec = Assert.Single(recorder.SpecsForPhase("merge"));
        AssertCredentialAndOpenNetwork(mergeSpec, "agent-merge");
    }

    [Fact]
    public async Task BuildSandboxSpec_AppendsRepositoryAllowedHostsToAgentAllowlist()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var pipelineOptions = new PipelineOptions
        {
            SandboxImageReference = "ignored",
            AgentAllowedHosts = [MarkerHost, "api.agent.example.invalid"],
        };

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            pipelineOptions: pipelineOptions);
        var access = new SandboxRepositoryAccess(
            CloneUrlInsideSandbox: "/repo",
            Mounts:
            [
                new SandboxMount
                {
                    SandboxPath = "/repo",
                    HostPath = _workspace,
                    ReadOnly = true,
                },
            ],
            Network: new SandboxNetworkPolicy
            {
                AllowedHosts = ["git-host.example.invalid", MarkerHost.ToUpperInvariant()],
            });
        var credential = new AgentCredential(
            AgentKind.Claude,
            EnvironmentVariables: new Dictionary<string, string> { [MarkerEnvKey] = MarkerEnvValue },
            Files: new Dictionary<string, string>());

        var method = typeof(PipelineRunner).GetMethod(
            "BuildSandboxSpec",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(PipelineRunner), "BuildSandboxSpec");
        var spec = Assert.IsType<SandboxSpec>(method.Invoke(
            tp.Pipeline,
            [
                access,
                credential,
                true,
                null,
                null,
                null,
                SandboxProfileFlavor.Headless,
                null,
                null,
                null,
                false,
                false,
            ]));

        Assert.Contains(MarkerHost, spec.Network.AllowedHosts);
        Assert.Contains("api.agent.example.invalid", spec.Network.AllowedHosts);
        Assert.Contains("git-host.example.invalid", spec.Network.AllowedHosts);
        Assert.Equal(
            1,
            spec.Network.AllowedHosts.Count(host =>
                string.Equals(host, MarkerHost, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task WorkAgentSandbox_PreservesDetachedBatchLaunchModeThroughPipeline()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var provider = new HttpIngestBatchModeProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            sandboxProvider: provider);

        SandboxExec? agentObservedExec = null;
        tp.Agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            var exec = new SandboxExec
            {
                Argv = ["sh", "-c", "cat > \"$0\"", $"{workingDirectory}/pipeline-detached-before-work.txt"],
                Stdin = "before-work\n",
                AgentOutputTransport = sandbox.AgentOutputTransportKind == SandboxAgentOutputTransportKind.HttpIngest
                    ? SandboxAgentOutputTransportPreference.PreferHttpIngest
                    : SandboxAgentOutputTransportPreference.ExecPipe,
                LaunchMode = sandbox.BatchLaunchMode == SandboxBatchLaunchMode.Detached
                    ? SandboxExecLaunchMode.DetachedBatch
                    : SandboxExecLaunchMode.Attached,
            };
            agentObservedExec = exec;
            var result = await sandbox.ExecAsync(exec, ct);
            Assert.True(result.Success, result.Stderr);
        };
        tp.Agent.WorkPlan.Enqueue(new FileWrite("pipeline-detached-work.txt", "work\n"));

        var item = NewItem("feature/pipeline-detached-batch");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.NotNull(agentObservedExec);
        Assert.Equal(SandboxAgentOutputTransportPreference.PreferHttpIngest, agentObservedExec!.AgentOutputTransport);
        Assert.Equal(SandboxExecLaunchMode.DetachedBatch, agentObservedExec.LaunchMode);

        var recorded = Assert.Single(
            provider.RecordedExecs,
            exec => exec.Argv.Contains($"{SandboxConventions.WorkDir}/pipeline-detached-before-work.txt", StringComparer.Ordinal));
        Assert.Equal(SandboxAgentOutputTransportPreference.PreferHttpIngest, recorded.AgentOutputTransport);
        Assert.Equal(SandboxExecLaunchMode.DetachedBatch, recorded.LaunchMode);
    }

    [Fact]
    public async Task AuditActivityTrackingSandbox_PreservesDetachedBatchLaunchModeThroughPipeline()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var provider = new HttpIngestBatchModeProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        var auditor = new BatchLaunchModeAuditor();

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            sandboxProvider: provider);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("activity-tracking-work.txt", "work\n"));
        var item = NewItem("feature/activity-tracking-detached-batch");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.NotNull(auditor.ObservedExec);
        Assert.Equal(SandboxAgentOutputTransportPreference.PreferHttpIngest, auditor.ObservedExec!.AgentOutputTransport);
        Assert.Equal(SandboxExecLaunchMode.DetachedBatch, auditor.ObservedExec.LaunchMode);

        var recorded = Assert.Single(
            provider.RecordedExecs,
            exec => exec.Argv.Contains($"{SandboxConventions.WorkDir}/activity-tracking-audit.txt", StringComparer.Ordinal));
        Assert.Equal(SandboxAgentOutputTransportPreference.PreferHttpIngest, recorded.AgentOutputTransport);
        Assert.Equal(SandboxExecLaunchMode.DetachedBatch, recorded.LaunchMode);
    }

    [Fact]
    public async Task AuditDotnetShim_DoesNotInterceptToolOrBuildTestGateAuditors()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var recorder = new RecordingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        var auditor = new DotnetShimAuditAuditor(
            expectShim: false,
            kind: "tool",
            role: AuditorRole.BuildTestGate);

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            sandboxProvider: recorder);

        tp.Agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            var fakeBin = $"{workingDirectory}/work-fake-dotnet-bin";
            var logPath = $"{workingDirectory}/work-dotnet-real.log";
            await InstallFakeDotnetAsync(sandbox, fakeBin, ct);

            var build = await RunDotnetAsync(
                sandbox,
                includeAuditShim: false,
                fakeBin,
                logPath,
                ["build"],
                ct);

            Assert.True(build.Success, build.Stderr);
            Assert.Contains("real dotnet build", build.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(AuditDotnetShimNotice, build.Stdout, StringComparison.Ordinal);
            Assert.Contains("build", await ReadSandboxFileOrEmptyAsync(sandbox, logPath, ct), StringComparison.Ordinal);
        };
        tp.Agent.WorkPlan.Enqueue(new FileWrite("audit-dotnet-shim.txt", "work\n"));

        var item = NewItem("feature/audit-dotnet-shim-tool-gate");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Empty(auditor.Errors);

        var auditSpec = Assert.Single(recorder.SpecsForPhase("audit"));
        Assert.DoesNotContain(auditSpec.Mounts, m => m.Tmpfs && m.SandboxPath == AuditDotnetShimDir);
        if (auditSpec.Environment.TryGetValue("PATH", out var auditPath))
            Assert.DoesNotContain(AuditDotnetShimDir, auditPath, StringComparison.Ordinal);

        var workSpec = Assert.Single(recorder.SpecsForPhase("work"));
        Assert.DoesNotContain(workSpec.Mounts, m => m.SandboxPath == AuditDotnetShimDir);
        if (workSpec.Environment.TryGetValue("PATH", out var workPath))
            Assert.DoesNotContain(AuditDotnetShimDir, workPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditDotnetShim_InterceptsBuildAndTestForLlmAuditors()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var recorder = new RecordingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        var auditor = new DotnetShimAuditAuditor(
            expectShim: true,
            kind: "llm",
            required: AuditCapabilities.AgentCredentials | AuditCapabilities.Network);

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: TestAuditGates.WithPassedBuildAndTest(auditor),
            sandboxProvider: recorder,
            credentials: new MarkerCredentialProvider());

        tp.Agent.WorkPlan.Enqueue(new FileWrite("audit-dotnet-shim.txt", "work\n"));

        var item = NewItem("feature/audit-dotnet-shim-llm");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Empty(auditor.Errors);

        var auditSpecs = recorder.SpecsForPhase("audit");
        Assert.Equal(2, auditSpecs.Count);
        Assert.Contains(auditSpecs, spec =>
            spec.Environment.TryGetValue("PATH", out var path)
            && path.StartsWith(AuditDotnetShimDir + ":", StringComparison.Ordinal)
            && spec.Mounts.Any(m => m.Tmpfs && m.SandboxPath == AuditDotnetShimDir));
        Assert.Contains(auditSpecs, spec =>
            !spec.Mounts.Any(m => m.SandboxPath == AuditDotnetShimDir)
            && (!spec.Environment.TryGetValue("PATH", out var path)
                || !path.Contains(AuditDotnetShimDir, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AuditDotnetShim_DisabledByPipelineTuning_AllowsLlmAuditDotnetBuildAndTest()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var recorder = new RecordingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        var auditor = new DotnetShimAuditAuditor(
            expectShim: false,
            kind: "llm",
            required: AuditCapabilities.AgentCredentials | AuditCapabilities.Network);
        var tuning = new PipelineTuningSnapshot(new PipelineTuningOptions
        {
            BlockRedundantDotnetBuildTestInAuditSandbox = false,
        });

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: TestAuditGates.WithPassedBuildAndTest(auditor),
            sandboxProvider: recorder,
            credentials: new MarkerCredentialProvider(),
            pipelineTuning: tuning);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("audit-dotnet-shim-disabled.txt", "work\n"));

        var item = NewItem("feature/audit-dotnet-shim-disabled");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Empty(auditor.Errors);

        var auditSpecs = recorder.SpecsForPhase("audit");
        Assert.Equal(2, auditSpecs.Count);
        Assert.All(auditSpecs, auditSpec =>
        {
            Assert.DoesNotContain(auditSpec.Mounts, m => m.SandboxPath == AuditDotnetShimDir);
            if (auditSpec.Environment.TryGetValue("PATH", out var auditPath))
                Assert.DoesNotContain(AuditDotnetShimDir, auditPath, StringComparison.Ordinal);
        });
    }

    // The sandbox-side env var that AuditReviewDotnetShim.Apply injects to arm
    // the absolute-path hardening script. It is the ONLY defense on the
    // production (multipass) provider against an auditor bypassing the PATH
    // shim via an absolute dotnet path (e.g. /usr/bin/dotnet test). The branch
    // that sets it is environment-independent, so it is unit-tested directly
    // here rather than only through the ProcessSandboxProvider integration path
    // (where hardening is always off) — a typo'd provider name or a dropped env
    // var would otherwise silently disable hardening in production with no
    // failing test.
    private const string HardenAbsoluteEnvKey = "CODEYBOX_AUDIT_DOTNET_SHIM_HARDEN_ABSOLUTE";

    [Theory]
    [InlineData("multipass")]
    [InlineData("multipass-remote")]
    public void AuditDotnetShim_ArmsAbsolutePathHardening_OnMultipassProviders(string providerName)
    {
        var shim = AuditReviewDotnetShim.From(new PipelineTuningOptions(), providerName);
        var applied = shim.Apply(BaseAuditSpec());

        Assert.True(
            applied.Environment.TryGetValue(HardenAbsoluteEnvKey, out var value),
            $"provider '{providerName}' must arm absolute-path hardening — it is the only bypass defense on that provider");
        Assert.Equal("1", value);
        AssertShimApplied(applied);
    }

    [Theory]
    [InlineData("process")]
    [InlineData("bubblewrap")]
    [InlineData("multipass-local")] // near-miss: must NOT match the multipass prefix
    [InlineData("Multipass")]       // case mismatch: comparison is Ordinal, not IgnoreCase
    public void AuditDotnetShim_DoesNotArmAbsolutePathHardening_OnOtherProviders(string providerName)
    {
        var shim = AuditReviewDotnetShim.From(new PipelineTuningOptions(), providerName);
        var applied = shim.Apply(BaseAuditSpec());

        Assert.DoesNotContain(HardenAbsoluteEnvKey, applied.Environment.Keys);
        // The PATH shim + tmpfs mount still apply on every provider — only the
        // privileged absolute-path hardening is multipass-scoped.
        AssertShimApplied(applied);
    }

    [Fact]
    public void AuditDotnetShim_Disabled_AppliesNothing_EvenOnMultipass()
    {
        var shim = AuditReviewDotnetShim.From(
            new PipelineTuningOptions { BlockRedundantDotnetBuildTestInAuditSandbox = false },
            "multipass");
        var spec = BaseAuditSpec();
        var applied = shim.Apply(spec);

        Assert.Same(spec, applied);
        Assert.DoesNotContain(HardenAbsoluteEnvKey, applied.Environment.Keys);
        Assert.DoesNotContain(applied.Mounts, m => m.SandboxPath == AuditDotnetShimDir);
    }

    // Behavioral coverage for AuditReviewDotnetShim.PrivilegedHardeningScript —
    // the ONLY defense on the production (multipass) provider against an auditor
    // bypassing the PATH shim by invoking dotnet via an absolute path (e.g.
    // /usr/bin/dotnet test). Every other AuditDotnetShim test exercises the PATH
    // shim only; the ~60-line privileged hardening body never runs there because
    // CODEYBOX_AUDIT_DOTNET_SHIM_HARDEN_ABSOLUTE is only armed on multipass and
    // the ProcessSandboxProvider integration path always leaves it off. This
    // drives the REAL hardening + shim scripts against a throwaway fixture tree
    // so the load-bearing actions run without root or the /codeybox/bin mount:
    // the arm-env gate, moving the real dotnet aside to <target>.codeybox-real,
    // dropping the shim over the target, the {Directory}/* skip guard, and the
    // shim's ${0}.codeybox-real passthrough for absolute invocations.
    [Fact]
    public async Task PrivilegedHardeningScript_MovesRealDotnetAside_AndInterceptsAbsoluteInvocation()
    {
        if (OperatingSystem.IsWindows())
            return; // POSIX shell + unix file modes; the audit sandboxes are Linux.

        var fixture = Directory.CreateTempSubdirectory("codeybox-harden-").FullName;
        try
        {
            var shimDir = Path.Combine(fixture, "codeybox", "bin");
            Directory.CreateDirectory(shimDir);
            var shimPath = Path.Combine(shimDir, "dotnet");
            await File.WriteAllTextAsync(shimPath, AuditReviewDotnetShim.ShimScript);
            MakeExecutable(shimPath);

            // Force the non-sudo branch deterministically: a fake `sudo` that
            // fails `sudo -n true` makes the script fall through to the direct
            // mv/cp/chmod path regardless of whether the host has passwordless
            // sudo (it does on the multipass image, it does not on CI). The
            // non-sudo branch is the one the audit finding singled out as
            // testable without root.
            var fakeSudo = Path.Combine(shimDir, "sudo");
            await File.WriteAllTextAsync(fakeSudo, "#!/bin/sh\nexit 1\n");
            MakeExecutable(fakeSudo);

            // A candidate that lives under the shim directory must be skipped by
            // the {Directory}/* case guard, never moved aside.
            var shimDirSibling = Path.Combine(shimDir, "dotnet-tool");
            await File.WriteAllTextAsync(shimDirSibling, "#!/bin/sh\necho sibling\n");
            MakeExecutable(shimDirSibling);

            // The real dotnet an auditor could reach by absolute path.
            var realDir = Path.Combine(fixture, "opt", "dotnet");
            Directory.CreateDirectory(realDir);
            var target = Path.Combine(realDir, "dotnet");
            const string realBody = "#!/bin/sh\necho \"real dotnet $*\"\n";
            await File.WriteAllTextAsync(target, realBody);
            MakeExecutable(target);

            var script = AuditReviewDotnetShim.BuildPrivilegedHardeningScript(
                shimPath, shimDir, shimDirSibling);

            // Gate: without the arming env var the script is a no-op.
            var noop = await RunHostShimScriptAsync(script, shimDir, target, arm: false);
            Assert.Equal(0, noop.code);
            Assert.Equal(realBody, await File.ReadAllTextAsync(target));
            Assert.False(File.Exists(target + ".codeybox-real"));

            // Armed (non-sudo branch): the real dotnet is moved aside and the
            // shim takes its place.
            var armed = await RunHostShimScriptAsync(script, shimDir, target, arm: true);
            Assert.Equal(0, armed.code);
            Assert.Equal(AuditReviewDotnetShim.ShimScript, await File.ReadAllTextAsync(target));
            Assert.Equal(realBody, await File.ReadAllTextAsync(target + ".codeybox-real"));

            // The shim-directory sibling is left untouched by the skip guard.
            Assert.False(File.Exists(shimDirSibling + ".codeybox-real"));

            // Absolute invocation of the now-shimmed target: build/test are
            // intercepted with the notice and never reach a compiler...
            var test = await RunHostBinaryAsync(target, ["test", "--filter", "X"]);
            Assert.Equal(0, test.code);
            Assert.Contains(AuditDotnetShimNotice, test.stdout, StringComparison.Ordinal);
            // ...while other subcommands exec the moved-aside real dotnet via the
            // shim's ${0}.codeybox-real sibling passthrough.
            var info = await RunHostBinaryAsync(target, ["--info"]);
            Assert.Equal(0, info.code);
            Assert.Contains("real dotnet --info", info.stdout, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(fixture, recursive: true); } catch { }
        }
    }

    private static Task<(int code, string stdout, string stderr)> RunHostShimScriptAsync(
        string script, string shimDir, string target, bool arm)
    {
        // shimDir leads PATH so the script's `command -v dotnet` resolves to the
        // fixture shim (and is skipped) rather than any real host dotnet.
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = shimDir + ":/usr/bin:/bin",
            // Empty (not absent) so an inherited value can never arm the gate.
            ["CODEYBOX_AUDIT_DOTNET_SHIM_HARDEN_ABSOLUTE"] = arm ? "1" : "",
        };
        return RunHostProcessAsync("/bin/sh", ["-s", "--", target], script, env);
    }

    private static Task<(int code, string stdout, string stderr)> RunHostBinaryAsync(
        string path, IReadOnlyList<string> args)
        => RunHostProcessAsync(path, args, stdin: null, env: null);

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static async Task<(int code, string stdout, string stderr)> RunHostProcessAsync(
        string fileName, IReadOnlyList<string> args, string? stdin, IReadOnlyDictionary<string, string>? env)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        if (env is not null)
            foreach (var (k, v) in env)
                psi.Environment[k] = v;

        using var p = System.Diagnostics.Process.Start(psi)!;
        if (stdin is not null)
        {
            await p.StandardInput.WriteAsync(stdin);
            p.StandardInput.Close();
        }
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await stdoutTask, await stderrTask);
    }

    private static SandboxSpec BaseAuditSpec() => new()
    {
        ImageReference = "ignored",
        Environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/local/bin:/usr/bin:/bin",
        },
    };

    private static void AssertShimApplied(SandboxSpec applied)
    {
        Assert.True(applied.Environment.TryGetValue("PATH", out var path));
        Assert.StartsWith(AuditDotnetShimDir + ":", path);
        Assert.Contains(applied.Mounts, m => m.Tmpfs && m.SandboxPath == AuditDotnetShimDir);
    }

    private static void AssertCredentialAndOpenNetwork(SandboxSpec spec, string phaseName)
    {
        Assert.True(spec.Environment.TryGetValue(MarkerEnvKey, out var marker),
            $"{phaseName} sandbox was created without baked credential env vars — credential argument was nulled or the credential's env was not propagated");
        Assert.Equal(MarkerEnvValue, marker);
        Assert.Contains(MarkerHost, spec.Network.AllowedHosts);
        // When allowAgentNetwork is false, BuildSandboxSpec sets AllowedHosts
        // to an empty array regardless of credential. A non-empty AllowedHosts
        // that includes the marker host pins both "allowAgentNetwork: true"
        // AND "credential != null" — those are the two switches the resolver
        // sandbox setup defect (#168 follow-up) flipped to the wrong values.
        Assert.NotEmpty(spec.Network.AllowedHosts);
    }

    private static void AssertCredentialTmpfsAndOpenNetwork(SandboxSpec spec, string phaseName)
    {
        Assert.Contains(
            spec.Mounts,
            mount => mount.Tmpfs
                && string.Equals(mount.SandboxPath, SandboxConventions.CredentialsDir, StringComparison.Ordinal));
        Assert.Contains(MarkerHost, spec.Network.AllowedHosts);
        Assert.True(spec.Network.AllowedHosts.Count > 0, $"{phaseName} sandbox was created without agent network hosts");
    }

    private static Project NewResolverProject(
        string seed,
        AgentKind defaultAgent,
        string? defaultAgentClass = "frontier")
        => new()
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seed,
            DefaultBaseBranch = "main",
            DefaultAgent = defaultAgent,
            DefaultAgentClass = defaultAgentClass,
            Audit = new ProjectAudit { MaxIterations = 1, AuditTypes = [] },
        };

    private Task<PickupRebaseRunResult> RunPickupRebaseConflictAsync(
        string seed,
        Project project,
        ICredentialProvider credentials,
        IAgentRunner primaryRunner,
        IReadOnlyList<IAgentRunner> extraAgentRunners,
        AgentClassRouter? classRouter = null,
        ISandboxProvider? sandboxProvider = null,
        PipelineOptions? pipelineOptions = null)
        => RunPickupRebaseAsync(
            seed,
            project,
            credentials,
            primaryRunner,
            extraAgentRunners,
            classRouter,
            sandboxProvider,
            pipelineOptions,
            workPath: "README.md",
            workContents: "work branch change\n",
            mainPath: "README.md",
            mainContents: "main branch change\n");

    private async Task<PickupRebaseRunResult> RunPickupRebaseAsync(
        string seed,
        Project project,
        ICredentialProvider credentials,
        IAgentRunner primaryRunner,
        IReadOnlyList<IAgentRunner> extraAgentRunners,
        AgentClassRouter? classRouter = null,
        ISandboxProvider? sandboxProvider = null,
        PipelineOptions? pipelineOptions = null,
        string workPath = "README.md",
        string workContents = "work branch change\n",
        string mainPath = "README.md",
        string mainContents = "main branch change\n")
    {
        var agentOverride = primaryRunner as ScriptedAgent;
        var additionalRunners = extraAgentRunners.ToList();
        if (agentOverride is null && !additionalRunners.Any(runner => ReferenceEquals(runner, primaryRunner)))
            additionalRunners.Insert(0, primaryRunner);

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            projectRepository: new InMemoryProjectRepository(project),
            classRouter: classRouter,
            credentials: credentials,
            agentOverride: agentOverride,
            extraAgentRunners: additionalRunners,
            sandboxProvider: sandboxProvider,
            pipelineOptions: pipelineOptions ?? new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
            });

        var itemId = WorkItemId.New();
        var item = NewItem($"codeybox/{itemId.ToString()[..8]}") with
        {
            Id = itemId,
            Agent = project.DefaultAgent,
            AgentClassId = project.DefaultAgentClass,
            State = WorkItemState.WorkComplete,
        };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(
            barePath,
            item.WorkBranch!,
            workPath,
            workContents,
            "work branch changes");
        await CommitToSeedAsync(seed, mainPath, mainContents, "main branch changes");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        return new PickupRebaseRunResult(final!, barePath, item.WorkBranch!);
    }

    private sealed record PickupRebaseRunResult(WorkItem Final, string BarePath, string WorkBranch);

    private static AgentClassRouter BuildResolverClassRouter(params IAgentRunner[] runners)
    {
        var agentClass = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members = runners
                .Select((runner, index) => new AgentMembership
                {
                    Agent = runner.Kind,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 100 - index,
                })
                .ToList(),
        };

        return new AgentClassRouter(
            [agentClass],
            probes: [],
            new QuotaRouterOptions { MinQuotaPct = 10.0 },
            NullLogger<AgentClassRouter>.Instance);
    }

    private static async Task InstallFakeDotnetAsync(ISandbox sandbox, string fakeBin, CancellationToken ct)
    {
        const string script = """
            #!/bin/sh
            printf '%s\n' "$*" >> "$CODEYBOX_FAKE_DOTNET_LOG"
            printf 'real dotnet %s\n' "$*"
            """;

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "mkdir -p \"$1\" && cat > \"$1/dotnet\" && chmod 0755 \"$1/dotnet\"", "sh", fakeBin],
            Stdin = script,
        }, ct);
        Assert.True(result.Success, result.Stderr);
    }

    private static Task<SandboxExecResult> RunDotnetAsync(
        ISandbox sandbox,
        bool includeAuditShim,
        string fakeBin,
        string logPath,
        string[] args,
        CancellationToken ct)
    {
        var exec = includeAuditShim
            ? new SandboxExec
            {
                Argv =
                [
                    "sh",
                    "-c",
                    "fake_bin=$1; log_path=$2; shift 2; case \"$PATH\" in *:*) PATH=\"${PATH%%:*}:$fake_bin:${PATH#*:}\" ;; *) PATH=\"$PATH:$fake_bin\" ;; esac; export PATH CODEYBOX_FAKE_DOTNET_LOG=\"$log_path\"; dotnet \"$@\"",
                    "sh",
                    fakeBin,
                    logPath,
                    .. args,
                ],
            }
            : new SandboxExec
            {
                Argv =
                [
                    "sh",
                    "-c",
                    "fake_bin=$1; log_path=$2; shift 2; PATH=\"$fake_bin:$PATH\"; export PATH CODEYBOX_FAKE_DOTNET_LOG=\"$log_path\"; dotnet \"$@\"",
                    "sh",
                    fakeBin,
                    logPath,
                    .. args,
                ],
            };
        return sandbox.ExecAsync(exec, ct);
    }

    private static async Task<string> ReadSandboxFileOrEmptyAsync(ISandbox sandbox, string path, CancellationToken ct)
    {
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "[ -f \"$1\" ] && cat \"$1\" || true", "sh", path],
        }, ct);
        Assert.True(result.Success, result.Stderr);
        return result.Stdout;
    }

    private async Task CommitToBareBranchAsync(
        string barePath, string branch, string fileName, string contents, string subject)
    {
        var clone = Path.Combine(_workspace, "stale-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", barePath, clone);
        await TestSupport.RunGit(clone, "config", "user.email", "t@l");
        await TestSupport.RunGit(clone, "config", "user.name", "T");
        await TestSupport.RunGit(clone, "checkout", "-B", branch);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(clone, fileName))!);
        await File.WriteAllTextAsync(Path.Combine(clone, fileName), contents);
        await TestSupport.RunGit(clone, "add", fileName);
        await TestSupport.RunGit(clone, "commit", "-m", $"{subject}\n\n{CodeyBoxTrailers.CoAuthoredBy}");
        await TestSupport.RunGit(clone, "push", "origin", $"{branch}:{branch}");
    }

    private async Task CommitToSeedAsync(string repoPath, string path, string content, string message)
    {
        await TestSupport.RunGit(repoPath, "config", "user.email", "t@l");
        await TestSupport.RunGit(repoPath, "config", "user.name", "T");
        await File.WriteAllTextAsync(Path.Combine(repoPath, path), content);
        await TestSupport.RunGit(repoPath, "add", path);
        await TestSupport.RunGit(repoPath, "commit", "-m", message);
    }

    private static async Task<string> ReadBareBranchFileAsync(string barePath, string branch, string path)
    {
        var (_, stdout, _) = await TestSupport.RunGit(barePath, "show", $"{branch}:{path}");
        return stdout;
    }

    private async Task<string> InstallFakeCursorAgentAsync(string name)
    {
        var dir = Path.Combine(_workspace, "fake-cursor-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "agent");
        const string script = """
            #!/bin/sh
            set -eu
            auth="$HOME/.config/cursor/auth.json"
            if [ ! -s "$auth" ]; then
              printf '%s\n' 'Authentication required. Please run '"'"'agent login'"'"' first, or set CURSOR_API_KEY' >&2
              exit 1
            fi
            cat "$auth" > cursor-auth-observed.json
            cat >/dev/null
            printf '%s\n%s\n' 'main branch change' 'work branch change' > README.md
            git add -- README.md cursor-auth-observed.json
            """;
        await File.WriteAllTextAsync(path, script);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        return path;
    }

    private static WorkItem NewItem(string workBranch) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = workBranch,
        PushUpstream = false,
    };

    /// <summary>
    /// Tool auditor that advances <c>main</c>'s copy of a file during the audit
    /// phase, so a work branch touching the same file merges with a conflict —
    /// routing the merge phase through the agentic conflict resolver, which
    /// builds the merge sandbox this test inspects.
    /// </summary>
    private sealed class MainAdvancingAuditor : IAuditor
    {
        private readonly string _workspace;
        private readonly string _path;
        private readonly string _content;

        public string? GitRoot { get; set; }
        public string Name => "advance-main";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public MainAdvancingAuditor(string workspace, string path, string content)
        {
            _workspace = workspace;
            _path = path;
            _content = content;
        }

        public async Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            _ = sandbox;
            _ = workingDirectory;
            _ = ct;
            if (GitRoot is null)
                throw new InvalidOperationException("GitRoot must be assigned before the auditor runs.");
            var barePath = Path.Combine(GitRoot, context.WorkItemId + ".git");
            var clone = Path.Combine(_workspace, "advance-main-" + Guid.NewGuid().ToString("N")[..8]);
            await TestSupport.RunGit(_workspace, "clone", barePath, clone);
            await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
            await TestSupport.RunGit(clone, "config", "user.name", "Test");
            await TestSupport.RunGit(clone, "checkout", context.BaseBranch);
            await File.WriteAllTextAsync(Path.Combine(clone, _path), _content);
            await TestSupport.RunGit(clone, "commit", "-am", "advance main during audit");
            await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{context.BaseBranch}");
            return new AuditResult(true, []);
        }
    }

    /// <summary>
    /// Returns a credential with a single marker env var. Merge/audit call-site
    /// tests assert the marker is present when a chosen agent credential is in
    /// scope; pickup-rebase asserts candidate credentials are pre-resolved but
    /// this marker is not baked into the shared resolver sandbox environment.
    /// </summary>
    private sealed class MarkerCredentialProvider : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
            => Task.FromResult<AgentCredential?>(new AgentCredential(
                agent,
                EnvironmentVariables: new Dictionary<string, string> { [MarkerEnvKey] = MarkerEnvValue },
                Files: new Dictionary<string, string>()));
    }

    private sealed class ResolverCredentialProvider : ICredentialProvider
    {
        public const string ClaudeCredentialJson = """{"claudeAiOauth":{"accessToken":"claude-token"}}""";
        public const string CodexCredentialJson = """{"tokens":{"access_token":"codex-token"}}""";

        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
        {
            AgentCredential? credential = agent switch
            {
                var kind when kind == AgentKind.Codex => new AgentCredential(
                    AgentKind.Codex,
                    EnvironmentVariables: new Dictionary<string, string>
                    {
                        [CodexApiKeyEnvKey] = CodexApiKeyValue,
                    },
                    Files: new Dictionary<string, string>
                    {
                        ["codex/auth.json"] = CodexCredentialJson,
                    }),
                var kind when kind == AgentKind.Cursor => new AgentCredential(
                    AgentKind.Cursor,
                    EnvironmentVariables: new Dictionary<string, string> { [CursorAuthEnvKey] = CursorAuthJson },
                    Files: new Dictionary<string, string>()),
                var kind when kind == AgentKind.Claude => new AgentCredential(
                    AgentKind.Claude,
                    EnvironmentVariables: new Dictionary<string, string>(),
                    Files: new Dictionary<string, string>
                    {
                        ["claude/credentials.json"] = ClaudeCredentialJson,
                    }),
                _ => null,
            };
            return Task.FromResult(credential);
        }
    }

    private sealed class CursorPreResolutionFailureCredentialProvider : ICredentialProvider
    {
        private readonly ResolverCredentialProvider _inner = new();

        public int CursorRequests { get; private set; }

        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
        {
            if (agent == AgentKind.Cursor)
            {
                CursorRequests++;
                throw new InvalidOperationException("cursor credential pre-resolution failed");
            }

            return _inner.GetAsync(agent, ct);
        }
    }

    private sealed class MountedResolverCredentialProvider(string hostPath) : ICredentialProvider
    {
        private readonly ResolverCredentialProvider _inner = new();

        public async Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
        {
            var credential = await _inner.GetAsync(agent, ct);
            return agent == AgentKind.Codex && credential is not null
                ? credential with
                {
                    Mounts =
                    [
                        new SandboxMount
                        {
                            SandboxPath = "/opt/codeybox/resolver-adjunct",
                            HostPath = hostPath,
                            ReadOnly = true,
                            SnapshotForIsolation = true,
                        },
                    ],
                }
                : credential;
        }
    }

    /// <summary>
    /// Returns an env-var-only API-key credential (Files empty) for one agent
    /// kind and null for all others. This mirrors the ANTHROPIC_API_KEY /
    /// OPENAI_API_KEY / GEMINI_API_KEY shape a real credential provider yields
    /// when the operator configured a plain API key rather than a subscription
    /// auth file.
    /// </summary>
    private sealed class ApiKeyOnlyPrimaryCredentialProvider : ICredentialProvider
    {
        private readonly AgentKind _target;
        private readonly string _apiKeyEnvVar;
        private readonly string _apiKeyValue;

        public ApiKeyOnlyPrimaryCredentialProvider(AgentKind target, string apiKeyEnvVar, string apiKeyValue)
        {
            _target = target;
            _apiKeyEnvVar = apiKeyEnvVar;
            _apiKeyValue = apiKeyValue;
        }

        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
        {
            if (agent != _target)
                return Task.FromResult<AgentCredential?>(null);
            return Task.FromResult<AgentCredential?>(new AgentCredential(
                agent,
                EnvironmentVariables: new Dictionary<string, string> { [_apiKeyEnvVar] = _apiKeyValue },
                Files: new Dictionary<string, string>()));
        }
    }

    private sealed class TrackingResolverCredentialProvider : ICredentialProvider
    {
        private readonly ResolverCredentialProvider _inner = new();
        private readonly List<AgentKind> _requestedAgents = new();

        public IReadOnlyList<AgentKind> RequestedAgents
        {
            get
            {
                lock (_requestedAgents)
                    return _requestedAgents.ToList();
            }
        }

        public async Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
        {
            lock (_requestedAgents)
                _requestedAgents.Add(agent);

            if (agent == AgentKind.Opencode)
            {
                return new AgentCredential(
                    AgentKind.Opencode,
                    EnvironmentVariables: new Dictionary<string, string>
                    {
                        [NonCandidateEnvKey] = NonCandidateEnvValue,
                    },
                    Files: new Dictionary<string, string>
                    {
                        ["opencode/auth.json"] = """{"token":"opencode-non-candidate"}""",
                    });
            }

            return await _inner.GetAsync(agent, ct);
        }
    }

    private sealed class FileCredentialAssertingResolverAgent : IAgentRunner, IAgentCredentialEnvironmentPolicy
    {
        private readonly AgentKind _kind;
        private readonly string _credentialPath;
        private readonly string _observationPath;

        public FileCredentialAssertingResolverAgent()
            : this(AgentKind.Claude, "claude/credentials.json", "file-credential-observed.json")
        {
        }

        public FileCredentialAssertingResolverAgent(
            AgentKind kind,
            string credentialPath,
            string observationPath)
        {
            _kind = kind;
            _credentialPath = credentialPath;
            _observationPath = observationPath;
        }

        public AgentKind Kind => _kind;
        public IReadOnlySet<string> DirectCredentialEnvironmentVariables =>
            _kind == AgentKind.Codex
                ? new HashSet<string>(StringComparer.Ordinal) { CodexApiKeyEnvKey }
                : new HashSet<string>(StringComparer.Ordinal);
        public IReadOnlySet<string> FileBackedCredentialEnvironmentVariables { get; } =
            new HashSet<string>(StringComparer.Ordinal);
        public IReadOnlyList<AgentCredentialFileDestination> CredentialFileDestinations => [];

        public async Task<AgentResult> RunAsync(
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
            _ = modelId;
            _ = reasoningMode;
            _ = stdoutChunkCallback;
            _ = captureStructuredStream;

            if (!prompt.StartsWith("# Conflict-resolution mode (in-sandbox agentic resolver)", StringComparison.Ordinal))
                return new AgentResult(false, "unsupported prompt", null, "unsupported prompt");

            var readCredential = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["cat", $"{SandboxConventions.CredentialsDir}/{_credentialPath}"],
            }, ct);
            if (!readCredential.Success)
                return new AgentResult(false, $"missing {_kind.Value} credential file", readCredential.Stdout, readCredential.Stderr);

            if (!prompt.Contains("\"README.md\"", StringComparison.Ordinal))
                return new AgentResult(false, "resolver prompt did not list README.md", null, null);

            var writeCredentialObservation = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "cat > \"$0\"", $"{workingDirectory}/{_observationPath}"],
                Stdin = readCredential.Stdout,
            }, ct);
            if (!writeCredentialObservation.Success)
                return new AgentResult(false, "failed to write credential observation", writeCredentialObservation.Stdout, writeCredentialObservation.Stderr);

            var write = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "cat > \"$0\"", $"{workingDirectory}/README.md"],
                Stdin = "main branch change\nwork branch change\n",
            }, ct);
            if (!write.Success)
                return new AgentResult(false, "failed to write README.md", write.Stdout, write.Stderr);

            var add = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workingDirectory, "add", "--", "README.md", _observationPath],
            }, ct);
            return add.Success
                ? new AgentResult(true, $"{_kind.Value} resolved", null, null)
                : new AgentResult(false, "failed to stage README.md", add.Stdout, add.Stderr);
        }
    }

    private sealed class DirectEnvironmentAssertingResolverAgent : IAgentRunner, IAgentCredentialEnvironmentPolicy
    {
        public AgentKind Kind => AgentKind.Codex;
        public IReadOnlySet<string> DirectCredentialEnvironmentVariables { get; } =
            new HashSet<string>(StringComparer.Ordinal) { CodexApiKeyEnvKey };
        public IReadOnlySet<string> FileBackedCredentialEnvironmentVariables { get; } =
            new HashSet<string>(StringComparer.Ordinal);
        public IReadOnlyList<AgentCredentialFileDestination> CredentialFileDestinations => [];

        public async Task<AgentResult> RunAsync(
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
            var observationPath = $"{workingDirectory}/direct-credential-observed.txt";
            var observe = await sandbox.ExecAsync(new SandboxExec
            {
                Argv =
                [
                    "sh",
                    "-c",
                    "test -n \"$OPENAI_API_KEY\" && printf %s \"$OPENAI_API_KEY\" > \"$1\"",
                    "sh",
                    observationPath,
                ],
            }, ct);
            if (!observe.Success)
                return new AgentResult(false, "direct credential missing", observe.Stdout, observe.Stderr);

            var write = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "cat > \"$0\"", $"{workingDirectory}/README.md"],
                Stdin = "main branch change\nwork branch change\n",
            }, ct);
            if (!write.Success)
                return new AgentResult(false, "failed to resolve README", write.Stdout, write.Stderr);

            var add = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workingDirectory, "add", "--", "README.md", "direct-credential-observed.txt"],
            }, ct);
            return add.Success
                ? new AgentResult(true, "resolved with direct credential", null, null)
                : new AgentResult(false, "failed to stage direct credential observation", add.Stdout, add.Stderr);
        }
    }

    /// <summary>
    /// Captures every <see cref="SandboxSpec"/> the orchestrator passes to
    /// <see cref="ISandboxProvider.CreateAsync"/>, partitioned by
    /// <see cref="SandboxSpec.TimingPhase"/>. The inner provider does the
    /// actual work; this wrapper just records.
    /// </summary>
    private sealed class RecordingSandboxProvider : ISandboxProvider
    {
        private readonly ISandboxProvider _inner;
        private readonly List<SandboxSpec> _specs = new();

        public RecordingSandboxProvider(ISandboxProvider inner) => _inner = inner;

        public string Name => _inner.Name;

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            lock (_specs) _specs.Add(spec);
            return _inner.CreateAsync(spec, ct);
        }

        public IReadOnlyList<SandboxSpec> SpecsForPhase(string phase)
        {
            lock (_specs)
                return _specs.Where(s => s.TimingPhase == phase).ToList();
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => _inner.ListAllManagedAsync(ct);

        public Task DisposeLeakedAsync(string name, CancellationToken ct)
            => _inner.DisposeLeakedAsync(name, ct);
    }

    private sealed class HttpIngestBatchModeProvider(ISandboxProvider inner) : ISandboxProvider
    {
        private readonly List<SandboxExec> _recordedExecs = new();

        public string Name => inner.Name;
        public SandboxAgentOutputTransportKind AgentOutputTransportKind => SandboxAgentOutputTransportKind.HttpIngest;
        public SandboxBatchLaunchMode BatchLaunchMode => SandboxBatchLaunchMode.Detached;

        public IReadOnlyList<SandboxExec> RecordedExecs
        {
            get
            {
                lock (_recordedExecs)
                    return _recordedExecs.ToList();
            }
        }

        public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => new HttpIngestBatchModeSandbox(await inner.CreateAsync(spec, ct), this);

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => inner.ListAllManagedAsync(ct);

        public Task DisposeLeakedAsync(string name, CancellationToken ct)
            => inner.DisposeLeakedAsync(name, ct);

        private void Record(SandboxExec exec)
        {
            lock (_recordedExecs)
                _recordedExecs.Add(exec);
        }

        private sealed class HttpIngestBatchModeSandbox(ISandbox innerSandbox, HttpIngestBatchModeProvider owner) : ISandbox
        {
            public string Id => innerSandbox.Id;
            public SandboxAgentOutputTransportKind AgentOutputTransportKind => SandboxAgentOutputTransportKind.HttpIngest;
            public SandboxBatchLaunchMode BatchLaunchMode => SandboxBatchLaunchMode.Detached;

            public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            {
                owner.Record(exec);
                return innerSandbox.ExecAsync(exec, ct);
            }

            public Task KillActiveExecsAsync(CancellationToken ct = default)
                => innerSandbox.KillActiveExecsAsync(ct);

            public Task<byte[]> GetScreenshotAsync(CancellationToken ct = default)
                => innerSandbox.GetScreenshotAsync(ct);

            public Task SynthesizeInputAsync(IReadOnlyList<SandboxInputEvent> events, CancellationToken ct = default)
                => innerSandbox.SynthesizeInputAsync(events, ct);

            public Task<SandboxAccessibilitySnapshot?> GetAccessibilityAtPointAsync(int x, int y, CancellationToken ct = default)
                => innerSandbox.GetAccessibilityAtPointAsync(x, y, ct);

            public Task<string?> GetAccessibilityTreeJsonAsync(CancellationToken ct = default)
                => innerSandbox.GetAccessibilityTreeJsonAsync(ct);

            public ValueTask DisposeAsync() => innerSandbox.DisposeAsync();
        }
    }

    private sealed class BatchLaunchModeAuditor : IAuditor
    {
        public string Name => "batch-launch-mode";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public SandboxExec? ObservedExec { get; private set; }

        public async Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
        {
            _ = context;
            var exec = new SandboxExec
            {
                Argv = ["sh", "-c", "cat > \"$0\"", $"{workingDirectory}/activity-tracking-audit.txt"],
                Stdin = "audit\n",
                AgentOutputTransport = sandbox.AgentOutputTransportKind == SandboxAgentOutputTransportKind.HttpIngest
                    ? SandboxAgentOutputTransportPreference.PreferHttpIngest
                    : SandboxAgentOutputTransportPreference.ExecPipe,
                LaunchMode = sandbox.BatchLaunchMode == SandboxBatchLaunchMode.Detached
                    ? SandboxExecLaunchMode.DetachedBatch
                    : SandboxExecLaunchMode.Attached,
            };
            ObservedExec = exec;
            var result = await sandbox.ExecAsync(exec, ct);
            return result.Success
                ? new AuditResult(true, [])
                : new AuditResult(false, [new AuditFinding(Name, AuditSeverity.Error, "batch launch mode exec failed", result.Stderr)]);
        }
    }

    private sealed class DotnetShimAuditAuditor(
        bool expectShim,
        string kind = "tool",
        AuditorRole role = AuditorRole.None,
        AuditCapabilities required = AuditCapabilities.None) : IAuditor, IRequiresPassedBuildTestGate
    {
        private readonly List<string> _errors = new();

        public string Name => expectShim ? $"audit-dotnet-shim-{kind}" : $"audit-dotnet-shim-disabled-{kind}";
        public string Kind => kind;
        public AuditCapabilities Required => required;
        public AuditorRole Role => role;
        public BuildTestGateEvidence BuildTestGateEvidence => role == AuditorRole.BuildTestGate
            ? BuildTestGateEvidence.BuildAndTest
            : BuildTestGateEvidence.None;
        public IReadOnlyList<string> Errors => _errors;

        public async Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
        {
            _ = context;
            var fakeBin = $"{workingDirectory}/{Name}-fake-bin";
            var logPath = $"{workingDirectory}/{Name}-real.log";

            try
            {
                await InstallFakeDotnetAsync(sandbox, fakeBin, ct);
                var build = await RunDotnetAsync(sandbox, expectShim, fakeBin, logPath, ["build", "--no-incremental"], ct);
                var test = await RunDotnetAsync(sandbox, expectShim, fakeBin, logPath, ["test", "--filter", "FullyQualifiedName~DotnetShim"], ct);
                var info = await RunDotnetAsync(sandbox, expectShim, fakeBin, logPath, ["--info"], ct);
                var restore = await RunDotnetAsync(sandbox, expectShim, fakeBin, logPath, ["restore", "--locked-mode"], ct);
                var nuget = await RunDotnetAsync(sandbox, expectShim, fakeBin, logPath, ["nuget", "locals", "all", "--list"], ct);
                var realLog = await ReadSandboxFileOrEmptyAsync(sandbox, logPath, ct);

                Require(build.Success, $"dotnet build failed: {build.Stderr}");
                Require(test.Success, $"dotnet test failed: {test.Stderr}");
                Require(info.Success, $"dotnet --info failed: {info.Stderr}");
                Require(restore.Success, $"dotnet restore failed: {restore.Stderr}");
                Require(nuget.Success, $"dotnet nuget failed: {nuget.Stderr}");

                if (expectShim)
                {
                    Require(build.Stdout.Contains(AuditDotnetShimNotice, StringComparison.Ordinal),
                        $"dotnet build was not intercepted; stdout was: {build.Stdout}");
                    Require(test.Stdout.Contains(AuditDotnetShimNotice, StringComparison.Ordinal),
                        $"dotnet test was not intercepted; stdout was: {test.Stdout}");
                    Require(!realLog.Contains("build --no-incremental", StringComparison.Ordinal),
                        $"dotnet build reached the real dotnet: {realLog}");
                    Require(!realLog.Contains("test --filter FullyQualifiedName~DotnetShim", StringComparison.Ordinal),
                        $"dotnet test reached the real dotnet: {realLog}");
                }
                else
                {
                    Require(build.Stdout.Contains("real dotnet build --no-incremental", StringComparison.Ordinal),
                        $"disabled shim did not pass dotnet build through: {build.Stdout}");
                    Require(test.Stdout.Contains("real dotnet test --filter FullyQualifiedName~DotnetShim", StringComparison.Ordinal),
                        $"disabled shim did not pass dotnet test through: {test.Stdout}");
                    Require(realLog.Contains("build --no-incremental", StringComparison.Ordinal),
                        $"disabled shim did not log real dotnet build: {realLog}");
                    Require(realLog.Contains("test --filter FullyQualifiedName~DotnetShim", StringComparison.Ordinal),
                        $"disabled shim did not log real dotnet test: {realLog}");
                }

                Require(info.Stdout.Contains("real dotnet --info", StringComparison.Ordinal),
                    $"dotnet --info did not pass through: {info.Stdout}");
                Require(restore.Stdout.Contains("real dotnet restore --locked-mode", StringComparison.Ordinal),
                    $"dotnet restore did not pass through: {restore.Stdout}");
                Require(nuget.Stdout.Contains("real dotnet nuget locals all --list", StringComparison.Ordinal),
                    $"dotnet nuget did not pass through: {nuget.Stdout}");
                Require(realLog.Contains("--info", StringComparison.Ordinal),
                    $"dotnet --info did not reach real dotnet: {realLog}");
                Require(realLog.Contains("restore --locked-mode", StringComparison.Ordinal),
                    $"dotnet restore did not reach real dotnet: {realLog}");
                Require(realLog.Contains("nuget locals all --list", StringComparison.Ordinal),
                    $"dotnet nuget did not preserve argv: {realLog}");
            }
            catch (Exception ex)
            {
                _errors.Add(ex.ToString());
            }

            return _errors.Count == 0
                ? new AuditResult(true, [])
                : new AuditResult(false, [new AuditFinding(Name, AuditSeverity.Error, "dotnet shim assertion failed", string.Join("\n", _errors))]);
        }

        private void Require(bool condition, string message)
        {
            if (!condition)
                _errors.Add(message);
        }
    }
}
