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

// PipelineRunner.AgentFailure.cs — Agent-failure classification: transient/infrastructure/auth failure mapping and follow-up enqueue.
public sealed partial class PipelineRunner
{
    private void ThrowIfTransientAgentFailure(
        IAgentRunner runner,
        AgentResult result,
        string phase)
    {
        if (TryBuildTransientAgentFailure(runner, result, phase, "during") is { } transient)
            throw transient;
    }

    private void ThrowIfInfrastructureAgentFailure(
        IAgentRunner runner,
        AgentResult result,
        string phase,
        string messagePrefix,
        AgentFailureClassification? classification = null)
    {
        var resolved = classification ?? _authFailureClassifier.ClassifyFailure(runner, result);
        if (resolved.Kind != AgentFailureKind.Infrastructure)
            return;

        var detail = BuildAgentFailureDetail(messagePrefix, result, _opts.MaxFailureDetailBytes);
        throw new AgentInfrastructureFailureException(runner.Kind, phase, detail);
    }

    private async Task ThrowIfAuthErrorAgentFailureAsync(
        WorkItem item,
        Project project,
        IAgentRunner runner,
        AgentResult result,
        string phase,
        AgentFailureClassification? classification,
        CancellationToken ct)
    {
        var resolved = classification ?? _authFailureClassifier.ClassifyFailure(runner, result);
        if (resolved.Kind != AgentFailureKind.AuthError)
            return;

        await ThrowAuthErrorAgentFailureAsync(item, project, runner.Kind, phase, resolved, ct);
    }

    private async Task ThrowAuthErrorAgentFailureAsync(
        WorkItem item,
        Project project,
        AgentKind agent,
        string phase,
        AgentFailureClassification classification,
        CancellationToken ct)
    {
        // A 401/403-shaped substring in captured output is agent-relayed text,
        // not a harness refusal. When the same credential still reads healthy
        // on the quota probe the classification is contradicted: fail the item
        // without benching the agent kind fleet-wide.
        var contradicted = await IsAuthContradictedByHealthyQuotaProbeAsync(item, project, agent, item.ModelId, ct).ConfigureAwait(false);
        var reason = _authRequiredHandler.BuildReason(
            phase,
            classification,
            stdoutOnlyEvidence: false,
            stdoutOnlyNote: contradicted
                ? "credential reads healthy on quota probe; item-level failure only, no fleet-wide bench"
                : null);
        if (!contradicted)
            await _authRequiredHandler.PublishSideEffectsAsync(agent, reason, item, project, ct: ct);
        throw new AgentAuthRequiredException(
            agent,
            phase,
            reason,
            contradicted ? WorkItemAuthFailureScope.Item : WorkItemAuthFailureScope.Fleet);
    }

    private void ThrowIfTransientAgentFailure(
        IAgentRunner runner,
        AgentSessionResumeExhaustedException resumeEx,
        string phase)
    {
        if (TryBuildTransientAgentFailure(
                runner,
                resumeEx.LastResult,
                phase,
                "after exhausting session resume during") is { } transient)
        {
            throw transient;
        }
    }

    private TerminalTransientNetworkError? TryBuildTransientAgentFailure(
        IAgentRunner runner,
        AgentResult result,
        string? phase,
        string failureContext)
    {
        var classification = _authFailureClassifier.ClassifyFailure(runner, result);
        if (classification.Kind != AgentFailureKind.TransientNetwork)
            return null;

        var reason = string.IsNullOrWhiteSpace(classification.Reason)
            ? "transient transport/network failure"
            : RedactAndTruncateAgentDetail(classification.Reason);
        var summary = RedactAndTruncateAgentDetail(result.Summary);
        var phaseSuffix = string.IsNullOrWhiteSpace(phase) ? "" : $" {phase}";
        return new TerminalTransientNetworkError(
            runner.Kind,
            phase,
            classification,
            $"Agent {runner.Kind} reported transient transport failure {failureContext}{phaseSuffix}: {summary} ({reason})");
    }

    internal static string BuildAgentFailureDetail(
        string firstLine,
        AgentResult result,
        int maxDetailBytes = SanitizedAgentDetail.DefaultTailMaxBytes) =>
        string.Join("\n",
            new[]
            {
                $"{firstLine}: {RedactAndTruncateAgentDetail(result.Summary)}",
                !string.IsNullOrEmpty(result.Stderr) ? $"stderr:\n{RedactAndTruncateAgentDetailTail(result.Stderr, maxDetailBytes)}" : null,
                !string.IsNullOrEmpty(result.Stdout) ? $"stdout:\n{RedactAndTruncateAgentDetailTail(result.Stdout, maxDetailBytes)}" : null,
            }.Where(s => s is not null));

