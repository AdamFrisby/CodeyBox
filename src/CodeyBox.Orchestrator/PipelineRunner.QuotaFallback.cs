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

// PipelineRunner.QuotaFallback.cs — Quota-fallback dispatch: InvokeAgentWithQuotaFallbackAsync, involvement tracking, and cost/model attribution helpers.
public sealed partial class PipelineRunner
{
    /// <summary>
    /// Runs <paramref name="invoker"/> with the work item's chosen agent runner;
    /// if the invocation classifies as <see cref="AgentFailureKind.QuotaExhausted"/>
    /// (signalled here as <see cref="TerminalQuotaError"/> from the inner phase),
    /// exceeds the configured per-attempt timeout, or exhausts CLI-native session
    /// resume attempts, picks the next-best class member, swaps the runner +
    /// ModelId + ReasoningMode on a trial copy of the work item, and retries the
    /// same iteration. Quota failures also mark the member exhausted in the
    /// router's in-process cache.
    ///
    /// <para>
    /// When no class router is wired or the item has no agent class, the wrapper
    /// is a single-attempt pass-through — the original behaviour. When every
    /// class member is exhausted in this pickup, throws
    /// <see cref="AgentClassExhaustedException"/>; both the work-phase and the
    /// audit-phase consumers re-surface the exception so the top-level
    /// <see cref="RunAsync"/> catch parks the item in WaitingForQuotaReset.
    /// Audit-phase callers used to silently skip the auditor for the
    /// iteration, but that lets a Pass verdict emerge with an incomplete
    /// review set — a Pass now requires every configured auditor to have
    /// produced a verdict, so quota exhaustion of a whole spill-to-peer pool
    /// parks and re-runs the same iteration when quota returns.
    /// </para>
    /// <para>
    /// <paramref name="invoker"/> receives a trial <see cref="WorkItem"/> whose
    /// <see cref="WorkItem.Agent"/>, <see cref="WorkItem.ModelId"/>, and
    /// <see cref="WorkItem.ReasoningMode"/> reflect the candidate currently
    /// being attempted. Callers must propagate this trial item into the agent
    /// invocation rather than capturing the original.
    /// </para>
    /// </summary>
    private async Task<TResult> InvokeAgentWithQuotaFallbackAsync<TResult>(
        WorkItem item,
        Project project,
        string phase,
        int? iteration,
        Func<IAgentRunner, WorkItem, CancellationToken, Task<TResult>> invoker,
        CancellationToken ct,
        PhaseCancellation? phaseCancellation = null,
        TimeSpan? attemptTimeout = null,
        IAgentRunner? initialRunnerOverride = null,
        AgentMembership? initialMemberOverride = null,
        bool recordInvolvement = true,
        InVmSmokeSandboxTarget? smokeTarget = null,
        string? requireCapability = null,
        bool skipInVmSmoke = false,
        bool allowAuthRequiredFallback = false)
    {
        // R8-core: every agent invocation gets a deterministic in-VM log path,
        // persisted on the work item BEFORE the runner starts. If SIGTERM fires
        // mid-invocation the shutdown teardown handler reads AgentLogPath out
        // of the store and the startup resume handler re-tails the same file on
        // the resumed VM. Path is keyed by (workItemId, phase, iteration) so
        // a single work item can have its work / audit-rework / merge / conflict-
        // rework runs all tagged unambiguously.
        var agentLogPath = BuildAgentLogPath(item.Id, phase, iteration);
        await PersistAgentLogPathAsync(item.Id, agentLogPath, ct);
        using var logScope = AgentInvocationLogContext.BeginScope(agentLogPath);

        var agentClassTag = item.AgentClassId ?? project.DefaultAgentClass ?? "(none)";

        async Task<TResult> InvokeAttemptAsync(IAgentRunner runner, WorkItem trialItem)
        {
            // Append a per-phase involvement row for the agent about to run, so the
            // full who-did-what trail captures every agent that touched the item —
            // not just the one currently stamped on WorkItem.Agent. Finalized with
            // an outcome below; on a quota/timeout fallback the next attempt records
            // its own row and this one is closed as a failure.
            //
            // recordInvolvement is false only for the LLM quota-fallback wrapper
            // around auditors: ExecAuditorAsync records one row per auditor sandbox
            // run (the single chokepoint for tool and LLM auditors alike), so
            // recording here as well would double-count.
            var involvementId = recordInvolvement
                ? await RecordInvolvementStartAsync(
                    item.Id, runner.Kind, trialItem.AgentInstanceId, trialItem.ModelId, phase, iteration)
                : null;
            using var attempt = phaseCancellation is not null && attemptTimeout is { } perAttempt
                ? phaseCancellation.BeginAttemptTimeout(perAttempt)
                : null;
            var attemptCt = attempt?.Token ?? phaseCancellation?.Token ?? ct;
            var modelTag = trialItem.ModelId ?? "(default)";
            using var invSpan = CodeyBoxActivities.Pipeline.StartActivity("agent.invoke", ActivityKind.Internal);
            if (invSpan is not null)
            {
                invSpan.SetTag("codeybox.work_item_id", item.Id.ToString());
                invSpan.SetTag("codeybox.phase", phase);
                invSpan.SetTag("codeybox.agent", runner.Kind.Value);
                invSpan.SetTag("codeybox.agent_instance", trialItem.AgentInstanceId ?? runner.Kind.Value);
                invSpan.SetTag("codeybox.model", modelTag);
                invSpan.SetTag("codeybox.agent_class", agentClassTag);
                if (iteration is not null) invSpan.SetTag("codeybox.iteration", iteration.Value.ToString());
            }
            var outcome = "error";
            // Per-agent circuit-breaker outcome for this dispatch attempt: true on
            // success (resets the breaker), false on a genuine failure of ANY kind
            // (feeds the windowed counter), left null for host/operator
            // cancellations which are not the agent's fault and must not bench it.
            bool? breakerSuccess = null;
            try
            {
                var result = await invoker(runner, trialItem, attemptCt);
                await FinalizeInvolvementAsync(involvementId, AgentInvolvementOutcomes.Success);
                outcome = AgentInvolvementOutcomes.Success;
                breakerSuccess = ClassifyDispatchOutcome(error: null, genuineAttemptTimeout: false);
                return result;
            }
            catch (OperationCanceledException oce) when (
                attempt is { TimeoutElapsed: true }
                && phaseCancellation is not null
                && oce is not PhaseCancellationException)
            {
                await FinalizeInvolvementAsync(involvementId, AgentInvolvementOutcomes.FailureTimeout);
                outcome = "canceled";
                if (phaseCancellation.Token.IsCancellationRequested
                    || phaseCancellation.Source is not null)
                {
                    // Host/phase cancellation surfaced as a timeout — not the
                    // agent's fault; classify as skip (null) so it never benches it.
                    breakerSuccess = ClassifyDispatchOutcome(oce, genuineAttemptTimeout: false);
                    throw phaseCancellation.Wrap(oce);
                }

                // A real per-attempt timeout (not a host/phase cancellation) is a
                // genuine dispatch failure — feed the breaker.
                breakerSuccess = ClassifyDispatchOutcome(oce, genuineAttemptTimeout: true);
                throw new AgentAttemptTimeoutException(
                    phaseCancellation.Phase,
                    runner.Kind,
                    attemptTimeout!.Value,
                    oce);
            }
            catch (OperationCanceledException ex)
            {
                await FinalizeInvolvementAsync(involvementId, OutcomeForFailure(ex));
                outcome = "canceled";
                // Host/operator cancellation — classify as skip (null) so it neither
                // opens nor resets the breaker.
                breakerSuccess = ClassifyDispatchOutcome(ex, genuineAttemptTimeout: false);
                throw;
            }
            catch (AgentSessionResumeExhaustedException ex)
            {
                // Every path out of this catch throws a terminal failure for the
                // attempt (auth, quota, transient, infrastructure, or agent) — all
                // genuine dispatch failures the breaker counts.
                breakerSuccess = ClassifyDispatchOutcome(ex, genuineAttemptTimeout: false);
                if (await TryConvertResumeExhaustionToAuthRequiredAsync(runner, trialItem, ex, attemptCt)
                    .ConfigureAwait(false) is { } authEx)
                {
                    await FinalizeInvolvementAsync(involvementId, AgentInvolvementOutcomes.FailureAuth);
                    throw authEx;
                }

                if (await TryConvertResumeExhaustionToQuotaAsync(runner, trialItem, ex, attemptCt)
                    .ConfigureAwait(false) is { } quotaEx)
                {
                    await FinalizeInvolvementAsync(involvementId, AgentInvolvementOutcomes.FailureQuota);
                    throw quotaEx;
                }

                if (TryConvertResumeExhaustionToTransient(runner, ex) is { } transientEx)
                {
                    await FinalizeInvolvementAsync(involvementId, AgentInvolvementOutcomes.FailureTransient);
                    throw transientEx;
                }

                var infrastructureClassification = _authFailureClassifier.ClassifyFailure(
                    runner,
                    ex.LastResult);
                if (ex.LastResult.ExecutionUnavailable
                    && infrastructureClassification.Kind == AgentFailureKind.Infrastructure)
                {
                    await FinalizeInvolvementAsync(
                        involvementId,
                        AgentInvolvementOutcomes.FailureInfrastructure);
                    throw new AgentInfrastructureFailureException(
                        runner.Kind,
                        phase,
                        BuildAgentFailureDetail(
                            $"Agent {runner.Kind} exhausted native session recovery after sandbox execution became unavailable",
                            ex.LastResult,
                            _opts.MaxFailureDetailBytes));
                }

                var exitCode = AgentSuspendResilience.ParseAgentExitCode(ex.LastResult.Summary);
                if (AgentSuspendResilience.IsInfrastructureProcessExitCode(exitCode))
                {
                    await FinalizeInvolvementAsync(
                        involvementId,
                        AgentInvolvementOutcomes.FailureInfrastructure);
                    throw new AgentInfrastructureFailureException(
                        runner.Kind,
                        phase,
                        BuildAgentFailureDetail(
                            $"Agent {runner.Kind} exhausted native session recovery after process termination (exit {exitCode})",
                            ex.LastResult,
                            _opts.MaxFailureDetailBytes));
                }

                await FinalizeInvolvementAsync(involvementId, AgentInvolvementOutcomes.FailureAgent);
                throw;
            }
            catch (Exception ex)
            {
                // Any non-cancellation exception (agent error, quota, infrastructure,
                // terminal quota) is a genuine dispatch failure for the breaker.
                breakerSuccess = ClassifyDispatchOutcome(ex, genuineAttemptTimeout: false);
                if (ex is NoActionRequiredException)
                {
                    // Terminal resolution, not a run failure: the invocation
                    // metric must not record an error for an agent that ran
                    // cleanly and delivered its determination. (Involvement
                    // outcome maps to success via OutcomeForFailure below.)
                    outcome = AgentInvolvementOutcomes.Success;
                }
                await FinalizeInvolvementAsync(involvementId, OutcomeForFailure(ex));
                throw;
            }
            finally
            {
                // Feed the per-agent failure circuit breaker with this attempt's
                // outcome, keyed by the SAME canonical route key the router's
                // dispatch gate reads. Null (host/operator cancellation) is not the
                // agent's fault and is skipped so it neither opens nor resets it.
                if (breakerSuccess is { } dispatchOutcome)
                    _classRouter?.RecordDispatchOutcome(
                        runner.Kind,
                        CanonicalAgentRouteKey(runner.Kind, trialItem.AgentInstanceId),
                        dispatchOutcome);
                invSpan?.SetTag("codeybox.outcome", outcome);
                CodeyBoxMeters.AgentInvocations.Add(1,
                    new KeyValuePair<string, object?>("agent.kind", runner.Kind.Value),
                    new KeyValuePair<string, object?>("agent.instance", trialItem.AgentInstanceId ?? runner.Kind.Value),
                    new KeyValuePair<string, object?>("model", modelTag),
                    new KeyValuePair<string, object?>("agent_class", agentClassTag),
                    new KeyValuePair<string, object?>("phase", phase),
                    new KeyValuePair<string, object?>("outcome", outcome));
            }
        }

        async Task<AgentAuthRequiredException?> TryConvertResumeExhaustionToAuthRequiredAsync(
            IAgentRunner runner,
            WorkItem trialItem,
            AgentSessionResumeExhaustedException resumeEx,
            CancellationToken token)
        {
            var last = resumeEx.LastResult;
            var detection = _authFailureClassifier.DetectDetailed(runner.Kind, last.Stderr, last.Stdout);
            if (detection is { Classification.Kind: AgentFailureKind.AuthRequired })
            {
                // Route stdout-only evidence through the shared corroboration
                // policy so a model-controlled stdout match cannot globally bench
                // the agent without the forced in-VM probe confirming the prompt.
                // The exception we return still fails the work item terminally —
                // that's the deterministic per-item handling — but the global
                // bench side effect only fires when corroborated.
                var handling = await HandleAuthRequiredDetectionAsync(
                    trialItem,
                    project,
                    runner.Kind,
                    phase,
                    detection.Classification,
                    throwOnMatch: false,
                    stdoutOnlyEvidence: detection.IsStdoutOnly,
                    requireStdoutOnlyCorroboration: true,
                    matchedConfiguredPattern: detection.MatchedConfiguredStderrPattern
                        || detection.MatchedConfiguredStdoutPattern,
                    ct: token).ConfigureAwait(false);

                var reason = _authRequiredHandler.BuildReason(phase, detection.Classification, detection.IsStdoutOnly);
                return new AgentAuthRequiredException(
                    runner.Kind,
                    phase,
                    handling.Reason ?? reason,
                    handling.Scope ?? WorkItemAuthFailureScope.Fleet);
            }

            var classification = _authFailureClassifier.ClassifyFailure(runner, last);
            if (classification.Kind != AgentFailureKind.AuthError)
                return null;

            // Same contradiction policy as the steady-state AuthError path: a
            // healthy quota reading on the same credential vetoes the
            // fleet-wide bench; the item still fails terminally.
            var contradicted = await IsAuthContradictedByHealthyQuotaProbeAsync(
                trialItem, project, runner.Kind, trialItem.ModelId, token).ConfigureAwait(false);
            var authErrorReason = _authRequiredHandler.BuildReason(
                phase,
                classification,
                stdoutOnlyEvidence: false,
                stdoutOnlyNote: contradicted
                    ? "credential reads healthy on quota probe; item-level failure only, no fleet-wide bench"
                    : null);
            if (!contradicted)
            {
                await _authRequiredHandler.PublishSideEffectsAsync(
                    runner.Kind,
                    authErrorReason,
                    trialItem,
                    project,
                    ct: token).ConfigureAwait(false);
            }

            return new AgentAuthRequiredException(
                runner.Kind,
                phase,
                authErrorReason,
                contradicted ? WorkItemAuthFailureScope.Item : WorkItemAuthFailureScope.Fleet);
        }

        async Task<TerminalQuotaError?> TryConvertResumeExhaustionToQuotaAsync(
            IAgentRunner runner,
            WorkItem trialItem,
            AgentSessionResumeExhaustedException resumeEx,
            CancellationToken token)
        {
            var last = resumeEx.LastResult;
            _quotaAuditEmitter.EmitAdvisoryAuditEvents(
                runner.Kind, last.Stderr, last.Stdout, phase, sandboxName: null);

            var classification = _quotaClassifier.Classify(runner.Kind, last.Stderr, last.Stdout);
            if (classification is not
                {
                    Kind: QuotaFailureClassificationKind.Quota,
                    Detection: { } detection,
                })
            {
                return null;
            }

            await _quotaClassifier.RecordIfQuotaFailureAsync(
                _quotaFailures,
                runner.Kind,
                ResolveObservedModelId(runner, trialItem.ModelId),
                last.Summary,
                last.Stderr,
                DateTimeOffset.UtcNow,
                _auditQuotaOptions.ObservedFailureRetention,
                token,
                projectId: trialItem.ProjectId,
                stdout: last.Stdout).ConfigureAwait(false);

            return new TerminalQuotaError(
                detection.Kind,
                QuotaFailureMessage(
                    detection.Kind,
                    $"Agent {runner.Kind} reported quota failure after exhausting session resume",
                    SanitizedAgentDetail.FromRaw(last.Summary)),
                detection.ResetAt,
                providerSurfaceMatch: classification.ProviderSurfaceMatch);
        }

        TerminalTransientNetworkError? TryConvertResumeExhaustionToTransient(
            IAgentRunner runner,
            AgentSessionResumeExhaustedException resumeEx)
        {
            var last = resumeEx.LastResult;
            var classification = _authFailureClassifier.ClassifyFailure(runner, last);
            if (classification.Kind != AgentFailureKind.TransientNetwork)
                return null;

            var reason = string.IsNullOrWhiteSpace(classification.Reason)
                ? "transient transport/network failure"
                : RedactAndTruncateAgentDetail(classification.Reason);
            var summary = RedactAndTruncateAgentDetail(last.Summary);
            return new TerminalTransientNetworkError(
                runner.Kind,
                phase,
                classification,
                $"Agent {runner.Kind} reported transient transport failure after exhausting session resume during {phase}: {summary} ({reason})");
        }

        // Resolve the initial member from the work item's currently-selected agent.
        // OrchestratorService writes Agent / ModelId / ReasoningMode onto item before
        // calling Pipeline.RunAsync; we trust those as the first-attempt picks.
        var initialAgent = initialRunnerOverride?.Kind ?? item.Agent ?? project.DefaultAgent;
        IAgentRunner initialRunner;
        if (initialRunnerOverride is not null)
        {
            initialRunner = initialRunnerOverride;
        }
        else if (!_agents.TryGet(initialAgent, out initialRunner))
        {
            throw new InvalidOperationException($"No runner registered for agent '{initialAgent}'");
        }
        var initialItem = initialRunnerOverride is null
            ? item
            : item with
            {
                Agent = initialAgent,
                AgentInstanceId = initialMemberOverride?.RouteKey ?? item.AgentInstanceId,
                ModelId = initialMemberOverride?.ModelId ?? item.ModelId,
                ReasoningMode = initialMemberOverride?.ReasoningMode ?? item.ReasoningMode,
            };

        if (initialItem.AgentTurnRecoveryLease is not null)
        {
            // This pickup only authenticates/adopts mutable provider recovery
            // evidence and converts it to the immutable checkpoint. No agent
            // CLI is dispatched, so do not run agent smoke/fallback policy,
            // create an involvement row, or emit an agent-invocation metric.
            // The conversion has its own bounded checkpoint deadline.
            return await invoker(
                initialRunner,
                initialItem,
                phaseCancellation?.Token ?? ct);
        }

        var fallbackSmokeTarget = smokeTarget ?? ResolvePhaseSmokeTarget(project, phase, item.BaselineImageRef);

        // Single-attempt path when fallback is not wired (no class, no router).
        // The behaviour matches the legacy code: TerminalQuotaError bubbles out.
        if (_classRouter is null
            || (item.AgentClassId is null && project.DefaultAgentClass is null))
        {
            var smokeAvailability = skipInVmSmoke
                ? await EnsureAgentPauseAllowsTextOnlyAsync(initialRunner.Kind, initialItem.AgentInstanceId, ct)
                : await EnsureAgentSmokeAvailableAsync(initialRunner.Kind, fallbackSmokeTarget, ct);
            if (!smokeAvailability.Available)
            {
                if (IsOperatorPaused(smokeAvailability))
                {
                    var pausedReason = smokeAvailability.Reason ?? AgentDispatchAvailability.PausedReasonPrefix;
                    throw new AgentPausedException(phase, initialRunner.Kind, pausedReason);
                }

                var reason = smokeAvailability.Reason ?? "unavailable";
                throw new AgentUnavailableException(
                    $"agent '{initialRunner.Kind.Value}' rejected by in-VM smoke gate in phase '{phase}': {reason}",
                    $"{initialRunner.Kind.Value}: smoke gate: {reason}",
                    initialRunner.Kind);
            }

            try
            {
                return await InvokeAttemptAsync(initialRunner, initialItem);
            }
            catch (AgentAttemptTimeoutException timeoutEx) when (phaseCancellation is not null)
            {
                throw new PhaseCancellationException(
                    phaseCancellation.Phase,
                    CancellationSources.PhaseTimeout(phaseCancellation.Phase),
                    timeoutEx);
            }
        }

        var classId = item.AgentClassId ?? project.DefaultAgentClass!;
        // Capability-pool filter for mid-iteration spill: when the caller
        // requires a capability (e.g. "audit") and the routed class has
        // at least one effectively capable member, mid-iteration fallback must
        // stay inside that pool — otherwise a Claude audit that quota-fails
        // could spill to a Gemini member which the operator never authorised
        // for auditing. Null pool = no opt-in for this class → legacy
        // unfiltered fallback (matches ResolveAuditAgentRunnerAsync gating).
        var requiredCapabilityPoolActive = requireCapability is not null
            && _classRouter.GetCapabilityPool(classId, requireCapability) is not null;
        var triedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var triedCount = 0;
        DateTimeOffset? earliestReset = null;
        var sawQuotaBlockedCandidate = false;
        // Armed by every successful runner swap and consumed by the next
        // attempt's outcome: an authentication failure on the first attempt
        // immediately after a swap is classified as infrastructure (the
        // swapped-in agent likely executed without its own credentials),
        // never as an agent re-authentication requirement. Disarmed by any
        // completed post-swap attempt or by the conversion itself, so genuine
        // credential expiry on later attempts still fails as auth-required.
        var postSwapAuthGuardArmed = false;
        var currentRunner = initialRunner;
        var currentItem = initialItem;
        AgentKind? pausedFallbackAgent = null;
        // Prefer the catalog's real AgentMembership (correct Billing / QualityScore /
        // ReasoningMode) so probe write-backs receive an accurate record. Only fall
        // back to a synthesised placeholder when the catalog has no matching row —
        // e.g. tests that exercise the wrapper without a fully-populated class.
        var currentMember = initialMemberOverride
            ?? _classRouter.FindMember(classId, initialAgent, item.ModelId, item.AgentInstanceId)
            ?? new AgentMembership
            {
                Agent = initialAgent,
                InstanceId = item.AgentInstanceId,
                ModelId = item.ModelId,
                ReasoningMode = item.ReasoningMode,
                Billing = AgentBilling.Subscription,
                QualityScore = 100,
            };
        // Bind the runner to this attempt's member: a member-scoped runner
        // (e.g. Copilot with a per-member BYOK provider) must render every
        // invocation — work, rework, audit, merge — from the member's
        // effective configuration, not the agent-global one.
        // Non-member-scoped runners pass through untouched.
        currentRunner = BindMemberRunner(currentRunner, currentMember);

        static string TriedMemberKey(AgentMembership member) =>
            $"{member.RouteKey}\0{member.ModelId ?? string.Empty}";

        async Task MoveToNextMemberOrThrowAsync(
            string safeReason,
            AgentFallbackTrigger trigger,
            DateTimeOffset? quotaResetAt,
            Exception terminalException,
            bool smokeRejected = false,
            bool pausedRejected = false)
        {
            var quotaExhausted = trigger == AgentFallbackTrigger.Quota;
            var fallbackKind = pausedRejected ? "paused" : smokeRejected ? "smoke" : FallbackMetricKind(trigger);
            if (pausedRejected)
                pausedFallbackAgent ??= currentRunner.Kind;
            if (quotaExhausted)
            {
                sawQuotaBlockedCandidate = true;
                // Cap the reset hint against a sane operator-visible ceiling. Reset
                // windows are extracted from attacker-influenceable agent output;
                // a maliciously-crafted Retry-After could otherwise park an item
                // arbitrarily far in the future.
                var clampedReset = ClampQuotaReset(quotaResetAt, _pipelineTuning.Current.MaxParsedQuotaResetWindow);

                // Mark the member exhausted in the router and the probe so the
                // next pickup (or the rest of this pipeline) skips it.
                _classRouter.MarkExhausted(currentMember, _pipelineTuning.Current.QuotaExhaustionFallbackTtl, clampedReset);
                if (ResolveQuotaProbe(currentMember).Probe is { } probe)
                {
                    try
                    {
                        await probe.MarkExhaustedAsync(currentMember, _pipelineTuning.Current.QuotaExhaustionFallbackTtl, clampedReset, ct);
                    }
                    catch (Exception probeEx) when (probeEx is not OperationCanceledException)
                    {
                        // Probe write-back is best-effort; in-process cache still suppresses.
                        _log.LogDebug(probeEx, "MarkExhaustedAsync failed for {Agent}", currentMember.Agent.Value);
                    }
                }
                if (clampedReset is { } reset
                    && (earliestReset is null || reset < earliestReset))
                {
                    earliestReset = reset;
                }
            }

            // Find the next candidate that we haven't already tried this run.
            var candidates = await _classRouter.OrderedFallbackCandidatesAsync(item, project, ct, fallbackSmokeTarget);
            AgentMembership? nextMember = null;
            IAgentRunner? nextRunner = null;
            foreach (var candidate in candidates)
            {
                var key = TriedMemberKey(candidate);
                if (triedKeys.Contains(key)) continue;
                // Capability-pool filter (e.g. audit). When the pool is active,
                // a candidate outside it must NEVER be chosen for the spill —
                // matches the resolve-time gate in ResolveAuditAgentRunnerAsync
                // so the work item never ends up on an agent the operator did
                // not tag for this phase.
                if (requiredCapabilityPoolActive
                    && !MemberHasClassCapability(classId, candidate, requireCapability!))
                {
                    _log.LogDebug(
                        "Class '{ClassId}' member '{Agent}' not in '{Capability}' pool; skipping for fallback (work item {WorkItemId})",
                        classId, candidate.Agent.Value, requireCapability, item.Id);
                    continue;
                }
                if (!_agents.TryGet(candidate.Agent, out var candidateRunner))
                {
                    // Audible misconfiguration: class declares this agent kind but
                    // no runner is wired in DI; skipping silently would hide the gap.
                    _log.LogWarning(
                        "Class '{ClassId}' member {Agent} has no registered runner; skipping for fallback (work item {WorkItemId})",
                        classId, candidate.Agent.Value, item.Id);
                    continue;
                }
                // Agent-switch credential gate (the same seam the
                // conflict-resolver path uses): the incoming runner must be
                // dispatchable with its OWN credentials before it is
                // selected. A swap that cannot work is not a fallback —
                // dispatching it would run the agent unauthenticated and
                // 401, so the candidate is refused and the search continues.
                // When every candidate is refused, the no-candidate branch
                // below keeps the original failure with no dispatch attempted.
                var candidateTrialItem = item with
                {
                    Agent = candidate.Agent,
                    AgentInstanceId = candidate.RouteKey,
                    ModelId = candidate.ModelId,
                    ReasoningMode = candidate.ReasoningMode,
                };
                IAgentRunner boundCandidateRunner;
                try
                {
                    boundCandidateRunner = BindMemberRunner(candidateRunner, candidate);
                }
                catch (Exception bindEx) when (bindEx is not OperationCanceledException)
                {
                    _log.LogWarning(bindEx,
                        "Class '{ClassId}' member {Agent}/{Model} cannot bind its runner configuration; refusing for fallback (work item {WorkItemId})",
                        classId, candidate.Agent.Value, candidate.ModelId ?? "(default)", item.Id);
                    triedKeys.Add(key);
                    continue;
                }
                AgentCredential? candidateCredential;
                try
                {
                    candidateCredential = await ResolveAgentCredentialForInvocationAsync(
                        boundCandidateRunner, project, candidateTrialItem, ct);
                }
                catch (Exception credEx) when (credEx is not OperationCanceledException)
                {
                    _log.LogWarning(credEx,
                        "Class '{ClassId}' member {Agent}/{Model} credential could not be resolved; refusing for fallback (work item {WorkItemId})",
                        classId, candidate.Agent.Value, candidate.ModelId ?? "(default)", item.Id);
                    triedKeys.Add(key);
                    continue;
                }
                var switchAssessment = AgentRunnerSwitchGate.AssessSwitch(boundCandidateRunner, candidateCredential);
                if (!switchAssessment.Allowed)
                {
                    _log.LogWarning(
                        "Class '{ClassId}' member {Agent}/{Model} refused for fallback (work item {WorkItemId}): {Reason}",
                        classId, candidate.Agent.Value, candidate.ModelId ?? "(default)", item.Id,
                        switchAssessment.RefusalReason ?? "credential cannot be materialised");
                    triedKeys.Add(key);
                    continue;
                }
                // The router's in-process exhausted-cache filters most stale
                // picks, but a member can be quota-failed in the persistent
                // observed-failure store (e.g. an earlier process recorded
                // the failure and just-started workers haven't seen the
                // event yet). Skip those too so the audit pipeline doesn't
                // burn a roundtrip rediscovering an exhaustion we already know.
                if (_quotaFailures is not null
                    && await _quotaFailures.HasRecentAsync(
                        candidate.Agent, candidate.ModelId,
                        _auditQuotaOptions.ObservedFailureWindow,
                        DateTimeOffset.UtcNow, ct))
                {
                    sawQuotaBlockedCandidate = true;
                    _log.LogInformation(
                        "Class '{ClassId}' member {Agent}/{Model} has a recent observed quota failure; skipping for fallback (work item {WorkItemId})",
                        classId, candidate.Agent.Value, candidate.ModelId ?? "(default)", item.Id);
                    continue;
                }
                // Local operator-budget gate. OrderedFallbackCandidates already
                // applies the router's budget provider when wired; keep this
                // pipeline-side fail-closed check for fixtures or deployments
                // where the pipeline has the provider but the router was built
                // without it.
                var (budget, budgetFailedClosed) =
                    await ReadCandidateBudgetAsync(candidate.Agent, candidate.ModelId, ct);
                var budgetPct = budget?.AvailablePct ?? -1;
                string? budgetRejectedReason = null;
                if (budgetPct >= 0 && budget is { } budgetSnapshot)
                {
                    var budgetGate = _auditQuotaGatePolicy.Evaluate(
                        candidate,
                        new EffectiveQuota(
                            budgetPct,
                            null,
                            null,
                            budgetSnapshot.Windows),
                        DateTimeOffset.UtcNow);
                    if (!budgetGate.Allow)
                        budgetRejectedReason = FormatBudgetGateComparison(budgetPct, budgetGate);
                }
                if (budgetFailedClosed || budgetRejectedReason is not null)
                {
                    sawQuotaBlockedCandidate = true;
                    _log.LogInformation(
                        "Class '{ClassId}' member {Agent}/{Model} local budget exhausted ({Pct}); skipping for fallback (work item {WorkItemId})",
                        classId, candidate.Agent.Value, candidate.ModelId ?? "(default)",
                        budgetFailedClosed ? "provider error" : budgetRejectedReason, item.Id);
                    continue;
                }
                nextMember = candidate;
                // Assessed (and member-bound) above by the agent-switch
                // credential gate; carried out so the swap below dispatches
                // exactly the runner that was validated.
                nextRunner = boundCandidateRunner;
                break;
            }

            if (nextMember is null)
            {
                if (quotaExhausted)
                {
                    AuditLog.AgentQuotaAllExhausted(item.Id, classId, phase, triedCount);
                    CodeyBoxMeters.AgentFallbacks.Add(1,
                        new KeyValuePair<string, object?>("from_agent", currentMember.Agent.Value),
                        new KeyValuePair<string, object?>("to_agent", "(none)"),
                        new KeyValuePair<string, object?>("kind", "quota"),
                        new KeyValuePair<string, object?>("phase", phase));
                    if (_fallbackHistory is not null)
                    {
                        try
                        {
                            await _fallbackHistory.RecordAsync(new AgentFallbackRecord(
                                Id: Guid.NewGuid(),
                                WorkItemId: item.Id,
                                Phase: phase,
                                Iteration: iteration,
                                FromAgent: currentMember.Agent,
                                FromModel: currentMember.ModelId,
                                ToAgent: null,
                                ToModel: null,
                                Reason: safeReason,
                                OccurredAt: DateTimeOffset.UtcNow,
                                FromInstanceId: currentMember.RouteKey), CancellationToken.None);
                        }
                        catch (Exception histEx)
                        {
                            _log.LogDebug(histEx, "fallback history record failed for all-exhausted event");
                        }
                    }
                    var msg = $"All {triedCount} eligible member(s) of class '{classId}' exhausted mid-{phase}; " +
                              $"last failure: {safeReason}";
                    throw new AgentClassExhaustedException(classId, phase, triedCount, earliestReset, msg);
                }

                if (smokeRejected)
                    throw new AgentUnavailableException(
                        $"all eligible member(s) of class '{classId}' were rejected by the in-VM smoke gate in phase '{phase}'; last rejection: {safeReason}",
                        safeReason,
                        currentMember.Agent);

                if (pausedRejected && sawQuotaBlockedCandidate)
                {
                    var msg = $"All {triedCount} eligible member(s) of class '{classId}' exhausted or paused mid-{phase}; " +
                              $"last paused rejection: {safeReason}";
                    throw new AgentPausedException(phase, pausedFallbackAgent ?? currentMember.Agent, msg);
                }

                if (pausedRejected)
                    throw new AgentPausedException(phase, pausedFallbackAgent ?? currentMember.Agent, safeReason);

                if (trigger == AgentFallbackTrigger.Timeout)
                {
                    var timeoutPhase = phaseCancellation?.Phase ?? phase;
                    throw new PhaseCancellationException(
                        timeoutPhase,
                        CancellationSources.PhaseTimeout(timeoutPhase),
                        terminalException);
                }

                // A terminal authentication failure with the post-swap guard
                // still armed means the swapped-in agent 401d on its very
                // first attempt: infrastructure (missing credential
                // materialisation), not an agent re-authentication
                // requirement. Without a preceding swap the guard is disarmed
                // and genuine credential expiry still fails as auth-required.
                if (terminalException is AgentAuthRequiredException terminalAuth && postSwapAuthGuardArmed)
                {
                    postSwapAuthGuardArmed = false;
                    throw AgentRunnerSwitchGate.ToPostSwapInfrastructureFailure(terminalAuth, phase);
                }

                throw terminalException;
            }

            if (nextRunner is null)
                throw new InvalidOperationException($"No runner resolved for fallback agent '{nextMember.Agent}'");

            if (quotaExhausted)
            {
                AuditLog.AgentQuotaFallback(
                    item.Id, phase, iteration,
                    fromAgent: currentMember.Agent, fromModel: currentMember.ModelId,
                    toAgent: nextMember.Agent, toModel: nextMember.ModelId,
                    reason: safeReason);
            }
            else if (trigger == AgentFallbackTrigger.Timeout)
            {
                if (smokeRejected)
                {
                    _log.LogInformation(
                        "Class '{ClassId}' member {FromAgent}/{FromModel} rejected by smoke gate; routing phase '{Phase}' to {ToAgent}/{ToModel}",
                        classId, currentMember.Agent.Value, currentMember.ModelId ?? "(default)",
                        phase, nextMember.Agent.Value, nextMember.ModelId ?? "(default)");
                }
                else if (pausedRejected)
                {
                    _log.LogInformation(
                        "Class '{ClassId}' member {FromAgent}/{FromModel} is paused; routing phase '{Phase}' to {ToAgent}/{ToModel}",
                        classId, currentMember.Agent.Value, currentMember.ModelId ?? "(default)",
                        phase, nextMember.Agent.Value, nextMember.ModelId ?? "(default)");
                }
                else
                {
                    AuditLog.AgentAttemptTimeoutFallback(
                        item.Id, phase, iteration,
                        fromAgent: currentMember.Agent, fromModel: currentMember.ModelId,
                        toAgent: nextMember.Agent, toModel: nextMember.ModelId,
                        reason: safeReason);
                }
            }
            else if (trigger == AgentFallbackTrigger.AuthRequired)
            {
                _log.LogWarning(
                    "Class '{ClassId}' member {FromAgent}/{FromModel} requires authentication; routing phase '{Phase}' to {ToAgent}/{ToModel}",
                    classId, currentMember.Agent.Value, currentMember.ModelId ?? "(default)",
                    phase, nextMember.Agent.Value, nextMember.ModelId ?? "(default)");
            }
            else
            {
                AuditLog.AgentResumeExhaustedFallback(
                    item.Id, phase, iteration,
                    fromAgent: currentMember.Agent, fromModel: currentMember.ModelId,
                    toAgent: nextMember.Agent, toModel: nextMember.ModelId,
                    reason: safeReason);
            }
            CodeyBoxMeters.AgentFallbacks.Add(1,
                new KeyValuePair<string, object?>("from_agent", currentMember.Agent.Value),
                new KeyValuePair<string, object?>("to_agent", nextMember.Agent.Value),
                new KeyValuePair<string, object?>("kind", fallbackKind),
                new KeyValuePair<string, object?>("phase", phase));

            // Trial item carries the new Agent / ModelId / ReasoningMode so webhook
            // consumers that read WorkItem.Agent see the agent actually being run.
            // The handoff brief (when EnableHandoffSeeding is on) is injected by the
            // CrossAgentHandoffPromptPreprocessor on the next agent invocation; it
            // reads the fallback history record we write below and asks the wired
            // ICrossAgentHandoffBriefBuilder for a fenced + sanitised brief. Keep
            // the prompt unchanged here.
            var trialItem = item with
            {
                Agent = nextMember.Agent,
                AgentInstanceId = nextMember.RouteKey,
                ModelId = nextMember.ModelId,
                ReasoningMode = nextMember.ReasoningMode,
            };

            if (_fallbackHistory is not null)
            {
                try
                {
                    await _fallbackHistory.RecordAsync(new AgentFallbackRecord(
                        Id: Guid.NewGuid(),
                        WorkItemId: item.Id,
                        Phase: phase,
                        Iteration: iteration,
                        FromAgent: currentMember.Agent,
                        FromModel: currentMember.ModelId,
                        ToAgent: nextMember.Agent,
                        ToModel: nextMember.ModelId,
                        Reason: safeReason,
                        OccurredAt: DateTimeOffset.UtcNow,
                        FromInstanceId: currentMember.RouteKey,
                        ToInstanceId: nextMember.RouteKey), CancellationToken.None);
                }
                catch (Exception histEx)
                {
                    _log.LogDebug(histEx, "fallback history record failed for agent.fallback event");
                }
            }

            if (_webhooks is not null)
            {
                try
                {
                    await _webhooks.PublishAsync(new WebhookEvent
                    {
                        Event = "agent.fallback",
                        WorkItem = trialItem,
                        Project = project,
                        Details = new AgentFallbackDetails(
                            WorkItemId: item.Id.ToString(),
                            Phase: phase,
                            Iteration: iteration,
                            FromAgent: currentMember.Agent.Value,
                            FromModel: currentMember.ModelId,
                            ToAgent: nextMember.Agent.Value,
                            ToModel: nextMember.ModelId,
                            Reason: safeReason),
                    }, CancellationToken.None);
                }
                catch (Exception webhookEx)
                {
                    _log.LogDebug(webhookEx, "agent.fallback webhook publish failed");
                }
            }

            currentMember = nextMember;
            currentRunner = nextRunner;
            currentItem = trialItem;
            // nextRunner was already bound to the NEW member by the
            // agent-switch credential gate during candidate selection (the
            // fallback member brings its own configuration, e.g. a different
            // Copilot BYOK provider or the native subscription), so the retry
            // runs bound to the incoming member, not the exhausted one.
            // The swap arms the post-swap auth guard: a 401 on the very next
            // attempt is infrastructure, not re-authentication.
            postSwapAuthGuardArmed = true;
            // The retry must execute with the incoming member's credential
            // environment. Direct credential variables are baked into the
            // sandbox spec at creation, so a warm reusable sandbox still
            // carries the exhausted member's environment — the incoming
            // agent's CLI would run without its own credentials and 401.
            // Surrender it so the retry provisions a fresh sandbox from the
            // incoming trial item's spec. No-op when reuse is disabled or no
            // reusable sandbox is held; same-member retries never reach here.
            await ReleaseAmbientWorkSandboxAsync();
        }

        while (true)
        {
            triedKeys.Add(TriedMemberKey(currentMember));
            triedCount++;

            var smokeAvailability = skipInVmSmoke
                ? await EnsureAgentPauseAllowsTextOnlyAsync(currentRunner.Kind, currentItem.AgentInstanceId, ct)
                : await EnsureAgentSmokeAvailableAsync(currentRunner.Kind, fallbackSmokeTarget, ct);
            if (!smokeAvailability.Available)
            {
                if (IsOperatorPaused(smokeAvailability))
                {
                    var pausedReason = SingleLineSummary(
                        smokeAvailability.Reason ?? AgentDispatchAvailability.PausedReasonPrefix);
                    await MoveToNextMemberOrThrowAsync(
                        pausedReason,
                        AgentFallbackTrigger.Timeout,
                        quotaResetAt: null,
                        terminalException: new AgentPausedException(phase, currentRunner.Kind, pausedReason),
                        pausedRejected: true);
                    continue;
                }

                var safeReason = SingleLineSummary(
                    $"smoke gate: {smokeAvailability.Reason ?? "unavailable"}");
                await MoveToNextMemberOrThrowAsync(
                    safeReason,
                    AgentFallbackTrigger.Timeout,
                    quotaResetAt: null,
                    terminalException: new AgentUnavailableException(
                        $"agent '{currentRunner.Kind.Value}' rejected by in-VM smoke gate in phase '{phase}': {safeReason}",
                        safeReason,
                        currentRunner.Kind),
                    smokeRejected: true);
                continue;
            }

            try
            {
                var attemptResult = await InvokeAttemptAsync(currentRunner, currentItem);
                // A completed post-swap attempt (success or a failure the
                // catches below do not convert) consumes the guard: later
                // auth failures are genuine credential events, not swap
                // artefacts.
                postSwapAuthGuardArmed = false;
                return attemptResult;
            }
            catch (TerminalQuotaError quotaEx)
            {
                // Normalize stderr-derived reason for log/webhook serialization:
                // strip CR/LF so plain-text log sinks can't be spoofed by embedded
                // newlines (CWE-117), and trim to a single-line summary.
                var safeReason = SingleLineSummary(quotaEx.Message);
                await MoveToNextMemberOrThrowAsync(
                    safeReason,
                    AgentFallbackTrigger.Quota,
                    quotaResetAt: quotaEx.ResetAt,
                    terminalException: quotaEx);
            }
            catch (AgentAuthRequiredException authEx) when (allowAuthRequiredFallback)
            {
                var safeReason = SingleLineSummary(authEx.Message);
                await MoveToNextMemberOrThrowAsync(
                    safeReason,
                    AgentFallbackTrigger.AuthRequired,
                    quotaResetAt: null,
                    terminalException: authEx);
            }
            catch (AgentAuthRequiredException authEx) when (postSwapAuthGuardArmed)
            {
                // The swapped-in runner 401d on its very first attempt. The
                // swap was assessed as materialisable before dispatch, so
                // this is infrastructure (the agent likely executed without
                // its own credentials), not an agent re-authentication
                // requirement. Without a preceding swap the guard is disarmed
                // and the auth failure propagates unchanged, so a genuinely
                // expired credential still fails the item as auth-required.
                postSwapAuthGuardArmed = false;
                throw AgentRunnerSwitchGate.ToPostSwapInfrastructureFailure(authEx, phase);
            }
            catch (AgentAttemptTimeoutException timeoutEx)
            {
                var safeReason = SingleLineSummary(timeoutEx.Message);
                await MoveToNextMemberOrThrowAsync(
                    safeReason,
                    AgentFallbackTrigger.Timeout,
                    quotaResetAt: null,
                    terminalException: timeoutEx);
            }
            catch (AgentSessionResumeExhaustedException resumeEx)
            {
                var safeReason = SingleLineSummary(resumeEx.Message);
                await MoveToNextMemberOrThrowAsync(
                    safeReason,
                    AgentFallbackTrigger.ResumeExhausted,
                    quotaResetAt: null,
                    terminalException: resumeEx);
            }
        }
    }

