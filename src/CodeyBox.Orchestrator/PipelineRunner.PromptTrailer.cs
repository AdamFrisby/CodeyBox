using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using CodeyBox.Agents;
using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Projects;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

// PipelineRunner.PromptTrailer.cs — Prompt-revision trailer stamping: commit-trailer composition and head-trailer repair.
public sealed partial class PipelineRunner
{
    /// <summary>
    /// Reads the prompt revision snapshotted into <c>work_item_iterations</c> at
    /// iteration-dispatch time, falling back to the item's current revision if
    /// no row exists yet (e.g. legacy data, or RunAgentPhaseAsync is invoked
    /// from a code path that did not pre-record the iteration).
    /// </summary>
    private async Task<int> ResolveIterationRevisionAsync(WorkItem item, int iteration, CancellationToken ct)
        => (await TryLookupIterationRevisionAsync(item.Id, iteration, ct)) ?? item.PromptRevision;

    /// <summary>
    /// Reads the prompt revision snapshotted at iteration-dispatch time, or null
    /// if no row exists. Used by the audit context where "no record" must surface
    /// distinctly from "record found, value = item.PromptRevision".
    /// </summary>
    private async Task<int?> TryLookupIterationRevisionAsync(WorkItemId workItemId, int iteration, CancellationToken ct)
    {
        var rows = await _store.GetIterationsAsync(workItemId, ct);
        var row = rows.FirstOrDefault(i => i.Iteration == iteration);
        return row?.PromptRevisionAtDispatch;
    }

