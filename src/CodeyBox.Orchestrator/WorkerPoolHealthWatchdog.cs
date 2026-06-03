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
    private readonly OrchestratorService _orchestrator;
    private readonly Func<WorkerPoolHealthWatchdogOptions> _optsAccessor;
    private readonly IProjectRepository? _projects;
    private readonly IQueueController? _queueController;
    private readonly IAgentRegistry? _agents;
    private readonly IAgentAvailabilityRegistry? _availability;
    private readonly AgentClassRouter? _router;
    private readonly QuotaRetryScheduler? _quotaRetryScheduler;
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
        OrchestratorService orchestrator,
        Func<WorkerPoolHealthWatchdogOptions> optionsAccessor,
        ILogger<WorkerPoolHealthWatchdog> log,
        IProjectRepository? projects = null,
        IQueueController? queueController = null,
        IAgentRegistry? agents = null,
        IAgentAvailabilityRegistry? availability = null,
        AgentClassRouter? router = null,
        QuotaRetryScheduler? quotaRetryScheduler = null,
        IWebhookDispatcher? webhooks = null,
        TimeProvider? timeProvider = null,
        IStartupInitialRecoveryBarrier? startupRecoveryBarrier = null)
    {
        _orchestrator = orchestrator;
        _optsAccessor = optionsAccessor;
        _log = log;
        _projects = projects;
        _queueController = queueController;
        _agents = agents;
        _availability = availability;
        _router = router;
        _quotaRetryScheduler = quotaRetryScheduler;
        _webhooks = webhooks;
        _time = timeProvider ?? TimeProvider.System;
        _startupRecoveryBarrier = startupRecoveryBarrier;
    }

    public WorkerPoolHealthWatchdog(
        OrchestratorService orchestrator,
        WorkerPoolHealthWatchdogOptions opts,
        ILogger<WorkerPoolHealthWatchdog> log,
        IProjectRepository? projects = null,
        IQueueController? queueController = null,
        IAgentRegistry? agents = null,
        IAgentAvailabilityRegistry? availability = null,
        AgentClassRouter? router = null,
        QuotaRetryScheduler? quotaRetryScheduler = null,
        IWebhookDispatcher? webhooks = null,
        TimeProvider? timeProvider = null,
        IStartupInitialRecoveryBarrier? startupRecoveryBarrier = null)
        : this(orchestrator, () => opts, log, projects, queueController, agents, availability,
            router, quotaRetryScheduler, webhooks, timeProvider, startupRecoveryBarrier) { }

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
    /// endpoints; idempotent across repeated calls while the same condition is
    /// active.
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
            _log.LogWarning(ex, "Worker-pool health watchdog evaluation failed");
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

        await RecoverOrEscalateAsync(condition, opts, stuckFor, ct);
    }

    private async Task<PoolHealthCondition?> EvaluateConditionAsync(
        WorkerPoolHealthWatchdogOptions opts,
        CancellationToken ct)
    {
        if (_orchestrator.IsDispatchPaused)
            return null;

        if (_queueController is not null && _queueController.State == QueueState.Paused)
            return null;

        var status = await _orchestrator.GetStatusAsync(ct);
        if (status.CurrentlyRunning >= status.MaxConcurrent)
            return null;

        var candidates = await _orchestrator.ListRunnableCandidatesForHealthCheckAsync(
            opts.MaxRecoveryEnqueueBatchSize, ct);
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

        if (_router is not null)
        {
            var decision = await _router.ResolveAsync(item, project, ct, slotGate: null);
            if (decision.Chosen is { } chosen)
                return IsDirectAgentAvailable(chosen.Agent);
            if (decision.ShouldWait || decision.NoEligibleMembers)
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

        var cap = _orchestrator.GetAgentCap(agent);
        return cap <= 0 || _orchestrator.GetRunning(agent) < cap;
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

        var quotaSweepRan = false;
        if (_quotaRetryScheduler is not null)
        {
            await _quotaRetryScheduler.RunWatchdogRecoverySweepAsync(ct);
            quotaSweepRan = true;
        }

        var enqueued = await _orchestrator.TriggerDispatchRecoveryAsync(
            condition.RunnableCandidates.Select(i => i.Id), ct);

        _log.LogWarning(
            "Worker-pool health watchdog recovery attempt {Attempt}: enqueued {Enqueued} runnable candidate(s); quotaSweepRan={QuotaSweepRan}",
            attempt, enqueued, quotaSweepRan);

        if (opts.RecoveryVerificationDelay > TimeSpan.Zero)
            await Task.Delay(opts.RecoveryVerificationDelay, ct);

        var after = await EvaluateConditionAsync(opts, ct);
        if (after is null)
        {
            ResetCondition();
            return;
        }

        if (_recoveryAttempts >= maxAttempts)
            await EscalateRestartRequiredAsync(after, _time.GetUtcNow() - (_conditionObservedAt ?? _time.GetUtcNow()), ct);
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
