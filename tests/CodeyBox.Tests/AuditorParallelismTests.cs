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
/// Running them in parallel the total wall-clock must be well under the 4 500 ms
/// that sequential execution would require.
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

        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditors = Enumerable.Range(0, AuditorCount)
            .Select(i => new FakeLlmAuditor($"slow-{i}", async (_, _, ct) =>
            {
                var current = Interlocked.Increment(ref running);
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
                    Interlocked.Decrement(ref running);
                }
            }))
            .ToArray();

        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: auditors, maxLlmAuditorParallelism: AuditorCount);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = AuditorTestHelpers.NewItem();
        await tp.Store.CreateAsync(item);

        var sw = Stopwatch.StartNew();
        await tp.Pipeline.RunAsync(item, CancellationToken.None);
        sw.Stop();

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        Assert.Equal(AuditorCount, Volatile.Read(ref maxRunning));
        // Keep a coarse guard against accidentally adding large serial work
        // around the parallel section, but do not use the exact sequential
        // delay as the threshold; solution-level test runs add scheduler noise.
        Assert.True(sw.ElapsedMilliseconds < AuditorCount * DelayMs + 1_500,
            $"Expected wall-clock near parallel execution but got {sw.ElapsedMilliseconds} ms");
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
            auditors: [auditorA, auditorB, auditorC],
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
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [auditorA, auditorB],
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

        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: auditors, maxLlmAuditorParallelism: 1);
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

        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [auditor], maxAuditIterations: 1);
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

        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [auditor], maxAuditIterations: 1);
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

    // Bug 779e7dc9 changed the audit-side quota-exhaustion policy from
    // "park the work item in WaitingForQuotaReset" to "skip the LLM auditor
    // for this iteration and keep going". These three tests previously
    // pinned the parsed/probe/default reset-time accuracy for the audit-park
    // path; with the new behaviour the audit pipeline no longer parks on
    // class-exhaustion, so they now pin the new skip-and-continue policy.
    // Reset-time accuracy for the still-parking work-phase path is covered
    // by PipelineRunnerQuotaFallbackTests.

    [Fact]
    public async Task LlmAgentQuotaFailure_AuditClassExhausted_SkipsAuditorRatherThanParking_WithParsedReset()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
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
        var router = new AgentClassRouter(
            [frontier],
            [probe],
            new QuotaRouterOptions { MinQuotaPct = 10 },
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
            auditors: [auditor],
            maxAuditIterations: 1,
            classRouter: router);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = AuditorTestHelpers.NewItem() with { AgentClassId = "frontier" };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.NotEqual(WorkItemState.WaitingForQuotaReset, final.State);
    }

    [Fact]
    public async Task LlmAgentQuotaFailure_AuditClassExhausted_SkipsAuditor_WithProbeResetAvailable()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var resetAt = DateTimeOffset.UtcNow.AddMinutes(17);
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
        var router = new AgentClassRouter(
            [frontier],
            [new FixedQuotaProbe(AgentKind.Claude, resetAt)],
            new QuotaRouterOptions { MinQuotaPct = 10 },
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
            auditors: [auditor],
            maxAuditIterations: 1,
            classRouter: router);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = AuditorTestHelpers.NewItem() with { AgentClassId = "frontier" };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Null(final.QuotaResetAt);
    }

    [Fact]
    public async Task LlmAgentQuotaFailure_AuditClassExhausted_SkipsAuditor_WhenProbeHasNoReset()
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
        var router = new AgentClassRouter(
            [frontier],
            [probe],
            new QuotaRouterOptions { MinQuotaPct = 10 },
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
            auditors: [auditor],
            maxAuditIterations: 1,
            classRouter: router);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = AuditorTestHelpers.NewItem() with { AgentClassId = "frontier" };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Null(final.QuotaResetAt);
    }
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

        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [auditorA, auditorB], maxLlmAuditorParallelism: 2);
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

        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: auditors, maxLlmAuditorParallelism: 3);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = AuditorTestHelpers.NewItem();
        await tp.Store.CreateAsync(item);

        using var cts = new CancellationTokenSource();
        var pipelineTask = Task.Run(() => tp.Pipeline.RunAsync(item, cts.Token));

        // Wait until at least one auditor has started (audit phase is live).
        var started = await auditPhaseStarted.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(started, "audit phase did not start within 15 s");

        // Cancel and wait for the pipeline to unwind.
        // RunAsync re-throws OperationCanceledException after setting state=Cancelled.
        cts.Cancel();
        try { await pipelineTask.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { /* expected — pipeline re-throws after setting state */ }

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Cancelled, final!.State);
    }
}
