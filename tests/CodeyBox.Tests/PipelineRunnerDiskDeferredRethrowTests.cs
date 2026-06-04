using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// PipelineRunner has a dedicated <c>catch (SandboxDiskDeferredException)</c>
/// that re-throws so the orchestrator's worker-loop defer-and-requeue handler
/// runs. Without that re-throw the catch-all immediately below would call
/// <c>TransitionFailed</c>, terminally marking the item Failed instead of
/// deferring it. The orchestrator-level <c>OrchestratorDiskDeferredTests</c>
/// substitutes its own <c>IPipelineRunner</c>, so the real PipelineRunner's
/// catch ordering needs its own integration test — otherwise a refactor that
/// drops or reorders the catch clauses leaves all tests green while
/// reintroducing the exact bug the comment warns about.
/// </summary>
[Collection("Pipeline integration")]
public sealed class PipelineRunnerDiskDeferredRethrowTests : IDisposable
{
    private readonly string _workspace;

    public PipelineRunnerDiskDeferredRethrowTests()
    {
        _workspace = Directory.CreateTempSubdirectory("codeybox-pipeline-diskdefer-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task RunAsync_RethrowsSandboxDiskDeferredException_AndLeavesItemInQueued()
    {
        var deferAt = new SandboxDiskDeferredException(
            mountPath: "/fake/mp",
            freeBytes: 1L * 1024 * 1024 * 1024,
            thresholdBytes: 10L * 1024 * 1024 * 1024,
            recheckIn: TimeSpan.FromMinutes(1));
        var fixture = await BuildPipelineAsync(new ThrowingDiskDeferSandboxProvider(deferAt));
        using var store = fixture.Store;

        var thrown = await Assert.ThrowsAsync<SandboxDiskDeferredException>(
            () => fixture.Pipeline.RunAsync(fixture.Item, CancellationToken.None, CancellationToken.None));

        Assert.Same(deferAt, thrown);

        // The catch-all below the SandboxDiskDeferredException catch would have
        // transitioned the item to Failed via TransitionFailed. Asserting the
        // item is still in a non-terminal pre-defer state proves the re-throw
        // ran instead of the demotion path.
        var persisted = await store.GetAsync(fixture.Item.Id);
        Assert.NotNull(persisted);
        Assert.NotEqual(WorkItemState.Failed, persisted!.State);
    }

    [Fact]
    public async Task RunAsync_RethrowsSandboxProvisioningDeferredException_AndLeavesItemNonFailed()
    {
        var deferAt = new SandboxProvisioningDeferredException(
            provider: "multipass",
            operation: "start",
            errorClass: "multipass-start-argument-not-found",
            detail: "multipass start failed after retries",
            recheckIn: TimeSpan.FromMinutes(1));
        var fixture = await BuildPipelineAsync(new ThrowingProvisioningDeferSandboxProvider(deferAt));
        using var store = fixture.Store;

        var thrown = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => fixture.Pipeline.RunAsync(fixture.Item, CancellationToken.None, CancellationToken.None));

        Assert.Same(deferAt, thrown);

        var persisted = await store.GetAsync(fixture.Item.Id);
        Assert.NotNull(persisted);
        Assert.NotEqual(WorkItemState.Failed, persisted!.State);
    }

    [Fact]
    public async Task RunAsync_CheckAndActRethrowsSandboxProvisioningDeferredException_AndLeavesItemNonFailed()
    {
        var deferAt = new SandboxProvisioningDeferredException(
            provider: "multipass",
            operation: "mount",
            errorClass: "multipass-mount-retry-exhausted",
            detail: "multipass mount failed after retries",
            recheckIn: TimeSpan.FromMinutes(1));
        var fixture = await BuildPipelineAsync(
            new ThrowingProvisioningDeferSandboxProvider(deferAt),
            item => item with
            {
                JobType = JobType.CheckAndAct,
                Check = new CheckAndActSpec
                {
                    Question = "Does the repo need work?",
                    ActionableAnswer = true,
                    OnYes = new OnYesActionSpec { Title = "Fix it", Prompt = "go" },
                },
            });
        using var store = fixture.Store;

        var thrown = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => fixture.Pipeline.RunAsync(fixture.Item, CancellationToken.None, CancellationToken.None));

        Assert.Same(deferAt, thrown);

        var persisted = await store.GetAsync(fixture.Item.Id);
        Assert.NotNull(persisted);
        Assert.NotEqual(WorkItemState.Failed, persisted!.State);
    }

    private async Task<(PipelineRunner Pipeline, SqliteWorkItemStore Store, WorkItem Item)> BuildPipelineAsync(
        ISandboxProvider sandboxes,
        Func<WorkItem, WorkItem>? configureItem = null)
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var registry = new AgentRegistry(new IAgentRunner[] { new UnreachableAgent() });
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seed,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            Audit = new ProjectAudit { MaxIterations = 1, AuditTypes = [] },
        });
        var pipeline = new PipelineRunner(
            sandboxes,
            gitHost,
            registry,
            new StaticCredentialProvider(),
            new InMemoryPullRequestService(),
            projects,
            new TestUpstreamFactory(),
            new ProjectAuditorComposer(new ScriptedAuditorCatalog([])),
            store,
            new NullWebhookDispatcher(),
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Queued,
        };
        item = configureItem?.Invoke(item) ?? item;
        await store.CreateAsync(item);
        return (pipeline, store, item);
    }

    /// <summary>
    /// Sandbox provider whose <c>CreateAsync</c> unconditionally throws the
    /// supplied <see cref="SandboxDiskDeferredException"/>. Used to drive the
    /// PipelineRunner's disk-defer catch from a non-multipass test host.
    /// </summary>
    private sealed class ThrowingDiskDeferSandboxProvider : ISandboxProvider
    {
        private readonly SandboxDiskDeferredException _ex;
        public ThrowingDiskDeferSandboxProvider(SandboxDiskDeferredException ex) => _ex = ex;
        public string Name => "throwing-disk-defer";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => throw _ex;
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ThrowingProvisioningDeferSandboxProvider : ISandboxProvider
    {
        private readonly SandboxProvisioningDeferredException _ex;
        public ThrowingProvisioningDeferSandboxProvider(SandboxProvisioningDeferredException ex) => _ex = ex;
        public string Name => "throwing-provisioning-defer";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => throw _ex;
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// Agent runner that fails fast if invoked. Reaching this is itself a test
    /// failure: the sandbox provider should throw the disk-defer exception
    /// before the agent gets to run.
    /// </summary>
    private sealed class UnreachableAgent : IAgentRunner
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
            => throw new InvalidOperationException(
                "agent should never be invoked when the sandbox preflight throws");
    }
}
