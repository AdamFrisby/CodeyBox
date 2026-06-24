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
///   <item>Only items whose <see cref="WorkItem.FailureKind"/> is in
///   <see cref="WorkItemFailureKinds.InfraShaped"/> — genuine work-side
///   rejections (build, agent, configuration, audit non-convergence) are
///   never touched, because re-running them against a freshly-healthy agent
///   would only re-fail on the same input.</item>
///   <item>Only items whose <see cref="WorkItem.Agent"/> matches the
///   restored agent. The <c>Agent</c> field reflects the LAST phase's
///   chosen agent (overwritten as the item moves through Work → Audit →
///   Rework → Merge), so this picks the items whose final failed attempt
///   was on the recovered agent.</item>
///   <item>Only items whose <see cref="WorkItem.UpdatedAt"/> falls inside
///   the outage window
///   <c>[OutageStartedAt - lookbackGrace, RestoredAt + margin]</c>. Items
///   that failed before the outage was even noticed (lookback grace) are
///   included; items that failed long before the outage are not. When
///   <see cref="AgentRestoredEvent.OutageStartedAt"/> is null (operator
///   reset on a never-failed agent, startup pass on an agent never
///   excluded) the sweep is a no-op — there is no window to scope by.</item>
///   <item>Idempotent. Each candidate is re-enqueued through
///   <see cref="WorkItemRetrier.RetryAsync"/>'s
///   <c>TryUpdateIfStateAsync</c> conditional update, so two restore
///   events firing in quick succession can't double-retry the same item.</item>
/// </list>
///
/// <para>Routing: the requeued item flows through the normal class router,
/// so if a peer agent in the same class has a higher quality score the
/// router prefers it. The just-restored agent only gets the item back when
/// it is the highest-scored eligible member.</para>
///
/// <para>OFF by default. Operators enable per the
/// <see cref="AgentRestoreRetryOptions.Enabled"/> flag.</para>
/// </summary>
public sealed class AgentRestoreRetryScheduler : BackgroundService
{
    private readonly IWorkItemStore _store;
    private readonly WorkItemRetrier _retrier;
    private readonly IAgentRestoreSignal? _signal;
    private readonly IWebhookDispatcher? _webhooks;
    private readonly IProjectRepository? _projects;
    private readonly Func<AgentRestoreRetryOptions> _optionsAccessor;
    private readonly TimeProvider _time;
    private readonly ILogger<AgentRestoreRetryScheduler> _log;

