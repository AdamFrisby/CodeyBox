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
    private readonly WorkItemRetrier _retrier;
    private readonly IAgentPauseController _pauses;
    private readonly ILogger<AgentPauseRetryScheduler> _log;
    private readonly SemaphoreSlim _wake = new(0);

    public AgentPauseRetryScheduler(
        IWorkItemStore store,
        WorkItemRetrier retrier,
        IAgentPauseController pauses,
        ILogger<AgentPauseRetryScheduler> log,
        IAgentPauseSignal? signal = null)
    {
        _store = store;
        _retrier = retrier;
        _pauses = pauses;
        _log = log;
        if (signal is not null)
            signal.AgentPauseChanged += NotifyAgentPauseChanged;
    }

    internal async Task<int> RetryWaitingItemsForTestAsync(
        string source,
        CancellationToken ct = default) =>
        await RetryWaitingItemsAsync(source, ct).ConfigureAwait(false);

    internal async Task<int> RunPeriodicExpirySweepForTestAsync(CancellationToken ct = default) =>
        await RunPeriodicExpirySweepAsync(ct).ConfigureAwait(false);

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

                await RunPeriodicExpirySweepAsync(stoppingToken).ConfigureAwait(false);
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
        using var signalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var signal = _wake.WaitAsync(signalCts.Token);
        var timer = Task.Delay(PeriodicSweepInterval, ct);
        var completed = await Task.WhenAny(signal, timer).ConfigureAwait(false);
        if (completed == signal)
        {
            await signal.ConfigureAwait(false);
            while (_wake.CurrentCount > 0 && _wake.Wait(0)) { }
            return true;
        }

        signalCts.Cancel();
        try
        {
            await signal.ConfigureAwait(false);
            while (_wake.CurrentCount > 0 && _wake.Wait(0)) { }
            return true;
        }
        catch (OperationCanceledException) when (signalCts.IsCancellationRequested)
        {
        }

        await timer.ConfigureAwait(false);
        return false;
    }

    private async Task<int> RetryWaitingItemsAsync(string source, CancellationToken ct)
    {
        var retried = 0;
        await foreach (var item in _store.ListByStateAsync(WorkItemState.WaitingForAgentResume, ct))
        {
            if (await IsResumeTargetStillPausedAsync(item, ct).ConfigureAwait(false))
            {
                _log.LogInformation(
                    "Agent pause retry skipped {Id}: paused target is still unavailable",
                    item.Id);
                continue;
            }

            var outcome = await _retrier.ResumeAfterAgentPauseAsync(item, source, ct)
                .ConfigureAwait(false);
            if (!outcome.Success)
            {
                _log.LogInformation(
                    "Agent pause retry skipped {Id}: {Reason}",
                    item.Id,
                    outcome.Error);
                continue;
            }

            retried++;
        }

        return retried;
    }

    private async Task<int> RunPeriodicExpirySweepAsync(CancellationToken ct)
    {
        // ListPausedAsync lazily expires duration-based pauses. RetryWaitingItemsAsync
        // then filters each row by its stamped target agent, so an expired claude
        // pause can requeue claude work even if a separate gemini pause remains.
        _ = await _pauses.ListPausedAsync(ct).ConfigureAwait(false);
        return await RetryWaitingItemsAsync("periodic-agent-pause-sweep", ct).ConfigureAwait(false);
    }

    private async Task<bool> IsResumeTargetStillPausedAsync(WorkItem item, CancellationToken ct)
    {
        if (item.AgentPauseTarget is { } target)
            return await _pauses.GetAgentStateAsync(target, ct).ConfigureAwait(false) is not null;

        if (item.Agent is { } legacyTarget)
            return await _pauses.GetAgentStateAsync(legacyTarget, ct).ConfigureAwait(false) is not null;

        // Older rows and class-routing rows with multiple paused eligible agents
        // do not carry a single target. Requeue them on pause-state changes and
        // let the router decide again; if every eligible agent is still paused,
        // pickup will park the item again with the current blocker set.
        return false;
    }
}
