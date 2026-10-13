using Microsoft.Extensions.Logging;
using CodeyBox.Core;
using CodeyBox.Projects;

namespace CodeyBox.Orchestrator;

// PipelineRunner.BaseBroken.cs — Base-broken containment: when the required
// build fails on the base tip (not the item's diff), record the project-level
// condition, file the deduped fix item, and return the item to its resumable
// state with the branch preserved — never a failure charge.
public sealed partial class PipelineRunner
{
    /// <summary>
    /// Handles a <see cref="BaseBuildBrokenException"/>: the item did not
    /// break the build — the base branch is broken at the recorded tip.
    /// Registers the project-level condition (deduped by base SHA), files
    /// the single highest-priority fix item for it, and parks the item at
    /// the exception's resume state so the dispatcher hold — not a terminal
    /// failure — governs what happens next. When no tracker is wired the
    /// item still parks rather than failing.
    /// </summary>
    private async Task ParkForBaseBrokenBaseAsync(
        WorkItem item,
        Project project,
        BaseBuildBrokenException ex)
    {
        // 1) Record the condition and file the fix item BEFORE parking the
        //    item, so the dispatcher hold is in place before this item can
        //    race back into pickup.
        var condition = new BaseBrokenCondition
        {
            ProjectId = project.Id,
            BaseBranch = ex.BaseBranch,
            BaseSha = ex.BaseSha,
            RepositoryId = item.Id.ToString(),
            ErrorSummary = SummarizeBaseBrokenOutput(ex.BaseBuildOutput),
            DetectedAt = _opts.TimeProvider.GetUtcNow(),
        };

        var parkReason =
            $"required build failed on base branch '{ex.BaseBranch}' tip " +
            $"{BaseBrokenTestSupport.ShortSha(ex.BaseSha)} (base broken, not this diff); " +
            "item held until the base builds again";
        await ParkForBaseBrokenCoreAsync(
            item, project, condition, ex.ResumeState, ex.Phase, parkReason,
            healthyAgain: "fails the required build",
            ct: CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles a <see cref="BaseTestBrokenException"/>: the item's tests
    /// fail, but so do the same tests on the base tip — the base is
    /// test-red, not the work. Same containment as the build path (record,
    /// file, park, hold); only the condition kind and the clear signal
    /// (tip CI green instead of tip build green) differ.
    /// </summary>
    private async Task ParkForBaseBrokenTestAsync(
        WorkItem item,
        Project project,
        BaseTestBrokenException ex)
    {
        var condition = new BaseBrokenCondition
        {
            ProjectId = project.Id,
            BaseBranch = ex.BaseBranch,
            BaseSha = ex.BaseSha,
            RepositoryId = item.Id.ToString(),
            ErrorSummary = ex.FailureSummary
                ?? BaseBrokenTestSupport.BuildSummary(ex.FailingTests, ex.SuspectCommits),
            FailureKind = BaseBrokenFailureKind.Test,
            FailingNames = BaseBrokenTestSupport.SanitizeNameListForRecord(ex.FailingTests),
            DetectedAt = _opts.TimeProvider.GetUtcNow(),
        };

        var parkReason =
            $"tests failed on base branch '{ex.BaseBranch}' tip " +
            $"{BaseBrokenTestSupport.ShortSha(ex.BaseSha)} (base test-red, not this diff); " +
            "item held until the base tip passes CI again";
        await ParkForBaseBrokenCoreAsync(
            item, project, condition, ex.ResumeState, ex.Phase, parkReason,
            healthyAgain: "is test-red",
            ct: CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ParkForBaseBrokenCoreAsync(
        WorkItem item,
        Project project,
        BaseBrokenCondition condition,
        WorkItemState resumeState,
        string? phase,
        string parkReason,
        string healthyAgain,
        CancellationToken ct)
    {
        WorkItemId? fixItemId = null;
        if (_baseBrokenConditions is not null)
        {
            try
            {
                var recorded = await _baseBrokenConditions.RecordDetectedAsync(
                    condition, CancellationToken.None).ConfigureAwait(false);
                fixItemId = await _baseBrokenConditions.EnsureFixItemAsync(
                    recorded,
                    item,
                    _pipelineTuning.Current.BaseBrokenFixItemPriority,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception conditionEx)
            {
                // A persistence/filing failure must not convert into an item
                // failure: the item still parks below, and the next
                // detection re-attempts the bookkeeping.
                _log.LogWarning(
                    conditionEx,
                    "Work item {Id}: failed to record/file base-broken condition for base tip {Sha}; parking item regardless",
                    item.Id, condition.BaseSha);
            }
        }

        // 2) Return the item to its resumable state with the branch
        //    preserved. Built via a record initializer (not With()) because
        //    With(Queued) intentionally clears WorkBranch for the
        //    failed-retry path — here the whole point is keeping it.
        await RunBoundedPostAgentAsync(
            item.Id,
            "transition-base-broken-park",
            CancellationToken.None,
            async transitionCt =>
            {
                var current = await _store.GetAsync(item.Id, transitionCt).ConfigureAwait(false) ?? item;
                if (WorkItemStates.IsTerminal(current.State))
                {
                    _log.LogInformation(
                        "Work item {Id} already terminal at {State}; skipping base-broken park",
                        item.Id, current.State);
                    return;
                }

                var parked = WorkItemRecoveryPolicy.ReleaseAgentTurnDispatchClaim(current) with
                {
                    State = resumeState,
                    LastError = parkReason,
                    UpdatedAt = _opts.TimeProvider.GetUtcNow(),
                    StartedAt = resumeState == WorkItemState.Queued
                        ? null
                        : current.StartedAt,
                    PreserveWorkBranchOnQueuedPickup =
                        resumeState == WorkItemState.Queued
                        && !string.IsNullOrWhiteSpace(current.WorkBranch),
                    // One-shot authorization and parked-retry metadata from
                    // the interrupted phase must not leak into the resumable
                    // state (mirrors WorkItem.With semantics for non-carrying
                    // states); failure charge stays untouched.
                    FailureKind = null,
                    QuotaResetAt = null,
                    NextQuotaRetryAt = null,
                    QuotaRetryFrom = null,
                    QuotaRetryPhase = null,
                    NextTransientRetryAt = null,
                    TransientRetryFrom = null,
                    AgentPauseTarget = null,
                    AgentPauseRetryFrom = null,
                    DelegationRequested = false,
                    CancellationReason = null,
                    CancellationSource = null,
                };
                var wrote = await _store.TryUpdateIfStateAsync(parked, current.State, transitionCt)
                    .ConfigureAwait(false);
                if (!wrote)
                {
                    _log.LogInformation(
                        "Work item {Id} state changed concurrently; skipping base-broken park",
                        item.Id);
                }
            }).ConfigureAwait(false);

        _log.LogWarning(
            "Work item {Id} parked at {ResumeState}: base branch '{Base}' tip {Sha} {HealthyAgain}{FixNote}",
            item.Id,
            resumeState,
            condition.BaseBranch,
            condition.BaseSha,
            healthyAgain,
            fixItemId is { } fix ? $" (fix item {fix})" : string.Empty);

        AuditLog.WorkItemTransitioned(item.Id, resumeState.ToString());
        await _webhooks.PublishAsync(new WebhookEvent
        {
            Event = "work_item.base_broken_parked",
            WorkItem = await _store.GetAsync(item.Id, CancellationToken.None).ConfigureAwait(false) ?? item,
            Project = project,
            Details = new
            {
                workItemId = item.Id.ToString(),
                baseBranch = condition.BaseBranch,
                baseSha = condition.BaseSha,
                failureKind = condition.FailureKind.ToString(),
                resumeState = resumeState.ToString(),
                fixWorkItemId = fixItemId?.ToString(),
                phase,
            },
        }, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Bounded, control-sanitized digest of the base-tip build output for
    /// durable storage. The output is untrusted (repo content → compiler):
    /// control chars and ANSI escapes are stripped before the summary is
    /// persisted or handed to the fix-item policy (which sanitizes again
    /// for prompt embedding).
    /// </summary>
    private static string SummarizeBaseBrokenOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return "(no base build output captured)";
        const int max = 1024;
        var cleaned = BaseBrokenFixItemPolicy.StripControl(output.Trim());
        return cleaned.Length <= max ? cleaned : cleaned[..max];
    }
}
