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

// PipelineRunner.ConflictRework.cs — Conflict-rework loop: rework iteration, agent outcome, push/stat, and prompt building.
public sealed partial class PipelineRunner
{
    /// <summary>
    /// Runs the focused conflict-rework iteration: re-engages the original
    /// work agent (same class) on the existing work branch with a prompt that
    /// explicitly preserves prior commits and resolves the upstream conflict.
    ///
    /// <para>
    /// Contract — must hold at agent invocation:
    ///   1. HEAD is the existing work branch tip (not main).
    ///   2. A rebase against current <paramref name="baseBranch"/> is in progress
    ///      and paused at the conflict; conflict markers are in the worktree
    ///      and the index is in a conflicted state.
    ///   3. No commits have been discarded; <c>git log HEAD..ORIG_HEAD</c>
    ///      lists the work agent's prior commits.
    /// </para>
    ///
    /// <para>
    /// Anti-abandonment guard: after the agent finishes, every commit that
    /// was on the work branch before the rework must remain in the new tip's
    /// ancestry. A destructive action (typically <c>git reset --hard</c> /
    /// <c>git rebase --abort</c> / <c>git checkout main</c>) fails this check
    /// and the item parks instead of force-pushing a salvage.
    /// </para>
    /// </summary>
    private async Task<ConflictReworkResult> RunConflictReworkIterationAsync(
        WorkItem item,
        Project project,
        IAgentRunner runner,
        string repoId,
        string baseBranch,
        string workBranch,
        MergeConflictResolutionFailedException originalFailure,
        CancellationToken ct,
        CancellationToken hostShutdownToken,
        bool countAttempt = true)
    {
        var startedAt = DateTimeOffset.UtcNow;

        string baseTip;
        string priorWorkTip;
        try
        {
            baseTip = await _gitHost.ResolveCommitAsync(repoId, baseBranch, ct);
            priorWorkTip = await _gitHost.ResolveCommitAsync(repoId, workBranch, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex,
                "Conflict rework: could not resolve branch SHAs for work item {Id}; declining to engage agent",
                item.Id);
            return new ConflictReworkResult(false,
                $"could not resolve branch tips before conflict rework: {ex.Message}");
        }

        await Transition(item, WorkItemState.ReworkingForConflict, ct, project);

        var smokeTarget = ResolvePhaseSmokeTarget(project, "rework", item.BaselineImageRef);
        var smokeAvailability = await EnsureAgentSmokeAvailableAsync(runner.Kind, smokeTarget, ct);
        if (!smokeAvailability.Available)
        {
            if (IsOperatorPaused(smokeAvailability))
            {
                var pausedReason = smokeAvailability.Reason ?? AgentDispatchAvailability.PausedReasonPrefix;
                throw new AgentPausedException(ConflictReworkPhaseKey, runner.Kind, pausedReason);
            }

            if (countAttempt)
            {
                var bumped = await BumpConflictReworkAttemptsAsync(item, ct);
                item = bumped ?? item;
            }

            return new ConflictReworkResult(
                false,
                $"in-VM smoke gate: {smokeAvailability.Reason ?? "unavailable"}",
                FailureKind: WorkItemFailureKinds.AgentUnavailable,
                Agent: runner.Kind);
        }

        if (countAttempt)
        {
            var bumped = await BumpConflictReworkAttemptsAsync(item, ct);
            item = bumped ?? item;
        }

        // Capture the file-set the work agent's prior commits modified. `git
        // rebase` re-creates commits with new SHAs so an ancestor-SHA check
        // doesn't survive a clean rebase; the *changed-file set* does. A
        // destructive `git reset --hard origin/<base>` produces an empty new
        // diff while the prior diff is non-empty, which is the anti-
        // abandonment signal.
        IReadOnlyList<string> priorChangedFiles;
        try
        {
            priorChangedFiles = await ListChangedFilesAsync(repoId, baseTip, priorWorkTip, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex,
                "Conflict rework: could not enumerate prior changed files on '{Branch}' for work item {Id}",
                workBranch, item.Id);
            priorChangedFiles = [];
        }

        var conflictReworkStartedPublished = false;

        async Task PublishStartedAsync(IReadOnlyList<string> conflictFiles)
        {
            if (conflictReworkStartedPublished)
                return;

            conflictReworkStartedPublished = true;
            await TryPublishEventAsync(item, project, "work_item.conflict_rework_started",
                new ConflictReworkStartedDetails
                {
                    WorkItemId = item.Id.ToString(),
                    BaseBranch = baseBranch,
                    WorkBranch = workBranch,
                    WorkBranchTip = priorWorkTip,
                    BaseTip = baseTip,
                    ConflictFiles = conflictFiles,
                }, ct);
        }

        async Task PublishFinishedAfterStartedAsync(
            bool success,
            string? newTip,
            IReadOnlyList<string>? filesChanged,
            int? insertions,
            int? deletions,
            string? semanticIncompatible,
            string? parkReason)
        {
            if (!conflictReworkStartedPublished)
                await PublishStartedAsync(Array.Empty<string>());

            await PublishConflictReworkFinishedAsync(item, project, baseBranch, workBranch,
                success, newTip, filesChanged, insertions, deletions, semanticIncompatible, parkReason, ct);
        }

        ConflictReworkAgentOutcome outcome;
        try
        {
            outcome = await RunConflictReworkAgentAsync(
                item, project, runner, repoId, baseBranch, workBranch,
                priorWorkTip, originalFailure, PublishStartedAsync, ct, hostShutdownToken);
        }
        catch (TerminalTransientNetworkError ex)
        {
            _log.LogWarning(ex,
                "Conflict rework agent invocation hit transient transport failure for work item {Id}: {Message}",
                item.Id, ex.Message);
            await PublishFinishedAfterStartedAsync(
                success: false, newTip: null, filesChanged: null,
                insertions: null, deletions: null, semanticIncompatible: null,
                parkReason: ex.Message);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException
            && ex is not SandboxProvisioningDeferredException
            && ex is not AgentPausedException
            && ex is not AgentAuthRequiredException
            && ex is not AgentInfrastructureFailureException)
        {
            _log.LogWarning(ex,
                "Conflict rework agent invocation failed for work item {Id}: {Message}",
                item.Id, ex.Message);
            await PublishFinishedAfterStartedAsync(
                success: false, newTip: null, filesChanged: null,
                insertions: null, deletions: null, semanticIncompatible: null,
                parkReason: ex.Message);
            return new ConflictReworkResult(false,
                $"conflict-rework agent failed: {ex.Message}");
        }

        if (outcome.SemanticIncompatibleReason is not null)
        {
            var parkMsg = $"{PromptComposer.SemanticIncompatibleMarker} {outcome.SemanticIncompatibleReason}";
            _log.LogWarning(
                "Work item {Id} conflict-rework declared semantic-incompatible: {Reason}",
                item.Id, outcome.SemanticIncompatibleReason);
            await PublishFinishedAfterStartedAsync(
                success: false, newTip: outcome.NewTip, filesChanged: outcome.FilesChanged,
                insertions: outcome.Insertions, deletions: outcome.Deletions,
                semanticIncompatible: outcome.SemanticIncompatibleReason,
                parkReason: parkMsg);
            return new ConflictReworkResult(false, parkMsg);
        }

        if (!outcome.AgentSucceeded || outcome.NewTip is null)
        {
            var parkMsg = $"conflict-rework agent did not produce a clean resolution: {outcome.FailureReason ?? "agent reported failure"}";
            await PublishFinishedAfterStartedAsync(
                success: false, newTip: outcome.NewTip, filesChanged: outcome.FilesChanged,
                insertions: outcome.Insertions, deletions: outcome.Deletions,
                semanticIncompatible: null, parkReason: parkMsg);
            return new ConflictReworkResult(
                false,
                parkMsg,
                FailureKind: outcome.FailureKind,
                Agent: outcome.Agent);
        }

        // Anti-abandonment guard: the file-set the work agent touched
        // (relative to `baseTip`) must remain reflected in the rework's diff
        // against the same base. A clean rebase preserves these — the SHAs
        // change but the files do not. A destructive `git reset --hard
        // origin/<base>` produces an empty diff and trips this check.
        if (priorChangedFiles.Count > 0)
        {
            IReadOnlyList<string> newChangedFiles;
            try
            {
                newChangedFiles = await ListChangedFilesAsync(repoId, baseTip, outcome.NewTip, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex,
                    "Conflict rework: could not enumerate rework changed files for work item {Id}; refusing to advance",
                    item.Id);
                var listFailMsg = $"could not verify rework diff for anti-abandonment guard: {ex.Message}";
                await PublishFinishedAfterStartedAsync(
                    success: false, newTip: outcome.NewTip, filesChanged: outcome.FilesChanged,
                    insertions: outcome.Insertions, deletions: outcome.Deletions,
                    semanticIncompatible: null, parkReason: listFailMsg);
                return new ConflictReworkResult(false, listFailMsg);
            }

            var newSet = newChangedFiles.ToHashSet(StringComparer.Ordinal);
            var missing = priorChangedFiles
                .Where(f => !newSet.Contains(f))
                .ToArray();
            if (missing.Length > 0 || newChangedFiles.Count == 0)
            {
                var hint = missing.Length > 0 ? missing[0] : "(all)";
                var parkMsg = $"conflict-rework agent discarded prior commits (e.g. work to {hint} lost); refusing to update work branch";
                _log.LogWarning(
                    "Work item {Id} conflict-rework dropped work-agent changes; missing={Missing}, priorTouched={Prior}, newTouched={New}",
                    item.Id, string.Join(',', missing), priorChangedFiles.Count, newChangedFiles.Count);
                await PublishFinishedAfterStartedAsync(
                    success: false, newTip: outcome.NewTip, filesChanged: outcome.FilesChanged,
                    insertions: outcome.Insertions, deletions: outcome.Deletions,
                    semanticIncompatible: null, parkReason: parkMsg);
                return new ConflictReworkResult(false, parkMsg);
            }
        }

        // All checks passed. Advance the host-side work branch to the new tip
        // so the upcoming RunMergePhase reads it.
        try
        {
            await _gitHost.SetBranchToCommitAsync(repoId, workBranch, outcome.NewTip, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var parkMsg = $"could not advance work branch '{workBranch}' to rework tip {outcome.NewTip}: {ex.Message}";
            _log.LogWarning(ex,
                "Conflict rework: failed to set work branch to rework tip for work item {Id}",
                item.Id);
            await PublishFinishedAfterStartedAsync(
                success: false, newTip: outcome.NewTip, filesChanged: outcome.FilesChanged,
                insertions: outcome.Insertions, deletions: outcome.Deletions,
                semanticIncompatible: null, parkReason: parkMsg);
            return new ConflictReworkResult(false, parkMsg);
        }

        await ResetRecoveryAttemptsAfterRealProgressEventAsync(
            item.Id,
            RecoveryProgressEvent.ConflictReworkBranchAdvanced,
            "conflict-rework-branch-advanced",
            ct);

        _ = startedAt; // currently unused; future: emit conflict_rework duration metric.

        await PublishFinishedAfterStartedAsync(
            success: true, newTip: outcome.NewTip, filesChanged: outcome.FilesChanged,
            insertions: outcome.Insertions, deletions: outcome.Deletions,
            semanticIncompatible: null, parkReason: null);

        return new ConflictReworkResult(true, null);
    }

    /// <summary>
    /// Bundle of state the rework iteration produced for the host to inspect.
    /// </summary>
    private readonly record struct ConflictReworkAgentOutcome(
        bool AgentSucceeded,
        string? NewTip,
        string? FailureReason,
        string? SemanticIncompatibleReason,
        IReadOnlyList<string>? FilesChanged,
        int? Insertions,
        int? Deletions,
        string? FailureKind = null,
        AgentKind? Agent = null);

    /// <summary>
    /// Drives the agent through a single conflict-rework iteration inside an
    /// isolated sandbox. Returns the new branch tip + diff stats on success, or
    /// the failure reason. Does NOT mutate the host bare repo on its own — the
    /// caller is responsible for the anti-abandonment ancestry check and the
    /// final force-update of the host work branch.
    /// </summary>
    private async Task<ConflictReworkAgentOutcome> RunConflictReworkAgentAsync(
        WorkItem item,
        Project project,
        IAgentRunner runner,
        string repoId,
        string baseBranch,
        string workBranch,
        string priorWorkTip,
        MergeConflictResolutionFailedException originalFailure,
        Func<IReadOnlyList<string>, Task> publishStartedAsync,
        CancellationToken ct,
        CancellationToken hostShutdownToken)
    {
        runner = BindMemberRunner(runner, TryResolveSelectedMember(runner.Kind, project, item));
        // The conflict-rework iteration uses an isolated bare repo clone so a
        // destructive agent action (rebase --abort, reset --hard, etc.) cannot
        // damage the durable host bare repo. The caller still verifies prior
        // commits remain in ancestry before applying the result.
        var isolatedRepoPath = await CreateIsolatedMergeRepositoryAsync(repoId, item.Id, ct);
        try
        {
            var credential = await ResolveAgentCredentialAsync(runner.Kind, project, item, ct);
            var access = _gitHost.GetIsolatedRepoSandboxAccess(isolatedRepoPath);
            var conflictReworkTarget = new SandboxTarget(
                project.NetworkProfiles.Rework ?? project.NetworkProfiles.Work,
                SandboxProfileFlavor.Headless);
            var spec = BuildSandboxSpec(access, includeAgentCredential: credential, allowAgentNetwork: true,
                hostNetworkProfile: conflictReworkTarget.NetworkProfile,
                timingWorkItemId: item.Id, timingPhase: ConflictReworkPhaseKey,
                baselineImageRef: SandboxTargetResolver.BaselineRefForTarget(project, conflictReworkTarget, item.BaselineImageRef),
                credentialRunner: runner,
                projectSecretEnvironment: ResolveProjectSecretEnvironment(project, ProjectSandboxSecretScopes.Rework));

            // Release-then-acquire (same pool-deadlock rationale as the merge
            // phase above): conflict rework provisions its own sandbox while a
            // prior phase's reusable sandbox may still be admitted.
            await ReleaseAmbientWorkSandboxAsync().ConfigureAwait(false);
            using var conflictReworkWaitScope = SandboxPermitWaitScope.Begin(item.Id.ToString(), "conflict-rework");
            await using var sandbox = await CreateMergeSandboxWithStagingRestoreAsync(spec, repoId, isolatedRepoPath, ct);
            if (credential is not null && credential.Files.Count > 0)
                await MaterialiseCredentialFilesAsync(sandbox, credential, ct);

            await Run(sandbox, "git", "clone", access.CloneUrlInsideSandbox, SandboxConventions.WorkDir);
            var (gitName, gitEmail) = ResolveGitIdentity(project, _opts.HostGitIdentity, item.Initiator);
            await RunMasked(sandbox, "git", "-C", SandboxConventions.WorkDir, "config", "user.email", gitEmail);
            await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "config", "user.name", gitName);

            // Fetch the work branch + base into the sandbox clone, then check out
            // the work branch at its existing tip and start a rebase against base.
            await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "fetch", "origin", workBranch);
            await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "fetch", "origin", baseBranch);
            await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "checkout", "-B", workBranch, $"origin/{workBranch}");

            // Start the rebase. We expect it to fail with conflicts (that's the
            // whole reason we're here); the agent receives the worktree in that
            // exact paused-at-conflict state.
            var rebaseStart = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", SandboxConventions.WorkDir, "rebase", $"origin/{baseBranch}"],
            }, ct);
            if (rebaseStart.Success)
            {
                // The host saw conflicts, but the sandbox rebase came back
                // clean. Most likely: upstream advanced after the merge phase
                // ran. Treat as a successful no-op resolution and push.
                _log.LogInformation(
                    "Conflict rework: sandbox rebase of '{Work}' onto '{Base}' completed without conflicts; treating as recovered",
                    workBranch, baseBranch);
                return await PushAndStatConflictReworkAsync(sandbox, isolatedRepoPath, repoId, workBranch, priorWorkTip, baseBranch, ct);
            }

            // Verify the rebase is actually paused — guard against transient
            // git errors that aren't conflict-related.
            var statusBefore = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", SandboxConventions.WorkDir, "status", "--porcelain"],
            }, ct);
            if (!statusBefore.Success || string.IsNullOrWhiteSpace(statusBefore.Stdout))
            {
                return new ConflictReworkAgentOutcome(
                    AgentSucceeded: false,
                    NewTip: null,
                    FailureReason: $"rebase failed but status came back clean: {rebaseStart.Stderr.Trim()}",
                    SemanticIncompatibleReason: null,
                    FilesChanged: null, Insertions: null, Deletions: null);
            }

            // Collect the actual sandbox-side conflict file list from git's
            // unmerged index entries. Do not fall back to the merge-phase error
            // string: that text is telemetry, not a safe path source.
            IReadOnlyList<string> sandboxConflictFiles;
            try
            {
                sandboxConflictFiles = await ListSandboxConflictFilesAsync(sandbox, ct);
            }
            catch (MergeConflictResolutionFailedException ex)
            {
                return new ConflictReworkAgentOutcome(
                    AgentSucceeded: false,
                    NewTip: null,
                    FailureReason: $"could not inspect sandbox conflict files: {ex.Message}",
                    SemanticIncompatibleReason: null,
                    FilesChanged: null, Insertions: null, Deletions: null);
            }

            if (sandboxConflictFiles.Count == 0)
            {
                return new ConflictReworkAgentOutcome(
                    AgentSucceeded: false,
                    NewTip: null,
                    FailureReason: "rebase failed but git ls-files reported no unmerged paths",
                    SemanticIncompatibleReason: null,
                    FilesChanged: null, Insertions: null, Deletions: null);
            }

            await publishStartedAsync(sandboxConflictFiles);

            var prompt = _promptComposer.BuildConflictReworkPrompt(
                item.Prompt, baseBranch, workBranch, sandboxConflictFiles, originalFailure.Message);
            prompt = await ProcessAgentPromptAsync(
                item.Id,
                runner.Kind,
                AgentPromptPhase.Rework,
                item.ConflictReworkAttempts,
                project,
                sandbox,
                prompt,
                ct);

            // Run the agent. We use the same agent identity/class as the
            // original work agent (this method's `runner` parameter); the
            // contract is `IAgentRunner.RunAsync`, identical to the work phase.
            using var phase = new PhaseCancellation(ConflictReworkPhaseKey, ct, _opts.TimeProvider);
            var (conflictWorkTimeout, _) = ResolveEffectiveWorkTimeout(item, project);
            phase.SetPhaseTimeout(ResolvePhaseAbsoluteTimeout(conflictWorkTimeout));
            phase.HookHostShutdown(hostShutdownToken, _opts.ShutdownGrace);
            AgentResult agentResult;
            var startedAt = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            // Merge-conflict rework is a distinct agent phase that runs outside the
            // InvokeAgentWithQuotaFallbackAsync chokepoint, so record its
            // involvement row directly — otherwise this real sandbox run would
            // leave no audit-trail entry and break operator attribution.
            var conflictInvolvementId = await RecordInvolvementStartAsync(
                item.Id, runner.Kind, item.AgentInstanceId, item.ModelId, ConflictReworkPhaseKey, iteration: null);
            var supervision = await StartAgentSupervisionSessionAsync(
                item.Id,
                project,
                ConflictReworkPhaseKey,
                Math.Max(1, item.ConflictReworkAttempts),
                runner,
                item.AgentInstanceId,
                item.ModelId,
                item.ReasoningMode,
                sandbox,
                SandboxConventions.WorkDir,
                source: "pipeline",
                ct);
            Action<string>? stdoutCallback = null;
            var captureStructuredStream = NeedsStructuredStreamForSessionResume(runner);
            try
            {
                agentResult = supervision is null
                    ? await runner.RunAsync(
                        sandbox, SandboxConventions.WorkDir, prompt, credential,
                        item.ModelId, item.ReasoningMode, phase.Token,
                        stdoutChunkCallback: stdoutCallback,
                        captureStructuredStream: captureStructuredStream)
                    : await AgentSupervisionTurnRunner.RunAutonomousAndQueuedInjectionsAsync(
                        runner,
                        sandbox,
                        SandboxConventions.WorkDir,
                        prompt,
                        credential,
                        item.ModelId,
                        item.ReasoningMode,
                        supervision,
                        stdoutCallback,
                        captureStructuredStream,
                        promptPreprocessor: (raw, pct) => ProcessAgentPromptAsync(
                            item.Id, runner.Kind, AgentPromptPhase.Rework,
                            item.ConflictReworkAttempts, project, sandbox, raw, pct),
                        phase.Token);
            }
            catch (OperationCanceledException oce) when (oce is not PhaseCancellationException)
            {
                await FinalizeInvolvementAsync(conflictInvolvementId, AgentInvolvementOutcomes.FailureCancelled);
                throw phase.Wrap(oce);
            }
            catch (AgentSessionResumeExhaustedException ex)
            {
                var classification = _authFailureClassifier.ClassifyFailure(runner, ex.LastResult);
                var authDetection = _authFailureClassifier.DetectDetailed(
                    runner.Kind,
                    ex.LastResult.Stderr,
                    ex.LastResult.Stdout);
                if (authDetection is { Classification.Kind: AgentFailureKind.AuthRequired })
                {
                    await FinalizeInvolvementAsync(conflictInvolvementId, AgentInvolvementOutcomes.FailureAuth);
                    // Match the work-phase session-resume catch (see RunWorkAgentAsync):
                    // a single model-controlled stdout match must not globally bench
                    // the agent without forced in-VM probe corroboration.
                    await HandleAuthRequiredDetectionAsync(
                        item,
                        project,
                        runner.Kind,
                        ConflictReworkPhaseKey,
                        authDetection.Classification,
                        throwOnMatch: true,
                        stdoutOnlyEvidence: authDetection.IsStdoutOnly,
                        requireStdoutOnlyCorroboration: true,
                        matchedConfiguredPattern: authDetection.MatchedConfiguredStderrPattern
                            || authDetection.MatchedConfiguredStdoutPattern,
                        ct: ct);
                }

                if (classification.Kind == AgentFailureKind.AuthError)
                {
                    await FinalizeInvolvementAsync(conflictInvolvementId, AgentInvolvementOutcomes.FailureAuth);
                    await ThrowAuthErrorAgentFailureAsync(
                        item,
                        project,
                        runner.Kind,
                        ConflictReworkPhaseKey,
                        classification,
                        ct);
                }

                await FinalizeInvolvementAsync(
                    conflictInvolvementId,
                    classification.Kind switch
                    {
                        AgentFailureKind.TransientNetwork => AgentInvolvementOutcomes.FailureTransient,
                        AgentFailureKind.Infrastructure => AgentInvolvementOutcomes.FailureInfrastructure,
                        _ => AgentInvolvementOutcomes.FailureAgent,
                    });
                ThrowIfTransientAgentFailure(runner, ex, ConflictReworkPhaseKey);
                if (classification.Kind == AgentFailureKind.Infrastructure)
                {
                    return new ConflictReworkAgentOutcome(
                        AgentSucceeded: false,
                        NewTip: null,
                        FailureReason: BuildAgentFailureDetail(
                            $"Conflict-rework agent {runner.Kind} reported infrastructure failure after exhausting session resume",
                            ex.LastResult,
                            _opts.MaxFailureDetailBytes),
                        SemanticIncompatibleReason: null,
                        FilesChanged: null, Insertions: null, Deletions: null,
                        FailureKind: WorkItemFailureKinds.Infrastructure,
                        Agent: runner.Kind);
                }
                throw;
            }
            catch (Exception ex)
            {
                // A phase timeout (PhaseCancellationException) or any other
                // unexpected failure from the agent run must still close the
                // involvement row — otherwise it dangles in-progress forever,
                // unlike InvokeAgentWithQuotaFallbackAsync / ExecAuditorAsync
                // which both finalize on generic Exception. OutcomeForFailure
                // maps cancellation/timeout to failure:cancelled and everything
                // else to failure:agent.
                await FinalizeInvolvementAsync(conflictInvolvementId, OutcomeForFailure(ex));
                throw;
            }
            finally
            {
                if (supervision is not null)
                    await supervision.DisposeAsync();
            }
            stopwatch.Stop();
            var endedAt = DateTimeOffset.UtcNow;
            await TryRecordCostAsync(agentResult.Stdout, agentResult.Stderr,
                runner.Kind, item.AgentInstanceId, item.Id, ConflictReworkPhaseKey, iteration: null, startedAt, endedAt,
                ResolveObservedModelId(runner, item.ModelId));

            var combined = (agentResult.Stdout ?? string.Empty) + "\n" + (agentResult.Stderr ?? string.Empty);
            var semanticIncompatible = ExtractSemanticIncompatibleReason(combined);
            var agentFailureClassification = !agentResult.Success
                ? _authFailureClassifier.ClassifyFailure(runner, agentResult)
                : null;
            // A semantic-incompatible declaration is the disposition the pipeline
            // acts on (it parks the item with that reason) even though the agent
            // legitimately exits non-zero to signal it — so it must be checked
            // before the generic !Success → failure:agent fallback, otherwise the
            // involvement outcome would mislabel it as a plain agent failure.
            var conflictInvolvementOutcome = semanticIncompatible is not null
                ? AgentInvolvementOutcomes.FailureSemanticIncompatible
                : agentFailureClassification?.Kind switch
                {
                    AgentFailureKind.AuthRequired or AgentFailureKind.AuthError => AgentInvolvementOutcomes.FailureAuth,
                    AgentFailureKind.TransientNetwork => AgentInvolvementOutcomes.FailureTransient,
                    AgentFailureKind.Infrastructure => AgentInvolvementOutcomes.FailureInfrastructure,
                    null => AgentInvolvementOutcomes.Success,
                    _ => AgentInvolvementOutcomes.FailureAgent,
                };
            await FinalizeInvolvementAsync(conflictInvolvementId, conflictInvolvementOutcome);
            // An exit-0 conflict-rework run that printed a login prompt would
            // otherwise fall through to the rebase/status handling below and be
            // recorded as an ordinary dirty/conflict rework failure, leaving
            // the unauthenticated agent routable. Detection runs before the
            // semantic-incompatible branch so an auth break is always reported
            // as the breaking signal, not as the agent's own reasoned refusal.
            // Require forced in-VM probe corroboration before publishing the
            // global bench — the session-resume catch sibling at line ~12929
            // is already corroborated; pairing this steady-state scan
            // preserves the symmetry the b946c6f / b8d9d09 retrofit work
            // established.
            await ThrowIfAuthRequiredOutputAsync(
                item, project, runner.Kind, ConflictReworkPhaseKey, agentResult,
                requireStdoutOnlyCorroboration: true,
                ct: ct);
            if (semanticIncompatible is not null)
            {
                return new ConflictReworkAgentOutcome(
                    AgentSucceeded: false,
                    NewTip: null,
                    FailureReason: null,
                    SemanticIncompatibleReason: semanticIncompatible,
                    FilesChanged: null, Insertions: null, Deletions: null);
            }

            if (!agentResult.Success)
            {
                var classification = agentFailureClassification
                    ?? _authFailureClassifier.ClassifyFailure(runner, agentResult);
                await ThrowIfAuthErrorAgentFailureAsync(
                    item,
                    project,
                    runner,
                    agentResult,
                    ConflictReworkPhaseKey,
                    classification,
                    ct);
                ThrowIfTransientAgentFailure(runner, agentResult, ConflictReworkPhaseKey);
                if (classification.Kind == AgentFailureKind.Infrastructure)
                {
                    return new ConflictReworkAgentOutcome(
                        AgentSucceeded: false,
                        NewTip: null,
                        FailureReason: BuildAgentFailureDetail(
                            $"Conflict-rework agent {runner.Kind} reported infrastructure failure",
                            agentResult,
                            _opts.MaxFailureDetailBytes),
                        SemanticIncompatibleReason: null,
                        FilesChanged: null, Insertions: null, Deletions: null,
                        FailureKind: WorkItemFailureKinds.Infrastructure,
                        Agent: runner.Kind);
                }

                return new ConflictReworkAgentOutcome(
                    AgentSucceeded: false,
                    NewTip: null,
                    FailureReason: agentResult.Summary,
                    SemanticIncompatibleReason: null,
                    FilesChanged: null, Insertions: null, Deletions: null);
            }

            // Some agents may have already advanced HEAD via `git rebase
            // --continue`; others leave the rebase in progress. If a rebase is
            // still in flight after a "successful" exit, try to continue once;
            // if that fails, treat as agent failure.
            var rebaseInProgress = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "test -d \"$0/.git/rebase-merge\" -o -d \"$0/.git/rebase-apply\"", SandboxConventions.WorkDir],
            }, ct);
            if (rebaseInProgress.ExitCode == 0)
            {
                var continueResult = await sandbox.ExecAsync(new SandboxExec
                {
                    Argv = ["git", "-C", SandboxConventions.WorkDir, "rebase", "--continue"],
                    ExtraEnvironment = new Dictionary<string, string>
                    {
                        ["GIT_EDITOR"] = "true",
                        ["GIT_SEQUENCE_EDITOR"] = "true",
                    },
                }, ct);
                if (!continueResult.Success)
                {
                    return new ConflictReworkAgentOutcome(
                        AgentSucceeded: false,
                        NewTip: null,
                        FailureReason: $"agent left rebase in progress and 'rebase --continue' failed: {continueResult.Stderr.Trim()}",
                        SemanticIncompatibleReason: null,
                        FilesChanged: null, Insertions: null, Deletions: null);
                }
            }

            return await PushAndStatConflictReworkAsync(sandbox, isolatedRepoPath, repoId, workBranch, priorWorkTip, baseBranch, ct);
        }
        finally
        {
            await _gitHost.DisposeIsolatedMergeCloneAsync(repoId, isolatedRepoPath, CancellationToken.None);
        }
    }

    private async Task<ConflictReworkAgentOutcome> PushAndStatConflictReworkAsync(
        ISandbox sandbox,
        string isolatedRepoPath,
        string repoId,
        string workBranch,
        string priorWorkTip,
        string baseBranch,
        CancellationToken ct)
    {
        var headSha = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", SandboxConventions.WorkDir, "rev-parse", "HEAD"],
        }, ct);
        if (!headSha.Success || string.IsNullOrWhiteSpace(headSha.Stdout))
        {
            return new ConflictReworkAgentOutcome(
                AgentSucceeded: false,
                NewTip: null,
                FailureReason: $"could not resolve HEAD after rework: {headSha.Stderr.Trim()}",
                SemanticIncompatibleReason: null,
                FilesChanged: null, Insertions: null, Deletions: null);
        }
        var newTip = headSha.Stdout.Trim();

        // Verify the work tree is clean (no unresolved conflicts, no straggling edits).
        var status = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", SandboxConventions.WorkDir, "status", "--porcelain"],
        }, ct);
        if (!status.Success)
        {
            return new ConflictReworkAgentOutcome(
                AgentSucceeded: false,
                NewTip: newTip,
                FailureReason: $"could not read post-rework status: {status.Stderr.Trim()}",
                SemanticIncompatibleReason: null,
                FilesChanged: null, Insertions: null, Deletions: null);
        }
        if (!string.IsNullOrWhiteSpace(status.Stdout))
        {
            return new ConflictReworkAgentOutcome(
                AgentSucceeded: false,
                NewTip: newTip,
                FailureReason: $"rework left dirty worktree:\n{status.Stdout.Trim()}",
                SemanticIncompatibleReason: null,
                FilesChanged: null, Insertions: null, Deletions: null);
        }

        // Push the rebased tip back into the isolated bare repo so the caller's
        // host-side SetBranchToCommitAsync can read it from there. The push is
        // safe inside the sandbox because the only mount is the isolated clone.
        var pushRef = $"refs/codeybox/conflict-rework/{Guid.NewGuid():N}";
        var push = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", SandboxConventions.WorkDir, "push", "origin", $"HEAD:{pushRef}"],
        }, ct);
        if (!push.Success)
        {
            return new ConflictReworkAgentOutcome(
                AgentSucceeded: false,
                NewTip: newTip,
                FailureReason: $"failed to publish rework tip to isolated repo: {push.Stderr.Trim()}",
                SemanticIncompatibleReason: null,
                FilesChanged: null, Insertions: null, Deletions: null);
        }
        await sandbox.SyncStateToHostAsync(ct);

        // Verify the push landed in the isolated repo and pull the resulting
        // commit back into the durable host bare repo so SetBranchToCommit can
        // find it.
        await ImportIsolatedMergeCommitAsync(repoId, isolatedRepoPath, pushRef, ct);
        try
        {
            // Resolve via the durable host repo to confirm the sha matches what
            // we expect, then drop the temporary ref.
            var resolved = await _gitHost.ResolveCommitAsync(repoId, pushRef, ct);
            if (!string.Equals(resolved, newTip, StringComparison.Ordinal))
            {
                return new ConflictReworkAgentOutcome(
                    AgentSucceeded: false,
                    NewTip: newTip,
                    FailureReason: $"imported rework tip {resolved} disagrees with sandbox HEAD {newTip}",
                    SemanticIncompatibleReason: null,
                    FilesChanged: null, Insertions: null, Deletions: null);
            }
        }
        finally
        {
            await DeleteHostRefBestEffortAsync(repoId, pushRef, CancellationToken.None);
        }

        // Best-effort diff stats from sandbox: prior tip vs new tip.
        IReadOnlyList<string>? changed = null;
        int? ins = null, dels = null;
        try
        {
            var nameOnly = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", SandboxConventions.WorkDir, "diff", "--name-only", priorWorkTip, "HEAD"],
            }, ct);
            if (nameOnly.Success)
            {
                changed = nameOnly.Stdout
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToArray();
            }
            var stat = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", SandboxConventions.WorkDir, "diff", "--numstat", priorWorkTip, "HEAD"],
            }, ct);
            if (stat.Success)
            {
                var totalIns = 0;
                var totalDel = 0;
                var any = false;
                foreach (var line in stat.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split('\t');
                    if (parts.Length < 2) continue;
                    if (int.TryParse(parts[0], out var i)) { totalIns += i; any = true; }
                    if (int.TryParse(parts[1], out var d)) { totalDel += d; any = true; }
                }
                if (any) { ins = totalIns; dels = totalDel; }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Conflict rework: best-effort diff stat collection failed");
        }

        _ = baseBranch; // baseBranch kept in signature for future symmetry with merge-phase stats.
        return new ConflictReworkAgentOutcome(
            AgentSucceeded: true,
            NewTip: newTip,
            FailureReason: null,
            SemanticIncompatibleReason: null,
            FilesChanged: changed,
            Insertions: ins,
            Deletions: dels);
    }

    /// <summary>
    /// Extracts the operator-facing reason that follows
    /// <see cref="SemanticIncompatibleMarker"/> in agent output. Returns null
    /// when the marker is absent or the captured tail is empty whitespace.
    /// </summary>
    private static string? ExtractSemanticIncompatibleReason(string output)
    {
        var idx = output.IndexOf(PromptComposer.SemanticIncompatibleMarker, StringComparison.Ordinal);
        if (idx < 0) return null;
        var tail = output[(idx + PromptComposer.SemanticIncompatibleMarker.Length)..];
        // Reason ends at the first newline so multi-line agent output doesn't
        // accidentally get folded into LastError.
        var nl = tail.IndexOfAny(['\r', '\n']);
        var reason = (nl < 0 ? tail : tail[..nl]).Trim();
        return reason.Length == 0 ? null : reason;
    }

    private static async Task<IReadOnlyList<string>> ListSandboxConflictFilesAsync(
        ISandbox sandbox,
        CancellationToken ct)
    {
        return await MergeConflictPathInspector.ListUnmergedPathsAsync(sandbox, SandboxConventions.WorkDir, ct);
    }

    // Cold-tier extraction forwarder: implementation lives on PromptComposer.
    internal static string BuildConflictReworkPrompt(
        string originalPrompt,
        string baseBranch,
        string workBranch,
        IReadOnlyList<string> conflictFiles,
        string mergePhaseFailureMessage) =>
        new PromptComposer().BuildConflictReworkPrompt(originalPrompt, baseBranch, workBranch, conflictFiles, mergePhaseFailureMessage);

    /// <summary>
    /// Persists the <c>ConflictReworkAttempts++</c> bump on the store. Returns
    /// the updated work-item snapshot, or null if the store row vanished
    /// between calls (e.g. a concurrent delete — the caller should treat this
    /// as a stale-write race and stop).
    /// </summary>
    private async Task<WorkItem?> BumpConflictReworkAttemptsAsync(WorkItem item, CancellationToken ct)
    {
        var current = await _store.GetAsync(item.Id, ct);
        if (current is null) return null;
        var bumped = current with { ConflictReworkAttempts = current.ConflictReworkAttempts + 1 };
        await _store.UpdateAsync(bumped, ct);
        return bumped;
    }

    /// <summary>
    /// Returns the file paths changed between <paramref name="fromTip"/> and
    /// <paramref name="toTip"/> in the host bare repo, in lexical order.
    /// Anti-abandonment uses this to detect a rework that discarded the
    /// work agent's prior diff — rebase changes commit SHAs but preserves
    /// changed-file sets, so a file-set comparison is the right signal.
    /// </summary>
    private async Task<IReadOnlyList<string>> ListChangedFilesAsync(
        string repoId, string fromTip, string toTip, CancellationToken ct)
    {
        var (stdout, _) = await RunHostGitCaptureAsync(_gitHost.GetRepoPath(repoId), ct,
            "diff", "--name-only", fromTip, toTip);
        return stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    private async Task PublishConflictReworkFinishedAsync(
        WorkItem item, Project project, string baseBranch, string workBranch,
        bool success, string? newTip, IReadOnlyList<string>? filesChanged,
        int? insertions, int? deletions,
        string? semanticIncompatible, string? parkReason, CancellationToken ct)
    {
        // Refresh the item snapshot so webhook subscribers see the live
        // ConflictReworkAttempts and any updated LastError.
        var current = await _store.GetAsync(item.Id, ct) ?? item;
        await TryPublishEventAsync(current, project, "work_item.conflict_rework_finished",
            new ConflictReworkFinishedDetails
            {
                WorkItemId = current.Id.ToString(),
                BaseBranch = baseBranch,
                WorkBranch = workBranch,
                Success = success,
                NewWorkBranchTip = newTip,
                FilesChanged = filesChanged,
                Insertions = insertions,
                Deletions = deletions,
                SemanticIncompatibleReason = semanticIncompatible,
                ParkReason = parkReason,
            }, ct);
    }

    private static bool TryGetUpstreamReconcileConflict(Exception ex, out UpstreamPushReconcileConflictException conflict)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is UpstreamPushReconcileConflictException typed)
            {
                conflict = typed;
                return true;
            }
        }

        conflict = null!;
        return false;
    }

}
