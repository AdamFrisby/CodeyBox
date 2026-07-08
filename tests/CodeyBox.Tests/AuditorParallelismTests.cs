using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

// ── Shared helpers ────────────────────────────────────────────────────────────

/// <summary>
/// A fake auditor with Kind="llm" and no required capabilities.
/// Using Kind="llm" causes CollectFindingsAsync to route it through the
/// parallel LLM path (one sandbox per auditor, Task.WhenAll) while
/// AuditCapabilities.None keeps the test free of credential machinery.
/// </summary>
file sealed class FakeLlmAuditor : IAuditor
{
    private readonly Func<ISandbox, AuditContext, CancellationToken, Task<AuditResult>> _body;

    public FakeLlmAuditor(
        string name,
        Func<ISandbox, AuditContext, CancellationToken, Task<AuditResult>> body,
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
        => _body(sandbox, context, ct);
}

file sealed class FakeToolAuditor : IAuditor
{
    private readonly Func<ISandbox, AuditContext, CancellationToken, Task<AuditResult>> _body;

    public FakeToolAuditor(
        string name,
        Func<ISandbox, AuditContext, CancellationToken, Task<AuditResult>> body,
        bool canShortCircuitOnBlockingFinding = false)
    {
        Name = name;
        _body = body;
        CanShortCircuitOnBlockingFinding = canShortCircuitOnBlockingFinding;
    }

    public string Name { get; }
    public string Kind => "tool";
    public AuditCapabilities Required => AuditCapabilities.None;
    public bool CanShortCircuitOnBlockingFinding { get; }

    public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        => _body(sandbox, context, ct);
}

file sealed class FixedQuotaProbe : IAgentQuotaProbe
{
    private readonly DateTimeOffset _resetAt;

    public FixedQuotaProbe(AgentKind kind, DateTimeOffset resetAt)
    {
        Kind = kind;
        _resetAt = resetAt;
    }

    public AgentKind Kind { get; }
    public int CallCount { get; private set; }

    public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
    {
        CallCount++;
        return Task.FromResult(new AgentQuotaSnapshot { AvailablePct = 0, ResetAt = _resetAt });
    }
}

file sealed class ExhaustedNoResetQuotaProbe : IAgentQuotaProbe
{
    public ExhaustedNoResetQuotaProbe(AgentKind kind) => Kind = kind;

    public AgentKind Kind { get; }
    public int CallCount { get; private set; }

    public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
    {
        CallCount++;
        return Task.FromResult(new AgentQuotaSnapshot { AvailablePct = 0, ResetAt = null });
    }
}

file static class AuditorTestHelpers
{
    public static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "parallelism test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = "feature/x",
        PushUpstream = false,
    };

    public static async Task<WorkItem?> WaitForStateAsync(
        IWorkItemStore store,
        WorkItemId id,
        WorkItemState state,
        TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        while (!timeoutCts.IsCancellationRequested)
        {
            var current = await store.GetAsync(id);
            if (current?.State == state)
                return current;
            if (current is not null && WorkItemDependencies.TerminalStates.Contains(current.State))
                return current;

            try
            {
                await Task.Delay(50, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                break;
            }
        }

        return await store.GetAsync(id);
    }
}

file sealed class CapturingAuditReportStore : IAuditReportStore
{
    public List<AuditReport> Reports { get; } = [];

    public Task CreateAsync(AuditReport report, CancellationToken ct = default)
    {
        Reports.Add(report);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditReport>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AuditReport>>(Reports.Where(r => r.WorkItemId == workItemId).ToList());

    public Task<string?> GetRawOutputAsync(string workItemId, int iteration, string auditorName, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
        => Task.FromResult(0);
}

// ── Test: parallel wall-clock ─────────────────────────────────────────────────

/// <summary>
/// Three slow fake LLM auditors each delay 1 500 ms.
/// Running them in parallel means all three auditor bodies are in flight at
/// once, regardless of unrelated sandbox/git scheduler noise around the batch.
/// </summary>
public sealed class AuditorParallelismTests : IDisposable
{
    private readonly string _workspace;
    public AuditorParallelismTests() => _workspace = Directory.CreateTempSubdirectory("codeybox-par-").FullName;
    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    [Fact]
    public async Task ThreeSlowLlmAuditors_RunInParallel_WallClockIsNotSumOfDelays()
    {
        const int DelayMs = 1_500;
        const int AuditorCount = 3;
        var running = 0;
        var maxRunning = 0;
        long firstAuditorStartTimestamp = 0;
        long lastAuditorFinishTimestamp = 0;

        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditors = Enumerable.Range(0, AuditorCount)
            .Select(i => new FakeLlmAuditor($"slow-{i}", async (_, _, ct) =>
            {
                var current = Interlocked.Increment(ref running);
                Interlocked.CompareExchange(ref firstAuditorStartTimestamp, Stopwatch.GetTimestamp(), 0);
                int observed;
                do
                {
                    observed = Volatile.Read(ref maxRunning);
                    if (current <= observed)
                        break;
                } while (Interlocked.CompareExchange(ref maxRunning, current, observed) != observed);

                try
                {
                    await Task.Delay(DelayMs, ct);
                    return new AuditResult(true, []);
                }
                finally
                {
                    if (Interlocked.Decrement(ref running) == 0)
                        Volatile.Write(ref lastAuditorFinishTimestamp, Stopwatch.GetTimestamp());
                }
            }))
            .ToArray();

        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: TestAuditGates.WithPassedBuildAndTest(auditors), maxLlmAuditorParallelism: AuditorCount);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = AuditorTestHelpers.NewItem();
        await tp.Store.CreateAsync(item);

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        Assert.Equal(AuditorCount, Volatile.Read(ref maxRunning));
        var firstAuditorStart = Volatile.Read(ref firstAuditorStartTimestamp);
        var lastAuditorFinish = Volatile.Read(ref lastAuditorFinishTimestamp);
        Assert.True(firstAuditorStart > 0, "Expected at least one LLM auditor to start.");
        Assert.True(lastAuditorFinish >= firstAuditorStart, "Expected LLM auditor completion to be observed.");

        var elapsedAuditorWindow = Stopwatch.GetElapsedTime(firstAuditorStart, lastAuditorFinish);
        var serialDelay = TimeSpan.FromMilliseconds(DelayMs * AuditorCount);
        var upperBound = TimeSpan.FromMilliseconds(DelayMs * (AuditorCount - 0.25));
        Assert.True(elapsedAuditorWindow < upperBound,
            $"Expected three {DelayMs}ms LLM auditors to complete well under their serial delay {serialDelay}; elapsed {elapsedAuditorWindow}");
    }
}

// ── Test: stable ordering ─────────────────────────────────────────────────────

/// <summary>
/// Auditors complete in reverse registration order (C fastest, A slowest).
/// Findings must aggregate in registration order (A, B, C), not completion order.
/// </summary>
public sealed class AuditorParallelismOrderingTests : IDisposable
{
    private readonly string _workspace;
    public AuditorParallelismOrderingTests() => _workspace = Directory.CreateTempSubdirectory("codeybox-order-").FullName;
    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    [Fact]
    public async Task Findings_AggregateInRegistrationOrder_RegardlessOfCompletionOrder()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        // A=1 500 ms, B=750 ms, C=0 ms → completion order is C, B, A.
        // Reports must be persisted in registration order (A, B, C).
        var auditorA = new FakeLlmAuditor("A", async (_, _, ct) =>
        {
            await Task.Delay(1_500, ct);
            return new AuditResult(false, [new AuditFinding("A", AuditSeverity.Warning, "finding-A", "from A")]);
        });
        var auditorB = new FakeLlmAuditor("B", async (_, _, ct) =>
        {
            await Task.Delay(750, ct);
            return new AuditResult(false, [new AuditFinding("B", AuditSeverity.Warning, "finding-B", "from B")]);
        });
        var auditorC = new FakeLlmAuditor("C", async (_, _, ct) =>
        {
            await Task.Delay(0, ct);
            return new AuditResult(false, [new AuditFinding("C", AuditSeverity.Warning, "finding-C", "from C")]);
        });

        var captureStore = new CapturingAuditReportStore();

        // Findings are Warnings; FailingSeverity defaults to Error so audit passes.
        using var tp = TestSupport.BuildPipeline(_workspace, seed,
            auditors: TestAuditGates.WithPassedBuildAndTest(auditorA, auditorB, auditorC),
            maxLlmAuditorParallelism: 3,
            auditReportStore: captureStore);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = AuditorTestHelpers.NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Null(final.LastError);

        // Reports are persisted by PostProcessAuditorRunAsync in the post-WhenAll
        // loop, which iterates llmRuns in input-task order (registration order).
        // Verify A comes before B comes before C despite C completing first.
        var reports = captureStore.Reports
            .Where(r => r.WorkItemId == item.Id.ToString())
            .Select(r => r.AuditorName)
            .Where(n => n != "test:build-and-test-pass")
            .ToList();
        Assert.Equal(["A", "B", "C"], reports);
    }

    [Fact]
    public async Task StopOnFirstFailure_UsesRegistrationOrder_NotCompletionOrder()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        // A is slow but failing at Error; B is fast and passing.
        // With stable ordering A is processed first — StopOnFirstFailure fires on A.
        var aRan = false;
        var bRan = false;
        var auditorA = new FakeLlmAuditor("A", async (_, _, ct) =>
        {
            await Task.Delay(500, ct);
            aRan = true;
            return new AuditResult(false, [new AuditFinding("A", AuditSeverity.Error, "blocking", "from A")]);
        });
        var auditorB = new FakeLlmAuditor("B", async (_, _, ct) =>
        {
            await Task.Delay(0, ct);
            bRan = true;
            return new AuditResult(true, []);
        });

        // StopOnFirstFailure requires maxAuditIterations=1 to avoid a rework loop
        // consuming the missing work-plan entries and confusing the assertion.
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: TestAuditGates.WithPassedBuildAndTest(auditorA, auditorB),
            maxAuditIterations: 1, maxLlmAuditorParallelism: 2);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = AuditorTestHelpers.NewItem() with { };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        // Both ran concurrently (parallel), but the Error finding from A should
        // have caused audit failure (after collecting both results).
        Assert.True(aRan, "auditor A should have run");
        Assert.True(bRan, "auditor B should have run (parallel start before results aggregated)");

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
    }
}

