using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Tuning for <see cref="StalePullRequestSweeper"/> — the periodic check that
/// detects CodeyBox-authored PRs whose base branch has moved and produced a
/// conflict the auto-merger can no longer resolve.
///
/// <para>The sweeper polls the configured forges (currently only GitHub upstream
/// projects) at <see cref="CheckInterval"/> and fires the
/// <c>upstream.pr_stale_base</c> webhook event the first time a PR with a given
/// <c>(prNumber, headSha, baseBranch)</c> identity is observed in the "dirty"
/// state. Repeated observations on the same identity are de-duplicated so a
/// stale PR does not spam the event bus on every tick.</para>
///
/// <para>Bind under <c>CodeyBox:StalePullRequestSweep</c>.</para>
/// </summary>
public sealed class StalePullRequestSweeperOptions
{
    /// <summary>When false the sweeper does not run. Default: true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often the sweeper polls open PRs. Default: 60 s. The 5-minute SLA
    /// in the bug spec is met comfortably at this cadence. Clamped to a 30-s
    /// minimum at startup to avoid hammering the GitHub API.
    /// </summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Head-branch prefix that identifies CodeyBox-authored PRs. PRs whose
    /// head branch does not begin with this prefix are ignored. Default
    /// <c>codeybox/</c> matches the convention used by the pipeline runner
    /// when it generates work-branch names.
    /// </summary>
    public string BranchPrefix { get; set; } = "codeybox/";

    /// <summary>
    /// Master switch for automated stale-base remediation. When true, a
    /// detected stale-base merge conflict — whether surfaced out-of-band by
    /// this sweeper or at upstream-push time inside the pipeline — drives the
    /// owning work item into the existing conflict-rework state machine
    /// (<see cref="WorkItemState.ReworkingForConflict"/>): the work branch is
    /// rebased onto the refreshed canonical base, the agentic conflict resolver
    /// runs, and the merge + upstream push are re-attempted.
    ///
    /// <para>When false (the default) both surfaces preserve their historical
    /// behaviour: the sweeper only emits the <c>upstream.pr_stale_base</c>
    /// audit-log entry and webhook (notify-only), and the pipeline parks at its
    /// existing terminal state. Turning this on is a hot-reloadable operator
    /// decision — no restart required.</para>
    /// </summary>
    public bool RouteToConflictRework { get; set; }

    /// <summary>
    /// Maximum number of conflict-rework iterations (counted on
    /// <see cref="WorkItem.ConflictReworkAttempts"/>) a work item may accrue via
    /// the stale-base routing path before it is parked at the existing terminal
    /// <see cref="WorkItemState.MergeConflictResolutionFailed"/>. Bounds the
    /// remediation so a base branch that is a moving target (repeatedly
    /// re-conflicting after each rebase) cannot loop unbounded. Values below 1
    /// are treated as 1. Default 2.
    /// </summary>
    public int MaxReworkAttempts { get; set; } = 2;
}
