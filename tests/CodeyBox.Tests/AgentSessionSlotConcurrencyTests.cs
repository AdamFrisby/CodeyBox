using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Per-agent session accounting: every agent CLI session — work turns AND
/// each LLM auditor run — must count against AgentConcurrency.Members.*.MaxConcurrent.
/// Regression coverage for the incident where only work-phase turns were
/// counted and auditor fan-out pushed the real provider session count past
/// the configured cap (websocket 1006 drops on the devin account).
/// </summary>
file static class SessionSlotTestHelpers
{
    public static WorkItem NewItem(string title = "t") => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = title,
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = "feature/" + Guid.NewGuid().ToString("N")[..8],
        PushUpstream = false,
        Agent = AgentKind.Claude,
    };

    public static void ObserveMax(ref int field, int value)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref field);
            if (value <= observed) return;
        } while (Interlocked.CompareExchange(ref field, value, observed) != observed);
    }
}

file sealed class GateObservingLlmAuditor : IAuditor
{
    private readonly Func<ISandbox, CancellationToken, Task<AuditResult>> _body;

    public GateObservingLlmAuditor(
        string name,
        Func<ISandbox, CancellationToken, Task<AuditResult>> body,
        AuditCapabilities required = AuditCapabilities.None)
    {
        Name = name;
        _body = body;
        Required = required;
    }

    public string Name { get; }
    public string Kind => "llm";
    public AuditCapabilities Required { get; }

    public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        => _body(sandbox, ct);
}

// ── Gate-level unit tests ────────────────────────────────────────────────────

public sealed class AgentSessionSlotGateTests : IDisposable
{
    private static readonly AgentKind Claude = AgentKind.Claude;
    private static readonly AgentKind Codex = AgentKind.Codex;

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-slots-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public AgentSessionSlotGateTests() => _store = new SqliteWorkItemStore(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
        TestScratchDirectory.DeleteSqliteCompanions(_dbPath);
    }

