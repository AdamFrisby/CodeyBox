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

// PipelineRunner.AgentTurnResume.cs — Agent-turn resume/preempt machinery: durable checkpoints, scratchpad archives, and session-lifecycle handling.
public sealed partial class PipelineRunner
{
    private static async Task<AgentResult> RunClaudeSessionSupervisedTurnsAsync(
        ClaudeSessionLifecycle sessionLifecycle,
        IAgentSupervisionSession supervision,
        string prompt,
        Action<string>? stdoutCallback,
        Func<string, CancellationToken, Task<string>> promptPreprocessor,
        CancellationToken ct)
    {
        var supervisedStdout = supervision.WrapStdoutCallback(stdoutCallback);
        await supervision.PublishCodeyBoxCommandAsync("autonomous", prompt, injectionId: null, ct)
            .ConfigureAwait(false);

        var agentResult = await sessionLifecycle.SendTurnAsync(prompt, ct, supervisedStdout)
            .ConfigureAwait(false);

        return await supervision.RunPendingInjectionsAsync(
                agentResult,
                async (turn, turnCt) =>
                {
                    var turnPrompt = await promptPreprocessor(turn.Prompt, turnCt).ConfigureAwait(false);
                    return await sessionLifecycle.SendTurnAsync(turnPrompt, turnCt, supervisedStdout)
                        .ConfigureAwait(false);
                },
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Pre-emptive self-review turn for the session pipeline. Fires ONE extra
    /// warm-session turn after the initial work turn lands (cache-hot, near-
    /// free) carrying the composer-built guidance derived from the project's
    /// active auditors. Any edits the agent makes are staged and committed
    /// on top of the work commit so the formal audit (which runs in its OWN
    /// fresh sandbox) sees the fixed code without ever being shown the
    /// self-review prompt or guidance. Failures here are SOFT — the work item
    /// proceeds to audit regardless, because the formal audit + rework loop
    /// still owns convergence.
    /// </summary>
    internal async Task TryRunPreemptiveSelfReviewTurnAsync(
        ClaudeSessionLifecycle sessionLifecycle,
        ISandbox sandbox,
        WorkItem item,
        Project project,
        IAgentRunner runner,
        string branch,
        IReadOnlyList<IAuditor> auditors,
        int promptRevisionAtDispatch,
        CancellationToken ct)
    {
        var guidance = SelfReviewChecklistComposer.Compose(auditors);
        if (string.IsNullOrWhiteSpace(guidance))
        {
            CodeyBoxMeters.SessionPreemptiveSelfReviewTurns.Add(1,
                new KeyValuePair<string, object?>("outcome", "skipped_empty_guidance"));
            return;
        }

        var selfReviewPrompt = BuildPreemptiveSelfReviewPrompt(guidance, promptRevisionAtDispatch);
        string shaBefore;
        try
        {
            selfReviewPrompt = await ProcessAgentPromptAsync(
                item.Id,
                runner.Kind,
                AgentPromptPhase.SelfReview,
                AuditProgressIterationNumbers.WorkPhase,
                project,
                sandbox,
                selfReviewPrompt,
                ct);

            var beforeHead = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", SandboxConventions.WorkDir, "rev-parse", "HEAD"],
            }, ct);
            if (!beforeHead.Success)
            {
                _log.LogWarning(
                    "Pre-emptive self-review could not read HEAD before turn for work item {Id}; continuing to audit without it: {Stderr}",
                    item.Id,
                    beforeHead.Stderr);
                CodeyBoxMeters.SessionPreemptiveSelfReviewTurns.Add(1,
                    new KeyValuePair<string, object?>("outcome", "failed"));
                return;
            }
            shaBefore = beforeHead.Stdout.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Pre-emptive self-review setup failed for work item {Id}; continuing to audit without it",
                item.Id);
            CodeyBoxMeters.SessionPreemptiveSelfReviewTurns.Add(1,
                new KeyValuePair<string, object?>("outcome", "failed"));
            return;
        }

