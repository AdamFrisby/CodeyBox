namespace CodeyBox.Core;

/// <summary>
/// Optional scope for an operator-initiated baseline migration. The two filters
/// are AND-combined; a null field means "no constraint on that dimension".
/// <see cref="ProjectId"/> limits the migration to one project;
/// <see cref="BaselineImageRef"/> limits it to items currently pinned to a
/// specific (old) baseline ref. The default value (both null) migrates every
/// eligible non-terminal item.
/// </summary>
public readonly record struct BaselineMigrationFilter(
    ProjectId? ProjectId = null,
    string? BaselineImageRef = null);

/// <summary>
/// Lean projection of a work item that currently pins a baseline image and is
/// not in a terminal state — the candidate set for baseline migration. Carries
/// only the fields the planner needs so the query can be served from the
/// partial index on <c>baseline_image_ref</c> without hydrating full rows.
/// </summary>
public sealed record BaselinePinnedWorkItem(
    WorkItemId Id,
    ProjectId ProjectId,
    WorkItemState State,
    string BaselineImageRef);

/// <summary>
/// A ref that some migrated items will recompute onto at their next pickup,
/// with how many items map to it. <see cref="BaselineImageRef"/> is null when
/// the items will recompute to "no pin" (the provider derives the ref from live
/// config, or produces none — e.g. baseline images disabled or the project has
/// no work profile).
/// </summary>
public sealed record BaselineRecomputeTarget(string? BaselineImageRef, int Count);

/// <summary>
/// Pure result of <see cref="BaselineMigrationPlanner.Plan"/>:
/// <see cref="ItemIdsToClear"/> is the exact set of work items whose pin should
/// be cleared, <see cref="RecomputeTargets"/> summarises what those items
/// will recompute to, and <see cref="AlreadyCurrentCount"/> is how many
/// inspected candidates were skipped because their pin already equals the
/// current-config ref — the legible counterpart to
/// <see cref="ItemIdsToClear"/>'s "needed migrating". Contains no side
/// effects; the caller performs the write.
/// </summary>
public sealed record BaselineMigrationPlan(
    IReadOnlyList<WorkItemId> ItemIdsToClear,
    IReadOnlyList<BaselineRecomputeTarget> RecomputeTargets,
    int AlreadyCurrentCount);

/// <summary>
/// Outcome of a baseline migration. <see cref="MigratedCount"/> is the number
/// of pins actually cleared (authoritative — reflects rows the store wrote,
/// after excluding any that raced to a terminal state).
/// <see cref="ScannedCount"/> is how many pinned candidates were inspected.
/// <see cref="AlreadyCurrentCount"/> is how many of those needed no write
/// because their pin already matches the current-config ref, and
/// <see cref="SkippedTerminalCount"/> is how many pinned items in the
/// requested scope were excluded up front for sitting in a terminal state —
/// terminal pins are historical record, not candidates, and the retry path
/// re-validates them before re-queueing. An operator can therefore reconcile
/// <c>scanned ≈ migrated + alreadyCurrent</c> (migrated can fall short when a
/// row races to a terminal state before the write lands) without reading the
/// log, and see the terminal backlog separately.
/// <see cref="Truncated"/> is true when the per-scan cap limited the pass, in
/// which case the operator can re-run (the operation is idempotent) to continue.
/// </summary>
public sealed record BaselineMigrationResult(
    int MigratedCount,
    int ScannedCount,
    int AlreadyCurrentCount,
    int SkippedTerminalCount,
    bool Truncated,
    IReadOnlyList<BaselineRecomputeTarget> RecomputeTargets);

/// <summary>
/// Pure decision core for baseline migration. Given the candidate pinned items,
/// the operator's filter, and the current-config baseline ref for each project,
/// it decides which pins to clear and what they will recompute to. Kept pure so
/// the migration policy can be unit-tested without a store, clock, or provider.
/// </summary>
public static class BaselineMigrationPlanner
{
    // Sentinel grouping key for candidates that recompute to no pin. Baseline
    // refs are non-empty content-hash names (never angle-bracketed), so this
    // cannot collide with a real ref. Only used internally to bucket the null
    // target before projection back to null.
    private const string NoRecomputeTargetKey = "<recompute-to-none>";

    /// <summary>
    /// Decides the migration. A candidate's pin is cleared only when it is
    /// non-empty, the item is non-terminal, it passes the filter, and the pin
    /// differs from the current-config ref for its project (items already on
    /// the current baseline are left untouched — making the operation
    /// idempotent). <paramref name="currentRefByProject"/> maps each project to
    /// the ref pickup would recompute now; a missing project is treated as "no
    /// current baseline" (null).
    /// </summary>
    public static BaselineMigrationPlan Plan(
        IEnumerable<BaselinePinnedWorkItem> candidates,
        BaselineMigrationFilter filter,
        IReadOnlyDictionary<ProjectId, string?> currentRefByProject)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(currentRefByProject);

        var idsToClear = new List<WorkItemId>();
        var targetCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var alreadyCurrent = 0;

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate.BaselineImageRef))
                continue;
            if (WorkItemStates.IsTerminal(candidate.State))
                continue;
            if (filter.ProjectId is { } pid && candidate.ProjectId != pid)
                continue;
            if (filter.BaselineImageRef is { } oldRef
                && !string.Equals(candidate.BaselineImageRef, oldRef, StringComparison.Ordinal))
                continue;

            var current = currentRefByProject.GetValueOrDefault(candidate.ProjectId);
            if (string.Equals(candidate.BaselineImageRef, current, StringComparison.Ordinal))
            {
                alreadyCurrent++; // already on the current-config baseline
                continue;
            }

            idsToClear.Add(candidate.Id);
            var key = current ?? NoRecomputeTargetKey;
            targetCounts[key] = targetCounts.GetValueOrDefault(key) + 1;
        }

        var targets = targetCounts
            .OrderByDescending(static kv => kv.Value)
            .ThenBy(static kv => kv.Key, StringComparer.Ordinal)
            .Select(static kv => new BaselineRecomputeTarget(
                kv.Key == NoRecomputeTargetKey ? null : kv.Key,
                kv.Value))
            .ToList();

        return new BaselineMigrationPlan(idsToClear, targets, alreadyCurrent);
    }
}
