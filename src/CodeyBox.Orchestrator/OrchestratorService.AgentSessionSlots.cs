using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

// OrchestratorService.AgentSessionSlots.cs — per-route agent CLI session accounting.
//
// Every agent CLI session counts against the route's configured MaxConcurrent:
// work turns (work, rework, delegation, conflict resolution) hold the dispatch
// reservation; each LLM auditor session takes an audit reservation through the
// same gate. The audit path uses release-before-acquire for the item's work
// slot (SuspendWorkSlotForAudit / ResumeWorkSlotAfterAuditAsync) so a cap-1
// agent cannot self-deadlock an item waiting on its own audit session.
public sealed partial class OrchestratorService
{
    // Guards _routeSessions, _itemWorkSlots, and every waiter queue. Contention
    // is bounded by worker count (per-item pickup/release/fan-out), so a single
    // lock beats the former lock-free CAS now that admission must evaluate a
    // compound predicate (work + audit < cap, audit < audit sub-cap) and feed
    // async waiters — neither expressible as a single TryUpdate.
    private readonly object _sessionSlotGate = new();
    private readonly Dictionary<string, RouteSessionCounts> _routeSessions = new(StringComparer.OrdinalIgnoreCase);

    // The dispatch-time work reservation per in-flight item. PipelineRunner
    // suspends this slot around the LLM auditor fan-out (release-before-
    // acquire) and resumes it before any rework turn; the worker's outer
    // finally releases whatever is still held. Only the item's own pipeline
    // suspends/resumes, and the finally runs after RunAsync returns, so the
    // Held flag is never contested — the lock is for readers and the
    // cap-accounting below, not ordering.
    private readonly Dictionary<WorkItemId, ItemWorkSlot> _itemWorkSlots = new();

    private sealed class RouteSessionCounts
    {
        public int Work;
        public int Audit;
        // Queued work/audit acquires blocked on this route's cap. Lazily
        // allocated — most routes never wait.
        public LinkedList<SessionSlotWaiter>? Waiters;
    }

    private sealed class ItemWorkSlot(string routeKey)
    {
        public string RouteKey { get; } = routeKey;

        /// <summary>False between SuspendWorkSlotForAudit and a completed
        /// ResumeWorkSlotAfterAuditAsync — the slot is genuinely free while
        /// suspended, which is what makes the audit acquire deadlock-free.</summary>
        public bool Held { get; set; } = true;
    }

    private sealed class SessionSlotWaiter
    {
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public required AgentSessionKind Kind { get; init; }
        public CancellationTokenRegistration Registration { get; set; }
        public Task Task => _tcs.Task;
        public bool IsCompleted => _tcs.Task.IsCompleted;

        public bool TrySetResult()
        {
            var ok = _tcs.TrySetResult();
            Registration.Dispose();
            return ok;
        }

        public bool TrySetCanceled()
        {
            var ok = _tcs.TrySetCanceled();
            Registration.Dispose();
            return ok;
        }
    }

    // ── Cap resolution (hot-reloadable: reads the shared snapshot live) ─────

    /// <summary>
    /// Per-agent cap configured for <paramref name="agent"/>, or 0 when no cap
    /// is configured ("unlimited within global pool"). Values &lt;= 0 in the
    /// stored entry are rejected at load by
    /// <see cref="AgentConcurrencyOptions.ValidateAndThrow"/>; the &gt; 0
    /// guard here is defence-in-depth for hand-built options in tests.
    /// </summary>
    internal int GetAgentCap(AgentKind agent)
    {
        var opts = _concurrencySnapshot.Current;
        return opts.Members.TryGetValue(agent.Value, out var entry) && entry is { MaxConcurrent: > 0 }
            ? entry.MaxConcurrent
            : 0;
    }

    internal int GetAgentCap(AgentMembership member)
    {
        var opts = _concurrencySnapshot.Current;
        if (opts.Members.TryGetValue(member.RouteKey, out var exact) && exact is { MaxConcurrent: > 0 })
            return exact.MaxConcurrent;
        return opts.Members.TryGetValue(member.Agent.Value, out var entry) && entry is { MaxConcurrent: > 0 }
            ? entry.MaxConcurrent
            : 0;
    }