    /// <summary>
    /// Builds the trailer block to append to an orchestrator-emitted commit
    /// message. Always includes <c>CodeyBox-WorkItem</c>, <c>CodeyBox-Agent</c>,
    /// and the terminal <c>Co-Authored-By</c> trailer; conditionally includes
    /// <c>CodeyBox-Fallbacks</c> when fallback events occurred for this work
    /// item. Failures to load fallback history degrade silently — the trailer
    /// block is still emitted, just without the optional fallbacks line, so a
    /// SQLite hiccup never blocks a commit.
    /// </summary>
    internal async Task<string> ComposeCommitTrailerBlockAsync(
        WorkItemId workItemId,
        AgentKind finalAgent,
        string? finalModel,
        CancellationToken ct,
        int? promptRevisionAtDispatch = null)
    {
        IReadOnlyList<AgentFallbackRecord>? history = null;
        if (_fallbackHistory is not null)
        {
            try
            {
                history = await _fallbackHistory.ListByWorkItemAsync(workItemId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug(ex, "fallback history fetch failed for commit-trailer composition (work item {WorkItemId})", workItemId);
            }
        }
        return CodeyBoxTrailers.Compose(workItemId, finalAgent, finalModel, history, promptRevisionAtDispatch);
    }

    /// <summary>
    /// Verifies the sandbox's HEAD commit carries a
    /// <c>CodeyBox-Prompt-Revision</c> trailer that matches the iteration's
    /// dispatched revision; if not, adds an empty stamp commit on top with
    /// the full canonical trailer block. The orchestrator owns the dispatch
    /// revision and the work-item id outright, so trusting the agent to echo
    /// them back on its final commit is unreliable: a missing trailer is a
    /// purely mechanical failure that would otherwise block the post-work
    /// audit and burn a whole iteration on a triviality.
    ///
    /// <para>The stamp is intentionally skipped when the operator updated the
    /// prompt mid-iteration (<c>dispatched != current</c>). In that case the
    /// agent ran against an older prompt and the
    /// <c>process:prompt-revision-trailer</c> auditor must still surface the
    /// missing trailer so the operator can decide whether to re-dispatch —
    /// auto-stamping it here would paper over a genuine stale-prompt signal.
    /// </para>
    /// </summary>
    internal async Task EnsureHeadCarriesPromptRevisionTrailerAsync(
        ISandbox sandbox,
        WorkItem item,
        AgentKind finalAgent,
        string? finalModel,
        int promptRevisionAtDispatch,
        string agentPhase,
        CancellationToken ct)
    {
        var trailers = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "git", "-C", SandboxConventions.WorkDir, "log", "-1",
                $"--pretty=format:%(trailers:key={CodeyBoxTrailers.PromptRevisionTrailerKey},valueonly=true,unfold=true)",
            ],
        }, ct);

        if (trailers.Success)
        {
            var raw = (trailers.Stdout ?? string.Empty).Trim();
            if (raw.Length > 0)
            {
                var firstLine = raw.Split('\n', 2)[0].Trim();
                if (int.TryParse(firstLine, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var found)
                    && found == promptRevisionAtDispatch)
                    return;
            }
        }
        else
        {
            // A failed git read is logged but does not block the stamp — the
            // commit itself will fail loudly if the repo is in a bad state, and
            // the audit gate downstream catches any remaining mismatch.
            _log.LogWarning(
                "Failed to read HEAD trailers in work item {Id} sandbox (git exit {Exit}); proceeding with conditional stamp",
                item.Id, trailers.ExitCode);
        }

        // Re-read the work item: if the operator bumped the prompt between
        // iteration dispatch and this point, do NOT auto-stamp — the auditor
        // must still flag the divergence so the operator can re-dispatch
        // against the new prompt. Falling back to the in-memory snapshot is
        // safe (and conservative — it favours stamping) when the store read
        // fails; the cost of a missed stamp here is just the audit iteration
        // we are trying to avoid.
        WorkItem? freshItem;
        try
        {
            freshItem = await _store.GetAsync(item.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex,
                "Failed to re-read work item {Id} during pre-audit trailer stamp; using in-memory snapshot",
                item.Id);
            freshItem = item;
        }
        var currentRevision = freshItem?.PromptRevision ?? item.PromptRevision;
        if (currentRevision != promptRevisionAtDispatch)
        {
            _log.LogInformation(
                "Work item {Id}: skipping pre-audit trailer stamp — dispatched revision {Dispatched} differs from current {Current}; auditor will surface the stale-prompt signal",
                item.Id, promptRevisionAtDispatch, currentRevision);
            return;
        }

        var trailerBlock = await ComposeCommitTrailerBlockAsync(item.Id, finalAgent, finalModel, ct,
            promptRevisionAtDispatch: promptRevisionAtDispatch);
        var commitMessage = $"codeybox: stamp prompt-revision trailer\n\n{trailerBlock}";

        await using (var stampScope = await TimingScope.BeginAsync(_timings, item.Id, agentPhase, "git.commit.stamp_trailer",
            activitySource: CodeyBoxActivities.Sandbox, log: _log))
        {
            await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "commit", "--allow-empty", "-m", commitMessage);
        }

        _log.LogInformation(
            "Work item {Id}: stamped CodeyBox-Prompt-Revision={Revision} on HEAD ({Phase}); agent did not emit the trailer",
            item.Id, promptRevisionAtDispatch, agentPhase);
    }

    private TimeSpan ResolvePhaseAbsoluteTimeout(TimeSpan perAttemptTimeout) =>
        ResolvePhaseAbsoluteTimeout(perAttemptTimeout, _opts.PhaseAbsoluteTimeoutMultiplier);

    internal static TimeSpan ResolvePhaseAbsoluteTimeout(TimeSpan perAttemptTimeout, double multiplier)
    {
        if (perAttemptTimeout == Timeout.InfiniteTimeSpan || perAttemptTimeout <= TimeSpan.Zero)
            return perAttemptTimeout;
        if (double.IsNaN(multiplier) || double.IsInfinity(multiplier) || multiplier < 1.0)
            throw new InvalidOperationException("CodeyBox:PhaseAbsoluteTimeoutMultiplier must be finite and >= 1");

        var ticks = Math.Ceiling(perAttemptTimeout.Ticks * multiplier);
        if (ticks >= MaxCancellationTimer.Ticks)
            return MaxCancellationTimer;
        return TimeSpan.FromTicks((long)ticks);
    }

}
