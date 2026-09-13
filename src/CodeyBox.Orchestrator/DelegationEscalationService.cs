using System.Diagnostics;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Outcome of a delegation trigger attempt.
/// </summary>
public sealed record DelegationTriggerResult(
    bool Delegated,
    WorkItem? Item,
    string? Error)
{
    public static DelegationTriggerResult Ok(WorkItem item) => new(true, item, null);
    public static DelegationTriggerResult Refused(string error) => new(false, null, error);
}

/// <summary>
/// Single home for every transition into the delegation phase: the operator
/// delegate command and both automatic-escalation conditions. The transition
/// is history-preserving (audit progress, stream summaries, and failure
/// counters ride along untouched) so the convergence brief composed at turn
/// start still sees the evidence that motivated the delegation, and the
/// failure signal is never consumed by the escalation.
/// <para>
/// Delegated items compete for the same worker and sandbox capacity as normal
/// work: the transition keeps the item's priority, stamps an explicit
/// end-of-queue position, and wakes the shared dispatcher through the normal
/// queue kick. There is no reserved lane, no priority boost, and no separate
/// pool, so delegation cannot starve normal dispatch beyond one fairly
/// ordered turn per trigger (automatic escalation additionally fires at most
/// once per item).
/// </summary>
public sealed class DelegationEscalationService
{
    private const int MaxPreservedErrorChars = 1000;

    private readonly IWorkItemStore _store;
    private readonly ITaskQueue? _queue;
    private readonly Func<DelegationEscalationOptions> _optionsAccessor;
    private readonly IWebhookDispatcher? _webhooks;
    private readonly IProjectRepository? _projects;
    private readonly TimeProvider _time;
    private readonly ILogger<DelegationEscalationService> _log;

    public DelegationEscalationService(
        IWorkItemStore store,
        ITaskQueue? queue = null,
        Func<DelegationEscalationOptions>? optionsAccessor = null,
        IWebhookDispatcher? webhooks = null,
        IProjectRepository? projects = null,
        TimeProvider? timeProvider = null,
        ILogger<DelegationEscalationService>? log = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _queue = queue;
        _optionsAccessor = optionsAccessor ?? (() => new DelegationEscalationOptions());
        _webhooks = webhooks;
        _projects = projects;
        _time = timeProvider ?? TimeProvider.System;
        _log = log ?? NullLogger<DelegationEscalationService>.Instance;
    }

    private DelegationEscalationOptions CurrentOptions
    {
        get
        {
            try { return _optionsAccessor(); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to read live delegation-escalation options; using defaults");
                return new DelegationEscalationOptions();
            }
        }
    }

    /// <summary>
    /// Whether the automatic condition <paramref name="autoTrigger"/> is
    /// armed for <paramref name="item"/> right now: the master switch and the
    /// per-condition toggle are on, the item has not already escalated
    /// automatically, no delegation turn has failed it, and (for the
    /// repeated-failure condition) the terminal-failure episode count reached
    /// the configured threshold. Pure check against live options and the
    /// item snapshot; performs no writes.
    /// </summary>
    public bool IsAutoTriggerArmed(string autoTrigger, WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var opts = CurrentOptions;
        if (!opts.Enabled)
            return false;
        if (string.Equals(autoTrigger, DelegationTriggers.AuditMaxIterations, StringComparison.Ordinal))
            return opts.OnAuditMaxIterations && DelegationEscalationPolicy.CanAutoEscalate(item);
        if (string.Equals(autoTrigger, DelegationTriggers.RepeatedTerminalFailure, StringComparison.Ordinal))
            return opts.OnRepeatedTerminalFailure
                && DelegationEscalationPolicy.CanAutoEscalate(item)
                && item.TerminalFailureCount >= Math.Max(1, opts.RepeatedTerminalFailureThreshold);
        return false;
    }

    /// <summary>
    /// Transitions <paramref name="item"/> into <see cref="WorkItemState.Delegating"/>
    /// for <paramref name="trigger"/> (one of <see cref="DelegationTriggers"/>),
    /// stamping the one-shot trigger flag, the attribution reason, and — for
    /// the operator trigger — <paramref name="note"/>. When
    /// <paramref name="markAutoEscalated"/> is set, the item's single
    /// automatic escalation is consumed.
    /// <para>
    /// Refuses (rather than throwing) when the trigger label is unknown, the
    /// item sits in a non-delegable state, an automatic escalation is no
    /// longer eligible, or the row advanced concurrently.
    /// </summary>
    /// <param name="failureContext">Failure description preserved into
    /// <c>LastError</c> alongside the escalation record so the underlying
    /// defect stays visible (audit-max park text, terminal error, …).</param>
    public async Task<DelegationTriggerResult> DelegateAsync(
        WorkItem item,
        string trigger,
        string? note,
        bool markAutoEscalated,
        string? failureContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!IsKnownTrigger(trigger))
            return DelegationTriggerResult.Refused($"unknown delegation trigger '{trigger}'");

        var current = await _store.GetAsync(item.Id, ct).ConfigureAwait(false) ?? item;
        if (!DelegationEscalationPolicy.IsDelegableState(current.State))
            return DelegationTriggerResult.Refused(
                $"cannot delegate item in state {current.State}; only non-terminal states and terminal failure states can be delegated");