    private OrchestratorService NewGate(AgentConcurrencyOptions opts) =>
        new(
            new InMemoryTaskQueue(),
            _store,
            new PinnedPipelineRunner(_store),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 8 },
            NullLogger<OrchestratorService>.Instance,
            agentConcurrency: opts);

    private static AgentConcurrencyOptions Caps(params (string Key, int Max, int? Audit)[] entries)
    {
        var opts = new AgentConcurrencyOptions();
        foreach (var (key, max, audit) in entries)
            opts.Members[key] = new AgentConcurrencyEntry
            {
                MaxConcurrent = max,
                MaxConcurrentAuditSessions = audit,
            };
        return opts;
    }

    [Fact]
    public async Task AuditSession_WaitsWhileRouteAtCap_AdmitsOnRelease()
    {
        // An LLM auditor session is a real provider session: while the route
        // is at its MaxConcurrent the audit acquire must wait, not over-admit.
        var gate = NewGate(Caps(("claude", 1, null)));

        Assert.True(gate.TryReserveAgentSlotForTest(Claude)); // work slot held — cap reached

        var auditWait = gate.WaitForAuditSlotAsync(Claude, CancellationToken.None);
        Assert.False(auditWait.IsCompleted, "audit session must not be admitted past MaxConcurrent");
        Assert.False(gate.TryReserveAudit(Claude), "non-blocking audit reserve must also refuse at cap");

        gate.ReleaseAgentSlotForTest(Claude); // work session ends → slot frees

        await auditWait.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, gate.GetRunningAudit(Claude));
        Assert.Equal(0, gate.GetRunningWork(Claude));
        Assert.Equal(1, gate.GetRunning(Claude)); // total is what the cap compares against

        gate.ReleaseAudit(Claude);
        Assert.Equal(0, gate.GetRunning(Claude));
    }

    [Fact]
    public async Task AuditSubCap_BoundsAuditSessions_IndependentOfTotalHeadroom()
    {
        // MaxConcurrentAuditSessions is a sub-cap: even with total headroom
        // under MaxConcurrent, audits stop admitting at the audit ceiling so
        // they cannot starve work turns.
        var gate = NewGate(Caps(("claude", 3, 1)));

        await gate.WaitForAuditSlotAsync(Claude, CancellationToken.None);
        Assert.Equal(1, gate.GetRunningAudit(Claude));
        Assert.Equal(1, gate.GetAuditCap(Claude));

        var second = gate.WaitForAuditSlotAsync(Claude, CancellationToken.None);
        Assert.False(second.IsCompleted, "audit sub-cap must block the second auditor despite total headroom");

        // Work sessions are unaffected by the audit sub-cap.
        Assert.True(gate.TryReserveAgentSlotForTest(Claude));
        Assert.Equal(2, gate.GetRunning(Claude));

        gate.ReleaseAudit(Claude);
        await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, gate.GetRunningAudit(Claude));

        gate.ReleaseAudit(Claude);
        gate.ReleaseAgentSlotForTest(Claude);
        Assert.Equal(0, gate.GetRunning(Claude));
    }

    [Fact]
    public async Task AuditWait_Cancellation_NeverConsumesSlot()
    {
        var gate = NewGate(Caps(("claude", 1, null)));
        Assert.True(gate.TryReserveAgentSlotForTest(Claude));

        using var cts = new CancellationTokenSource();
        var wait = gate.WaitForAuditSlotAsync(Claude, cts.Token);
        Assert.False(wait.IsCompleted);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);

        // The cancelled waiter must not hold a permit: the cap still admits
        // exactly one session after the work slot frees.
        gate.ReleaseAgentSlotForTest(Claude);
        Assert.True(gate.TryReserveAudit(Claude));
        Assert.Equal(1, gate.GetRunning(Claude));
        gate.ReleaseAudit(Claude);
        Assert.Equal(0, gate.GetRunning(Claude));
    }

    [Fact]
    public async Task ItemWorkSlot_SuspendForAudit_FreesCapacity_ThenResumeReacquires()
    {
        // The release-before-acquire contract: at cap=1 an item holding its
        // work slot frees it for the audit window, then re-acquires.
        var gate = NewGate(Caps(("claude", 1, null)));
        var item = SessionSlotTestHelpers.NewItem();

        Assert.True(gate.TryReserveAgentSlotForTest(Claude));
        gate.RegisterItemWorkSlotForTest(item.Id, "claude");
        Assert.Equal(1, gate.GetRunningWork(Claude));

        Assert.True(gate.SuspendWorkSlotForAudit(item.Id));
        Assert.Equal(0, gate.GetRunningWork(Claude));

        // The audit session admits against the freed capacity immediately.
        await gate.WaitForAuditSlotAsync(Claude, CancellationToken.None);
        Assert.Equal(1, gate.GetRunningAudit(Claude));
        Assert.Equal(1, gate.GetRunning(Claude));

        // Suspend is idempotent.
        Assert.False(gate.SuspendWorkSlotForAudit(item.Id));

        gate.ReleaseAudit(Claude);
        await gate.ResumeWorkSlotAfterAuditAsync(item.Id, CancellationToken.None);
        Assert.Equal(1, gate.GetRunningWork(Claude));
        Assert.Equal(0, gate.GetRunningAudit(Claude));

        // The orchestrator's outer-finally release balances the books.
        gate.ReleaseItemWorkSlotIfHeldForTest(item.Id);
        Assert.Equal(0, gate.GetRunning(Claude));
    }

    [Fact]
    public async Task ItemExitWhileAuditSuspended_ReleasesCleanly()
    {
        // If the item exits while suspended (cancellation mid-audit), the
        // finally-path release must neither double-release nor leak.
        var gate = NewGate(Caps(("claude", 1, null)));
        var item = SessionSlotTestHelpers.NewItem();

        Assert.True(gate.TryReserveAgentSlotForTest(Claude));
        gate.RegisterItemWorkSlotForTest(item.Id, "claude");
        Assert.True(gate.SuspendWorkSlotForAudit(item.Id));
        await gate.WaitForAuditSlotAsync(Claude, CancellationToken.None);

        // Simulate the pipeline abandoning mid-audit: the audit slot is
        // released by its own finally, then the item release runs.
        gate.ReleaseAudit(Claude);
        gate.ReleaseItemWorkSlotIfHeldForTest(item.Id);
        Assert.Equal(0, gate.GetRunning(Claude));

        // A fresh reservation must admit cleanly — no phantom capacity.
        Assert.True(gate.TryReserveAgentSlotForTest(Claude));
        gate.ReleaseAgentSlotForTest(Claude);
    }

    [Fact]
    public void ConcurrencyState_SplitsWorkAuditAndTotal()
    {
        var gate = NewGate(Caps(("claude", 3, 2)));
        var item = SessionSlotTestHelpers.NewItem();

        Assert.True(gate.TryReserveAgentSlotForTest(Claude));
        gate.RegisterItemWorkSlotForTest(item.Id, "claude");
        Assert.True(gate.TryReserveAudit(Claude));

        var state = gate.GetConcurrencyState();
        Assert.Equal(2, state.CurrentlyRunningPerAgent["claude"]);   // total — compared to the cap
        Assert.Equal(1, state.CurrentlyRunningWorkPerAgent["claude"]);
        Assert.Equal(1, state.CurrentlyRunningAuditPerAgent["claude"]);
        Assert.Equal(3, state.PerAgentCaps["claude"]);
        Assert.Equal(2, state.PerAgentAuditCaps["claude"]);

        gate.ReleaseAudit(Claude);
        gate.ReleaseItemWorkSlotIfHeldForTest(item.Id);

        var drained = gate.GetConcurrencyState();
        Assert.Empty(drained.CurrentlyRunningPerAgent);
        Assert.Empty(drained.CurrentlyRunningWorkPerAgent);
        Assert.Empty(drained.CurrentlyRunningAuditPerAgent);
    }

    [Fact]
    public async Task InstanceRouteKey_AuditSessionsCountOnMemberRoute()
    {
        // Member-level caps ("claude/acct-a") must bound audit sessions on
        // the same route bucket the member's work slot uses.
        var gate = NewGate(Caps(("claude/acct-a", 1, null)));
        var member = new AgentMembership
        {
            Agent = Claude,
            InstanceId = "acct-a",
            Billing = AgentBilling.Subscription,
            QualityScore = 100,
        };

        Assert.True(gate.TryReserve(member));
        Assert.False(gate.TryReserveAudit(member));

        gate.Release(member);
        await gate.WaitForAuditSlotAsync(member, CancellationToken.None);
        Assert.Equal(1, gate.GetRunningAudit(Claude));
        gate.ReleaseAudit(member);
        Assert.Equal(0, gate.GetRunning(Claude));
    }

    [Fact]
    public void AuditSession_OnUnrelatedRoute_DoesNotContend()
    {
        var gate = NewGate(Caps(("claude", 1, null)));
        Assert.True(gate.TryReserveAgentSlotForTest(Claude));
        Assert.True(gate.TryReserveAudit(Codex), "uncapped agent must admit");
        gate.ReleaseAudit(Codex);
        gate.ReleaseAgentSlotForTest(Claude);
    }
}

