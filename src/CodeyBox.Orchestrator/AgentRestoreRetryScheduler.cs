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
///   store is wired, then falls back to <see cref="WorkItem.Agent"/>, then
///   to the project's default agent for legacy direct-agent rows whose
///   persisted <c>Agent</c> was never stamped.</item>
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
/// <para>Enabled by default. Operators may disable it with the
/// <see cref="AgentRestoreRetryOptions.Enabled"/> flag.</para>
/// </summary>
public sealed class AgentRestoreRetryScheduler : BackgroundService
{
    private static readonly TimeSpan NearTerminalLookback = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan NearTerminalClockSkew = TimeSpan.FromMinutes(1);

    private readonly IWorkItemStore _store;
    private readonly WorkItemRetrier _retrier;
    private readonly IAgentRestoreSignal? _signal;
    private readonly IWebhookDispatcher? _webhooks;
    private readonly IProjectRepository? _projects;
    private readonly IAgentInvolvementStore? _involvement;
    private readonly Func<AgentRestoreRetryOptions> _optionsAccessor;
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
        IAgentInvolvementStore? involvement = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _retrier = retrier ?? throw new ArgumentNullException(nameof(retrier));
        _optionsAccessor = optionsAccessor ?? throw new ArgumentNullException(nameof(optionsAccessor));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _signal = signal;
        _webhooks = webhooks;
        _projects = projects;
        _involvement = involvement;
        if (_signal is not null)
            _signal.AgentRestored += EnqueueEvent;
    }

    private void EnqueueEvent(AgentRestoredEvent evt)
    {
        try
        {
            if (!_events.Writer.TryWrite(evt))
            {
                _log.LogWarning(
                    "AgentRestoreRetryScheduler: restore event queue rejected event for {Agent}; sweep skipped",
                    evt.Agent.Value);
            }
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
            catch (System.Threading.Channels.ChannelClosedException)
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

        var candidateStates = new[]
        {
            WorkItemState.Failed,
            WorkItemState.MergeConflictResolutionFailed,
        };

        foreach (var state in candidateStates)
        {
            await foreach (var item in _store.ListByStateAsync(state, ct))
            {
                if (!await IsCandidateAsync(item, evt.Agent, windowStart, windowEnd, ct).ConfigureAwait(false))
                {
                    continue;
                }

                bool success; string? error; string? actualFrom;
                try
                {
                    (success, error, _, actualFrom, _) = await _retrier.RetryAsync(
                        item, from: null, trigger: "agent-restore", ct).ConfigureAwait(false);
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
                await EmitAutoRetryWebhookAsync(item, evt, actualFrom, ct).ConfigureAwait(false);
            }
        }

        AuditLog.AgentRestoreRequeueSwept(
            evt.Agent, evt.OutageStartedAt, evt.RestoredAt, requeued, skipped);
        await EmitSweepWebhookAsync(evt, requeued, skipped, ct)
            .ConfigureAwait(false);
        return new AgentRestoreSweepSummary(requeued, skipped);
    }

    private async Task<bool> IsCandidateAsync(
        WorkItem item,
        AgentKind restoredAgent,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken ct)
    {
        if (!IsRestoreSweepEligibleFailure(item))
            return false;

        if (item.UpdatedAt < windowStart || item.UpdatedAt > windowEnd)
            return false;

        if (await TryResolveLastFailedInvolvementAgentAsync(item.Id, item.UpdatedAt, ct).ConfigureAwait(false) is { } failedAgent)
            return failedAgent == restoredAgent;

        if (item.Agent is { } itemAgent)
            return itemAgent == restoredAgent;

        if (_projects is not null)
        {
            try
            {
                var project = await _projects.GetAsync(item.ProjectId, ct).ConfigureAwait(false);
                if (project is not null)
                    return project.DefaultAgent == restoredAgent;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex,
                    "AgentRestoreRetryScheduler: project lookup failed for {Id}; cannot infer default agent for restore sweep",
                    item.Id);
            }
        }

        return false;
    }

    private async Task<AgentKind?> TryResolveLastFailedInvolvementAgentAsync(
        WorkItemId id,
        DateTimeOffset terminalUpdatedAt,
        CancellationToken ct)
    {
        if (_involvement is null) return null;

        try
        {
            var rows = await _involvement.ListByWorkItemAsync(id, ct).ConfigureAwait(false);
            return rows
                .Where(row => row.Outcome is not null
                    && row.Outcome.StartsWith("failure:", StringComparison.OrdinalIgnoreCase)
                    && IsNearTerminalUpdate(row.EndedAt ?? row.StartedAt, terminalUpdatedAt))
                .OrderByDescending(static row => row.EndedAt ?? row.StartedAt)
                .FirstOrDefault()
                ?.AgentKind;
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

    private static bool IsNearTerminalUpdate(DateTimeOffset involvementAt, DateTimeOffset terminalUpdatedAt) =>
        involvementAt >= terminalUpdatedAt - NearTerminalLookback
        && involvementAt <= terminalUpdatedAt + NearTerminalClockSkew;

    private static bool IsRestoreSweepEligibleFailure(WorkItem item)
    {
        if (!WorkItemFailureKinds.IsInfraShaped(item.FailureKind))
            return false;

        if (!string.Equals(item.FailureKind, WorkItemFailureKinds.AuthRequired, StringComparison.OrdinalIgnoreCase))
            return true;

        if (item.AuthFailureScope == WorkItemAuthFailureScope.Item)
            return false;

        if (item.AuthFailureScope == WorkItemAuthFailureScope.Fleet)
            return true;

        // Legacy rows from before auth_failure_scope existed only carried the
        // item-vs-fleet distinction in the formatted diagnostic text.
        return !IsLegacyUncorroboratedStdoutOnlyAuthFailure(item.LastError);
    }

    private static bool IsLegacyUncorroboratedStdoutOnlyAuthFailure(string? lastError)
    {
        if (string.IsNullOrWhiteSpace(lastError))
            return false;

        return lastError.Contains("stdout accepted for item failure only", StringComparison.OrdinalIgnoreCase)
            || lastError.Contains("forced in-VM smoke probe did not corroborate auth", StringComparison.OrdinalIgnoreCase)
            || lastError.Contains("item-level failure only, no fleet-wide bench", StringComparison.OrdinalIgnoreCase)
            || lastError.Contains("stdout auth evidence NOT corroborated", StringComparison.OrdinalIgnoreCase);
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

    private async Task EmitAutoRetryWebhookAsync(
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
/// Hot-reloadable options for <see cref="AgentRestoreRetryScheduler"/>. Bound
/// from <c>CodeyBox:AutoRequeueOnAgentRestore</c>.
/// </summary>
public sealed record AgentRestoreRetryOptions
{
    /// <summary>
    /// Master switch. Default <c>true</c>; set false to disable restore-driven
    /// infra-failure sweeps.
    /// </summary>
    public bool Enabled { get; init; } = true;

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
}

/// <summary>
/// Outcome of one restore-driven sweep. <see cref="Requeued"/> is the count
/// of items the sweep actually transitioned back to Queued; <see cref="Skipped"/>
/// is the count of candidates that matched the filter but were not retried
/// (concurrent state change, retrier-internal reject like an open operator question).
/// </summary>
public readonly record struct AgentRestoreSweepSummary(int Requeued, int Skipped);
