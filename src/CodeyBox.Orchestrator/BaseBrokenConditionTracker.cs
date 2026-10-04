using System.Collections.Concurrent;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Coordination point for the project-level "base branch does not build"
/// condition. Writes go through <see cref="IBaseBrokenConditionStore"/> and
/// mirror into an in-memory index so dispatch-pickup checks and the queue
/// status endpoint answer without a database read per candidate. The store
/// is the durable truth; the index is hydrated once from it and then kept
/// coherent because every mutation flows through this singleton.
///
/// <para>Duties:</para>
/// <list type="bullet">
///   <item><see cref="RecordDetectedAsync"/> — register/update an active
///   condition (deduped by base SHA) and emit detection events once.</item>
///   <item><see cref="EnsureFixItemAsync"/> — file ONE highest-priority fix
///   item per broken SHA (deduped via the marker external id and the store's
///   unique (project, namespace, value) constraint), parked for explicit
///   operator approval: it is created in
///   <see cref="WorkItemState.NeedsOperatorInput"/> and never auto-enqueued,
///   so no tool-bearing agent consumes the filed item until an operator
///   reviews the captured failure and retries it. The captured build
///   excerpt lives on the condition record and the item's operator note —
///   never in the agent-consumed prompt.</item>
///   <item><see cref="HoldsBuildPhasesAsync"/> — dispatcher hold check:
///   an active condition exists for the project and the candidate is not
///   the filed fix item.</item>
///   <item><see cref="ClearAsync"/> — clear a condition when the base tip
///   builds again and wake the dispatcher so held items resume.</item>
/// </list>
/// </summary>
public sealed class BaseBrokenConditionTracker : IBaseBrokenConditionStatusProvider
{
    /// <summary>
    /// ExternalIds namespace marking the auto-filed base-fix item; the value
    /// is the broken base tip SHA. The store's (project, namespace, value)
    /// uniqueness makes concurrent filings collapse to one row. Single source
    /// of truth lives in <see cref="Validation.SystemBaseFixExternalIdNamespace"/>;
    /// callers cannot write this namespace (rejected at every caller-facing
    /// validation funnel) and <see cref="IsOpenFixItem"/> additionally
    /// requires system provenance, so a caller-planted marker can neither be
    /// filed nor adopted as the fix item.
    /// </summary>
    public const string FixMarkerNamespace = Validation.SystemBaseFixExternalIdNamespace;

    private readonly IBaseBrokenConditionStore _store;
    private readonly IWorkItemStore _items;
    private readonly ITaskQueue? _queue;
    private readonly IWebhookDispatcher? _webhooks;
    private readonly TimeProvider _time;
    private readonly ILogger<BaseBrokenConditionTracker> _log;

    // Serialize condition mutations + fix-item filing: two workers detecting
    // the same broken SHA must not file duplicate fix items or double-fire
    // detection events.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _hydrated;

    private readonly ConcurrentDictionary<ProjectId, ConcurrentDictionary<string, BaseBrokenCondition>> _active = new();
    private readonly ConcurrentDictionary<WorkItemId, byte> _fixItemIds = new();

    public BaseBrokenConditionTracker(
        IBaseBrokenConditionStore store,
        IWorkItemStore items,
        ITaskQueue? queue = null,
        IWebhookDispatcher? webhooks = null,
        TimeProvider? timeProvider = null,
        ILogger<BaseBrokenConditionTracker>? log = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _queue = queue;
        _webhooks = webhooks;
        _time = timeProvider ?? TimeProvider.System;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<BaseBrokenConditionTracker>.Instance;
    }

    /// <summary>Active-condition snapshot for queue-status reporting.</summary>
    public IReadOnlyList<BaseBrokenCondition> GetActiveConditions() =>
        _active.Values
            .SelectMany(set => set.Values)
            .OrderBy(c => c.DetectedAt)
            .ToList();

