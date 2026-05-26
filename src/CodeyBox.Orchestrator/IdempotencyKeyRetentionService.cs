using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Hourly background sweep that deletes <c>idempotency_keys</c> rows whose
/// <c>expires_at</c> has passed. Without this, the table grows unbounded
/// because <see cref="SqliteIdempotencyStore.PutAsync"/> only refreshes
/// expired rows opportunistically (on a same-key replay after the TTL); keys
/// that are never reused stay in the table forever.
/// </summary>
public sealed class IdempotencyKeyRetentionService : BackgroundService
{
    private readonly IIdempotencyStore _store;
    private readonly ILogger<IdempotencyKeyRetentionService> _log;
    private readonly TimeSpan _interval;

    public IdempotencyKeyRetentionService(
        IIdempotencyStore store,
        ILogger<IdempotencyKeyRetentionService> log,
        TimeSpan? interval = null)
    {
        _store = store;
        _log = log;
        _interval = interval ?? TimeSpan.FromHours(1);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        do
        {
            await RunSweepAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunSweepAsync(CancellationToken ct)
    {
        try
        {
            var deleted = await _store.DeleteExpiredAsync(DateTimeOffset.UtcNow, ct);
            if (deleted > 0)
                _log.LogInformation(
                    "IdempotencyKeyRetention: deleted {Count} expired idempotency rows", deleted);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "IdempotencyKeyRetention: sweep failed");
        }
    }
}
