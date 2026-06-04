using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Re-enqueues work items parked in <see cref="WorkItemState.WaitingForAgentResume"/>
/// when operator pause state changes. The router may park them again if their
/// only eligible agent remains paused; this scheduler never records quota retry
/// attempts because operator pause is not quota exhaustion.
/// </summary>
public sealed class AgentPauseRetryScheduler : BackgroundService
{
    private static readonly TimeSpan PeriodicSweepInterval = TimeSpan.FromMinutes(1);

    private readonly IWorkItemStore _store;
    private readonly ITaskQueue _queue;
    private readonly IAgentPauseController _pauses;
    private readonly ILogger<AgentPauseRetryScheduler> _log;
    private readonly SemaphoreSlim _wake = new(0);

    public AgentPauseRetryScheduler(
        IWorkItemStore store,
        ITaskQueue queue,
        IAgentPauseController pauses,
        ILogger<AgentPauseRetryScheduler> log,
        IAgentPauseSignal? signal = null)
    {
        _store = store;
        _queue = queue;
        _pauses = pauses;
        _log = log;
        if (signal is not null)
            signal.AgentPauseChanged += NotifyAgentPauseChanged;
    }

    internal async Task<int> RetryWaitingItemsForTestAsync(
        string source,
        CancellationToken ct = default) =>
        await RetryWaitingItemsAsync(source, ct).ConfigureAwait(false);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RetryWaitingItemsAsync("startup", stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var signalled = await WaitForSignalOrTimerAsync(stoppingToken).ConfigureAwait(false);
                if (signalled)
                {
                    await RetryWaitingItemsAsync("agent-pause-signal", stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // ListPausedAsync lazily expires duration-based pauses. If that
                // expiry removes the final active pause, retry all waiting items.
                var stillPaused = await _pauses.ListPausedAsync(stoppingToken).ConfigureAwait(false);
                if (stillPaused.Count == 0)
                    await RetryWaitingItemsAsync("periodic-no-pauses", stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Agent pause retry sweep failed; will retry on next signal/tick");
            }
        }
    }

    private void NotifyAgentPauseChanged()
    {
        try
        {
            _wake.Release();
        }
        catch (ObjectDisposedException)
        {
            // Host is shutting down; parked items remain durable.
        }
    }

    private async Task<bool> WaitForSignalOrTimerAsync(CancellationToken ct)
    {
        var signal = _wake.WaitAsync(ct);
        var timer = Task.Delay(PeriodicSweepInterval, ct);
        var completed = await Task.WhenAny(signal, timer).ConfigureAwait(false);
        if (completed == signal)
        {
            await signal.ConfigureAwait(false);
            while (_wake.CurrentCount > 0 && _wake.Wait(0)) { }
            return true;
        }

        await timer.ConfigureAwait(false);
        return false;
    }

    private async Task<int> RetryWaitingItemsAsync(string source, CancellationToken ct)
    {
        var retried = 0;
        await foreach (var item in _store.ListByStateAsync(WorkItemState.WaitingForAgentResume, ct))
        {
            var retryFrom = NormaliseRetryFrom(item.QuotaRetryFrom);
            var resumeState = AgentPauseResumeMapper.ResumeStateForRetryFrom(retryFrom);
            var resumed = item.With(resumeState, error: null) with
            {
                FailureKind = null,
                QuotaResetAt = null,
                NextQuotaRetryAt = null,
                QuotaRetryFrom = null,
                StartedAt = null,
            };

            var updated = await _store.TryUpdateIfStateAsync(
                    resumed,
                    WorkItemState.WaitingForAgentResume,
                    ct)
                .ConfigureAwait(false);
            if (!updated)
            {
                _log.LogInformation(
                    "Agent pause retry skipped {Id}: state changed before retry",
                    item.Id);
                continue;
            }

            await _queue.EnqueueAsync(item.Id, ct).ConfigureAwait(false);
            AuditLog.AgentPauseWaitingItemResumed(item.Id, source, retryFrom);
            retried++;
        }

        return retried;
    }

    private static string NormaliseRetryFrom(string? retryFrom) =>
        retryFrom?.Trim().ToLowerInvariant() switch
        {
            "audit" => "audit",
            "merge" => "merge",
            "upstream" => "upstream",
            _ => "work",
        };
}