    private enum AgentFallbackTrigger
    {
        Quota,
        AuthRequired,
        Timeout,
        ResumeExhausted,
    }

    private static string FallbackMetricKind(AgentFallbackTrigger trigger) => trigger switch
    {
        AgentFallbackTrigger.Quota => "quota",
        AgentFallbackTrigger.AuthRequired => "auth",
        AgentFallbackTrigger.Timeout => "timeout",
        AgentFallbackTrigger.ResumeExhausted => "resume_exhausted",
        _ => "agent",
    };

    private Task<Guid?> RecordInvolvementStartAsync(
        WorkItemId workItemId, AgentKind agent, string? agentInstanceId, string? modelId, string phase, int? iteration)
        => _involvementTracker.RecordStartAsync(workItemId, agent, agentInstanceId, modelId, phase, iteration);

    private Task FinalizeInvolvementAsync(Guid? involvementId, string outcome)
        => _involvementTracker.FinalizeAsync(involvementId, outcome);

    private static string OutcomeForFailure(Exception ex) => InvolvementTracker.OutcomeForFailure(ex);

    /// <summary>
    /// Maps a completed auditor run to an involvement outcome. A quota-shaped
    /// agent failure is surfaced as <c>failure:quota</c> (the same signal that
    /// later triggers fallback), a non-quota review-agent crash as
    /// <c>failure:agent</c>; everything else — including a clean pass and a pass
    /// that merely reported findings — is <c>success</c> (the agent ran fine; the
    /// findings are the work product, not a run failure).
    /// </summary>
    private string AuditorRunOutcome(IAgentRunner runner, AuditResult result)
    {
        if (_quotaClassifier.Detect(runner.Kind, result.AgentStderr, result.AgentStdout) is not null)
            return AgentInvolvementOutcomes.FailureQuota;

        var classification = _authFailureClassifier.ClassifyFailure(runner, ToAgentResultForAuditFailureClassification(result));
        if (classification.Kind == AgentFailureKind.QuotaExhausted)
            return AgentInvolvementOutcomes.FailureQuota;
        if (classification.Kind == AgentFailureKind.TransientNetwork)
            return AgentInvolvementOutcomes.FailureTransient;
        if (classification.Kind == AgentFailureKind.AuthError)
            return AgentInvolvementOutcomes.FailureAuth;
        if (IsLlmAgentExecutionFailure(result))
            return AgentInvolvementOutcomes.FailureAgent;
        return AgentInvolvementOutcomes.Success;
    }

