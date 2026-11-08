using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end tests for the pickup-time stale-baseline-pin policy: when an
/// item's pinned baseline was baked from a different toolchain than live
/// config resolves now, and the dispatch model has changed since the pin, the
/// pickup clears the pin so the item recomputes onto the live baseline (the
/// same outcome as <c>POST /baselines/migrate</c>) instead of running a model
/// the baked CLI does not know. Reproduces the 2026-11-06 incident shape: a
/// CLI upgrade paired with a model switch.
/// </summary>
[Collection("Background service timing")]
public sealed class BaselineStalePinPickupTests : IDisposable
{
    private static readonly AgentKind Claude = AgentKind.Claude;

    private readonly TestScratchDirectory _scratch = TestScratchDirectory.Create("codeybox-stale-pin-");
    private string _dbPath => _scratch.DbPath("stale-pin.db");
    private readonly SqliteWorkItemStore _store;

    public BaselineStalePinPickupTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
        TestScratchDirectory.DeleteSqliteCompanions(_dbPath);
        _scratch.Dispose();
    }

    private static WorkItem MakeItem(string projectId = "p") => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId(projectId),
        Title = "t",
        Prompt = "x",
        State = WorkItemState.Queued,
        AgentClassId = "frontier",
    };

    private static Project MakeProject() => new()
    {
        Id = new ProjectId("p"),
        DisplayName = "Test",
        RepositoryUrl = "https://example.invalid/repo.git",
        NetworkProfiles = new ProjectNetworkProfiles { Work = "work" },
    };

    private static AgentClassRouter BuildRouter(string? modelId)
    {
        var cls = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members =
            [
                new AgentMembership
                {
                    Agent = Claude,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 100,
                    ModelId = modelId,
                },
            ],
        };
        var availability = new AgentAvailabilityRegistry(
            new AvailabilityOptions(), TimeProvider.System,
            NullLogger<AgentAvailabilityRegistry>.Instance);
        return new AgentClassRouter(
            [cls],
            [new FakeProbe(Claude, 90.0)],
            new QuotaRouterOptions { MinQuotaPct = 10.0, QuotaRecheckInterval = TimeSpan.FromMinutes(5) },
            NullLogger<AgentClassRouter>.Instance,
            timeProvider: null,
            todModifiers: null,
            quotaFailures: null,
            burnEstimator: null,
            runningCounters: null,
            dispatchAvailability: new AgentDispatchAvailability(availability, new AlwaysAvailableGate()));
    }

    private sealed class AlwaysAvailableGate : IInVmSmokeGate
    {
        public bool Enabled => true;
        public Task<AgentAvailability> EnsureAvailableAsync(AgentKind kind, InVmSmokeSandboxTarget target, CancellationToken ct) =>
            Task.FromResult(new AgentAvailability(true, null, null));
        public Task ProbeAllAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ProbeAllAsync(InVmSmokeSandboxTarget target, CancellationToken ct) => Task.CompletedTask;
        public Task<AgentAvailability?> ForceProbeAsync(AgentKind kind, CancellationToken ct) =>
            Task.FromResult<AgentAvailability?>(new AgentAvailability(true, null, null));
    }

    private sealed class StubResolver(string? returns) : IBaselineImageResolver
    {
        public string? ResolveBaselineRef(string? profileName, SandboxProfileFlavor flavor) => returns;
        public Task<IReadOnlyList<BaselineImageInfo>> ListBaselineImagesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BaselineImageInfo>>([]);
        public Task DisposeBaselineImageAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class CapturingPipelineRunner(IWorkItemStore store) : IPipelineRunner
    {
        public TaskCompletionSource RunAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WorkItem? LastItem { get; private set; }

        public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            LastItem = item;
            await store.UpdateAsync(item.With(WorkItemState.Done), ct);
            RunAttempted.TrySetResult();
        }
    }

    private async Task<WorkItem> PickUpAsync(
        WorkItem item,
        string? liveRef,
        string? dispatchModel,
        BaselineStalePinMode mode = BaselineStalePinMode.MigrateOnToolchainAndModelChange)
    {
        var pipeline = new CapturingPipelineRunner(_store);
        var queue = new InMemoryTaskQueue();
        var svc = new OrchestratorService(
            queue, _store, pipeline, new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            router: BuildRouter(dispatchModel),
            projects: new InMemoryProjectRepository(MakeProject()),
            baselineResolver: new StubResolver(liveRef),
            stalePinOptions: () => new BaselineStalePinOptions { Mode = mode });

        await _store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);

        await svc.StartAsync(CancellationToken.None);
        await pipeline.RunAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await svc.StopAsync(CancellationToken.None);

        Assert.NotNull(pipeline.LastItem);
        return pipeline.LastItem!;
    }

    /// <summary>
    /// The incident: item pinned to the old-toolchain baseline with the old
    /// model, live config now resolves a new-toolchain baseline, and routing
    /// dispatches the new model. The pickup must migrate the pin onto the
    /// live baseline and restamp the model attribution.
    /// </summary>
    [Fact]
    public async Task Pickup_ToolchainAndModelChanged_MigratesPinToLive()
    {
        var oldPin = BaselinePin.FormatScopedPin("incus", "aaaaaaaaaaaa", "vm-old");
        var live = BaselinePin.FormatScopedPin("incus", "bbbbbbbbbbbb", "vm-new");
        var item = MakeItem() with
        {
            Agent = Claude,
            ModelId = "old-model",
            BaselineImageRef = oldPin,
            BaselineImageAgent = Claude,
            BaselineImageModelId = "old-model",
        };

        var picked = await PickUpAsync(item, live, dispatchModel: "new-model");

        Assert.Equal(live, picked.BaselineImageRef);
        Assert.Equal(Claude, picked.BaselineImageAgent);
        Assert.Equal("new-model", picked.BaselineImageModelId);

        var persisted = await _store.GetAsync(item.Id);
        Assert.Equal(live, persisted!.BaselineImageRef);
        Assert.Equal("new-model", persisted.BaselineImageModelId);
    }

    /// <summary>
    /// CLI-only upgrade: the toolchain moved but the dispatch model matches
    /// the pinned model, so the old CLI still serves it and the pin survives.
    /// </summary>
    [Fact]
    public async Task Pickup_ToolchainChanged_ModelUnchanged_KeepsPin()
    {
        var oldPin = BaselinePin.FormatScopedPin("incus", "aaaaaaaaaaaa", "vm-old");
        var live = BaselinePin.FormatScopedPin("incus", "bbbbbbbbbbbb", "vm-new");
        var item = MakeItem() with
        {
            Agent = Claude,
            BaselineImageRef = oldPin,
            BaselineImageAgent = Claude,
            BaselineImageModelId = "same-model",
        };

        var picked = await PickUpAsync(item, live, dispatchModel: "same-model");

        Assert.Equal(oldPin, picked.BaselineImageRef);
        Assert.Equal("same-model", picked.BaselineImageModelId);
    }

    /// <summary>
    /// Policy off: even the incident combination keeps its pin, restoring the
    /// legacy behaviour for operators who migrate manually.
    /// </summary>
    [Fact]
    public async Task Pickup_PolicyDisabled_KeepsStalePin()
    {
        var oldPin = BaselinePin.FormatScopedPin("incus", "aaaaaaaaaaaa", "vm-old");
        var live = BaselinePin.FormatScopedPin("incus", "bbbbbbbbbbbb", "vm-new");
        var item = MakeItem() with
        {
            Agent = Claude,
            BaselineImageRef = oldPin,
            BaselineImageAgent = Claude,
            BaselineImageModelId = "old-model",
        };

        var picked = await PickUpAsync(item, live, dispatchModel: "new-model", BaselineStalePinMode.Disabled);

        Assert.Equal(oldPin, picked.BaselineImageRef);
    }

    /// <summary>
    /// Same toolchain, new model: the pin stays (the re-resolve would produce
    /// the identical ref) but the model attribution is restamped so later
    /// pickups compare against the current dispatch model.
    /// </summary>
    [Fact]
    public async Task Pickup_SameToolchain_NewModel_RefreshesModelAttribution()
    {
        var pin = BaselinePin.FormatScopedPin("incus", "aaaaaaaaaaaa", "vm-1");
        var item = MakeItem() with
        {
            Agent = Claude,
            BaselineImageRef = pin,
            BaselineImageAgent = Claude,
            BaselineImageModelId = "old-model",
        };

        var picked = await PickUpAsync(item, pin, dispatchModel: "new-model");

        Assert.Equal(pin, picked.BaselineImageRef);
        Assert.Equal("new-model", picked.BaselineImageModelId);
    }
}
