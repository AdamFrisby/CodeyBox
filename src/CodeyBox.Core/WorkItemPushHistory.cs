namespace CodeyBox.Core;

/// <summary>
/// Pure bookkeeping for the bounded per-item history of pushed work-branch
/// tip shas. Every successful work-branch push (plain or lease-guarded, on
/// any path) is recorded through <see cref="RecordPush"/> so a later retry
/// can prove a diverged remote tip is CodeyBox's own history even when the
/// latest record went stale (crash between push and persist, legacy rows).
/// The history also backs the lease check when commit trailers are disabled
/// by configuration and no trailer proof is possible.
/// </summary>
public static class WorkItemPushHistory
{
    /// <summary>
    /// Default cap on retained entries when no configured bound is
    /// available. The configured
    /// <see cref="UpstreamBranchOwnershipOptions.MaxPushHistoryEntries"/>
    /// wins whenever options are present.
    /// </summary>
    public const int DefaultMaxEntries = 20;

    /// <summary>
    /// Guards the persisted-state sink: only plausible commit ids (hex,
    /// 40–64 chars after trimming) qualify as pushed shas. Forge-reported
    /// tips are untrusted input, and lease decisions branch on the recorded
    /// history, so garbage must never enter it.
    /// </summary>
    public static bool IsPlausibleSha(string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha))
            return false;
        var trimmed = sha.Trim();
        if (trimmed.Length is < 40 or > 64)
            return false;
        foreach (var c in trimmed)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Returns <paramref name="item"/> with <paramref name="sha"/> recorded
    /// as the latest push: <see cref="WorkItem.LastPushedWorkBranchSha"/>
    /// becomes <paramref name="sha"/> and the sha is appended to
    /// <see cref="WorkItem.PushedWorkBranchShaHistory"/> (moved to the end
    /// when already present), truncated to the newest
    /// <paramref name="maxEntries"/> entries. Blank or implausible shas
    /// (see <see cref="IsPlausibleSha"/>) leave the item unchanged — a guess
    /// is never persisted. Pure: the input is never mutated.
    /// </summary>
    public static WorkItem RecordPush(WorkItem item, string? sha, int maxEntries = DefaultMaxEntries)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!IsPlausibleSha(sha))
            return item;
        var bound = Math.Max(1, maxEntries);
        var trimmed = sha!.Trim();
        var history = item.PushedWorkBranchShaHistory
            .Where(IsPlausibleSha)
            .Select(s => s.Trim())
            .Where(s => !string.Equals(s, trimmed, StringComparison.OrdinalIgnoreCase))
            .ToList();
        history.Add(trimmed);
        while (history.Count > bound)
            history.RemoveAt(0);
        return item with
        {
            LastPushedWorkBranchSha = trimmed,
            PushedWorkBranchShaHistory = history,
        };
    }

    /// <summary>
    /// True when <paramref name="sha"/> equals any recorded push for
    /// <paramref name="item"/> (latest or history), compared ordinally
    /// ignoring case. Used by the lease guard before treating a moved remote
    /// as third-party work.
    /// </summary>
    public static bool Contains(WorkItem item, string? sha)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(sha))
            return false;
        var trimmed = sha.Trim();
        if (string.Equals(item.LastPushedWorkBranchSha?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
            return true;
        return item.PushedWorkBranchShaHistory.Any(s =>
            string.Equals(s?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
    }
}
