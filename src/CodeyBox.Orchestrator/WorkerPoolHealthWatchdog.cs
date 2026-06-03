using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Dispatcher-level watchdog for the inverse of the drain watcher: the pool has
/// free worker slots, runnable dependency-satisfied work exists, and at least
/// one eligible agent is available, but dispatch has not spawned a worker for
/// the configured window.
/// </summary>
public sealed class WorkerPoolHealthWatchdog : BackgroundService
{
    private readonly IWorkerPoolHealthSource _pool;
    private readonly Func<WorkerPoolHealthWatchdogOptions> _optsAccessor;
    private readonly IAgentCapacitySnapshot _capacity;
    private readonly IProjectRepository? _projects;
    private readonly IQueueController? _queueController;
    private readonly IAgentRegistry? _agents;
    private readonly IAgentAvailabilityRegistry? _availability;
    private readonly IAgentRoutingReadiness? _routingReadiness;
    private readonly IWorkerPoolQuotaRecovery? _quotaRecovery;
    private readonly IWebhookDispatcher? _webhooks;
    private readonly ILogger<WorkerPoolHealthWatchdog> _log;
    private readonly TimeProvider _time;
    private readonly IStartupInitialRecoveryBarrier? _startupRecoveryBarrier;

    private DateTimeOffset? _conditionObservedAt;
    private DateTimeOffset? _observedLastSpawnAt;
    private int _recoveryAttempts;
    private bool _restartEscalated;

    private WorkerPoolHealthWatchdogOptions _opts => _optsAccessor();

    public WorkerPoolHealthWatchdog(
        IWorkerPoolHealthSource pool,
        IAgentCapacitySnapshot capacity,
        Func<WorkerPoolHealthWatchdogOptions> optionsAccessor,
        ILogger<WorkerPoolHealthWatchdog> log,
        IProjectRepository? projects = null,
        IQueueController? queueController = null,
        IAgentRegistry? agents = null,
        IAgentAvailabilityRegistry? availability = null,
        IAgentRoutingReadiness? routingReadiness = null,
        IWorkerPoolQuotaRecovery? quotaRecovery = null,
        IWebhookDispatcher? webhooks = null,
        TimeProvider? timeProvider = null,
        IStartupInitialRecoveryBarrier? startupRecoveryBarrier = null)
    {
        _pool = pool;
        _capacity = capacity;
        _optsAccessor = optionsAccessor;
        _log = log;
        _projects = projects;
        _queueController = queueController;
        _agents = agents;
        _availability = availability;
        _routingReadiness = routingReadiness;
        _quotaRecovery = quotaRecovery;
        _webhooks = webhooks;
        _time = timeProvider ?? TimeProvider.System;
        _startupRecoveryBarrier = startupRecoveryBarrier;
    }