    private int GetAgentCapForRoute(AgentKind agent, string routeKey)
    {
        var opts = _concurrencySnapshot.Current;
        if (opts.Members.TryGetValue(routeKey, out var exact) && exact is { MaxConcurrent: > 0 })
            return exact.MaxConcurrent;
        return opts.Members.TryGetValue(agent.Value, out var byKind) && byKind is { MaxConcurrent: > 0 }
            ? byKind.MaxConcurrent
            : 0;
    }

    /// <summary>
    /// Effective audit sub-cap for a route: the entry's
    /// <c>MaxConcurrentAuditSessions</c> when set, else its effective
    /// <c>MaxConcurrent</c>, else 0 (uncapped). Route-key entries win over the
    /// bare-kind fallback, matching the work-slot cap lookup.
    /// </summary>
    private int GetAuditCapForRoute(string routeKey)
    {
        var opts = _concurrencySnapshot.Current;
        if (!TryGetCapEntry(opts, routeKey, out var entry))
            return 0;
        if (entry.MaxConcurrentAuditSessions is { } auditCap)
            return auditCap;
        return entry.MaxConcurrent > 0 ? entry.MaxConcurrent : 0;
    }

    private static bool TryGetCapEntry(
        AgentConcurrencyOptions opts,
        string routeKey,
        out AgentConcurrencyEntry entry)
    {
        if (opts.Members.TryGetValue(routeKey, out var exact) && exact is not null)
        {
            entry = exact;
            return true;
        }
        var kind = AgentInstanceIds.KindFromRouteKey(routeKey);
        if (opts.Members.TryGetValue(kind, out var byKind) && byKind is not null)
        {
            entry = byKind;
            return true;
        }
        entry = null!;
        return false;
    }

    // ── Admission ───────────────────────────────────────────────────────────

    // Work: a session may start while the route's TOTAL sessions (work plus
    // audit) is below MaxConcurrent — audit sessions are real provider
    // sessions and must count against the same ceiling.
    private bool CanAdmitWorkLocked(string routeKey, RouteSessionCounts counts)
    {
        var cap = GetAgentCapForRoute(new AgentKind(AgentInstanceIds.KindFromRouteKey(routeKey)), routeKey);
        return cap <= 0 || counts.Work + counts.Audit < cap;
    }

    // Audit: same total ceiling, plus the audit sub-cap so a burst of auditor
    // sessions cannot starve work turns on a tight cap.
    private bool CanAdmitAuditLocked(string routeKey, RouteSessionCounts counts)
    {
        var auditCap = GetAuditCapForRoute(routeKey);
        if (auditCap > 0 && counts.Audit >= auditCap)
            return false;
        return CanAdmitWorkLocked(routeKey, counts);
    }

    private RouteSessionCounts GetOrAddRouteLocked(string routeKey)
    {
        if (!_routeSessions.TryGetValue(routeKey, out var counts))
            _routeSessions[routeKey] = counts = new RouteSessionCounts();
        return counts;
    }

    private static void IncrementLocked(RouteSessionCounts counts, AgentSessionKind kind)
    {
        if (kind == AgentSessionKind.Work) counts.Work++;
        else counts.Audit++;
    }

    private static void DecrementLocked(RouteSessionCounts counts, AgentSessionKind kind)
    {
        if (kind == AgentSessionKind.Work) counts.Work--;
        else counts.Audit--;
    }

    private void PruneRouteIfDrainedLocked(string routeKey, RouteSessionCounts counts)
    {
        // Drop the key when it is fully drained so snapshots stay tight — the
        // previous implementation removed zero-valued keys for the same
        // reason. Cancelled waiters that were never woken are swept here too;
        // they are already completed so discarding them is safe.
        if (counts.Work > 0 || counts.Audit > 0)
            return;
        if (counts.Waiters is not null)
        {
            var node = counts.Waiters.First;
            while (node is not null)
            {
                if (!node.Value.IsCompleted) return;
                node = node.Next;
            }
        }
        _routeSessions.Remove(routeKey);
    }

    // ── Work reservations (IAgentSlotGate) ──────────────────────────────────