    private static string RedactAndTruncateAgentDetailTail(string s, int maxBytes)
        => SanitizedAgentDetail.FromRawTail(s, maxBytes).Value;

    /// <summary>
    /// Builds and persists the on-yes follow-up Normal work item triggered by
    /// a matching check verdict. The follow-up inherits the parent's
    /// <see cref="WorkItem.ProjectId"/> and base branch, uses the spec's
    /// title / prompt verbatim, and back-links to the check via
    /// <see cref="WorkItem.OriginCheckWorkItemId"/>. Optional spec fields
    /// (agent kind, agent class, dependsOn, priority, min-model-score, knobs)
    /// flow through verbatim — no defaulting here so the operator's intent is
    /// preserved end-to-end. Dependency resolution mirrors
    /// <c>POST /workitems</c>: UUIDs and bare/namespaced externalIds within
    /// the same project.
    /// </summary>
    private async Task EnqueueOnYesFollowupAsync(
        WorkItem checkItem, Project project, OnYesActionSpec onYes, CancellationToken ct)
    {
        var existing = await CheckAndActFollowupRecovery.FindExistingFollowupAsync(_store, checkItem.Id, ct);
        if (existing is not null)
        {
            await CheckAndActFollowupRecovery.EnqueueIfReadyAsync(_store, _taskQueue, existing, ct);
            _log.LogInformation(
                "Work item {Id} already has check-and-act follow-up {FollowupId}; not creating a duplicate",
                checkItem.Id, existing.Id);
            return;
        }

        var newId = WorkItemId.New();
        var dependsOn = await ResolveOnYesDependsOnAsync(checkItem.ProjectId, onYes.DependsOn ?? [], ct);
        AgentKind? agentOverride = string.IsNullOrWhiteSpace(onYes.Agent) ? null : new AgentKind(onYes.Agent.Trim());
        var classId = string.IsNullOrWhiteSpace(onYes.AgentClassId) ? null : onYes.AgentClassId.Trim();
        var priority = onYes.Priority is { } p ? Math.Clamp(p, -1000, 1000) : 0;
        var minScore = onYes.MinModelScore is { } s ? Math.Clamp(s, 0, 200) : 0;

        var followup = new WorkItem
        {
            Id = newId,
            ProjectId = checkItem.ProjectId,
            Title = onYes.Title,
            Prompt = onYes.Prompt,
            BaseBranch = checkItem.BaseBranch,
            Agent = agentOverride,
            AgentClassId = classId,
            PushUpstream = checkItem.PushUpstream,
            DependsOn = dependsOn,
            QueuePosition = DateTimeOffset.UtcNow.Ticks,
            Priority = priority,
            MinModelScore = minScore,
            OriginCheckWorkItemId = checkItem.Id,
            JobType = JobType.Normal,
            Knobs = onYes.Knobs,
            Initiator = checkItem.Initiator,
        };

        try
        {
            await _store.CreateAsync(followup, ct);
        }
        catch (WorkItemOriginCheckConflictException)
        {
            existing = await CheckAndActFollowupRecovery.FindExistingFollowupAsync(_store, checkItem.Id, ct);
            if (existing is null)
                throw;

            await CheckAndActFollowupRecovery.EnqueueIfReadyAsync(_store, _taskQueue, existing, ct);
            _log.LogInformation(
                "Work item {Id} lost a race creating check-and-act follow-up {FollowupId}; reusing the existing follow-up",
                checkItem.Id, existing.Id);
            return;
        }

        AuditLog.WorkItemCreated(followup.Id, followup.ProjectId, followup.Title, followup.Initiator);

        // Enqueue iff all (zero-or-more) dependencies are already satisfied.
        // Same posture as POST /workitems: unsatisfied deps mean we persist
        // Queued but defer enqueue until they reach Done.
        await CheckAndActFollowupRecovery.EnqueueIfReadyAsync(_store, _taskQueue, followup, ct);

        await _webhooks.PublishAsync(new WebhookEvent
        {
            Event = "work_item.check_followup_enqueued",
            WorkItem = followup,
            Project = project,
            Details = new
            {
                originCheckWorkItemId = checkItem.Id.ToString(),
                followupWorkItemId = followup.Id.ToString(),
            },
        }, CancellationToken.None);
    }

