using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Integration tests for the per-agent concurrency cap behaviour. Uses an
/// in-memory store + queue and a pipeline that blocks until released so we
/// can pin items into "running" state and inspect the per-agent counts.
/// </summary>
[Collection("Background service timing")]
public sealed class OrchestratorPerAgentConcurrencyTests : IDisposable
{
    private static readonly AgentKind Codex = AgentKind.Codex;
    private static readonly AgentKind Claude = AgentKind.Claude;
    private static readonly AgentKind Gemini = AgentKind.Gemini;

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-peragent-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public OrchestratorPerAgentConcurrencyTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkItem Item(string title = "t") => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("p"),
        Title = title,
        Prompt = "p",
        AgentClassId = "frontier",
    };

    private static AgentClass FrontierClass() => new()
    {
        Id = "frontier",
        DisplayName = "Frontier",
        Members =
        [
            new AgentMembership { Agent = Codex,  Billing = AgentBilling.Subscription, QualityScore = 100 },
            new AgentMembership { Agent = Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
            new AgentMembership { Agent = Gemini, Billing = AgentBilling.Subscription, QualityScore = 100 },
        ],
    };

    [Fact]
    public async Task PerAgentCaps_AreRespected_ForAllMembers()
    {
        // Caps: codex=1, claude=2, gemini=1; Concurrency=4; 5 items dispatched.
        // Assertion: at no observed moment do per-agent counts exceed their caps,
        // and total in-flight never exceeds the global cap.
        // The router's quality scores are equal so members are tied; the router
        // breaks the tie by config order (codex, claude, gemini) — but we set
        // distinct quality scores per probe so each item routes deterministically.

        // To force distinct routing, use one class per item-target — simpler:
        // we drive routing by configuring availability so the router skips members
        // until it reaches the desired one. But to keep this test deterministic
        // and focused on caps (not router preference), we use three classes —
        // one per agent — and assign items round-robin.

        var classes = new[]
        {
            SingleAgentClass("codex-cls", Codex),
            SingleAgentClass("claude-cls", Claude),
            SingleAgentClass("gemini-cls", Gemini),
        };

        var concurrency = new AgentConcurrencyOptions
        {
            Members =
            {
                ["codex"]  = new AgentConcurrencyEntry { MaxConcurrent = 1 },
                ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 2 },
                ["gemini"] = new AgentConcurrencyEntry { MaxConcurrent = 1 },
            }
        };

        var probes = new IAgentQuotaProbe[]
        {
            new FakeProbe(Codex, 100.0),
            new FakeProbe(Claude, 100.0),
            new FakeProbe(Gemini, 100.0),
        };
        var router = new AgentClassRouter(
            classes,
            probes,
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);

        var pipeline = new PinnedPipelineRunner(_store);
        var queue = new InMemoryTaskQueue();
        var reg = new CancellationRegistry(CancellationToken.None);
        var orchestrator = new OrchestratorService(
            queue, _store, pipeline, reg,
            new OrchestratorOptions { MaxConcurrentWorkers = 4 },
            NullLogger<OrchestratorService>.Instance,
            router: router,
            agentConcurrency: concurrency);

        // 5 items: 3 codex-routed, 1 claude-routed, 1 gemini-routed.
        // (Disproportionate codex on purpose so the per-agent cap definitely bites.)
        var codexA = Item("c-a") with { AgentClassId = "codex-cls" };
        var codexB = Item("c-b") with { AgentClassId = "codex-cls" };
        var codexC = Item("c-c") with { AgentClassId = "codex-cls" };
        var claudeA = Item("cl-a") with { AgentClassId = "claude-cls" };
        var geminiA = Item("g-a") with { AgentClassId = "gemini-cls" };

        foreach (var it in new[] { codexA, codexB, codexC, claudeA, geminiA })
        {
            await _store.CreateAsync(it);
            await queue.EnqueueAsync(it.Id);
        }

        var observed = new ConcurrentBag<(AgentKind Agent, int Count)>();
        using var monitorCts = new CancellationTokenSource();
        var monitor = Task.Run(async () =>
        {
            while (!monitorCts.IsCancellationRequested)
            {
                foreach (var kv in orchestrator.Snapshot())
                    observed.Add((kv.Key, kv.Value));
                try { await Task.Delay(25, monitorCts.Token); }
                catch (OperationCanceledException) { return; }
            }
        });

        await orchestrator.StartAsync(CancellationToken.None);

        // Wait until at least 3 items are pinned in-flight (≤ codex 1 + claude 2 ≤ 3,
        // gemini 1 may bring total to 4). Then sample the running counts.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var total = orchestrator.Snapshot().Values.Sum();
            if (total >= 3) break;
            await Task.Delay(50);
        }

        // At any sampled moment: codex ≤ 1, claude ≤ 2, gemini ≤ 1, total ≤ 4.
        foreach (var (agent, count) in observed)
        {
            if (agent == Codex) Assert.True(count <= 1, $"codex cap exceeded: observed {count}");
            else if (agent == Claude) Assert.True(count <= 2, $"claude cap exceeded: observed {count}");
            else if (agent == Gemini) Assert.True(count <= 1, $"gemini cap exceeded: observed {count}");
        }

        var snap = orchestrator.Snapshot();
        Assert.True(snap.Values.Sum() <= 4, $"global cap exceeded: total {snap.Values.Sum()}");

        // Release the pipelines and let everything drain.
        pipeline.Release();
        await orchestrator.StopAsync(CancellationToken.None);
        monitorCts.Cancel();
        await monitor;
    }

    [Fact]
    public async Task Dispatch_EmitsDispatchCountMeasurement()
    {
        var probes = new IAgentQuotaProbe[] { new FakeProbe(Codex, 100.0) };
        var router = new AgentClassRouter(
            [SingleAgentClass("codex-cls", Codex)],
            probes,
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);

        var pipeline = new PinnedPipelineRunner(_store);
        var queue = new InMemoryTaskQueue();
        var orchestrator = new OrchestratorService(
            queue, _store, pipeline, new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 2 },
            NullLogger<OrchestratorService>.Instance,
            router: router);

        var item = Item("dispatch-metric") with { AgentClassId = "codex-cls" };
        await _store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);

        // Capture must be live before the dispatch loop spawns the worker.
        using var metrics = new MetricCapture("codeybox.dispatch.count");
        await orchestrator.StartAsync(CancellationToken.None);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline && orchestrator.CurrentlyRunningTotal < 1)
            await Task.Delay(25);

        Assert.True(metrics.Items.Any(m => m.Instrument == "codeybox.dispatch.count"),
            "expected a codeybox.dispatch.count measurement when a worker is spawned");

        pipeline.Release();
        await orchestrator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TryReserveAgentSlot_AfterRelease_AllowsReReservation()
    {
        // Regression: an earlier revision of TryReserveAgentSlot used TryAdd
        // when the dictionary key existed at 0, which always failed and pinned
        // a CPU core in the while(true) loop. This test reserves cap, releases,
        // and re-reserves — the buggy version hangs here.
        var concurrency = new AgentConcurrencyOptions
        {
            Members = { ["codex"] = new AgentConcurrencyEntry { MaxConcurrent = 1 } }
        };
        var orchestrator = new OrchestratorService(
            new InMemoryTaskQueue(), _store, new PinnedPipelineRunner(_store),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            agentConcurrency: concurrency);

        Assert.True(orchestrator.TryReserveAgentSlotForTest(Codex));
        Assert.False(orchestrator.TryReserveAgentSlotForTest(Codex));
        Assert.Equal(1, orchestrator.GetRunning(Codex));

        orchestrator.ReleaseAgentSlotForTest(Codex);
        Assert.Equal(0, orchestrator.GetRunning(Codex));

        // The buggy version would spin forever here.
        var task = Task.Run(() => orchestrator.TryReserveAgentSlotForTest(Codex));
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(task, completed); // TryReserveAgentSlot hung after Release — hot-spin regression
        Assert.True(await task);
        Assert.Equal(1, orchestrator.GetRunning(Codex));

        orchestrator.ReleaseAgentSlotForTest(Codex);
        Assert.Equal(0, orchestrator.GetRunning(Codex));
    }

    [Fact]
    public void HasCapacity_ReflectsConfiguredCapAndLiveReservations()
    {
        var concurrency = new AgentConcurrencyOptions
        {
            Members = { ["codex"] = new AgentConcurrencyEntry { MaxConcurrent = 1 } }
        };
        var orchestrator = new OrchestratorService(
            new InMemoryTaskQueue(), _store, new PinnedPipelineRunner(_store),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 2 },
            NullLogger<OrchestratorService>.Instance,
            agentConcurrency: concurrency);

        Assert.True(orchestrator.HasCapacity(Codex));
        Assert.True(orchestrator.TryReserveAgentSlotForTest(Codex));
        Assert.False(orchestrator.HasCapacity(Codex));

        orchestrator.ReleaseAgentSlotForTest(Codex);
        Assert.True(orchestrator.HasCapacity(Codex));

        Assert.True(orchestrator.TryReserveAgentSlotForTest(Claude));
        Assert.True(orchestrator.HasCapacity(Claude));
        orchestrator.ReleaseAgentSlotForTest(Claude);
    }

    [Fact]
    public async Task TryReserveAgentSlot_UnderConcurrentReservers_DoesNotExceedCap()
    {
        // Spec acceptance: per-agent cap is never violated under concurrent
        // dispatch. 100 parallel reservers against cap=3 must give exactly 3
        // successes; the loop's TryUpdate-CAS path must serialise the increment.
        var concurrency = new AgentConcurrencyOptions
        {
            Members = { ["codex"] = new AgentConcurrencyEntry { MaxConcurrent = 3 } }
        };
        var orchestrator = new OrchestratorService(
            new InMemoryTaskQueue(), _store, new PinnedPipelineRunner(_store),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 100 },
            NullLogger<OrchestratorService>.Instance,
            agentConcurrency: concurrency);

        var successes = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, 100), async (_, _) =>
        {
            if (orchestrator.TryReserveAgentSlotForTest(Codex))
                Interlocked.Increment(ref successes);
            await Task.Yield();
        });

        Assert.Equal(3, successes);
        Assert.Equal(3, orchestrator.GetRunning(Codex));
    }

    [Fact]
    public void TryReserveAgentSlot_NoCapConfigured_AlwaysSucceedsAndIncrementsCounter()
    {
        // The "no per-agent cap" branch goes through AddOrUpdate so the
        // /concurrency surface still reflects the live in-flight count.
        var orchestrator = new OrchestratorService(
            new InMemoryTaskQueue(), _store, new PinnedPipelineRunner(_store),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 10 },
            NullLogger<OrchestratorService>.Instance);

        for (var i = 0; i < 5; i++)
            Assert.True(orchestrator.TryReserveAgentSlotForTest(Codex));
        Assert.Equal(5, orchestrator.GetRunning(Codex));

        for (var i = 0; i < 5; i++) orchestrator.ReleaseAgentSlotForTest(Codex);
        Assert.Equal(0, orchestrator.GetRunning(Codex));

        // After full drain, key is removed from the dictionary so Snapshot is empty.
        Assert.Empty(orchestrator.Snapshot());
    }

    [Fact]
    public void GetConcurrencyState_ReflectsLiveRunningCounts()
    {
        // The empty-state shape is asserted by GetConcurrencyState_ReflectsCapsAndCounts;
        // this test exercises the >0 filter and the per-agent count surface.
        var concurrency = new AgentConcurrencyOptions
        {
            Members =
            {
                ["codex"]  = new AgentConcurrencyEntry { MaxConcurrent = 2 },
                ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 2 },
            }
        };
        var orchestrator = new OrchestratorService(
            new InMemoryTaskQueue(), _store, new PinnedPipelineRunner(_store),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 4 },
            NullLogger<OrchestratorService>.Instance,
            agentConcurrency: concurrency);

        orchestrator.TryReserveAgentSlotForTest(Codex);
        orchestrator.TryReserveAgentSlotForTest(Codex);
        orchestrator.TryReserveAgentSlotForTest(Claude);

        var state = orchestrator.GetConcurrencyState();
        Assert.Equal(2, state.CurrentlyRunningPerAgent["codex"]);
        Assert.Equal(1, state.CurrentlyRunningPerAgent["claude"]);
        // Gemini cap not configured and not running → not in the snapshot.
        Assert.False(state.CurrentlyRunningPerAgent.ContainsKey("gemini"));

        orchestrator.ReleaseAgentSlotForTest(Codex);
        orchestrator.ReleaseAgentSlotForTest(Codex);
        orchestrator.ReleaseAgentSlotForTest(Claude);

        var drained = orchestrator.GetConcurrencyState();
        // After draining, no agent has running > 0 — the >0 filter excludes them.
        Assert.Empty(drained.CurrentlyRunningPerAgent);
    }

    [Fact]
    public void GetConcurrencyState_ReflectsCapsAndCounts()
    {
        var concurrency = new AgentConcurrencyOptions
        {
            Members =
            {
                ["codex"]  = new AgentConcurrencyEntry { MaxConcurrent = 1 },
                ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 2 },
            }
        };
        var queue = new InMemoryTaskQueue();
        var pipeline = new PinnedPipelineRunner(_store);
        var reg = new CancellationRegistry(CancellationToken.None);
        var orchestrator = new OrchestratorService(
            queue, _store, pipeline, reg,
            new OrchestratorOptions { MaxConcurrentWorkers = 4 },
            NullLogger<OrchestratorService>.Instance,
            agentConcurrency: concurrency);

        var state = orchestrator.GetConcurrencyState();
        Assert.Equal(4, state.GlobalMaxConcurrent);
        Assert.Equal(0, state.CurrentlyRunningTotal);
        Assert.Equal(1, state.PerAgentCaps["codex"]);
        Assert.Equal(2, state.PerAgentCaps["claude"]);
        Assert.Empty(state.CurrentlyRunningPerAgent);
    }

    [Fact]
    public async Task PerAgentCap_SpillsToNextEligibleMember_WhenTopIsSaturated()
    {
        // Acceptance scenario from the spill spec: class with A(QS95, cap=1)
        // and B(QS90, cap=2) and 3 ready items — item1 routes to A, items 2&3
        // SPILL to B instead of deferring on A. Pre-spill behavior deferred
        // items 2&3 until A's slot freed up; we assert at least two distinct
        // agents are seen running at the same time, which proves spill.
        var cls = new AgentClass
        {
            Id = "spill",
            DisplayName = "Spill",
            Members =
            [
                new AgentMembership { Agent = Codex,  Billing = AgentBilling.Subscription, QualityScore = 95 },
                new AgentMembership { Agent = Claude, Billing = AgentBilling.Subscription, QualityScore = 90 },
            ],
        };

        var concurrency = new AgentConcurrencyOptions
        {
            Members =
            {
                ["codex"]  = new AgentConcurrencyEntry { MaxConcurrent = 1 },
                ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 2 },
            }
        };

        var probes = new IAgentQuotaProbe[]
        {
            new FakeProbe(Codex, 90.0),
            new FakeProbe(Claude, 90.0),
        };
        // Mirror production DI: the router sees live counters via a deferred
        // wrapper and shares the same hot-reloadable cap snapshot as the pool.
        var sharedConcurrency = new AgentConcurrencySnapshot(concurrency);
        OrchestratorService orchestrator = null!;
        var router = new AgentClassRouter(
            [cls],
            probes,
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance,
            runningCounters: new DeferredAgentRunningCounters(() => orchestrator),
            concurrencySnapshot: sharedConcurrency);

        var pipeline = new PinnedPipelineRunner(_store);
        var queue = new InMemoryTaskQueue();
        var reg = new CancellationRegistry(CancellationToken.None);
        orchestrator = new OrchestratorService(
            queue, _store, pipeline, reg,
            new OrchestratorOptions { MaxConcurrentWorkers = 4 },
            NullLogger<OrchestratorService>.Instance,
            router: router,
            agentConcurrencySnapshot: sharedConcurrency);

        // 3 items into the same class. Without spill, only one would run
        // (the others queue on Codex's cap=1); with spill, all three run
        // concurrently (1 Codex + 2 Claude).
        var i1 = Item("a") with { AgentClassId = "spill" };
        var i2 = Item("b") with { AgentClassId = "spill" };
        var i3 = Item("c") with { AgentClassId = "spill" };
        foreach (var it in new[] { i1, i2, i3 })
        {
            await _store.CreateAsync(it);
            await queue.EnqueueAsync(it.Id);
        }

        await orchestrator.StartAsync(CancellationToken.None);

        // Wait until all three items are in-flight simultaneously.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        int observedCodex = 0, observedClaude = 0, observedTotal = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snap = orchestrator.Snapshot();
            observedCodex = Math.Max(observedCodex, snap.GetValueOrDefault(Codex));
            observedClaude = Math.Max(observedClaude, snap.GetValueOrDefault(Claude));
            observedTotal = Math.Max(observedTotal, snap.Values.Sum());
            if (observedTotal >= 3) break;
            await Task.Delay(25);
        }

        try
        {
            // Spill working: Codex hit its cap=1 AND Claude has 2 items running.
            Assert.Equal(1, observedCodex);
            Assert.Equal(2, observedClaude);
            Assert.Equal(3, observedTotal);

            // Per-item agent assignment: exactly one item routed to Codex
            // (the top-scoring member, picked first) and the other two
            // spilled to Claude. Without spill, items 2&3 would have stayed
            // Queued and their Agent field would be null. The reservation
            // counter increments inside the router BEFORE the per-item
            // StartedAt/Agent store write, so observedTotal==3 only proves
            // the slots are pinned — we still have to wait for the store
            // stamps to land before reading them.
            var stampDeadline = DateTimeOffset.UtcNow.AddSeconds(8);
            AgentKind?[] agents;
            while (true)
            {
                var snap1 = await _store.GetAsync(i1.Id);
                var snap2 = await _store.GetAsync(i2.Id);
                var snap3 = await _store.GetAsync(i3.Id);
                agents = [snap1?.Agent, snap2?.Agent, snap3?.Agent];
                if (agents.All(a => a is not null) || DateTimeOffset.UtcNow >= stampDeadline)
                    break;
                await Task.Delay(25);
            }
            Assert.Equal(1, agents.Count(a => a == Codex));
            Assert.Equal(2, agents.Count(a => a == Claude));
        }
        finally
        {
            pipeline.Release();
            await orchestrator.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RateAwareTopMemberGated_ReadyBacklogSpillsToFallbackAndFillsOpenSlots()
    {
        // Incident regression: codex is intentionally rate-aware gated with
        // two codex workers already running, but the pool has two global slots
        // free. Ready class-routed backlog must spill to the healthy fallback
        // member instead of leaving those global slots idle.
        var cls = new AgentClass
        {
            Id = "rate-spill",
            DisplayName = "Rate spill",
            Members =
            [
                new AgentMembership { Agent = Codex,  Billing = AgentBilling.Subscription, QualityScore = 100 },
                new AgentMembership { Agent = Claude, Billing = AgentBilling.Subscription, QualityScore = 90 },
            ],
        };

        var estimator = new FakeBurnEstimator
        {
            EstimatesByAgent =
            {
                [Codex] = new AgentBurnEstimate
                {
                    AvgBurnPctPerItem = 90.0,
                    SampleCount = 10,
                    Status = AgentBurnEstimateStatus.Measured,
                },
                [Claude] = new AgentBurnEstimate
                {
                    AvgBurnPctPerItem = 25.0,
                    SampleCount = 10,
                    Status = AgentBurnEstimateStatus.Measured,
                },
            },
        };

        OrchestratorService orchestrator = null!;
        var router = new AgentClassRouter(
            [cls],
            [
                new FakeProbe(Codex, 47.0),
                new FakeProbe(Claude, 100.0),
            ],
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance,
            burnEstimator: estimator,
            runningCounters: new DeferredAgentRunningCounters(() => orchestrator));

        var pipeline = new PinnedPipelineRunner(_store);
        var queue = new InMemoryTaskQueue();
        using var reg = new CancellationRegistry(CancellationToken.None);
        orchestrator = new OrchestratorService(
            queue, _store, pipeline, reg,
            new OrchestratorOptions { MaxConcurrentWorkers = 4 },
            NullLogger<OrchestratorService>.Instance,
            router: router);

        var codexA = Item("codex-a") with { AgentClassId = null, Agent = Codex };
        var codexB = Item("codex-b") with { AgentClassId = null, Agent = Codex };
        await _store.CreateAsync(codexA);
        await _store.CreateAsync(codexB);

        await orchestrator.StartAsync(CancellationToken.None);

        try
        {
            await queue.EnqueueAsync(codexA.Id);
            await queue.EnqueueAsync(codexB.Id);

            var codexDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < codexDeadline)
            {
                var snap = orchestrator.Snapshot();
                if (snap.GetValueOrDefault(Codex) == 2
                    && orchestrator.CurrentlyRunningTotal == 2)
                    break;
                await Task.Delay(25);
            }

            Assert.Equal(2, orchestrator.Snapshot().GetValueOrDefault(Codex));
            Assert.Equal(2, orchestrator.CurrentlyRunningTotal);

            var fallbackA = Item("fallback-a") with { AgentClassId = "rate-spill" };
            var fallbackB = Item("fallback-b") with { AgentClassId = "rate-spill" };
            await _store.CreateAsync(fallbackA);
            await _store.CreateAsync(fallbackB);
            await queue.EnqueueAsync(fallbackA.Id);
            await queue.EnqueueAsync(fallbackB.Id);

            var fillDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < fillDeadline)
            {
                var snap = orchestrator.Snapshot();
                if (snap.GetValueOrDefault(Codex) == 2
                    && snap.GetValueOrDefault(Claude) == 2
                    && orchestrator.CurrentlyRunningTotal == 4)
                    break;
                await Task.Delay(25);
            }

            var filled = orchestrator.Snapshot();
            Assert.Equal(2, filled.GetValueOrDefault(Codex));
            Assert.Equal(2, filled.GetValueOrDefault(Claude));
            Assert.Equal(4, orchestrator.CurrentlyRunningTotal);

            WorkItem? storedA = null;
            WorkItem? storedB = null;
            var stampDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < stampDeadline)
            {
                storedA = await _store.GetAsync(fallbackA.Id);
                storedB = await _store.GetAsync(fallbackB.Id);
                if (storedA?.Agent == Claude && storedB?.Agent == Claude)
                    break;
                await Task.Delay(25);
            }

            Assert.Equal(Claude, storedA!.Agent);
            Assert.Equal(Claude, storedB!.Agent);
        }
        finally
        {
            pipeline.Release();
            var drainDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (orchestrator.CurrentlyRunningTotal > 0 && DateTimeOffset.UtcNow < drainDeadline)
                await Task.Delay(25);
            await orchestrator.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task PerAgentCap_AllMembersAtCap_DefersWithoutChoosingASaturatedMember()
    {
        // When every class member is at its cap, the item defers — the router
        // returns ShouldWait+AnyMemberAtCap=true. We assert that the work
        // item stays Queued (does not transition to Working) while every
        // member is saturated.
        var cls = new AgentClass
        {
            Id = "saturated",
            DisplayName = "Saturated",
            Members =
            [
                new AgentMembership { Agent = Codex,  Billing = AgentBilling.Subscription, QualityScore = 100 },
                new AgentMembership { Agent = Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
            ],
        };

        var concurrency = new AgentConcurrencyOptions
        {
            Members =
            {
                ["codex"]  = new AgentConcurrencyEntry { MaxConcurrent = 1 },
                ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 1 },
            }
        };

        var probes = new IAgentQuotaProbe[]
        {
            new FakeProbe(Codex, 90.0),
            new FakeProbe(Claude, 90.0),
        };
        var router = new AgentClassRouter(
            [cls],
            probes,
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);

        var pipeline = new PinnedPipelineRunner(_store);
        var queue = new InMemoryTaskQueue();
        var reg = new CancellationRegistry(CancellationToken.None);
        var orchestrator = new OrchestratorService(
            queue, _store, pipeline, reg,
            new OrchestratorOptions { MaxConcurrentWorkers = 4 },
            NullLogger<OrchestratorService>.Instance,
            router: router,
            agentConcurrency: concurrency);

        // Pre-saturate both agents' caps via the test reservation API.
        Assert.True(orchestrator.TryReserveAgentSlotForTest(Codex));
        Assert.True(orchestrator.TryReserveAgentSlotForTest(Claude));

        // Enqueue one item; with both caps saturated, it should defer rather
        // than be picked up.
        var deferred = Item("d") with { AgentClassId = "saturated" };
        await _store.CreateAsync(deferred);
        await queue.EnqueueAsync(deferred.Id);

        await orchestrator.StartAsync(CancellationToken.None);

        try
        {
            // Give the dispatcher a moment to attempt and defer.
            await Task.Delay(300);

            // Item must still be Queued (deferred) — never assigned to either
            // already-at-cap agent, and never transitioned to Working.
            var snap = await _store.GetAsync(deferred.Id);
            Assert.NotNull(snap);
            Assert.Equal(WorkItemState.Queued, snap!.State);

            // Total in-flight count from this test's pre-reservations is 2;
            // the dispatcher did not increment it for the deferred item.
            var state = orchestrator.GetConcurrencyState();
            Assert.Equal(1, state.CurrentlyRunningPerAgent["codex"]);
            Assert.Equal(1, state.CurrentlyRunningPerAgent["claude"]);
        }
        finally
        {
            orchestrator.ReleaseAgentSlotForTest(Codex);
            orchestrator.ReleaseAgentSlotForTest(Claude);
            pipeline.Release();
            await orchestrator.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task PerAgentCap_AllAtCap_UsesShortCapRetryDelay_NotQuotaRecheckInterval()
    {
        // Behaviour guard for the router→orchestrator cap-retry plumbing.
        // The router signals AnyMemberAtCap with SuggestedRecheckIn=CapRetryRecheckInterval
        // (we configure 200ms) instead of the QuotaRecheckInterval (60s). If the
        // orchestrator ignored the suggestion and waited the longer quota interval,
        // the item would still be Queued at our 1.5s deadline — the test would fail.
        var cls = new AgentClass
        {
            Id = "cap-retry",
            DisplayName = "CapRetry",
            Members =
            [
                new AgentMembership { Agent = Codex,  Billing = AgentBilling.Subscription, QualityScore = 100 },
                new AgentMembership { Agent = Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
            ],
        };
        var concurrency = new AgentConcurrencyOptions
        {
            Members =
            {
                ["codex"]  = new AgentConcurrencyEntry { MaxConcurrent = 1 },
                ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 1 },
            }
        };
        var probes = new IAgentQuotaProbe[]
        {
            new FakeProbe(Codex, 90.0),
            new FakeProbe(Claude, 90.0),
        };
        var router = new AgentClassRouter(
            [cls],
            probes,
            new QuotaRouterOptions
            {
                MinQuotaPct = 5.0,
                QuotaRecheckInterval = TimeSpan.FromSeconds(60),
                CapRetryRecheckInterval = TimeSpan.FromMilliseconds(200),
            },
            NullLogger<AgentClassRouter>.Instance);

        var pipeline = new PinnedPipelineRunner(_store);
        var queue = new InMemoryTaskQueue();
        var reg = new CancellationRegistry(CancellationToken.None);
        var orchestrator = new OrchestratorService(
            queue, _store, pipeline, reg,
            new OrchestratorOptions { MaxConcurrentWorkers = 4 },
            NullLogger<OrchestratorService>.Instance,
            router: router,
            agentConcurrency: concurrency);

        // Saturate both members.
        Assert.True(orchestrator.TryReserveAgentSlotForTest(Codex));
        Assert.True(orchestrator.TryReserveAgentSlotForTest(Claude));

        var item = Item("d") with { AgentClassId = "cap-retry" };
        await _store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);

        await orchestrator.StartAsync(CancellationToken.None);

        try
        {
            // The item must defer on first attempt (everything at cap). We
            // detect successful re-pickup by polling the per-agent running
            // counter: PinnedPipelineRunner blocks until Release, so the
            // work item's State stays Queued throughout, but the orchestrator
            // increments the running count the moment the router reserves a
            // slot during pickup.
            var deferredDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (!orchestrator.IsDeferredForTest(item.Id) && DateTimeOffset.UtcNow < deferredDeadline)
                await Task.Delay(25);
            Assert.True(
                orchestrator.IsDeferredForTest(item.Id),
                "the item must defer while every class member is at its per-agent cap");

            // Pre-release sanity: only the two test pre-reservations are visible.
            var pre = orchestrator.Snapshot();
            Assert.Equal(1, pre.GetValueOrDefault(Codex));
            Assert.Equal(1, pre.GetValueOrDefault(Claude));

            // Now free Codex so the cap-retry re-pickup can route there.
            orchestrator.ReleaseAgentSlotForTest(Codex);

            // Within the cap-retry window (200ms) + a small scheduling jitter
            // budget, the deferred item must re-attempt pickup and reserve
            // Codex's slot. If the orchestrator had used the 60s quota
            // interval, Codex's in-flight count would still be 0 at the
            // deadline. The 10s budget is well under the 60s quota window
            // but several orders of magnitude over the 200ms cap-retry
            // interval, leaving room for the audit-runtime CI scheduler
            // (many parallel test classes) and absorbing GC pauses /
            // scheduling jitter on a loaded CI host without weakening the
            // cap-retry vs quota-interval contrast the test is asserting.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            int observedCodex = 0;
            while (DateTimeOffset.UtcNow < deadline)
            {
                observedCodex = orchestrator.Snapshot().GetValueOrDefault(Codex);
                if (observedCodex >= 1) break;
                await Task.Delay(25);
            }
            Assert.Equal(1, observedCodex);

            // Cross-check: the work item's stamped Agent field reflects the
            // chosen-and-reserved member from the re-pickup. The reservation
            // counter increments inside the router before the subsequent
            // StartedAt/Agent store write, so wait for the persisted stamp.
            var stampDeadline = DateTimeOffset.UtcNow.AddSeconds(8);
            WorkItem? snap = null;
            while (DateTimeOffset.UtcNow < stampDeadline)
            {
                snap = await _store.GetAsync(item.Id);
                if (snap?.Agent == Codex) break;
                await Task.Delay(25);
            }
            Assert.Equal(Codex, snap!.Agent);
        }
        finally
        {
            // Pipeline released so the in-flight worker drains; both agent
            // slots are released by the orchestrator's outer finally.
            pipeline.Release();
            await orchestrator.StopAsync(CancellationToken.None);
        }
    }

    private static AgentClass SingleAgentClass(string classId, AgentKind agent) => new()
    {
        Id = classId,
        DisplayName = classId,
        Members =
        [
            new AgentMembership { Agent = agent, Billing = AgentBilling.Subscription, QualityScore = 100 },
        ],
    };
}

/// <summary>
/// Pipeline that blocks until <see cref="Release"/> is called, then marks the
/// item Done. Lets the test pin items in "in-flight" state and observe the
/// per-agent counts.
/// </summary>
internal sealed class PinnedPipelineRunner : IPipelineRunner
{
    private readonly IWorkItemStore _store;
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PinnedPipelineRunner(IWorkItemStore store) { _store = store; }

    public void Release() => _release.TrySetResult();

    public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
    {
        try { await _release.Task.WaitAsync(ct); }
        catch (OperationCanceledException) { /* shutting down */ }
        await _store.UpdateAsync(item.With(WorkItemState.Done), ct);
    }
}