    private static AgentResult ToAgentResultForAuditFailureClassification(AuditResult result) =>
        new(
            Success: !IsLlmAgentExecutionFailure(result),
            Summary: result.AgentSummary ?? "agent failed",
            Stdout: result.AgentStdout,
            Stderr: result.AgentStderr);

    internal sealed class AgentAttemptTimeoutException : OperationCanceledException
    {
        public AgentAttemptTimeoutException(
            string phase,
            AgentKind agent,
            TimeSpan timeout,
            Exception inner)
            : base($"Agent {agent.Value} attempt in phase '{phase}' exceeded per-attempt timeout {timeout}.", inner)
        {
        }
    }

    private sealed class AgentTurnCheckpointConvertedException : Exception
    {
        public AgentTurnCheckpointConvertedException(string phase)
            : base("Retained sandbox was converted to an immutable checkpoint.")
        {
            Phase = phase;
        }

        public string Phase { get; }
    }

    private sealed class AgentTurnResumeClaimConflictException : Exception
    {
        public AgentTurnResumeClaimConflictException(string message)
            : base(message)
        {
        }
    }

    private sealed class InvalidAgentTurnResumeCheckpointException : Exception
    {
        public InvalidAgentTurnResumeCheckpointException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    internal static string? ResolveObservedModelId(IAgentRunner runner, string? modelId)
    {
        if (modelId is not null)
            return string.IsNullOrWhiteSpace(modelId) ? null : modelId;

        if (runner is IAgentDefaultModelProvider defaults)
            return string.IsNullOrWhiteSpace(defaults.DefaultModelId) ? null : defaults.DefaultModelId;

        return null;
    }