// ── Test: declared short-circuit gates ───────────────────────────────────────

public sealed class AuditorShortCircuitTests : IDisposable
{
    private readonly string _workspace;
    public AuditorShortCircuitTests() => _workspace = Directory.CreateTempSubdirectory("codeybox-short-circuit-").FullName;
    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    [Fact]
    public async Task BlockingShortCircuitGate_SkipsSubsequentAuditors()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var reports = new CapturingAuditReportStore();
        var gateCalls = 0;
        var laterToolCalls = 0;
        var llmCalls = 0;
        var gate = new FakeToolAuditor("gate:build", (_, _, _) =>
        {
            gateCalls++;
            return Task.FromResult(new AuditResult(false, [new AuditFinding(
                "gate:build",
                AuditSeverity.Warning,
                "build failed",
                "reported as failed through AuditResult.Passed")]));
        }, canShortCircuitOnBlockingFinding: true);
        var laterTool = new FakeToolAuditor("tool:later", (_, _, _) =>
        {
            laterToolCalls++;
            return Task.FromResult(new AuditResult(true, []));
        });
        var llm = new FakeLlmAuditor("llm:review", (_, _, _) =>
        {
            llmCalls++;
            return Task.FromResult(new AuditResult(true, []));
        });

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: TestAuditGates.WithPassedBuildAndTest(laterTool, llm, gate),
            maxAuditIterations: 1,
            auditReportStore: reports);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = AuditorTestHelpers.NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Equal(1, gateCalls);
        Assert.Equal(0, laterToolCalls);
        Assert.Equal(0, llmCalls);
        Assert.Equal(["gate:build"], reports.Reports
            .Select(r => r.AuditorName)
            .Where(n => n != "test:build-and-test-pass")
            .ToArray());
    }

    [Fact]
    public async Task ErrorFindingFromPassingShortCircuitGate_SkipsSubsequentAuditors()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var reports = new CapturingAuditReportStore();
        var gateCalls = 0;
        var laterToolCalls = 0;
        var llmCalls = 0;
        var gate = new FakeToolAuditor("gate:build", (_, _, _) =>
        {
            gateCalls++;
            return Task.FromResult(new AuditResult(true, [new AuditFinding(
                "gate:build-error",
                AuditSeverity.Error,
                "build failed",
                "reported as blocking through an Error finding")]));
        }, canShortCircuitOnBlockingFinding: true);
        var laterTool = new FakeToolAuditor("tool:later", (_, _, _) =>
        {
            laterToolCalls++;
            return Task.FromResult(new AuditResult(true, []));
        });
        var llm = new FakeLlmAuditor("llm:review", (_, _, _) =>
        {
            llmCalls++;
            return Task.FromResult(new AuditResult(true, []));
        });

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: TestAuditGates.WithPassedBuildAndTest(laterTool, llm, gate),
            maxAuditIterations: 1,
            auditReportStore: reports);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = AuditorTestHelpers.NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Equal(1, gateCalls);
        Assert.Equal(0, laterToolCalls);
        Assert.Equal(0, llmCalls);
        var report = Assert.Single(reports.Reports, r => r.AuditorName != "test:build-and-test-pass");
        Assert.Equal("gate:build", report.AuditorName);
        var finding = Assert.Single(report.Findings);
        Assert.Equal("Error", finding.Severity);
        Assert.Equal("build failed", finding.Title);
    }

    [Fact]
    public async Task PassingShortCircuitGate_RunsAllAuditorsWithGateFirst()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var reports = new CapturingAuditReportStore();
        var gate = new FakeToolAuditor(
            "gate:build",
            (_, _, _) => Task.FromResult(new AuditResult(true, [])),
            canShortCircuitOnBlockingFinding: true);
        var laterTool = new FakeToolAuditor(
            "tool:later",
            (_, _, _) => Task.FromResult(new AuditResult(true, [])));
        var llm = new FakeLlmAuditor(
            "llm:review",
            (_, _, _) => Task.FromResult(new AuditResult(true, [])));

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: TestAuditGates.WithPassedBuildAndTest(laterTool, llm, gate),
            maxAuditIterations: 1,
            auditReportStore: reports);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = AuditorTestHelpers.NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(["gate:build", "tool:later", "llm:review"],
            reports.Reports.Select(r => r.AuditorName)
                .Where(n => n != "test:build-and-test-pass")
                .ToArray());
    }

    [Fact]
    public async Task DisabledShortCircuit_PreservesNonGateRegistrationOrder()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var reports = new CapturingAuditReportStore();
        var gate = new FakeToolAuditor(
            "gate:build",
            (_, _, _) => Task.FromResult(new AuditResult(false, [new AuditFinding(
                "gate:build",
                AuditSeverity.Error,
                "build failed",
                "compile error")])),
            canShortCircuitOnBlockingFinding: true);
        var laterTool = new FakeToolAuditor(
            "tool:later",
            (_, _, _) => Task.FromResult(new AuditResult(true, [])));
        var llm = new FakeLlmAuditor(
            "llm:review",
            (_, _, _) => Task.FromResult(new AuditResult(true, [])));
        var tuning = new PipelineTuningSnapshot(new PipelineTuningOptions
        {
            AuditShortCircuitEnabled = false,
        });

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: TestAuditGates.WithPassedBuildAndTest(laterTool, gate, llm),
            maxAuditIterations: 1,
            auditReportStore: reports,
            pipelineTuning: tuning);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = AuditorTestHelpers.NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Equal(["tool:later", "gate:build", "llm:review"],
            reports.Reports.Select(r => r.AuditorName)
                .Where(n => n != "test:build-and-test-pass")
                .ToArray());
    }
}

