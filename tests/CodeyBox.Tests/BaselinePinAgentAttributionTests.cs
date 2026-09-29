using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Baseline pins are agent-scoped: <see cref="WorkItem.BaselineImageRef"/> is
/// content-addressed over what has to be provisioned — including the agent CLI
/// set — so a pin resolved under one agent kind must not survive a re-route to
/// another. These tests exercise the pickup path end to end: the pinned item
/// is re-dispatched through a real <see cref="AgentClassRouter"/> while a stub
/// <see cref="IBaselineImageResolver"/> supplies the live ref and a recording
/// gate captures which baseline each member was probed on.
/// </summary>
[Collection("Background service timing")]
public sealed class BaselinePinAgentAttributionTests : IDisposable
{
    private static readonly AgentKind Copilot = AgentKind.Copilot;
    private static readonly AgentKind Devin = AgentKind.Devin;
    private static readonly AgentKind Claude = AgentKind.Claude;

    private readonly TestScratchDirectory _scratch = TestScratchDirectory.Create("codeybox-pin-agent-");
    private string _dbPath => _scratch.DbPath("pin-agent.db");
    private readonly SqliteWorkItemStore _store;

    public BaselinePinAgentAttributionTests()
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

    /// <summary>
    /// The defect: an item pinned while routed to agent A is re-routed to agent
    /// B and must not launch a sandbox from A's provisioning — the pinned image
    /// can lack B's binary entirely. The router spill (copilot's quota reading
    /// below the floor, devin's above it) reproduces the incident shape.
    /// </summary>
    [Fact]
    public async Task ReRouteToDifferentAgent_ReResolvesPinForNewAgent()
    {
        var gate = new PerMemberSmokeGate();
        var resolver = new StubResolver("cb-devin-era-baseline");
        var router = BuildRouter(
            gate,
            (Copilot, 0.0),   // quota below MinQuotaPct → spills to devin
            (Devin, 90.0));
        var pipeline = new CapturingPipelineRunner(_store);
        var queue = new InMemoryTaskQueue();
        var svc = new OrchestratorService(
            queue, _store, pipeline, new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            router: router,
            projects: new InMemoryProjectRepository(MakeProject()),
            baselineResolver: resolver);

        // The incident shape: dispatched to copilot and pinned to the
        // copilot-era baseline, then re-queued and re-routed to devin.
        var item = MakeItem() with
        {
            Agent = Copilot,
            BaselineImageRef = "cb-copilot-era-baseline",
            BaselineImageAgent = Copilot,
        };
        await _store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);

        await svc.StartAsync(CancellationToken.None);
        await pipeline.RunAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await svc.StopAsync(CancellationToken.None);

        // The pipeline (and the persisted StartedAt write) must carry the ref
        // the new agent resolves to, not the stale copilot pin.
        Assert.NotNull(pipeline.LastItem);
        Assert.Equal(Devin, pipeline.LastItem!.Agent);
        Assert.Equal("cb-devin-era-baseline", pipeline.LastItem.BaselineImageRef);
        Assert.Equal(Devin, pipeline.LastItem.BaselineImageAgent);

        var persisted = await _store.GetAsync(item.Id);
        Assert.Equal("cb-devin-era-baseline", persisted!.BaselineImageRef);
        Assert.Equal(Devin, persisted.BaselineImageAgent);