    /// <summary>
    /// Resolves the model id an auditor dispatches on (and records spend under, so
    /// dispatch and usage accounting stay on a single value). A same-kind auditor
    /// keeps the work item's model (<paramref name="ctxModelId"/>, itself the work
    /// member's configured ModelId). A cross-kind auditor cannot use the work model
    /// (it is vendor-specific to the work kind), so it resolves the AUDITOR's own
    /// configured class-member model (<paramref name="auditorMemberModelId"/>) —
    /// the single source of truth for that agent's model. In either branch, an
    /// empty resolved model falls back to the runner's config-driven
    /// <see cref="IAgentDefaultModelProvider.DefaultModelId"/> (never a hardcoded
    /// literal), so spend still lands in a concrete bucket and the CLI never
    /// silently drops to its stale built-in model (the gpt-5.5 mis-route incident).
    /// </summary>
    internal static string? ResolveAuditModelId(
        IAgentRunner auditRunner, AgentKind workRunnerKind, string? ctxModelId, string? auditorMemberModelId)
    {
        var crossKind = auditRunner.Kind != workRunnerKind;
        return ResolveObservedModelId(auditRunner, crossKind ? auditorMemberModelId : ctxModelId);
    }

    /// <summary>
    /// Clamps a parsed reset-window hint against <paramref name="maxWindow"/>
    /// (production callers pass <c>_pipelineTuning.Current.MaxParsedQuotaResetWindow</c>;
    /// falls back to the legacy static <see cref="MaxParsedQuotaResetWindow"/>
    /// when <paramref name="maxWindow"/> is omitted). The hint comes from agent
    /// stdout/stderr and is attacker-influenceable via prompt injection; without
    /// a ceiling, a hostile output could park an item arbitrarily far in the
    /// future and re-arm targeted retry timers for that instant. Returns null
    /// when input is null.
    /// </summary>
    internal static DateTimeOffset? ClampQuotaReset(DateTimeOffset? resetAt, TimeSpan? maxWindow = null)
    {
        if (resetAt is not { } parsed) return null;
        var now = DateTimeOffset.UtcNow;
        var ceiling = now + (maxWindow ?? MaxParsedQuotaResetWindow);
        return parsed > ceiling ? ceiling : parsed;
    }