// ── Test: respects MaxLlmAuditorParallelism ───────────────────────────────────

/// <summary>
/// MaxLlmAuditorParallelism=1 must serialise LLM auditors so the total
/// wall-clock is at least (N-1) × individual-delay.
/// </summary>
public sealed class AuditorParallelismRespectsMaxTests : IDisposable
{
    private readonly string _workspace;
    public AuditorParallelismRespectsMaxTests() => _workspace = Directory.CreateTempSubdirectory("codeybox-maxpar-").FullName;
    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    [Fact]
    public async Task MaxParallelism1_ThreeAuditors_RunSequentially()
    {
        const int DelayMs = 800;
        const int AuditorCount = 3;

        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditors = Enumerable.Range(0, AuditorCount)
            .Select(i => new FakeLlmAuditor($"slow-{i}", async (_, _, ct) =>
            {
                await Task.Delay(DelayMs, ct);
                return new AuditResult(true, []);
            }))
            .ToArray();

        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: TestAuditGates.WithPassedBuildAndTest(auditors), maxLlmAuditorParallelism: 1);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = AuditorTestHelpers.NewItem();
        await tp.Store.CreateAsync(item);

        var sw = Stopwatch.StartNew();
        await tp.Pipeline.RunAsync(item, CancellationToken.None);
        sw.Stop();

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        // Sequential: at least (N-1) × delay to prove auditors didn't all overlap.
        var minExpectedMs = (AuditorCount - 1) * DelayMs;
        Assert.True(sw.ElapsedMilliseconds >= minExpectedMs,
            $"Expected wall-clock ≥ {minExpectedMs} ms (sequential) but got {sw.ElapsedMilliseconds} ms");
    }
}

// ── Test: failed LLM agent execution retry / quota classification ─────────────