    /// <summary>
    /// Resolves the dependency strings supplied on an <see cref="OnYesActionSpec"/>
    /// to <see cref="WorkItemId"/>s. Mirrors the create-time resolver in
    /// <c>WorkItemEndpoints.CreateAsync</c> at the orchestrator layer: GUIDs
    /// pass through, namespaced <c>"ns:value"</c> externalIds use the indexed
    /// lookup, bare externalIds are unambiguous-or-skipped within the same
    /// project. Unknown entries are silently dropped here rather than failing
    /// the check item — the check has already run successfully and recording
    /// the verdict is the priority. The follow-up's dependency gate will then
    /// see an empty dependsOn (vs a stale GUID that would never satisfy).
    /// </summary>
    private async Task<IReadOnlyList<WorkItemId>> ResolveOnYesDependsOnAsync(
        ProjectId projectId, IReadOnlyList<string> rawDeps, CancellationToken ct)
    {
        if (rawDeps.Count == 0) return [];

        var ids = new List<WorkItemId>(rawDeps.Count);
        List<WorkItem>? cachedProjectItems = null;
        foreach (var rawId in rawDeps)
        {
            if (string.IsNullOrWhiteSpace(rawId)) continue;
            if (Guid.TryParse(rawId, out var g))
            {
                ids.Add(new WorkItemId(g));
                continue;
            }

            if (cachedProjectItems is null)
            {
                cachedProjectItems = new List<WorkItem>();
                await foreach (var existing in _store.ListAsync(ct))
                    if (existing.ProjectId == projectId) cachedProjectItems.Add(existing);
            }

            if (Validation.TryParseNamespacedExternalId(rawId, out var ns, out var value) && ns is not null)
            {
                var hit = cachedProjectItems.FirstOrDefault(i =>
                    i.ExternalIds.TryGetValue(ns, out var v) && string.Equals(v, value, StringComparison.Ordinal));
                if (hit is not null) ids.Add(hit.Id);
                continue;
            }

            var matches = cachedProjectItems
                .Where(i => i.ExternalIds.Values.Any(v => string.Equals(v, rawId, StringComparison.Ordinal)))
                .Select(i => i.Id)
                .Distinct()
                .ToList();
            if (matches.Count == 1) ids.Add(matches[0]);
            // Ambiguous bare externalId (>1 match) and unknown (0 matches) both
            // silently drop — see method docstring for rationale.
        }
        return ids;
    }