// ── Pipeline-level integration tests ─────────────────────────────────────────

/// <summary>
/// Drives the real <see cref="PipelineRunner"/> audit fan-out against the real
/// orchestrator gate so the acquire/release wiring is exercised through the
/// same path production runs.
/// </summary>
public sealed class AgentSessionSlotPipelineTests : IDisposable
{
    private static readonly AgentKind Claude = AgentKind.Claude;

    private readonly string _workspace;
    private readonly string _gateDbPath;
    private readonly SqliteWorkItemStore _gateStore;
    private readonly List<IDisposable> _disposables = [];

    public AgentSessionSlotPipelineTests()
    {
        _workspace = Directory.CreateTempSubdirectory("codeybox-slots-").FullName;
        _gateDbPath = Path.Combine(Path.GetTempPath(), $"codeybox-slots-gate-{Guid.NewGuid():N}.db");
        _gateStore = new SqliteWorkItemStore(_gateDbPath);
    }

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { }
        }
        _gateStore.Dispose();
        try { Directory.Delete(_workspace, recursive: true); } catch { }
        try { File.Delete(_gateDbPath); } catch { }
        TestScratchDirectory.DeleteSqliteCompanions(_gateDbPath);
    }

    private OrchestratorService NewGate(AgentConcurrencyOptions opts)
    {
        var gate = new OrchestratorService(
            new InMemoryTaskQueue(),
            _gateStore,
            new PinnedPipelineRunner(_gateStore),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 8 },
            NullLogger<OrchestratorService>.Instance,
            agentConcurrency: opts);
        _disposables.Add(gate);
        return gate;
    }

    private TestPipeline NewPipeline(
        string dir,
        string seed,
        IReadOnlyList<IAuditor> auditors,
        OrchestratorService gate,
        int maxLlmAuditorParallelism = 3)
    {
        var tp = TestSupport.BuildPipeline(
            dir,
            seed,
            auditors: auditors,
            maxAuditIterations: 1,
            maxLlmAuditorParallelism: maxLlmAuditorParallelism,
            sessionSlotGate: gate);
        _disposables.Add(tp);
        return tp;
    }

    /// <summary>
    /// Regression for the cap-1 self-deadlock: the item holds the agent's only
    /// work slot when its audit starts. Without release-before-acquire, the
    /// auditor's slot wait would block on a slot only the item itself could
    /// free. The run must complete and reach Done.
    /// </summary>
    [Fact]
    public async Task CapOne_ItemAuditsAfterWork_CompletesWithoutSelfDeadlock()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gate = NewGate(new AgentConcurrencyOptions
        {
            Members = { ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 1 } },
        });

        var sawAuditSession = false;
        var auditWorkCount = -1;
        var auditTotal = -1;
        var auditor = new GateObservingLlmAuditor("llm:review", async (_, ct) =>
        {
            // Inside the session: the item's work slot must be suspended (the
            // audit session itself is the only session on the route).
            auditWorkCount = gate.GetRunningWork(Claude);
            auditTotal = gate.GetRunning(Claude);
            sawAuditSession = gate.GetRunningAudit(Claude) == 1;
            await Task.Delay(50, ct);
            return new AuditResult(true, []);
        });

        var tp = NewPipeline(_workspace, seed, TestAuditGates.WithPassedBuildAndTest(auditor), gate);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = SessionSlotTestHelpers.NewItem("cap-1 self-deadlock");
        await tp.Store.CreateAsync(item);

        // Dispatch-time state: the item's work slot is the only slot on the
        // route and the cap is saturated — exactly the incident shape.
        Assert.True(gate.TryReserve(Claude));
        gate.RegisterItemWorkSlotForTest(item.Id, "claude");
        try
        {
            // Bound the run so a regression wedges loudly instead of hanging the suite.
            using var runCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await tp.Pipeline.RunAsync(item, runCts.Token);

            var final = await tp.Store.GetAsync(item.Id);
            Assert.Equal(WorkItemState.Done, final!.State);
            Assert.True(sawAuditSession, "the auditor must have run inside an acquired audit slot");
            Assert.Equal(0, auditWorkCount);  // work slot suspended during audit
            Assert.Equal(1, auditTotal);      // audit session alone on the route
        }
        finally
        {
            gate.ReleaseItemWorkSlotIfHeldForTest(item.Id);
        }
        Assert.Equal(0, gate.GetRunning(Claude));
    }

    /// <summary>
    /// The incident shape: cap=3, four items reach their audit phase, each
    /// fanning out two LLM auditors — the live session count must never pass 3.
    /// Each item runs on its own pipeline (own scripted agent + store) so the
    /// only shared resource under test is the session-slot gate itself.
    /// </summary>
    [Fact]
    public async Task CapThree_FourAuditingItems_NeverExceedProviderLimit()
    {
        var gate = NewGate(new AgentConcurrencyOptions
        {
            Members = { ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 3 } },
        });

        var running = 0;
        var maxRunning = 0;
        var maxGateAudit = 0;
        var maxGateTotal = 0;

        GateObservingLlmAuditor MakeAuditor(string name) => new(name, async (_, ct) =>
        {
            SessionSlotTestHelpers.ObserveMax(ref maxRunning, Interlocked.Increment(ref running));
            SessionSlotTestHelpers.ObserveMax(ref maxGateAudit, gate.GetRunningAudit(Claude));
            SessionSlotTestHelpers.ObserveMax(ref maxGateTotal, gate.GetRunning(Claude));
            try
            {
                // Wait for saturation (cap fully consumed) so the test proves
                // the gate actually bit — then hold so siblings overlap.
                var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
                while (Volatile.Read(ref running) < 3 && DateTimeOffset.UtcNow < deadline)
                    await Task.Delay(10, ct);
                await Task.Delay(300, ct);
                return new AuditResult(true, []);
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        });

        var pipelines = new List<TestPipeline>();
        var items = new List<WorkItem>();
        // Three items hold the route's work slots — a fourth item could not
        // have dispatched, but release-before-acquire lets all four audit
        // under the same cap.
        var heldItems = new List<WorkItem>();
        try
        {
            for (var i = 0; i < 4; i++)
            {
                var dir = Path.Combine(_workspace, $"p{i}");
                Directory.CreateDirectory(dir);
                var seed = await TestSupport.CreateSeedRepoAsync(dir);
                var tp = NewPipeline(
                    dir,
                    seed,
                    TestAuditGates.WithPassedBuildAndTest(MakeAuditor("llm:a"), MakeAuditor("llm:b")),
                    gate,
                    maxLlmAuditorParallelism: 2);
                pipelines.Add(tp);
                tp.Agent.WorkPlan.Enqueue(new FileWrite($"f{i}.txt", "v"));

                var item = SessionSlotTestHelpers.NewItem($"audit-{i}");
                await tp.Store.CreateAsync(item);
                items.Add(item);

                if (i < 3)
                {
                    Assert.True(gate.TryReserve(Claude));
                    gate.RegisterItemWorkSlotForTest(item.Id, "claude");
                    heldItems.Add(item);
                }
            }

            using var runCts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            await Task.WhenAll(items.Select((item, i) =>
                pipelines[i].Pipeline.RunAsync(item, runCts.Token)));

            for (var i = 0; i < items.Count; i++)
            {
                var final = await pipelines[i].Store.GetAsync(items[i].Id);
                Assert.Equal(WorkItemState.Done, final!.State);
            }
        }
        finally
        {
            foreach (var item in heldItems)
                gate.ReleaseItemWorkSlotIfHeldForTest(item.Id);
        }

        Assert.True(maxRunning <= 3, $"concurrent auditor bodies exceeded cap: {maxRunning}");
        Assert.True(maxGateAudit <= 3, $"gate audit count exceeded cap: {maxGateAudit}");
        Assert.True(maxGateTotal <= 3, $"gate total session count exceeded cap: {maxGateTotal}");
        // Demand (8 sessions over 4 pipelines) definitely exceeded the cap —
        // if saturation was never reached the cap was not actually exercised.
        Assert.Equal(3, maxRunning);
        Assert.Equal(3, maxGateAudit);
        Assert.Equal(0, gate.GetRunning(Claude));
        Assert.Equal(0, gate.GetRunningAudit(Claude));
    }

    /// <summary>
    /// An auditor that throws must still release its audit session — the
    /// release lives in the attempt's finally, covering failures, not just
    /// clean runs.
    /// </summary>
    [Fact]
    public async Task AuditSlot_ReleasedOnAuditorFailure()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gate = NewGate(new AgentConcurrencyOptions
        {
            Members = { ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 1 } },
        });

        var calls = 0;
        var auditor = new GateObservingLlmAuditor("llm:boom", (_, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("auditor exploded");
        });

        var tp = NewPipeline(_workspace, seed, TestAuditGates.WithPassedBuildAndTest(auditor), gate);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = SessionSlotTestHelpers.NewItem("audit-failure");
        await tp.Store.CreateAsync(item);

        using var runCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await tp.Pipeline.RunAsync(item, runCts.Token);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotEqual(WorkItemState.Done, final!.State);
        Assert.True(calls > 0, "the fake auditor should have been invoked");
        Assert.Equal(0, gate.GetRunningAudit(Claude));
        Assert.Equal(0, gate.GetRunning(Claude));
    }

    /// <summary>
    /// An auditor that trips its idle timeout must release its session slot —
    /// the single fresh-sandbox retry gets a slot of its own and releases it
    /// on the same finally path.
    /// </summary>
    [Fact]
    public async Task AuditSlot_ReleasedOnAuditorIdleTimeout()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gate = NewGate(new AgentConcurrencyOptions
        {
            Members = { ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 2 } },
        });

        var calls = 0;
        var auditor = new GateObservingLlmAuditor("llm:idle", (_, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new AuditorIdleTimeoutException("llm:idle", Claude, TimeSpan.FromSeconds(5));
        });

        var tp = NewPipeline(_workspace, seed, TestAuditGates.WithPassedBuildAndTest(auditor), gate);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = SessionSlotTestHelpers.NewItem("audit-timeout");
        await tp.Store.CreateAsync(item);

        using var runCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await tp.Pipeline.RunAsync(item, runCts.Token);

        Assert.True(calls >= 1, "the fake auditor should have been invoked");
        Assert.Equal(0, gate.GetRunningAudit(Claude));
        Assert.Equal(0, gate.GetRunning(Claude));
    }

    /// <summary>
    /// Operator/host cancellation mid-audit must release the session slot on
    /// the cancellation exit path, not just on success/failure.
    /// </summary>
    [Fact]
    public async Task AuditSlot_ReleasedOnCancellation()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gate = NewGate(new AgentConcurrencyOptions
        {
            Members = { ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 2 } },
        });

        var auditStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var auditor = new GateObservingLlmAuditor("llm:hang", async (_, ct) =>
        {
            auditStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct); // cancelled mid-session
            return new AuditResult(true, []);
        });

        var tp = NewPipeline(_workspace, seed, TestAuditGates.WithPassedBuildAndTest(auditor), gate);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = SessionSlotTestHelpers.NewItem("audit-cancel");
        await tp.Store.CreateAsync(item);

        using var runCts = new CancellationTokenSource();
        var run = tp.Pipeline.RunAsync(item, runCts.Token);

        // Wait for the audit session to actually hold its slot.
        await auditStarted.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(1, gate.GetRunningAudit(Claude));

        await runCts.CancelAsync();
        try { await run; } catch (OperationCanceledException) { }

        // Drain — the finally release may land a tick after RunAsync returns.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (gate.GetRunningAudit(Claude) != 0 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(25);

        Assert.Equal(0, gate.GetRunningAudit(Claude));
        Assert.Equal(0, gate.GetRunning(Claude));
    }
}