public sealed class AuditorAgentExecutionFailureTests : IDisposable
{
    private readonly string _workspace;
    public AuditorAgentExecutionFailureTests() => _workspace = Directory.CreateTempSubdirectory("codeybox-llm-agent-fail-").FullName;
    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    [Fact]
    public async Task LlmAgentExecutionFailure_IsRetriedOnceInFreshSandbox()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var calls = 0;
        var sandboxIds = new List<string>();
        var auditor = new FakeLlmAuditor("flaky-review", (sandbox, _, _) =>
        {
            calls++;
            sandboxIds.Add(sandbox.Id);
            if (calls == 1)
            {
                return Task.FromResult(new AuditResult(false, [new AuditFinding(
                    "flaky-review",
                    AuditSeverity.Error,
                    "review agent failed to run",
                    "transient CLI failure")],
                    AgentSummary: "agent exited 1",
                    AgentStderr: "transient CLI failure"));
            }

            return Task.FromResult(new AuditResult(true, []));
        });

        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: TestAuditGates.WithPassedBuildAndTest(auditor), maxAuditIterations: 1);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = AuditorTestHelpers.NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(2, calls);
        Assert.Equal(2, sandboxIds.Distinct().Count());
    }

    [Fact]
    public async Task LlmAgentQuotaFailure_IsClassifiedAsQuotaFailureWithoutRetry()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var calls = 0;
        var auditor = new FakeLlmAuditor(
            "quota-review",
            (_, _, _) =>
            {
                calls++;
                return Task.FromResult(new AuditResult(false, [new AuditFinding(
                    "quota-review",
                    AuditSeverity.Error,
                    "review agent failed to run",
                    "quota")],
                    AgentSummary: "agent exited 1",
                    AgentStdout: "rate_limit_exceeded"));
            },
            AuditCapabilities.AgentCredentials);

        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: TestAuditGates.WithPassedBuildAndTest(auditor), maxAuditIterations: 1);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = AuditorTestHelpers.NewItem();
        await tp.Store.CreateAsync(item);
        var beforeFailure = DateTimeOffset.UtcNow;
        await tp.Pipeline.RunAsync(item, CancellationToken.None);
        var afterFailure = DateTimeOffset.UtcNow;

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.Equal("quota", final.FailureKind);
        Assert.NotNull(final.QuotaResetAt);
        Assert.NotNull(final.NextQuotaRetryAt);
        Assert.Equal("audit", final.QuotaRetryFrom);
        Assert.InRange(
            final.QuotaResetAt.Value,
            beforeFailure.AddMinutes(5),
            afterFailure.AddMinutes(5));
        Assert.Equal(final.QuotaResetAt, final.NextQuotaRetryAt);
        Assert.Contains("Audit agent", final.LastError);
        Assert.Equal(1, calls);
    }

    // The audit-side quota-exhaustion policy parks the work item in
    // WaitingForQuotaReset when the entire spill-to-peer pool is exhausted —
    // silently skipping the auditor would let a Pass verdict emerge with an
    // incomplete review set. (Bug 779e7dc9 briefly inverted this to a
    // warning-and-skip variant, but the bypassed-gate hole that opened drove
    // the revert to park-and-retry. The QuotaRetryScheduler resumes the
    // same iteration when quota returns.) These three tests pin the
    // parsed/probe/default reset-time accuracy for the audit-park path.
    // Reset-time accuracy for the work-phase park is covered by
    // PipelineRunnerQuotaFallbackTests.

    [Fact]
    public async Task LlmAgentQuotaFailure_AuditClassExhausted_ParksForQuotaReset_WithParsedReset()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        // Probe reports a 1-hour reset, but the parsed stderr tail wins:
        // we assert the parked QuotaResetAt is the 13-minute parsed value,
        // not the probe's 60-minute hint.
        var probe = new FixedQuotaProbe(AgentKind.Claude, DateTimeOffset.UtcNow.AddHours(1));
        var frontier = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members =
            [
                new AgentMembership
                {
                    Agent = AgentKind.Claude,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 100,
                },
            ],
        };
        var quotaOptions = new QuotaRouterOptions { MinQuotaPct = 10 };
        var router = new AgentClassRouter(
            [frontier],
            [probe],
            quotaOptions,
            NullLogger<AgentClassRouter>.Instance);

        var auditor = new FakeLlmAuditor(
            "quota-review",
            (_, _, _) => Task.FromResult(new AuditResult(false, [new AuditFinding(
                "quota-review",
                AuditSeverity.Error,
                "review agent failed to run",
                "quota")],
                AgentSummary: "agent exited 1",
                AgentStdout: "rate_limit_exceeded; retry after 13m")),
            AuditCapabilities.AgentCredentials);

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: TestAuditGates.WithPassedBuildAndTest(auditor),
            maxAuditIterations: 1,
            classRouter: router,
            auditQuotaOptions: quotaOptions);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = AuditorTestHelpers.NewItem() with { AgentClassId = "frontier" };
        await tp.Store.CreateAsync(item);
        var before = DateTimeOffset.UtcNow;
        await tp.Pipeline.RunAsync(item, CancellationToken.None);
        var after = DateTimeOffset.UtcNow;

        var final = await tp.Store.GetAsync(item.Id);
        // Single-member audit pool, quota-failing auditor → entire
        // spill-to-peer pool exhausted → park for quota reset rather than
        // silently passing audit.
        Assert.Equal(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.NotEqual(WorkItemState.Done, final.State);
        // Parsed reset MUST win: the 13-minute tail from the stderr
        // overrides the probe's 60-minute hint. A wider tolerance would
        // let the probe-reset variant pass silently if the parsed-reset
        // wiring regressed.
        Assert.NotNull(final.QuotaResetAt);
        Assert.InRange(
            final.QuotaResetAt!.Value,
            before.AddMinutes(13),
            after.AddMinutes(13));
        Assert.Equal(final.QuotaResetAt, final.NextQuotaRetryAt);
    }

    [Fact]
    public async Task LlmAgentQuotaFailure_AuditClassExhausted_ParksForQuotaReset_WithProbeResetAvailable()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        // Probe reports a 17-minute reset; stderr has no parseable tail so
        // the resolver falls back to the probe-supplied window. We assert
        // QuotaResetAt is the probe value, not a default-pause window.
        var probeResetAt = DateTimeOffset.UtcNow.AddMinutes(17);
        var frontier = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members =
            [
                new AgentMembership
                {
                    Agent = AgentKind.Claude,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 100,
                },
            ],
        };
        var quotaOptions = new QuotaRouterOptions { MinQuotaPct = 10 };
        var router = new AgentClassRouter(
            [frontier],
            [new FixedQuotaProbe(AgentKind.Claude, probeResetAt)],
            quotaOptions,
            NullLogger<AgentClassRouter>.Instance);

        var auditor = new FakeLlmAuditor(
            "quota-review",
            (_, _, _) => Task.FromResult(new AuditResult(false, [new AuditFinding(
                "quota-review",
                AuditSeverity.Error,
                "review agent failed to run",
                "quota")],
                AgentSummary: "agent exited 1",
                AgentStdout: "rate_limit_exceeded")),
            AuditCapabilities.AgentCredentials);

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: TestAuditGates.WithPassedBuildAndTest(auditor),
            maxAuditIterations: 1,
            classRouter: router,
            auditQuotaOptions: quotaOptions);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = AuditorTestHelpers.NewItem() with { AgentClassId = "frontier" };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        // Probe-supplied reset is parsed into the parked item's
        // QuotaResetAt so the retry scheduler wakes at the right moment.
        Assert.Equal(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.NotNull(final.QuotaResetAt);
        // Probe reset MUST be threaded through: a 2-second tolerance pins
        // the exact probe value and would fail if the resolver substituted
        // an arbitrary default-pause window.
        var driftTolerance = TimeSpan.FromSeconds(2);
        Assert.InRange(
            final.QuotaResetAt!.Value,
            probeResetAt - driftTolerance,
            probeResetAt + driftTolerance);
        Assert.Equal(final.QuotaResetAt, final.NextQuotaRetryAt);
    }

    [Fact]
    public async Task LlmAgentQuotaFailure_AuditClassExhausted_ParksForQuotaReset_WhenProbeHasNoReset()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var probe = new ExhaustedNoResetQuotaProbe(AgentKind.Claude);
        var frontier = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members =
            [
                new AgentMembership
                {
                    Agent = AgentKind.Claude,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 100,
                },
            ],
        };
        var quotaOptions = new QuotaRouterOptions { MinQuotaPct = 10 };
        var router = new AgentClassRouter(
            [frontier],
            [probe],
            quotaOptions,
            NullLogger<AgentClassRouter>.Instance);

        var auditor = new FakeLlmAuditor(
            "quota-review",
            (_, _, _) => Task.FromResult(new AuditResult(false, [new AuditFinding(
                "quota-review",
                AuditSeverity.Error,
                "review agent failed to run",
                "quota")],
                AgentSummary: "agent exited 1",
                AgentStdout: "rate_limit_exceeded")),
            AuditCapabilities.AgentCredentials);

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: TestAuditGates.WithPassedBuildAndTest(auditor),
            maxAuditIterations: 1,
            classRouter: router,
            auditQuotaOptions: quotaOptions);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = AuditorTestHelpers.NewItem() with { AgentClassId = "frontier" };
        await tp.Store.CreateAsync(item);
        var before = DateTimeOffset.UtcNow;
        await tp.Pipeline.RunAsync(item, CancellationToken.None);
        var after = DateTimeOffset.UtcNow;

        var final = await tp.Store.GetAsync(item.Id);
        // No probe-supplied reset → still parks; the retry scheduler will
        // use the default quota-failure-pause window (5 minutes).
        Assert.Equal(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.NotNull(final.QuotaResetAt);
        // Default pause MUST be the 5-minute window from
        // PipelineTuningOptions.DefaultQuotaFailurePause. Without an
        // assertion that pins the window, an accidental change to an
        // arbitrary retry interval (e.g. 30s or 24h) would silently pass.
        Assert.InRange(
            final.QuotaResetAt!.Value,
            before.AddMinutes(5),
            after.AddMinutes(5));
        Assert.Equal(final.QuotaResetAt, final.NextQuotaRetryAt);
    }

    // Audit-pool misconfiguration — distinct from quota exhaustion. When the
    // active audit-capability pool has at least one tagged member but every
    // candidate is filtered out for a non-quota reason (missing registered
    // runner, missing credentials, or smoke-rejected), the resolver MUST
    // surface AuditUnavailableException → failureKind="infrastructure", NOT:
    //   - park as WaitingForQuotaReset (quota returning would not make a
    //     missing runner / missing credential appear);
    //   - fall back to the work agent (would breach the audit-capability
    //     gate and silently audit on an untagged agent);
    //   - silently skip the auditor and emit a Pass with one fewer review
    //     than configured (the 094bb05 hole this commit reverted).
    [Fact]
    public async Task AuditPoolMisconfigured_AllMembersLackRegisteredRunner_FailsWithInfrastructureKind()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        // Class: Claude (work, NOT audit-tagged) + Codex (audit-tagged, but
        // no Codex runner is registered in the test pipeline so the
        // candidate cannot be dispatched).
        var frontier = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members =
            [
                new AgentMembership
                {
                    Agent = AgentKind.Claude,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 100,
                },
                new AgentMembership
                {
                    Agent = AgentKind.Codex,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 90,
                    Capabilities = [WellKnownCapabilities.Audit],
                },
            ],
        };
        var quotaOptions = new QuotaRouterOptions { MinQuotaPct = 10 };
        var router = new AgentClassRouter(
            [frontier],
            [],     // no probes — no candidate gets that far
            quotaOptions,
            NullLogger<AgentClassRouter>.Instance);

        // The auditor body should never be invoked — resolution fails first.
        var auditorCalls = 0;
        var auditor = new FakeLlmAuditor(
            "infra-review",
            (_, _, _) =>
            {
                Interlocked.Increment(ref auditorCalls);
                return Task.FromResult(new AuditResult(true, []));
            },
            AuditCapabilities.AgentCredentials);

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: TestAuditGates.WithPassedBuildAndTest(auditor),
            maxAuditIterations: 1,
            classRouter: router,
            auditQuotaOptions: quotaOptions);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = AuditorTestHelpers.NewItem() with { AgentClassId = "frontier" };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        // Item MUST land terminally failed with failureKind=infrastructure,
        // NOT Done (silent-skip regression) and NOT WaitingForQuotaReset
        // (quota-misclassification regression — the architecture finding).
        Assert.NotEqual(WorkItemState.Done, final!.State);
        Assert.NotEqual(WorkItemState.WaitingForQuotaReset, final.State);
        Assert.NotEqual(WorkItemState.AuditPassed, final.State);
        Assert.Equal(WorkItemState.Failed, final.State);
        Assert.Equal("infrastructure", final.FailureKind);
        // Resolution short-circuited before the auditor body could run —
        // there is no usable runner to dispatch into.
        Assert.Equal(0, auditorCalls);
    }

    // Sibling test to AllMembersLackRegisteredRunner: every audit-capable
    // candidate is registered but the credential provider returns null for it.
    // The resolver MUST classify this as configuration-shaped (infrastructure)
    // rather than quota — a returning quota window will not make a missing
    // credential file appear, so park-and-retry would leave the item spinning
    // on the QuotaRetryScheduler instead of surfacing the misconfig.
    [Fact]
    public async Task AuditPoolMisconfigured_AllMembersLackCredentials_FailsWithInfrastructureKind()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        // Class: Claude (work, NOT audit-tagged) + Codex (audit-tagged) and
        // Codex IS in the registry (extraAgentRunners) — so the missing-
        // runner branch is NOT what filters Codex out. The credential
        // provider returns null for every agent (StaticCredentialProvider),
        // so the pool walk hits "no credentials" and increments the
        // missing-credentials count instead.
        var frontier = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members =
            [
                new AgentMembership
                {
                    Agent = AgentKind.Claude,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 100,
                },
                new AgentMembership
                {
                    Agent = AgentKind.Codex,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 90,
                    Capabilities = [WellKnownCapabilities.Audit],
                },
            ],
        };
        var quotaOptions = new QuotaRouterOptions { MinQuotaPct = 10 };
        var router = new AgentClassRouter(
            [frontier],
            [],
            quotaOptions,
            NullLogger<AgentClassRouter>.Instance);

        var auditorCalls = 0;
        var auditor = new FakeLlmAuditor(
            "infra-review",
            (_, _, _) =>
            {
                Interlocked.Increment(ref auditorCalls);
                return Task.FromResult(new AuditResult(true, []));
            },
            AuditCapabilities.AgentCredentials);

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: TestAuditGates.WithPassedBuildAndTest(auditor),
            maxAuditIterations: 1,
            classRouter: router,
            auditQuotaOptions: quotaOptions,
            // StaticCredentialProvider returns null for every agent —
            // so Codex (the registered audit-capable candidate) has no
            // credentials and is filtered out for missing creds, not
            // quota.
            extraAgentRunners: [new PoolPassthroughRunner(AgentKind.Codex)]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = AuditorTestHelpers.NewItem() with { AgentClassId = "frontier" };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        // Same terminal-failure expectation as the missing-runner sibling:
        // configuration-shaped absence must surface as infrastructure, not
        // quota-park (which would never resolve) and not silent-pass
        // (which would breach the every-auditor-runs invariant).
        Assert.NotEqual(WorkItemState.Done, final!.State);
        Assert.NotEqual(WorkItemState.WaitingForQuotaReset, final.State);
        Assert.NotEqual(WorkItemState.AuditPassed, final.State);
        Assert.Equal(WorkItemState.Failed, final.State);
        Assert.Equal("infrastructure", final.FailureKind);
        Assert.Equal(0, auditorCalls);
    }

    // Sibling test to AllMembersLackRegisteredRunner: every audit-capable
    // candidate is registered + credentialed but the in-VM smoke gate
    // benches them all. The resolver MUST again classify as
    // configuration-shaped (infrastructure) — returning quota will not
    // make a broken sandbox CLI start working, and the bench source must
    // not be quietly absorbed into a Pass verdict either.
    [Fact]
    public async Task AuditPoolMisconfigured_AllMembersSmokeRejected_FailsWithInfrastructureKind()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var frontier = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members =
            [
                // Claude is the work agent, NOT audit-tagged — the work
                // phase still completes against it. Tag it un-audit so the
                // pool consists only of Codex; that way the entire audit
                // pool is smoke-rejected when Codex is benched.
                new AgentMembership
                {
                    Agent = AgentKind.Claude,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 100,
                },
                new AgentMembership
                {
                    Agent = AgentKind.Codex,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 90,
                    Capabilities = [WellKnownCapabilities.Audit],
                },
            ],
        };
        var quotaOptions = new QuotaRouterOptions { MinQuotaPct = 10 };
        // Wire the same in-VM smoke gate into the router AND the pipeline:
        // OrderedFallbackCandidatesAsync (router) filters smoke-rejected
        // members from its candidate list, AND EnsureAgentSmokeAvailableAsync
        // (pipeline) gates dispatch — without both, the pool walk would
        // still surface Codex and the test would not exercise the
        // "every audit-capable member smoke-rejected → throw
        // AuditUnavailableException" branch of SelectFromAuditCapablePoolAsync.
        var smokeGate = new BenchKindsSmokeGate(AgentKind.Codex);
        var dispatchAvailability = new AgentDispatchAvailability(inVmSmokeGate: smokeGate);
        var router = new AgentClassRouter(
            [frontier],
            [],
            quotaOptions,
            NullLogger<AgentClassRouter>.Instance,
            dispatchAvailability: dispatchAvailability);

        var auditorCalls = 0;
        var auditor = new FakeLlmAuditor(
            "infra-review",
            (_, _, _) =>
            {
                Interlocked.Increment(ref auditorCalls);
                return Task.FromResult(new AuditResult(true, []));
            },
            AuditCapabilities.AgentCredentials);

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: TestAuditGates.WithPassedBuildAndTest(auditor),
            maxAuditIterations: 1,
            classRouter: router,
            auditQuotaOptions: quotaOptions,
            // Codex is registered and credentialed, but the smoke gate
            // benches it on probe. OrderedFallbackCandidatesAsync then
            // filters Codex out of the pool walk, leaving zero
            // dispatchable audit-capable candidates.
            extraAgentRunners: [new PoolPassthroughRunner(AgentKind.Codex)],
            credentials: new GrantCredentialsForProvider(AgentKind.Codex),
            inVmSmokeGate: smokeGate);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = AuditorTestHelpers.NewItem() with { AgentClassId = "frontier" };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.NotEqual(WorkItemState.Done, final!.State);
        Assert.NotEqual(WorkItemState.WaitingForQuotaReset, final.State);
        Assert.NotEqual(WorkItemState.AuditPassed, final.State);
        Assert.Equal(WorkItemState.Failed, final.State);
        Assert.Equal("infrastructure", final.FailureKind);
        Assert.Equal(0, auditorCalls);
    }

    // Audit-pool QUOTA exhaustion via the router's in-process MarkExhausted
    // cache — distinct from the probe-floor / mid-iteration-failure paths.
    // AgentClassRouter.OrderedFallbackCandidatesAsync filters cached-
    // exhausted members BEFORE running probes, so when every audit-capable
    // member of the class is in the exhaustion cache the pool walk returns
    // zero candidates and quotaRejectedCount inside
    // SelectFromAuditCapablePoolAsync stays at 0. The dedicated cached-
    // exhausted helper (CountCachedExhaustedAuditCapableMembers) must
    // reclassify that empty-loop state as quota exhaustion and throw
    // AgentClassExhaustedException so the item parks for quota reset.
    // Without that helper the resolver would fall through to
    // AuditUnavailableException → failureKind="infrastructure" — a
    // misclassification that would strand items even though a normal quota
    // reset would unblock them.
    [Fact]
    public async Task AuditPool_AllAuditCapableMembersCachedExhausted_ParksForQuotaReset()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        // Class: Claude (work, NOT audit-tagged) + Codex (audit-tagged).
        // The audit pool has exactly one member; pre-seeding the router
        // marks Codex exhausted-in-cache, so OrderedFallbackCandidatesAsync
        // returns no audit-capable candidates and the cached-exhausted
        // helper is the only thing standing between this item and an
        // incorrect infrastructure-failure verdict.
        var frontier = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members =
            [
                new AgentMembership
                {
                    Agent = AgentKind.Claude,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 100,
                },
                new AgentMembership
                {
                    Agent = AgentKind.Codex,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 90,
                    Capabilities = [WellKnownCapabilities.Audit],
                },
            ],
        };
        var quotaOptions = new QuotaRouterOptions { MinQuotaPct = 10 };
        // Healthy probe deliberately wired alongside the exhaustion cache:
        // if a regression dropped the in-cache filter inside
        // OrderedFallbackCandidatesAsync, the probe would surface Codex
        // as available and the test would silently pass against a broken
        // production path. With the probe healthy, the ONLY thing that
        // can park this item is the cached-exhausted helper.
        var router = new AgentClassRouter(
            [frontier],
            [new FakeProbe(AgentKind.Codex, 80.0)],
            quotaOptions,
            NullLogger<AgentClassRouter>.Instance);
        router.MarkExhausted(
            frontier.Members[1],
            TimeSpan.FromHours(1),
            resetAt: DateTimeOffset.UtcNow.AddHours(1));

        var auditorCalls = 0;
        var auditor = new FakeLlmAuditor(
            "cached-exhausted-review",
            (_, _, _) =>
            {
                Interlocked.Increment(ref auditorCalls);
                return Task.FromResult(new AuditResult(true, []));
            },
            AuditCapabilities.AgentCredentials);

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: TestAuditGates.WithPassedBuildAndTest(auditor),
            maxAuditIterations: 1,
            classRouter: router,
            auditQuotaOptions: quotaOptions,
            extraAgentRunners: [new PoolPassthroughRunner(AgentKind.Codex)]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = AuditorTestHelpers.NewItem() with { AgentClassId = "frontier" };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        // The single audit-capable member is cached-exhausted: the resolver
        // MUST classify this as quota (park for quota reset), NOT
        // infrastructure (terminal failure that strands the item until
        // operator intervention) and NOT Done (silent-skip Pass with zero
        // review).
        Assert.Equal(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.NotEqual(WorkItemState.Failed, final.State);
        Assert.NotEqual("infrastructure", final.FailureKind);
        // The auditor body must never have run — resolution short-circuited
        // before any dispatch into a sandbox.
        Assert.Equal(0, auditorCalls);
    }

    // Sibling pin to AllAuditCapableMembersCachedExhausted: a NON-audit
    // member sitting in the exhaustion cache must NOT contribute to the
    // helper's count. The audit-capability filter inside
    // CountCachedExhaustedAuditCapableMembers is what stops the resolver
    // from misclassifying a misconfiguration (missing runner / missing
    // credentials on the only audit-capable member) as a quota crunch
    // just because some unrelated non-audit member happens to be cache-
    // exhausted. Without that filter, the helper would reclassify the
    // infrastructure failure as quota and the item would park behind
    // QuotaRetryScheduler — even though quota returning would never make
    // the missing runner / credential appear.
    [Fact]
    public async Task AuditPool_NonAuditMemberCachedExhausted_AuditCapableMisconfigured_FailsInfrastructure()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        // Class layout:
        //   - Claude (work runner, NOT audit-tagged, NOT exhausted) —
        //     keeps the work phase healthy so the item reaches audit.
        //   - Codex  (audit-tagged) with NO registered runner — the
        //     audit pool walk hits the missing-runner branch, leaving
        //     quotaRejectedCount=0 and triggering the cached-exhausted
        //     reclassification helper.
        //   - Gemini (NOT audit-tagged) pre-seeded exhausted — the
        //     non-audit cache entry that must NOT be counted by the
        //     helper.
        var frontier = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members =
            [
                new AgentMembership
                {
                    Agent = AgentKind.Claude,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 100,
                },
                new AgentMembership
                {
                    Agent = AgentKind.Codex,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 90,
                    Capabilities = [WellKnownCapabilities.Audit],
                },
                new AgentMembership
                {
                    Agent = AgentKind.Gemini,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 80,
                },
            ],
        };
        var quotaOptions = new QuotaRouterOptions { MinQuotaPct = 10 };
        var router = new AgentClassRouter(
            [frontier],
            [],
            quotaOptions,
            NullLogger<AgentClassRouter>.Instance);
        router.MarkExhausted(
            frontier.Members[2],
            TimeSpan.FromHours(1),
            resetAt: DateTimeOffset.UtcNow.AddHours(1));

        var auditorCalls = 0;
        var auditor = new FakeLlmAuditor(
            "non-audit-exhausted-review",
            (_, _, _) =>
            {
                Interlocked.Increment(ref auditorCalls);
                return Task.FromResult(new AuditResult(true, []));
            },
            AuditCapabilities.AgentCredentials);

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: TestAuditGates.WithPassedBuildAndTest(auditor),
            maxAuditIterations: 1,
            classRouter: router,
            auditQuotaOptions: quotaOptions);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = AuditorTestHelpers.NewItem() with { AgentClassId = "frontier" };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        // The audit pool's only audit-capable member has no runner →
        // infrastructure misconfig. Cache-exhausted Gemini is NOT audit-
        // capable so it MUST NOT inflate totalExhausted; without that
        // filter the resolver would mistakenly park for quota reset.
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal("infrastructure", final.FailureKind);
        Assert.NotEqual(WorkItemState.WaitingForQuotaReset, final.State);
        Assert.NotEqual(WorkItemState.Done, final.State);
        Assert.Equal(0, auditorCalls);
    }
}

