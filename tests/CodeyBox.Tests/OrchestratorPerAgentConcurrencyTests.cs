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