        var turnAttempted = false;
        try
        {
            turnAttempted = true;
            var turnResult = await sessionLifecycle.SendTurnAsync(selfReviewPrompt, ct, stdoutChunkCallback: null);

            // Mark that the turn fired regardless of commit outcome so the
            // audit-iteration metric tags this item with self_review=on.
            sessionLifecycle.MarkPreemptiveSelfReviewRan();

            if (!turnResult.Success)
            {
                _log.LogInformation(
                    "Pre-emptive self-review turn for work item {Id} returned non-success; restoring worktree and continuing to audit.",
                    item.Id);
                CodeyBoxMeters.SessionPreemptiveSelfReviewTurns.Add(1,
                    new KeyValuePair<string, object?>("outcome", "failed"));
                await RestoreSelfReviewWorktreeOrCloseSessionAsync(
                    sessionLifecycle,
                    sandbox,
                    item,
                    project,
                    branch,
                    "self-review turn returned non-success",
                    ct);
                return;
            }

            // Stage anything the self-review turn left dirty, mirroring the
            // work-phase staging policy: strip suggestions.json so the audit
            // branch never carries it.
            await RunWithCancellation(sandbox, ct, "git", "-C", SandboxConventions.WorkDir, "add", "-A");
            await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", SandboxConventions.WorkDir, "rm", "--cached", "--",
                    ".codeybox/suggestions.json"],
            }, ct);

            var staged = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", SandboxConventions.WorkDir, "diff", "--cached", "--quiet"],
            }, ct);
            var hasStagedDiff = staged.ExitCode != 0;

            var observedModelId = ResolveObservedModelId(runner, item.ModelId);
            if (hasStagedDiff)
            {
                var trailerBlock = await ComposeCommitTrailerBlockAsync(
                    item.Id, runner.Kind, observedModelId, ct,
                    promptRevisionAtDispatch: promptRevisionAtDispatch);
                var commitMessage = $"codeybox: pre-emptive self-review fixes\n\n{trailerBlock}";
                await RunWithCancellation(sandbox, ct, "git", "-C", SandboxConventions.WorkDir, "commit", "-m", commitMessage);
            }

            var afterHead = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", SandboxConventions.WorkDir, "rev-parse", "HEAD"],
            }, ct);
            if (!afterHead.Success)
                throw new InvalidOperationException($"Failed to read HEAD after pre-emptive self-review: {afterHead.Stderr}");
            var shaAfter = afterHead.Stdout.Trim();

            if (string.Equals(shaBefore, shaAfter, StringComparison.Ordinal))
            {
                // Self-review turn produced no edits — the work was already clean
                // by the auditor's criteria. That's a successful outcome, not a
                // failure: the formal audit still runs and judges independently.
                CodeyBoxMeters.SessionPreemptiveSelfReviewTurns.Add(1,
                    new KeyValuePair<string, object?>("outcome", "no_changes"));
                return;
            }

            // Stamp the prompt-revision trailer on HEAD if the agent forgot,
            // mirroring the work-phase commit hygiene so the
            // process:prompt-revision-trailer auditor doesn't fire on the extra
            // commit. This also covers the path where the agent created its own
            // clean commit and left no staged diff for the orchestrator to commit.
            await EnsureHeadCarriesPromptRevisionTrailerAsync(
                sandbox, item, runner.Kind, observedModelId,
                promptRevisionAtDispatch, agentPhase: "self-review", ct);

            await PushSandboxWorkBranchWithReconcileAsync(sandbox, branch, ct);

            CodeyBoxMeters.SessionPreemptiveSelfReviewTurns.Add(1,
                new KeyValuePair<string, object?>("outcome", "committed_changes"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Pre-emptive self-review integration failed for work item {Id} session {SessionId}; restoring worktree and continuing to audit without it",
                item.Id, sessionLifecycle.Handle.SessionId);
            if (turnAttempted)
                sessionLifecycle.MarkPreemptiveSelfReviewRan();
            CodeyBoxMeters.SessionPreemptiveSelfReviewTurns.Add(1,
                new KeyValuePair<string, object?>("outcome", "failed"));
            if (turnAttempted)
            {
                await RestoreSelfReviewWorktreeOrCloseSessionAsync(
                    sessionLifecycle,
                    sandbox,
                    item,
                    project,
                    branch,
                    "self-review integration failed",
                    ct);
            }
        }
    }

    private async Task RestoreSelfReviewWorktreeOrCloseSessionAsync(
        ClaudeSessionLifecycle sessionLifecycle,
        ISandbox sandbox,
        WorkItem item,
        Project project,
        string branch,
        string reason,
        CancellationToken ct)
    {
        try
        {
            await RunWithCancellation(sandbox, ct, "git", "-C", SandboxConventions.WorkDir, "fetch", "origin", branch);
            await RunWithCancellation(sandbox, ct, "git", "-C", SandboxConventions.WorkDir, "reset", "--hard", $"origin/{branch}");
            await RunWithCancellation(sandbox, ct, "git", "-C", SandboxConventions.WorkDir, "clean", "-fdx");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception cleanupEx)
        {
            _log.LogWarning(cleanupEx,
                "Failed to restore worktree after pre-emptive self-review failure for work item {Id}; closing session and degrading future turns to fresh sandboxes",
                item.Id);
            try
            {
                await CloseAmbientClaudeSessionAsync(
                    sessionLifecycle,
                    item,
                    project,
                    $"{reason}; failed to restore self-review worktree");
            }
            catch (Exception closeEx) when (closeEx is not OperationCanceledException)
            {
                _log.LogWarning(closeEx,
                    "Failed to close Claude session after pre-emptive self-review cleanup failure for work item {Id}; continuing to audit already-pushed work branch",
                    item.Id);
            }
            _ambientSessionLifecycle.Value = null;
        }
    }

    /// <summary>
    /// Builds the prompt for the pre-emptive self-review turn. Composed from
    /// the runtime guidance the active auditors contribute (cheating opted
    /// out at the auditor source). Framed as "fix any GENUINE issues" so the
    /// agent acts in good faith rather than maximising compliance — a
    /// compliance-maximising prompt would invite tactical edits that the
    /// independent auditor catches anyway.
    /// </summary>
    internal static string BuildPreemptiveSelfReviewPrompt(
        string guidance,
        int promptRevisionAtDispatch)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(
            "An independent auditor is about to review the changes you just committed in this session. ");
        sb.Append(
            "Before that review fires, take ONE pass over your own diff and fix any GENUINE issues you can see against the criteria below. ");
        sb.Append(
            "This is not a compliance exercise — the auditor catches tactical edits and rubber-stamping. ");
        sb.Append(
            "If your changes are already clean against these criteria, make no edits and exit; that is a perfectly valid outcome.");
        sb.Append("\n\nReview criteria:\n\n");
        sb.Append(guidance);
        sb.Append("\n\n");
        sb.Append(
            $"If you make any edits, commit them on top of the current HEAD. The `{CodeyBoxTrailers.PromptRevisionTrailerKey}` trailer value for any commit you create MUST be the literal integer **{promptRevisionAtDispatch.ToString(System.Globalization.CultureInfo.InvariantCulture)}** (the same revision the prior work turn used). Do not run build / test commands; the orchestrator will run the formal audit gates in its own sandbox after this turn.");
        return sb.ToString();
    }

    private static string PreemptRefFor(WorkItemId id) => $"refs/heads/codeybox/preempt/{id}";

    private async Task CloseAmbientClaudeSessionAsync(
        ClaudeSessionLifecycle lifecycle,
        WorkItem item,
        Project project,
        string reason)
    {
        var sessionId = lifecycle.Handle.SessionId;
        try
        {
            await lifecycle.DisposeAsync();
        }
        catch (Exception ex)
        {
            AuditLog.ClaudeSessionCloseFailed(item.Id, sessionId, ex.Message);
            _log.LogWarning(ex,
                "Claude session close failed for work item {Id} session {SessionId} while {Reason}",
                item.Id, sessionId, reason);
            try
            {
                await _webhooks.PublishAsync(new WebhookEvent
                {
                    Event = "agent.claude_session_close_failed",
                    WorkItem = item,
                    Project = project,
                    Details = new
                    {
                        workItemId = item.Id.ToString(),
                        sessionId,
                        reason,
                        error = ex.Message,
                    },
                }, CancellationToken.None);
            }
            catch
            {
                // Best-effort; the structured audit log above is durable.
            }
            throw;
        }
    }

    private static string ValidatePreemptCheckpoint(WorkItem item, string checkpointRef)
    {
        if (item.AgentTurnResumeCheckpoint is not null)
        {
            AgentTurnCheckpointRef parsed;
            try
            {
                parsed = AgentTurnCheckpointRef.Parse(checkpointRef);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    $"Invalid durable agent-turn checkpoint ref for work item {item.Id}.",
                    ex);
            }
            if (parsed.WorkItemId != item.Id)
            {
                throw new InvalidOperationException(
                    $"Durable agent-turn checkpoint ref belongs to a different work item than {item.Id}.");
            }
            return checkpointRef["refs/heads/".Length..];
        }

        var expected = PreemptRefFor(item.Id);
        if (!string.Equals(checkpointRef, expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Invalid preempt checkpoint ref for work item {item.Id}: {checkpointRef}");
        return checkpointRef["refs/heads/".Length..];
    }

    private static void ValidateAgentTurnResumeCheckpoint(WorkItem item)
    {
        if (item.AgentTurnResumeCheckpoint is not { } checkpoint)
        {
            if (item.AgentTurnRecoveryLease is not null)
            {
                throw new InvalidOperationException(
                    "A retained sandbox lease is present without its agent-turn metadata.");
            }
            return;
        }

        var hasGitCheckpoint = !string.IsNullOrWhiteSpace(item.PreemptCheckpoint);
        var hasRetainedSandbox = item.AgentTurnRecoveryLease is not null;
        if (hasGitCheckpoint == hasRetainedSandbox)
        {
            throw new InvalidOperationException(
                "Agent-turn metadata requires exactly one recovery backing: a Git checkpoint or a retained sandbox lease.");
        }

        if (hasGitCheckpoint)
            _ = ValidatePreemptCheckpoint(item, item.PreemptCheckpoint!);
        if (item.State != checkpoint.ResumeState)
        {
            throw new InvalidOperationException(
                $"Checkpoint state {checkpoint.ResumeState} does not match work item state {item.State}.");
        }

        if (item.PromptRevision != checkpoint.PromptRevision)
        {
            throw new InvalidOperationException(
                "The work item prompt changed after the agent-turn checkpoint was created.");
        }

        if (string.IsNullOrWhiteSpace(item.WorkBranch))
            throw new InvalidOperationException("A durable agent-turn checkpoint requires its work branch.");

        if (item.PreemptedAt is null)
            throw new InvalidOperationException(
                "The durable agent-turn checkpoint is missing its preemption timestamp.");
        if (item.Agent != checkpoint.Agent)
            throw new InvalidOperationException(
                "The durable agent-turn checkpoint agent does not match the authoritative work item.");
        if (!string.Equals(item.AgentInstanceId, checkpoint.AgentInstanceRoute, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The durable agent-turn checkpoint route does not match the authoritative work item.");
        // Model and reasoning are runtime-only routing hints rather than
        // work_items columns. Validate them when a caller carries them, but
        // do not reject an authoritative row merely because a database
        // round-trip omitted those values. Publication validates the original
        // binding, and native/private restore is independently gated by
        // IsExactCheckpointRoute at the consumption sink.
        if (item.ModelId is not null
            && !string.Equals(item.ModelId, checkpoint.ModelId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The durable agent-turn checkpoint model does not match the authoritative work item.");
        if (item.ReasoningMode is not null
            && !string.Equals(item.ReasoningMode, checkpoint.ReasoningMode, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The durable agent-turn checkpoint reasoning mode does not match the authoritative work item.");
    }

    private async Task<WorkItem> ClaimAgentTurnResumeDispatchAsync(
        WorkItem item,
        CancellationToken ct)
    {
        var current = await _store.GetAsync(item.Id, ct) ?? item;
        if (current.AgentTurnResumeCheckpoint is not { } checkpoint)
        {
            throw new AgentTurnResumeClaimConflictException(
                "Durable agent-turn resume checkpoint disappeared before dispatch.");
        }

        try
        {
            ValidateAgentTurnResumeCheckpoint(current);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidAgentTurnResumeCheckpointException(
                "The authoritative durable agent-turn checkpoint changed or became invalid before dispatch.",
                ex);
        }

        if (checkpoint.DispatchClaimId is not null)
        {
            throw new AgentTurnResumeClaimConflictException(
                "Durable agent-turn resume checkpoint is already claimed by another dispatch.");
        }

        var configuredLimit = SessionResumeOptions.MaxResumeAttempts;
        if (configuredLimit <= 0)
        {
            throw new InvalidOperationException(
                "Durable agent-turn resume is disabled by AgentSessionResumeMaxAttempts.");
        }
        if (checkpoint.AttemptCount >= configuredLimit)
        {
            throw new InvalidOperationException(
                $"Durable agent-turn resume reached its configured {configuredLimit}-dispatch limit.");
        }

        var claimedCheckpoint = checkpoint.ClaimDispatch(_dispatchClaimIdFactory());
        var claimed = current with
        {
            AgentTurnResumeCheckpoint = claimedCheckpoint,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var updated = await _store.TryUpdateIfStateAndUpdatedAtAsync(
            claimed,
            current.State,
            current.UpdatedAt,
            ct);
        if (!updated)
        {
            throw new AgentTurnResumeClaimConflictException(
                "Durable agent-turn resume dispatch could not be claimed atomically because the work item changed.");
        }

        // The persisted row keeps the checkpoint's exact provider identity.
        // A same-pickup class fallback may consume only its source tree, so its
        // in-memory dispatch must retain the already-selected fallback route
        // and must not receive another provider's model/reasoning settings.
        return claimed with
        {
            Agent = item.Agent,
            AgentInstanceId = item.AgentInstanceId,
            ModelId = item.ModelId,
            ReasoningMode = item.ReasoningMode,
        };
    }

    private async Task<WorkItem> ClaimAgentTurnResumePreparationAsync(
        WorkItem item,
        CancellationToken ct)
    {
        var current = await _store.GetAsync(item.Id, ct) ?? item;
        if (current.AgentTurnResumeCheckpoint is not { } checkpoint)
        {
            throw new AgentTurnResumeClaimConflictException(
                "Retained-sandbox recovery metadata disappeared before adoption.");
        }

        try
        {
            ValidateAgentTurnResumeCheckpoint(current);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidAgentTurnResumeCheckpointException(
                "The authoritative retained-sandbox checkpoint changed or became invalid before adoption.",
                ex);
        }

        if (current.AgentTurnRecoveryLease is null
            || !string.IsNullOrWhiteSpace(current.PreemptCheckpoint))
        {
            throw new InvalidAgentTurnResumeCheckpointException(
                "Preparation claims are valid only for a retained-sandbox recovery boundary.",
                new InvalidOperationException("Retained-sandbox recovery backing is missing or ambiguous."));
        }
        if (checkpoint.DispatchClaimId is not null)
        {
            throw new AgentTurnResumeClaimConflictException(
                "Retained-sandbox recovery is already claimed by another worker.");
        }

        var claimedCheckpoint = checkpoint.ClaimPreparation(_dispatchClaimIdFactory());
        var claimed = current with
        {
            AgentTurnResumeCheckpoint = claimedCheckpoint,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        if (!await _store.TryUpdateIfStateAndUpdatedAtAsync(
                claimed,
                current.State,
                current.UpdatedAt,
                ct))
        {
            throw new AgentTurnResumeClaimConflictException(
                "Retained-sandbox adoption could not be claimed atomically because the work item changed.");
        }

        return claimed with
        {
            Agent = item.Agent,
            AgentInstanceId = item.AgentInstanceId,
            ModelId = item.ModelId,
            ReasoningMode = item.ReasoningMode,
        };
    }

    private async Task TryReleaseAgentTurnPreparationClaimAsync(
        WorkItem claimedItem,
        CancellationToken ct)
    {
        try
        {
            var expectedClaimId = claimedItem.AgentTurnResumeCheckpoint?.DispatchClaimId;
            if (expectedClaimId is null
                || claimedItem.AgentTurnResumeCheckpoint?.DispatchClaimStage
                    != AgentTurnDispatchClaimStage.Preparation)
            {
                return;
            }

            var current = await _store.GetAsync(claimedItem.Id, ct);
            if (current?.AgentTurnResumeCheckpoint is not { } checkpoint
                || checkpoint.DispatchClaimId != expectedClaimId
                || checkpoint.DispatchClaimStage != AgentTurnDispatchClaimStage.Preparation)
            {
                _log.LogWarning(
                    "Could not release retained-sandbox preparation claim for work item {Id} because its authoritative claim changed",
                    claimedItem.Id);
                return;
            }

            var released = current with
            {
                AgentTurnResumeCheckpoint = checkpoint.ReleaseDispatchClaim(),
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            if (!await _store.TryUpdateIfStateAndUpdatedAtAsync(
                    released,
                    current.State,
                    current.UpdatedAt,
                    ct))
            {
                _log.LogWarning(
                    "Could not release retained-sandbox preparation claim for work item {Id} because its lifecycle state advanced concurrently",
                    claimedItem.Id);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Could not release retained-sandbox preparation claim for work item {Id}",
                claimedItem.Id);
        }
    }

    private async Task TryRefundUndispatchedAgentTurnClaimAsync(
        WorkItem claimedItem,
        CancellationToken ct)
    {
        try
        {
            var expectedClaimId = claimedItem.AgentTurnResumeCheckpoint?.DispatchClaimId;
            if (expectedClaimId is null)
                return;

            var current = await _store.GetAsync(claimedItem.Id, ct);
            if (current?.AgentTurnResumeCheckpoint is not { } checkpoint
                || checkpoint.DispatchClaimId != expectedClaimId)
            {
                _log.LogWarning(
                    "Could not refund undispatched durable resume claim for work item {Id} because its authoritative claim changed",
                    claimedItem.Id);
                return;
            }

            var refunded = current with
            {
                AgentTurnResumeCheckpoint = checkpoint.ReleaseUndispatchedClaim(),
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            if (!await _store.TryUpdateIfStateAndUpdatedAtAsync(
                    refunded,
                    current.State,
                    current.UpdatedAt,
                    ct))
            {
                _log.LogWarning(
                    "Could not refund undispatched durable resume claim for work item {Id} because its lifecycle state advanced concurrently",
                    claimedItem.Id);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Could not refund undispatched durable resume claim for work item {Id}; the infrastructure failure remains authoritative",
                claimedItem.Id);
        }
    }

    private async Task<WorkItem> RefreshAgentTurnResumeCheckpointAsync(
        WorkItem item,
        bool isInitial,
        int? iteration,
        CancellationToken ct)
    {
        var current = await _store.GetAsync(item.Id, ct) ?? item;
        ValidateAgentTurnResumeCheckpoint(current);

        if (current.AgentTurnResumeCheckpoint is { } checkpoint)
        {
            var expectedPhase = isInitial
                ? AgentTurnResumePhase.Work
                : AgentTurnResumePhase.Rework;
            if (checkpoint.Phase != expectedPhase)
            {
                throw new InvalidOperationException(
                    $"Agent-turn checkpoint phase {checkpoint.Phase} cannot resume the {(isInitial ? "work" : "rework")} phase.");
            }

            if (checkpoint.Iteration is { } checkpointIteration
                && checkpointIteration != iteration)
            {
                throw new InvalidOperationException(
                    $"Agent-turn checkpoint iteration {checkpointIteration} does not match dispatched iteration {iteration?.ToString() ?? "(none)"}.");
            }
        }

        // The store is authoritative for checkpoint state. Keep the caller's
        // trial runner/model fields: an in-pickup class fallback may continue
        // from the git checkpoint, but it must never receive another member's
        // native session id.
        return item with
        {
            PreemptedAt = current.PreemptedAt,
            PreemptCheckpoint = current.PreemptCheckpoint,
            AgentTurnResumeCheckpoint = current.AgentTurnResumeCheckpoint,
            AgentTurnRecoveryLease = current.AgentTurnRecoveryLease,
            WorkBranch = current.WorkBranch ?? item.WorkBranch,
        };
    }

    private static bool IsExactCheckpointRoute(
        WorkItem item,
        IAgentRunner runner,
        AgentTurnResumeCheckpoint checkpoint)
        => checkpoint.Agent == runner.Kind
            && string.Equals(
                checkpoint.AgentInstanceRoute,
                CanonicalAgentRouteKey(runner.Kind, item.AgentInstanceId),
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(checkpoint.ModelId, item.ModelId, StringComparison.Ordinal)
            && string.Equals(checkpoint.ReasoningMode, item.ReasoningMode, StringComparison.Ordinal);

    private static async Task RemovePreemptScratchpadFilesAsync(
        ISandbox sandbox,
        CancellationToken ct)
    {
        var removal = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "python3", "-c",
                """
                import os
                import stat
                import sys

                def remove_entry(parent_fd, name):
                    try:
                        entry_stat = os.stat(name, dir_fd=parent_fd, follow_symlinks=False)
                    except FileNotFoundError:
                        return
                    if stat.S_ISDIR(entry_stat.st_mode):
                        child_fd = os.open(
                            name,
                            os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW,
                            dir_fd=parent_fd)
                        try:
                            for child_name in os.listdir(child_fd):
                                remove_entry(child_fd, child_name)
                        finally:
                            os.close(child_fd)
                        os.rmdir(name, dir_fd=parent_fd)
                    else:
                        os.unlink(name, dir_fd=parent_fd)

                root_fd = os.open(sys.argv[1], os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
                try:
                    for entry_name in os.listdir(root_fd):
                        if (entry_name == "scratchpad.tgz"
                                or entry_name.startswith("scratchpad.tgz.tmp")
                                or entry_name.startswith("capture.")
                                or entry_name.startswith("resume.")):
                            remove_entry(root_fd, entry_name)
                finally:
                    os.close(root_fd)
                """,
                SandboxConventions.AgentTurnScratchpadDir,
            ],
            WorkingDirectory = "/",
        }, ct);
        ThrowIfExecutionUnavailable(removal);
        if (!removal.Success)
        {
            throw new InvalidOperationException(
                $"Failed to remove internal preempt scratchpad files (exit {removal.ExitCode}; executionUnavailable={removal.ExecutionUnavailable}).");
        }
    }

    private static async Task<bool> HasMeaningfulAgentChangesAsync(
        ISandbox sandbox,
        string beforeSha,
        string afterSha,
        CancellationToken ct)
    {
        Validation.ValidateCommitSha(beforeSha, nameof(beforeSha));
        Validation.ValidateCommitSha(afterSha, nameof(afterSha));
        if (string.Equals(beforeSha, afterSha, StringComparison.Ordinal))
            return false;

        var diff = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "git", "-C", SandboxConventions.WorkDir,
                "diff", "--quiet", beforeSha, afterSha, "--", ".",
                ":(exclude).codeybox/suggestions.json",
                ":(exclude).codeybox/no-action-required.json",
                .. ReservedLegacyScratchpadExcludePathspecs,
            ],
            MaxStdoutBytes = 4096,
            MaxStderrBytes = 4096,
            KillOnOutputLimit = true,
        }, ct);
        ThrowIfExecutionUnavailable(diff);
        return diff.ExitCode switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidOperationException(
                $"Failed to compare the agent-visible checkpoint tree (exit {diff.ExitCode})."),
        };
    }

    private static async Task<bool> CheckpointContainsMeaningfulAgentChangesAsync(
        ISandbox sandbox,
        string preTurnCommitSha,
        string checkpointCommitSha,
        CancellationToken ct)
    {
        Validation.ValidateCommitSha(preTurnCommitSha, nameof(preTurnCommitSha));
        Validation.ValidateCommitSha(checkpointCommitSha, nameof(checkpointCommitSha));
        var ancestry = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "git", "-C", SandboxConventions.WorkDir,
                "merge-base", "--is-ancestor", preTurnCommitSha, checkpointCommitSha,
            ],
            MaxStdoutBytes = 4096,
            MaxStderrBytes = 4096,
            KillOnOutputLimit = true,
        }, ct);
        ThrowIfExecutionUnavailable(ancestry);
        if (ancestry.ExitCode == 1)
        {
            throw new InvalidDataException(
                "Durable agent-turn checkpoint no longer descends from its pre-turn work-branch commit.");
        }
        if (!ancestry.Success)
        {
            throw new InvalidOperationException(
                $"Failed to verify durable agent-turn checkpoint ancestry (exit {ancestry.ExitCode}).");
        }

        return await HasMeaningfulAgentChangesAsync(
            sandbox,
            preTurnCommitSha,
            checkpointCommitSha,
            ct);
    }

    private const int AgentTurnScratchpadTransferChunkBytes = 1024 * 1024;

    private static async Task<AgentTurnScratchpadArchive> ReadAgentTurnScratchpadArchiveAsync(
        ISandbox sandbox,
        CancellationToken ct)
    {
        var maximumEncodedBytes = checked(((AgentTurnScratchpadArchive.MaximumBytes + 2) / 3) * 4 + 16);
        // Derive the transfer caps from the sandbox's provider-wide output
        // bounds when advertised: a hardcoded base64 cap exceeds the Incus CLI
        // bound and the provider rejects the read before any checkpoint
        // evidence exists. Oversized archives then fail here as truncated
        // output rather than as a thrown guard violation.
        var (transferStdoutBytes, transferStderrBytes) =
            AgentTurnCheckpointLimits.ResolveExecOutputLimits(sandbox, maximumEncodedBytes, 4096);
        var readResult = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "python3", "-c",
                $$"""
                import base64
                import gzip
                import io
                import os
                import stat
                import sys
                import tarfile

                root = sys.argv[1]
                archive_name = sys.argv[2]
                root_fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
                archive_fd = -1
                try:
                    archive_fd = os.open(
                        archive_name,
                        os.O_RDONLY | os.O_NOFOLLOW,
                        dir_fd=root_fd)
                    archive_stat = os.fstat(archive_fd)
                    if not stat.S_ISREG(archive_stat.st_mode):
                        raise ValueError("scratchpad archive is not a regular file")
                    if archive_stat.st_size < 1 or archive_stat.st_size > {{AgentTurnScratchpadArchive.MaximumBytes}}:
                        raise ValueError("scratchpad archive exceeds compressed-byte limit")
                    path_stat = os.stat(archive_name, dir_fd=root_fd, follow_symlinks=False)
                    if (path_stat.st_dev, path_stat.st_ino) != (archive_stat.st_dev, archive_stat.st_ino):
                        raise ValueError("scratchpad archive changed before validation")

                    remaining = archive_stat.st_size
                    chunks = []
                    while remaining:
                        chunk = os.read(archive_fd, min(1024 * 1024, remaining))
                        if not chunk:
                            raise ValueError("scratchpad archive was truncated during validation")
                        chunks.append(chunk)
                        remaining -= len(chunk)
                    if os.read(archive_fd, 1):
                        raise ValueError("scratchpad archive grew during validation")
                    archive_bytes = b"".join(chunks)

                    expanded = io.BytesIO()
                    expanded_bytes = 0
                    with gzip.GzipFile(fileobj=io.BytesIO(archive_bytes), mode="rb") as uncompressed:
                        while True:
                            chunk = uncompressed.read(
                                min(1024 * 1024, {{AgentTurnScratchpadArchive.MaximumExpandedBytes}} + 1 - expanded_bytes))
                            if not chunk:
                                break
                            expanded_bytes += len(chunk)
                            if expanded_bytes > {{AgentTurnScratchpadArchive.MaximumExpandedBytes}}:
                                raise ValueError("scratchpad archive exceeds expanded-byte limit")
                            expanded.write(chunk)
                    if expanded_bytes < 1:
                        raise ValueError("scratchpad archive expands to an empty stream")

                    expanded.seek(0)
                    seen = set()
                    entry_count = 0
                    manifest_seen = False
                    with tarfile.open(fileobj=expanded, mode="r:") as archive:
                        while True:
                            member = archive.next()
                            if member is None:
                                break
                            entry_count += 1
                            if entry_count > {{AgentTurnScratchpadArchive.MaximumEntries}}:
                                raise ValueError("scratchpad archive exceeds entry limit")
                            name = member.name.removeprefix("./").rstrip("/")
                            if name == ".":
                                name = ""
                            if any(ord(character) < 32 or ord(character) == 127 for character in name):
                                raise ValueError("scratchpad archive contains a control character in a path")
                            parts = [part for part in name.split("/") if part]
                            if name.startswith("/") or any(part in (".", "..") for part in parts):
                                raise ValueError("scratchpad archive contains an unsafe path")
                            if len(parts) > {{AgentTurnScratchpadArchive.MaximumPathDepth + 1}}:
                                raise ValueError("scratchpad archive exceeds path-depth limit")
                            if name in seen:
                                raise ValueError("scratchpad archive contains duplicate paths")
                            seen.add(name)
                            if not member.isdir() and not member.isreg():
                                raise ValueError("scratchpad archive contains an unsupported file type")
                            if member.isreg() and member.size > {{AgentTurnScratchpadArchive.MaximumFileBytes}}:
                                raise ValueError("scratchpad archive contains an oversized file")
                            if name == "manifest.tsv":
                                if not member.isreg() or member.size > {{AgentTurnScratchpadArchive.MaximumManifestBytes}}:
                                    raise ValueError("scratchpad archive manifest is invalid")
                                manifest_seen = True
                    if not manifest_seen:
                        raise ValueError("scratchpad archive has no restore manifest")

                    output = sys.stdout.buffer
                    chunk_size = 786432
                    for offset in range(0, len(archive_bytes), chunk_size):
                        output.write(base64.b64encode(archive_bytes[offset:offset + chunk_size]))
                finally:
                    if archive_fd >= 0:
                        os.close(archive_fd)
                    os.close(root_fd)
                """,
                SandboxConventions.AgentTurnScratchpadDir,
                "scratchpad.tgz",
            ],
            WorkingDirectory = "/",
            MaxStdoutBytes = transferStdoutBytes,
            MaxStderrBytes = transferStderrBytes,
            KillOnOutputLimit = true,
        }, ct);
        ThrowIfExecutionUnavailable(readResult);
        if (!readResult.Success || readResult.StdoutLimitExceeded)
        {
            throw new InvalidDataException(
                $"Agent scratchpad archive failed bounded validation (exit {readResult.ExitCode}).");
        }

        try
        {
            return new AgentTurnScratchpadArchive(Convert.FromBase64String(readResult.Stdout));
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Agent scratchpad archive was not valid base64.", ex);
        }
    }

    private static async Task<string> ReadSandboxHeadShaAsync(
        ISandbox sandbox,
        CancellationToken ct)
    {
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", SandboxConventions.WorkDir, "rev-parse", "HEAD"],
            MaxStdoutBytes = 128,
            MaxStderrBytes = 4096,
            KillOnOutputLimit = true,
        }, ct);
        ThrowIfExecutionUnavailable(result);
        if (!result.Success)
            throw new InvalidOperationException($"Failed to read sandbox HEAD (exit {result.ExitCode}).");

        var sha = result.Stdout.Trim();
        Validation.ValidateCommitSha(sha, nameof(sha));
        return sha.ToLowerInvariant();
    }

    private static async Task MigrateAndRemoveLegacyScratchpadArchiveAsync(
        ISandbox sandbox,
        bool migrateArchive,
        CancellationToken ct)
    {
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "python3", "-c",
                $$"""
                import errno
                import gzip
                import io
                import os
                import secrets
                import stat
                import sys
                import tarfile

                work_directory = sys.argv[1]
                scratch_root = sys.argv[2]
                migrate_archive = sys.argv[3] == "1"
                legacy_directory_name = sys.argv[4]
                legacy_archive_name = sys.argv[5]
                legacy_manifest_name = sys.argv[6]
                maximum_archive_bytes = {{AgentTurnScratchpadArchive.MaximumBytes}}
                maximum_expanded_bytes = {{AgentTurnScratchpadArchive.MaximumExpandedBytes}}
                maximum_entries = {{AgentTurnScratchpadArchive.MaximumEntries}}
                maximum_path_depth = {{AgentTurnScratchpadArchive.MaximumPathDepth + 1}}
                maximum_file_bytes = {{AgentTurnScratchpadArchive.MaximumFileBytes}}
                maximum_content_bytes = {{AgentTurnScratchpadArchive.MaximumContentBytes}}
                final_name = "scratchpad.tgz"
                temporary_name = f"scratchpad.tgz.tmp.{secrets.token_hex(16)}"
                removed_entries = [0]

                def remove_entry(parent_fd, name):
                    try:
                        entry_stat = os.stat(name, dir_fd=parent_fd, follow_symlinks=False)
                    except FileNotFoundError:
                        return
                    removed_entries[0] += 1
                    if removed_entries[0] > maximum_entries:
                        raise ValueError("legacy scratchpad cleanup entry limit exceeded")
                    if stat.S_ISDIR(entry_stat.st_mode):
                        child_fd = os.open(
                            name,
                            os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW,
                            dir_fd=parent_fd)
                        try:
                            for child_name in os.listdir(child_fd):
                                remove_entry(child_fd, child_name)
                        finally:
                            os.close(child_fd)
                        os.rmdir(name, dir_fd=parent_fd)
                    else:
                        os.unlink(name, dir_fd=parent_fd)

                root_fd = os.open(
                    scratch_root,
                    os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
                work_fd = -1
                legacy_directory_fd = -1
                legacy_archive_fd = -1
                private_archive_fd = -1
                try:
                    if migrate_archive:
                        for root_name in os.listdir(root_fd):
                            if root_name == final_name or root_name.startswith("scratchpad.tgz.tmp"):
                                remove_entry(root_fd, root_name)

                    work_fd = os.open(
                        work_directory,
                        os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
                    try:
                        legacy_directory_fd = os.open(
                            legacy_directory_name,
                            os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW,
                            dir_fd=work_fd)
                    except OSError as error:
                        if error.errno not in (errno.ENOENT, errno.ENOTDIR, errno.ELOOP):
                            raise

                    if legacy_directory_fd >= 0 and migrate_archive:
                        try:
                            legacy_archive_fd = os.open(
                                legacy_archive_name,
                                os.O_RDONLY | os.O_NOFOLLOW,
                                dir_fd=legacy_directory_fd)
                        except OSError as error:
                            if error.errno not in (errno.ENOENT, errno.ELOOP):
                                raise

                        if legacy_archive_fd >= 0:
                            archive_stat = os.fstat(legacy_archive_fd)
                            if stat.S_ISREG(archive_stat.st_mode):
                                if archive_stat.st_size < 1 or archive_stat.st_size > maximum_archive_bytes:
                                    raise ValueError("legacy scratchpad archive exceeds compressed-byte limit")
                                remaining = archive_stat.st_size
                                chunks = []
                                while remaining:
                                    chunk = os.read(legacy_archive_fd, min(1024 * 1024, remaining))
                                    if not chunk:
                                        raise ValueError("legacy scratchpad archive was truncated")
                                    chunks.append(chunk)
                                    remaining -= len(chunk)
                                if os.read(legacy_archive_fd, 1):
                                    raise ValueError("legacy scratchpad archive grew during migration")
                                archive_bytes = b"".join(chunks)

                                expanded = io.BytesIO()
                                expanded_bytes = 0
                                with gzip.GzipFile(fileobj=io.BytesIO(archive_bytes), mode="rb") as uncompressed:
                                    while True:
                                        chunk = uncompressed.read(
                                            min(1024 * 1024, maximum_expanded_bytes + 1 - expanded_bytes))
                                        if not chunk:
                                            break
                                        expanded_bytes += len(chunk)
                                        if expanded_bytes > maximum_expanded_bytes:
                                            raise ValueError("legacy scratchpad archive exceeds expanded-byte limit")
                                        expanded.write(chunk)
                                if expanded_bytes < 1:
                                    raise ValueError("legacy scratchpad archive expands to an empty stream")

                                expanded.seek(0)
                                entry_count = 0
                                content_bytes = 0
                                seen = set()
                                with tarfile.open(fileobj=expanded, mode="r:") as archive:
                                    while True:
                                        member = archive.next()
                                        if member is None:
                                            break
                                        entry_count += 1
                                        if entry_count > maximum_entries:
                                            raise ValueError("legacy scratchpad archive exceeds entry limit")
                                        name = member.name.removeprefix("./").rstrip("/")
                                        if name == ".":
                                            name = ""
                                        if any(ord(character) < 32 or ord(character) == 127 for character in name):
                                            raise ValueError("legacy scratchpad archive contains an unsafe path")
                                        parts = [part for part in name.split("/") if part]
                                        if (name.startswith("/")
                                                or any(part in (".", "..") for part in parts)
                                                or len(parts) > maximum_path_depth):
                                            raise ValueError("legacy scratchpad archive contains an unsafe path")
                                        if name in seen:
                                            raise ValueError("legacy scratchpad archive contains duplicate paths")
                                        seen.add(name)
                                        if not member.isdir() and not member.isreg():
                                            raise ValueError("legacy scratchpad archive contains an unsupported file type")
                                        if member.isreg():
                                            if member.size > maximum_file_bytes:
                                                raise ValueError("legacy scratchpad archive contains an oversized file")
                                            content_bytes += member.size
                                            if content_bytes > maximum_content_bytes + 2 * {{AgentTurnScratchpadArchive.MaximumManifestBytes}}:
                                                raise ValueError("legacy scratchpad archive exceeds content limit")

                                private_archive_fd = os.open(
                                    temporary_name,
                                    os.O_RDWR | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW,
                                    0o600,
                                    dir_fd=root_fd)
                                written = 0
                                while written < len(archive_bytes):
                                    written += os.write(private_archive_fd, archive_bytes[written:])
                                os.fsync(private_archive_fd)
                                private_stat = os.fstat(private_archive_fd)
                                path_stat = os.stat(
                                    temporary_name,
                                    dir_fd=root_fd,
                                    follow_symlinks=False)
                                if (path_stat.st_dev, path_stat.st_ino) != (private_stat.st_dev, private_stat.st_ino):
                                    raise ValueError("private scratchpad temporary changed before migration")
                                os.replace(
                                    temporary_name,
                                    final_name,
                                    src_dir_fd=root_fd,
                                    dst_dir_fd=root_fd)
                                os.fsync(root_fd)

                    if legacy_directory_fd >= 0:
                        remove_entry(legacy_directory_fd, legacy_archive_name)
                        remove_entry(legacy_directory_fd, legacy_manifest_name)
                finally:
                    if private_archive_fd >= 0:
                        os.close(private_archive_fd)
                    if legacy_archive_fd >= 0:
                        os.close(legacy_archive_fd)
                    if legacy_directory_fd >= 0:
                        os.close(legacy_directory_fd)
                    if work_fd >= 0:
                        os.close(work_fd)
                    try:
                        os.unlink(temporary_name, dir_fd=root_fd)
                    except FileNotFoundError:
                        pass
                    os.close(root_fd)
                """,
                SandboxConventions.WorkDir,
                SandboxConventions.AgentTurnScratchpadDir,
                migrateArchive ? "1" : "0",
                AgentTurnScratchpadArchive.LegacyRepositoryDirectory,
                AgentTurnScratchpadArchive.LegacyArchiveFileName,
                AgentTurnScratchpadArchive.LegacyManifestFileName,
            ],
            WorkingDirectory = "/",
            MaxStdoutBytes = 4096,
            MaxStderrBytes = 4096,
            KillOnOutputLimit = true,
        }, ct);
        ThrowIfExecutionUnavailable(result);
        if (!result.Success || result.OutputLimitExceeded)
        {
            throw new InvalidDataException(
                $"Legacy agent scratchpad migration failed bounded validation (exit {result.ExitCode}).");
        }
    }

    private async Task RestoreAgentTurnScratchpadArchiveAsync(
        WorkItemId workItemId,
        AgentTurnCheckpointRef checkpointRef,
        ISandbox sandbox,
        CancellationToken ct)
    {
        var scratchpadStore = _store as IAgentTurnScratchpadStore
            ?? throw new InvalidOperationException(
                "The work-item store does not provide host-private agent-turn scratchpad storage.");
        var archive = await scratchpadStore.ReadAsync(workItemId, checkpointRef, ct)
            ?? throw new InvalidDataException(
                "The host-private scratchpad archive paired with the durable checkpoint is missing.");
        var archiveBytes = archive.ToArray();
        const string temporaryName = "scratchpad.tgz.tmp";
        const string archiveName = "scratchpad.tgz";

        await RunWithCancellation(
            sandbox,
            ct,
            "python3",
            "-c",
            """
            import os
            import sys

            root_fd = os.open(sys.argv[1], os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
            try:
                for name in sys.argv[2:]:
                    try:
                        os.unlink(name, dir_fd=root_fd)
                    except FileNotFoundError:
                        pass
                temporary_fd = os.open(
                    sys.argv[2],
                    os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW,
                    0o600,
                    dir_fd=root_fd)
                os.close(temporary_fd)
            finally:
                os.close(root_fd)
            """,
            SandboxConventions.AgentTurnScratchpadDir,
            temporaryName,
            archiveName);
        for (var offset = 0; offset < archiveBytes.Length; offset += AgentTurnScratchpadTransferChunkBytes)
        {
            var count = Math.Min(AgentTurnScratchpadTransferChunkBytes, archiveBytes.Length - offset);
            var encodedChunk = Convert.ToBase64String(archiveBytes, offset, count);
            var write = await sandbox.ExecAsync(new SandboxExec
            {
                Argv =
                [
                    "python3", "-c",
                    $$"""
                    import base64
                    import os
                    import stat
                    import sys

                    root_fd = os.open(sys.argv[1], os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
                    archive_fd = -1
                    try:
                        archive_fd = os.open(
                            sys.argv[2],
                            os.O_WRONLY | os.O_NOFOLLOW,
                            dir_fd=root_fd)
                        archive_stat = os.fstat(archive_fd)
                        path_stat = os.stat(sys.argv[2], dir_fd=root_fd, follow_symlinks=False)
                        if not stat.S_ISREG(archive_stat.st_mode):
                            raise ValueError("scratchpad restore temporary is not a regular file")
                        if (path_stat.st_dev, path_stat.st_ino) != (archive_stat.st_dev, archive_stat.st_ino):
                            raise ValueError("scratchpad restore temporary changed during transfer")
                        offset = int(sys.argv[3])
                        expected_count = int(sys.argv[4])
                        if archive_stat.st_size != offset:
                            raise ValueError("scratchpad restore chunk offset is not contiguous")
                        chunk = base64.b64decode(sys.stdin.buffer.read(), validate=True)
                        if len(chunk) != expected_count:
                            raise ValueError("scratchpad restore chunk length is invalid")
                        if offset + len(chunk) > {{AgentTurnScratchpadArchive.MaximumBytes}}:
                            raise ValueError("scratchpad restore exceeds compressed-byte limit")
                        written = 0
                        while written < len(chunk):
                            written += os.pwrite(archive_fd, chunk[written:], offset + written)
                        os.fsync(archive_fd)
                    finally:
                        if archive_fd >= 0:
                            os.close(archive_fd)
                        os.close(root_fd)
                    """,
                    SandboxConventions.AgentTurnScratchpadDir,
                    temporaryName,
                    offset.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ],
                Stdin = encodedChunk,
                WorkingDirectory = "/",
                MaxStdoutBytes = 4096,
                MaxStderrBytes = 4096,
                KillOnOutputLimit = true,
            }, ct);
            ThrowIfExecutionUnavailable(write);
            if (!write.Success)
                throw new InvalidDataException("A bounded host-private scratchpad chunk could not be restored.");
        }

        await RunWithCancellation(
            sandbox,
            ct,
            "python3",
            "-c",
            """
            import hashlib
            import os
            import stat
            import sys

            root_fd = os.open(sys.argv[1], os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
            archive_fd = -1
            try:
                archive_fd = os.open(sys.argv[2], os.O_RDONLY | os.O_NOFOLLOW, dir_fd=root_fd)
                archive_stat = os.fstat(archive_fd)
                path_stat = os.stat(sys.argv[2], dir_fd=root_fd, follow_symlinks=False)
                if not stat.S_ISREG(archive_stat.st_mode):
                    raise ValueError("scratchpad restore temporary is not a regular file")
                if archive_stat.st_size != int(sys.argv[5]):
                    raise ValueError("scratchpad restore temporary length is invalid")
                if (path_stat.st_dev, path_stat.st_ino) != (archive_stat.st_dev, archive_stat.st_ino):
                    raise ValueError("scratchpad restore temporary changed before publication")
                digest = hashlib.file_digest(os.fdopen(os.dup(archive_fd), "rb"), "sha256").hexdigest()
                if digest != sys.argv[4]:
                    raise ValueError("scratchpad restore checksum is invalid")
                os.replace(sys.argv[2], sys.argv[3], src_dir_fd=root_fd, dst_dir_fd=root_fd)
                os.fsync(root_fd)
            finally:
                if archive_fd >= 0:
                    os.close(archive_fd)
                os.close(root_fd)
            """,
            SandboxConventions.AgentTurnScratchpadDir,
            temporaryName,
            archiveName,
            archive.Sha256,
            archive.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // Cold-tier extraction forwarder: implementation lives on PromptComposer.
    internal static string BuildResumePrompt(string basePrompt, string checkpointRef) =>
        new PromptComposer().BuildResumePrompt(basePrompt, checkpointRef);

    /// <summary>
    /// Appends a session-mode override to the work/rework prompt that pins
    /// the <c>CodeyBox-Prompt-Revision</c> trailer value to the literal
    /// integer resolved at iteration-dispatch time. The session worker VM
    /// is opened once with <c>extraEnvironment: null</c> and reused for
    /// every turn, so the per-iteration <c>CODEYBOX_PROMPT_REVISION</c>
    /// env var the legacy fresh-sandbox path sets is not visible inside
    /// the VM; without this inline directive the agent has an impossible
    /// instruction (read an unset env var) and the orchestrator stamp
    /// would be papering over noisy commits.
    /// </summary>
    internal static string AppendSessionPromptRevisionDirective(string prompt, int revision) =>
        prompt + "\n\n# Session-mode prompt-revision override\n\n"
            + $"The `{CodeyBoxTrailers.PromptRevisionTrailerKey}` trailer value for this turn MUST be the literal integer **{revision}**. "
            + $"(The `{CodeyBoxTrailers.PromptRevisionEnvVar}` environment variable is not available in the session worker VM — use this literal integer instead.)";

}
