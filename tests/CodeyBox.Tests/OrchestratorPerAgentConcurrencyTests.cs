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
        var monitor = Task.Run(async () =>
        {
            while (true)
            {
                foreach (var kv in orchestrator.Snapshot())
                    observed.Add((kv.Key, kv.Value));
                try { await Task.Delay(25); } catch { return; }
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
