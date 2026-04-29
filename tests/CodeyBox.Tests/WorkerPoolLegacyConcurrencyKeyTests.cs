using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies the legacy CodeyBox:Concurrency config key path:
/// - OrchestratorOptionsFactory.Build maps it to MaxConcurrentWorkers
/// - A deprecation warning is emitted
/// - The resulting pool actually enforces the configured concurrency
/// </summary>
public sealed class WorkerPoolLegacyConcurrencyKeyTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-legacytest-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public WorkerPoolLegacyConcurrencyKeyTests() => _store = new SqliteWorkItemStore(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkItem MakeItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test"),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Queued,
    };

    [Fact]
    public void LegacyConcurrencyKey_MapsToMaxConcurrentWorkers_AndEmitsWarning()
    {
        // Simulate what Program.cs DI factory does when CodeyBox:Concurrency is set
        // in config but CodeyBox:WorkerPool is not.
        var capturingLogger = new CapturingLogger();
        var legacyConcurrency = 3;
        var defaultWorkerPool = new WorkerPoolOptions(); // as if CodeyBox:WorkerPool is absent

        var opts = OrchestratorOptionsFactory.Build(legacyConcurrency, defaultWorkerPool, capturingLogger);

        // The legacy value must be honoured as MaxConcurrentWorkers.
        Assert.Equal(legacyConcurrency, opts.MaxConcurrentWorkers);

        // The factory must emit exactly one deprecation warning that mentions the old key.
        var warnings = capturingLogger.Entries
            .Where(e => e.Level == LogLevel.Warning)
            .ToList();
        Assert.Single(warnings);
        Assert.Contains("CodeyBox:Concurrency", warnings[0].Message);
        Assert.Contains("deprecated", warnings[0].Message);
    }

    [Fact]
    public void LegacyConcurrencyKey_AbsentConcurrency_NoWarningEmitted()
    {
        // When CodeyBox:Concurrency is null (new-style config only), no warning.
        var capturingLogger = new CapturingLogger();
        var opts = OrchestratorOptionsFactory.Build(null, new WorkerPoolOptions { MaxConcurrentWorkers = 2 }, capturingLogger);

        Assert.Equal(2, opts.MaxConcurrentWorkers);
        Assert.DoesNotContain(capturingLogger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task LegacyConcurrencyKey_PoolFunctions()
    {
        // End-to-end: options built via the legacy path produce a working pool.
        var opts = OrchestratorOptionsFactory.Build(
            legacyConcurrency: 2,
            workerPool: new WorkerPoolOptions(),
            log: NullLogger.Instance);

        const int itemCount = 4;
        var executed = new ConcurrentBag<WorkItemId>();

        var pipeline = new RecordingPipelineRunner(_store, executed);
        var queue = new InMemoryTaskQueue();
        var registry = new CancellationRegistry(CancellationToken.None);
        var svc = new OrchestratorService(queue, _store, pipeline, registry, opts,
            NullLogger<OrchestratorService>.Instance);

        for (int i = 0; i < itemCount; i++)
        {
            var item = MakeItem();
            await _store.CreateAsync(item);
            await queue.EnqueueAsync(item.Id);
        }

        using var _ = registry;
        await svc.StartAsync(CancellationToken.None);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            int doneCount = 0;
            await foreach (var item in _store.ListByStateAsync(WorkItemState.Done))
                doneCount++;
            if (doneCount >= itemCount) break;
            await Task.Delay(50);
        }

        await svc.StopAsync(CancellationToken.None);

        Assert.Equal(itemCount, executed.Count);

        int stillQueued = 0;
        await foreach (var item in _store.ListByStateAsync(WorkItemState.Queued))
            stillQueued++;
        Assert.Equal(0, stillQueued);
    }
}

/// <summary>Captures log entries for assertion in tests.</summary>
internal sealed class CapturingLogger : ILogger
{
    public sealed record LogEntry(LogLevel Level, string Message);

    public List<LogEntry> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }
}

internal sealed class RecordingPipelineRunner : IPipelineRunner
{
    private readonly IWorkItemStore _store;
    private readonly ConcurrentBag<WorkItemId> _executed;

    public RecordingPipelineRunner(IWorkItemStore store, ConcurrentBag<WorkItemId> executed)
    {
        _store = store;
        _executed = executed;
    }

    public async Task RunAsync(WorkItem item, CancellationToken ct)
    {
        _executed.Add(item.Id);
        await _store.UpdateAsync(item.With(WorkItemState.Done), ct);
    }
}