    /// <summary>
    /// <see cref="IAgentSlotGate.TryReserve"/> implementation. Atomically
    /// reserves a work slot for <paramref name="agent"/> when the route's
    /// total sessions (work + audit) is under its configured cap; returns
    /// false at ceiling so the router can spill to another member.
    /// </summary>
    public bool TryReserve(AgentKind agent)
    {
        lock (_sessionSlotGate)
        {
            var counts = GetOrAddRouteLocked(agent.Value);
            if (!CanAdmitWorkLocked(agent.Value, counts))
                return false;
            counts.Work++;
            return true;
        }
    }

    /// <inheritdoc />
    public bool TryReserve(AgentMembership member)
    {
        lock (_sessionSlotGate)
        {
            var counts = GetOrAddRouteLocked(member.RouteKey);
            if (!CanAdmitWorkLocked(member.RouteKey, counts))
                return false;
            counts.Work++;
            return true;
        }
    }

    /// <summary>Direct-reservation path for unrouted items; same admission rule.</summary>
    private bool TryReserveRoute(string routeKey)
    {
        lock (_sessionSlotGate)
        {
            var counts = GetOrAddRouteLocked(routeKey);
            if (!CanAdmitWorkLocked(routeKey, counts))
                return false;
            counts.Work++;
            return true;
        }
    }

    /// <summary>
    /// <see cref="IAgentSlotGate.Release"/> implementation. Frees a work slot
    /// and wakes both queued session-slot waiters and cap-deferred items.
    /// </summary>
    public void Release(AgentKind agent) => ReleaseWorkRoute(agent.Value);

    /// <inheritdoc />
    public void Release(AgentMembership member) => ReleaseWorkRoute(member.RouteKey);

    private void ReleaseWorkRoute(string routeKey)
    {
        if (TryReleaseSessionSlot(routeKey, AgentSessionKind.Work))
            WakeAgentCapWaitersForRouteRelease(routeKey);
    }

    // Returns true when a real in-flight count dropped — the only outcome that
    // frees capacity, so it is the only outcome that wakes waiters.
    private bool TryReleaseSessionSlot(string routeKey, AgentSessionKind kind)
    {
        lock (_sessionSlotGate)
        {
            if (!_routeSessions.TryGetValue(routeKey, out var counts))
                return false;
            if (kind == AgentSessionKind.Work ? counts.Work <= 0 : counts.Audit <= 0)
                return false;
            DecrementLocked(counts, kind);
            PruneRouteIfDrainedLocked(routeKey, counts);
        }
        DrainSessionSlotWaiters(routeKey);
        return true;
    }

    // ── Audit reservations (IAgentSessionSlotGate) ──────────────────────────

    /// <inheritdoc />
    public bool TryReserveAudit(AgentMembership member) => TryReserveAuditRoute(member.RouteKey);

    /// <inheritdoc />
    public bool TryReserveAudit(AgentKind agent) => TryReserveAuditRoute(agent.Value);

    private bool TryReserveAuditRoute(string routeKey)
    {
        lock (_sessionSlotGate)
        {
            var counts = GetOrAddRouteLocked(routeKey);
            if (!CanAdmitAuditLocked(routeKey, counts))
                return false;
            counts.Audit++;
            return true;
        }
    }

    /// <inheritdoc />
    public void ReleaseAudit(AgentMembership member) => ReleaseAuditRoute(member.RouteKey);

    /// <inheritdoc />
    public void ReleaseAudit(AgentKind agent) => ReleaseAuditRoute(agent.Value);

    private void ReleaseAuditRoute(string routeKey)
    {
        if (TryReleaseSessionSlot(routeKey, AgentSessionKind.Audit))
            WakeAgentCapWaitersForRouteRelease(routeKey);
    }

    /// <inheritdoc />
    public Task WaitForAuditSlotAsync(AgentMembership member, CancellationToken ct) =>
        WaitForSessionSlotAsync(member.RouteKey, AgentSessionKind.Audit, ct);

    /// <inheritdoc />
    public Task WaitForAuditSlotAsync(AgentKind agent, CancellationToken ct) =>
        WaitForSessionSlotAsync(agent.Value, AgentSessionKind.Audit, ct);

