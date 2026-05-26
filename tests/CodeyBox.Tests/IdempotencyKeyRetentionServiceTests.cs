using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="IdempotencyKeyRetentionService"/>: the hourly sweep
/// BackgroundService that purges expired rows. Without these, the entire
/// service is dead code from a coverage standpoint and a regression that
/// (a) never calls DeleteExpiredAsync, (b) lets one failure tear down the
/// host, or (c) silently changes the default interval to something
/// pathological would not be caught.
/// </summary>
public sealed class IdempotencyKeyRetentionServiceTests
{
    private sealed class RecordingStore : IIdempotencyStore
    {
        private readonly Func<int>? _resultOrThrow;
        public int DeleteExpiredCallCount { get; private set; }
        public List<DateTimeOffset> CutoffsObserved { get; } = new();

        public RecordingStore(Func<int>? resultOrThrow = null) => _resultOrThrow = resultOrThrow;

        public Task<IdempotencyLookupResult> LookupAsync(string key, string bodyHash, DateTimeOffset now, CancellationToken ct = default)
            => Task.FromResult(new IdempotencyLookupResult(IdempotencyLookupOutcome.Miss, null));

        public Task PutAsync(IdempotencyEntry entry, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<int> DeleteExpiredAsync(DateTimeOffset cutoff, CancellationToken ct = default)
        {
            DeleteExpiredCallCount++;
            CutoffsObserved.Add(cutoff);
            var result = _resultOrThrow?.Invoke() ?? 0;
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task ExecuteAsync_InvokesDeleteExpiredOnFirstTick()
    {
        // The sweep runs once immediately at startup, then every interval.
        // A regression that only ran on subsequent ticks would leave expired
        // rows in place for the full interval after each host restart.
        var store = new RecordingStore();
        var service = new IdempotencyKeyRetentionService(
            store, NullLogger<IdempotencyKeyRetentionService>.Instance,
            interval: TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var task = service.StartAsync(cts.Token);
        // Give the first iteration time to run.
        await WaitForAsync(() => store.DeleteExpiredCallCount >= 1, TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);
        await task;

        Assert.True(store.DeleteExpiredCallCount >= 1,
            $"expected DeleteExpiredAsync to run at least once; observed {store.DeleteExpiredCallCount} calls");
    }

    [Fact]
    public async Task ExecuteAsync_SweepFailure_DoesNotTerminateService()
    {
        // RunSweepAsync wraps DeleteExpiredAsync in a try/catch that logs the
        // failure as a warning. A regression that lets the exception propagate
        // out of ExecuteAsync would tear down the BackgroundService for the
        // remaining lifetime of the host — every later sweep is silently lost.
        var callCount = 0;
        var store = new RecordingStore(() =>
        {
            callCount++;
            if (callCount == 1)
                throw new InvalidOperationException("transient sqlite hiccup");
            return 0; // subsequent calls succeed
        });
        var logger = new CapturingLogger<IdempotencyKeyRetentionService>();
        var service = new IdempotencyKeyRetentionService(
            store, logger, interval: TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var task = service.StartAsync(cts.Token);
        // Sweep #1 throws; we need to see sweep #2 succeed AFTER the catch.
        await WaitForAsync(() => store.DeleteExpiredCallCount >= 2, TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);
        await task;

        Assert.True(store.DeleteExpiredCallCount >= 2,
            $"expected at least 2 sweeps despite the first one throwing; observed {store.DeleteExpiredCallCount}");
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationDuringSweep_ExitsCleanly()
    {
        // OperationCanceledException must NOT be re-raised as a warning — a
        // graceful host shutdown shouldn't emit a noisy log line every time.
        // RunSweepAsync's separate `catch (OperationCanceledException) {}` makes
        // this safe; this test pins it.
        var store = new RecordingStore(() => throw new OperationCanceledException());
        var logger = new CapturingLogger<IdempotencyKeyRetentionService>();
        var service = new IdempotencyKeyRetentionService(
            store, logger, interval: TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource();
        var task = service.StartAsync(cts.Token);
        await WaitForAsync(() => store.DeleteExpiredCallCount >= 1, TimeSpan.FromSeconds(2));
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);
        await task;

        // No Warning emitted for the cancellation path.
        Assert.DoesNotContain(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Exception is OperationCanceledException);
    }

    [Fact]
    public void Constructor_DefaultInterval_IsOneHour()
    {
        // The constructor exposes an optional interval; when omitted the
        // service must default to TimeSpan.FromHours(1). A regression that
        // dropped the default to a very small value would hammer the SQLite
        // write lock; bumping it past an hour would let expired rows linger.
        var field = typeof(IdempotencyKeyRetentionService)
            .GetField("_interval",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        var service = new IdempotencyKeyRetentionService(
            new RecordingStore(), NullLogger<IdempotencyKeyRetentionService>.Instance);
        var value = (TimeSpan)field!.GetValue(service)!;
        Assert.Equal(TimeSpan.FromHours(1), value);
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!predicate() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(25);
    }
}
