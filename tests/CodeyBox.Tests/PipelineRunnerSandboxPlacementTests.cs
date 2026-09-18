using CodeyBox.Agents;
using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using CodeyBox.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// The main pipeline path acquires work-phase sandboxes through placement: a
/// placed member's provider serves the work spec, a permanent refusal fails
/// the item operator-visible (no retry loop), and a transient refusal
/// rethrows as deferred so the existing backoff requeues the item.
/// </summary>
[Collection("Pipeline integration")]
public sealed class PipelineRunnerSandboxPlacementTests : IDisposable
{
    private readonly string _workspace;

    public PipelineRunnerSandboxPlacementTests()
    {
        _workspace = Directory.CreateTempSubdirectory("codeybox-pipeline-placement-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task WorkPhase_CreatesSandboxOnPlacedMemberProvider()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var legacy = new RecordingProvider(new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        var placed = new RecordingProvider(new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        var placer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("local", "placed")),
            new PlacementFakeSandboxProviderRegistry([placed], _ => "placed"));

        using var pipeline = TestSupport.BuildPipeline(
            _workspace,
            seed,
            sandboxProvider: legacy,
            sandboxPlacer: placer);
        pipeline.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "placed work",
            Prompt = "do the work",
            State = WorkItemState.Queued,
        };
        await pipeline.Store.CreateAsync(item);
        await pipeline.Pipeline.RunAsync(item, CancellationToken.None, CancellationToken.None);

        var persisted = await pipeline.Store.GetAsync(item.Id);
        Assert.NotNull(persisted);
        Assert.Equal(WorkItemState.Done, persisted!.State);
        Assert.Single(placed.SpecsForPhase("work"));
        Assert.Empty(legacy.SpecsForPhase("work"));
    }

    [Fact]
    public async Task WorkPhase_Unplaceable_MarksFailedNamingCapabilityWithoutRetrying()
    {
        const string missingCap = "cap-no-member-provides";
        var a = new PlacementFakeSandboxProvider("a");
        var b = new PlacementFakeSandboxProvider("b");
        var placer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("a", "a"),
                SandboxPlacementTestMembers.Member("b", "b")),
            new PlacementFakeSandboxProviderRegistry([a, b]));
        var fixture = await BuildPipelineAsync(
            new PlacementFakeSandboxProvider("legacy"),
            placer,
            item => item with { RequiredCapabilities = [missingCap] });

        // Permanent refusal: no throw, no retry loop — the item fails with
        // the unmet capability named operator-visible.
        await fixture.Pipeline.RunAsync(fixture.Item, CancellationToken.None, CancellationToken.None);

        var persisted = await fixture.Store.GetAsync(fixture.Item.Id);
        Assert.NotNull(persisted);
        Assert.Equal(WorkItemState.Failed, persisted!.State);
        Assert.Contains(missingCap, persisted.LastError);
        Assert.Empty(a.Specs);
        Assert.Empty(b.Specs);
    }

    [Fact]
    public async Task WorkPhase_TransientRefusal_RethrowsDeferredForRequeue()
    {
        var provider = new PlacementFakeSandboxProvider("special");
        var placer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("special", "special", networkProfiles: ["special-only"])),
            new PlacementFakeSandboxProviderRegistry([provider], _ => "special"),
            optionsAccessor: () => new ExecutorPhaseDispatchOptions
            {
                PlacementRecheckIn = TimeSpan.FromSeconds(7),
            });
        var fixture = await BuildPipelineAsync(new PlacementFakeSandboxProvider("legacy"), placer);

        // Transient refusal: rethrows so the orchestrator's existing
        // defer-and-requeue path (not the failure path) runs.
        var thrown = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => fixture.Pipeline.RunAsync(fixture.Item, CancellationToken.None, CancellationToken.None));

        Assert.Equal("no-eligible-host", thrown.ErrorClass);
        Assert.Equal(TimeSpan.FromSeconds(7), thrown.RecheckIn);
        var persisted = await fixture.Store.GetAsync(fixture.Item.Id);
        Assert.NotNull(persisted);
        Assert.NotEqual(WorkItemState.Failed, persisted!.State);
        Assert.Empty(provider.Specs);
    }

    private async Task<(PipelineRunner Pipeline, SqliteWorkItemStore Store, WorkItem Item)> BuildPipelineAsync(
        ISandboxProvider sandboxes,
        SandboxPlacementAcquirer placer,
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
        var webhooks = new NullWebhookDispatcher();
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);
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
            webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions,
            sandboxPlacer: placer);

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

    private sealed class RecordingProvider : ISandboxProvider
    {
        private readonly ISandboxProvider _inner;
        private readonly List<SandboxSpec> _specs = new();

        public RecordingProvider(ISandboxProvider inner) => _inner = inner;

        public string Name => _inner.Name;

        public IReadOnlyList<SandboxSpec> SpecsForPhase(string phase)
        {
            lock (_specs)
                return _specs.Where(s => s.TimingPhase == phase).ToList();
        }

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            lock (_specs) _specs.Add(spec);
            return _inner.CreateAsync(spec, ct);
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => _inner.ListAllManagedAsync(ct);

        public Task DisposeLeakedAsync(string name, CancellationToken ct)
            => _inner.DisposeLeakedAsync(name, ct);
    }

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
            bool captureStructuredStream = false) =>
            throw new InvalidOperationException("Agent must not run before sandbox placement refuses or succeeds.");
    }
}