    /// <summary>
    /// True async wait for a session slot on <paramref name="routeKey"/>.
    /// The permit is counted at grant time (inside the lock), so a waiter
    /// cancelled between grant and completion has its admission reverted —
    /// the same race ResizableConcurrencyGate closes for the global pool.
    /// </summary>
    private Task WaitForSessionSlotAsync(string routeKey, AgentSessionKind kind, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SessionSlotWaiter waiter;
        lock (_sessionSlotGate)
        {
            var counts = GetOrAddRouteLocked(routeKey);
            if (CanAdmitLocked(routeKey, counts, kind))
            {
                IncrementLocked(counts, kind);
                return Task.CompletedTask;
            }
            waiter = new SessionSlotWaiter { Kind = kind };
            (counts.Waiters ??= new LinkedList<SessionSlotWaiter>()).AddLast(waiter);
        }
        if (ct.CanBeCanceled)
        {
            waiter.Registration = ct.Register(static state =>
            {
                var w = (SessionSlotWaiter)state!;
                w.TrySetCanceled();
            }, waiter);
        }
        return waiter.Task;
    }

    private bool CanAdmitLocked(string routeKey, RouteSessionCounts counts, AgentSessionKind kind) =>
        kind == AgentSessionKind.Audit
            ? CanAdmitAuditLocked(routeKey, counts)
            : CanAdmitWorkLocked(routeKey, counts);

    /// <summary>
    /// Admits every queued waiter on <paramref name="routeKey"/> whose session
    /// kind currently fits. Scan order is enqueue order but blocked waiters
    /// are skipped — a work waiter behind an audit-sub-cap-blocked audit
    /// waiter still gets admitted, so neither kind can head-of-line starve
    /// the other.
    /// </summary>
    private void DrainSessionSlotWaiters(string routeKey)
    {
        while (true)
        {
            List<SessionSlotWaiter>? admitted = null;
            lock (_sessionSlotGate)
            {
                if (!_routeSessions.TryGetValue(routeKey, out var counts))
                    return;
                if (counts.Waiters is not null)
                {
                    var node = counts.Waiters.First;
                    while (node is not null)
                    {
                        var next = node.Next;
                        var waiter = node.Value;
                        if (waiter.IsCompleted)
                        {
                            counts.Waiters.Remove(node);
                        }
                        else if (CanAdmitLocked(routeKey, counts, waiter.Kind))
                        {
                            IncrementLocked(counts, waiter.Kind);
                            counts.Waiters.Remove(node);
                            (admitted ??= new List<SessionSlotWaiter>()).Add(waiter);
                        }
                        node = next;
                    }
                }
                PruneRouteIfDrainedLocked(routeKey, counts);
            }
            if (admitted is null)
                return;
            var anyRejected = false;
            foreach (var waiter in admitted)
            {
                if (waiter.TrySetResult())
                    continue;
                // Cancelled between the locked admission scan and the wake:
                // hand the permit back and loop so another waiter can take it.
                RevertAdmission(routeKey, waiter.Kind);
                anyRejected = true;
            }
            if (!anyRejected)
                return;
        }
    }

    private void RevertAdmission(string routeKey, AgentSessionKind kind)
    {
        lock (_sessionSlotGate)
        {
            if (!_routeSessions.TryGetValue(routeKey, out var counts))
                return;
            DecrementLocked(counts, kind);
            PruneRouteIfDrainedLocked(routeKey, counts);
        }
    }

    /// <summary>
    /// Re-evaluates every route's queued waiters after the cap snapshot was
    /// swapped (a raised or removed cap can admit waiters that were blocked
    /// under the old ceiling). Called from
    /// <see cref="ApplyAgentConcurrencyReload"/>.
    /// </summary>
    private void DrainAllSessionSlotWaiters()
    {
        string[] routes;
        lock (_sessionSlotGate)
            routes = _routeSessions.Keys.ToArray();
        foreach (var route in routes)
            DrainSessionSlotWaiters(route);
    }

    // ── Per-item work-slot suspension (release-before-acquire) ──────────────

