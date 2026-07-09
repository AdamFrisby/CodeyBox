using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// When an agent recovers from an infrastructure-level outage (auth, missing
/// binary, provisioning), sweep terminal-failed work items whose last attempt
/// landed on that agent during the outage window and re-enqueue the ones
/// classified as infra-shaped. Eliminates the manual operator sweep
/// previously needed (e.g. 27 items hand-retried after the antigravity auth
/// fix on 2026-06-10).
///
/// <para>Bounds:</para>
/// <list type="bullet">
///   <item>Only items whose <see cref="WorkItem.FailureKind"/> is
///   infrastructure-shaped according to <see cref="WorkItemFailureKinds.IsInfraShaped"/>
///   — genuine work-side
///   rejections (build, agent, configuration, audit non-convergence) are
///   never touched, because re-running them against a freshly-healthy agent
///   would only re-fail on the same input.</item>
///   <item>Only items whose failed agent resolves to the restored agent. The
///   sweep prefers a recent failed agent-involvement row when the history
///   store is wired. Without that row, only explicitly agent-stamped
///   pickup/auth failure kinds can fall back to <see cref="WorkItem.Agent"/>;
///   generic infrastructure rows fail closed because they can be produced
///   by non-agent sinks such as build verification or audit persistence.</item>
///   <item>Only items whose <see cref="WorkItem.UpdatedAt"/> falls inside
///   the outage window
///   <c>[OutageStartedAt - lookbackGrace, RestoredAt + margin]</c>. Items
///   that failed before the outage was even noticed (lookback grace) are
///   included; items that failed long before the outage are not. When
///   <see cref="AgentRestoredEvent.OutageStartedAt"/> is null the sweep
///   does not retry anything because there is no window to scope by, but it
///   still emits zero-count audit/webhook telemetry.</item>
///   <item>Idempotent. A prior successful claim for the restore event window
///   blocks duplicate sweeps. The new claim is committed atomically with
///   <see cref="WorkItemRetrier.RetryAgentRestoreAsync"/>'s snapshot-guarded
///   conditional update so a crash cannot strand an unrequeued terminal item.
///   Failed retry attempts release the idempotency key so a later sweep can
///   try again.</item>
/// </list>
///
/// <para>Routing: the requeued item flows through the normal class router,
/// so if a peer agent in the same class has a higher quality score the
/// router prefers it. The just-restored agent only gets the item back when
/// it is the highest-scored eligible member.</para>
///
/// <para>Enabled by default. Operators may disable it with the
/// <see cref="AgentRestoreRetryOptions.Enabled"/> flag.</para>
/// </summary>
public sealed class AgentRestoreRetryScheduler : BackgroundService
{
    private readonly IWorkItemStore _store;
    private readonly WorkItemRetrier _retrier;
    private readonly IAgentRestoreSignal? _signal;
    private readonly IWebhookDispatcher? _webhooks;
    private readonly IProjectRepository? _projects;
    private readonly IAgentInvolvementStore? _involvement;
    private readonly Func<AgentRestoreRetryOptions> _optionsAccessor;
    private readonly ILogger<AgentRestoreRetryScheduler> _log;
    private readonly TimeProvider _time;
    private readonly Channel<AgentRestoredEvent> _events;
    private readonly ConcurrentDictionary<Task, byte> _deferredEventWrites = new();
    private readonly ConcurrentDictionary<Task, byte> _delayedSweepTasks = new();

