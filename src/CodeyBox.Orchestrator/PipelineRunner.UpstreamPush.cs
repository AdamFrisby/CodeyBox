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

// PipelineRunner.UpstreamPush.cs — Upstream-push phase: push with reconcile, stale-base routing, and auto-merge race recovery.
public sealed partial class PipelineRunner
{
    /// <summary>
    /// Attempts to route a stale-base conflict discovered during the upstream
    /// push into the conflict-rework state machine. Reads the item fresh so the
    /// router's attempt-cap check and atomic re-dispatch operate on the current
    /// persisted state. Returns <see cref="StaleBaseReworkOutcome.NotEnabled"/>
    /// (caller does its historical park) when no router is wired.
    /// </summary>
    private async Task<StaleBaseReworkOutcome> TryRouteStaleBasePushConflictAsync(
        WorkItem item, CancellationToken ct)
    {
        if (_staleBaseReworkRouter is null)
            return StaleBaseReworkOutcome.NotEnabled;

        var current = await _store.GetAsync(item.Id, ct) ?? item;
        return await _staleBaseReworkRouter.TryRouteAsync(
            current,
            "upstream-push-stale-base",
            StaleBaseConflictReworkRouter.PushPathEligibleSourceStates,
            ct);
    }

    private async Task RunUpstreamPushPhaseAsync(
        WorkItem item,
        Project project,
        IUpstreamRemote upstream,
        string repoId,
        string baseBranch,
        string workBranch,
        string? mergeSha,
        string? agentStdout,
        Func<CancellationToken, Task<(string MergeSha, string? AgentStdout)>>? reRunMergePhase,
        CancellationToken ct,
        CancellationToken hostShutdownToken)
    {
        using var upstreamPhaseScope = BeginPhaseScope(item, "upstream");
        using var upstreamPhase = new PhaseCancellation("upstream", ct, _opts.TimeProvider);
        upstreamPhase.HookHostShutdown(hostShutdownToken, _opts.ShutdownGrace);
        ct = upstreamPhase.Token;

        try
        {
            await Transition(item, WorkItemState.UpstreamPushing, ct, project);

            // Best-effort: compute the diff for LLM-generated PR descriptions.
            // Failures here are non-fatal — the fields default to empty strings
            // and the generator falls back to the static template.
            var (diffStat, fullDiff) = (string.Empty, string.Empty);
            try
            {
                (diffStat, fullDiff) = await _gitHost.GetDiffAsync(repoId, baseBranch, workBranch, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.LogDebug("Could not compute diff for PR description: {Message}", ex.Message);
            }

            IReadOnlyList<string> addressedFindings = [];
            if (_auditReports is not null)
            {
                try
                {
                    var reports = await _auditReports.GetByWorkItemAsync(
                        item.Id.ToString(), AuditTarget.Code, ct);
                    var titles = new List<string>();
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var report in reports)
                        foreach (var finding in report.Findings)
                            if (seen.Add(finding.Title))
                                titles.Add(finding.Title);
                    addressedFindings = titles;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _log.LogDebug("Could not load audit findings for PR description: {Message}", ex.Message);
                }
            }

            // Best-effort: read the agent's own commit messages for LLM-generated
            // PR descriptions. Failures here are non-fatal — the field defaults
            // to an empty list and the generator falls back to the diff alone.
            IReadOnlyList<string> commitMessages = [];
            try
            {
                commitMessages = await _gitHost.GetCommitMessagesAsync(repoId, baseBranch, workBranch, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.LogDebug("Could not read commit messages for PR description: {Message}", ex.Message);
            }

            var request = new UpstreamCompletionRequest
            {
                RepositoryId = repoId,
                WorkItemId = item.Id,
                ProjectId = project.Id,
                WorkBranch = workBranch,
                BaseBranch = baseBranch,
                MergeSha = mergeSha,
                Title = item.Title,
                Description = BuildPrDescription(item.Id, agentStdout),
                DiffStat = diffStat,
                FullDiff = fullDiff,
                WorkItemPrompt = item.Prompt,
                AddressedFindings = addressedFindings,
                CommitMessages = commitMessages,
                AgentStdout = agentStdout,
                PromptRevision = item.PromptRevision,
                Initiator = item.Initiator,
                TokenEnvVar = project.Upstream.TokenEnvVar,
                AutoMerge = project.Upstream.AutoMerge,
                MergeMethod = project.Upstream.MergeMethod,
            };

            // Pre-merge CI gate. The forge's textual `mergeable` flag does not
            // catch the case where a clean merge against newly-moved `main`
            // still breaks the build or tests (e.g. a helper renamed on `main`
            // that the PR still calls under its old name). When a verifier is
            // registered AND the project has opted in via PreMergeVerifyArgv,
            // re-validate the post-local-merge tree before the auto-merge API
            // call. A failure here is signalled by throwing
            // MergeConflictResolutionFailedException — the centralized catch
            // handler (see the catch at the top of RunAsync) parks the work
            // item at MergeConflictResolutionFailed with the same bookkeeping
            // every other merge-conflict-resolution failure goes through, so
            // there is exactly one park-and-publish path to maintain.
            if (project.Upstream.AutoMerge &&
                _preMergeVerifier is not null &&
                project.Upstream.PreMergeVerifyArgv.Count > 0 &&
                !string.IsNullOrEmpty(mergeSha))
            {
                PreMergeVerifyResult verifyResult;
                try
                {
                    verifyResult = await _preMergeVerifier.VerifyAsync(new PreMergeVerifyRequest
                    {
                        WorkItemId = item.Id,
                        ProjectId = project.Id,
                        RepositoryId = repoId,
                        BaseBranch = baseBranch,
                        WorkBranch = workBranch,
                        MergeSha = mergeSha!,
                        Argv = project.Upstream.PreMergeVerifyArgv,
                    }, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Verifier blew up. Park rather than silently proceed —
                    // the gate exists precisely to refuse-merge-on-doubt.
                    // ex.Message is routed through RawOutputRedactor here for
                    // the same reason SummariseOutput does it in-band: this
                    // string flows into LastError and the
                    // work_item.merge_conflict_resolution_failed webhook
                    // payload, both operator-visible surfaces. A native
                    // subprocess / IGitHost / I/O exception can in principle
                    // quote command lines or env values that contain tokens,
                    // so we redact defensively before forwarding.
                    _log.LogWarning(ex, "Pre-merge verifier threw; parking work item rather than auto-merging");
                    verifyResult = PreMergeVerifyResult.BuildOrTestFailed(
                        $"verifier threw: {RawOutputRedactor.Redact(ex.Message ?? string.Empty)}");
                }

                if (!verifyResult.Success)
                {
                    var prefix = verifyResult.FailureMode switch
                    {
                        PreMergeVerifyFailureMode.RebaseFailed => "pre-merge verify: rebase failed",
                        PreMergeVerifyFailureMode.BuildOrTestFailed => "pre-merge verify: rebased build failed",
                        _ => "pre-merge verify: failed",
                    };
                    var parkReason = $"{prefix}: {verifyResult.FailureReason ?? "(no detail provided)"}";
                    _log.LogWarning(
                        "Work item {Id} blocked from auto-merge by pre-merge verify ({Mode}): {Reason}",
                        item.Id, verifyResult.FailureMode, verifyResult.FailureReason);
                    throw new MergeConflictResolutionFailedException(parkReason);
                }

                _log.LogInformation(
                    "Pre-merge verify passed for work item {Id} ({Argv})",
                    item.Id, string.Join(' ', project.Upstream.PreMergeVerifyArgv));
            }

            // Capture the outcome from a successful CompleteAsync so the local
            // bookkeeping (state transition + webhook events) runs once, outside
            // the retry loop. Transition must NOT be inside the try — if it throws
            // after a successful CompleteAsync, the loop would re-invoke the remote
            // API call, creating duplicate PRs or merge attempts.
            UpstreamCompletionOutcome? completed = null;
            // Set when the most recent CompleteAsync attempt returned with the
            // AutoMergeRaced flag and we never escaped before hitting the cap.
            // The post-loop block uses this to emit the "main is being hammered"
            // diagnostic distinct from the normal infrastructure-failure path.
            var lastIterationRaced = false;
            // Set when race recovery already transitioned the item to a
            // terminal state with its own park message (could not refetch base,
            // could not advance work branch, PR number missing, etc.).
            // Suppresses the post-loop "main is being hammered" message which
            // would otherwise clobber the more specific diagnostic.
            var raceRecoveryParked = false;
            // Tracks how many times we've successfully performed a full
            // auto-merge race recovery (refetch base + re-run merge phase +
            // update work branch). Bounded by the hot-reloadable
            // AutoMergeRaceRecoveryMaxAttempts in PipelineTuning to prevent
            // pathological re-merge loops when the upstream base is a moving
            // target (hammered by sibling writes / direct pushes). Distinct
            // from UpstreamPushMaxAttempts, which caps total upstream API
            // calls including transient infrastructure retries.
            var raceRecoveryCount = 0;
            // Each iteration may either retry transient failures OR recover from
            // an auto-merge race (405 on PUT /pulls/N/merge). The shared cap
            // prevents pathological loops since each race-recovery iteration
            // costs a full LLM merge phase invocation.
            for (var attempt = 1; attempt <= _opts.UpstreamPushMaxAttempts; attempt++)
            {
                // Reset per-iteration so a race in iteration N does not leak
                // into iteration N+1's post-loop attribution if the next
                // iteration fails with a non-race exception.
                lastIterationRaced = false;
                var current = await _store.GetAsync(item.Id, ct) ?? item;
                await _store.UpdateAsync(current with { UpstreamPushAttempts = attempt }, ct);

                try
                {
                    UpstreamCompletionOutcome outcome;
                    await using (var upstreamScope = await TimingScope.BeginAsync(
                        _timings, item.Id, "upstream_push", "upstream.complete",
                        metadata: new Dictionary<string, object> { ["attempt"] = attempt },
                        log: _log))
                    {
                        outcome = await upstream.CompleteAsync(request, ct);
                    }
                    if (outcome.PullRequestUrl is not null)
                        _log.LogInformation("Upstream PR: {Url}", outcome.PullRequestUrl);
                    if (outcome.MergedSha is not null)
                        _log.LogInformation("Upstream PR auto-merged: {Sha}", outcome.MergedSha);
                    if (outcome.Notes is not null)
                        _log.LogInformation("Upstream notes: {Notes}", outcome.Notes);

                    lastIterationRaced = outcome.AutoMergeRaced;
                    if (outcome.AutoMergeRaced && reRunMergePhase is not null)
                    {
                        // GitHub said the PR is unmergeable. Two plausible causes:
                        //   1) Upstream main moved (a race we can fix by re-running
                        //      the LLM merger against the fresh base).
                        //   2) Branch protection / unrelated unmergeability (re-running
                        //      won't help; base sha will be unchanged).
                        // Distinguish via the base sha before/after refetch.
                        var raceRecovery = await TryRecoverFromAutoMergeRaceAsync(
                            item, project, upstream, repoId, baseBranch, workBranch,
                            outcome.PullRequestNumber,
                            reRunMergePhase,
                            attempt,
                            ct);
                        if (raceRecovery.ParkReason is not null)
                        {
                            _log.LogWarning(
                                "Auto-merge race recovery declined to retry (attempt {Attempt}): {Reason}",
                                attempt, raceRecovery.ParkReason);
                            // Stale-base remediation: before parking, try to route
                            // the item into conflict-rework (rebase onto the
                            // refreshed base + agentic resolution). CapExhausted /
                            // NotEnabled fall through to the existing
                            // MergeConflictResolutionFailed park below.
                            if (await TryRouteStaleBasePushConflictAsync(item, ct) == StaleBaseReworkOutcome.Routed)
                            {
                                raceRecoveryParked = true;
                                break;
                            }
                            var failed = await _store.GetAsync(item.Id, ct) ?? item;
                            var failedWithReason = failed.With(WorkItemState.MergeConflictResolutionFailed, raceRecovery.ParkReason);
                            await _store.UpdateAsync(failedWithReason, ct);
                            var revision = await BuildTerminalRevisionAsync(failedWithReason, ct);
                            await _webhooks.PublishAsync(new WebhookEvent
                            {
                                Event = "work_item.merge_conflict_resolution_failed",
                                WorkItem = failedWithReason,
                                Project = project,
                                PromptRevision = revision?.PromptRevision,
                                RevisionAtCompletion = revision?.RevisionAtCompletion,
                                RevisionMatches = revision?.RevisionMatches,
                            }, ct);
                            raceRecoveryParked = true;
                            break;
                        }

                        // Race recovery succeeded — update local state.
                        raceRecoveryCount++;
                        var maxRaceRecovery = _pipelineTuning.Current.AutoMergeRaceRecoveryMaxAttempts;
                        if (raceRecoveryCount >= maxRaceRecovery)
                        {
                            _log.LogWarning(
                                "Work item {Id} auto-merge race recovery cap ({Cap}) exhausted after {Count} recoveries; baseBranch likely being mutated by another writer",
                                item.Id, maxRaceRecovery, raceRecoveryCount);
                            // Hand off to the conflict-rework machine (bounded by
                            // its own attempt cap) before falling back to the park.
                            if (await TryRouteStaleBasePushConflictAsync(item, ct) == StaleBaseReworkOutcome.Routed)
                            {
                                raceRecoveryParked = true;
                                break;
                            }
                            var failed = await _store.GetAsync(item.Id, ct) ?? item;
                            const string raceExhaustionMessage =
                                "GitHub merge failed repeatedly after re-running LLM merger; baseBranch likely being mutated by another writer. Resolve manually.";
                            var failedWithReason = failed.With(WorkItemState.MergeConflictResolutionFailed, raceExhaustionMessage);
                            await _store.UpdateAsync(failedWithReason, ct);
                            var revision = await BuildTerminalRevisionAsync(failedWithReason, ct);
                            await _webhooks.PublishAsync(new WebhookEvent
                            {
                                Event = "work_item.merge_conflict_resolution_failed",
                                WorkItem = failedWithReason,
                                Project = project,
                                PromptRevision = revision?.PromptRevision,
                                RevisionAtCompletion = revision?.RevisionAtCompletion,
                                RevisionMatches = revision?.RevisionMatches,
                            }, ct);
                            raceRecoveryParked = true;
                            break;
                        }

                        mergeSha = raceRecovery.NewMergeSha;
                        agentStdout = raceRecovery.NewAgentStdout;
                        request = request with
                        {
                            MergeSha = mergeSha,
                            AgentStdout = agentStdout,
                            ExistingPullRequestNumber = outcome.PullRequestNumber,
                        };
                        continue;
                    }

                    completed = outcome;
                    break;
                }
                // MergeConflictResolutionFailedException intentionally passes
                // through this catch — it identifies an LLM-merger failure and
                // must reach RunAsync's MergeConflictResolutionFailedException
                // handler to be attributed correctly. If we caught it here we
                // would relabel it as "infrastructure" and (worse) on success
                // backoffs misclassify the next iteration's terminal state.
                // Sandbox provisioning deferrals must also bubble out to the
                // orchestrator requeue path; retrying them here would hard-fail
                // infrastructure flaps after the upstream attempt budget.
                catch (Exception ex) when (ex is not MergeConflictResolutionFailedException
                    && ex is not TerminalTransientNetworkError
                    && ex is not SandboxProvisioningDeferredException
                    && ex is not AgentPausedException
                    && ex is not AgentAuthRequiredException
                    && ex is not AgentInfrastructureFailureException)
                {
                    if (TryGetUpstreamReconcileConflict(ex, out var conflict))
                    {
                        _log.LogWarning("Upstream complete failed with unrecoverable reconcile conflict: {Error}", conflict.Message);
                        // A non-fast-forward push whose automatic single-shot
                        // rebase/merge itself conflicts is a stale-base conflict.
                        // Route it into conflict-rework (rebase onto refreshed
                        // base + agentic resolution) when enabled; on cap
                        // exhaustion park at the merge-conflict terminal (the
                        // centralized RunAsync catch does the bookkeeping);
                        // otherwise preserve the historical infrastructure park.
                        var reworkOutcome = await TryRouteStaleBasePushConflictAsync(item, ct);
                        if (reworkOutcome == StaleBaseReworkOutcome.Routed)
                            return;
                        if (reworkOutcome == StaleBaseReworkOutcome.CapExhausted)
                            throw new MergeConflictResolutionFailedException(conflict.Message, ex);
                        await TransitionFailed(item, conflict.Message, ct, project, failureKind: "infrastructure");
                        break;
                    }

                    _log.LogWarning("Upstream complete attempt {Attempt} failed: {Error}", attempt, ex.Message);
                    if (attempt < _opts.UpstreamPushMaxAttempts)
                        await Task.Delay(_opts.UpstreamPushBackoff, ct);
                    else
                        await TransitionFailed(item, $"upstream complete failed after {attempt} attempts: {ex.Message}", ct, project, failureKind: "infrastructure");
                }
            }

            // If the loop exited at the cap with the most recent outcome still
            // flagged as AutoMergeRaced (i.e., we never escaped the race), park
            // the item with the "main is being hammered" message.
            // Distinguish from MergeConflictResolutionFailed-from-LLM-failure
            // and from the generic infrastructure-failure path so an operator
            // inspecting lastError can tell "LLM gave up" from "main was a
            // moving target".
            //
            // raceRecoveryParked guards against clobbering a more specific
            // park message (e.g., refetch failure or merge-failure) that recovery
            // already wrote — we should not overwrite that with the generic cap
            // diagnostic.
            if (completed is null && lastIterationRaced && reRunMergePhase is not null && !raceRecoveryParked
                && await TryRouteStaleBasePushConflictAsync(item, ct) == StaleBaseReworkOutcome.Routed)
            {
                // Re-dispatched into conflict-rework; skip the terminal park.
                raceRecoveryParked = true;
            }

            if (completed is null && lastIterationRaced && reRunMergePhase is not null && !raceRecoveryParked)
            {
                var lastAttemptItem = await _store.GetAsync(item.Id, ct) ?? item;
                const string raceExhaustionMessage =
                    "GitHub merge failed repeatedly after re-running LLM merger; baseBranch likely being mutated by another writer. Resolve manually.";
                _log.LogWarning(
                    "Work item {Id} hit upstream-push attempt cap ({UpstreamCap}) with {RaceRecoveryCount} auto-merge recoveries (recovery cap {RaceRecoveryCap}) without resolving",
                    item.Id, _opts.UpstreamPushMaxAttempts, raceRecoveryCount, _pipelineTuning.Current.AutoMergeRaceRecoveryMaxAttempts);
                var failed = lastAttemptItem.With(WorkItemState.MergeConflictResolutionFailed, raceExhaustionMessage);
                await _store.UpdateAsync(failed, ct);
                var revision = await BuildTerminalRevisionAsync(failed, ct);
                await _webhooks.PublishAsync(new WebhookEvent
                {
                    Event = "work_item.merge_conflict_resolution_failed",
                    WorkItem = failed,
                    Project = project,
                    PromptRevision = revision?.PromptRevision,
                    RevisionAtCompletion = revision?.RevisionAtCompletion,
                    RevisionMatches = revision?.RevisionMatches,
                }, ct);
            }

            if (completed is not null)
            {
                // Persist the forge-authoritative merge identity BEFORE
                // publishing pull_request_opened / transitioning to Done so
                // the webhook + DTO surfaces read the new fields off the
                // already-saved row rather than the stale local-merge
                // snapshot. MergeSha gets the GitHub-side sha (the squash
                // commit GitHub mints — the one that resolves on the
                // commits API). LocalSquashSha keeps the bare-repo merge
                // sha for diagnostics. PR number / URL land here so
                // operator monitoring tools have a single canonical
                // reference instead of having to reassemble from logs.
                //
                // Auto-merge-disabled / graceful-soft-fail outcomes return
                // MergedSha=null; in that case MergeSha stays null (the
                // work item is Done but the GitHub merge has not yet
                // happened — a human will merge later) and the prior
                // local-only sha lives on LocalSquashSha.
                if (completed.MergedSha is not null ||
                    completed.PullRequestNumber is not null ||
                    completed.PullRequestUrl is not null)
                {
                    var preMergePersist = await _store.GetAsync(item.Id, ct) ?? item;
                    await _store.UpdateAsync(preMergePersist with
                    {
                        MergeSha = completed.MergedSha ?? preMergePersist.MergeSha,
                        MergedPrNumber = completed.PullRequestNumber ?? preMergePersist.MergedPrNumber,
                        MergedPrUrl = completed.PullRequestUrl ?? preMergePersist.MergedPrUrl,
                    }, ct);
                }

                if (completed.PullRequestUrl is not null && completed.PullRequestNumber is not null)
                {
                    var current = await _store.GetAsync(item.Id, ct) ?? item;
                    await _webhooks.PublishAsync(new WebhookEvent
                    {
                        Event = "work_item.pull_request_opened",
                        WorkItem = current,
                        Project = project,
                        Details = new PullRequestOpenedDetails
                        {
                            WorkBranch = workBranch,
                            BaseBranch = baseBranch,
                            PullRequestNumber = completed.PullRequestNumber.Value,
                            PullRequestUrl = completed.PullRequestUrl,
                            MergedSha = completed.MergedSha,
                        },
                    }, ct);
                }
                await Transition(item, WorkItemState.Done, ct, project);
            }
        }
        catch (OperationCanceledException oce) when (oce is not PhaseCancellationException)
        {
            // Attribute any OCE bubbling out of the upstream-push body. The
            // explicit per-attempt logic above swallows non-cancellation
            // failures itself; anything reaching here is a cancellation we want
            // routed to the new attributed catches in RunAsync.
            throw upstreamPhase.Wrap(oce);
        }
    }

    /// <summary>
    /// Carrier for the auto-merge race recovery outcome. When
    /// <see cref="ParkReason"/> is non-null the caller transitions the item to
    /// MergeConflictResolutionFailed with that message and stops retrying;
    /// example: fetch failure, no PR number returned, or the upstream does not
    /// advertise the base branch. When <see cref="ParkReason"/> is null, the
    /// caller updates its local merge-sha and stdout fields and loops to the
    /// next CompleteAsync attempt.
    /// </summary>
    private readonly record struct AutoMergeRaceRecovery(
        string? ParkReason,
        string NewMergeSha,
        string? NewAgentStdout);

    private async Task<AutoMergeRaceRecovery> TryRecoverFromAutoMergeRaceAsync(
        WorkItem item,
        Project project,
        IUpstreamRemote upstream,
        string repoId,
        string baseBranch,
        string workBranch,
        int? existingPrNumber,
        Func<CancellationToken, Task<(string MergeSha, string? AgentStdout)>> reRunMergePhase,
        int attempt,
        CancellationToken ct)
    {
        if (existingPrNumber is null)
        {
            // CompleteAsync set AutoMergeRaced=true but produced no PR number;
            // we have nothing to retry against. Treat as a hard failure.
            return new AutoMergeRaceRecovery(
                ParkReason: "auto-merge raced but no PR number returned; cannot retry merge",
                NewMergeSha: string.Empty,
                NewAgentStdout: null);
        }

        // Step 1: capture the upstream base sha at merge time for diagnostic
        // logging. We can't read the current local base ref because the local
        // merge phase already advanced it to mergeSha (the merge commit). The
        // merge commit's first parent IS the upstream base sha we just merged
        // against — we compare that against the post-fetch upstream tip for
        // informational purposes (logged in Step 3). The comparison no longer
        // gates retry; we always re-run the merge phase after refetching.
        string? preMergeBaseSha = null;
        var currentItem = await _store.GetAsync(item.Id, ct) ?? item;
        // Read LocalSquashSha (the local bare-repo merge sha) rather than
        // MergeSha — the latter holds the GitHub-side authoritative sha
        // (or is null on the first attempt, since the auto-merge hasn't
        // succeeded yet during race recovery) and would never resolve via
        // `git cat-file` against the local bare repo.
        var localMergeSha = currentItem.LocalSquashSha;
        if (!string.IsNullOrEmpty(localMergeSha))
        {
            try
            {
                preMergeBaseSha = await _gitHost.ResolveCommitAsync(repoId, $"{localMergeSha}^1", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug(ex,
                    "Auto-merge race recovery: could not read first parent of merge sha {Sha}; falling back to local base ref",
                    localMergeSha);
            }
        }
        // Fallback: a fast-forward merge (no merge commit) leaves localMergeSha
        // with a single-parent history — `^1` still resolves but to an
        // arbitrary ancestor of work. Use the current local base ref as a
        // best-effort sentinel; the next branch decides whether to proceed.
        if (preMergeBaseSha is null)
        {
            try
            {
                preMergeBaseSha = await _gitHost.ResolveCommitAsync(repoId, baseBranch, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Auto-merge race recovery: could not resolve local base sha before refetch; proceeding anyway");
                preMergeBaseSha = null;
            }
        }

        // Step 2: refetch base from upstream.
        string? postFetchBaseSha;
        try
        {
            postFetchBaseSha = await upstream.FetchBaseBranchAsync(repoId, baseBranch, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Auto-merge race recovery: refetch of upstream base failed");
            return new AutoMergeRaceRecovery(
                ParkReason: $"could not refetch upstream base '{baseBranch}': {ex.Message}",
                NewMergeSha: string.Empty,
                NewAgentStdout: null);
        }

        if (postFetchBaseSha is null)
        {
            return new AutoMergeRaceRecovery(
                ParkReason: $"upstream does not advertise base branch '{baseBranch}' for race recovery",
                NewMergeSha: string.Empty,
                NewAgentStdout: null);
        }

        // Step 3: the upstream said the PR is unmergeable. Whether or not
        // we can detect base motion from the local pre/post sha comparison,
        // always re-run the merge phase against the freshly-fetched base.
        // The "base didn't move" check (which used to park here) is now a
        // diagnostic only — premature escalation on a true race (where the
        // local bare-repo base ref was too stale to show the movement) is
        // the defect this change eliminates. The merge itself will reveal
        // real semantic conflicts, which the in-VM resolver and bounded
        // retry cap handle.
        if (preMergeBaseSha is not null)
        {
            if (string.Equals(preMergeBaseSha, postFetchBaseSha, StringComparison.Ordinal))
            {
                _log.LogInformation(
                    "Auto-merge race recovery (attempt {Attempt}): upstream base '{Branch}' sha ({Sha}) unchanged since merge; re-running merge phase anyway in case local base was stale",
                    attempt, baseBranch, preMergeBaseSha);
            }
            else
            {
                _log.LogInformation(
                    "Auto-merge race detected (attempt {Attempt}): upstream base '{Branch}' moved {Old} → {New}; re-running merge phase",
                    attempt, baseBranch, preMergeBaseSha, postFetchBaseSha);
            }
        }

        // Step 4: re-run the merge phase against the freshly-fetched base. The
        // merge phase reads local baseBranch via ResolveCommitAsync, which now
        // reflects the upstream tip after step 2. The LLM merger produces a new
        // merge commit M with parents (postFetchBaseSha, workBranch_tip).
        string newMergeSha;
        string? newAgentStdout;
        try
        {
            (newMergeSha, newAgentStdout) = await reRunMergePhase(ct);
        }
        catch (MergeConflictResolutionFailedException ex)
        {
            // The LLM merger itself failed against the new base — distinct
            // failure path from the race (existing semantics handle this).
            // Bubble up so the standard MergeConflictResolutionFailed catch in
            // RunAsync attributes it correctly.
            throw new MergeConflictResolutionFailedException(
                $"re-run of merge phase against refreshed base '{baseBranch}' (auto-merge race recovery) failed: {ex.Message}", ex);
        }

        // Step 5: advance local workBranch to the new merge commit. The push
        // step inside CompleteAsync will then publish this tip to upstream as
        // a fast-forward (M has the prior workBranch tip as a parent, so the
        // upstream workBranch fast-forwards without a force).
        try
        {
            await _gitHost.SetBranchToCommitAsync(repoId, workBranch, newMergeSha, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Auto-merge race recovery: could not advance local work branch to new merge sha");
            return new AutoMergeRaceRecovery(
                ParkReason: $"could not advance local work branch '{workBranch}' to new merge sha {newMergeSha}: {ex.Message}",
                NewMergeSha: string.Empty,
                NewAgentStdout: null);
        }

        await _store.UpdateAsync((await _store.GetAsync(item.Id, ct) ?? item) with { LocalSquashSha = newMergeSha }, ct);
        return new AutoMergeRaceRecovery(
            ParkReason: null,
            NewMergeSha: newMergeSha,
            NewAgentStdout: newAgentStdout);
    }

    // ── Conflict-rework iteration (third-line merge fallback) ────────────────

    /// <summary>
    /// Phase key for cost/timing rows captured during the focused
    /// conflict-rework iteration. Kept distinct from <c>work</c>/<c>rework</c>
    /// so operators can measure how much budget the third-line fallback is
    /// burning per failed merge.
    /// </summary>
    internal const string ConflictReworkPhaseKey = "conflict_rework";

    /// <summary>
    /// Outcome of <see cref="RunConflictReworkIterationAsync"/>. When
    /// <see cref="Success"/> is true the caller advances the work branch and
    /// re-runs the merge phase; otherwise <see cref="ParkReason"/> carries the
    /// message the outer catch will record on
    /// <see cref="WorkItemState.MergeConflictResolutionFailed"/>.
    /// </summary>
    private readonly record struct ConflictReworkResult(
        bool Success,
        string? ParkReason,
        string? FailureKind = null,
        AgentKind? Agent = null);

}
