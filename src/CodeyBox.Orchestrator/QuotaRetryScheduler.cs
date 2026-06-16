using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Hosted service that automatically retries work items parked for quota reset
/// or transient transport backoff.
/// </summary>
public sealed class QuotaRetryScheduler : BackgroundService, IDisposable, IWorkerPoolQuotaRecovery, IWorkItemAutoRetryScheduler
{
    // There is no provider-agnostic options-change callback on this class: the
    // live options enter through an accessor. While disabled, poll that
    // accessor at a small named interval so hot-enabling does not require a
    // process restart or an unrelated router wake-up.
    private static readonly TimeSpan OptionsReloadPollInterval = TimeSpan.FromSeconds(1);

    private readonly IWorkItemStore _store;
    private readonly WorkItemRetrier _retrier;
    private readonly IQuotaRetryRouter? _router;
    private readonly IProjectRepository? _projects;
    private readonly IQueueController? _queueController;
    private readonly IWebhookDispatcher? _webhooks;
    private readonly IWorkItemTerminalTransition _terminalTransitions;
    private readonly OrchestratorOptions _opts;
    private readonly Func<AutoRetryOnQuotaFailureOptions> _autoRetryOptionsAccessor;
    private readonly Func<AutoRetryOnTransientFailureOptions> _transientRetryOptionsAccessor;
    private readonly IAgentQuotaAvailabilitySignal? _quotaAvailabilitySignal;
    private readonly TimeProvider _time;
    private readonly IBaselineImageResolver _baselineResolver;
    private readonly Func<double> _jitterRandom;
    private readonly ILogger<QuotaRetryScheduler> _log;
    private readonly CancellationTokenSource _quotaUsableSweepCts = new();
    private readonly object _quotaUsableSweepLock = new();
    private readonly ConcurrentDictionary<string, bool> _invalidIntervalWarnings = new(StringComparer.Ordinal);
    private Task? _quotaUsableSweepTask;
    private int _quotaUsableSweepScheduled;
    private int _disposed;

    // Active timers for targeted wakeups. Key = WorkItemId.
    private readonly ConcurrentDictionary<WorkItemId, ITimer> _targetedTimers = new();
    private readonly record struct QuotaRetryAttemptResult(string Outcome, string? Reason = null);
    private readonly record struct TransientRetryAttemptResult(string Outcome, string? Reason = null);
    private AutoRetryOnQuotaFailureOptions CurrentRetryOptions
    {
        get
        {
            try
            {
                return _autoRetryOptionsAccessor();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to read live quota auto-retry options; using startup options");
                return _opts.AutoRetryOnQuotaFailure;
            }
        }
    }

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