// Minimal registered runner whose only job is to be in the AgentRegistry so
// the audit pool walk does not short-circuit on "missing runner" before it
// reaches the credential / smoke gates. Never actually invoked in these
// tests — audit resolution fails first.
file sealed class PoolPassthroughRunner : IAgentRunner
{
    public AgentKind Kind { get; }
    public PoolPassthroughRunner(AgentKind kind) => Kind = kind;

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
        => Task.FromResult(new AgentResult(true, "pool-passthrough", null, null));
}

// Returns non-null credentials only for the configured kind. Used for the
// smoke-rejected test where Codex must have credentials so the pool walk
// reaches the smoke gate (not the missing-credentials gate).
file sealed class GrantCredentialsForProvider : ICredentialProvider
{
    private readonly AgentKind _grantedKind;
    public GrantCredentialsForProvider(AgentKind grantedKind) => _grantedKind = grantedKind;

    public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
        => Task.FromResult<AgentCredential?>(
            agent == _grantedKind
                ? new AgentCredential(agent, new Dictionary<string, string>(), new Dictionary<string, string>())
                : null);
}

// Minimal in-VM smoke gate that returns Available=false (with a non-quota
// reason) for every configured kind and Available=true otherwise. Pins the
// "all audit-capable smoke-rejected" branch of SelectFromAuditCapablePoolAsync
// — the pool walk's GetGatedAvailabilityAsync filter drops smoke-rejected
// members, so the resolver throws AuditUnavailableException rather than
// silently parking on quota.
file sealed class BenchKindsSmokeGate : IInVmSmokeGate
{
    private readonly HashSet<AgentKind> _benched;
    public BenchKindsSmokeGate(params AgentKind[] kinds) => _benched = [.. kinds];

    public bool Enabled => true;

    public Task<AgentAvailability> EnsureAvailableAsync(
        AgentKind kind,
        InVmSmokeSandboxTarget target,
        CancellationToken ct)
        => Task.FromResult(_benched.Contains(kind)
            ? new AgentAvailability(false, "in-VM smoke: bench (test)", null, AgentAvailabilityCause.SmokeGate)
            : new AgentAvailability(true, null, null));

    public Task ProbeAllAsync(CancellationToken ct) => Task.CompletedTask;
    public Task ProbeAllAsync(InVmSmokeSandboxTarget target, CancellationToken ct) => Task.CompletedTask;
    public Task<AgentAvailability?> ForceProbeAsync(AgentKind kind, CancellationToken ct)
        => Task.FromResult<AgentAvailability?>(_benched.Contains(kind)
            ? new AgentAvailability(false, "in-VM smoke: bench (test)", null, AgentAvailabilityCause.SmokeGate)
            : new AgentAvailability(true, null, null));
}

