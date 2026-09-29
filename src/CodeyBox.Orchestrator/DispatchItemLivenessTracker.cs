using System.Collections.Concurrent;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Point-in-time dispatcher view of one work item: when the dispatcher last
/// evaluated it, whether it is currently sleeping in a deferred-requeue delay,
/// and whether it is continuously blocked on quota. Consumed by
/// <see cref="ItemStaleProgressWatchdog"/> so an item that is legitimately
/// waiting behind quota/cap/budget is recognised as waiting — not wedged.
/// </summary>
public sealed record DispatchItemLiveness(
    DateTimeOffset LastEvaluatedAt,
    bool IsDeferred,
    DateTimeOffset? QuotaBlockedSince,
    string? QuotaBlockedAgent);

/// <summary>
/// One item that has been waiting purely on quota longer than the notice
/// threshold. Surfaced through <c>GET /queue/status</c> so operators can tell
/// "waiting on quota" apart from "wedged".
/// </summary>
public sealed record QuotaWaitStatus(
    WorkItemId WorkItemId,
    string Agent,
    DateTimeOffset Since)
{
    /// <summary>Human-readable queue-status reason for this wait.</summary>
    public string Reason => $"waiting on quota for {Agent} since {Since:O}";
}

/// <summary>
/// Read surface the stale-item watchdog (and queue status) uses to tell
/// "dispatcher is actively deferring this item" from "the item has fallen out
/// of dispatch". Implemented by <see cref="DispatchItemLivenessTracker"/>,
/// which the orchestrator feeds on every deferral and pickup.
/// </summary>
public interface IItemDispatchLivenessSource
{
    /// <summary>
    /// Returns the dispatcher liveness snapshot for <paramref name="id"/>,
    /// or false when the dispatcher has no record of the item (never
    /// evaluated in this process, or its record aged out).
    /// </summary>
    bool TryGetLiveness(WorkItemId id, out DispatchItemLiveness liveness);

    /// <summary>Snapshot of items currently in a continuous quota-blocked episode.</summary>
    IReadOnlyList<QuotaWaitStatus> GetQuotaWaits();

    /// <summary>Forgets <paramref name="id"/> entirely (recovery / test paths).</summary>
    void NoteRemoved(WorkItemId id);
}

/// <summary>
/// In-process record of dispatcher evaluations per work item. The
/// orchestrator calls <see cref="NoteDispatchEvaluated"/> on every deferral
/// (quota / cap / budget / pause / infrastructure) and
/// <see cref="NotePickedUp"/> when a worker takes the item; the deferred
/// flag is answered live through <see cref="IsDeferredProvider"/> so it can
/// never drift from the orchestrator's deferred-requeue registry.
///
/// <para>
/// A quota-blocked episode starts on the first quota-shaped deferral and ends
/// on any non-quota evaluation or pickup — only a wait that is <em>purely</em>
/// quota-blocked keeps its original <c>Since</c> stamp, which is what the
/// long-wait notice thresholds on.
/// </para>
///
/// <para>
/// Bounded: at most <see cref="MaxEntries"/> items are tracked; older
/// records age out after <see cref="Retention"/> via opportunistic pruning on
/// write, so a long-lived process cannot grow this map without limit.
/// </para>
/// </summary>
public sealed class DispatchItemLivenessTracker : IItemDispatchLivenessSource
{
    /// <summary>Hard cap on tracked items; enforced before buffering.</summary>
    public const int MaxEntries = 10000;

    /// <summary>Records older than this are eligible for pruning.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<WorkItemId, Entry> _entries = new();
    private readonly TimeProvider _time;

    private sealed record Entry(
        DateTimeOffset LastEvaluatedAt,
        DateTimeOffset? QuotaBlockedSince,
        string? QuotaBlockedAgent);

    public DispatchItemLivenessTracker(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Live probe for deferred-requeue membership, wired once by the
    /// orchestrator (it owns the registry). When null, <c>IsDeferred</c>
    /// reports false and recency of <c>LastEvaluatedAt</c> is the only signal.
    /// </summary>
    public Func<WorkItemId, bool>? IsDeferredProvider { get; set; }

    /// <summary>
    /// Records one dispatcher evaluation of <paramref name="id"/> at the
    /// current clock time. A non-null <paramref name="quotaBlockedAgent"/>
    /// marks a quota-shaped deferral (every eligible agent quota-blocked);
    /// null marks any other evaluation and ends an ongoing quota episode.
    /// </summary>
    public void NoteDispatchEvaluated(WorkItemId id, string? quotaBlockedAgent)
    {
        PruneIfOverCap();
        var now = _time.GetUtcNow();
        _entries.AddOrUpdate(
            id,
            addValueFactory: static (_, state) => state.quotaBlockedAgent is null
                ? new Entry(state.now, null, null)
                : new Entry(state.now, state.now, state.quotaBlockedAgent),
            updateValueFactory: static (_, existing, state) => state.quotaBlockedAgent is null
                ? existing with { LastEvaluatedAt = state.now, QuotaBlockedSince = null, QuotaBlockedAgent = null }
                : existing with
                {
                    LastEvaluatedAt = state.now,
                    QuotaBlockedSince = existing.QuotaBlockedSince ?? state.now,
                    QuotaBlockedAgent = state.quotaBlockedAgent,
                },
            factoryArgument: (now, quotaBlockedAgent));
    }

    /// <summary>
    /// Records that a worker took <paramref name="id"/> for a pipeline run.
    /// Ends any quota episode; the bound-worker liveness path owns the item
    /// from here.
    /// </summary>
    public void NotePickedUp(WorkItemId id)
    {
        PruneIfOverCap();
        var now = _time.GetUtcNow();
        _entries.AddOrUpdate(
            id,
            addValueFactory: static (_, now) => new Entry(now, null, null),
            updateValueFactory: static (_, existing, now) => existing with
            {
                LastEvaluatedAt = now,
                QuotaBlockedSince = null,
                QuotaBlockedAgent = null,
            },
            factoryArgument: now);
    }

    /// <summary>Forgets <paramref name="id"/> entirely (recovery / test paths).</summary>
    public void NoteRemoved(WorkItemId id) => _entries.TryRemove(id, out _);

    /// <inheritdoc />
    public bool TryGetLiveness(WorkItemId id, out DispatchItemLiveness liveness)
    {
        if (!_entries.TryGetValue(id, out var entry))
        {
            liveness = null!;
            return false;
        }

        liveness = new DispatchItemLiveness(
            entry.LastEvaluatedAt,
            IsDeferredProvider?.Invoke(id) ?? false,
            entry.QuotaBlockedSince,
            entry.QuotaBlockedAgent);
        return true;
    }

    /// <inheritdoc />
    public IReadOnlyList<QuotaWaitStatus> GetQuotaWaits()
    {
        var waits = new List<QuotaWaitStatus>();
        foreach (var (id, entry) in _entries)
        {
            if (entry.QuotaBlockedSince is { } since)
                waits.Add(new QuotaWaitStatus(id, entry.QuotaBlockedAgent ?? "eligible agents", since));
        }
        return waits;
    }

    private void PruneIfOverCap()
    {
        if (_entries.Count <= MaxEntries)
            return;

        var cutoff = _time.GetUtcNow() - Retention;
        foreach (var (id, entry) in _entries)
        {
            if (entry.LastEvaluatedAt < cutoff)
                _entries.TryRemove(id, out _);
        }
    }
}