    public QuotaRetryScheduler(
        IWorkItemStore store,
        WorkItemRetrier retrier,
        OrchestratorOptions opts,
        ILogger<QuotaRetryScheduler> log,
        IQuotaRetryRouter? router = null,
        IProjectRepository? projects = null,
        IQueueController? queueController = null,
        IWebhookDispatcher? webhooks = null,
        TimeProvider? timeProvider = null,
        IBaselineImageResolver? baselineResolver = null,
        Func<AutoRetryOnQuotaFailureOptions>? autoRetryOptionsAccessor = null,
        IAgentQuotaAvailabilitySignal? quotaAvailabilitySignal = null,
        Func<AutoRetryOnTransientFailureOptions>? transientRetryOptionsAccessor = null,
        Func<double>? jitterRandom = null,
        IWorkItemTerminalTransition? terminalTransitions = null)
    {
        _store = store;
        _retrier = retrier;
        _opts = opts;
        _autoRetryOptionsAccessor = autoRetryOptionsAccessor ?? (() => _opts.AutoRetryOnQuotaFailure);
        _transientRetryOptionsAccessor = transientRetryOptionsAccessor ?? (() => _opts.AutoRetryOnTransientFailure);
        _log = log;
        _router = router;
        _projects = projects;
        _queueController = queueController;
        _webhooks = webhooks;
        _terminalTransitions = terminalTransitions
            ?? new WorkItemTerminalTransition(
                store,
                webhooks,
                projects,
                NullLogger<WorkItemTerminalTransition>.Instance);
        _time = timeProvider ?? TimeProvider.System;
        _baselineResolver = baselineResolver ?? NullBaselineImageResolver.Instance;
        _jitterRandom = jitterRandom ?? Random.Shared.NextDouble;
        _quotaAvailabilitySignal = quotaAvailabilitySignal;
        if (_quotaAvailabilitySignal is not null)
            _quotaAvailabilitySignal.QuotaUsableThresholdCrossed += OnQuotaUsableThresholdCrossed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wasQuotaEnabled = false;
        var wasTransientEnabled = false;
        var loggedDisabled = false;
        var lastQuotaSweepAt = _time.GetUtcNow();
        var lastTransientSweepAt = lastQuotaSweepAt;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var retryOptions = CurrentRetryOptions;
                var transientOptions = CurrentTransientRetryOptions;
                if (!retryOptions.Enabled && !transientOptions.Enabled)
                {
                    if (!loggedDisabled)
                    {
                        _log.LogInformation("Quota and transient auto-retry are disabled");
                        loggedDisabled = true;
                    }

                    wasQuotaEnabled = false;
                    wasTransientEnabled = false;
                    await Task.Delay(OptionsReloadPollInterval, stoppingToken);
                    continue;
                }

                loggedDisabled = false;
                if (retryOptions.Enabled && !wasQuotaEnabled)
                {
                    _log.LogInformation("Quota auto-retry is enabled. Periodic interval: {Interval}, Drift margin: {Margin}",
                        retryOptions.PeriodicCheckInterval,
                        retryOptions.ClockDriftSafetyMargin);

                    // Re-arm timers and re-evaluate parked items each time the
                    // feature transitions from disabled to enabled.
                    await RearmTimersAsync(stoppingToken);
                    lastQuotaSweepAt = _time.GetUtcNow();
                    wasQuotaEnabled = true;
                }
                else if (!retryOptions.Enabled)
                {
                    wasQuotaEnabled = false;
                }

                if (transientOptions.Enabled && !wasTransientEnabled)
                {
                    _log.LogInformation(
                        "Transient auto-retry is enabled. Periodic interval: {Interval}, base={BaseDelay}, multiplier={Multiplier}, cap={MaxDelay}, maxAttempts={MaxAttempts}, maxElapsed={MaxElapsed}, jitter={Jitter}",
                        transientOptions.PeriodicCheckInterval,
                        transientOptions.BaseDelay,
                        transientOptions.Multiplier,
                        transientOptions.MaxDelay,
                        transientOptions.MaxAutoRetriesPerWorkItem,
                        transientOptions.MaxElapsedTime,
                        transientOptions.JitterMode);

                    await RearmTransientRetryTimersAsync(stoppingToken);
                    lastTransientSweepAt = _time.GetUtcNow();
                    wasTransientEnabled = true;
                }
                else if (!transientOptions.Enabled)
                {
                    wasTransientEnabled = false;
                }

                var now = _time.GetUtcNow();
                var quotaInterval = NormalizeInterval("Quota", retryOptions.PeriodicCheckInterval);
                var transientInterval = NormalizeInterval("Transient", transientOptions.PeriodicCheckInterval);
                var nextQuotaSweepAt = retryOptions.Enabled
                    ? lastQuotaSweepAt + quotaInterval
                    : DateTimeOffset.MaxValue;
                var nextTransientSweepAt = transientOptions.Enabled
                    ? lastTransientSweepAt + transientInterval
                    : DateTimeOffset.MaxValue;

                var ranSweep = false;
                if (retryOptions.Enabled && now >= nextQuotaSweepAt)
                {
                    lastQuotaSweepAt = now;
                    await RunPeriodicSweepAsync(stoppingToken);
                    ranSweep = true;
                }

                if (transientOptions.Enabled && now >= nextTransientSweepAt)
                {
                    lastTransientSweepAt = now;
                    await RunTransientPeriodicSweepAsync(stoppingToken);
                    ranSweep = true;
                }

                if (ranSweep)
                    continue;

                var nextSweepAt = nextQuotaSweepAt < nextTransientSweepAt
                    ? nextQuotaSweepAt
                    : nextTransientSweepAt;
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
                _log.LogError(ex, "Error during periodic quota retry sweep");
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

    private async Task RearmTimersAsync(CancellationToken ct)
    {
        _log.LogInformation("Re-arming quota retry timers from database...");
        var count = 0;
        await foreach (var item in _store.ListByStateAsync(WorkItemState.Failed, ct))
        {
            if (item.FailureKind == "quota" && item.NextQuotaRetryAt.HasValue)
            {
                if (await TryRearmTimerAsync(item, ct))
                    count++;
            }
        }
        await foreach (var item in _store.ListByStateAsync(WorkItemState.WaitingForQuotaReset, ct))
        {
            if (await TryStartupRequeueWaitingItemAsync(item, ct))
                count++;
        }
        _log.LogInformation("Re-armed or re-evaluated {Count} quota retry item(s)", count);
    }

    private async Task RearmTransientRetryTimersAsync(CancellationToken ct)
    {
        _log.LogInformation("Re-arming transient retry timers from database...");
        var count = 0;
        await foreach (var item in _store.ListByStateAsync(WorkItemState.WaitingForTransientRetry, ct))
        {
            if (IsTransientRetryPending(item))
            {
                if (await TryRearmTransientTimerAsync(item, ct))
                    count++;
            }
        }
        await foreach (var item in _store.ListByStateAsync(WorkItemState.Failed, ct))
        {
            if (!IsTransientRetryPending(item))
                continue;
            if (await TryRearmTransientTimerAsync(item, ct))
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

    private async Task<bool> TryRearmTimerAsync(WorkItem item, CancellationToken ct)
    {
        try
        {
            await RearmTimerAsync(item, ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error re-arming quota retry timer for work item {Id}; continuing startup sweep", item.Id);
            return false;
        }
    }

    private async Task RearmTimerAsync(WorkItem item, CancellationToken ct)
    {
        var nextRetryAt = item.NextQuotaRetryAt!.Value;
        var now = _time.GetUtcNow();
        var isOverdue = nextRetryAt < now;
        var delay = ScheduleTargetedRetry(item.Id, nextRetryAt);

        _log.LogInformation(
            "Re-armed quota retry timer for work item {Id}: state={State} nextRetryAt={NextRetryAt} delay={Delay} overdue={Overdue}",
            item.Id, item.State, nextRetryAt, delay, isOverdue);

        if (!isOverdue)
            return;

        var outcome = await TryRetryAsync(item, "rearm-overdue", ct);
        _log.LogInformation(
            "Quota retry startup re-arm walked overdue work item {Id} in state {State}: outcome={Outcome} reason={Reason}",
            item.Id, item.State, outcome.Outcome, outcome.Reason);
    }

    private async Task<bool> TryStartupRequeueWaitingItemAsync(WorkItem item, CancellationToken ct)
    {
        try
        {
            await StartupRequeueWaitingItemAsync(item, ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error re-queueing quota-waiting work item {Id}; continuing startup sweep", item.Id);
            return false;
        }
    }

    private async Task StartupRequeueWaitingItemAsync(WorkItem item, CancellationToken ct)
    {
        var outcome = await TryStartupRequeueAsync(item, ct);
        var action = outcome.Outcome switch
        {
            "retried" => "re-queued",
            "retry-failed" => "left-waiting",
            "skipped:max-retries" => "max-retries",
            _ when outcome.Outcome.StartsWith("skipped:", StringComparison.Ordinal) => "skipped",
            _ => "evaluated",
        };
        _log.LogInformation(
            "Quota retry startup sweep evaluated work item {Id} in state {State}: action={Action} outcome={Outcome} reason={Reason}",
            item.Id, item.State, action, outcome.Outcome, outcome.Reason);

        if (outcome.Outcome == "retried")
            CancelTargetedRetry(item.Id);
    }

    private async Task<QuotaRetryAttemptResult> TryStartupRequeueAsync(WorkItem item, CancellationToken ct)
    {
        try
        {
            var retryOptions = CurrentRetryOptions;
            QuotaRetryAttemptResult outcome;
            if (!retryOptions.Enabled)
            {
                _log.LogInformation("Quota auto-retry is disabled; skipping startup retry for work item {Id}", item.Id);
                outcome = new QuotaRetryAttemptResult("skipped:auto-retry-disabled");
            }
            else
            {
                // Startup is the escape hatch for persisted WaitingForQuotaReset rows:
                // every parked item is put back on the queue so dispatch evaluates
                // current agent availability from scratch, even when the row is
                // already at the periodic auto-retry cap. The router preflight is
                // advisory: it records the short-lived quota-retry admission that
                // lets the subsequent dispatch bypass stale local quota suppression,
                // but it does not block the unconditional startup requeue.
                item = await PrepareStartupQuotaRetryAdmissionAsync(item, ct);
                // Preserve the saved phase rather than forcing from=work.
                outcome = await PerformRetryAsync(item, "startup", ct);
            }

            AuditLog.QuotaRetryAttempted(item.Id, "startup", outcome.Outcome, item.State.ToString(), outcome.Reason);
            return outcome;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AuditLog.QuotaRetryAttempted(item.Id, "startup", "error", item.State.ToString(), ex.Message);
            throw;
        }
    }

    private async Task<WorkItem> PrepareStartupQuotaRetryAdmissionAsync(WorkItem item, CancellationToken ct)
    {
        if (_router is null || _projects is null)
            return item;

        var project = await _projects.GetAsync(item.ProjectId, ct);
        if (project is null)
            return item;

        if (item.BaselineImageRef is null)
        {
            var pinnedRef = ResolveBaselineRefForRetry(item, project);
            if (pinnedRef is not null)
                item = item with { BaselineImageRef = pinnedRef };
        }

        var decision = await _router.ResolveQuotaRetryAsync(
            item,
            project,
            ct,
            RequiredCapabilityForRetry(item));
        _log.LogDebug(
            "Quota retry startup preflight for work item {Id}: shouldWait={ShouldWait} noEligible={NoEligible} reason={Reason}",
            item.Id,
            decision.ShouldWait,
            decision.NoEligibleMembers,
            decision.Reason);
        return item;
    }

    // The periodic sweep is the safety net: it walks every Failed/quota item
    // and asks the router whether it could run now, ignoring NextQuotaRetryAt
    // entirely. NextQuotaRetryAt is an *optimisation* (drives the targeted
    // timer), not a "don't even try" gate — probe caches can be stale, the
    // park-time estimate can be wrong, and class members can refill earlier
    // than predicted. Keeping the sweep router-driven means we recover even
    // when those estimates miss.
    private async Task RunPeriodicSweepAsync(CancellationToken ct)
    {
        _log.LogDebug("Starting periodic quota retry sweep");
        await foreach (var item in _store.ListByStateAsync(WorkItemState.Failed, ct))
        {
            if (item.FailureKind == "quota")
            {
                await TryPeriodicRetryAsync(item, ct);
            }
        }
        await foreach (var item in _store.ListByStateAsync(WorkItemState.WaitingForQuotaReset, ct))
        {
            await TryPeriodicRetryAsync(item, ct);
        }
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

    public async Task RunWatchdogRecoverySweepAsync(CancellationToken ct)
    {
        _log.LogWarning("Worker-pool health watchdog triggered quota retry recovery sweep");
        await foreach (var item in _store.ListByStateAsync(WorkItemState.WaitingForQuotaReset, ct))
        {
            await TryWatchdogRecoveryRetryAsync(item, ct);
        }
    }

    private async Task TryPeriodicRetryAsync(WorkItem item, CancellationToken ct)
    {
        try
        {
            var outcome = await TryRetryAsync(item, "periodic", ct);
            _log.LogInformation(
                "Quota retry periodic sweep walked work item {Id} in state {State}: outcome={Outcome} reason={Reason}",
                item.Id, item.State, outcome.Outcome, outcome.Reason);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error during periodic quota retry for work item {Id}; continuing sweep", item.Id);
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

    private async Task TryWatchdogRecoveryRetryAsync(WorkItem item, CancellationToken ct)
    {
        try
        {
            var outcome = await TryRetryWithAutoRetryGateAsync(
                item,
                "watchdog",
                ct,
                requireAutoRetryEnabled: false);
            _log.LogInformation(
                "Quota retry watchdog recovery walked work item {Id} in state {State}: outcome={Outcome} reason={Reason}",
                item.Id, item.State, outcome.Outcome, outcome.Reason);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error during watchdog quota retry recovery for work item {Id}; continuing sweep", item.Id);
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

    private void OnQuotaUsableThresholdCrossed()
    {
        if (!CurrentRetryOptions.Enabled)
            return;
        if (Volatile.Read(ref _disposed) == 1)
            return;
        if (_quotaUsableSweepCts.IsCancellationRequested)
            return;
        if (Interlocked.Exchange(ref _quotaUsableSweepScheduled, 1) == 1)
            return;

        var task = Task.Run(() => RunQuotaUsableWakeUpSweepAsync(_quotaUsableSweepCts.Token));
        lock (_quotaUsableSweepLock)
        {
            _quotaUsableSweepTask = task;
        }
    }

    private async Task RunQuotaUsableWakeUpSweepAsync(CancellationToken ct)
    {
        try
        {
            await RunPeriodicSweepAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error during quota-usable wake-up sweep");
        }
        finally
        {
            Interlocked.Exchange(ref _quotaUsableSweepScheduled, 0);
        }
    }

    private void OnTargetedTimerFired(object? state)
    {
        if (state is not WorkItemId id) return;
        _targetedTimers.TryRemove(id, out _);

        // Run the retry logic in a background task to avoid blocking the timer thread.
        _ = Task.Run(async () =>
        {
            try
            {
                var item = await _store.GetAsync(id, CancellationToken.None);
                if (item is { State: WorkItemState.Failed, FailureKind: "quota" }
                    || item is { State: WorkItemState.WaitingForQuotaReset })
                {
                    await TryRetryAsync(item, "targeted", CancellationToken.None);
                }
                else if (item is not null && IsTransientRetryPending(item))
                {
                    await TryTransientRetryAsync(item, "targeted", CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error during targeted auto-retry for work item {Id}", id);
            }
        });
    }

    private Task<QuotaRetryAttemptResult> TryRetryAsync(
        WorkItem item,
        string source,
        CancellationToken ct)
        => TryRetryWithAutoRetryGateAsync(
            item,
            source,
            ct,
            requireAutoRetryEnabled: true);

    private async Task<QuotaRetryAttemptResult> TryRetryWithAutoRetryGateAsync(
        WorkItem item,
        string source,
        CancellationToken ct,
        bool requireAutoRetryEnabled)
    {
        try
        {
            var outcome = await TryRetryCoreAsync(item, source, ct, requireAutoRetryEnabled);
            AuditLog.QuotaRetryAttempted(item.Id, source, outcome.Outcome, item.State.ToString(), outcome.Reason);
            return outcome;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AuditLog.QuotaRetryAttempted(item.Id, source, "error", item.State.ToString(), ex.Message);
            throw;
        }
    }

    private async Task<QuotaRetryAttemptResult> TryRetryCoreAsync(
        WorkItem item,
        string trigger,
        CancellationToken ct,
        bool requireAutoRetryEnabled)
    {
        var retryOptions = CurrentRetryOptions;
        if (requireAutoRetryEnabled && !retryOptions.Enabled)
        {
            _log.LogInformation("Quota auto-retry is disabled; skipping retry for work item {Id}", item.Id);
            return new QuotaRetryAttemptResult("skipped:auto-retry-disabled");
        }

        // 1. Check if queue is paused.
        if (_queueController is not null)
        {
            if (_queueController.State == QueueState.Paused)
            {
                _log.LogInformation("Global queue is paused; skipping auto-retry for work item {Id}", item.Id);
                return new QuotaRetryAttemptResult("skipped:global-queue-paused");
            }

            var projectState = await _queueController.GetProjectStateAsync(item.ProjectId, ct);
            if (projectState is { Paused: true })
            {
                _log.LogInformation("Project {ProjectId} queue is paused; skipping auto-retry for work item {Id}",
                    item.ProjectId, item.Id);
                return new QuotaRetryAttemptResult("skipped:project-queue-paused", $"projectId={item.ProjectId.Value}");
            }
        }

        // 2. Resolve project.
        if (_projects is null)
        {
            _log.LogInformation("Project repository unavailable; skipping auto-retry for work item {Id}", item.Id);
            return new QuotaRetryAttemptResult("skipped:project-repository-unavailable");
        }
        var project = await _projects.GetAsync(item.ProjectId, ct);
        if (project is null)
        {
            _log.LogInformation("Project {ProjectId} not found; skipping auto-retry for work item {Id}",
                item.ProjectId, item.Id);
            return new QuotaRetryAttemptResult("skipped:project-not-found", $"projectId={item.ProjectId.Value}");
        }

        // 3. Ask the quota gate.
        if (_router is null)
        {
            _log.LogInformation("Quota router unavailable; skipping auto-retry for work item {Id}", item.Id);
            return new QuotaRetryAttemptResult("skipped:router-unavailable");
        }
        // Pin the work/headless baseline ref before the router gates, mirroring
        // the dispatch pickup path: the in-VM smoke gate must probe the image a
        // matching work-profile retry will clone, not the active baseline.
        // Retried items are normally already stamped from their first pickup;
        // this only fills a null ref (e.g. an item that never ran) so the gate
        // never probes/caches a matching work target under the wrong image.
        if (item.BaselineImageRef is null)
        {
            var pinnedRef = ResolveBaselineRefForRetry(item, project);
            if (pinnedRef is not null)
                item = item with { BaselineImageRef = pinnedRef };
        }

        var decision = await _router.ResolveQuotaRetryAsync(
            item,
            project,
            ct,
            RequiredCapabilityForRetry(item));
        if (decision.ShouldWait)
        {
            if (decision.WaitingForPausedAgent)
                return await TransitionWaitingItemForAgentResumeAsync(item, decision.Reason, ct);

            _log.LogDebug("Work item {Id} still gated by quota; decision: {Reason}", item.Id, decision.Reason);
            return new QuotaRetryAttemptResult("skipped:quota-still-gated", decision.Reason);
        }
        if (decision.NoEligibleMembers)
        {
            _log.LogInformation("Work item {Id} has no eligible class members; skipping auto-retry", item.Id);
            return new QuotaRetryAttemptResult("skipped:no-eligible-members", decision.Reason);
        }

        // 4. Enforce the max retry cap only after quota re-evaluation. A
        // WaitingForQuotaReset row at the cap must not remain parked once an
        // eligible member is usable; move it to an operator-visible state.
        if (item.QuotaRetryAttempts >= retryOptions.MaxAutoRetriesPerWorkItem)
        {
            _log.LogInformation("Work item {Id} reached max quota auto-retries ({Max}); skipping",
                item.Id, retryOptions.MaxAutoRetriesPerWorkItem);

            if (item.State == WorkItemState.WaitingForQuotaReset)
                return await TransitionWaitingItemAtRetryCapAsync(item, retryOptions, ct);

            return new QuotaRetryAttemptResult("skipped:max-retries",
                $"attempts={item.QuotaRetryAttempts}; max={retryOptions.MaxAutoRetriesPerWorkItem}");
        }

        // 5. Trigger retry.
        return await PerformRetryAsync(item, trigger, ct);
    }

    private async Task<QuotaRetryAttemptResult> TransitionWaitingItemForAgentResumeAsync(
        WorkItem item,
        string? reason,
        CancellationToken ct)
    {
        var pausedReason = string.IsNullOrWhiteSpace(reason)
            ? "agent paused by operator"
            : reason.Trim();
        Project? project = null;
        if (_projects is not null)
        {
            try
            {
                project = await _projects.GetAsync(item.ProjectId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Could not load project for agent-pause transition of quota retry item {Id}",
                    item.Id);
            }
        }

        var result = await WorkItemAgentPauseParking.ParkAsync(
            _store,
            _webhooks,
            _log,
            item,
            pausedReason,
            project,
            pausedAgent: null,
            ct,
            retryFrom: NormalizeRetryFrom(item.QuotaRetryFrom));
        if (!result.Updated)
        {
            return new QuotaRetryAttemptResult("skipped:state-changed", result.Reason);
        }

        return new QuotaRetryAttemptResult("moved:waiting-for-agent-resume", result.Reason);
    }

    private async Task<QuotaRetryAttemptResult> TransitionWaitingItemAtRetryCapAsync(
        WorkItem item,
        AutoRetryOnQuotaFailureOptions retryOptions,
        CancellationToken ct)
    {
        var reason = $"attempts={item.QuotaRetryAttempts}; max={retryOptions.MaxAutoRetriesPerWorkItem}";
        var failed = item.With(
            WorkItemState.Failed,
            $"quota auto-retry reached max attempts ({retryOptions.MaxAutoRetriesPerWorkItem}); operator retry required",
            failureKind: "quota",
            quotaResetAt: item.QuotaResetAt) with
        {
            NextQuotaRetryAt = null,
        };

        var updated = await _store.TryUpdateIfStateAsync(failed, WorkItemState.WaitingForQuotaReset, ct);
        if (updated)
        {
            CancelTargetedRetry(item.Id);
            _log.LogWarning(
                "Work item {Id} left WaitingForQuotaReset after reaching max quota auto-retries ({Max}) with usable quota available",
                item.Id,
                retryOptions.MaxAutoRetriesPerWorkItem);
        }
        else
        {
            _log.LogInformation(
                "Work item {Id} reached max quota auto-retries but state changed before it could leave WaitingForQuotaReset",
                item.Id);
        }

        return new QuotaRetryAttemptResult("skipped:max-retries", reason);
    }

    /// <summary>
    /// Resolves the baseline image ref this item would be cloned from at pickup,
    /// using the project's work-phase network profile (mirrors
    /// <c>OrchestratorService.ResolveBaselineRefForPickup</c>). Returns null when
    /// no resolver is wired or the resolver cannot pin a ref.
    /// </summary>
    private string? ResolveBaselineRefForRetry(WorkItem item, Project project)
    {
        try
        {
            return _baselineResolver.ResolveBaselineRef(
                project.NetworkProfiles.Work, SandboxProfileFlavor.Headless);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Baseline-ref resolver threw for work item {Id}; proceeding without pin", item.Id);
            return null;
        }
    }

    private async Task<QuotaRetryAttemptResult> PerformRetryAsync(
        WorkItem item,
        string trigger,
        CancellationToken ct)
    {
        var retryFrom = NormalizeRetryFrom(item.QuotaRetryFrom);
        _log.LogInformation("Triggering quota auto-retry ({Trigger}) for work item {Id} (attempt {Attempt})",
            trigger, item.Id, item.QuotaRetryAttempts + 1);

        // Re-use logic from shared WorkItemRetrier to ensure identical side effects,
        // audit logs, and conditional state updates (prevents race conditions).
        var (success, error, _, actualFrom, _) = await _retrier.RetryAsync(
            item,
            from: retryFrom,
            trigger: trigger,
            autoRetryKind: WorkItemAutoRetryKind.Quota,
            ct: ct);

        if (!success)
        {
            _log.LogWarning("Failed to trigger quota auto-retry for work item {Id}: {Error}", item.Id, error);
            return new QuotaRetryAttemptResult("retry-failed", error);
        }

        if (_webhooks is not null)
        {
            try
            {
                var project = await _projects!.GetAsync(item.ProjectId, CancellationToken.None);
                var updated = await _store.GetAsync(item.Id, CancellationToken.None);
                await _webhooks.PublishAsync(new WebhookEvent
                {
                    Event = "work_item.auto_retry",
                    WorkItem = updated ?? item,
                    Project = project,
                    Details = new
                    {
                        workItemId = item.Id.ToString(),
                        reason = "quota",
                        attemptNumber = (updated?.QuotaRetryAttempts ?? item.QuotaRetryAttempts + 1),
                        triggeredBy = trigger,
                        from = retryFrom,
                        actualFrom
                    }
                }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Quota auto-retry for work item {Id} succeeded, but auto-retry webhook delivery failed",
                    item.Id);
            }
        }

        return new QuotaRetryAttemptResult("retried", actualFrom == retryFrom ? $"from={retryFrom}" : $"from={retryFrom}; actualFrom={actualFrom}");
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

        // Respect operator queue pauses before consuming the retry budget.
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

    private static string? RequiredCapabilityForRetry(WorkItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.QuotaRetryPhase))
            return RequiredCapabilityForPhase(item.QuotaRetryPhase);

        return NormalizeRetryFrom(item.QuotaRetryFrom) == "audit"
            ? WellKnownCapabilities.Audit
            : null;
    }

    private static string? RequiredCapabilityForPhase(string? phase) =>
        string.Equals(phase?.Trim(), "audit", StringComparison.OrdinalIgnoreCase)
            ? WellKnownCapabilities.Audit
            : null;

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
                UpdatedAt = DateTimeOffset.UtcNow,
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
        item.FailureKind == "transient"
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

    /// <summary>
    /// Notifies the scheduler that a work item has failed with a quota error,
    /// so it can schedule a targeted retry.
    /// </summary>
    public async Task NotifyQuotaFailureAsync(WorkItem item, CancellationToken ct = default)
    {
        var retryOptions = CurrentRetryOptions;
        if (!retryOptions.Enabled) return;
        var isQuotaFailed = item.State == WorkItemState.Failed && item.FailureKind == "quota";
        var isWaitingReset = item.State == WorkItemState.WaitingForQuotaReset;
        if (!isQuotaFailed && !isWaitingReset) return;

        // Park time should be the soonest any class member can plausibly become
        // eligible — MIN(resetAt) across exhausted members — not the last-tried
        // agent's reset (which is often the latest, e.g. gemini's per-model
        // daily window of 21–24h vs claude's 5h rolling cap).
        DateTimeOffset? resetAt = null;
        if (_router is not null && _projects is not null)
        {
            try
            {
                var project = await _projects.GetAsync(item.ProjectId, ct);
                resetAt = await _router.ComputeEarliestExhaustedResetAsync(
                    item,
                    project,
                    ct,
                    RequiredCapabilityForRetry(item));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Failed to compute earliest class-member reset for {Id}; falling back to failing-agent reset", item.Id);
            }
        }

        // Always consider the failing agent's own reset: probe caches may be
        // stale, and the failure provides fresh information the probe doesn't
        // have yet. Take the earliest across both sources.
        if (item.QuotaResetAt is { } failingReset
            && (resetAt is null || failingReset < resetAt.Value))
        {
            resetAt = failingReset;
        }

        // Fall back to a pipeline-supplied NextQuotaRetryAt if neither
        // earliest-member-reset nor failing-agent reset is available (covers the
        // WaitingForQuotaReset path where the pipeline already computed a wake time).
        DateTimeOffset? nextRetryAt = resetAt.HasValue
            ? resetAt.Value.Add(retryOptions.ClockDriftSafetyMargin)
            : item.NextQuotaRetryAt;
        if (nextRetryAt is null) return;

        try
        {
            var updated = await _store.TryUpdateIfStateAsync(
                item with { NextQuotaRetryAt = nextRetryAt }, item.State, ct);
            if (updated)
            {
                ScheduleTargetedRetry(item.Id, nextRetryAt.Value);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error updating NextQuotaRetryAt for work item {Id}", item.Id);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        CancelQuotaUsableSweep();
        await base.StopAsync(cancellationToken);

        Task? wakeUpSweep;
        lock (_quotaUsableSweepLock)
        {
            wakeUpSweep = _quotaUsableSweepTask;
        }

        if (wakeUpSweep is null)
            return;

        try
        {
            await wakeUpSweep.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || IsQuotaUsableSweepCancellationRequested())
        {
        }
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        CancelQuotaUsableSweep();
        Task? wakeUpSweep;
        lock (_quotaUsableSweepLock)
        {
            wakeUpSweep = _quotaUsableSweepTask;
        }

        if (wakeUpSweep is not null)
        {
            try
            {
                wakeUpSweep.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (IsQuotaUsableSweepCancellationRequested())
            {
            }
        }

        if (_quotaAvailabilitySignal is not null)
            _quotaAvailabilitySignal.QuotaUsableThresholdCrossed -= OnQuotaUsableThresholdCrossed;
        foreach (var timer in _targetedTimers.Values)
        {
            timer.Dispose();
        }
        _targetedTimers.Clear();
        _quotaUsableSweepCts.Dispose();
        base.Dispose();
    }

    private void CancelQuotaUsableSweep()
    {
        try { _quotaUsableSweepCts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private bool IsQuotaUsableSweepCancellationRequested()
    {
        try { return _quotaUsableSweepCts.IsCancellationRequested; }
        catch (ObjectDisposedException) { return true; }
    }
}