    public AgentRestoreRetryScheduler(
        IWorkItemStore store,
        WorkItemRetrier retrier,
        Func<AgentRestoreRetryOptions> optionsAccessor,
        ILogger<AgentRestoreRetryScheduler> log,
        IAgentRestoreSignal? signal = null,
        IWebhookDispatcher? webhooks = null,
        IProjectRepository? projects = null,
        IAgentInvolvementStore? involvement = null,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _retrier = retrier ?? throw new ArgumentNullException(nameof(retrier));
        _optionsAccessor = optionsAccessor ?? throw new ArgumentNullException(nameof(optionsAccessor));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _signal = signal;
        _webhooks = webhooks;
        _projects = projects;
        _involvement = involvement;
        _time = timeProvider ?? TimeProvider.System;
        var queueCapacity = _optionsAccessor().EventQueueCapacity;
        if (queueCapacity <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(AgentRestoreRetryOptions.EventQueueCapacity),
                queueCapacity,
                "Agent restore retry event queue capacity must be positive.");
        _events = Channel.CreateBounded<AgentRestoredEvent>(
            new BoundedChannelOptions(queueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
        if (_signal is not null)
            _signal.AgentRestored += EnqueueEvent;
    }

    private void EnqueueEvent(AgentRestoredEvent evt)
    {
        try
        {
            if (_events.Writer.TryWrite(evt))
                return;

            var deferredWrite = _events.Writer.WriteAsync(evt);
            if (deferredWrite.IsCompletedSuccessfully)
                return;

            TrackDeferredEventWrite(deferredWrite.AsTask(), evt);
            _log.LogWarning(
                "AgentRestoreRetryScheduler: restore event queue is full for {Agent}; sweep deferred until queue capacity is available",
                evt.Agent.Value);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Failed to enqueue agent-restore event for {Agent}; sweep skipped",
                evt.Agent.Value);
        }
    }

    private void TrackDeferredEventWrite(Task writeTask, AgentRestoredEvent evt)
    {
        var handle = new DeferredEventWriteHandle();
        var observerTask = ObserveDeferredEventWriteAsync(writeTask, evt, handle);
        handle.ObserverTask = observerTask;
        _deferredEventWrites.TryAdd(observerTask, 0);
        if (observerTask.IsCompleted)
            _deferredEventWrites.TryRemove(observerTask, out _);
    }

    private async Task ObserveDeferredEventWriteAsync(
        Task writeTask,
        AgentRestoredEvent evt,
        DeferredEventWriteHandle handle)
    {
        try
        {
            await writeTask.ConfigureAwait(false);
        }
        catch (ChannelClosedException ex)
        {
            _log.LogDebug(ex,
                "AgentRestoreRetryScheduler: restore event for {Agent} was not enqueued because the scheduler is stopping",
                evt.Agent.Value);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "AgentRestoreRetryScheduler: deferred restore event enqueue failed for {Agent}; sweep skipped",
                evt.Agent.Value);
        }
        finally
        {
            if (handle.ObserverTask is { } observerTask)
                _deferredEventWrites.TryRemove(observerTask, out _);
        }
    }

    private sealed class DeferredEventWriteHandle
    {
        public Task? ObserverTask { get; set; }
    }

    /// <summary>
    /// Test hook: synchronously runs the sweep for one restore event without
    /// going through the background channel. Returns the sweep summary so
    /// tests can assert (requeued, skipped) counts.
    /// </summary>
    internal Task<AgentRestoreSweepSummary> SweepForTestAsync(
        AgentRestoredEvent evt,
        CancellationToken ct = default) =>
        SweepAsync(evt, _optionsAccessor(), ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_signal is null)
        {
            _log.LogInformation("AgentRestoreRetryScheduler: no IAgentRestoreSignal registered; service is inert");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var evt = await _events.Reader.ReadAsync(stoppingToken).ConfigureAwait(false);
                var opts = _optionsAccessor();
                if (!opts.Enabled)
                {
                    _log.LogDebug(
                        "AgentRestoreRetryScheduler: dropping restore event for {Agent}; feature disabled",
                        evt.Agent.Value);
                    continue;
                }
                await ProcessRestoreEventAsync(evt, opts, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (ChannelClosedException)
            {
                // StopAsync completed the writer before the host's stopping
                // token flipped — exit cleanly instead of looping back into a
                // never-readable channel and spamming "sweep loop iteration
                // failed".
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "AgentRestoreRetryScheduler: sweep loop iteration failed");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_signal is not null)
            _signal.AgentRestored -= EnqueueEvent;
        _events.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        var deferredWrites = _deferredEventWrites.Keys.ToArray();
        if (deferredWrites.Length > 0)
        {
            try
            {
                await Task.WhenAll(deferredWrites).WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The host is giving up on graceful stop; pending deferred writes
                // will fault against the completed channel.
            }
        }

        var delayedSweeps = _delayedSweepTasks.Keys.ToArray();
        if (delayedSweeps.Length == 0)
            return;

        try
        {
            await Task.WhenAll(delayedSweeps).WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown already cancelled the scheduler token; delayed
            // post-restore passes may finish cancellation after StopAsync's
            // grace period.
        }
    }