    /// <summary>
    /// Renders the recorded failure reason for a quota-shaped terminal error,
    /// distinguishing a transient provider rate refusal from a spent account
    /// cap. Operators respond differently to the two — a 429 clears on its
    /// own backoff, an exhausted cap needs capacity or a new window — so the
    /// <c>LastError</c> parked on the work item must not conflate them.
    ///
    /// <para>The agent-controlled tail is accepted only as a
    /// <see cref="SanitizedAgentDetail"/> (see
    /// <see cref="SanitizedAgentDetail.FromRaw"/>), never as a raw string, so
    /// a future call site cannot bypass redaction and truncation by
    /// interpolating agent output directly. The <paramref name="prefix"/>
    /// carries only orchestrator-owned text (agent kind, phase, auditor name,
    /// evidence source).</para>
    ///
    /// <para>Only the <see cref="QuotaFailureKind.RateLimitExceeded"/> wording
    /// changes; every other kind returns the composed message byte-identical.
    /// The rate-limit rewrite targets the single
    /// <c>"reported quota failure"</c> marker: messages that do not carry it
    /// are returned unchanged rather than guessed at.</para>
    /// </summary>
    internal static string QuotaFailureMessage(QuotaFailureKind kind, string prefix, SanitizedAgentDetail detail)
    {
        var exhaustedMessage = $"{prefix}: {detail.Value}";
        if (kind != QuotaFailureKind.RateLimitExceeded)
            return exhaustedMessage;
        const string marker = "reported quota failure";
        var index = exhaustedMessage.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
            return exhaustedMessage;
        return string.Concat(
            exhaustedMessage.AsSpan(0, index),
            "rate-limited by provider (transient rate limit; retrying after backoff)",
            exhaustedMessage.AsSpan(index + marker.Length));
    }

