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

// PipelineRunner.AgentPreempt.cs — Preempt/checkpoint retention: sandbox retain/convert, scratchpad capture, and preempt signalling.
public sealed partial class PipelineRunner
{
    private async Task ConvertRetainedSandboxToCheckpointAsync(
        WorkItem claimedItem,
        IAgentRunner runner,
        ISandbox sandbox,
        string branch,
        string expectedOrigin,
        bool isInitial,
        int? iteration,
        int promptRevisionAtDispatch,
        CancellationToken ct)
    {
        using var conversionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        conversionCts.CancelAfter(_opts.PreemptCheckpointDrain);
        try
        {
            var current = await _store.GetAsync(claimedItem.Id, conversionCts.Token)
                ?? throw new AgentTurnResumeClaimConflictException(
                    "Retained-sandbox work item disappeared during adoption.");
            ValidateAgentTurnResumeCheckpoint(current);
            if (current.AgentTurnRecoveryLease is null
                || current.AgentTurnResumeCheckpoint?.DispatchClaimId
                    != claimedItem.AgentTurnResumeCheckpoint?.DispatchClaimId
                || current.AgentTurnResumeCheckpoint?.DispatchClaimStage
                    != AgentTurnDispatchClaimStage.Preparation)
            {
                throw new AgentTurnResumeClaimConflictException(
                    "Retained-sandbox preparation ownership changed during adoption.");
            }

            var retainedBranch = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", SandboxConventions.WorkDir, "branch", "--show-current"],
                MaxStdoutBytes = 4096,
                MaxStderrBytes = 4096,
                KillOnOutputLimit = true,
            }, conversionCts.Token);
            ThrowIfExecutionUnavailable(retainedBranch);
            if (!retainedBranch.Success
                || retainedBranch.OutputLimitExceeded
                || !string.Equals(retainedBranch.Stdout.Trim(), branch, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Retained sandbox work tree is not on the expected work-item branch.");
            }

            var retainedOrigin = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", SandboxConventions.WorkDir, "remote", "get-url", "origin"],
                MaxStdoutBytes = 4096,
                MaxStderrBytes = 4096,
                KillOnOutputLimit = true,
            }, conversionCts.Token);
            ThrowIfExecutionUnavailable(retainedOrigin);
            if (!retainedOrigin.Success
                || retainedOrigin.OutputLimitExceeded
                || !string.Equals(retainedOrigin.Stdout.Trim(), expectedOrigin, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Retained sandbox Git origin does not match the work item's isolated repository.");
            }

            await CaptureAgentScratchpadAsync(
                runner,
                sandbox,
                SandboxConventions.WorkDir,
                conversionCts.Token);

            var checkpoint = CreateAgentTurnResumeCheckpoint(
                claimedItem,
                current,
                runner,
                current.AgentTurnResumeCheckpoint.NativeSessionId,
                isInitial,
                iteration,
                promptRevisionAtDispatch);
            await CheckpointPreemptAsync(
                claimedItem,
                sandbox,
                branch,
                runner.Kind,
                ResolveObservedModelId(runner, claimedItem.ModelId),
                conversionCts.Token,
                checkpoint);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AgentInfrastructureFailureException(
                runner.Kind,
                isInitial ? "work" : "rework",
                $"Timed out converting the retained sandbox to an immutable checkpoint after {_opts.PreemptCheckpointDrain}.",
                ex);
        }
    }

    private static AgentTurnResumeCheckpoint CreateAgentTurnResumeCheckpoint(
        WorkItem dispatchedItem,
        WorkItem persistedItem,
        IAgentRunner runner,
        AgentNativeSessionId? nativeSessionId,
        bool isInitial,
        int? iteration,
        int promptRevisionAtDispatch)
    {
        var previous = persistedItem.AgentTurnResumeCheckpoint;
        return new AgentTurnResumeCheckpoint(
            runner.Kind,
            CanonicalAgentRouteKey(runner.Kind, dispatchedItem.AgentInstanceId),
            dispatchedItem.ModelId,
            dispatchedItem.ReasoningMode,
            nativeSessionId,
            isInitial ? WorkItemState.Working : WorkItemState.Reworking,
            isInitial ? AgentTurnResumePhase.Work : AgentTurnResumePhase.Rework,
            isInitial ? null : iteration ?? 1,
            promptRevisionAtDispatch,
            previous?.CreatedAt ?? DateTimeOffset.UtcNow,
            previous?.AttemptCount ?? 0);
    }

    private async Task CheckpointPreemptAsync(
        WorkItem item,
        ISandbox sandbox,
        string branch,
        AgentKind agentKind,
        string? observedModelId,
        CancellationToken ct,
        AgentTurnResumeCheckpoint? agentTurnResumeCheckpoint = null)
    {
        IAgentTurnScratchpadStore? scratchpadStoreForRollback = null;
        AgentTurnCheckpointRef? savedCheckpointRef = null;
        try
        {
            var beforeCheckpoint = await _store.GetAsync(item.Id, ct) ?? item;
            var effectiveTurnResume = agentTurnResumeCheckpoint
                ?? beforeCheckpoint.AgentTurnResumeCheckpoint;
            var scratchpadStore = effectiveTurnResume is null
                ? null
                : _store as IAgentTurnScratchpadStore
                    ?? throw new InvalidOperationException(
                        "The work-item store does not provide host-private agent-turn scratchpad storage.");

            AgentTurnScratchpadArchive? scratchpadArchive = null;
            if (effectiveTurnResume is not null)
            {
                scratchpadArchive = await ReadAgentTurnScratchpadArchiveAsync(sandbox, ct);
                // Provider session state is host-private. The Git checkpoint
                // carries only the dirty source tree; the archive is restored
                // into the exact route's sandbox from SQLite on redispatch.
                await RemovePreemptScratchpadFilesAsync(sandbox, ct);
            }

            await RunWithCancellation(sandbox, ct, "git", "-C", SandboxConventions.WorkDir, "add", "-A");
            var suggestionsRemoval = await sandbox.ExecAsync(new SandboxExec
            {
                Argv =
                [
                    "git", "-C", SandboxConventions.WorkDir,
                    "rm", "--cached", "--ignore-unmatch", "--",
                    ".codeybox/suggestions.json",
                ],
                MaxStdoutBytes = 4096,
                MaxStderrBytes = 4096,
                KillOnOutputLimit = true,
            }, ct);
            ThrowIfExecutionUnavailable(suggestionsRemoval);
            if (!suggestionsRemoval.Success || suggestionsRemoval.OutputLimitExceeded)
                throw new InvalidOperationException("Failed to remove suggestions.json from the preempt checkpoint index.");
            // Keep the internal agent-log scratch dir out of the preempt checkpoint
            // commit too — it is pushed to a remote ref and the tree becomes the
            // resumed work tree, so an unredacted glog here leaks just like the PR.
            await StripAgentLogScratchFromIndexAsync(sandbox, ct);
            await StripReservedScratchpadPathsFromIndexAsync(sandbox, ct);
            var trailerBlock = await ComposeCommitTrailerBlockAsync(item.Id, agentKind, observedModelId, ct);
            await RunWithCancellation(sandbox, ct, "git", "-C", SandboxConventions.WorkDir, "commit", "--allow-empty", "-m",
                $"codeybox: preempt checkpoint {item.Title}\n\n{trailerBlock}");
            await EnsureReservedScratchpadPathsAbsentFromTreeAsync(sandbox, ct);
            var sourceCommitSha = await ReadSandboxHeadShaAsync(sandbox, ct);
            var typedCheckpointRef = scratchpadArchive is null
                ? null
                : AgentTurnCheckpointRef.Create(item.Id, sourceCommitSha, scratchpadArchive);
            var checkpointRef = typedCheckpointRef?.Value ?? PreemptRefFor(item.Id);
            if (typedCheckpointRef is not null)
            {
                await scratchpadStore!.SaveAsync(
                    item.Id,
                    typedCheckpointRef,
                    scratchpadArchive!,
                    ct);
                scratchpadStoreForRollback = scratchpadStore;
                savedCheckpointRef = typedCheckpointRef;
            }
            await RunWithCancellation(sandbox, ct, "git", "-C", SandboxConventions.WorkDir, "push", "origin", $"HEAD:{checkpointRef}");
            await sandbox.SyncStateToHostAsync(ct);

            var current = await _store.GetAsync(item.Id, ct) ?? item;
            var preempted = current with
            {
                State = effectiveTurnResume?.ResumeState
                    ?? (current.State is WorkItemState.Reworking ? WorkItemState.Reworking : WorkItemState.Working),
                Agent = effectiveTurnResume?.Agent ?? current.Agent,
                AgentInstanceId = effectiveTurnResume?.AgentInstanceRoute ?? current.AgentInstanceId,
                ModelId = effectiveTurnResume?.ModelId ?? current.ModelId,
                ReasoningMode = effectiveTurnResume?.ReasoningMode ?? current.ReasoningMode,
                WorkBranch = branch,
                PreemptedAt = DateTimeOffset.UtcNow,
                PreemptCheckpoint = checkpointRef,
                AgentTurnResumeCheckpoint = effectiveTurnResume,
                AgentTurnRecoveryLease = null,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            var published = typedCheckpointRef is null
                ? await _store.TryUpdateIfStateAndUpdatedAtAsync(
                    preempted,
                    current.State,
                    current.UpdatedAt,
                    ct)
                : await scratchpadStore!.TryPublishAsync(
                    preempted,
                    current.State,
                    current.UpdatedAt,
                    typedCheckpointRef,
                    ct);
            if (!published)
            {
                throw new InvalidOperationException(
                    "Work item changed while its preempt checkpoint was being published.");
            }
            _log.LogInformation("Work item {Id} checkpointed for restart preemption at {Ref}", item.Id, checkpointRef);
            CodeyBoxMeters.AgentTurnCheckpoints.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "committed"),
                new KeyValuePair<string, object?>("stage", "publish"));
        }
        catch (OperationCanceledException)
        {
            await TryDeleteUncommittedAgentTurnScratchpadAsync(
                item.Id,
                scratchpadStoreForRollback,
                savedCheckpointRef);
            throw;
        }
        catch (Exception ex)
        {
            await TryDeleteUncommittedAgentTurnScratchpadAsync(
                item.Id,
                scratchpadStoreForRollback,
                savedCheckpointRef);
            if (AgentTurnCheckpointLimits.ShouldReportDegraded(ex))
                AgentTurnCheckpointLimits.ReportDegraded(item.Id, "publish", ex, _log);
            _log.LogError(ex, "Preempt checkpoint commit failed for work item {Id}; not marking checkpoint valid", item.Id);
            throw;
        }
    }

    private async Task TryDeleteUncommittedAgentTurnScratchpadAsync(
        WorkItemId workItemId,
        IAgentTurnScratchpadStore? scratchpadStore,
        AgentTurnCheckpointRef? checkpointRef)
    {
        if (scratchpadStore is null || checkpointRef is null)
            return;

        try
        {
            _ = await scratchpadStore.DeleteAsync(
                workItemId,
                checkpointRef,
                CancellationToken.None);
        }
        catch (Exception cleanupEx)
        {
            _log.LogWarning(
                cleanupEx,
                "Failed deleting uncommitted private agent-turn scratchpad for work item {Id}; startup reconciliation will retry",
                workItemId);
        }
    }

    private async Task<bool> RequestAgentPreemptWithDeadlineAsync(
        IAgentRunner runner,
        ISandbox sandbox,
        string workingDirectory,
        CancellationToken shutdownDeadlineToken)
    {
        using var preemptCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownDeadlineToken);
        var preemptTask = RequestAgentPreemptAsync(runner, sandbox, workingDirectory, preemptCts.Token);
        var timeoutTask = Task.Delay(_opts.AgentPreemptSignalTimeout, shutdownDeadlineToken);
        var completed = await Task.WhenAny(preemptTask, timeoutTask);

        if (completed == preemptTask)
        {
            try
            {
                await preemptTask;
            }
            catch (OperationCanceledException ex)
            {
                _log.LogWarning(ex, "Best-effort agent preempt signal was canceled");
            }
            return true;
        }

        try { await preemptCts.CancelAsync(); } catch { }
        _log.LogWarning("Best-effort agent preempt signal exceeded timeout {Timeout}", _opts.AgentPreemptSignalTimeout);
        completed = await Task.WhenAny(
            preemptTask,
            Task.Delay(_opts.AgentPreemptSignalTimeout, CancellationToken.None));
        if (completed != preemptTask)
        {
            _log.LogError(
                "Agent preempt capture did not terminate within {Timeout} after cancellation; checkpoint publication will be refused",
                _opts.AgentPreemptSignalTimeout);
            return false;
        }

        try { await preemptTask; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Best-effort agent preempt signal failed after timeout");
        }
        catch (OperationCanceledException) { }
        return true;
    }

    private async Task RequestAgentPreemptAsync(
        IAgentRunner runner,
        ISandbox sandbox,
        string workingDirectory,
        CancellationToken ct)
    {
        try
        {
            await CaptureAgentScratchpadAsync(runner, sandbox, workingDirectory, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Best-effort agent preempt signal failed");
        }
    }

    private static async Task CaptureAgentScratchpadAsync(
        IAgentRunner runner,
        ISandbox sandbox,
        string workingDirectory,
        CancellationToken ct)
    {
        if (runner is IPreemptibleAgentRunner preemptible)
        {
            await preemptible.RequestPreemptAsync(sandbox, workingDirectory, ct);
            return;
        }

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "python3", "-c",
                $$"""
                import io
                import os
                import secrets
                import stat
                import sys
                import tarfile

                root = sys.argv[1]
                final_name = "scratchpad.tgz"
                temporary_name = f"scratchpad.tgz.tmp.{secrets.token_hex(16)}"
                root_fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
                archive_fd = -1
                try:
                    try:
                        os.unlink(final_name, dir_fd=root_fd)
                    except FileNotFoundError:
                        pass
                    for stale_name in os.listdir(root_fd):
                        if stale_name.startswith("scratchpad.tgz.tmp."):
                            try:
                                os.unlink(stale_name, dir_fd=root_fd)
                            except FileNotFoundError:
                                pass
                    archive_fd = os.open(
                        temporary_name,
                        os.O_RDWR | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW,
                        0o600,
                        dir_fd=root_fd)
                    with os.fdopen(os.dup(archive_fd), "wb", closefd=True) as archive_file:
                        with tarfile.open(fileobj=archive_file, mode="w:gz") as archive:
                            for name, content in (
                                ("manifest.tsv", b""),
                                ("manifest.txt", b"Preempt requested; this runner has no CLI scratchpad hook.\n")):
                                info = tarfile.TarInfo(name)
                                info.mode = 0o600
                                info.size = len(content)
                                archive.addfile(info, io.BytesIO(content))
                    os.fsync(archive_fd)
                    archive_stat = os.fstat(archive_fd)
                    if not stat.S_ISREG(archive_stat.st_mode):
                        raise ValueError("fallback scratchpad archive is not a regular file")
                    if archive_stat.st_size < 1 or archive_stat.st_size > {{AgentTurnScratchpadArchive.MaximumBytes}}:
                        raise ValueError("fallback scratchpad archive exceeds compressed-byte limit")
                    path_stat = os.stat(temporary_name, dir_fd=root_fd, follow_symlinks=False)
                    if (path_stat.st_dev, path_stat.st_ino) != (archive_stat.st_dev, archive_stat.st_ino):
                        raise ValueError("fallback scratchpad archive changed before publication")
                    os.replace(
                        temporary_name,
                        final_name,
                        src_dir_fd=root_fd,
                        dst_dir_fd=root_fd)
                    os.fsync(root_fd)
                finally:
                    if archive_fd >= 0:
                        os.close(archive_fd)
                    try:
                        os.unlink(temporary_name, dir_fd=root_fd)
                    except FileNotFoundError:
                        pass
                    os.close(root_fd)
                """,
                SandboxConventions.AgentTurnScratchpadDir,
            ],
            WorkingDirectory = "/",
        }, ct);
        ThrowIfExecutionUnavailable(result);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"Agent scratchpad capture failed (exit {result.ExitCode}).");
        }
    }

    private static Task WaitForCancellationAsync(CancellationToken ct)
    {
        if (!ct.CanBeCanceled)
            return Task.Delay(Timeout.InfiniteTimeSpan);
        if (ct.IsCancellationRequested)
            return Task.CompletedTask;

        return WaitForCancellationCoreAsync(ct);
    }

    private static async Task WaitForCancellationCoreAsync(CancellationToken ct)
    {
        try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
        catch (OperationCanceledException) { }
    }


    // Returns a 2 KB tail of agent output for inclusion in audit log events.
    private static string? Tail(string? s)
    {
        const int max = 2000;
        return string.IsNullOrEmpty(s) ? null : s.Length <= max ? s : "…" + s[^max..];
    }

    private static string RedactAndTruncateAgentDetail(string s)
        => SanitizedAgentDetail.FromRaw(s).Value;

    private async Task<AgentFailureClassification?> RecordAvailabilityOutcomeAsync(
        IAgentAvailabilityRegistry registry,
        IAgentRunner runner,
        AgentResult result,
        TimeSpan duration,
        WorkItem item,
        Project project,
        string sandboxId,
        string phase,
        AgentResult? classificationResult = null)
    {
        if (!result.Success)
        {
            var classification = _authFailureClassifier.ClassifyFailure(runner, classificationResult ?? result);
            if (classification.Kind is AgentFailureKind.Infrastructure
                or AgentFailureKind.TransientNetwork
                or AgentFailureKind.AuthRequired
                or AgentFailureKind.AuthError)
            {
                if (classification.Kind == AgentFailureKind.Infrastructure)
                {
                    AuditLog.SandboxAgentInfrastructureFailure(
                        item.Id,
                        runner.Kind,
                        sandboxId,
                        phase,
                        result.Summary,
                        classification.Reason);
                }

                _log.LogWarning(
                    "Agent {Agent} {Kind} failure in sandbox {Sandbox} during {Phase}; skipping fast-fail breaker: {Summary} ({Reason})",
                    runner.Kind.Value,
                    classification.Kind,
                    sandboxId,
                    phase,
                    result.Summary,
                    classification.Reason);
                return classification;
            }
        }

        // Feed the availability registry so the fast-fail circuit breaker can
        // exclude an agent that genuinely exits non-zero in under
        // FastFailThresholdSeconds for MaxConsecutiveFastFails attempts in a
        // row. Infrastructure-shaped failures are filtered above because they
        // belong to sandbox/provisioning health, not agent availability.
        var transition = registry.RecordRunOutcome(runner.Kind, result.Success, duration);
        if (!transition.PreviouslyExcluded && transition.NowExcluded)
        {
            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "agent.smoke_failed",
                WorkItem = item,
                Project = project,
                Details = new AgentSmokeFailedDetails
                {
                    AgentKind = runner.Kind.Value,
                    Reason = transition.Reason,
                    // Fast-fail circuit-breaker exclusions are persistent by
                    // construction: the binary launched, exited non-zero fast,
                    // and did so repeatedly. A retry without operator
                    // intervention will produce the same outcome.
                    Category = SmokeFailureCategory.Persistent,
                },
            }, CancellationToken.None);
        }
        return null;
    }

    /// <summary>
    /// Feeds a "clean exit, working tree unchanged" outcome into the no-changes
    /// circuit breaker and fires an operator alert when the breaker newly
    /// excludes the agent. The exception that surfaces the no-changes outcome
    /// to the caller is thrown by the caller AFTER this method runs, so the
    /// alert is dispatched even on the trip-and-throw path.
    /// </summary>
    private async Task RecordNoChangesOutcomeAsync(
        AgentKind kind,
        WorkItem item,
        Project project)
    {
        if (_availability is not { } registry) return;
        var transition = registry.RecordNoChangesOutcome(kind, item.Id);
        if (!transition.PreviouslyExcluded && transition.NowExcluded)
        {
            AuditLog.AgentNoChangesBreakerTripped(
                kind,
                consecutiveDistinctItems: registry
                    .Snapshot()
                    .FirstOrDefault(s => s.Agent == kind)?.ConsecutiveNoChanges ?? 0,
                reason: transition.Reason);
            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "agent.smoke_failed",
                WorkItem = item,
                Project = project,
                Details = new AgentSmokeFailedDetails
                {
                    AgentKind = kind.Value,
                    Reason = transition.Reason,
                    // Silent-failure exclusions are persistent by construction:
                    // an agent that produced N empty diffs in a row needs
                    // operator diagnosis (auth, capability collapse, or a new
                    // failure shape) — retrying without intervention will
                    // produce the same outcome.
                    Category = SmokeFailureCategory.Persistent,
                },
            }, CancellationToken.None);
        }
    }

    /// <summary>
    /// Logs a truncated tail of agent stdout/stderr at Information level.
    /// Truncated because agent output can be tens of KB; the tail is
    /// usually where the conclusion / "I'm done" / refusal message lives.
    /// </summary>
    private static void LogAgentOutput(ILogger log, AgentKind kind, AgentResult result)
    {
        static string Display(string? s) => string.IsNullOrEmpty(s) ? "(empty)" : s;
        log.LogInformation(
            "Agent {Kind} finished: success={Success} exit={Summary}\nstdout-tail:\n{StdoutTail}\nstderr-tail:\n{StderrTail}",
            kind.Value, result.Success, result.Summary, Display(Tail(result.Stdout)), Display(Tail(result.Stderr)));
    }

    private static bool NeedsStructuredStreamForSessionResume(IAgentRunner runner)
        => runner is ICliSessionResumableAgentRunner
        {
            RequiresStructuredStreamForSessionId: true,
        };

    private async Task<bool> HandleAuthRequiredOutputAsync(
        WorkItem? item,
        Project project,
        AgentKind agent,
        string phase,
        string? stdout,
        string? stderr,
        bool throwOnMatch,
        bool requireStdoutOnlyCorroboration = false,
        CancellationToken ct = default)
    {
        var detection = _authFailureClassifier.DetectDetailed(agent, stderr, stdout);
        if (detection is null || detection.Classification.Kind != AgentFailureKind.AuthRequired)
            return false;

        var handling = await HandleAuthRequiredDetectionAsync(
            item,
            project,
            agent,
            phase,
            detection.Classification,
            throwOnMatch,
            stdoutOnlyEvidence: detection.IsStdoutOnly,
            requireStdoutOnlyCorroboration: requireStdoutOnlyCorroboration,
            matchedConfiguredPattern: detection.MatchedConfiguredStderrPattern
                || detection.MatchedConfiguredStdoutPattern,
            ct: ct);
        return handling.Matched;
    }

    private async Task<AuthRequiredHandlingResult> HandleAuthRequiredDetectionAsync(
        WorkItem? item,
        Project project,
        AgentKind agent,
        string phase,
        AgentFailureClassification classification,
        bool throwOnMatch,
        bool stdoutOnlyEvidence = false,
        bool requireStdoutOnlyCorroboration = false,
        bool requireAuthCorroboration = false,
        bool matchedConfiguredPattern = false,
        CancellationToken ct = default)
    {
        if (classification.Kind != AgentFailureKind.AuthRequired)
            return AuthRequiredHandlingResult.NotMatched;

        var publishSideEffects = true;
        string? authCorroborationNote = null;
        if (requireAuthCorroboration || stdoutOnlyEvidence && requireStdoutOnlyCorroboration)
        {
            var corroboration = await TryCorroborateAuthRequiredAsync(item, project, agent, phase, ct);
            // Fail-CLOSED on the irreversible fleet-wide "operator action
            // required" bench: only POSITIVELY corroborated stdout-only
            // evidence (or generic captured auth output that explicitly asked
            // for the same corroboration) escalates to a global bench. Both
            // NotCorroborated and Unavailable (e.g. in-VM smoke disabled)
            // degrade to item-level handling — the resolver reroutes to another
            // class member — rather than benching a possibly-authenticated
            // agent fleet-wide on uncorroborated agent output.
            publishSideEffects = corroboration == AuthRequiredCorroboration.Corroborated;
            authCorroborationNote = corroboration switch
            {
                AuthRequiredCorroboration.Corroborated =>
                    "auth evidence corroborated by forced in-VM smoke probe for global benching",
                AuthRequiredCorroboration.NotCorroborated =>
                    "auth evidence accepted for item failure only; forced in-VM smoke probe did not corroborate auth",
                _ =>
                    "auth evidence NOT corroborated (forced in-VM smoke unavailable); item-level failure only, no fleet-wide bench",
            };
        }

        if (publishSideEffects
            && !matchedConfiguredPattern
            && await IsAuthContradictedByHealthyQuotaProbeAsync(item, project, agent, item?.ModelId, ct).ConfigureAwait(false))
        {
            // The quota probe authenticates with the same credential the failed
            // run used and just read healthy: the credential is valid, so the
            // captured text is the agent's narration (e.g. credential-handling
            // work quoting auth shapes), not a harness refusal. Fail the item
            // without the fleet-wide exclusion — one item's output must not
            // bench the agent kind with operator-only recovery. Explicitly
            // operator-configured patterns are exempt: the operator asserted
            // that text means auth failure for the agent, so a heuristic
            // reading does not overrule it.
            publishSideEffects = false;
            authCorroborationNote = authCorroborationNote is null
                ? "credential reads healthy on quota probe; item-level failure only, no fleet-wide bench"
                : $"{authCorroborationNote}; credential reads healthy on quota probe; item-level failure only, no fleet-wide bench";
        }

        var reason = _authRequiredHandler.BuildReason(phase, classification, stdoutOnlyEvidence, authCorroborationNote);
        var scope = publishSideEffects
            ? WorkItemAuthFailureScope.Fleet
            : WorkItemAuthFailureScope.Item;

        if (publishSideEffects)
            await _authRequiredHandler.PublishSideEffectsAsync(agent, reason, item, project, ct: ct);

        if (throwOnMatch)
            throw new AgentAuthRequiredException(agent, phase, reason, scope);

        return new AuthRequiredHandlingResult(true, reason, scope);
    }

    private readonly record struct AuthRequiredHandlingResult(
        bool Matched,
        string? Reason,
        WorkItemAuthFailureScope? Scope)
    {
        public static AuthRequiredHandlingResult NotMatched { get; } = new(false, null, null);
    }

}