// ── Test: sandbox isolation ───────────────────────────────────────────────────

/// <summary>
/// Two LLM auditors running in their own sandboxes must not observe each
/// other's sandbox state.  Each auditor checks that the /work directory
/// exists (freshly cloned) and records its sandbox ID; we assert distinct IDs.
/// </summary>
public sealed class AuditorParallelismIsolationTests : IDisposable
{
    private readonly string _workspace;
    public AuditorParallelismIsolationTests() => _workspace = Directory.CreateTempSubdirectory("codeybox-iso-").FullName;
    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    [Fact]
    public async Task TwoLlmAuditors_HaveSeparateSandboxes_FindingsDoNotCrossTaint()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var seenSandboxIds = new System.Collections.Concurrent.ConcurrentBag<string>();

        var auditorA = new FakeLlmAuditor("A", async (sandbox, _, ct) =>
        {
            seenSandboxIds.Add(sandbox.Id);
            await Task.Delay(10, ct);
            return new AuditResult(false, [new AuditFinding("A", AuditSeverity.Warning, "warn-A", "from A")]);
        });
        var auditorB = new FakeLlmAuditor("B", async (sandbox, _, ct) =>
        {
            seenSandboxIds.Add(sandbox.Id);
            await Task.Delay(10, ct);
            return new AuditResult(false, [new AuditFinding("B", AuditSeverity.Warning, "warn-B", "from B")]);
        });

        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: TestAuditGates.WithPassedBuildAndTest(auditorA, auditorB), maxLlmAuditorParallelism: 2);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = AuditorTestHelpers.NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        // Both auditors returned warnings only; FailingSeverity=Error → audit passes.
        Assert.Equal(WorkItemState.Done, final!.State);

        // Each auditor must have observed a distinct sandbox.
        var ids = seenSandboxIds.ToList();
        Assert.Equal(2, ids.Count);
        Assert.NotEqual(ids[0], ids[1]);
    }
}