    /// <summary>
    /// Post-act re-validation gate for items that were enqueued as the on-yes
    /// follow-up of a CheckAndAct (see <see cref="WorkItem.OriginCheckWorkItemId"/>).
    /// Re-runs the originating check's yes/no question against the modified repo
    /// after the act has been applied, using the same in-VM execution path as
    /// the original check (sandbox clone + <see cref="CheckAndActPipeline.BuildPrompt"/>
    /// + <see cref="CheckAndActPipeline.TryParseVerdict"/>). Each iteration's
    /// verdict is appended to <see cref="WorkItem.ReCheckVerdicts"/> for the
    /// timeline; non-actionable result accepts the remediation and returns,
    /// actionable result reworks the agent with the failing verdict as
    /// feedback and re-validates again. Bounded by
    /// <see cref="ProjectAudit.MaxIterations"/> — the same cap that bounds the
    /// audit/rework loop, reused per the CONFIG-OVER-HARDCODING posture so the
    /// re-check question/condition are read from the originating check item's
    /// stored <see cref="CheckAndActSpec"/> rather than baked into a new path.
    /// Throws when the cap is exhausted while the re-check still reports the
    /// actionable condition; the outer pipeline catch transitions the item to
    /// Failed with a "remediation did not satisfy the check after N attempts"
    /// reason so the operator can re-scope.
    /// </summary>
    private async Task RunPostActRevalidationLoopAsync(
        WorkItem item,
        Project project,
        IAgentRunner agentRunner,
        string repoId,
        string baseBranch,
        string workBranch,
        CancellationToken ct,
        CancellationToken hostShutdownToken)
    {
        if (item.OriginCheckWorkItemId is null) return;
        var originCheckId = item.OriginCheckWorkItemId.Value;

        // CONFIG-OVER-HARDCODING: the re-check question / actionable answer
        // come from the originating check item, not a new hardcoded copy.
        var originCheck = await _store.GetAsync(originCheckId, ct);
        if (originCheck is null || originCheck.Check is null)
        {
            _log.LogWarning(
                "Work item {Id} originating check {OriginId} missing or has no spec; skipping post-act re-validation",
                item.Id, originCheckId);
            return;
        }
        var checkSpec = originCheck.Check;
        var maxIterations = Math.Max(1, project.Audit.MaxIterations);

        for (var iteration = 1; iteration <= maxIterations; iteration++)
        {
            if (hostShutdownToken.IsCancellationRequested)
                throw new OperationCanceledException(hostShutdownToken);

            string? stdout = null;
            if (string.Equals(checkSpec.Mode, CheckAndActModes.Completion, StringComparison.OrdinalIgnoreCase))
            {
                stdout = await TryRunCheckAndActCompletionAsync(
                    item,
                    project,
                    checkSpec,
                    repoId,
                    baseBranch,
                    targetBranch: workBranch,
                    phase: "post-act-recheck",
                    iteration,
                    ct);
            }

            if (stdout is null)
            {
                var prompt = CheckAndActPipeline.BuildPrompt(checkSpec);
                var recheckSmokeTarget = SandboxTargetResolver.ToInVmSmokeTarget(
                    project,
                    new SandboxTarget(project.NetworkProfiles.Work, SandboxProfileFlavor.Headless),
                    item.BaselineImageRef);
                stdout = await InvokeAgentWithQuotaFallbackAsync(
                    item,
                    project,
                    "post-act-recheck",
                    iteration,
                    (runner, trialItem, attemptCt) => RunPostActReCheckAgentAsync(
                        trialItem, project, runner, repoId, workBranch, prompt, iteration, attemptCt),
                    ct,
                    initialRunnerOverride: agentRunner,
                    initialMemberOverride: _classRouter?.FindMember(
                        item.AgentClassId ?? project.DefaultAgentClass ?? string.Empty,
                        agentRunner.Kind,
                        item.ModelId),
                    smokeTarget: recheckSmokeTarget);
            }

            if (!CheckAndActPipeline.TryParseVerdict(stdout, out var verdict, out var parseError))
            {
                throw new InvalidOperationException(
                    $"post-act re-check verdict parse failure on iteration {iteration}/{maxIterations}: {parseError}");
            }

            // Persist the verdict to the act item's history. Re-read first so a
            // concurrent partial update (priority / prompt) is not clobbered.
            var current = await _store.GetAsync(item.Id, ct) ?? item;
            var newHistory = current.ReCheckVerdicts.Count == 0
                ? new List<CheckVerdict> { verdict! }
                : new List<CheckVerdict>(current.ReCheckVerdicts) { verdict! };
            var withHistory = current with { ReCheckVerdicts = newHistory };
            await _store.UpdateAsync(withHistory, ct);
            item = withHistory;

            _log.LogInformation(
                "Work item {Id} post-act re-check iteration {Iter}/{Max}: answer={Answer} confidence={Conf}",
                item.Id, iteration, maxIterations, verdict!.Answer,
                verdict.Confidence ?? "(unspecified)");

            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "work_item.post_act_recheck_completed",
                WorkItem = item,
                Project = project,
                Details = new
                {
                    iteration,
                    maxIterations,
                    answer = verdict.Answer,
                    actionableAnswer = checkSpec.ActionableAnswer,
                    actionable = verdict.Answer == checkSpec.ActionableAnswer,
                    originCheckWorkItemId = originCheckId.ToString(),
                },
            }, CancellationToken.None);

            // Non-actionable answer → remediation accepted; proceed to merge.
            if (verdict.Answer != checkSpec.ActionableAnswer)
                return;

            // Still actionable after the last allowed iteration → fail with a
            // clear reason so the operator can re-scope.
            if (iteration >= maxIterations)
            {
                throw new InvalidOperationException(
                    $"remediation did not satisfy the check after {maxIterations} attempt(s) " +
                    $"(originating check {originCheckId}); last evidence: {verdict.Evidence}");
            }

