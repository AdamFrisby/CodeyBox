using System.Collections.Concurrent;
using CodeyBox.Majordomo;

namespace CodeyBox.Api.Majordomo;

/// <summary>
/// Server-side accounting for the per-turn mutation budget. A majordomo
/// "turn" is a burst of tool calls from one identity; the transport cannot
/// know where a turn begins, so the ledger bounds the burst instead: it
/// counts mutations attributed to an identity inside a rolling window
/// (<see cref="MajordomoServerOptions.TurnWindowSeconds"/>) and feeds that
/// count to <see cref="MajordomoAuthorization.Decide"/>. The window rolls so
/// a later turn can spend budget again — the cap throttles a runaway burst,
/// it does not permanently disable the account.
/// </summary>
/// <remarks>
/// Entries are appended only after a mutation actually executes — reads,
/// proposals, refusals, and dry-runs never consume budget, matching the
/// <see cref="MajordomoTurnUsage"/> contract. The ledger is bounded: each key
/// keeps at most <see cref="MaxTrackedMutationsPerKey"/> timestamps (newer
/// entries are what the window check reads, so capping history is safe), and
/// keys are pruned when their newest entry ages out of the window.
/// </remarks>
internal sealed class MajordomoTurnLedger
{
    /// <summary>
    /// Hard bound on retained timestamps per identity — comfortably above the
    /// hard per-turn cap (<see cref="MajordomoOptions.MaxAllowedMutatedItemsPerTurn"/>)
    /// so the budget check always sees the full window.
    /// </summary>
    private const int MaxTrackedMutationsPerKey = 1024;

    /// <summary>Hard bound on tracked identities; least-recently-active entries are evicted.</summary>
    private const int MaxKeys = 4096;

    private readonly ConcurrentDictionary<string, LinkedList<DateTimeOffset>> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public MajordomoTurnLedger(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// The number of items <paramref name="key"/> has mutated inside
    /// <paramref name="window"/> ending now.
    /// </summary>
    public MajordomoTurnUsage Snapshot(string key, TimeSpan window)
    {
        var cutoff = _time.GetUtcNow() - window;
        if (!_entries.TryGetValue(key, out var list))
            return MajordomoTurnUsage.None;

        lock (list)
        {
            Prune(list, cutoff);
            return new MajordomoTurnUsage { MutatedItems = list.Count };
        }
    }

    /// <summary>
    /// Records <paramref name="count"/> mutations by <paramref name="key"/> at
    /// the current instant. Call only after the mutation executed.
    /// </summary>
    public void Record(string key, int count)
    {
        var list = _entries.GetOrAdd(key, static _ => new LinkedList<DateTimeOffset>());
        var now = _time.GetUtcNow();
        lock (list)
        {
            for (var i = 0; i < count; i++)
                list.AddLast(now);
            while (list.Count > MaxTrackedMutationsPerKey)
                list.RemoveFirst();
        }

        // Keep the map bounded: evict keys whose newest entry predates a
        // generous idle horizon (four max windows covers any configured
        // TurnWindowSeconds, and timestamps older than that can never count).
        if (_entries.Count > MaxKeys)
        {
            var idleCutoff = now - TimeSpan.FromSeconds(MajordomoServerOptions.MaxTurnWindowSeconds * 4);
            foreach (var kv in _entries)
            {
                LinkedListNode<DateTimeOffset>? newest;
                lock (kv.Value)
                    newest = kv.Value.Last;
                if (newest is null || newest.Value < idleCutoff)
                    _entries.TryRemove(kv.Key, out _);
            }
        }
    }

    private static void Prune(LinkedList<DateTimeOffset> list, DateTimeOffset cutoff)
    {
        while (list.First is { } head && head.Value <= cutoff)
            list.RemoveFirst();
    }
}