    public WorkerPoolHealthWatchdog(
        IWorkerPoolHealthSource pool,
        IAgentCapacitySnapshot capacity,
        WorkerPoolHealthWatchdogOptions opts,
        ILogger<WorkerPoolHealthWatchdog> log,
        IProjectRepository? projects = null,
        IQueueController? queueController = null,
        IAgentRegistry? agents = null,
        IAgentAvailabilityRegistry? availability = null,
        IAgentRoutingReadiness? routingReadiness = null,
        IWorkerPoolQuotaRecovery? quotaRecovery = null,
        IWebhookDispatcher? webhooks = null,
        TimeProvider? timeProvider = null,
        IStartupInitialRecoveryBarrier? startupRecoveryBarrier = null)
        : this(pool, capacity, () => opts, log, projects, queueController, agents, availability,
            routingReadiness, quotaRecovery, webhooks, timeProvider, startupRecoveryBarrier)
    { }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_startupRecoveryBarrier is not null)
            await _startupRecoveryBarrier.InitialRecoveryCompleted.WaitAsync(stoppingToken);

        await RunOnceAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = _opts.CheckInterval;
            await Task.Delay(delay, stoppingToken);
            await RunOnceAsync(stoppingToken);
        }
    }

    /// <summary>
    /// Runs one pool-health evaluation. Public for tests and future operator
    /// endpoints. Repeated calls while a stuck condition remains active can
    /// advance bounded recovery attempts after <see cref="WorkerPoolHealthWatchdogOptions.StallTimeout"/>.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var opts = _opts;
        if (opts.StallTimeout <= TimeSpan.Zero)
        {
            ResetCondition();
            return;
        }

        PoolHealthCondition? condition;
        try
        {
            condition = await EvaluateConditionAsync(opts, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Worker-pool health watchdog evaluation failed");
            await PublishWatchdogFailureAsync(ex, ct);
            return;
        }

        if (condition is null)
        {
            ResetCondition();
            return;
        }

        var now = _time.GetUtcNow();
        if (_conditionObservedAt is null || _observedLastSpawnAt != condition.LastSpawnAt)
        {
            _conditionObservedAt = now;
            _observedLastSpawnAt = condition.LastSpawnAt;
            _recoveryAttempts = 0;
            _restartEscalated = false;
            _log.LogDebug(
                "Worker-pool health watchdog observed under-filled runnable pool: running={Running}/{Max}, candidates={Candidates}, lastSpawnAt={LastSpawnAt}",
                condition.CurrentlyRunning,
                condition.MaxConcurrent,
                condition.RunnableCandidates.Count,
                condition.LastSpawnAt);
            return;
        }

        var stuckFor = now - _conditionObservedAt.Value;
        if (stuckFor < opts.StallTimeout)
            return;

        try
        {
            await RecoverOrEscalateAsync(condition, opts, stuckFor, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Worker-pool health watchdog recovery failed");
            if (_recoveryAttempts >= opts.MaxRecoveryAttempts)
                await EscalateRestartRequiredAsync(condition, stuckFor, ct);
        }
    }

    private async Task<PoolHealthCondition?> EvaluateConditionAsync(
        WorkerPoolHealthWatchdogOptions opts,
        CancellationToken ct)
    {
        if (_pool.IsDispatchPaused)
            return null;

        if (_queueController is not null && _queueController.State == QueueState.Paused)
            return null;

        var status = await _pool.GetStatusAsync(ct);
        if (status.CurrentlyRunning >= status.MaxConcurrent)
            return null;

        var candidates = await _pool.ListPoolHealthCandidatesAsync(
            opts.MaxHealthCheckCandidateScan, ct);
        if (candidates.Count == 0)
            return null;

        var runnable = new List<WorkItem>();
        foreach (var candidate in candidates)
        {
            if (await IsProjectPausedAsync(candidate, ct))
                continue;

            if (await HasEligibleAvailableAgentAsync(candidate, ct))
                runnable.Add(candidate);
        }

        if (runnable.Count == 0)
            return null;

        return new PoolHealthCondition(
            status.MaxConcurrent,
            status.CurrentlyRunning,
            status.LastSpawnAt,
            runnable);
    }

    private async Task<bool> IsProjectPausedAsync(WorkItem item, CancellationToken ct)
    {
        if (_queueController is null)
            return false;

        var state = await _queueController.GetProjectStateAsync(item.ProjectId, ct);
        return state is { Paused: true };
    }

    private async Task<bool> HasEligibleAvailableAgentAsync(WorkItem item, CancellationToken ct)
    {
        Project? project = null;
        if (_projects is not null)
        {
            project = await _projects.GetAsync(item.ProjectId, ct);
            if (project is null)
                return false;
        }

        if (_routingReadiness is not null)
        {
            var readiness = await _routingReadiness.CheckReadinessAsync(item, project, _capacity, ct);
            if (readiness.State == AgentRoutingReadinessState.Available)
                return true;
            if (readiness.State == AgentRoutingReadinessState.Unavailable)
                return false;
        }

        var directAgent = item.Agent ?? project?.DefaultAgent;
        return directAgent is { } agent && IsDirectAgentAvailable(agent);
    }

    private bool IsDirectAgentAvailable(AgentKind agent)
    {
        if (_agents is not null && !_agents.Available.Contains(agent))
            return false;

        var availability = _availability?.GetAvailability(agent);
        if (availability is { Available: false })
            return false;

        return _capacity.HasCapacity(agent);
    }

    private async Task RecoverOrEscalateAsync(
        PoolHealthCondition condition,
        WorkerPoolHealthWatchdogOptions opts,
        TimeSpan stuckFor,
        CancellationToken ct)
    {
        var maxAttempts = opts.MaxRecoveryAttempts;
        if (_recoveryAttempts >= maxAttempts)
        {
            await EscalateRestartRequiredAsync(condition, stuckFor, ct);
            return;
        }

        _recoveryAttempts++;
        var attempt = _recoveryAttempts;

        _log.LogCritical(
            "Worker pool stalled: {Running}/{Max} running with {FreeSlots} free slot(s), {Runnable} runnable candidate(s), and no worker spawn for {StuckFor}. Recovery attempt {Attempt}/{MaxAttempts}",
            condition.CurrentlyRunning,
            condition.MaxConcurrent,
            condition.FreeSlots,
            condition.RunnableCandidates.Count,
            stuckFor,
            attempt,
            maxAttempts);

        await PublishAsync("worker_pool.stalled", new
        {
            severity = "critical",
            currentlyRunning = condition.CurrentlyRunning,
            maxConcurrent = condition.MaxConcurrent,
            freeSlots = condition.FreeSlots,
            runnableWorkItemIds = condition.RunnableCandidates.Select(i => i.Id.ToString()).ToArray(),
            lastSpawnAt = condition.LastSpawnAt,
            stuckForSeconds = (long)stuckFor.TotalSeconds,
            recoveryAttempt = attempt,
            maxRecoveryAttempts = maxAttempts,
        }, ct);

        try
        {
            var quotaSweepRan = false;
            if (_quotaRecovery is not null)
            {
                await _quotaRecovery.RunWatchdogRecoverySweepAsync(ct);
                quotaSweepRan = true;
            }

            var enqueueIds = condition.RunnableCandidates
                .Where(static i => i.State != WorkItemState.WaitingForQuotaReset)
                .Take(opts.MaxRecoveryEnqueueBatchSize)
                .Select(i => i.Id);
            var enqueued = await _pool.TriggerDispatchRecoveryAsync(enqueueIds, ct);

            _log.LogWarning(
                "Worker-pool health watchdog recovery attempt {Attempt}: enqueued {Enqueued} runnable candidate(s); quotaSweepRan={QuotaSweepRan}",
                attempt, enqueued, quotaSweepRan);

            if (opts.RecoveryVerificationDelay > TimeSpan.Zero)
                await Task.Delay(opts.RecoveryVerificationDelay, ct);

            var after = await EvaluateConditionAsync(opts, ct);
            if (after is null || HasDispatchProgress(condition, after))
            {
                ResetCondition();
                return;
            }

            if (_recoveryAttempts >= maxAttempts)
                await EscalateRestartRequiredAsync(
                    after,
                    _time.GetUtcNow() - (_conditionObservedAt ?? _time.GetUtcNow()),
                    ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Worker-pool health watchdog recovery attempt {Attempt} failed", attempt);
            if (_recoveryAttempts >= maxAttempts)
                await EscalateRestartRequiredAsync(condition, stuckFor, ct);
        }
    }

    private static bool HasDispatchProgress(PoolHealthCondition before, PoolHealthCondition after)
    {
        if (after.LastSpawnAt != before.LastSpawnAt)
            return true;

        return after.CurrentlyRunning > before.CurrentlyRunning;
    }

    private async Task EscalateRestartRequiredAsync(
        PoolHealthCondition condition,
        TimeSpan stuckFor,
        CancellationToken ct)
    {
        if (_restartEscalated)
            return;

        _restartEscalated = true;
        _log.LogCritical(
            "Worker pool recovery failed: {Running}/{Max} running with {FreeSlots} free slot(s) and {Runnable} runnable candidate(s) after {Attempts} recovery attempt(s). Operator restart needed.",
            condition.CurrentlyRunning,
            condition.MaxConcurrent,
            condition.FreeSlots,
            condition.RunnableCandidates.Count,
            _recoveryAttempts);

        await PublishAsync("worker_pool.restart_required", new
        {
            severity = "critical",
            currentlyRunning = condition.CurrentlyRunning,
            maxConcurrent = condition.MaxConcurrent,
            freeSlots = condition.FreeSlots,
            runnableWorkItemIds = condition.RunnableCandidates.Select(i => i.Id.ToString()).ToArray(),
            lastSpawnAt = condition.LastSpawnAt,
            stuckForSeconds = (long)stuckFor.TotalSeconds,
            recoveryAttempts = _recoveryAttempts,
            reason = "worker-pool watchdog recovery did not produce dispatch progress",
        }, ct);
    }

    private async Task PublishAsync(string eventName, object details, CancellationToken ct)
    {
        if (_webhooks is null)
            return;

        try
        {
            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = eventName,
                Details = details,
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Worker-pool health watchdog failed to publish {Event}", eventName);
        }
    }

    private Task PublishWatchdogFailureAsync(Exception ex, CancellationToken ct)
        => PublishAsync("worker_pool.restart_required", new
        {
            severity = "critical",
            reason = "worker-pool health watchdog evaluation failed",
            exceptionType = ex.GetType().Name,
            message = ex.Message,
        }, ct);

    private void ResetCondition()
    {
        _conditionObservedAt = null;
        _observedLastSpawnAt = null;
        _recoveryAttempts = 0;
        _restartEscalated = false;
    }

    private sealed record PoolHealthCondition(
        int MaxConcurrent,
        int CurrentlyRunning,
        DateTimeOffset? LastSpawnAt,
        IReadOnlyList<WorkItem> RunnableCandidates)
    {
        public int FreeSlots => Math.Max(0, MaxConcurrent - CurrentlyRunning);
    }
}