        // Per-member probe consistency: the pinned image is only evidence for
        // the agent it was resolved under. Copilot was gated on the pin; devin
        // was gated on the active baseline — the image its sandbox would clone
        // once the pin dropped.
        Assert.Contains((Copilot, "cb-copilot-era-baseline"), gate.Seen);
        Assert.Contains((Devin, (string?)null), gate.Seen);
    }

    /// <summary>
    /// A re-dispatch that keeps the same agent must keep the pin: the point of
    /// pinning is that an in-flight item is not silently migrated when the
    /// operator's config (and therefore the live ref) changes underneath it.
    /// The resolver now returns a different ref — the pin still wins.
    /// </summary>
    [Fact]
    public async Task ReDispatchToSameAgent_KeepsPinAcrossBaselineChange()
    {
        var gate = new PerMemberSmokeGate();
        var resolver = new StubResolver("cb-new-live-baseline");
        var router = BuildRouter(gate, (Claude, 90.0));
        var pipeline = new CapturingPipelineRunner(_store);
        var queue = new InMemoryTaskQueue();
        var svc = new OrchestratorService(
            queue, _store, pipeline, new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            router: router,
            projects: new InMemoryProjectRepository(MakeProject()),
            baselineResolver: resolver);

        var item = MakeItem() with
        {
            Agent = Claude,
            BaselineImageRef = "cb-original-pin",
            BaselineImageAgent = Claude,
        };
        await _store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);

        await svc.StartAsync(CancellationToken.None);
        await pipeline.RunAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await svc.StopAsync(CancellationToken.None);

        Assert.NotNull(pipeline.LastItem);
        Assert.Equal("cb-original-pin", pipeline.LastItem!.BaselineImageRef);
        Assert.Equal(Claude, pipeline.LastItem.BaselineImageAgent);

        var persisted = await _store.GetAsync(item.Id);
        Assert.Equal("cb-original-pin", persisted!.BaselineImageRef);
        Assert.Equal(Claude, persisted.BaselineImageAgent);

        // The matching member was gated on the pin — the same image the
        // dispatch clones — never on the live ref it no longer resolves to.
        Assert.Equal([(Claude, "cb-original-pin")], gate.Seen);
    }

    /// <summary>
    /// Re-pickup of an item that already started (re-enqueue after a transient
    /// defer) takes the reconcile-only path — no StartedAt write. The pin must
    /// survive intact for the same agent even though the resolver now reports
    /// a rebaked baseline.
    /// </summary>
    [Fact]
    public async Task RePickup_StartedItem_KeepsPinWhenAgentUnchanged()
    {
        var gate = new PerMemberSmokeGate();
        var resolver = new StubResolver("cb-rebaked-baseline");
        var router = BuildRouter(gate, (Claude, 90.0));
        var pipeline = new CapturingPipelineRunner(_store);
        var queue = new InMemoryTaskQueue();
        var svc = new OrchestratorService(
            queue, _store, pipeline, new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            router: router,
            projects: new InMemoryProjectRepository(MakeProject()),
            baselineResolver: resolver);

        var item = MakeItem() with
        {
            Agent = Claude,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            BaselineImageRef = "cb-inflight-pin",
            BaselineImageAgent = Claude,
        };
        await _store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);

        await svc.StartAsync(CancellationToken.None);
        await pipeline.RunAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await svc.StopAsync(CancellationToken.None);

        Assert.NotNull(pipeline.LastItem);
        Assert.Equal("cb-inflight-pin", pipeline.LastItem!.BaselineImageRef);
        Assert.Equal(Claude, pipeline.LastItem.BaselineImageAgent);
    }

    /// <summary>
    /// A pin with no recorded attribution predates the column and cannot be
    /// matched to a dispatch agent — it is re-resolved rather than trusted, so
    /// a stale legacy pin cannot follow the item into the wrong (or a merely
    /// outdated) baseline.
    /// </summary>
    [Fact]
    public async Task UnattributedPin_ReResolvedOnDispatch()
    {
        var resolver = new StubResolver("cb-current-baseline");
        var router = BuildRouter(new PerMemberSmokeGate(), (Claude, 90.0));
        var pipeline = new CapturingPipelineRunner(_store);
        var queue = new InMemoryTaskQueue();
        var svc = new OrchestratorService(
            queue, _store, pipeline, new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            router: router,
            projects: new InMemoryProjectRepository(MakeProject()),
            baselineResolver: resolver);

        var item = MakeItem() with
        {
            Agent = Claude,
            BaselineImageRef = "cb-legacy-pin",
            BaselineImageAgent = null,
        };
        await _store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);

        await svc.StartAsync(CancellationToken.None);
        await pipeline.RunAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await svc.StopAsync(CancellationToken.None);

        Assert.NotNull(pipeline.LastItem);
        Assert.Equal("cb-current-baseline", pipeline.LastItem!.BaselineImageRef);
        Assert.Equal(Claude, pipeline.LastItem.BaselineImageAgent);
    }

    private static AgentClassRouter BuildRouter(
        IInVmSmokeGate gate,
        params (AgentKind Agent, double QuotaPct)[] members)
    {
        var cls = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members = members
                .Select(m => new AgentMembership
                {
                    Agent = m.Agent,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 100,
                })
                .ToList(),
        };
        var availability = new AgentAvailabilityRegistry(
            new AvailabilityOptions(), TimeProvider.System,
            NullLogger<AgentAvailabilityRegistry>.Instance);
        return new AgentClassRouter(
            [cls],
            members.Select(m => (IAgentQuotaProbe)new FakeProbe(m.Agent, m.QuotaPct)).ToArray(),
            new QuotaRouterOptions { MinQuotaPct = 10.0, QuotaRecheckInterval = TimeSpan.FromMinutes(5) },
            NullLogger<AgentClassRouter>.Instance,
            timeProvider: null,
            todModifiers: null,
            quotaFailures: null,
            burnEstimator: null,
            runningCounters: null,
            dispatchAvailability: new AgentDispatchAvailability(availability, gate));
    }

    /// <summary>
    /// Records the (agent, baselineRef) pair of every gate call so tests can
    /// assert which image each member was probed against. Reports available.
    /// </summary>
    private sealed class PerMemberSmokeGate : IInVmSmokeGate
    {
        public List<(AgentKind Agent, string? BaselineRef)> Seen { get; } = [];
        public bool Enabled => true;

        public Task<AgentAvailability> EnsureAvailableAsync(
            AgentKind kind,
            InVmSmokeSandboxTarget target,
            CancellationToken ct)
        {
            lock (Seen)
            {
                Seen.Add((kind, target.BaselineRef));
            }
            return Task.FromResult(new AgentAvailability(true, null, null));
        }

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

    /// <summary>
    /// Pipeline stub that captures the WorkItem handed to it by the dispatcher
    /// (so the test asserts what the orchestrator threaded through after the
    /// pickup-time pin reconcile) and then completes the item.
    /// </summary>
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
}
