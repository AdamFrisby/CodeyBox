using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Hosted service that automatically retries work items parked for transient
/// transport backoff.
/// </summary>
public sealed class TransientRetryScheduler : BackgroundService, IDisposable
{
    // Match QuotaRetryScheduler's hot-reload polling cadence while disabled.
    private static readonly TimeSpan OptionsReloadPollInterval = TimeSpan.FromSeconds(1);

    private readonly IWorkItemStore _store;
    private readonly WorkItemRetrier _retrier;
    private readonly IProjectRepository? _projects;
    private readonly IQueueController? _queueController;
    private readonly IWebhookDispatcher? _webhooks;
    private readonly IWorkItemTerminalTransition _terminalTransitions;
    private readonly OrchestratorOptions _opts;
    private readonly Func<AutoRetryOnTransientFailureOptions> _transientRetryOptionsAccessor;
    private readonly TimeProvider _time;
    private readonly Func<double> _jitterRandom;
    private readonly ILogger<TransientRetryScheduler> _log;
    private readonly ConcurrentDictionary<string, bool> _invalidIntervalWarnings = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<WorkItemId, ITimer> _targetedTimers = new();
    private int _disposed;

    private readonly record struct TransientRetryAttemptResult(string Outcome, string? Reason = null);

    private AutoRetryOnTransientFailureOptions CurrentTransientRetryOptions
    {
        get
        {
            try
            {
                return _transientRetryOptionsAccessor();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to read live transient auto-retry options; using startup options");
                return _opts.AutoRetryOnTransientFailure;
            }
        }
    }