    private async Task ProcessRestoreEventAsync(
        AgentRestoredEvent evt,
        AgentRestoreRetryOptions opts,
        CancellationToken ct)
    {
        await SweepAsync(evt, opts, ct).ConfigureAwait(false);

        if (evt.OutageStartedAt is null || opts.PostRestoreMargin <= TimeSpan.Zero)
            return;

        var delayedSweepAt = evt.RestoredAt + opts.PostRestoreMargin;
        var delay = delayedSweepAt - _time.GetUtcNow();
        if (delay <= TimeSpan.Zero)
            return;

        TrackDelayedPostRestoreSweep(evt, delayedSweepAt, delay, ct);
    }

    private void TrackDelayedPostRestoreSweep(
        AgentRestoredEvent evt,
        DateTimeOffset delayedSweepAt,
        TimeSpan delay,
        CancellationToken ct)
    {
        _log.LogDebug(
            "AgentRestoreRetryScheduler: scheduling delayed post-restore sweep for {Agent} at {DelayedSweepAt}",
            evt.Agent.Value,
            delayedSweepAt);
        var handle = new DelayedSweepHandle();
        var task = RunDelayedPostRestoreSweepAsync(evt, delay, ct, handle);
        handle.Task = task;
        _delayedSweepTasks.TryAdd(task, 0);
        if (task.IsCompleted)
            _delayedSweepTasks.TryRemove(task, out _);
    }

    private sealed class DelayedSweepHandle
    {
        public Task? Task { get; set; }
    }

    private async Task RunDelayedPostRestoreSweepAsync(
        AgentRestoredEvent evt,
        TimeSpan delay,
        CancellationToken ct,
        DelayedSweepHandle handle)
    {
        try
        {
            await Task.Delay(delay, _time, ct).ConfigureAwait(false);

            var latestOptions = _optionsAccessor();
            if (!latestOptions.Enabled)
            {
                _log.LogDebug(
                    "AgentRestoreRetryScheduler: delayed restore sweep for {Agent} skipped because feature disabled",
                    evt.Agent.Value);
                return;
            }

            await SweepAsync(evt, latestOptions, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.LogDebug(
                "AgentRestoreRetryScheduler: delayed restore sweep for {Agent} cancelled because the scheduler is stopping",
                evt.Agent.Value);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "AgentRestoreRetryScheduler: delayed restore sweep for {Agent} failed",
                evt.Agent.Value);
        }
        finally
        {
            if (handle.Task is { } task)
                _delayedSweepTasks.TryRemove(task, out _);
        }
    }

