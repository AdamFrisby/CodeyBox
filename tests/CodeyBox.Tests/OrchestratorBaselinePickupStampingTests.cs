using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// B1: end-to-end test for the pickup-time stamping write-path in
/// <see cref="OrchestratorService"/>. The orchestrator must call the registered
/// <see cref="IBaselineImageResolver"/> when an item is first picked up (the
/// same SQL UPDATE that records <c>StartedAt</c>) and persist whatever
/// non-null value the resolver returned to
/// <see cref="WorkItem.BaselineImageRef"/>. The in-memory item passed into
/// <see cref="IPipelineRunner"/> must carry that same persisted ref.
/// </summary>
[Collection("Background service timing")]
public sealed class OrchestratorBaselinePickupStampingTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-bsl-pickup-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public OrchestratorBaselinePickupStampingTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        TestTempArtifacts.DeleteSqliteDatabase(_dbPath);
    }

    private static WorkItem MakeItem(string projectId = "p") => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId(projectId),
        Title = "t",
        Prompt = "x",
        State = WorkItemState.Queued,
    };

    /// <summary>
    /// Happy path: a Queued item with no project picked up by the orchestrator
    /// must have BaselineImageRef persisted exactly as the resolver returned.
    /// </summary>
    [Fact]
    public async Task Pickup_NullProject_StampsResolverRefOnWorkItem()
    {
        var resolver = new StubResolver("cb-baseline-from-resolver");
        var pipeline = new CapturingPipelineRunner(_store);
        var queue = new InMemoryTaskQueue();
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 1 };
        var reg = new CancellationRegistry(CancellationToken.None);
        var svc = new OrchestratorService(
            queue, _store, pipeline, reg, opts,
            NullLogger<OrchestratorService>.Instance,
            baselineResolver: resolver);

        var item = MakeItem();
        await _store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);

        await svc.StartAsync(CancellationToken.None);
        await pipeline.RunAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await svc.StopAsync(CancellationToken.None);

        // Persisted row carries the stamped ref.
        var persisted = await _store.GetAsync(item.Id);
        Assert.NotNull(persisted);
        Assert.Equal("cb-baseline-from-resolver", persisted!.BaselineImageRef);
        Assert.NotNull(persisted.StartedAt);

        // The in-memory item handed to the pipeline carries the stamped ref too
        // (the dispatcher rebuilds pipelineItem with the new ref so downstream
        // phases see it without reloading from the store).
        Assert.NotNull(pipeline.LastItem);
        Assert.Equal("cb-baseline-from-resolver", pipeline.LastItem!.BaselineImageRef);
    }

    /// <summary>
    /// Same as the null-project case but with a project loaded — exercises the
    /// other pickup branch (budget-locked path). The stamping write must fire
    /// there too.
    /// </summary>
    [Fact]
    public async Task Pickup_WithProject_StampsResolverRefOnWorkItem()
    {
        var resolver = new StubResolver("cb-baseline-with-project");
        var pipeline = new CapturingPipelineRunner(_store);
        var queue = new InMemoryTaskQueue();
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 1 };
        var reg = new CancellationRegistry(CancellationToken.None);
        var projectRepo = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("proj"),
            DisplayName = "Test",
            RepositoryUrl = "https://example/repo",
            NetworkProfiles = new ProjectNetworkProfiles { Work = "work" },
        });
        var svc = new OrchestratorService(
            queue, _store, pipeline, reg, opts,
            NullLogger<OrchestratorService>.Instance,
            projects: projectRepo,
            baselineResolver: resolver);

        var item = MakeItem("proj");
        await _store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);

        await svc.StartAsync(CancellationToken.None);
        await pipeline.RunAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await svc.StopAsync(CancellationToken.None);

        var persisted = await _store.GetAsync(item.Id);
        Assert.Equal("cb-baseline-with-project", persisted!.BaselineImageRef);
        Assert.Equal("cb-baseline-with-project", pipeline.LastItem!.BaselineImageRef);
        // Resolver was asked with the project's Work profile.
        Assert.Equal("work", resolver.LastProfile);
    }

    /// <summary>
    /// A resolver that returns null leaves BaselineImageRef unset on the item —
    /// pickup must not write a sentinel or empty string. This is the "provider
    /// has no baseline for this combo" path (e.g. UseBaselineImages=false).
    /// </summary>
    [Fact]
    public async Task Pickup_ResolverReturnsNull_LeavesBaselineImageRefNull()
    {
        var resolver = new StubResolver(returns: null);
        var pipeline = new CapturingPipelineRunner(_store);
        var queue = new InMemoryTaskQueue();
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 1 };
        var reg = new CancellationRegistry(CancellationToken.None);
        var svc = new OrchestratorService(
            queue, _store, pipeline, reg, opts,
            NullLogger<OrchestratorService>.Instance,
            baselineResolver: resolver);

        var item = MakeItem();
        await _store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);

        await svc.StartAsync(CancellationToken.None);
        await pipeline.RunAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await svc.StopAsync(CancellationToken.None);

        var persisted = await _store.GetAsync(item.Id);
        Assert.NotNull(persisted);
        Assert.Null(persisted!.BaselineImageRef);
        Assert.NotNull(persisted.StartedAt);
        Assert.Null(pipeline.LastItem!.BaselineImageRef);
    }

    /// <summary>
    /// A resolver that throws must not break pickup — pinning is documented
    /// as an optimisation, not a correctness primitive. The work item is
    /// stamped with StartedAt and proceeds to the pipeline without a pin.
    /// </summary>
    [Fact]
    public async Task Pickup_ResolverThrows_FailsOpenWithoutPin()
    {
        var resolver = new ThrowingResolver();
        var pipeline = new CapturingPipelineRunner(_store);
        var queue = new InMemoryTaskQueue();
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 1 };
        var reg = new CancellationRegistry(CancellationToken.None);
        var svc = new OrchestratorService(
            queue, _store, pipeline, reg, opts,
            NullLogger<OrchestratorService>.Instance,
            baselineResolver: resolver);

        var item = MakeItem();
        await _store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);

        await svc.StartAsync(CancellationToken.None);
        await pipeline.RunAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await svc.StopAsync(CancellationToken.None);

        var persisted = await _store.GetAsync(item.Id);
        Assert.NotNull(persisted);
        Assert.Null(persisted!.BaselineImageRef);
        Assert.NotNull(persisted.StartedAt);
    }

    /// <summary>
    /// When the work item already has a non-null BaselineImageRef (e.g. picked
    /// up earlier, then re-enqueued after a transient defer), the existing pin
    /// is preserved — the resolver's current view must not overwrite it. This
    /// guards against the bug where inverted null-coalescing
    /// (<c>resolverRef ?? item.BaselineImageRef</c>) would clobber an existing
    /// pin every restart.
    /// </summary>
    [Fact]
    public async Task Pickup_ExistingBaselineImageRef_NotOverwritten()
    {
        var resolver = new StubResolver("cb-baseline-newer-from-resolver");
        var pipeline = new CapturingPipelineRunner(_store);
        var queue = new InMemoryTaskQueue();
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 1 };
        var reg = new CancellationRegistry(CancellationToken.None);
        var svc = new OrchestratorService(
            queue, _store, pipeline, reg, opts,
            NullLogger<OrchestratorService>.Instance,
            baselineResolver: resolver);

        // Item already carries a pin but has not yet started. The dispatcher
        // sees StartedAt == null and walks into the stamping branch; the
        // existing BaselineImageRef must survive the write.
        var item = MakeItem() with { BaselineImageRef = "cb-baseline-original-pin" };
        await _store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);

        await svc.StartAsync(CancellationToken.None);
        await pipeline.RunAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await svc.StopAsync(CancellationToken.None);

        var persisted = await _store.GetAsync(item.Id);
        Assert.Equal("cb-baseline-original-pin", persisted!.BaselineImageRef);
    }

    /// <summary>
    /// The pickup path must pin <see cref="WorkItem.BaselineImageRef"/> BEFORE it
    /// calls the router, so the in-VM smoke gate probes the image this dispatch
    /// will actually clone rather than the active baseline. The other tests here
    /// only assert the persisted/pipeline ref <em>after</em> pickup completes —
    /// they would still pass if the pre-routing pin were removed and only the
    /// later SQL stamp remained, while the router silently gated on a null ref.
    /// This test wires a real <see cref="AgentClassRouter"/> with a recording
    /// in-VM gate and asserts the gate saw the resolver's ref (never null),
    /// proving the pin lands before routing (AC#1).
    /// </summary>
    [Fact]
    public async Task Pickup_PinsBaselineRef_BeforeRouterGatesInVmSmoke()
    {
        var resolver = new StubResolver("cb-pin-before-routing");
        var gate = new RecordingInVmSmokeGate();
        var router = BuildRouterWithGate(gate);
        var pipeline = new CapturingPipelineRunner(_store);
        var queue = new InMemoryTaskQueue();
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 1 };
        var reg = new CancellationRegistry(CancellationToken.None);
        var svc = new OrchestratorService(
            queue, _store, pipeline, reg, opts,
            NullLogger<OrchestratorService>.Instance,
            projects: new InMemoryProjectRepository(new Project
            {
                Id = new ProjectId("p"),
                DisplayName = "Test",
                RepositoryUrl = "https://example/repo",
                NetworkProfiles = new ProjectNetworkProfiles { Work = "work" },
            }),
            router: router,
            baselineResolver: resolver);

        var item = MakeItem() with { AgentClassId = "frontier" };
        await _store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);

        await svc.StartAsync(CancellationToken.None);
        await pipeline.RunAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await svc.StopAsync(CancellationToken.None);

        // The router forwards item.BaselineImageRef to the gate per scored member.
        // If the orchestrator pinned before routing, the gate saw the resolver's
        // ref; if the pre-routing pin were dropped it would have seen null.
        Assert.NotEmpty(gate.SeenBaselineRefs);
        Assert.Contains("cb-pin-before-routing", gate.SeenBaselineRefs);
        Assert.DoesNotContain(null, gate.SeenBaselineRefs);
    }

    private static AgentClassRouter BuildRouterWithGate(IInVmSmokeGate gate)
    {
        var cls = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members =
            [
                new() { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
            ],
        };
        var availability = new AgentAvailabilityRegistry(
            new AvailabilityOptions(), TimeProvider.System,
            NullLogger<AgentAvailabilityRegistry>.Instance);
        return new AgentClassRouter(
            [cls],
            [new FakeProbe(AgentKind.Claude, 90.0)],
            new QuotaRouterOptions { MinQuotaPct = 10.0, QuotaRecheckInterval = TimeSpan.FromMinutes(5) },
            NullLogger<AgentClassRouter>.Instance,
            timeProvider: null,
            todModifiers: null,
            quotaFailures: null,
            burnEstimator: null,
            runningCounters: null,
            dispatchAvailability: new AgentDispatchAvailability(availability, gate));
    }

    private sealed class StubResolver : IBaselineImageResolver
    {
        private readonly string? _returns;
        public string? LastProfile { get; private set; }
        public SandboxProfileFlavor LastFlavor { get; private set; }
        public int Calls { get; private set; }

        public StubResolver(string? returns) { _returns = returns; }

        public string? ResolveBaselineRef(string? profileName, SandboxProfileFlavor flavor)
        {
            LastProfile = profileName;
            LastFlavor = flavor;
            Calls++;
            return _returns;
        }
        public Task<IReadOnlyList<BaselineImageInfo>> ListBaselineImagesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BaselineImageInfo>>([]);
        public Task DisposeBaselineImageAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ThrowingResolver : IBaselineImageResolver
    {
        public string? ResolveBaselineRef(string? profileName, SandboxProfileFlavor flavor) =>
            throw new InvalidOperationException("resolver under test failure");
        public Task<IReadOnlyList<BaselineImageInfo>> ListBaselineImagesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BaselineImageInfo>>([]);
        public Task DisposeBaselineImageAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// Pipeline stub that captures the WorkItem instance handed to it by the
    /// dispatcher (so the test can assert what the orchestrator threaded
    /// through after the pickup-time stamping) and then completes the item.
    /// </summary>
    private sealed class CapturingPipelineRunner : IPipelineRunner
    {
        private readonly IWorkItemStore _store;
        public TaskCompletionSource RunAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WorkItem? LastItem { get; private set; }

        public CapturingPipelineRunner(IWorkItemStore store) { _store = store; }

        public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            LastItem = item;
            await _store.UpdateAsync(item.With(WorkItemState.Done), ct);
            RunAttempted.TrySetResult();
        }
    }
}