    public TransientRetryScheduler(
        IWorkItemStore store,
        WorkItemRetrier retrier,
        OrchestratorOptions opts,
        ILogger<TransientRetryScheduler> log,
        IWorkItemTerminalTransition terminalTransitions,
        IProjectRepository? projects = null,
        IQueueController? queueController = null,
        IWebhookDispatcher? webhooks = null,
        TimeProvider? timeProvider = null,
        Func<AutoRetryOnTransientFailureOptions>? transientRetryOptionsAccessor = null,
        Func<double>? jitterRandom = null)
    {
        _store = store;
        _retrier = retrier;
        _opts = opts;
        _transientRetryOptionsAccessor = transientRetryOptionsAccessor ?? (() => _opts.AutoRetryOnTransientFailure);
        _log = log;
        _terminalTransitions = terminalTransitions;
        _projects = projects;
        _queueController = queueController;
        _webhooks = webhooks;
        _time = timeProvider ?? TimeProvider.System;
        _jitterRandom = jitterRandom ?? Random.Shared.NextDouble;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wasEnabled = false;
        var loggedDisabled = false;
        var lastSweepAt = _time.GetUtcNow();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var retryOptions = CurrentTransientRetryOptions;
                if (!retryOptions.Enabled)
                {
                    if (!loggedDisabled)
                    {
                        _log.LogInformation("Transient auto-retry is disabled");
                        loggedDisabled = true;
                    }

                    wasEnabled = false;
                    await Task.Delay(OptionsReloadPollInterval, stoppingToken);
                    continue;
                }

                loggedDisabled = false;
                if (!wasEnabled)
                {
                    _log.LogInformation(
                        "Transient auto-retry is enabled. Periodic interval: {Interval}, base={BaseDelay}, multiplier={Multiplier}, cap={MaxDelay}, maxAttempts={MaxAttempts}, maxElapsed={MaxElapsed}, jitter={Jitter}",
                        retryOptions.PeriodicCheckInterval,
                        retryOptions.BaseDelay,
                        retryOptions.Multiplier,
                        retryOptions.MaxDelay,
                        retryOptions.MaxAutoRetriesPerWorkItem,
                        retryOptions.MaxElapsedTime,
                        retryOptions.JitterMode);

                    await RearmTransientRetryTimersAsync(stoppingToken);
                    lastSweepAt = _time.GetUtcNow();
                    wasEnabled = true;
                }

                var now = _time.GetUtcNow();
                var interval = NormalizeInterval("Transient", retryOptions.PeriodicCheckInterval);
                var nextSweepAt = lastSweepAt + interval;
                if (now >= nextSweepAt)
                {
                    lastSweepAt = now;
                    await RunTransientPeriodicSweepAsync(stoppingToken);
                    continue;
                }

                var delay = nextSweepAt - now;
                if (delay < TimeSpan.Zero || delay > OptionsReloadPollInterval)
                    delay = OptionsReloadPollInterval;
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error during periodic transient retry sweep");
            }
        }
    }

    private TimeSpan NormalizeInterval(string label, TimeSpan interval)
    {
        if (interval > TimeSpan.Zero)
        {
            _invalidIntervalWarnings.TryRemove(label, out _);
            return interval;
        }

        if (_invalidIntervalWarnings.TryAdd(label, true))
        {
            _log.LogWarning(
                "{RetryKind} auto-retry periodic interval {Interval} is invalid; using {Fallback}",
                label,
                interval,
                OptionsReloadPollInterval);
        }
        return OptionsReloadPollInterval;
    }

    private async Task RearmTransientRetryTimersAsync(CancellationToken ct)
    {
        _log.LogInformation("Re-arming transient retry timers from database...");
        var count = 0;
        await foreach (var item in _store.ListByStateAsync(WorkItemState.WaitingForTransientRetry, ct))
        {
            if (IsTransientRetryPending(item) && await TryRearmTransientTimerAsync(item, ct))
                count++;
        }
        await foreach (var item in _store.ListByStateAsync(WorkItemState.Failed, ct))
        {
            if (IsTransientRetryPending(item) && await TryRearmTransientTimerAsync(item, ct))
                count++;
        }
        _log.LogInformation("Re-armed or re-evaluated {Count} transient retry item(s)", count);
    }

    private async Task<bool> TryRearmTransientTimerAsync(WorkItem item, CancellationToken ct)
    {
        try
        {
            await RearmTransientTimerAsync(item, ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error re-arming transient retry timer for work item {Id}; continuing startup sweep", item.Id);
            return false;
        }
    }

    private async Task RearmTransientTimerAsync(WorkItem item, CancellationToken ct)
    {
        if (item.NextTransientRetryAt is null)
        {
            await NotifyTransientFailureAsync(item, ct);
            return;
        }

        var nextRetryAt = item.NextTransientRetryAt.Value;
        var now = _time.GetUtcNow();
        var isOverdue = nextRetryAt < now;
        var delay = ScheduleTargetedRetry(item.Id, nextRetryAt);

        _log.LogInformation(
            "Re-armed transient retry timer for work item {Id}: state={State} nextRetryAt={NextRetryAt} delay={Delay} overdue={Overdue}",
            item.Id, item.State, nextRetryAt, delay, isOverdue);

        if (!isOverdue)
            return;

        var outcome = await TryTransientRetryAsync(item, "rearm-overdue", ct);
        _log.LogInformation(
            "Transient retry startup re-arm walked overdue work item {Id} in state {State}: outcome={Outcome} reason={Reason}",
            item.Id, item.State, outcome.Outcome, outcome.Reason);
    }

    private async Task RunTransientPeriodicSweepAsync(CancellationToken ct)
    {
        _log.LogDebug("Starting periodic transient retry sweep");
        await foreach (var item in _store.ListByStateAsync(WorkItemState.WaitingForTransientRetry, ct))
        {
            if (IsTransientRetryPending(item))
                await TryTransientPeriodicRetryAsync(item, ct);
        }
        await foreach (var item in _store.ListByStateAsync(WorkItemState.Failed, ct))
        {
            if (IsTransientRetryPending(item))
                await TryTransientPeriodicRetryAsync(item, ct);
        }
    }

    private async Task TryTransientPeriodicRetryAsync(WorkItem item, CancellationToken ct)
    {
        try
        {
            if (item.NextTransientRetryAt is null)
            {
                await NotifyTransientFailureAsync(item, ct);
                return;
            }

            if (item.NextTransientRetryAt > _time.GetUtcNow())
            {
                _log.LogDebug(
                    "Transient retry periodic sweep skipped work item {Id}: nextRetryAt={NextRetryAt}",
                    item.Id,
                    item.NextTransientRetryAt);
                return;
            }

            var outcome = await TryTransientRetryAsync(item, "periodic", ct);
            _log.LogInformation(
                "Transient retry periodic sweep walked work item {Id} in state {State}: outcome={Outcome} reason={Reason}",
                item.Id, item.State, outcome.Outcome, outcome.Reason);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error during periodic transient retry for work item {Id}; continuing sweep", item.Id);
        }
    }

    private TimeSpan ScheduleTargetedRetry(WorkItemId id, DateTimeOffset nextRetryAt)
    {
        var now = _time.GetUtcNow();
        var delay = nextRetryAt - now;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

        _targetedTimers.AddOrUpdate(id,
            _ => _time.CreateTimer(OnTargetedTimerFired, id, delay, Timeout.InfiniteTimeSpan),
            (_, old) =>
            {
                old.Dispose();
                return _time.CreateTimer(OnTargetedTimerFired, id, delay, Timeout.InfiniteTimeSpan);
            });

        return delay;
    }

    private void CancelTargetedRetry(WorkItemId id)
    {
        if (_targetedTimers.TryRemove(id, out var timer))
            timer.Dispose();
    }

    private void OnTargetedTimerFired(object? state)
    {
        if (state is not WorkItemId id) return;
        _targetedTimers.TryRemove(id, out _);

        _ = Task.Run(async () =>
        {
            try
            {
                var item = await _store.GetAsync(id, CancellationToken.None);
                if (item is not null && IsTransientRetryPending(item))
                    await TryTransientRetryAsync(item, "targeted", CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error during targeted transient auto-retry for work item {Id}", id);
            }
        });
    }

    private async Task<TransientRetryAttemptResult> TryTransientRetryAsync(
        WorkItem item,
        string source,
        CancellationToken ct)
    {
        try
        {
            var outcome = await TryTransientRetryCoreAsync(item, source, ct);
            AuditLog.TransientRetryAttempted(item.Id, source, outcome.Outcome, item.State.ToString(), outcome.Reason);
            return outcome;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AuditLog.TransientRetryAttempted(item.Id, source, "error", item.State.ToString(), ex.Message);
            throw;
        }
    }

    private async Task<TransientRetryAttemptResult> TryTransientRetryCoreAsync(
        WorkItem item,
        string trigger,
        CancellationToken ct)
    {
        var retryOptions = CurrentTransientRetryOptions;
        if (!retryOptions.Enabled)
        {
            _log.LogInformation("Transient auto-retry is disabled; skipping retry for work item {Id}", item.Id);
            return new TransientRetryAttemptResult("skipped:auto-retry-disabled");
        }

        var current = await _store.GetAsync(item.Id, ct);
        if (current is null)
            return new TransientRetryAttemptResult("skipped:not-found");

        item = current;

        if (!IsTransientRetryPending(item))
            return new TransientRetryAttemptResult("skipped:not-transient");

        if (item.NextTransientRetryAt is { } dueAt && dueAt > _time.GetUtcNow())
            return new TransientRetryAttemptResult("skipped:not-due", $"nextRetryAt={dueAt:O}");

        if (_queueController is not null)
        {
            if (_queueController.State == QueueState.Paused)
            {
                _log.LogInformation("Global queue is paused; skipping transient auto-retry for work item {Id}", item.Id);
                return new TransientRetryAttemptResult("skipped:global-queue-paused");
            }

            var projectState = await _queueController.GetProjectStateAsync(item.ProjectId, ct);
            if (projectState is { Paused: true })
            {
                _log.LogInformation("Project {ProjectId} queue is paused; skipping transient auto-retry for work item {Id}",
                    item.ProjectId, item.Id);
                return new TransientRetryAttemptResult("skipped:project-queue-paused", $"projectId={item.ProjectId.Value}");
            }
        }

        if (_projects is null)
        {
            _log.LogInformation("Project repository unavailable; skipping transient auto-retry for work item {Id}", item.Id);
            return new TransientRetryAttemptResult("skipped:project-repository-unavailable");
        }

        var project = await _projects.GetAsync(item.ProjectId, ct);
        if (project is null)
        {
            _log.LogInformation("Project {ProjectId} not found; skipping transient auto-retry for work item {Id}",
                item.ProjectId, item.Id);
            return new TransientRetryAttemptResult("skipped:project-not-found", $"projectId={item.ProjectId.Value}");
        }

        if (ShouldExhaustTransientRetry(item, retryOptions, _time.GetUtcNow(), out var capReason))
            return await TransitionTransientItemAtRetryCapAsync(item, capReason, ct, item.UpdatedAt);

        return await PerformTransientRetryAsync(item, trigger, ct);
    }

    private async Task<TransientRetryAttemptResult> TransitionTransientItemAtRetryCapAsync(
        WorkItem item,
        string reason,
        CancellationToken ct,
        DateTimeOffset? expectedUpdatedAt = null)
    {
        var current = await _store.GetAsync(item.Id, ct);
        if (current is not null)
        {
            if (!IsTransientRetryPending(current))
            {
                _log.LogInformation(
                    "Skipping transient retry exhaustion transition for work item {Id}; current state is {State} and failure kind is {FailureKind}",
                    item.Id,
                    current.State,
                    current.FailureKind);
                return new TransientRetryAttemptResult("skipped:not-transient", $"state={current.State}; failureKind={current.FailureKind}");
            }

            if (expectedUpdatedAt is { } expected && current.UpdatedAt != expected)
            {
                _log.LogInformation(
                    "Skipping transient retry exhaustion transition for work item {Id}; row changed after cap check",
                    item.Id);
                return new TransientRetryAttemptResult("skipped:state-changed", $"updatedAt={current.UpdatedAt:O}");
            }

            item = current with
            {
                TransientRetryFirstFailedAt = item.TransientRetryFirstFailedAt ?? current.TransientRetryFirstFailedAt,
            };
        }

        var transition = await _terminalTransitions.TransitionFailedAsync(
            item,
            $"transient network auto-retry exhausted ({reason}); operator retry required",
            new WorkItemTerminalFailureTransitionOptions
            {
                FailureKind = "transient-exhausted",
                ExpectedStates =
                [
                    WorkItemState.Failed,
                    WorkItemState.WaitingForTransientRetry,
                ],
                ExpectedUpdatedAt = expectedUpdatedAt,
                PrepareFailedItem = failed => failed with
                {
                    NextTransientRetryAt = null,
                    TransientRetryFirstFailedAt = item.TransientRetryFirstFailedAt ?? failed.TransientRetryFirstFailedAt,
                },
                DetailsFactory = failed => new
                {
                    workItemId = failed.Id.ToString(),
                    failureKind = failed.FailureKind,
                    reason,
                    transientRetryAttempts = failed.TransientRetryAttempts,
                },
                ResolveProjectWhenMissing = true,
                FallbackProjectWhenMissing = false,
                SwallowPublishExceptions = true,
            },
            ct);

        if (transition.Updated)
        {
            CancelTargetedRetry(item.Id);
            _log.LogWarning(
                "Work item {Id} exhausted transient auto-retry budget: {Reason}",
                item.Id,
                reason);
        }

        return new TransientRetryAttemptResult("skipped:max-retries", reason);
    }

    private async Task<TransientRetryAttemptResult> PerformTransientRetryAsync(
        WorkItem item,
        string trigger,
        CancellationToken ct)
    {
        _log.LogInformation(
            "Triggering transient auto-retry ({Trigger}) for work item {Id} (attempt {Attempt})",
            trigger,
            item.Id,
            item.TransientRetryAttempts + 1);
        var retryFrom = string.IsNullOrWhiteSpace(item.TransientRetryFrom)
            ? null
            : NormalizeRetryFrom(item.TransientRetryFrom);

        var (success, error, _, actualFrom, _) = await _retrier.RetryAsync(
            item,
            from: retryFrom,
            trigger: $"transient-{trigger}",
            autoRetryKind: WorkItemAutoRetryKind.Transient,
            ct: ct);

        if (!success)
        {
            _log.LogWarning("Failed to trigger transient auto-retry for work item {Id}: {Error}", item.Id, error);
            return new TransientRetryAttemptResult("retry-failed", error);
        }

        if (_webhooks is not null)
        {
            try
            {
                var project = _projects is null ? null : await _projects.GetAsync(item.ProjectId, CancellationToken.None);
                var updated = await _store.GetAsync(item.Id, CancellationToken.None);
                await _webhooks.PublishAsync(new WebhookEvent
                {
                    Event = "work_item.auto_retry",
                    WorkItem = updated ?? item,
                    Project = project,
                    Details = new
                    {
                        workItemId = item.Id.ToString(),
                        reason = "transient",
                        attemptNumber = updated?.TransientRetryAttempts ?? item.TransientRetryAttempts + 1,
                        triggeredBy = trigger,
                        from = retryFrom ?? "auto",
                        actualFrom
                    }
                }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Transient auto-retry for work item {Id} succeeded, but auto-retry webhook delivery failed",
                    item.Id);
            }
        }

        return new TransientRetryAttemptResult("retried", actualFrom is null ? null : $"actualFrom={actualFrom}");
    }

    private static string NormalizeRetryFrom(string? retryFrom) => retryFrom?.Trim().ToLowerInvariant() switch
    {
        "audit" => "audit",
        "conflict_rework" => "conflict_rework",
        "merge" => "merge",
        "upstream" => "upstream",
        _ => "work",
    };

    public async Task<WorkItemAutoRetryScheduleResult> NotifyTransientFailureAsync(WorkItem item, CancellationToken ct = default)
    {
        var retryOptions = CurrentTransientRetryOptions;
        if (!retryOptions.Enabled)
            return WorkItemAutoRetryScheduleResult.Skipped(item, "disabled");
        if (!IsTransientRetryPending(item))
            return WorkItemAutoRetryScheduleResult.Skipped(item, "not-transient");

        var now = _time.GetUtcNow();
        var current = await _store.GetAsync(item.Id, ct);
        if (current is null)
            return WorkItemAutoRetryScheduleResult.Skipped(item, "not-found");
        if (!IsTransientRetryPending(current))
            return WorkItemAutoRetryScheduleResult.Skipped(current, "state-changed");

        item = current;
        if (ShouldExhaustTransientRetry(item, retryOptions, now, out var capReason))
        {
            await TransitionTransientItemAtRetryCapAsync(item, capReason, ct, item.UpdatedAt);
            return await GetTransientScheduleResultAfterCapAsync(item, capReason, ct);
        }

        var firstFailedAt = item.TransientRetryFirstFailedAt ?? now;
        var delay = ComputeTransientRetryDelay(item.TransientRetryAttempts + 1, retryOptions);
        var nextRetryAt = now + delay;
        if (retryOptions.MaxElapsedTime > TimeSpan.Zero
            && nextRetryAt > firstFailedAt + retryOptions.MaxElapsedTime)
        {
            await TransitionTransientItemAtRetryCapAsync(
                item with { TransientRetryFirstFailedAt = firstFailedAt },
                $"elapsed would exceed max={retryOptions.MaxElapsedTime}",
                ct,
                item.UpdatedAt);
            return await GetTransientScheduleResultAfterCapAsync(
                item with { TransientRetryFirstFailedAt = firstFailedAt },
                $"elapsed would exceed max={retryOptions.MaxElapsedTime}",
                ct);
        }

        try
        {
            var scheduledItem = item with
            {
                NextTransientRetryAt = nextRetryAt,
                TransientRetryFirstFailedAt = firstFailedAt,
                UpdatedAt = now,
            };
            var updated = await _store.TryUpdateIfStateAndUpdatedAtAsync(
                scheduledItem,
                item.State,
                item.UpdatedAt,
                ct);
            if (updated)
            {
                ScheduleTargetedRetry(item.Id, nextRetryAt);
                _log.LogInformation(
                    "Scheduled transient auto-retry for work item {Id}: attempt={Attempt} nextRetryAt={NextRetryAt} delay={Delay}",
                    item.Id,
                    item.TransientRetryAttempts + 1,
                    nextRetryAt,
                    delay);
                return WorkItemAutoRetryScheduleResult.Scheduled(scheduledItem, nextRetryAt);
            }

            var latest = await _store.GetAsync(item.Id, ct) ?? item;
            return WorkItemAutoRetryScheduleResult.Skipped(latest, "state-changed");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Error updating NextTransientRetryAt for work item {Id}", item.Id);
            return WorkItemAutoRetryScheduleResult.Skipped(item, ex.Message);
        }
    }

    private async Task<WorkItemAutoRetryScheduleResult> GetTransientScheduleResultAfterCapAsync(
        WorkItem item,
        string reason,
        CancellationToken ct)
    {
        var current = await _store.GetAsync(item.Id, ct) ?? item;
        return current.State == WorkItemState.Failed
            && string.Equals(current.FailureKind, "transient-exhausted", StringComparison.OrdinalIgnoreCase)
            ? WorkItemAutoRetryScheduleResult.Exhausted(current, reason)
            : WorkItemAutoRetryScheduleResult.Skipped(current, reason);
    }

    private bool ShouldExhaustTransientRetry(
        WorkItem item,
        AutoRetryOnTransientFailureOptions retryOptions,
        DateTimeOffset now,
        out string reason)
    {
        if (item.TransientRetryAttempts >= retryOptions.MaxAutoRetriesPerWorkItem)
        {
            reason = $"attempts={item.TransientRetryAttempts}; max={retryOptions.MaxAutoRetriesPerWorkItem}";
            return true;
        }

        if (retryOptions.MaxElapsedTime > TimeSpan.Zero
            && item.TransientRetryFirstFailedAt is { } firstFailedAt
            && now - firstFailedAt >= retryOptions.MaxElapsedTime)
        {
            reason = $"elapsed={now - firstFailedAt}; max={retryOptions.MaxElapsedTime}";
            return true;
        }

        reason = "";
        return false;
    }

    private static bool IsTransientRetryPending(WorkItem item) =>
        string.Equals(item.FailureKind, "transient", StringComparison.OrdinalIgnoreCase)
        && item.State is WorkItemState.WaitingForTransientRetry or WorkItemState.Failed;

    private TimeSpan ComputeTransientRetryDelay(
        int attemptOrdinal,
        AutoRetryOnTransientFailureOptions retryOptions)
    {
        var exponential = ComputeExponentialDelay(attemptOrdinal, retryOptions);
        var random = Math.Clamp(_jitterRandom(), 0d, 1d);

        return retryOptions.JitterMode switch
        {
            TransientRetryJitterMode.None => exponential,
            TransientRetryJitterMode.Decorrelated => ComputeDecorrelatedDelay(attemptOrdinal, retryOptions, random),
            _ => TimeSpan.FromMilliseconds(exponential.TotalMilliseconds * random),
        };
    }

    private static TimeSpan ComputeExponentialDelay(
        int attemptOrdinal,
        AutoRetryOnTransientFailureOptions retryOptions)
    {
        if (attemptOrdinal <= 1 || retryOptions.Multiplier <= 1.0)
            return retryOptions.BaseDelay <= retryOptions.MaxDelay ? retryOptions.BaseDelay : retryOptions.MaxDelay;

        var delayMs = retryOptions.BaseDelay.TotalMilliseconds;
        var maxMs = retryOptions.MaxDelay.TotalMilliseconds;
        for (var i = 1; i < attemptOrdinal; i++)
        {
            delayMs *= retryOptions.Multiplier;
            if (double.IsInfinity(delayMs) || delayMs >= maxMs)
                return retryOptions.MaxDelay;
        }

        return TimeSpan.FromMilliseconds(Math.Min(delayMs, maxMs));
    }

    private static TimeSpan ComputeDecorrelatedDelay(
        int attemptOrdinal,
        AutoRetryOnTransientFailureOptions retryOptions,
        double random)
    {
        var previous = attemptOrdinal <= 1
            ? retryOptions.BaseDelay
            : ComputeExponentialDelay(attemptOrdinal - 1, retryOptions);
        var lowerMs = retryOptions.BaseDelay.TotalMilliseconds;
        var upperMs = Math.Min(retryOptions.MaxDelay.TotalMilliseconds, previous.TotalMilliseconds * 3.0);
        if (upperMs <= lowerMs)
            return retryOptions.BaseDelay <= retryOptions.MaxDelay ? retryOptions.BaseDelay : retryOptions.MaxDelay;
        return TimeSpan.FromMilliseconds(lowerMs + ((upperMs - lowerMs) * random));
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        foreach (var timer in _targetedTimers.Values)
        {
            timer.Dispose();
        }
        _targetedTimers.Clear();
        base.Dispose();
    }
}
