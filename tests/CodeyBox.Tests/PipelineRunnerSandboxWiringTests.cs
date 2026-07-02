using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Pins the wiring fix that broke the in-VM agentic conflict resolver: the
/// pickup-rebase sandbox AND the agent-merge sandbox MUST be created with a
/// non-null agent credential and <c>allowAgentNetwork: true</c>. Without these,
/// the agent CLI invoked in-sandbox by
/// <see cref="AgenticConflictResolver"/> starves for both auth and egress —
/// the exact "agent exited 1 in the resolver sandbox" failure that
/// MergeConflictResolutionFailed items hit after PR #168.
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
    private const string AuditDotnetShimDir = AuditReviewDotnetShim.Directory;
    private const string AuditDotnetShimNotice = AuditReviewDotnetShim.Notice;

    private readonly string _workspace = Directory.CreateTempSubdirectory(
        "codeybox-sandbox-wiring-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task PickupRebaseSandbox_IsCreatedWithAgentCredentialAndOpenNetwork()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var recorder = new RecordingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));

        // PipelineOptions's AgentAllowedHosts must be non-empty so we can tell
        // "credential != null, allowAgentNetwork: true" apart from a
        // "credential null, network disabled" regression — BuildSandboxSpec
        // picks AgentAllowedHosts only when both conditions hold.
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
        AssertCredentialAndOpenNetwork(pickupSpec, "pickup-rebase");
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
    /// Returns a credential with a single marker env var so the recorded
    /// <see cref="SandboxSpec.Environment"/> can tell credential-was-passed
    /// apart from credential-was-nulled at the call site.
    /// </summary>
    private sealed class MarkerCredentialProvider : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
            => Task.FromResult<AgentCredential?>(new AgentCredential(
                agent,
                EnvironmentVariables: new Dictionary<string, string> { [MarkerEnvKey] = MarkerEnvValue },
                Files: new Dictionary<string, string>()));
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
