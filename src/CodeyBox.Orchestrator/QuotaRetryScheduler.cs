using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Hosted service that automatically retries work items that failed due to
/// quota exhaustion, once quota is available again.
/// </summary>
public sealed class QuotaRetryScheduler : BackgroundService, IDisposable
{
    private readonly IWorkItemStore _store;
    private readonly WorkItemRetrier _retrier;
    private readonly AgentClassRouter? _router;
    private readonly IProjectRepository? _projects;
    private readonly IQueueController? _queueController;
    private readonly IWebhookDispatcher? _webhooks;
    private readonly OrchestratorOptions _opts;
    private readonly TimeProvider _time;
    private readonly IBaselineImageResolver _baselineResolver;
    private readonly ILogger<QuotaRetryScheduler> _log;

    // Active timers for targeted wakeups. Key = WorkItemId.
    private readonly ConcurrentDictionary<WorkItemId, ITimer> _targetedTimers = new();
    private readonly record struct QuotaRetryAttemptResult(string Outcome, string? Reason = null);

    public QuotaRetryScheduler(
        IWorkItemStore store,
        WorkItemRetrier retrier,
        OrchestratorOptions opts,
        ILogger<QuotaRetryScheduler> log,
        AgentClassRouter? router = null,
        IProjectRepository? projects = null,
        IQueueController? queueController = null,
        IWebhookDispatcher? webhooks = null,
        TimeProvider? timeProvider = null,
        IBaselineImageResolver? baselineResolver = null)
    {
        _store = store;
        _retrier = retrier;
        _opts = opts;
        _log = log;
        _router = router;
        _projects = projects;
        _queueController = queueController;
        _webhooks = webhooks;
        _time = timeProvider ?? TimeProvider.System;
        _baselineResolver = baselineResolver ?? NullBaselineImageResolver.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.AutoRetryOnQuotaFailure.Enabled)
        {
            _log.LogInformation("Quota auto-retry is disabled");
            return;
        }

        _log.LogInformation("Quota auto-retry is enabled. Periodic interval: {Interval}, Drift margin: {Margin}",
            _opts.AutoRetryOnQuotaFailure.PeriodicCheckInterval,
            _opts.AutoRetryOnQuotaFailure.ClockDriftSafetyMargin);

        // Re-arm timers from the DB on startup.
        await RearmTimersAsync(stoppingToken);

        // Periodic sweep loop.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_opts.AutoRetryOnQuotaFailure.PeriodicCheckInterval, stoppingToken);
                await RunPeriodicSweepAsync(stoppingToken);
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
            if (item.NextQuotaRetryAt.HasValue)
            {
                if (await TryRearmTimerAsync(item, ct))
                    count++;
            }
        }
        _log.LogInformation("Re-armed {Count} quota retry timers", count);
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
                _log.LogError(ex, "Error during targeted quota retry for work item {Id}", id);
            }
        });
    }

    private async Task<QuotaRetryAttemptResult> TryRetryAsync(WorkItem item, string source, CancellationToken ct)
    {
        try
        {
            var outcome = await TryRetryCoreAsync(item, source, ct);
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

    private async Task<QuotaRetryAttemptResult> TryRetryCoreAsync(WorkItem item, string trigger, CancellationToken ct)
    {
        // 1. Check max retries.
        if (item.QuotaRetryAttempts >= _opts.AutoRetryOnQuotaFailure.MaxAutoRetriesPerWorkItem)
        {
            _log.LogInformation("Work item {Id} reached max quota auto-retries ({Max}); skipping",
                item.Id, _opts.AutoRetryOnQuotaFailure.MaxAutoRetriesPerWorkItem);
            return new QuotaRetryAttemptResult("skipped:max-retries",
                $"attempts={item.QuotaRetryAttempts}; max={_opts.AutoRetryOnQuotaFailure.MaxAutoRetriesPerWorkItem}");
        }

        // 2. Check if queue is paused.
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

        // 3. Resolve project.
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

        // 4. Ask the quota gate.
        if (_router is null)
        {
            _log.LogInformation("Quota router unavailable; skipping auto-retry for work item {Id}", item.Id);
            return new QuotaRetryAttemptResult("skipped:router-unavailable");
        }
        // Pin the baseline ref before the router gates, mirroring the dispatch
        // pickup path: the in-VM smoke gate must probe the image this item will
        // actually be cloned from, not the active baseline. Retried items are
        // normally already stamped from their first pickup; this only fills a
        // null ref (e.g. an item that never ran) so the gate never probes/caches
        // under the wrong image.
        if (item.BaselineImageRef is null)
        {
            var pinnedRef = ResolveBaselineRefForRetry(item, project);
            if (pinnedRef is not null)
                item = item with { BaselineImageRef = pinnedRef };
        }

        var decision = await _router.ResolveAsync(item, project, ct);
        if (decision.ShouldWait)
        {
            _log.LogDebug("Work item {Id} still gated by quota; decision: {Reason}", item.Id, decision.Reason);
            return new QuotaRetryAttemptResult("skipped:quota-still-gated", decision.Reason);
        }
        if (decision.NoEligibleMembers)
        {
            _log.LogInformation("Work item {Id} has no eligible class members; skipping auto-retry", item.Id);
            return new QuotaRetryAttemptResult("skipped:no-eligible-members", decision.Reason);
        }

        // 5. Trigger retry.
        return await PerformRetryAsync(item, trigger, ct);
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

    private async Task<QuotaRetryAttemptResult> PerformRetryAsync(WorkItem item, string trigger, CancellationToken ct)
    {
        var retryFrom = NormalizeRetryFrom(item.QuotaRetryFrom);
        _log.LogInformation("Triggering quota auto-retry ({Trigger}) for work item {Id} (attempt {Attempt})",
            trigger, item.Id, item.QuotaRetryAttempts + 1);

        // Re-use logic from shared WorkItemRetrier to ensure identical side effects,
        // audit logs, and conditional state updates (prevents race conditions).
        var (success, error, _, actualFrom) = await _retrier.RetryAsync(item, from: retryFrom, trigger, ct);

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
        "audit" => "audit",
        "merge" => "merge",
        "upstream" => "upstream",
        _ => "work",
    };

    /// <summary>
    /// Notifies the scheduler that a work item has failed with a quota error,
    /// so it can schedule a targeted retry.
    /// </summary>
    public async Task NotifyQuotaFailureAsync(WorkItem item)
    {
        if (!_opts.AutoRetryOnQuotaFailure.Enabled) return;
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
                var project = await _projects.GetAsync(item.ProjectId, CancellationToken.None);
                resetAt = await _router.ComputeEarliestExhaustedResetAsync(item, project, CancellationToken.None);
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
            ? resetAt.Value.Add(_opts.AutoRetryOnQuotaFailure.ClockDriftSafetyMargin)
            : item.NextQuotaRetryAt;
        if (nextRetryAt is null) return;

        try
        {
            var updated = await _store.TryUpdateIfStateAsync(
                item with { NextQuotaRetryAt = nextRetryAt }, item.State, CancellationToken.None);
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
    public override void Dispose()
    {
        foreach (var timer in _targetedTimers.Values)
        {
            timer.Dispose();
        }
        _targetedTimers.Clear();
        base.Dispose();
    }
}