    private async Task<AgentRestoreSweepSummary> SweepAsync(
        AgentRestoredEvent evt,
        AgentRestoreRetryOptions opts,
        CancellationToken ct)
    {
        if (evt.OutageStartedAt is null)
        {
            _log.LogDebug(
                "AgentRestoreRetryScheduler: no outage window known for {Agent} (no prior failure timestamp); skipping sweep",
                evt.Agent.Value);
            // Emit the audit event anyway so operators monitoring
            // `agent.restore_requeue_swept` can distinguish "feature disabled"
            // (no event), "no candidates matched" (event with requeued=0,
            // skipped=0, outageStartedAt non-null), and the null-window
            // degenerate case (event with outageStartedAt=null) instead of
            // seeing silence in all three.
            AuditLog.AgentRestoreRequeueSwept(
                evt.Agent, evt.OutageStartedAt, evt.RestoredAt, 0, 0);
            await EmitSweepWebhookAsync(evt, requeued: 0, skipped: 0, ct: ct)
                .ConfigureAwait(false);
            return new AgentRestoreSweepSummary(0, 0);
        }

        var windowStart = evt.OutageStartedAt.Value - opts.LookbackGrace;
        var windowEnd = evt.RestoredAt + opts.PostRestoreMargin;
        var requeued = 0;
        var skipped = 0;
        var candidatesEvaluated = 0;
        DateTimeOffset? afterUpdatedAt = null;
        WorkItemId? afterId = null;

        while (candidatesEvaluated < opts.MaxCandidatesPerSweep)
        {
            var pageCount = 0;
            var remainingCandidateBudget = opts.MaxCandidatesPerSweep - candidatesEvaluated;
            await foreach (var item in _store.ListRestoreRetryCandidatesAsync(
                evt.Agent,
                windowStart,
                windowEnd,
                opts.InvolvementTerminalLookback,
                opts.InvolvementTerminalClockSkew,
                remainingCandidateBudget,
                afterUpdatedAt,
                afterId,
                ct).ConfigureAwait(false))
            {
                pageCount++;
                candidatesEvaluated++;
                afterUpdatedAt = item.UpdatedAt;
                afterId = item.Id;

                if (!await IsCandidateAsync(item, evt.Agent, opts, ct).ConfigureAwait(false))
                {
                    skipped++;
                    _log.LogDebug(
                        "AgentRestoreRetryScheduler: skipped {Id}; final attribution guard rejected agent {Agent}",
                        item.Id,
                        evt.Agent.Value);
                    continue;
                }

                if (await _store.HasAgentRestoreRetryClaimAsync(
                    item.Id,
                    evt.Agent,
                    evt.OutageStartedAt.Value,
                    ct).ConfigureAwait(false))
                {
                    skipped++;
                    _log.LogDebug(
                        "AgentRestoreRetryScheduler: skipped {Id}; restore event for {Agent} was already claimed",
                        item.Id,
                        evt.Agent.Value);
                    continue;
                }

                WorkItemRetryResult retry;
                try
                {
                    retry = await _retrier.RetryAgentRestoreDetailedAsync(
                        item,
                        from: null,
                        trigger: "agent-restore",
                        evt.Agent,
                        evt.OutageStartedAt.Value,
                        evt.RestoredAt,
                        ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    skipped++;
                    _log.LogWarning(ex,
                        "AgentRestoreRetryScheduler: retry threw for {Id}; skipping and continuing sweep",
                        item.Id);
                    continue;
                }

                if (!retry.Success)
                {
                    skipped++;
                    if (retry.FailureKind != WorkItemRetryFailureKind.StateChangedConcurrently)
                    {
                        await ReleaseClaimAfterRetryFailureAsync(item.Id, evt, "retry did not requeue", ct)
                            .ConfigureAwait(false);
                    }
                    _log.LogDebug(
                        "AgentRestoreRetryScheduler: skipped {Id}: {Error}",
                        item.Id, retry.Error);
                    continue;
                }

                requeued++;
                AuditLog.AgentRestoreRequeueItem(
                    item.Id, evt.Agent, item.FailureKind, retry.ActualFrom ?? "work");
                await EmitAgentRestoreRetryWebhookAsync(item, evt, retry.ActualFrom, ct).ConfigureAwait(false);
            }

            if (pageCount < remainingCandidateBudget)
                break;
        }

        AuditLog.AgentRestoreRequeueSwept(
            evt.Agent, evt.OutageStartedAt, evt.RestoredAt, requeued, skipped);
        await EmitSweepWebhookAsync(evt, requeued, skipped, ct)
            .ConfigureAwait(false);
        return new AgentRestoreSweepSummary(requeued, skipped);
    }

    private async Task ReleaseClaimAfterRetryFailureAsync(
        WorkItemId id,
        AgentRestoredEvent evt,
        string reason,
        CancellationToken ct)
    {
        if (evt.OutageStartedAt is not { } outageStartedAt)
            return;

        try
        {
            await _store.ReleaseAgentRestoreRetryClaimAsync(
                id,
                evt.Agent,
                outageStartedAt,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "AgentRestoreRetryScheduler: failed to release idempotency claim for {Id} after {Reason}",
                id,
                reason);
        }
    }

    private async Task<bool> IsCandidateAsync(
        WorkItem item,
        AgentKind restoredAgent,
        AgentRestoreRetryOptions opts,
        CancellationToken ct)
    {
        var failedAgent = await TryResolveLastFailedInvolvementAgentAsync(item.Id, item.UpdatedAt, opts, ct)
            .ConfigureAwait(false);
        return AgentRestoreRetryCandidatePolicy.IsEligible(item, restoredAgent, failedAgent);
    }

    private async Task<AgentKind?> TryResolveLastFailedInvolvementAgentAsync(
        WorkItemId id,
        DateTimeOffset terminalUpdatedAt,
        AgentRestoreRetryOptions opts,
        CancellationToken ct)
    {
        if (_involvement is null) return null;

        try
        {
            var rows = await _involvement.ListByWorkItemAsync(id, ct).ConfigureAwait(false);
            return AgentRestoreRetryCandidatePolicy.LatestFailedInvolvementAgent(
                rows,
                terminalUpdatedAt,
                opts.InvolvementTerminalLookback,
                opts.InvolvementTerminalClockSkew,
                AgentInvolvementOutcomes.IsFailure);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex,
                "AgentRestoreRetryScheduler: involvement lookup failed for {Id}; falling back to work item agent",
                id);
            return null;
        }
    }