            // Re-engage the original work agent with the failing verdict as
            // feedback, then loop and re-validate again. Mirrors the
            // audit/rework loop's wiring (PhaseCancellation, quota fallback,
            // stuck probe, RunAgentPhaseAsync) so the post-act rework
            // participates in the same routing/observability machinery as
            // the audit-driven rework.
            var reworkPrompt = _promptComposer.BuildPostActReworkPrompt(item.Prompt, checkSpec, verdict, iteration, maxIterations);
            await Transition(item, WorkItemState.Reworking, ct, project);
            using var reworkPhase = new PhaseCancellation("post-act-rework", ct, _opts.TimeProvider);
            var (postActWorkTimeout, _) = ResolveEffectiveWorkTimeout(item, project);
            reworkPhase.SetPhaseTimeout(ResolvePhaseAbsoluteTimeout(postActWorkTimeout));
            reworkPhase.HookHostShutdown(hostShutdownToken, _opts.ShutdownGrace);
            var sandboxTarget = SandboxTargetResolver.ResolveProjectPhase(project, project.NetworkProfiles.Rework);
            try
            {
                await InvokeAgentWithQuotaFallbackAsync(item, project, "rework", iteration: null,
                    async (workerRunner, trialItem, attemptCt) =>
                        await RunWithStuckProbeAsync(trialItem, project, workerRunner.Kind, "rework", reworkPhase, ct,
                            phaseCt => RunAgentPhaseAsync(trialItem, workerRunner, repoId, baseBranch, workBranch,
                                reworkPrompt, isInitial: false,
                                networkProfile: sandboxTarget.NetworkProfile,
                                sandboxFlavor: sandboxTarget.Flavor,
                                project: project,
                                phaseCt,
                                hostShutdownToken,
                                // Post-act rework is followed by another check-verdict iteration,
                                // NOT a build-gated audit iteration. A non-compiling tree here will
                                // not be re-surfaced by any subsequent gate, so a build failure
                                // produced by this rework must terminal-fail the item rather than
                                // silently slip toward the merge / merged path.
                                buildFailurePolicy: RequiredBuildPolicy.Terminal,
                                iteration: null),
                            workToken: attemptCt),
                    ct,
                    phaseCancellation: reworkPhase,
                    attemptTimeout: postActWorkTimeout);
            }
            catch (OperationCanceledException oce) when (oce is not PhaseCancellationException)
            {
                throw reworkPhase.Wrap(oce);
            }