// ── Test: cancellation ────────────────────────────────────────────────────────

/// <summary>
/// Cancelling the audit phase's CancellationToken must abort all in-flight
/// LLM auditors.  The work item should reach WorkItemState.Cancelled.
/// </summary>
public sealed class AuditorParallelismCancellationTests : IDisposable
{
    private readonly string _workspace;
    public AuditorParallelismCancellationTests() => _workspace = Directory.CreateTempSubdirectory("codeybox-cancel-par-").FullName;
    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    [Fact]
    public async Task CancelDuringAudit_AbortsInFlightAuditors_WorkItemIsCancelled()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        // Signal fires once the first auditor has started so we know the audit
        // phase is actually running before we cancel. maxCount matches the
        // number of auditors so all three can release without throwing.
        var auditPhaseStarted = new SemaphoreSlim(0, 3);

        var auditors = Enumerable.Range(0, 3)
            .Select(i => new FakeLlmAuditor($"slow-{i}", async (_, _, ct) =>
            {
                auditPhaseStarted.Release();
                await Task.Delay(30_000, ct); // blocked until cancelled
                return new AuditResult(true, []);
            }))
            .ToArray();

        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: TestAuditGates.WithPassedBuildAndTest(auditors), maxLlmAuditorParallelism: 3);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = AuditorTestHelpers.NewItem();
        await tp.Store.CreateAsync(item);

        using var cts = new CancellationTokenSource();
        var pipelineTask = Task.Run(() => tp.Pipeline.RunAsync(item, cts.Token));

        var auditing = await AuditorTestHelpers.WaitForStateAsync(
            tp.Store,
            item.Id,
            WorkItemState.Auditing,
            TimeSpan.FromSeconds(60));
        if (auditing?.State != WorkItemState.Auditing)
        {
            await CancelAndDrainPipelineAsync(cts, pipelineTask);
            Assert.Fail($"work item did not enter Auditing within 60 s; state={auditing?.State}, lastError={auditing?.LastError}");
        }

        // Wait until at least one auditor body has started. This includes the
        // per-auditor sandbox clone/checkout setup, which can be delayed by
        // unrelated full-suite git/process contention even after the audit
        // phase itself is live.
        var started = await auditPhaseStarted.WaitAsync(TimeSpan.FromMinutes(3));
        if (!started)
        {
            var current = await tp.Store.GetAsync(item.Id);
            await CancelAndDrainPipelineAsync(cts, pipelineTask);
            Assert.Fail($"LLM auditor body did not start within 180 s after Auditing; state={current?.State}, lastError={current?.LastError}");
        }

        // Cancel and wait for the pipeline to unwind.
        // RunAsync re-throws OperationCanceledException after setting state=Cancelled.
        await CancelAndDrainPipelineAsync(cts, pipelineTask);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Cancelled, final!.State);
    }

    private static async Task CancelAndDrainPipelineAsync(CancellationTokenSource cts, Task pipelineTask)
    {
        await cts.CancelAsync();
        try
        {
            await pipelineTask.WaitAsync(TimeSpan.FromMinutes(2));
        }
        catch (OperationCanceledException)
        {
            // Expected: RunAsync re-throws after setting state=Cancelled.
        }
    }

    // Defends the per-task IsCanceled branch in the audit settling loop.
    // The outer audit ct stays NOT cancelled, but one auditor task ends in
    // Canceled state via a child cancellation token (simulating a phase
    // timeout or other inner CTS firing without the parent token going
    // cancelled). Without the dedicated IsCanceled branch the loop would
    // walk past the (cancelled, task.Exception=null) entry, drop the
    // cancellation, and either (a) reach a Pass verdict with one fewer
    // auditor than configured, or (b) misroute a phase-timeout-style cancel
    // as a generic failure. The branch surfaces the OCE so the orchestrator's
    // last-resort OCE catch routes it to the transient-cancellation path
    // (HandleTransientCancellationAsync, source=Unknown), which moves the
    // work item back to Queued and increments TransientCancelRetries.
    [Fact]
    public async Task ChildTokenCancelledAuditorTask_RoutesAsTransientCancellation_NotPassNorGenericFailure()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var auditorCalls = 0;
        var auditor = new FakeLlmAuditor("inner-cancelled", (_, _, _) =>
        {
            Interlocked.Increment(ref auditorCalls);
            // A pre-cancelled INNER CTS, distinct from the audit phase's
            // outer ct. Task.FromCanceled<AuditResult> hands back a Canceled
            // task tied to innerCts.Token (NOT the outer ct). The async
            // settling lambda re-await of this cancelled Task propagates the
            // OperationCanceledException up — and because the OCE's token is
            // innerCts.Token (not outer ct), the task wrapping the auditor
            // entry ends in Canceled state without the outer ct ever firing.
            // That is exactly the shape the IsCanceled branch defends against.
            var innerCts = new CancellationTokenSource();
            innerCts.Cancel();
            return Task.FromCanceled<AuditResult>(innerCts.Token);
        });

        using var tp = TestSupport.BuildPipeline(_workspace, seed,
            auditors: TestAuditGates.WithPassedBuildAndTest(auditor), maxAuditIterations: 1);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = AuditorTestHelpers.NewItem();
        await tp.Store.CreateAsync(item);
        // Outer ct stays NOT cancelled. The only cancellation in flight is
        // the per-task inner token — this is what the IsCanceled branch is
        // there to handle.
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(1, auditorCalls);
        // The IsCanceled branch threw OperationCanceledException(ct); the
        // audit phase wraps it as PhaseCancellationException("audit", …)
        // before it leaves the phase scope. RunAsync's unattributed-cancel
        // catch routes it through HandleTransientCancellationAsync, and
        // ResumeStateForTransientRetry maps phase="audit" back to
        // WorkComplete while bumping the transient-cancel retry counter —
        // exactly the rescue path the per-task IsCanceled branch was added
        // to guarantee. Without that branch the cancelled child task would
        // either (a) be silently dropped (Pass on a skipped review) or
        // (b) misroute as a generic Failed/timeout.
        Assert.Equal(WorkItemState.WorkComplete, final!.State);
        Assert.Equal(1, final.TransientCancelRetries);
        // Regression guards: the cancellation MUST NOT land as a code-quality
        // verdict (Done / AuditPassed / AuditFailed) or as a generic Failed.
        Assert.NotEqual(WorkItemState.Done, final.State);
        Assert.NotEqual(WorkItemState.AuditPassed, final.State);
        Assert.NotEqual(WorkItemState.AuditFailed, final.State);
        Assert.NotEqual(WorkItemState.Failed, final.State);
    }
}
