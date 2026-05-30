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

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            pipelineOptions: pipelineOptions,
            credentials: new MarkerCredentialProvider(),
            sandboxProvider: recorder);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("merge.txt", "merge\n"));
        var item = NewItem("feature/merge-wiring");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        // RunAgentMergePhaseAsync builds the merge sandbox with timingPhase = "merge".
        var mergeSpec = Assert.Single(recorder.SpecsForPhase("merge"));
        AssertCredentialAndOpenNetwork(mergeSpec, "agent-merge");
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
}