    /// <summary>
    /// Hydrates the in-memory index from the durable store on first use
    /// (restart recovery). Idempotent; the write gate serializes it against
    /// mutations so a record/clear cannot interleave with the load.
    /// </summary>
    public async Task HydrateAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _hydrated) != 0)
            return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_hydrated != 0)
                return;
            var active = await _store.ListActiveAsync(ct).ConfigureAwait(false);
            foreach (var condition in active)
                RegisterInMemory(condition);
            Volatile.Write(ref _hydrated, 1);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Dispatcher hold check: true when the candidate's project has an
    /// active base-broken condition and the candidate is not the fix item
    /// filed to repair it. Job types without a build gate are never held.
    /// </summary>
    public async Task<bool> HoldsBuildPhasesAsync(
        WorkItem candidate,
        CancellationToken ct = default)
    {
        // Agent-control and check items have no required-build gate on their
        // path; holding them gains nothing and an AgentControl row parked
        // behind a broken base would strand operator pause/resume actions.
        if (candidate.JobType is JobType.AgentControl or JobType.CheckAndAct)
            return false;
        if (candidate.State is WorkItemState.Merged or WorkItemState.UpstreamPushing)
            return false;

        await HydrateAsync(ct).ConfigureAwait(false);
        return _active.TryGetValue(candidate.ProjectId, out var set)
            && !set.IsEmpty
            && !_fixItemIds.ContainsKey(candidate.Id);
    }

    /// <summary>
    /// Records the broken-base verdict for the project. Returns the
    /// effective condition row: the pre-existing active row when this SHA
    /// was already recorded (no duplicate events), else the newly-inserted
    /// one. Callers check <see cref="BaseBrokenCondition.FixWorkItemId"/> to
    /// decide whether a fix item still needs filing.
    /// </summary>
    public async Task<BaseBrokenCondition> RecordDetectedAsync(
        BaseBrokenCondition condition,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await HydrateCoreAsync(ct).ConfigureAwait(false);
            if (_active.TryGetValue(condition.ProjectId, out var set)
                && set.TryGetValue(condition.BaseSha, out var existing))
            {
                return existing;
            }

            await _store.UpsertAsync(condition, ct).ConfigureAwait(false);
            RegisterInMemory(condition);
        }
        finally
        {
            _gate.Release();
        }

        _log.LogWarning(
            "Base-broken condition detected for project {ProjectId}: base '{Base}' tip {Sha} fails the required build; build-dependent phases held",
            condition.ProjectId.Value, condition.BaseBranch, condition.BaseSha);
        AuditLog.BaseBrokenDetected(condition.ProjectId, condition.BaseBranch, condition.BaseSha, condition.ErrorSummary);
        if (_webhooks is not null)
        {
            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "project.base_broken_detected",
                Details = new
                {
                    projectId = condition.ProjectId.Value,
                    baseBranch = condition.BaseBranch,
                    baseSha = condition.BaseSha,
                },
            }, CancellationToken.None).ConfigureAwait(false);
        }

        return condition;
    }

    /// <summary>
    /// Returns the fix item for the condition, filing one when none exists.
    /// Dedupe order: the condition's recorded fix id, then an open
    /// marker-marked item for the same SHA, then a fresh create — losing a
    /// create race to the unique-external-id constraint collapses back to
    /// the concurrent winner.
    ///
    /// A freshly filed item is parked in
    /// <see cref="WorkItemState.NeedsOperatorInput"/> and is NOT enqueued:
    /// the build log that triggered it is untrusted repo-derived content,
    /// so the repair must not auto-dispatch to a tool-bearing agent. The
    /// agent-consumed prompt carries no build output at all (the agent
    /// reproduces the failure itself); the captured excerpt is available to
    /// the operator on the condition record and the item's note. An
    /// operator approves dispatch by retrying the item, which returns it to
    /// the queue — at which point the dispatch-hold exemption (it is the
    /// repair path) lets it through while ordinary items stay held.
    /// Adopted pre-existing items keep their current state: adoption means
    /// a concurrent filing already owns this SHA.
    /// </summary>
    public async Task<WorkItemId?> EnsureFixItemAsync(
        BaseBrokenCondition condition,
        WorkItem sourceItem,
        int priority,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await HydrateCoreAsync(ct).ConfigureAwait(false);
            if (_active.TryGetValue(condition.ProjectId, out var set)
                && set.TryGetValue(condition.BaseSha, out var current)
                && current.FixWorkItemId is { } recorded)
            {
                return recorded;
            }

            var allItems = new List<WorkItem>();
            await foreach (var item in _items.ListAsync(ct).ConfigureAwait(false))
                allItems.Add(item);

            var existing = FindFixItem(allItems, condition.ProjectId, condition.BaseSha);
            if (existing is not null)
            {
                await _store.AttachFixItemAsync(
                    condition.ProjectId, condition.BaseSha, existing.Id, existing.Id.ToString(), ct)
                    .ConfigureAwait(false);
                RegisterFixItem(condition.ProjectId, condition.BaseSha, existing.Id);
                return existing.Id;
            }

            var now = _time.GetUtcNow();
            var fix = new WorkItem
            {
                Id = WorkItemId.New(),
                ProjectId = condition.ProjectId,
                Title = BaseBrokenFixItemPolicy.BuildTitle(condition.BaseBranch, condition.BaseSha),
                Prompt = BaseBrokenFixItemPolicy.BuildPrompt(
                    condition.BaseBranch,
                    condition.BaseSha,
                    sourceItem.Id),
                BaseBranch = condition.BaseBranch,
                DependsOn = [],
                QueuePosition = now.Ticks,
                Priority = priority,
                PushUpstream = sourceItem.PushUpstream,
                // Parked for explicit operator approval (never auto-enqueued):
                // the triggering build log is untrusted repo-derived content
                // and must not auto-dispatch to a tool-bearing agent. The
                // dispatch-eligible query excludes this state, so no worker
                // picks it up; the operator retry path resumes it to Queued
                // after reviewing the excerpt. The excerpt lives on LastError
                // (operator surface, never composed into an agent prompt).
                State = WorkItemState.NeedsOperatorInput,
                LastError = BaseBrokenFixItemPolicy.BuildOperatorNote(
                    condition.BaseBranch,
                    condition.BaseSha,
                    condition.ErrorSummary),
                // System provenance: caller-facing creation always stamps a
                // non-null initiator (server-resolved from auth), so a null
                // initiator marks this row as orchestrator-filed. IsOpenFixItem
                // requires it — a caller-planted marker row carries the
                // caller's initiator and is never adopted as the fix item.
                Initiator = null,
                ExternalIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [FixMarkerNamespace] = condition.BaseSha,
                },
            };

            try
            {
                await _items.CreateAsync(fix, ct).ConfigureAwait(false);
            }
            catch (WorkItemExternalIdConflictException)
            {
                // Usually a concurrent detector filed the fix for this SHA
                // first — adopt it instead of duplicating.
                var winner = FindFixItem(allItems, condition.ProjectId, condition.BaseSha)
                    ?? await ScanForFixItemAsync(condition, ct).ConfigureAwait(false);
                if (winner is not null)
                {
                    await _store.AttachFixItemAsync(
                        condition.ProjectId, condition.BaseSha, winner.Id, winner.Id.ToString(), ct)
                        .ConfigureAwait(false);
                    RegisterFixItem(condition.ProjectId, condition.BaseSha, winner.Id);
                    return winner.Id;
                }

                // Otherwise the conflicting row carries the reserved marker
                // without system provenance (caller-planted before the
                // write-time reservation): it can never be adopted, and
                // leaving it in place would suppress the genuine repair item
                // via the unique (project, namespace, value) constraint.
                // Reclaim the reserved namespace from that row, then file.
                if (!await ReclaimReservedMarkerAsync(condition, ct).ConfigureAwait(false))
                    throw;
                try
                {
                    await _items.CreateAsync(fix, ct).ConfigureAwait(false);
                }
                catch (WorkItemExternalIdConflictException)
                {
                    // A concurrent genuine filing landed between the reclaim
                    // and the retry — adopt it when visible, else surface.
                    var retryWinner = await ScanForFixItemAsync(condition, ct).ConfigureAwait(false);
                    if (retryWinner is null)
                        throw;
                    await _store.AttachFixItemAsync(
                        condition.ProjectId, condition.BaseSha, retryWinner.Id, retryWinner.Id.ToString(), ct)
                        .ConfigureAwait(false);
                    RegisterFixItem(condition.ProjectId, condition.BaseSha, retryWinner.Id);
                    return retryWinner.Id;
                }
            }

            await _store.AttachFixItemAsync(
                condition.ProjectId, condition.BaseSha, fix.Id, fix.Id.ToString(), ct)
                .ConfigureAwait(false);
            RegisterFixItem(condition.ProjectId, condition.BaseSha, fix.Id);
            AuditLog.WorkItemCreated(fix.Id, fix.ProjectId, fix.Title, fix.Initiator);
            _log.LogWarning(
                "Auto-filed base-fix item {FixId} for project {ProjectId} (base '{Base}' tip {Sha}); held for operator approval, not enqueued",
                fix.Id, condition.ProjectId.Value, condition.BaseBranch, condition.BaseSha);

            return fix.Id;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Marks a condition cleared (base tip builds again), removes the hold,
    /// and wakes the dispatcher so held items resume. No-op when the
    /// (project, sha) row is not active.
    /// </summary>
    public async Task ClearAsync(
        ProjectId projectId,
        string baseSha,
        CancellationToken ct = default)
    {
        string? baseBranch = null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await HydrateCoreAsync(ct).ConfigureAwait(false);
            await _store.ClearAsync(projectId, baseSha, _time.GetUtcNow(), ct).ConfigureAwait(false);
            if (_active.TryGetValue(projectId, out var set)
                && set.TryRemove(baseSha, out var removed))
            {
                baseBranch = removed.BaseBranch;
                if (removed.FixWorkItemId is { } fixId)
                    _fixItemIds.TryRemove(fixId, out _);
            }
            else
            {
                return;
            }
        }
        finally
        {
            _gate.Release();
        }

        _log.LogWarning(
            "Base-broken condition cleared for project {ProjectId}: base '{Base}' tip {Sha} builds again; resuming held items",
            projectId.Value, baseBranch ?? "(unknown)", baseSha);
        AuditLog.BaseBrokenCleared(projectId, baseBranch ?? "(unknown)", baseSha);
        if (_webhooks is not null)
        {
            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "project.base_broken_cleared",
                Details = new
                {
                    projectId = projectId.Value,
                    baseBranch,
                    baseSha,
                },
            }, CancellationToken.None).ConfigureAwait(false);
        }
        if (_queue is not null)
            await _queue.EnqueueDispatchWakeAsync(ct).ConfigureAwait(false);
    }

    private async Task HydrateCoreAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _hydrated) != 0)
            return;
        var active = await _store.ListActiveAsync(ct).ConfigureAwait(false);
        foreach (var condition in active)
            RegisterInMemory(condition);
        Volatile.Write(ref _hydrated, 1);
    }

    private void RegisterInMemory(BaseBrokenCondition condition)
    {
        var set = _active.GetOrAdd(
            condition.ProjectId,
            _ => new ConcurrentDictionary<string, BaseBrokenCondition>(StringComparer.Ordinal));
        set[condition.BaseSha] = condition;
        if (condition.FixWorkItemId is { } fixId)
            _fixItemIds[fixId] = 0;
    }

    private void RegisterFixItem(ProjectId projectId, string baseSha, WorkItemId fixId)
    {
        if (_active.TryGetValue(projectId, out var set)
            && set.TryGetValue(baseSha, out var condition))
        {
            set[baseSha] = condition with { FixWorkItemId = fixId, RepositoryId = fixId.ToString() };
        }
        _fixItemIds[fixId] = 0;
    }

    private async Task<WorkItem?> ScanForFixItemAsync(
        BaseBrokenCondition condition,
        CancellationToken ct)
    {
        await foreach (var item in _items.ListAsync(ct).ConfigureAwait(false))
        {
            if (IsOpenFixItem(item, condition.ProjectId, condition.BaseSha))
                return item;
        }
        return null;
    }

    /// <summary>
    /// Removes the reserved fix marker from the row currently holding it
    /// when that row fails fix-item provenance (caller-planted, never
    /// adoptable), so the genuine fix item can be filed. Returns false when
    /// there is nothing reclaimable: no holding row, or the holder is a
    /// genuine system row the caller should adopt instead (a concurrent
    /// filing that landed after the scan). Only the reserved namespace is
    /// removed — every other external id on the row is preserved.
    /// </summary>
    private async Task<bool> ReclaimReservedMarkerAsync(
        BaseBrokenCondition condition,
        CancellationToken ct)
    {
        var blocker = await _items.GetByNamespacedExternalIdAsync(
            condition.ProjectId, FixMarkerNamespace, condition.BaseSha, ct).ConfigureAwait(false);
        if (blocker is null || IsOpenFixItem(blocker, condition.ProjectId, condition.BaseSha))
            return false;
        var cleaned = new Dictionary<string, string>(blocker.ExternalIds, StringComparer.OrdinalIgnoreCase);
        if (!cleaned.Remove(FixMarkerNamespace))
            return false;
        var updated = await _items.ReplaceExternalIdsAsync(
            blocker.Id, cleaned, _time.GetUtcNow(), ct).ConfigureAwait(false);
        if (updated is null)
            return false;
        _log.LogWarning(
            "Reclaimed reserved '{Namespace}' marker from work item {BlockerId} for project {ProjectId} (base tip {Sha}); the marker lacked system provenance",
            FixMarkerNamespace, blocker.Id, condition.ProjectId.Value, condition.BaseSha);
        return true;
    }

    internal static WorkItem? FindFixItem(
        IReadOnlyList<WorkItem> allItems,
        ProjectId projectId,
        string baseSha)
    {
        foreach (var item in allItems)
        {
            if (IsOpenFixItem(item, projectId, baseSha))
                return item;
        }
        return null;
    }

    /// <summary>
    /// True for an open (non-terminal) item in this project carrying this
    /// SHA under the fix marker. Provenance-guarded: only orchestrator-filed
    /// rows qualify — caller-facing creation always stamps a non-null
    /// initiator, so a null initiator plus a well-formed base-SHA marker
    /// value (exact ordinal match, commit-SHA shape) marks system provenance.
    /// A caller-planted <c>base-fix</c> marker (rejected at write time going
    /// forward, but possibly present in pre-reservation rows) never
    /// satisfies this and is therefore never adopted as the fix item nor
    /// exempted from the dispatch hold.
    /// </summary>
    private static bool IsOpenFixItem(WorkItem item, ProjectId projectId, string baseSha) =>
        item.ProjectId == projectId
        && item.Initiator is null
        && !WorkItemDependencies.TerminalStates.Contains(item.State)
        && item.ExternalIds.TryGetValue(FixMarkerNamespace, out var sha)
        && IsWellFormedBaseShaMarker(sha)
        && string.Equals(sha, baseSha, StringComparison.Ordinal);

    private static bool IsWellFormedBaseShaMarker(string? sha)
    {
        if (string.IsNullOrEmpty(sha) || sha.Length is < 40 or > 64)
            return false;
        foreach (var c in sha)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }
        return true;
    }
}
