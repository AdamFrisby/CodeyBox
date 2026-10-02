using Microsoft.Extensions.Logging;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

// PipelineRunner.BaselineProduction.cs — Post-merge per-test coverage
// baseline hooks: schedule one sandboxed production job per successful base
// merge, and stage the freshest ancestry-reachable baseline into audit
// sandboxes. Both are advisory: scheduling/staging faults never fail merges
// or audits (the selector falls back to the full suite).
public sealed partial class PipelineRunner
{
    /// <summary>
    /// Schedules baseline production after a successful merge to the
    /// project's base branch. Exactly one pending job per project: a second
    /// merge before the job starts overwrites the pending entry (newest
    /// commit wins). Best-effort — any fault is logged and swallowed so the
    /// merge outcome (already persisted) is never affected.
    /// </summary>
    private void ScheduleBaselineProduction(
        Project project,
        string repoId,
        string baseBranch,
        string? mergeSha)
    {
        if (_baselineScheduler is null)
            return;
        try
        {
            var globalEnabled = _baselineProductionOptions?.Invoke().Enabled ?? true;
            if (!ShouldScheduleBaselineProduction(project.TestSelectionBaselineEnabled, globalEnabled, mergeSha))
            {
                if (project.TestSelectionBaselineEnabled
                    && !string.IsNullOrWhiteSpace(mergeSha)
                    && !SandboxTestSelectionBaselineRunner.IsHexSha(mergeSha.Trim()))
                {
                    _log.LogWarning(
                        "Baseline production not scheduled for project {ProjectId}: merge sha is not a commit sha",
                        project.Id.Value);
                }

                return;
            }

            _baselineScheduler.Schedule(new TestSelectionBaselineRequest(
                project.Id.Value,
                repoId,
                baseBranch,
                mergeSha!.Trim()));
            _log.LogInformation(
                "Scheduled test-selection baseline production for project {ProjectId}@{Commit}",
                project.Id.Value, mergeSha.Trim());
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Baseline production scheduling failed for project {ProjectId}; merge is unaffected",
                project.Id.Value);
        }
    }

    /// <summary>
    /// Pure scheduling gate: a merge schedules production only when the
    /// project opted in, the global kill-switch is on, and the merge sha is
    /// a real commit sha. Anything else leaves the queue untouched.
    /// </summary>
    internal static bool ShouldScheduleBaselineProduction(
        bool projectEnabled,
        bool globalEnabled,
        string? mergeSha)
        => projectEnabled
            && globalEnabled
            && !string.IsNullOrWhiteSpace(mergeSha)
            && SandboxTestSelectionBaselineRunner.IsHexSha(mergeSha.Trim());

    /// <summary>
    /// Stages the newest stored baseline whose commit is an ancestor of the
    /// item's base tip into the audit sandbox at
    /// <c>BaselineSandboxPath</c> (read-only). Stages nothing when no stored
    /// baseline is fresh within <c>MaxBaselineAge</c>. Best-effort — any
    /// fault (or a null return) leaves the audit to run the full suite.
    /// </summary>
    private async Task StageTestSelectionBaselineAsync(
        ISandbox sandbox,
        WorkItem item,
        Project project,
        string repoId,
        CancellationToken ct)
    {
        if (_baselineStager is null || !project.TestSelectionBaselineEnabled)
            return;
        try
        {
            var baseBranch = item.BaseBranch ?? project.DefaultBaseBranch;
            if (string.IsNullOrWhiteSpace(baseBranch))
                return;
            await _baselineStager.StageAsync(sandbox, project.Id.Value, repoId, baseBranch, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Baseline staging failed for work item {Id}; audit proceeds without a baseline",
                item.Id);
        }
    }
}