    private async Task EmitSweepWebhookAsync(
        AgentRestoredEvent evt,
        int requeued,
        int skipped,
        CancellationToken ct)
    {
        if (_webhooks is null) return;

        try
        {
            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "agent.restore_requeue_swept",
                Details = new
                {
                    reason = "agent_restore",
                    restoredAgent = evt.Agent.Value,
                    outageStartedAt = evt.OutageStartedAt,
                    restoredAt = evt.RestoredAt,
                    requeued,
                    skipped,
                },
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "AgentRestoreRetryScheduler: sweep webhook delivery failed after agent {Agent} restore",
                evt.Agent.Value);
        }
    }

    private async Task EmitAgentRestoreRetryWebhookAsync(
        WorkItem item,
        AgentRestoredEvent evt,
        string? actualFrom,
        CancellationToken ct)
    {
        if (_webhooks is null) return;
        try
        {
            Project? project = null;
            if (_projects is not null)
            {
                try { project = await _projects.GetAsync(item.ProjectId, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception lookupEx)
                {
                    _log.LogDebug(lookupEx, "AgentRestoreRetryScheduler: project lookup failed for {Id}; emitting webhook without project context", item.Id);
                }
            }
            var updated = await _store.GetAsync(item.Id, ct).ConfigureAwait(false);
            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "work_item.agent_restore_requeued",
                WorkItem = updated ?? item,
                Project = project,
                Details = new
                {
                    workItemId = item.Id.ToString(),
                    reason = "agent_restore",
                    restoredAgent = evt.Agent.Value,
                    failureKind = item.FailureKind,
                    triggeredBy = "agent-restore",
                    from = actualFrom,
                    outageStartedAt = evt.OutageStartedAt,
                    restoredAt = evt.RestoredAt,
                },
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown — propagate so the sweep loop drains promptly.
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "AgentRestoreRetryScheduler: webhook delivery failed for work item {Id} after agent {Agent} restore",
                item.Id, evt.Agent.Value);
        }
    }
}

