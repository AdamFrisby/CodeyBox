using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Integration tests for the two <see cref="PhaseCancellationException"/>
/// catch blocks added to the <see cref="OrchestratorService"/> worker loop.
/// Both catches emit structured log lines used by post-incident triage to
/// correlate the worker exit with the phase + source the pipeline saw:
/// a typo in the template, swallowing the wrong exception type, or omitting
/// the phase/source placeholders would silently regress the operator dashboard
/// contract advertised in docs/audit-logging.md and docs/work-items.md.
/// </summary>
public sealed class OrchestratorServicePhaseCancellationLogTests : IDisposable
{
    // Positive event-driven waits below (pipeline-start signal, log-entry match)
    // use this as a backstop only: the awaited state WILL occur on a correct run,
    // so the timeout just needs headroom for a correct-but-starved dispatch on the
    // co-resident capped full-suite host. A real regression still fails because the
    // awaited state never occurs. Same class as commits 0df5ee7 / 47661bd.
    private static readonly TimeSpan StarvationBackstopTimeout = TimeSpan.FromSeconds(60);

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-orch-pcex-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public OrchestratorServicePhaseCancellationLogTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Queued,
    };

    [Fact]
    public async Task HostShutdown_PhaseCancellation_LogsAbortedByHostShutdown()
    {
        // Pipeline blocks until host shutdown fires, then throws PhaseCancellationException
        // tagged with the HostShutdown source. The worker loop's
        // `catch (PhaseCancellationException pex) when (ct.IsCancellationRequested)`
        // branch should log "aborted by host shutdown: phase=… source=…".
        var item = NewItem();
        await _store.CreateAsync(item);

        var pipeline = new ThrowingPipelineRunner(
            phase: "rework",
            source: CancellationSources.HostShutdown,
            waitForHostShutdown: true);

        var queue = new InMemoryTaskQueue();
        await queue.EnqueueAsync(item.Id);

        var logger = new CapturingLogger<OrchestratorService>();
        using var service = new OrchestratorService(
            queue,
            _store,
            pipeline,
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            logger);

        await service.StartAsync(CancellationToken.None);
        await pipeline.Started.Task.WaitAsync(StarvationBackstopTimeout);
        await service.StopAsync(new CancellationTokenSource(StarvationBackstopTimeout).Token);

        var entry = await WaitForLogAsync(logger, e =>
            e.Properties.TryGetValue("CancellationSource", out var s)
                && s is string ss
                && ss == CancellationSources.HostShutdown
                && e.Message.Contains("aborted by host shutdown", StringComparison.Ordinal));

        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("rework", entry.Properties["Phase"]);
        Assert.Equal(item.Id, entry.Properties["Id"]);
        Assert.Contains("phase=rework", entry.Message, StringComparison.Ordinal);
        Assert.Contains($"source={CancellationSources.HostShutdown}", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenericPhaseCancellation_LogsCancelledInPhase()
    {
        // Pipeline throws PhaseCancellationException immediately (no host shutdown
        // in flight). The worker loop's bare `catch (PhaseCancellationException pex)`
        // branch should log "cancelled in phase {Phase}: source={CancellationSource}".
        var item = NewItem();
        await _store.CreateAsync(item);

        var pipeline = new ThrowingPipelineRunner(
            phase: "audit",
            source: CancellationSources.StuckProbe,
            waitForHostShutdown: false);

        var queue = new InMemoryTaskQueue();
        await queue.EnqueueAsync(item.Id);

        var logger = new CapturingLogger<OrchestratorService>();
        using var service = new OrchestratorService(
            queue,
            _store,
            pipeline,
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            logger);

        await service.StartAsync(CancellationToken.None);

        var entry = await WaitForLogAsync(logger, e =>
            e.Properties.TryGetValue("CancellationSource", out var s)
                && s is string ss
                && ss == CancellationSources.StuckProbe
                && e.Message.Contains("cancelled in phase", StringComparison.Ordinal));

        await service.StopAsync(new CancellationTokenSource(StarvationBackstopTimeout).Token);

        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("audit", entry.Properties["Phase"]);
        Assert.Equal(item.Id, entry.Properties["Id"]);
        Assert.Contains("audit", entry.Message, StringComparison.Ordinal);
        Assert.Contains(CancellationSources.StuckProbe, entry.Message, StringComparison.Ordinal);
    }

    private static Task<CapturedLogEntry> WaitForLogAsync(
        CapturingLogger<OrchestratorService> logger,
        Func<CapturedLogEntry, bool> predicate)
    {
        // Push-based wait: wakes on each new log call rather than polling a
        // wall-clock deadline that races ThreadPool starvation under the capped
        // full-suite load. The timeout is only a no-log-regression backstop.
        return logger.WaitForEntryAsync(predicate, StarvationBackstopTimeout);
    }

    /// <summary>
    /// Test-only <see cref="IPipelineRunner"/> that signals when RunAsync starts
    /// and then throws a <see cref="PhaseCancellationException"/>. Optionally
    /// waits for the host shutdown token to fire first so the worker loop's
    /// host-shutdown-gated catch (<c>when (ct.IsCancellationRequested)</c>)
    /// branch can be exercised.
    /// </summary>
    private sealed class ThrowingPipelineRunner : IPipelineRunner
    {
        private readonly string _phase;
        private readonly string _source;
        private readonly bool _waitForHostShutdown;

        public ThrowingPipelineRunner(string phase, string source, bool waitForHostShutdown)
        {
            _phase = phase;
            _source = source;
            _waitForHostShutdown = waitForHostShutdown;
        }

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            Started.TrySetResult();
            if (_waitForHostShutdown)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, hostShutdownToken);
                }
                catch (OperationCanceledException)
                {
                    // Fall through to throw PhaseCancellationException below
                }
            }
            else
            {
                // Yield once so the worker has finished its bookkeeping before
                // we throw — keeps the catch site deterministic.
                await Task.Yield();
            }
            throw new PhaseCancellationException(_phase, _source, new TaskCanceledException());
        }
    }
}