        if (markAutoEscalated && !DelegationEscalationPolicy.CanAutoEscalate(current))
            return DelegationTriggerResult.Refused(
                "item is not eligible for automatic escalation: it already escalated automatically or a delegation turn already failed it");

        var opts = CurrentOptions;
        var safeNote = BoundNote(note, opts.MaxNoteChars);
        var now = _time.GetUtcNow();
        var reason = $"Delegation requested by '{trigger}' from '{current.State}'.";
        var delegated = current.With(WorkItemState.Delegating, BuildLastError(trigger, current, failureContext)) with
        {
            DelegationRequested = true,
            DelegationReason = reason,
            DelegationNote = safeNote,
            // Explicit end-of-queue position: the delegated turn competes
            // through the normal priority/creation-time pickup ordering, and
            // any later return to Queued sorts behind items already waiting
            // rather than inheriting a stale reorder slot.
            QueuePosition = now.Ticks,
            // Fresh dispatch: a stale StartedAt would misreport the item as
            // in-flight to CountInFlightAsync before the pipeline picks it up.
            StartedAt = null,
            AutoDelegationEscalated = current.AutoDelegationEscalated || markAutoEscalated,
        };

        var updated = await _store.TryUpdateIfStateAsync(delegated, current.State, ct).ConfigureAwait(false);
        if (!updated)
            return DelegationTriggerResult.Refused("work item state changed concurrently; delegation aborted");

        if (_queue is not null)
        {
            try
            {
                await _queue.EnqueueAsync(delegated.Id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex,
                    "Delegation of work item {Id} updated state to Delegating but queue kick failed; rolling back",
                    delegated.Id);
                var rolledBack = false;
                try
                {
                    rolledBack = await _store.TryUpdateIfStateAsync(current, WorkItemState.Delegating, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception rollbackEx)
                {
                    _log.LogError(rollbackEx,
                        "Failed to roll back work item {Id} after delegation queue kick failed",
                        delegated.Id);
                }

                return rolledBack
                    ? DelegationTriggerResult.Refused($"queue enqueue failed after state update; rolled back to {current.State}: {ex.Message}")
                    : DelegationTriggerResult.Refused($"queue enqueue failed after state update and rollback did not apply: {ex.Message}");
            }
        }

        CodeyBoxMeters.DelegationCounts.Add(1,
            new KeyValuePair<string, object?>("trigger", trigger));
        AuditLog.WorkItemTransitioned(
            delegated.Id,
            $"Delegating (delegated by '{trigger}' from {current.State})");
        await PublishDelegatedAsync(delegated, current, trigger, safeNote, ct).ConfigureAwait(false);
        return DelegationTriggerResult.Ok(delegated);
    }

    private static bool IsKnownTrigger(string trigger) =>
        string.Equals(trigger, DelegationTriggers.Operator, StringComparison.Ordinal)
        || string.Equals(trigger, DelegationTriggers.AuditMaxIterations, StringComparison.Ordinal)
        || string.Equals(trigger, DelegationTriggers.RepeatedTerminalFailure, StringComparison.Ordinal);

    private static string? BoundNote(string? note, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(note))
            return null;
        var cap = Math.Clamp(maxChars, 1, 1_000_000);
        var trimmed = note.Trim();
        return trimmed.Length <= cap ? trimmed : trimmed[..cap];
    }

    private static string BuildLastError(string trigger, WorkItem current, string? failureContext)
    {
        var header = string.Equals(trigger, DelegationTriggers.Operator, StringComparison.Ordinal)
            ? $"Delegated by operator from {current.State}."
            : $"Escalated to delegation ({trigger}) from {current.State}.";
        var preserved = !string.IsNullOrWhiteSpace(failureContext)
            ? failureContext
            : current.LastError;
        if (string.IsNullOrWhiteSpace(preserved))
            return header;
        var clipped = preserved.Length <= MaxPreservedErrorChars
            ? preserved.Trim()
            : preserved.Trim()[..MaxPreservedErrorChars];
        return $"{header} Previous failure: {clipped}";
    }

    private async Task PublishDelegatedAsync(
        WorkItem delegated,
        WorkItem prior,
        string trigger,
        string? note,
        CancellationToken ct)
    {
        if (_webhooks is null)
            return;
        try
        {
            Project? project = null;
            if (_projects is not null)
            {
                try { project = await _projects.GetAsync(delegated.ProjectId, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogDebug(ex, "Failed to resolve project for delegation webhook; publishing without it");
                }
            }

            project ??= new Project
            {
                Id = delegated.ProjectId,
                DisplayName = delegated.ProjectId.Value,
                RepositoryUrl = string.Empty,
            };
            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "work_item.delegated",
                WorkItem = delegated,
                Project = project,
                Details = new
                {
                    trigger,
                    priorState = prior.State.ToString(),
                    reason = delegated.DelegationReason,
                    note,
                    terminalFailureCount = delegated.TerminalFailureCount,
                    autoEscalated = delegated.AutoDelegationEscalated,
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
                "Work item {Id} delegated by '{Trigger}', but delegation webhook delivery failed",
                delegated.Id, trigger);
        }
    }
}