/// <summary>
/// Options for <see cref="AgentRestoreRetryScheduler"/>. Bound from
/// <c>CodeyBox:AutoRequeueOnAgentRestore</c>. All values are hot-reloadable
/// except <see cref="EventQueueCapacity"/>, which sizes the scheduler channel
/// at startup.
/// </summary>
public sealed record AgentRestoreRetryOptions
{
    public static readonly TimeSpan DefaultLookbackGrace = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan DefaultPostRestoreMargin = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultInvolvementTerminalLookback = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan DefaultInvolvementTerminalClockSkew = TimeSpan.FromMinutes(1);

    public static string DefaultLookbackGraceConfigValue => ToConfigString(DefaultLookbackGrace);
    public static string DefaultPostRestoreMarginConfigValue => ToConfigString(DefaultPostRestoreMargin);
    public static string DefaultInvolvementTerminalLookbackConfigValue => ToConfigString(DefaultInvolvementTerminalLookback);
    public static string DefaultInvolvementTerminalClockSkewConfigValue => ToConfigString(DefaultInvolvementTerminalClockSkew);

    public const int DefaultMaxCandidatesPerSweep = 500;
    public const int DefaultEventQueueCapacity = 128;

    /// <summary>
    /// Master switch. Default <c>true</c>; set false to disable restore-driven
    /// infra-failure sweeps.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// How far back from <see cref="AgentRestoredEvent.OutageStartedAt"/> the
    /// sweep looks for candidates. Work items can fail several minutes
    /// before the smoke probe notices the outage — this lookback catches
    /// them. Defaults to <see cref="DefaultLookbackGrace"/>.
    /// </summary>
    public TimeSpan LookbackGrace { get; init; } = DefaultLookbackGrace;

    /// <summary>
    /// How far past <see cref="AgentRestoredEvent.RestoredAt"/> the sweep
    /// also considers as part of the outage window. Defaults to
    /// <see cref="DefaultPostRestoreMargin"/> and guards against ordering races
    /// where a failed write outlives the restore notification by milliseconds.
    /// </summary>
    public TimeSpan PostRestoreMargin { get; init; } = DefaultPostRestoreMargin;

    /// <summary>
    /// Maximum number of store-filtered terminal candidates evaluated during one
    /// restore sweep. Applied inside the store query before buffering rows.
    /// Default 500.
    /// </summary>
    public int MaxCandidatesPerSweep { get; init; } = DefaultMaxCandidatesPerSweep;

    /// <summary>
    /// Bounded channel capacity for restore notifications awaiting the scheduler
    /// loop. Read once when the scheduler is constructed; restart the API to
    /// apply changes. Default 128.
    /// </summary>
    public int EventQueueCapacity { get; init; } = DefaultEventQueueCapacity;

    /// <summary>
    /// How far before a terminal work-item update a failed involvement row can
    /// still attribute the failed agent. Defaults to
    /// <see cref="DefaultInvolvementTerminalLookback"/>.
    /// </summary>
    public TimeSpan InvolvementTerminalLookback { get; init; } = DefaultInvolvementTerminalLookback;

    /// <summary>
    /// How far after a terminal work-item update a failed involvement row can
    /// still attribute the failed agent, absorbing write-order clock skew.
    /// Defaults to <see cref="DefaultInvolvementTerminalClockSkew"/>.
    /// </summary>
    public TimeSpan InvolvementTerminalClockSkew { get; init; } = DefaultInvolvementTerminalClockSkew;

    private static string ToConfigString(TimeSpan value) =>
        value.ToString("c", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Outcome of one restore-driven sweep. <see cref="Requeued"/> is the count
/// of items the sweep actually transitioned back to Queued; <see cref="Skipped"/>
/// is the count of candidates that matched the filter but were not retried
/// (concurrent state change, retrier-internal reject like an open operator question).
/// </summary>
public readonly record struct AgentRestoreSweepSummary(int Requeued, int Skipped);