    /// <summary>
    /// R8-core: deterministic in-VM path to the tee'd agent log file for a
    /// single agent invocation. Persisted on the work item so the suspend-on-
    /// shutdown handler can read it back without coordinating with this
    /// process, and the startup resume handler can re-tail the same file on
    /// the resumed VM. <paramref name="phase"/> / <paramref name="iteration"/>
    /// keep adjacent runs from clobbering each other; iteration is null for
    /// merge / conflict-rework invocations that have no audit-loop counter.
    /// </summary>
    internal static string BuildAgentLogPath(WorkItemId workItemId, string phase, int? iteration)
    {
        var safePhase = string.IsNullOrEmpty(phase) ? "agent" : phase;
        var iterSuffix = iteration.HasValue ? $"-i{iteration.Value}" : string.Empty;
        return $"{SandboxConventions.AgentLogDir}/{workItemId.ToString()}-{safePhase}{iterSuffix}.log";
    }

    /// <summary>
    /// Unstages CodeyBox's internal agent-log scratch directory
    /// (<see cref="SandboxConventions.AgentLogDir"/> — <c>.codeybox/agent-logs/</c>)
    /// from the git index before a commit. Those files are orchestrator diagnostics
    /// tee'd into the work tree during the agent run — the base stdout/stderr
    /// capture and, for antigravity, agy's <em>unredacted</em> internal glog
    /// (auth material, tool output, model resolution). They must never be committed
    /// to the work branch and pushed in the PR. CodeyBox operates on arbitrary
    /// target repos and cannot assume they gitignore <c>.codeybox/</c>, so the strip
    /// is explicit — mirroring the <c>suggestions.json</c> strip. The working-tree
    /// copies are left in place (<c>--cached</c>) so the suspend/resume re-tail path
    /// still reads them; <c>--ignore-unmatch</c> makes it a no-op when nothing under
    /// the dir was staged.
    /// </summary>
    private static Task StripAgentLogScratchFromIndexAsync(ISandbox sandbox, CancellationToken ct) =>
        sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", SandboxConventions.WorkDir, "rm", "-r", "--cached",
                "--ignore-unmatch", "--", ".codeybox/agent-logs"],
        }, ct);

    private static readonly string[] ReservedLegacyScratchpadPrefixes =
    [
        AgentTurnScratchpadArchive.LegacyCapturePrefix,
        AgentTurnScratchpadArchive.LegacyRestorePrefix,
    ];

    private static readonly string[] ReservedLegacyScratchpadPathspecs =
        ReservedLegacyScratchpadPrefixes
            .SelectMany(static prefix => new[]
            {
                $":(glob){prefix}*",
                $":(glob){prefix}*/**",
            })
            .ToArray();

    private static readonly string[] ReservedLegacyScratchpadExcludePathspecs =
        ReservedLegacyScratchpadPrefixes
            .SelectMany(static prefix => new[]
            {
                $":(exclude,glob){prefix}*",
                $":(exclude,glob){prefix}*/**",
            })
            .ToArray();

    /// <summary>
    /// Removes every legacy repository-local capture artifact from the candidate
    /// tree, then positively verifies the index. Provider state is private and
    /// must only travel through the bounded host-private scratchpad store.
    /// </summary>
    private static async Task StripReservedScratchpadPathsFromIndexAsync(
        ISandbox sandbox,
        CancellationToken ct)
    {
        var removal = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "git", "-C", SandboxConventions.WorkDir,
                "rm", "-r", "--cached", "--ignore-unmatch", "--",
                .. ReservedLegacyScratchpadPathspecs,
            ],
            MaxStdoutBytes = 4096,
            MaxStderrBytes = 4096,
            KillOnOutputLimit = true,
        }, ct);
        ThrowIfExecutionUnavailable(removal);
        if (!removal.Success || removal.StdoutLimitExceeded || removal.StderrLimitExceeded)
            throw new InvalidOperationException("Failed to remove reserved agent scratchpad paths from the Git index.");

        await EnsureReservedScratchpadPathsAbsentAsync(sandbox, tree: null, ct);
    }

    private static Task EnsureReservedScratchpadPathsAbsentFromTreeAsync(
        ISandbox sandbox,
        CancellationToken ct) =>
        EnsureReservedScratchpadPathsAbsentAsync(sandbox, tree: "HEAD", ct);

    private static async Task EnsureReservedScratchpadPathsAbsentAsync(
        ISandbox sandbox,
        string? tree,
        CancellationToken ct)
    {
        const int maximumScannedEntries = 100_000;
        const int maximumPathBytes = 4_096;
        var check = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "bash", "-c",
                "set -euo pipefail; if [ -n \"$2\" ]; then git -C \"$1\" ls-tree -r -z --name-only \"$2\" -- \"$6\"; else git -C \"$1\" ls-files -z --cached -- \"$6\"; fi | python3 -c \"$3\" \"$4\" \"$5\" \"${@:7}\"",
                "codeybox-verify-private-scratchpad-paths",
                SandboxConventions.WorkDir,
                tree ?? string.Empty,
                """
                import sys

                maximum_entries = int(sys.argv[1])
                maximum_path_bytes = int(sys.argv[2])
                prefixes = tuple(prefix.encode("utf-8") for prefix in sys.argv[3:])
                buffered = bytearray()
                entry_count = 0
                first_match = None
                while True:
                    chunk = sys.stdin.buffer.read(65536)
                    if not chunk:
                        break
                    buffered.extend(chunk)
                    while True:
                        separator = buffered.find(0)
                        if separator < 0:
                            if len(buffered) > maximum_path_bytes:
                                raise ValueError("Git path exceeds reserved-path scan limit")
                            break
                        path = bytes(buffered[:separator])
                        del buffered[:separator + 1]
                        entry_count += 1
                        if entry_count > maximum_entries:
                            raise ValueError("Git reserved-path scan entry limit exceeded")
                        if len(path) > maximum_path_bytes:
                            raise ValueError("Git path exceeds reserved-path scan limit")
                        if first_match is None and path.startswith(prefixes):
                            first_match = path
                if buffered:
                    raise ValueError("Git path stream was not NUL terminated")
                if first_match is not None:
                    sys.stdout.buffer.write(first_match)
                """,
                maximumScannedEntries.ToString(System.Globalization.CultureInfo.InvariantCulture),
                maximumPathBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                AgentTurnScratchpadArchive.LegacyRepositoryDirectory,
                .. ReservedLegacyScratchpadPrefixes,
            ],
            MaxStdoutBytes = maximumPathBytes,
            MaxStderrBytes = 4096,
            KillOnOutputLimit = true,
        }, ct);
        ThrowIfExecutionUnavailable(check);
        if (!check.Success || check.StdoutLimitExceeded || check.StderrLimitExceeded)
            throw new InvalidOperationException("Failed to verify reserved agent scratchpad paths are absent from Git.");
        if (!string.IsNullOrEmpty(check.Stdout))
        {
            throw new InvalidDataException(
                tree is null
                    ? "A reserved agent scratchpad path remained in the Git index."
                    : "A reserved agent scratchpad path reached the committed Git tree.");
        }
    }

    /// <summary>
    /// Persists <paramref name="agentLogPath"/> on <paramref name="id"/> BEFORE
    /// the agent runs so a SIGTERM mid-invocation lets the shutdown teardown
    /// handler read the path out of the store. The write is guarded by the
    /// state and update stamp from the row read here so it cannot restore a
    /// stale lifecycle snapshot over concurrent recovery or cancellation.
    /// </summary>
    private Task PersistAgentLogPathAsync(WorkItemId id, string agentLogPath, CancellationToken ct) =>
        PersistAgentLogPathAsync(_store, _log, id, agentLogPath, ct);

    /// <summary>
    /// Static testable core of <see cref="PersistAgentLogPathAsync(WorkItemId,string,CancellationToken)"/>.
    /// Returns true when the guarded write succeeds, false when short-circuited
    /// (item missing, path already matches), the row changes concurrently, or a
    /// store exception is swallowed. Cancellation is propagated; every other
    /// exception is logged at warning and absorbed.
    /// </summary>
    internal static async Task<bool> PersistAgentLogPathAsync(
        IWorkItemStore store,
        Microsoft.Extensions.Logging.ILogger log,
        WorkItemId id,
        string agentLogPath,
        CancellationToken ct)
    {
        try
        {
            var fresh = await store.GetAsync(id, ct);
            if (fresh is null) return false;
            if (string.Equals(fresh.AgentLogPath, agentLogPath, StringComparison.Ordinal))
                return false;
            return await store.TryUpdateIfStateAndUpdatedAtAsync(fresh with
            {
                AgentLogPath = agentLogPath,
                UpdatedAt = DateTimeOffset.UtcNow,
            }, fresh.State, fresh.UpdatedAt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: a store hiccup here must not block the agent
            // invocation. Worst-case, the shutdown teardown handler does not
            // see AgentLogPath and the startup resume handler falls back to
            // the standard stranded-item recovery path.
            log.LogWarning(ex, "Failed to persist agent log path for {WorkItemId}", id);
            return false;
        }
    }

    /// <summary>
    /// Reason-string normaliser shared with <see cref="ReleaseService"/> via the
    /// auth-required handler. Strips CR/LF and other control characters (replaced
    /// with spaces) so plain-text log sinks cannot be spoofed by embedded
    /// newlines (CWE-117), collapses runs of whitespace, and trims. Returns an
    /// empty string for null input.
    /// </summary>
    internal static string SingleLineSummary(string? text)
        => AgentAuthRequiredHandler.SingleLineSummary(text);

}
