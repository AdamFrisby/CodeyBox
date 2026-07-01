using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Hosted service that automatically retries work items parked for quota reset.
/// </summary>
public sealed class QuotaRetryScheduler : BackgroundService, IDisposable, IWorkerPoolQuotaRecovery, IQuotaFailureAutoRetryScheduler, IQuotaRetryDispatchPromoter
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
    private readonly OrchestratorOptions _opts;
    private readonly Func<AutoRetryOnQuotaFailureOptions> _autoRetryOptionsAccessor;
    private readonly IAgentQuotaAvailabilitySignal? _quotaAvailabilitySignal;
    private readonly IAgentPauseSignal? _pauseSignal;
    private readonly TimeProvider _time;
    private readonly IBaselineImageResolver _baselineResolver;
    private readonly ILogger<QuotaRetryScheduler> _log;
    // A single wake-up sweep machinery serves every signal that can make a
    // parked item routable again (quota refill, operator pause/resume, etc.).
    // The sweep is class-agnostic by design: it re-evaluates every parked item
    // against the FULL current class availability via the router, so a peer
    // becoming usable reroutes items parked against the original agent.
    private readonly CancellationTokenSource _wakeUpSweepCts = new();
    private readonly object _wakeUpSweepLock = new();
    private readonly ConcurrentDictionary<string, bool> _invalidIntervalWarnings = new(StringComparer.Ordinal);
    private Task? _wakeUpSweepTask;
    private int _wakeUpSweepScheduled;
    private int _disposed;

    // Active timers for targeted wakeups. Key = WorkItemId.
    private readonly ConcurrentDictionary<WorkItemId, ITimer> _targetedTimers = new();
    private readonly record struct QuotaRetryAttemptResult(string Outcome, string? Reason = null);
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
        IAgentPauseSignal? pauseSignal = null)
    {
        _store = store;
        _retrier = retrier;
        _opts = opts;
        _autoRetryOptionsAccessor = autoRetryOptionsAccessor ?? (() => _opts.AutoRetryOnQuotaFailure);
        _log = log;
        _router = router;
        _projects = projects;
        _queueController = queueController;
        _webhooks = webhooks;
        _time = timeProvider ?? TimeProvider.System;
        _baselineResolver = baselineResolver ?? NullBaselineImageResolver.Instance;
        _quotaAvailabilitySignal = quotaAvailabilitySignal;
        if (_quotaAvailabilitySignal is not null)
            _quotaAvailabilitySignal.QuotaUsableThresholdCrossed += OnClassAvailabilityChanged;
        // An operator pause/resume (or auto-expiry) of any agent can make a
        // class peer dispatchable for items parked on a sibling's exhaustion.
        // Without this hook, a WaitingForQuotaReset row pinned to an exhausted
        // agent stays parked until the periodic sweep ticks even if the
        // operator just paused the parking agent and a peer is wide open.
        _pauseSignal = pauseSignal;
        if (_pauseSignal is not null)
            _pauseSignal.AgentPauseChanged += OnClassAvailabilityChanged;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wasQuotaEnabled = false;
        var loggedDisabled = false;
        var lastQuotaSweepAt = _time.GetUtcNow();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var retryOptions = CurrentRetryOptions;
                if (!retryOptions.Enabled)
                {
                    if (!loggedDisabled)
                    {
                        _log.LogInformation("Quota auto-retry is disabled");
                        loggedDisabled = true;
                    }

                    wasQuotaEnabled = false;
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

                var now = _time.GetUtcNow();
                var quotaInterval = NormalizeInterval("Quota", retryOptions.PeriodicCheckInterval);
                var nextQuotaSweepAt = retryOptions.Enabled
                    ? lastQuotaSweepAt + quotaInterval
                    : DateTimeOffset.MaxValue;

                if (retryOptions.Enabled && now >= nextQuotaSweepAt)
                {
                    lastQuotaSweepAt = now;
                    await RunPeriodicSweepAsync(stoppingToken);
                    continue;
                }

                var delay = nextQuotaSweepAt - now;
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

    public async Task<QuotaRetryDispatchPromotionResult> TryPromoteForDispatchAsync(
        WorkItem item,
        CancellationToken ct = default)
    {
        if (item.State != WorkItemState.WaitingForQuotaReset)
        {
            return new QuotaRetryDispatchPromotionResult(
                Promoted: false,
                Outcome: "skipped:not-waiting-for-quota-reset",
                Reason: $"state={item.State}");
        }

        var now = _time.GetUtcNow();
        if (item.NextQuotaRetryAt is { } nextRetryAt && nextRetryAt > now)
        {
            return new QuotaRetryDispatchPromotionResult(
                Promoted: false,
                Outcome: "skipped:not-due",
                Reason: $"nextRetryAt={nextRetryAt:O}");
        }

        try
        {
            var outcome = await TryRetryAsync(item, "dispatch-due", ct);
            if (outcome.Outcome == "retried")
            {
                CancelTargetedRetry(item.Id);
                return new QuotaRetryDispatchPromotionResult(
                    Promoted: true,
                    Outcome: outcome.Outcome,
                    Reason: outcome.Reason);
            }

            return new QuotaRetryDispatchPromotionResult(
                Promoted: false,
                Outcome: outcome.Outcome,
                Reason: outcome.Reason,
                Disposition: DispatchDispositionForOutcome(outcome));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "Error promoting quota-waiting work item {Id} for dispatch",
                item.Id);
            return new QuotaRetryDispatchPromotionResult(
                Promoted: false,
                Outcome: "error",
                Reason: ex.Message,
                Disposition: QuotaRetryDispatchDisposition.Blocked);
        }
    }

    private static QuotaRetryDispatchDisposition DispatchDispositionForOutcome(
        QuotaRetryAttemptResult outcome)
    {
        if (outcome.Outcome == "retry-failed"
            && outcome.Reason?.Contains("state changed concurrently", StringComparison.OrdinalIgnoreCase) == true)
            return QuotaRetryDispatchDisposition.RestartSelection;

        return outcome.Outcome switch
        {
            "skipped:quota-still-gated" => QuotaRetryDispatchDisposition.Blocked,
            "skipped:max-retries" => QuotaRetryDispatchDisposition.RestartSelection,
            "moved:waiting-for-agent-resume" => QuotaRetryDispatchDisposition.RestartSelection,
            _ => QuotaRetryDispatchDisposition.Continue,
        };
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

    private void OnClassAvailabilityChanged()
    {
        if (!CurrentRetryOptions.Enabled)
            return;
        if (Volatile.Read(ref _disposed) == 1)
            return;
        if (_wakeUpSweepCts.IsCancellationRequested)
            return;
        if (Interlocked.Exchange(ref _wakeUpSweepScheduled, 1) == 1)
            return;

        var task = Task.Run(() => RunClassAvailabilityWakeUpSweepAsync(_wakeUpSweepCts.Token));
        lock (_wakeUpSweepLock)
        {
            _wakeUpSweepTask = task;
        }
    }

    private async Task RunClassAvailabilityWakeUpSweepAsync(CancellationToken ct)
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
            _log.LogError(ex, "Error during class-availability wake-up sweep");
        }
        finally
        {
            Interlocked.Exchange(ref _wakeUpSweepScheduled, 0);
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
        var (success, error, _, actualFrom, _) = await _retrier.RetryQuotaAutoAsync(
            item,
            from: retryFrom,
            trigger: trigger,
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

    private static string NormalizeRetryFrom(string? retryFrom) => retryFrom?.Trim().ToLowerInvariant() switch
    {
        "planning" => "planning",
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
        // Backstop: a parked item must always carry a forward retry trigger,
        // otherwise the targeted timer never fires and recovery depends solely
        // on the periodic sweep. Anchor to the next periodic sweep window so
        // the item is guaranteed to be re-evaluated.
        var backstopInterval = NormalizeInterval("Quota", retryOptions.PeriodicCheckInterval);
        nextRetryAt ??= _time.GetUtcNow().Add(backstopInterval);

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
        CancelWakeUpSweep();
        await base.StopAsync(cancellationToken);

        Task? wakeUpSweep;
        lock (_wakeUpSweepLock)
        {
            wakeUpSweep = _wakeUpSweepTask;
        }

        if (wakeUpSweep is null)
            return;

        try
        {
            await wakeUpSweep.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || IsWakeUpSweepCancellationRequested())
        {
        }
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        CancelWakeUpSweep();
        Task? wakeUpSweep;
        lock (_wakeUpSweepLock)
        {
            wakeUpSweep = _wakeUpSweepTask;
        }

        if (wakeUpSweep is not null)
        {
            try
            {
                wakeUpSweep.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (IsWakeUpSweepCancellationRequested())
            {
            }
        }

        if (_quotaAvailabilitySignal is not null)
            _quotaAvailabilitySignal.QuotaUsableThresholdCrossed -= OnClassAvailabilityChanged;
        if (_pauseSignal is not null)
            _pauseSignal.AgentPauseChanged -= OnClassAvailabilityChanged;
        foreach (var timer in _targetedTimers.Values)
        {
            timer.Dispose();
        }
        _targetedTimers.Clear();
        _wakeUpSweepCts.Dispose();
        base.Dispose();
    }

    private void CancelWakeUpSweep()
    {
        try { _wakeUpSweepCts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private bool IsWakeUpSweepCancellationRequested()
    {
        try { return _wakeUpSweepCts.IsCancellationRequested; }
        catch (ObjectDisposedException) { return true; }
    }
}