    /// <summary>
    /// Binds <paramref name="item"/>'s dispatch-time work reservation to its
    /// work item id so the pipeline can suspend/resume it around the LLM
    /// auditor fan-out. Called immediately after the reservation succeeds at
    /// either pickup reservation site.
    /// </summary>
    private void RegisterItemWorkSlot(WorkItemId item, string routeKey)
    {
        lock (_sessionSlotGate)
        {
            // A still-registered entry for the same item would mean a second
            // reservation without a release — the route count behind it leaks
            // because only the newest route key is released. Unreachable under
            // _activeItems pickup discipline; warn rather than silently leak.
            if (_itemWorkSlots.TryGetValue(item, out var existing) && existing.Held)
                _log.LogWarning(
                    "Work item {Id}: overwriting a held work-slot registration ({OldRoute} -> {NewRoute}); the prior route's count is orphaned",
                    item, existing.RouteKey, routeKey);
            _itemWorkSlots[item] = new ItemWorkSlot(routeKey);
        }
    }

    /// <summary>
    /// <see cref="IAgentSessionSlotGate.SuspendWorkSlotForAudit"/> — frees the
    /// item's held work slot so its own audit sessions can be admitted under
    /// a tight cap (cap 1 self-deadlock otherwise). The freed capacity is
    /// real: other items' sessions may take it while the audit runs.
    /// </summary>
    public bool SuspendWorkSlotForAudit(WorkItemId item)
    {
        string routeKey;
        lock (_sessionSlotGate)
        {
            if (!_itemWorkSlots.TryGetValue(item, out var slot) || !slot.Held)
                return false;
            slot.Held = false;
            routeKey = slot.RouteKey;
            if (_routeSessions.TryGetValue(routeKey, out var counts) && counts.Work > 0)
            {
                counts.Work--;
                PruneRouteIfDrainedLocked(routeKey, counts);
            }
        }
        DrainSessionSlotWaiters(routeKey);
        WakeAgentCapWaitersForRouteRelease(routeKey);
        _log.LogDebug(
            "Work item {Id}: suspended per-agent work slot on route {RouteKey} for LLM auditor fan-out",
            item, routeKey);
        return true;
    }

    /// <summary>
    /// <see cref="IAgentSessionSlotGate.ResumeWorkSlotAfterAuditAsync"/> —
    /// re-acquires the item's work slot through the normal capped wait, so a
    /// saturated agent delays rework rather than over-admitting sessions.
    /// </summary>
    public async Task ResumeWorkSlotAfterAuditAsync(WorkItemId item, CancellationToken ct)
    {
        string routeKey;
        lock (_sessionSlotGate)
        {
            if (!_itemWorkSlots.TryGetValue(item, out var slot) || slot.Held)
                return;
            routeKey = slot.RouteKey;
        }
        await WaitForSessionSlotAsync(routeKey, AgentSessionKind.Work, ct).ConfigureAwait(false);
        bool refunded = false;
        lock (_sessionSlotGate)
        {
            if (_itemWorkSlots.TryGetValue(item, out var slot))
            {
                slot.Held = true;
                _log.LogDebug(
                    "Work item {Id}: resumed per-agent work slot on route {RouteKey} after LLM auditor batch",
                    item, routeKey);
                return;
            }
            // The item exited while the resume was queued (the registration
            // is gone): hand the just-acquired permit back so it can't leak.
            if (_routeSessions.TryGetValue(routeKey, out var counts) && counts.Work > 0)
            {
                counts.Work--;
                PruneRouteIfDrainedLocked(routeKey, counts);
                refunded = true;
            }
        }
        if (refunded)
        {
            DrainSessionSlotWaiters(routeKey);
            WakeAgentCapWaitersForRouteRelease(routeKey);
        }
    }

    /// <summary>
    /// The worker's outer-finally release: frees the item's work slot when it
    /// is still held, and drops the registration either way. A suspended slot
    /// was already freed — nothing to release — so an item that exits
    /// mid-audit can neither leak a slot nor double-release one.
    /// </summary>
    public void ReleaseItemWorkSlotIfHeld(WorkItemId item)
    {
        string? routeKey = null;
        lock (_sessionSlotGate)
        {
            if (!_itemWorkSlots.Remove(item, out var slot))
                return;
            if (slot.Held)
            {
                routeKey = slot.RouteKey;
                if (_routeSessions.TryGetValue(routeKey, out var counts) && counts.Work > 0)
                {
                    counts.Work--;
                    PruneRouteIfDrainedLocked(routeKey, counts);
                }
            }
        }
        if (routeKey is not null)
        {
            DrainSessionSlotWaiters(routeKey);
            WakeAgentCapWaitersForRouteRelease(routeKey);
        }
    }

