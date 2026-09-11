using System.Collections.Concurrent;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Regression coverage for the 2026-09-11 dispatch stall: 6 workers against 6
/// sandbox permits, every worker holding its work-phase sandbox while waiting
/// for its next-phase sandbox, zero progress for hours while health signals
/// stayed green. The fix has three parts, each tested here:
/// <list type="number">
/// <item>startup validation rejects sandbox ceilings below 2x the worker count
/// (see <c>WorkerPoolOptionsValidationTests</c> for the message contract),</item>
/// <item>the pipeline surrenders its work-phase sandbox before acquiring
/// audit/merge sandboxes (release-then-acquire),</item>
/// <item>a permit wait past the warning threshold logs the work item and phase.</item>
/// </list>
/// </summary>
public sealed class SandboxAdmissionDeadlockTests : IDisposable
{
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(30);

    private readonly string _workspace;

    public SandboxAdmissionDeadlockTests() =>
        _workspace = Directory.CreateTempSubdirectory("codeybox-sandbox-deadlock-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task HoldWhileAcquire_AtOneToOneRatio_Blocks()
    {
        // Characterises the incident shape: with permits == workers, every
        // worker holding one permit while waiting for another never frees a
        // permit. This is why the 1:1 configuration is rejected at startup.
        var gate = new SandboxAdmissionGate(maxConcurrent: 2);
        using var first = await gate.AcquireAsync(CancellationToken.None);
        using var second = await gate.AcquireAsync(CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var ex = await Record.ExceptionAsync(() => gate.AcquireAsync(timeout.Token).AsTask());

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }

    [Fact]
    public async Task ReleaseThenAcquire_AtMinimumRatio_Drains()
    {
        // The supported-shape counterpart: at the minimum supported ratio
        // (2 permits per worker) every worker can surrender its work-phase
        // permit and still acquire its next-phase permit, so the pool drains.
        const int workers = 3;
        var inner = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var provider = SandboxAdmissionControlledProvider.Wrap(
            inner,
            maxConcurrentSandboxes: 2 * workers,
            NullLogger.Instance);
        var snapshot = Assert.IsAssignableFrom<ISandboxAdmissionSnapshot>(provider);
        var tuning = new PipelineTuningSnapshot(new PipelineTuningOptions());
        using var timeout = new CancellationTokenSource(TestDeadline);

        var completed = 0;
        var tasks = Enumerable.Range(0, workers).Select(async workerIndex =>
        {
            await using var context = new WorkSandboxContext(provider, tuning, NullLogger.Instance);
            _ = await context.GetOrCreateSandboxAsync(
                new SandboxSpec { ImageReference = "ignored" }, timeout.Token);

            // Work phase complete: surrender the permit before acquiring the
            // next phase's sandbox (the pipeline's release-then-acquire shape).
            await context.ReleaseActiveSandboxAsync();
            await using var nextPhase = await provider.CreateAsync(
                new SandboxSpec { ImageReference = "ignored" }, timeout.Token);
            await Task.Delay(25, timeout.Token);
            Interlocked.Increment(ref completed);
        });

        await Task.WhenAll(tasks).WaitAsync(timeout.Token);

        Assert.Equal(workers, completed);
        Assert.Equal(0, CountAdmitted(snapshot));
    }

    [Fact]
    public async Task ReleaseActiveSandbox_IsIdempotentAndRecreatable()
    {
        var inner = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var provider = SandboxAdmissionControlledProvider.Wrap(
            inner, maxConcurrentSandboxes: 1, NullLogger.Instance);
        var snapshot = Assert.IsAssignableFrom<ISandboxAdmissionSnapshot>(provider);
        var tuning = new PipelineTuningSnapshot(new PipelineTuningOptions());

        await using var context = new WorkSandboxContext(provider, tuning, NullLogger.Instance);
        _ = await context.GetOrCreateSandboxAsync(
            new SandboxSpec { ImageReference = "ignored" }, CancellationToken.None);
        Assert.Equal(1, CountAdmitted(snapshot));

        await context.ReleaseActiveSandboxAsync();
        Assert.Equal(0, CountAdmitted(snapshot));

        // Releasing with nothing held is a no-op, and the next phase can
        // transparently provision a fresh sandbox afterwards.
        await context.ReleaseActiveSandboxAsync();
        _ = await context.GetOrCreateSandboxAsync(
            new SandboxSpec { ImageReference = "ignored" }, CancellationToken.None);
        Assert.Equal(1, CountAdmitted(snapshot));
    }

    [Fact]
    public async Task PermitWaitBeyondThreshold_EmitsDiagnosticNamingWorkItemAndPhase()
    {
        var log = new CapturingLogger();
        var gate = new SandboxAdmissionGate(
            maxConcurrent: 1,
            log: log,
            waitWarningThresholdProvider: static () => TimeSpan.FromMilliseconds(50));
        using var held = await gate.AcquireAsync(CancellationToken.None);

        using var scope = SandboxPermitWaitScope.Begin("deadlock-item-1", "audit");
        var acquire = gate.AcquireAsync(CancellationToken.None).AsTask();

        var warning = await WaitForMessageAsync(
            log,
            message => message.Contains("deadlock-item-1", StringComparison.Ordinal)
                && message.Contains("audit", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));

        held.Dispose();
        await acquire.WaitAsync(TestDeadline);

        Assert.NotNull(warning);
    }

    [Fact]
    public async Task PermitWaitBeyondThreshold_WithoutScope_EmitsGenericWarning()
    {
        var log = new CapturingLogger();
        var gate = new SandboxAdmissionGate(
            maxConcurrent: 1,
            log: log,
            waitWarningThresholdProvider: static () => TimeSpan.FromMilliseconds(50));
        using var held = await gate.AcquireAsync(CancellationToken.None);

        var acquire = gate.AcquireAsync(CancellationToken.None).AsTask();
        var warning = await WaitForMessageAsync(
            log,
            message => message.Contains("MaxConcurrentSandboxes", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));

        held.Dispose();
        await acquire.WaitAsync(TestDeadline);

        Assert.NotNull(warning);
    }

    [Fact]
    public async Task PermitWaitWarning_DisabledByZeroThreshold()
    {
        var log = new CapturingLogger();
        var gate = new SandboxAdmissionGate(
            maxConcurrent: 1,
            log: log,
            waitWarningThresholdProvider: static () => TimeSpan.Zero);
        using var held = await gate.AcquireAsync(CancellationToken.None);

        var acquire = gate.AcquireAsync(CancellationToken.None).AsTask();
        await Task.Delay(300);
        Assert.Empty(log.Messages);

        held.Dispose();
        await acquire.WaitAsync(TestDeadline);
    }

    [Fact]
    public async Task SingleWorkerReleasesWorkSandbox_BeforeAcquiringNextPhaseSandbox()
    {
        // Exact 1:1 incident shape through real wiring: one worker holds the
        // only permit via its work-phase reusable sandbox, completes work, and
        // must then acquire its next-phase sandbox. Without the surrender step
        // the acquisition below would block forever (see the
        // HoldWhileAcquire test); with it, the phase handoff succeeds.
        var inner = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var provider = SandboxAdmissionControlledProvider.Wrap(
            inner, maxConcurrentSandboxes: 1, NullLogger.Instance);
        var snapshot = Assert.IsAssignableFrom<ISandboxAdmissionSnapshot>(provider);
        var tuning = new PipelineTuningSnapshot(new PipelineTuningOptions());
        using var timeout = new CancellationTokenSource(TestDeadline);

        await using var context = new WorkSandboxContext(provider, tuning, NullLogger.Instance);
        _ = await context.GetOrCreateSandboxAsync(
            new SandboxSpec { ImageReference = "ignored" }, timeout.Token);
        Assert.Equal(1, CountAdmitted(snapshot));

        await context.ReleaseActiveSandboxAsync();

        await using var nextPhase = await provider.CreateAsync(
            new SandboxSpec { ImageReference = "ignored" }, timeout.Token);
        Assert.Equal(1, CountAdmitted(snapshot));
    }

    [Fact]
    public async Task QueuedItemPickedUp_WhileWorkersArePastWorkPhase()
    {
        // All worker slots occupied by items whose work phase has completed
        // (mid-pipeline, acquiring next-phase sandboxes at the minimum
        // supported ratio) must still drain so a queued item is picked up.
        const int workers = 2;
        var dbPath = Path.Combine(_workspace, $"pool-{Guid.NewGuid():N}.db");
        using var store = new SqliteWorkItemStore(dbPath);
        var gated = SandboxAdmissionControlledProvider.Wrap(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance),
            maxConcurrentSandboxes: 2 * workers,
            NullLogger.Instance);
        var tuning = new PipelineTuningSnapshot(new PipelineTuningOptions());
        var gate = new Barrier(participantCount: 2);
        var arrivals = 0;
        var started = new ConcurrentBag<WorkItemId>();
        var pipeline = new PhasedPipelineRunner(
            store,
            gated,
            tuning,
            onStart: id =>
            {
                started.Add(id);
                if (Interlocked.Increment(ref arrivals) <= workers
                    && !gate.SignalAndWait(millisecondsTimeout: 20000))
                {
                    throw new TimeoutException("Timed out waiting for both workers to enter the work phase.");
                }
            });
        var queue = new InMemoryTaskQueue();
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue,
            store,
            pipeline,
            registry,
            new OrchestratorOptions { MaxConcurrentWorkers = workers },
            NullLogger<OrchestratorService>.Instance);

        for (var i = 0; i < workers + 1; i++)
        {
            var item = NewItem();
            await store.CreateAsync(item);
            await queue.EnqueueAsync(item.Id);
        }

        await svc.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (!timeout.IsCancellationRequested)
            {
                var done = 0;
                await foreach (var item in store.ListByStateAsync(WorkItemState.Done))
                    done++;
                if (done >= workers + 1)
                    break;
                await Task.Delay(50, timeout.Token);
            }

            var doneCount = 0;
            await foreach (var item in store.ListByStateAsync(WorkItemState.Done))
                doneCount++;
            Assert.Equal(workers + 1, doneCount);
            Assert.Equal(workers + 1, started.Distinct().Count());
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    private static int CountAdmitted(ISandboxAdmissionSnapshot snapshot) =>
        snapshot.CurrentAdmittedSandboxes;

    private static async Task<string?> WaitForMessageAsync(
        CapturingLogger log, Func<string, bool> predicate, TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        while (!timeoutCts.IsCancellationRequested)
        {
            var match = log.Messages.FirstOrDefault(predicate);
            if (match is not null)
                return match;
            try
            {
                await Task.Delay(20, timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        return log.Messages.FirstOrDefault(predicate);
    }

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "sandbox deadlock test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = "feature/x",
        PushUpstream = false,
    };

    /// <summary>
    /// Mimics the fixed pipeline shape through real wiring: hold the
    /// work-phase reusable sandbox, surrender it when the work phase
    /// completes, then acquire the next phase's sandbox directly.
    /// </summary>
    private sealed class PhasedPipelineRunner(
        IWorkItemStore store,
        ISandboxProvider sandboxes,
        PipelineTuningSnapshot tuning,
        Action<WorkItemId> onStart) : IPipelineRunner
    {
        public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            onStart(item.Id);
            await using var context = new WorkSandboxContext(sandboxes, tuning, NullLogger.Instance);
            _ = await context.GetOrCreateSandboxAsync(
                new SandboxSpec { ImageReference = "ignored" }, ct);
            await Task.Delay(25, ct);

            await context.ReleaseActiveSandboxAsync();
            await using var nextPhase = await sandboxes.CreateAsync(
                new SandboxSpec { ImageReference = "ignored" }, ct);
            await Task.Delay(25, ct);

            await store.UpdateAsync(item.With(WorkItemState.Done), ct);
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly object _sync = new();
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_sync)
                    return _messages.ToArray();
            }
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_sync)
                _messages.Add($"[{logLevel}] {formatter(state, exception)}");
        }
    }
}