    private readonly System.Threading.Channels.Channel<AgentRestoredEvent> _events =
        System.Threading.Channels.Channel.CreateUnbounded<AgentRestoredEvent>(
            new System.Threading.Channels.UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });

    public AgentRestoreRetryScheduler(
        IWorkItemStore store,
        WorkItemRetrier retrier,
        Func<AgentRestoreRetryOptions> optionsAccessor,
        ILogger<AgentRestoreRetryScheduler> log,
        IAgentRestoreSignal? signal = null,
        IWebhookDispatcher? webhooks = null,
        IProjectRepository? projects = null,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _retrier = retrier ?? throw new ArgumentNullException(nameof(retrier));
        _optionsAccessor = optionsAccessor ?? throw new ArgumentNullException(nameof(optionsAccessor));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _signal = signal;
        _webhooks = webhooks;
        _projects = projects;
        _time = timeProvider ?? TimeProvider.System;
        if (_signal is not null)
            _signal.AgentRestored += EnqueueEvent;
    }

    private void EnqueueEvent(AgentRestoredEvent evt)
    {
        try
        {
            _events.Writer.TryWrite(evt);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Failed to enqueue agent-restore event for {Agent}; sweep skipped",
                evt.Agent.Value);
        }
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
                await SweepAsync(evt, opts, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
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
            return new AgentRestoreSweepSummary(0, 0);
        }

        var windowStart = evt.OutageStartedAt.Value - opts.LookbackGrace;
        var windowEnd = evt.RestoredAt + opts.PostRestoreMargin;
        var requeued = 0;
        var skipped = 0;

        var candidateStates = new[]
        {
            WorkItemState.Failed,
            WorkItemState.MergeConflictResolutionFailed,
        };

        foreach (var state in candidateStates)
        {
            await foreach (var item in _store.ListByStateAsync(state, ct))
            {
                if (!IsCandidate(item, evt.Agent, windowStart, windowEnd))
                {
                    continue;
                }

                if (requeued >= opts.MaxItemsPerRestore)
                {
                    skipped++;
                    _log.LogWarning(
                        "AgentRestoreRetryScheduler: per-restore cap {Cap} reached for {Agent}; further candidates left parked (item {Id})",
                        opts.MaxItemsPerRestore, evt.Agent.Value, item.Id);
                    continue;
                }

                var (success, error, _, actualFrom, _) = await _retrier.RetryAsync(
                    item, from: null, trigger: "agent-restore", ct).ConfigureAwait(false);
                if (!success)
                {
                    skipped++;
                    _log.LogDebug(
                        "AgentRestoreRetryScheduler: skipped {Id}: {Error}",
                        item.Id, error);
                    continue;
                }

                requeued++;
                AuditLog.AgentRestoreRequeueItem(
                    item.Id, evt.Agent, item.FailureKind, actualFrom ?? "work");
                await EmitAutoRetryWebhookAsync(item, evt, actualFrom).ConfigureAwait(false);
            }
        }

        AuditLog.AgentRestoreRequeueSwept(
            evt.Agent, evt.OutageStartedAt, evt.RestoredAt, requeued, skipped);
        return new AgentRestoreSweepSummary(requeued, skipped);
    }

    private static bool IsCandidate(
        WorkItem item,
        AgentKind restoredAgent,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        if (!WorkItemFailureKinds.IsInfraShaped(item.FailureKind))
            return false;

        if (item.Agent is not { } itemAgent)
            return false;
        if (!string.Equals(itemAgent.Value, restoredAgent.Value, StringComparison.OrdinalIgnoreCase))
            return false;

        if (item.UpdatedAt < windowStart || item.UpdatedAt > windowEnd)
            return false;

        return true;
    }

    private async Task EmitAutoRetryWebhookAsync(
        WorkItem item,
        AgentRestoredEvent evt,
        string? actualFrom)
    {
        if (_webhooks is null) return;
        try
        {
            Project? project = null;
            if (_projects is not null)
            {
                try { project = await _projects.GetAsync(item.ProjectId, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception lookupEx)
                {
                    _log.LogDebug(lookupEx, "AgentRestoreRetryScheduler: project lookup failed for {Id}; emitting webhook without project context", item.Id);
                }
            }
            var updated = await _store.GetAsync(item.Id, CancellationToken.None).ConfigureAwait(false);
            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "work_item.auto_retry",
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
            }, CancellationToken.None).ConfigureAwait(false);
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
/// Hot-reloadable options for <see cref="AgentRestoreRetryScheduler"/>. Bound
/// from <c>CodeyBox:AutoRequeueOnAgentRestore</c>.
/// </summary>
public sealed record AgentRestoreRetryOptions
{
    /// <summary>
    /// Master switch. Default <c>false</c> so the sweep is opt-in for the
    /// first ship — operators flip it on after confirming the failure-class
    /// signal is producing the expected partition (infra vs real) in their
    /// audit log.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// How far back from <see cref="AgentRestoredEvent.OutageStartedAt"/> the
    /// sweep looks for candidates. Work items can fail several minutes
    /// before the smoke probe notices the outage — this lookback catches
    /// them. Default 30 minutes.
    /// </summary>
    public TimeSpan LookbackGrace { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How far past <see cref="AgentRestoredEvent.RestoredAt"/> the sweep
    /// also considers as part of the outage window. Default 5 minutes —
    /// guards against ordering races where a failed write outlives the
    /// restore notification by milliseconds.
    /// </summary>
    public TimeSpan PostRestoreMargin { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Per-restore-event cap on how many items the sweep may requeue. A
    /// safety valve against re-enqueuing thousands of items in one burst if
    /// the operator was already running an unrelated cleanup. Default 200.
    /// </summary>
    public int MaxItemsPerRestore { get; init; } = 200;
}

/// <summary>
/// Outcome of one restore-driven sweep. <see cref="Requeued"/> is the count
/// of items the sweep actually transitioned back to Queued; <see cref="Skipped"/>
/// is the count of candidates that matched the filter but were not retried
/// (concurrent state change, per-restore cap reached, retrier-internal
/// reject like an open operator question).
/// </summary>
public readonly record struct AgentRestoreSweepSummary(int Requeued, int Skipped);