    // ── Live counts ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>Counts all in-flight CLI sessions — work plus audit — since
    /// both draw on the same provider concurrency budget.</remarks>
    public int GetRunning(AgentKind agent)
    {
        lock (_sessionSlotGate)
        {
            var total = 0;
            foreach (var kv in _routeSessions)
            {
                if (string.Equals(AgentInstanceIds.KindFromRouteKey(kv.Key), agent.Value, StringComparison.OrdinalIgnoreCase))
                    total += kv.Value.Work + kv.Value.Audit;
            }
            return total;
        }
    }

    /// <inheritdoc />
    public int GetRunning(AgentMembership member)
    {
        lock (_sessionSlotGate)
            return _routeSessions.TryGetValue(member.RouteKey, out var counts)
                ? counts.Work + counts.Audit
                : 0;
    }

    /// <inheritdoc />
    public int GetRunningWork(AgentKind agent) => GetRunningByKind(agent, AgentSessionKind.Work);

    /// <inheritdoc />
    public int GetRunningAudit(AgentKind agent) => GetRunningByKind(agent, AgentSessionKind.Audit);

    private int GetRunningByKind(AgentKind agent, AgentSessionKind kind)
    {
        lock (_sessionSlotGate)
        {
            var total = 0;
            foreach (var kv in _routeSessions)
            {
                if (string.Equals(AgentInstanceIds.KindFromRouteKey(kv.Key), agent.Value, StringComparison.OrdinalIgnoreCase))
                    total += kind == AgentSessionKind.Work ? kv.Value.Work : kv.Value.Audit;
            }
            return total;
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<AgentKind, int> Snapshot()
    {
        // Materialise so callers can iterate safely while the dispatcher mutates.
        var snap = new Dictionary<AgentKind, int>();
        lock (_sessionSlotGate)
        {
            foreach (var kv in _routeSessions)
            {
                var total = kv.Value.Work + kv.Value.Audit;
                if (total <= 0) continue;
                var kind = new AgentKind(AgentInstanceIds.KindFromRouteKey(kv.Key));
                snap[kind] = snap.TryGetValue(kind, out var existing) ? existing + total : total;
            }
        }
        return snap;
    }

    /// <summary>
    /// Per-route session counts split by kind, keyed by route key (the same
    /// keys <see cref="ConcurrencyStateSnapshot.CurrentlyRunningPerAgent"/>
    /// has always used). Zero-count routes are pruned, so the dictionaries
    /// only carry live sessions.
    /// </summary>
    private (Dictionary<string, int> Work, Dictionary<string, int> Audit) SnapshotRouteSessions()
    {
        var work = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var audit = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        lock (_sessionSlotGate)
        {
            foreach (var kv in _routeSessions)
            {
                if (kv.Value.Work > 0) work[kv.Key] = kv.Value.Work;
                if (kv.Value.Audit > 0) audit[kv.Key] = kv.Value.Audit;
            }
        }
        return (work, audit);
    }

    private int GetRunningForRoute(string routeKey)
    {
        lock (_sessionSlotGate)
            return _routeSessions.TryGetValue(routeKey, out var counts)
                ? counts.Work + counts.Audit
                : 0;
    }

    public bool HasCapacity(AgentKind agent)
    {
        var cap = GetAgentCap(agent);
        return cap <= 0 || GetRunning(agent) < cap;
    }

    public bool HasCapacity(AgentMembership member)
    {
        var cap = GetAgentCap(member);
        return cap <= 0 || GetRunning(member) < cap;
    }

    /// <inheritdoc />
    public int GetAuditCap(AgentMembership member) => GetAuditCapForRoute(member.RouteKey);

    /// <inheritdoc />
    public int GetAuditCap(AgentKind agent) => GetAuditCapForRoute(agent.Value);

    private static string ResolveDirectRouteKey(AgentKind agent, string? routeKeyOrInstanceId)
    {
        if (string.IsNullOrWhiteSpace(routeKeyOrInstanceId))
            return agent.Value;
        return AgentInstanceIds.RouteKey(agent, routeKeyOrInstanceId);
    }
}