            // The rework agent committed; the next loop iteration will
            // re-check against the new work-branch tip. Refresh so the next
            // re-check sees any state mutations made by the rework path
            // (e.g. concurrent prompt edit captured by RunAgentPhaseAsync).
            await ResetRecoveryAttemptsAfterRealProgressEventAsync(
                item.Id,
                RecoveryProgressEvent.PostActReworkCompleted,
                "post-act-rework-completed",
                ct);
            item = await _store.GetAsync(item.Id, ct) ?? item;
        }
    }

    /// <summary>
    /// Runs a single post-act re-check agent invocation in a fresh sandbox,
    /// cloning the per-work-item repo and checking out the work branch (so the
    /// agent's committed remediation is visible). Mirrors
    /// <see cref="RunCheckAndActAgentAsync"/> — single invocation, no commit,
    /// no merge, no push — but evaluates the modified repo instead of the
    /// pristine base. Returns the agent-visible text (streamed chunks +
    /// terminal payload, projected through <see cref="AgentVisibleStdout"/>
    /// for envelope-framed runners) so the verdict parser sees the full tail.
    /// </summary>
    private async Task<string> RunPostActReCheckAgentAsync(
        WorkItem item, Project project, IAgentRunner agentRunner,
        string repoId, string workBranch, string prompt, int iteration, CancellationToken ct)
    {
        agentRunner = BindMemberRunner(agentRunner, TryResolveSelectedMember(agentRunner.Kind, project, item));
        var credential = await ResolveAgentCredentialAsync(agentRunner.Kind, project, item, ct);
        var access = _gitHost.GetSandboxAccess(repoId);

        var spec = BuildSandboxSpec(
            access,
            includeAgentCredential: credential,
            allowAgentNetwork: true,
            hostNetworkProfile: project.NetworkProfiles.Work,
            timingWorkItemId: item.Id,
            timingPhase: "post-act-recheck",
            flavor: SandboxProfileFlavor.Headless,
            extraEnvironment: null,
            baselineImageRef: SandboxTargetResolver.BaselineRefForTarget(
                project,
                new SandboxTarget(project.NetworkProfiles.Work, SandboxProfileFlavor.Headless),
                item.BaselineImageRef),
            credentialRunner: agentRunner);

        await using var sandbox = await _sandboxes.CreateAsync(spec, ct);
        if (credential is not null && credential.Files.Count > 0)
            await MaterialiseCredentialFilesAsync(sandbox, credential, ct);

        await Run(sandbox, "git", "clone", access.CloneUrlInsideSandbox, SandboxConventions.WorkDir);
        await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "checkout", "-B", workBranch, $"origin/{workBranch}");

        var aggregator = new System.Text.StringBuilder();
        Action<string>? chunkCallback = chunk =>
        {
            aggregator.Append(chunk);
            _stdoutBroadcaster?.BroadcastChunk(item.Id, "post-act-recheck", chunk);
        };

        AuditLog.AgentStarted(agentRunner.Kind, sandbox.Id, "post-act-recheck");
        prompt = await ProcessAgentPromptAsync(
            item.Id,
            agentRunner.Kind,
            AgentPromptPhase.CheckAndAct,
            1,
            project,
            sandbox,
            prompt,
            ct);
        await using var supervision = await StartAgentSupervisionSessionAsync(
            item.Id,
            project,
            "post-act-recheck",
            iteration,
            agentRunner,
            item.AgentInstanceId,
            item.ModelId,
            item.ReasoningMode,
            sandbox,
            SandboxConventions.WorkDir,
            source: "check-and-act",
            ct);
        var startedAt = DateTimeOffset.UtcNow;
        var result = supervision is null
            ? await agentRunner.RunAsync(
                sandbox, SandboxConventions.WorkDir, prompt, credential,
                item.ModelId, item.ReasoningMode, ct,
                stdoutChunkCallback: chunkCallback,
                captureStructuredStream: false)
            : await AgentSupervisionTurnRunner.RunAutonomousAndQueuedInjectionsAsync(
                agentRunner,
                sandbox,
                SandboxConventions.WorkDir,
                prompt,
                credential,
                item.ModelId,
                item.ReasoningMode,
                supervision,
                chunkCallback,
                captureStructuredStream: false,
                promptPreprocessor: (raw, pct) => ProcessAgentPromptAsync(
                    item.Id, agentRunner.Kind, AgentPromptPhase.CheckAndAct,
                    1, project, sandbox, raw, pct),
                ct);
        var endedAt = DateTimeOffset.UtcNow;

        var aggregatedStdout = aggregator.ToString();
        if (!string.IsNullOrEmpty(result.Stdout) && !aggregatedStdout.EndsWith(result.Stdout, StringComparison.Ordinal))
        {
            aggregator.Append(result.Stdout);
            aggregatedStdout = aggregator.ToString();
        }

        await TryRecordCostAsync(aggregatedStdout, result.Stderr,
            agentRunner.Kind, item.AgentInstanceId, item.Id, "post-act-recheck", iteration,
            startedAt, endedAt, ResolveObservedModelId(agentRunner, item.ModelId));

        // See RunCheckAndActAgentAsync above for the phase policy.
        await ThrowIfAuthRequiredOutputAsync(
            item, project, agentRunner.Kind, "post-act-recheck", aggregatedStdout, result.Stderr,
            requireStdoutOnlyCorroboration: true,
            ct: ct);

        if (!result.Success)
        {
            ThrowIfTransientAgentFailure(agentRunner, result, "post-act-recheck");
            var detail = BuildAgentFailureDetail("post-act re-check agent failed", result, _opts.MaxFailureDetailBytes);
            throw new InvalidOperationException(detail);
        }

        return AgentVisibleStdout(agentRunner, aggregatedStdout);
    }

    /// <summary>
    /// Builds the rework prompt for a post-act re-check that still reports the
    /// actionable condition. Surfaces the failing verdict's evidence as
    /// feedback so the agent can target the remaining issue, references the
    /// originating check's question verbatim, and frames the iteration count
    /// against the configured cap so the agent knows how many attempts remain.
    /// Kept distinct from <see cref="ReworkPromptBuilder"/> because the latter
    /// is shaped around auditor findings; here the "finding" is a single
    /// yes/no verdict with a free-form evidence string.
    /// </summary>
    private async Task ClearPreemptAsync(WorkItem item, CancellationToken ct)
    {
        var current = await _store.GetAsync(item.Id, ct) ?? item;
        if (current.PreemptedAt is null
            && string.IsNullOrWhiteSpace(current.PreemptCheckpoint)
            && current.AgentTurnResumeCheckpoint is null
            && current.AgentTurnRecoveryLease is null)
            return;

        var cleared = current with
        {
            PreemptedAt = null,
            PreemptCheckpoint = null,
            AgentTurnResumeCheckpoint = null,
            AgentTurnRecoveryLease = null,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        if (!await _store.TryUpdateIfStateAndUpdatedAtAsync(
                cleared,
                current.State,
                current.UpdatedAt,
                ct))
        {
            throw new InvalidOperationException(
                "Work item changed while its completed agent-turn recovery boundary was being cleared.");
        }
    }

    private async Task ScheduleConvertedAgentTurnCheckpointAsync(
        WorkItem item,
        Project project,
        string phase)
    {
        var current = await _store.GetAsync(item.Id, CancellationToken.None) ?? item;
        if (!current.HasAgentTurnRecoveryBoundary
            || string.IsNullOrWhiteSpace(current.PreemptCheckpoint)
            || current.AgentTurnRecoveryLease is not null)
        {
            await TransitionFailed(
                current,
                "Retained sandbox conversion completed without an authoritative immutable checkpoint.",
                CancellationToken.None,
                project,
                failureKind: "restore",
                expectedStates: [current.State],
                expectedUpdatedAt: current.UpdatedAt);
            return;
        }

        if (_taskQueue is null)
        {
            await TransitionFailed(
                current,
                "The retained sandbox was converted to an immutable checkpoint, but no task queue is available to continue it automatically.",
                CancellationToken.None,
                project,
                failureKind: WorkItemFailureKinds.Infrastructure,
                agent: current.Agent,
                expectedStates: [current.State],
                expectedUpdatedAt: current.UpdatedAt);
            return;
        }

        try
        {
            await _taskQueue.EnqueueAsync(item.Id, CancellationToken.None);
            _log.LogInformation(
                "Work item {Id} converted its retained sandbox to an immutable {Phase} checkpoint and was queued for resumed dispatch",
                item.Id,
                phase);
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "Failed queueing work item {Id} after retained-sandbox checkpoint conversion",
                item.Id);
            await TransitionFailed(
                current,
                "The retained sandbox was converted to an immutable checkpoint, but queuing its continuation failed.",
                CancellationToken.None,
                project,
                failureKind: WorkItemFailureKinds.Infrastructure,
                agent: current.Agent,
                expectedStates: [current.State],
                expectedUpdatedAt: current.UpdatedAt);
        }
    }

    private async Task<bool> TryCheckpointRecoverableAgentTurnAsync(
        WorkItem item,
        IAgentRunner runner,
        ISandbox sandbox,
        string branch,
        AgentResult result,
        bool isInitial,
        int? iteration,
        int promptRevisionAtDispatch,
        bool failureAlreadyClassified = false)
    {
        if (SessionResumeOptions.MaxResumeAttempts <= 0)
        {
            return false;
        }

        var classification = _authFailureClassifier.ClassifyFailure(runner, result);
        var isRecoverableFailure = failureAlreadyClassified
            || _quotaClassifier.Detect(
                runner.Kind,
                result.Stderr,
                result.Stdout) is not null
            || classification.Kind == AgentFailureKind.TransientNetwork
            || classification.Kind == AgentFailureKind.Infrastructure && result.ExecutionUnavailable
            || AgentSuspendResilience.IsInfrastructureProcessExitCode(
                AgentSuspendResilience.ParseAgentExitCode(result.Summary));
        if (!isRecoverableFailure)
            return false;

        try
        {
            using var checkpointCts = new CancellationTokenSource(_opts.PreemptCheckpointDrain);
            await CaptureAgentScratchpadAsync(
                runner,
                sandbox,
                SandboxConventions.WorkDir,
                checkpointCts.Token);

            var current = await _store.GetAsync(item.Id, checkpointCts.Token) ?? item;
            var checkpoint = CreateAgentTurnResumeCheckpoint(
                item,
                current,
                runner,
                result.NativeSessionId,
                isInitial,
                iteration,
                promptRevisionAtDispatch);

            await CheckpointPreemptAsync(
                item,
                sandbox,
                branch,
                runner.Kind,
                ResolveObservedModelId(runner, item.ModelId),
                checkpointCts.Token,
                checkpoint);
            return true;
        }
        catch (SandboxExecutionUnavailableException ex)
        {
            _log.LogWarning(
                ex,
                "Sandbox execution remained unavailable while checkpointing work item {Id}; attempting bounded provider-owned retention",
                item.Id);
            return await TryRetainAgentTurnSandboxAsync(
                item,
                runner,
                sandbox,
                branch,
                result.NativeSessionId,
                isInitial,
                iteration,
                promptRevisionAtDispatch);
        }
        catch (OperationCanceledException ex)
        {
            _log.LogWarning(
                ex,
                "Timed out creating the immutable agent-turn checkpoint for work item {Id} after {Timeout}; attempting bounded provider-owned retention",
                item.Id,
                _opts.PreemptCheckpointDrain);
            return await TryRetainAgentTurnSandboxAsync(
                item,
                runner,
                sandbox,
                branch,
                result.NativeSessionId,
                isInitial,
                iteration,
                promptRevisionAtDispatch);
        }
        catch (Exception ex)
        {
            // Checkpointing is recovery evidence, not the original operation.
            // Preserve and classify the real agent failure; a partial/failed
            // checkpoint is never marked valid by CheckpointPreemptAsync. A
            // checkpoint that genuinely cannot be written is additionally
            // reported as a degraded resumption guarantee (audit log + meter)
            // so the loss is operator-visible; the publish path reports first
            // and this handler skips the duplicate.
            if (AgentTurnCheckpointLimits.ShouldReportDegraded(ex))
                AgentTurnCheckpointLimits.ReportDegraded(item.Id, "recoverable-turn", ex, _log);
            _log.LogError(
                ex,
                "Failed creating durable agent-turn checkpoint for work item {Id}; the original agent failure will be preserved and the missing checkpoint is reported as degraded",
                item.Id);
            return false;
        }
    }

    private async Task<bool> TryRetainAgentTurnSandboxAsync(
        WorkItem item,
        IAgentRunner runner,
        ISandbox sandbox,
        string branch,
        AgentNativeSessionId? nativeSessionId,
        bool isInitial,
        int? iteration,
        int promptRevisionAtDispatch)
    {
        var preemptible = SandboxCapability.Find<IPreemptibleSandbox>(sandbox);
        var preserveControl = SandboxCapability.Find<IPreserveOnDisposeSandbox>(sandbox);
        var providerOwner = SandboxCapability.Find<IProviderOwnedSandbox>(sandbox);
        if (preemptible is null || preserveControl is null || providerOwner is null)
        {
            _log.LogWarning(
                "Sandbox {SandboxId} cannot publish a durable infrastructure-recovery lease for work item {Id}",
                sandbox.Id,
                item.Id);
            return false;
        }

        var published = false;
        try
        {
            using var retentionCts = new CancellationTokenSource(_opts.SandboxPreserveDrain);
            var recoveryLease = await preemptible
                .RetainForInfrastructureRecoveryAsync(retentionCts.Token)
                .ConfigureAwait(false);
            if (recoveryLease is null)
                return false;
            if (!string.Equals(recoveryLease.ProviderId, providerOwner.ProviderId, StringComparison.Ordinal)
                || !string.Equals(recoveryLease.SandboxId, sandbox.Id, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Sandbox recovery lease does not match the exact provider-owned sandbox.");
            }

            var current = await _store.GetAsync(item.Id, CancellationToken.None) ?? item;
            if (!string.IsNullOrWhiteSpace(current.PreemptCheckpoint))
            {
                // A normal immutable checkpoint won a race; it is stronger
                // evidence than a mutable retained VM and needs no lease.
                return true;
            }
            if (current.AgentTurnRecoveryLease is { } existingLease
                && existingLease != recoveryLease)
            {
                throw new InvalidDataException(
                    "Work item is already bound to a different retained sandbox recovery lease.");
            }
            if (current.AgentTurnRecoveryLease == recoveryLease)
            {
                // This exact capability is already authoritative. Do not
                // republish it: an unrelated concurrent lifecycle edit could
                // make the CAS lose, and disarming preservation in that case
                // would delete the VM still referenced by the database.
                published = true;
                return true;
            }

            var checkpoint = CreateAgentTurnResumeCheckpoint(
                item,
                current,
                runner,
                nativeSessionId ?? current.AgentTurnResumeCheckpoint?.NativeSessionId,
                isInitial,
                iteration,
                promptRevisionAtDispatch);
            var retained = current with
            {
                State = checkpoint.ResumeState,
                Agent = checkpoint.Agent,
                AgentInstanceId = checkpoint.AgentInstanceRoute,
                ModelId = checkpoint.ModelId,
                ReasoningMode = checkpoint.ReasoningMode,
                WorkBranch = branch,
                PreemptedAt = DateTimeOffset.UtcNow,
                PreemptCheckpoint = null,
                AgentTurnResumeCheckpoint = checkpoint,
                AgentTurnRecoveryLease = recoveryLease,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            var recoveryStore = _store as IAgentTurnScratchpadStore
                ?? throw new InvalidOperationException(
                    "The work-item store cannot atomically publish retained agent-turn recovery leases.");
            published = await recoveryStore.TryPublishRecoveryLeaseAsync(
                retained,
                current.State,
                current.UpdatedAt,
                _pipelineTuning.Current.MaxRetainedAgentTurnSandboxes,
                CancellationToken.None);
            if (!published)
            {
                _log.LogWarning(
                    "Retained sandbox recovery lease for work item {Id} lost its lifecycle CAS or the configured global retention cap was reached",
                    item.Id);
                return false;
            }

            _log.LogWarning(
                "Retained provider-owned sandbox {SandboxId} for work item {Id} until infrastructure recovery can publish its immutable checkpoint",
                sandbox.Id,
                item.Id);
            return true;
        }
        catch (OperationCanceledException ex)
        {
            _log.LogWarning(
                ex,
                "Timed out retaining provider-owned sandbox {SandboxId} for work item {Id}",
                sandbox.Id,
                item.Id);
            return false;
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "Failed retaining provider-owned sandbox {SandboxId} for work item {Id}",
                sandbox.Id,
                item.Id);
            return false;
        }
        finally
        {
            if (!published)
                preserveControl.DisablePreserveOnDispose();
        }
    }

}
