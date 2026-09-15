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

// PipelineRunner.AuditSelection.cs — Audit-agent selection: runner resolution, pause/quota gating, and audit-class chain fallback.
public sealed partial class PipelineRunner
{
    /// <summary>
    /// Builds an audit-agent selection with the runner bound to the selected
    /// member's configuration (e.g. a Copilot member's BYOK provider), so
    /// audit turns run against the same backend the member's work turns use.
    /// </summary>
    private static AuditAgentSelection SelectAuditAgent(IAgentRunner runner, AgentMembership? member)
        => new(BindMemberRunner(runner, member), member);

    private async Task<AuditAgentSelection> ResolveAuditAgentRunnerAsync(
        WorkItem item,
        Project project,
        string auditorName,
        AuditCapabilities required,
        IAgentRunner workRunner,
        CancellationToken ct)
    {
        AgentKind? preferredKind = project.Audit.PerAuditorAgent.TryGetValue(auditorName, out var perAuditor)
            ? perAuditor
            : project.Audit.AuditAgent;

        var classId = item.AgentClassId ?? project.DefaultAgentClass;
        // null when no class is wired OR no member carries the "audit" tag.
        // A non-null pool is the operator's opt-in that audit must stay
        // within it; a null pool preserves legacy routing.
        var auditPool = _classRouter?.GetCapabilityPool(classId, WellKnownCapabilities.Audit);
        var auditSmokeTarget = SandboxTargetResolver.ToInVmSmokeTarget(
            project,
            SandboxTargetResolver.ResolveAudit(project.NetworkProfiles.AuditAgent, required),
            item.BaselineImageRef);

        // Demote a preferred agent the operator named that the audit pool
        // (when active) rejects — the configured preference is no longer
        // routable for audit, but the routing system still finds a tagged
        // substitute rather than the operator's pipeline hard-failing.
        if (preferredKind is { } pk && auditPool is not null && !auditPool.Contains(pk))
        {
            AuditLog.AuditAgentNotAuditCapable(pk, auditorName, classId!);
            _log.LogWarning(
                "Preferred audit agent '{AuditKind}' for auditor '{Auditor}' is not tagged 'audit' in class '{ClassId}'; routing to an audit-capable class member instead",
                pk.Value, auditorName, classId);
            preferredKind = null;
        }

        if (preferredKind is null)
        {
            // No explicit override (or it was demoted by the capability gate).
            // Legacy path: no class chain configured at all → work agent is
            // the only audit candidate; gate it on pause only.
            if (_classRouter is null || classId is null)
                return WorkRunnerForAuditUnlessPaused(item, project, workRunner, auditorName);

            // Class chain wired. Two shapes:
            //   * auditPool != null — audit-capability pool active; work
            //     agent is only safe if the router says its class member is
            //     effectively audit-capable, otherwise we MUST walk that
            //     subset (falling back to workRunner would breach the AC:
            //     "a non-audit-capable agent must NEVER be selected for
            //     auditing").
            //   * auditPool == null — legacy no-tag class; every class
            //     member is audit-eligible. The work agent is in that pool
            //     (the work-phase router picked it from the same chain),
            //     so we apply the SAME smoke + quota + router-cache gates
            //     before trusting it. Without these gates a class whose
            //     audit-eligible pool is already known exhausted could
            //     still dispatch on the work runner and reach a Pass
            //     verdict if the invocation returned cleanly. Spill to
            //     SelectFromAuditClassChainAsync on rejection so the entire
            //     class is walked — which either picks a healthy peer or
            //     surfaces the proper park (AgentClassExhaustedException)
            //     / infrastructure (AuditUnavailableException) exception.
            var workMember = TryResolveSelectedMember(workRunner.Kind, project, item);
            var workIsAuditCapable = auditPool is null
                || (workMember is not null
                    && MemberHasClassCapability(classId!, workMember, WellKnownCapabilities.Audit));
            if (workIsAuditCapable && GetAgentPausedReason(workRunner.Kind) is null)
            {
                // Hard invariant: an audit-capable work member must clear
                // the SAME smoke + quota gates the preferred branch runs
                // before it can audit. The earlier shortcut here let an
                // already smoke-benched or quota-exhausted work runner be
                // returned and dispatched against an exhausted bucket; the
                // run could then return cleanly (cached output, partial
                // success, …) and produce a Pass verdict even though the
                // audit pool was meant to spill or park. Re-using the
                // preferred branch's probe-member shape keeps the gate
                // semantics in lockstep.
                var workProbeMember = workMember ?? new AgentMembership
                {
                    Agent = workRunner.Kind,
                    Billing = AgentBilling.Subscription,
                    ModelId = ResolveObservedModelId(workRunner, modelId: null),
                    QualityScore = 100,
                };
                if (IsRouterCachedExhausted(item.Id, workMember))
                {
                    _log.LogInformation(
                        "Audit-capable work agent '{WorkKind}' rejected (router cache: exhausted) for auditor '{Auditor}'; spilling to audit pool",
                        workRunner.Kind.Value, auditorName);
                }
                else
                {
                    var workAvailability = await EnsureAgentSmokeAvailableAsync(
                        workRunner.Kind, auditSmokeTarget, ct);
                    if (workAvailability.Available && !IsOperatorPaused(workAvailability))
                    {
                        var (workOk, workReason) = await EvaluateAuditCandidateQuotaAsync(
                            item.Id, workRunner.Kind, workProbeMember, ct);
                        if (workOk)
                            return SelectAuditAgent(workRunner, workMember);
                        _log.LogInformation(
                            "Audit-capable work agent '{WorkKind}' rejected ({Reason}) for auditor '{Auditor}'; spilling to audit pool",
                            workRunner.Kind.Value, workReason, auditorName);
                    }
                    else
                    {
                        _log.LogInformation(
                            "Audit-capable work agent '{WorkKind}' rejected (smoke gate: {Reason}) for auditor '{Auditor}'; spilling to audit pool",
                            workRunner.Kind.Value, workAvailability.Reason ?? "unavailable", auditorName);
                    }
                }
            }
            return await SelectFromAuditClassChainAsync(
                item, project, auditorName, classId!,
                requireAuditCapability: auditPool is not null,
                auditSmokeTarget,
                ct);
        }

        if (!_agents.TryGet(preferredKind.Value, out var preferredRunner))
        {
            _log.LogWarning(
                "Audit agent '{AuditKind}' is not registered for auditor '{Auditor}'; falling back to work agent '{WorkKind}'",
                preferredKind.Value.Value, auditorName, workRunner.Kind.Value);
            return await FallbackToWorkRunnerOrSpillToAuditPoolAsync(
                item, project, workRunner, auditorName, classId, auditPool, auditSmokeTarget, ct);
        }

        // Resolve the configured class member to gate the preferred audit
        // fast path against. The legacy FindMember(modelId:null) lookup only
        // matched members whose ModelId was explicitly null/empty, so a
        // class configured with a real ModelId-pinned member fell through
        // to a synthetic AgentMembership below. The router-cache and quota
        // gates then ran against a (kind, runner-default model) bucket
        // that no probe / cache entry actually tracks — so an exhausted
        // real member could still slip through and reach a Pass verdict.
        // FindPreferredAuditMember walks every member of preferredKind in
        // the class, prefers an instance/model match, and (when the audit
        // pool is active) restricts to audit-capable members so the gates
        // run against the real bucket the spill / park logic depends on.
        var preferredMember = classId is not null
            ? FindPreferredAuditMember(
                classId,
                preferredKind.Value,
                preferredModelId: preferredKind.Value == item.Agent ? item.ModelId : null,
                instanceId: preferredKind.Value == item.Agent ? item.AgentInstanceId : null,
                requireAuditCapability: auditPool is not null)
            : null;
        var preferredCred = preferredMember is not null
            ? await ResolveAgentCredentialAsync(preferredMember, project, ct)
            : await ResolveAgentCredentialAsync(preferredKind.Value, project, item, ct);
        if (preferredCred is null)
        {
            _log.LogWarning(
                "No credentials found for audit agent '{AuditKind}' (auditor '{Auditor}'); falling back to work agent '{WorkKind}'",
                preferredKind.Value.Value, auditorName, workRunner.Kind.Value);
            return await FallbackToWorkRunnerOrSpillToAuditPoolAsync(
                item, project, workRunner, auditorName, classId, auditPool, auditSmokeTarget, ct);
        }

        var preferredProbeMember = preferredMember ?? new AgentMembership
        {
            Agent = preferredKind.Value,
            Billing = AgentBilling.Subscription,
            ModelId = ResolveObservedModelId(preferredRunner, modelId: null),
            QualityScore = 100,
        };

        // Gate the preferred agent on router-cached exhaustion + in-VM smoke +
        // availability exactly as the work-phase router
        // (AgentClassRouter.ResolveAsync) does, BEFORE trusting it. An agent
        // benched by in-VM smoke (exit 127 / auth drift), the fast-fail
        // breaker, or the router's in-process exhaustion cache must not run
        // audit even when named explicitly — the class-chain walk below
        // already gates its members via OrderedFallbackCandidatesAsync, so
        // without this the preferred fast path was the one hole left open.
        // The cache check matters because the live smoke + quota probe can
        // currently look healthy for a member that was just marked exhausted
        // by a mid-iteration spill; returning it here would re-dispatch
        // against the same bucket the spill was meant to avoid.
        AgentAvailability? preferredAvailability = null;
        var preferredAvailable = false;
        var preferredOk = false;
        string? preferredReason = null;
        string? preferredPauseReason = null;
        var preferredCachedExhausted = IsRouterCachedExhausted(item.Id, preferredMember);

        if (preferredCachedExhausted)
        {
            preferredReason = "router cache: exhausted";
            _log.LogInformation(
                "Audit agent '{AuditKind}' rejected ({Reason}) for auditor '{Auditor}'",
                preferredKind.Value.Value, preferredReason, auditorName);
        }
        else
        {
            preferredAvailability = await EnsureAgentSmokeAvailableAsync(
                preferredKind.Value, auditSmokeTarget, ct);
            if (IsOperatorPaused(preferredAvailability))
            {
                preferredPauseReason = preferredAvailability.Reason ?? AgentDispatchAvailability.PausedReasonPrefix;
                _log.LogInformation(
                    "Audit agent '{AuditKind}' rejected ({Reason}) for auditor '{Auditor}'",
                    preferredKind.Value.Value, preferredPauseReason, auditorName);
            }
            else
            {
                preferredAvailable = preferredAvailability.Available;

                (preferredOk, preferredReason) = await EvaluateAuditCandidateQuotaAsync(
                    item.Id, preferredKind.Value, preferredProbeMember, ct);
            }
        }
        if (!preferredCachedExhausted && preferredPauseReason is null && preferredAvailable && preferredOk)
            return SelectAuditAgent(preferredRunner, preferredMember);

        if (!preferredCachedExhausted && preferredPauseReason is null)
        {
            var rejectReason = preferredAvailable
                ? preferredReason
                : $"smoke gate: {(preferredAvailability?.Reason ?? "unavailable")}";
            _log.LogInformation(
                "Audit agent '{AuditKind}' rejected ({Reason}) for auditor '{Auditor}'",
                preferredKind.Value.Value, rejectReason, auditorName);
        }

        // No class chain to walk — preserve legacy fall-through to the work
        // agent. With no class configured, the operator hasn't opted into
        // class-aware audit routing, so the workRunner is the best we can do.
        if (_classRouter is null || classId is null)
        {
            AuditLog.QuotaAuditFallthrough(preferredKind.Value, workRunner.Kind, auditorName);
            return WorkRunnerForAuditUnlessPaused(item, project, workRunner, auditorName);
        }

        // Walk the work item's class chain for an unexhausted candidate.
        // quotaRejectedCount counts candidates rejected specifically for quota
        // (including the preferred agent above) — this is what the
        // LlmAuditorParkedQuota event reports. Candidates skipped for other
        // reasons (missing runner / credentials) are intentionally excluded.
        // A router-cache-exhausted preferred member is counted here because
        // an exhaustion entry has the same eventual reset shape as a live
        // probe rejection — quota returning is what clears it.
        var quotaRejectedCount = preferredCachedExhausted
            ? 1
            : preferredPauseReason is null && preferredAvailable && !preferredOk
                ? 1
                : 0;
        // Track configuration-shaped rejections so the final throw can
        // distinguish "quota exhausted" (park for reset) from "no candidate
        // was ever dispatchable" (infrastructure failure). The preferred
        // path's smoke-unavailable count starts at 1 when smoke rejected
        // the named agent — without that the all-smoke-rejected pool would
        // surface as a 0-candidate "quota exhausted" message, which both
        // misleads operators and parks the item behind QuotaRetryScheduler
        // even though quota returning will not make a smoke-benched CLI
        // usable.
        var smokeRejectedCount = !preferredCachedExhausted
            && preferredPauseReason is null
            && !preferredAvailable
            ? 1
            : 0;
        var missingRunnerCount = 0;
        var missingCredentialsCount = 0;
        foreach (var member in await _classRouter.OrderedFallbackCandidatesAsync(
            item, project, ct, auditSmokeTarget, requireQuota: false))
        {
            if (preferredMember is not null
                && SameMemberBucket(member, preferredMember))
                continue;   // already counted above
            // Audit-capability gate: when the pool is active, restrict the
            // walk to effectively audit-capable members so a non-audit-capable
            // member is NEVER picked for auditing — even when it is the only
            // one with quota.
            // Mid-iteration fallback in InvokeAgentWithQuotaFallbackAsync
            // enforces the same gate via requireAuditCapability.
            if (auditPool is not null
                && !MemberHasClassCapability(classId!, member, WellKnownCapabilities.Audit))
            {
                _log.LogDebug(
                    "Class '{ClassId}' member '{Member}' is not audit-capable; skipping for auditor '{Auditor}'",
                    classId, member.Agent.Value, auditorName);
                continue;
            }
            if (!_agents.TryGet(member.Agent, out var memberRunner))
            {
                _log.LogWarning(
                    "Class '{ClassId}' member '{Member}' has no registered runner for auditor '{Auditor}'; skipping",
                    classId, member.Agent.Value, auditorName);
                missingRunnerCount++;
                continue;
            }
            var memberCred = await ResolveAgentCredentialAsync(member, project, ct);
            if (memberCred is null)
            {
                _log.LogWarning(
                    "Class '{ClassId}' member '{Member}' has no credentials for auditor '{Auditor}'; skipping",
                    classId, member.Agent.Value, auditorName);
                missingCredentialsCount++;
                continue;
            }
            var (memberOk, memberReason) = await EvaluateAuditCandidateQuotaAsync(item.Id, member.Agent, member, ct);
            if (!memberOk)
            {
                _log.LogInformation(
                    "Class '{ClassId}' member '{Member}' rejected ({Reason}) for auditor '{Auditor}'",
                    classId, member.Agent.Value, memberReason, auditorName);
                quotaRejectedCount++;
                continue;
            }
            _log.LogInformation(
                "Audit agent '{AuditKind}' exhausted; routing auditor '{Auditor}' to class member '{Member}'",
                preferredKind.Value.Value, auditorName, member.Agent.Value);
            // Emit the fallthrough audit-log only once the real fallback agent
            // is picked. Earlier this fired unconditionally before the chain
            // walk, naming workRunner — incorrect when the chain picks a
            // different member.
            AuditLog.QuotaAuditFallthrough(preferredKind.Value, member.Agent, auditorName);
            return SelectAuditAgent(memberRunner, member);
        }

        // The work agent is one of the class members (the work-phase router
        // picked it from this same chain) so if every class member is
        // exhausted, falling back to workRunner doesn't help. Park the work
        // item in WaitingForQuotaReset instead — silently skipping the
        // auditor would let a Pass verdict emerge with one fewer review
        // than configured, which violates the per-auditor independent-gate
        // contract. QuotaRetryScheduler picks the same iteration back up
        // when the audit pool's quota returns.
        var cachedExhaustedCount = _classRouter!.CountEligibleExhaustedClassMembersWithCapability(
            item, project, auditPool is not null ? WellKnownCapabilities.Audit : null);
        if (preferredCachedExhausted && cachedExhaustedCount > 0)
            cachedExhaustedCount--;
        var totalQuotaRejected = quotaRejectedCount + cachedExhaustedCount;

        if (preferredPauseReason is not null && totalQuotaRejected == 0)
            throw new AgentPausedException("audit", preferredKind.Value, preferredPauseReason);

        if (totalQuotaRejected > 0)
        {
            // Staleness detection: a candidate the router merely
            // evaluation-rejected (stale records, budgets) while its live
            // probe reads healthy must not park the item; router-cache
            // marks (fresh rejections) still park per the cache-wins rule
            // enforced inside the check.
            var hasHealthyCandidate = await HasAnyQuotaCandidateHealthyProbeAsync(
                item, project, classId, ct, preferredProbeMember,
                requireCapability: auditPool is not null ? WellKnownCapabilities.Audit : null);
            if (hasHealthyCandidate)
            {
                _log.LogWarning(
                    "LLM auditor '{Auditor}' candidate(s) of class '{ClassId}' were flagged as quota-rejected, but probe reports healthy quota; treating as transient retry instead of WaitingForQuotaReset",
                    auditorName, classId);
                throw new TerminalTransientNetworkError(
                    preferredKind ?? item.Agent ?? workRunner.Kind,
                    "audit",
                    new AgentFailureClassification(AgentFailureKind.TransientNetwork, Reason: "LLM auditor candidate flagged exhausted but probe is healthy"),
                    $"LLM auditor '{auditorName}' cannot run: candidate agent(s) flagged exhausted but probe is healthy");
            }

            var parkMessage =
                $"LLM auditor '{auditorName}' cannot run: all {totalQuotaRejected} candidate agent(s) of class '{classId}' quota-exhausted";
            AuditLog.LlmAuditorParkedQuota(item.Id, auditorName, totalQuotaRejected);
            _log.LogWarning(parkMessage);
            throw new AgentClassExhaustedException(
                classId,
                phase: "audit",
                memberCount: totalQuotaRejected,
                earliestResetAt: null,
                message: parkMessage);
        }

        // Zero quota rejections at this point means every candidate was
        // filtered out for a non-quota reason: smoke gate, missing runner,
        // or missing credentials. Reporting this as "quota exhausted" would
        // both mislead operators investigating the skip AND park the item
        // behind QuotaRetryScheduler even though quota returning will not
        // make a smoke-benched CLI / unregistered runner / missing
        // credential usable. Surface it as a transient infrastructure
        // failure (AuditUnavailableException) so the RunAsync catch routes
        // it to failureKind="infrastructure" — distinct from both
        // "quota" (park-and-retry) and "code-quality finding"
        // (false-AuditFailed regression from 1aa5a13f).
        var infraMessage =
            $"LLM auditor '{auditorName}' cannot run: no candidate agent of class '{classId}' is dispatchable " +
            $"(smoke-rejected={smokeRejectedCount}, missing runner={missingRunnerCount}, missing credentials={missingCredentialsCount})";
        _log.LogWarning(infraMessage);
        throw new AuditUnavailableException(infraMessage);
    }

    private AuditAgentSelection WorkRunnerForAuditUnlessPaused(
        WorkItem item,
        Project project,
        IAgentRunner workRunner,
        string auditorName)
    {
        var pauseReason = GetAgentPausedReason(workRunner.Kind);
        if (pauseReason is null)
            return SelectAuditAgent(workRunner, TryResolveSelectedMember(workRunner.Kind, project, item));

        _log.LogWarning(
            "LLM auditor '{Auditor}' waiting: work agent '{Agent}' is {Reason}",
            auditorName,
            workRunner.Kind.Value,
            pauseReason);
        throw new AgentPausedException("audit", workRunner.Kind, pauseReason);
    }

    /// <summary>
    /// Fallback used when a configured preferred audit agent is unregistered or
    /// has no credentials. Applies the SAME pause + smoke + quota gates the
    /// no-preferred-audit-agent branch applies to the work runner before
    /// trusting it, then spills to the gated audit pool walk on any rejection
    /// — which either picks a healthy audit-capable peer or surfaces the
    /// proper park (AgentClassExhaustedException → WaitingForQuotaReset) /
    /// infrastructure (AuditUnavailableException) exception. Without these
    /// gates an audit-capable work runner that is already smoke-rejected or
    /// quota-exhausted could be dispatched against an effectively-skipped
    /// review and a Pass verdict could emerge with one fewer auditor than
    /// configured — the silent-skip hole this resolver exists to defend.
    /// When no audit pool / class chain is configured, preserves the legacy
    /// pause-only fall-through (the operator hasn't opted into class-aware
    /// audit routing so the work runner is the only configured candidate).
    /// </summary>
    private async Task<AuditAgentSelection> FallbackToWorkRunnerOrSpillToAuditPoolAsync(
        WorkItem item,
        Project project,
        IAgentRunner workRunner,
        string auditorName,
        string? classId,
        IReadOnlySet<AgentKind>? auditPool,
        InVmSmokeSandboxTarget auditSmokeTarget,
        CancellationToken ct)
    {
        // No class chain wired: legacy pause-only fallback — the work runner
        // is the operator's only configured audit candidate.
        if (classId is null || _classRouter is null)
            return WorkRunnerForAuditUnlessPaused(item, project, workRunner, auditorName);

        // Audit-capability pool active and the work agent isn't in it: the
        // work agent must NEVER audit. Walk the effective audit-capable subset
        // (fully gated).
        var workMember = TryResolveSelectedMember(workRunner.Kind, project, item);
        var workIsAuditCapable = auditPool is null
            || (workMember is not null
                && MemberHasClassCapability(classId, workMember, WellKnownCapabilities.Audit));
        if (!workIsAuditCapable)
            return await SelectFromAuditCapablePoolAsync(
                item, project, auditorName, classId, auditSmokeTarget, ct);

        // Either the concrete work member is effectively audit-capable, or no
        // pool is active (legacy no-tag class — every class member is
        // audit-eligible).
        // Mirror the no-preferred branch's pause + smoke + quota + router-cache
        // gating before trusting the work runner; on any rejection spill to the
        // class-chain walk (which either picks a healthy peer or throws the
        // proper park / infrastructure exception).
        // The legacy no-tag class is intentionally gated identically — without
        // it a class whose audit-eligible pool is already known exhausted
        // could still slip a Pass verdict in on the work runner.
        if (GetAgentPausedReason(workRunner.Kind) is null)
        {
            var workProbeMember = workMember ?? new AgentMembership
            {
                Agent = workRunner.Kind,
                Billing = AgentBilling.Subscription,
                ModelId = ResolveObservedModelId(workRunner, modelId: null),
                QualityScore = 100,
            };
            if (IsRouterCachedExhausted(item.Id, workMember))
            {
                _log.LogInformation(
                    "Audit-capable work agent '{WorkKind}' rejected (router cache: exhausted) for auditor '{Auditor}'; spilling to audit pool",
                    workRunner.Kind.Value, auditorName);
            }
            else
            {
                var workAvailability = await EnsureAgentSmokeAvailableAsync(
                    workRunner.Kind, auditSmokeTarget, ct);
                if (workAvailability.Available && !IsOperatorPaused(workAvailability))
                {
                    var (workOk, workReason) = await EvaluateAuditCandidateQuotaAsync(
                        item.Id, workRunner.Kind, workProbeMember, ct);
                    if (workOk)
                        return SelectAuditAgent(workRunner, workMember);
                    _log.LogInformation(
                        "Audit-capable work agent '{WorkKind}' rejected ({Reason}) for auditor '{Auditor}'; spilling to audit pool",
                        workRunner.Kind.Value, workReason, auditorName);
                }
                else
                {
                    _log.LogInformation(
                        "Audit-capable work agent '{WorkKind}' rejected (smoke gate: {Reason}) for auditor '{Auditor}'; spilling to audit pool",
                        workRunner.Kind.Value, workAvailability.Reason ?? "unavailable", auditorName);
                }
            }
        }
        return await SelectFromAuditClassChainAsync(
            item, project, auditorName, classId,
            requireAuditCapability: auditPool is not null,
            auditSmokeTarget,
            ct);
    }

    private string? GetAgentPausedReason(AgentKind agent)
    {
        var availability = _dispatchAvailability?.GetAvailability(agent);
        return IsOperatorPaused(availability)
            ? availability!.Reason ?? AgentDispatchAvailability.PausedReasonPrefix
            : null;
    }

    private string? GetAgentPausedReason(AgentMembership member)
    {
        var availability = _dispatchAvailability?.GetAvailability(member);
        return IsOperatorPaused(availability)
            ? availability!.Reason ?? AgentDispatchAvailability.PausedReasonPrefix
            : null;
    }

    private bool MemberHasClassCapability(string classId, AgentMembership member, string capability) =>
        _classRouter?.MemberHasCapability(classId, member, capability)
        ?? member.HasCapability(capability);

    /// <summary>
    /// Walks the routed class chain looking for the first eligible member that
    /// is registered, credentialed, and quota OK. Smoke availability is handled
    /// upstream by <see cref="AgentClassRouter.OrderedFallbackCandidatesAsync"/>,
    /// which yields smoke-checked members for this path; this method applies
    /// the audit quota policy itself. <paramref name="auditSmokeTarget"/> is
    /// threaded into that smoke gate so candidates are checked against the
    /// audit sandbox profile, not the work profile — without this, a member
    /// benched only for the audit profile could pass the work-profile smoke
    /// check and be re-selected after the audit dispatch gate already
    /// rejected it (the "unrunnable auditor must not be a Pass" invariant).
    /// <para>
    /// When <paramref name="requireAuditCapability"/> is true, only members
    /// with effective <see cref="WellKnownCapabilities.Audit"/> capability are
    /// considered — the audit-capability pool path. When false, every class
    /// member is eligible — the legacy no-tag-class path where the entire class
    /// is the audit
    /// pool. Either way, falling back to the work agent here would breach the
    /// hard invariant ("a non-audit-capable agent must NEVER be selected for
    /// auditing" / "the gate must apply to every class audit pool").
    /// </para>
    /// Throws <see cref="AgentClassExhaustedException"/> when at least one
    /// eligible candidate was quota-rejected (the work item then parks
    /// in WaitingForQuotaReset rather than passing audit with an incomplete
    /// review set). Throws <see cref="AuditUnavailableException"/> on
    /// configuration-shaped absence (no eligible members at all, every
    /// candidate missing a registered runner or credentials) — surfacing the
    /// misconfig as a transient-execution failure that the caller routes via
    /// the existing RunAsync catch to failureKind="infrastructure". Never
    /// returns a null selection: a configured auditor that has no usable
    /// candidate must surface as an explicit failure, not a silent skip
    /// against which a Pass verdict could still be computed.
    /// </summary>
    private async Task<AuditAgentSelection> SelectFromAuditClassChainAsync(
        WorkItem item,
        Project project,
        string auditorName,
        string classId,
        bool requireAuditCapability,
        InVmSmokeSandboxTarget auditSmokeTarget,
        CancellationToken ct)
    {
        if (_classRouter is null)
            throw new AuditUnavailableException(
                $"LLM auditor '{auditorName}' cannot run: no class router is configured but class '{classId}' is required for audit routing");
        var poolDescriptor = requireAuditCapability ? "audit-capable member" : "member";
        var quotaRejectedCount = 0;
        var missingRunnerCount = 0;
        var missingCredentialsCount = 0;
        // Thread the resolved audit smoke target through the router so its
        // in-VM smoke gate runs against the audit sandbox profile, not the
        // work profile. Without this, a member benched only for the audit
        // sandbox profile could pass the work-profile smoke check and be
        // re-selected after the audit dispatch gate already rejected it —
        // contradicting the "unrunnable auditor must not be treated as a
        // valid audit verdict" invariant. Matches the preferred-agent
        // fallback path's gating.
        foreach (var member in await _classRouter.OrderedFallbackCandidatesAsync(
            item, project, ct, smokeTarget: auditSmokeTarget, requireQuota: false))
        {
            if (requireAuditCapability
                && !MemberHasClassCapability(classId, member, WellKnownCapabilities.Audit))
                continue;
            if (!_agents.TryGet(member.Agent, out var memberRunner))
            {
                _log.LogWarning(
                    "Class '{ClassId}' member '{Member}' has no registered runner for auditor '{Auditor}'; skipping",
                    classId, member.Agent.Value, auditorName);
                missingRunnerCount++;
                continue;
            }
            var memberCred = await ResolveAgentCredentialAsync(member, project, ct);
            if (memberCred is null)
            {
                _log.LogWarning(
                    "Class '{ClassId}' member '{Member}' has no credentials for auditor '{Auditor}'; skipping",
                    classId, member.Agent.Value, auditorName);
                missingCredentialsCount++;
                continue;
            }
            var (memberOk, memberReason) = await EvaluateAuditCandidateQuotaAsync(item.Id, member.Agent, member, ct);
            if (!memberOk)
            {
                _log.LogInformation(
                    "Class '{ClassId}' member '{Member}' rejected ({Reason}) for auditor '{Auditor}'",
                    classId, member.Agent.Value, memberReason, auditorName);
                quotaRejectedCount++;
                continue;
            }
            _log.LogInformation(
                "Routing auditor '{Auditor}' to class member '{Member}'",
                auditorName, member.Agent.Value);
            return SelectAuditAgent(memberRunner, member);
        }
        // LlmAuditorParkedQuota names "quota" — only emit when at least one
        // candidate was actually quota-rejected. When the pool is empty or
        // every member is filtered for missing runner/credentials, the cause
        // is misconfiguration, not a quota crunch; surfacing it as quota would
        // misdirect operators investigating the skip.
        if (quotaRejectedCount == 0
            && await TryGetPausedAuditPoolMemberAsync(item, project, classId, requireAuditCapability, ct) is { } paused)
            throw new AgentPausedException("audit", paused.Agent, paused.Reason);

        // OrderedFallbackCandidatesAsync filters out members already marked
        // exhausted in the router's in-process cache before returning, so when
        // every eligible member of the class is cached-exhausted the loop
        // sees zero candidates and quotaRejectedCount stays at 0. That state
        // is still quota exhaustion — surfacing it as AuditUnavailableException
        // would route the item to failureKind="infrastructure" instead of
        // parking in WaitingForQuotaReset and re-introduce a silent-skip path
        // (the hard invariant being defended is: a Pass verdict must never
        // emerge while a configured auditor's spill-to-peer pool was entirely
        // quota-blocked). Reclassify here as exhausted so the existing park
        // path runs. The router-owned helper applies the SAME item-specific
        // eligibility filter OrderedFallbackCandidatesAsync did (MinModelScore
        // + RequiredCapabilities) so a member that could never have been
        // picked for this item cannot inflate the count and park work that
        // should have surfaced as infrastructure.
        var cachedExhaustedCount = _classRouter!.CountEligibleExhaustedClassMembersWithCapability(
            item, project, requireAuditCapability ? WellKnownCapabilities.Audit : null);
        var totalExhausted = quotaRejectedCount + cachedExhaustedCount;
        if (totalExhausted > 0)
        {
            var parkMessage =
                $"LLM auditor '{auditorName}' cannot run: no {poolDescriptor} of class '{classId}' is available ({totalExhausted} quota-rejected)";
            AuditLog.LlmAuditorParkedQuota(item.Id, auditorName, totalExhausted);
            _log.LogWarning(parkMessage);
            // Park the work item rather than silently skipping the auditor:
            // a Pass verdict must never emerge while a configured auditor
            // could not run because its entire spill-to-peer pool was
            // quota-exhausted.
            throw new AgentClassExhaustedException(
                classId,
                phase: "audit",
                memberCount: totalExhausted,
                earliestResetAt: null,
                message: parkMessage);
        }

        // No usable candidate at all: every eligible member of the pool
        // is missing a registered runner, missing credentials, or the pool is
        // empty. This is a configuration / operator-environment failure, not
        // a quota crunch — surface it as a transient infrastructure failure
        // (AuditUnavailableException) so the audit phase cannot resolve to a
        // Pass verdict with an incomplete review set. The RunAsync catch
        // (line 1542) routes it to failureKind="infrastructure" rather than
        // a code-quality finding (would re-introduce the 1aa5a13f false-
        // AuditFailed regression) or a silent skip (the bug this fix targets).
        var infraMessage =
            $"LLM auditor '{auditorName}' cannot run: no {poolDescriptor} of class '{classId}' is dispatchable " +
            $"(missing runner={missingRunnerCount}, missing credentials={missingCredentialsCount})";
        _log.LogWarning(infraMessage);
        throw new AuditUnavailableException(infraMessage);
    }

    private Task<AuditAgentSelection> SelectFromAuditCapablePoolAsync(
        WorkItem item,
        Project project,
        string auditorName,
        string classId,
        InVmSmokeSandboxTarget auditSmokeTarget,
        CancellationToken ct)
        => SelectFromAuditClassChainAsync(
            item, project, auditorName, classId,
            requireAuditCapability: true, auditSmokeTarget, ct);

    /// <summary>
    /// Returns true when the router's in-process exhaustion cache says
    /// <paramref name="member"/> is currently exhausted. The audit fast paths
    /// (preferred-runner and audit-capable-work-runner) must consult this
    /// before trusting a live smoke + quota probe — without it a member that
    /// was marked exhausted via <see cref="AgentClassRouter.MarkExhausted"/>
    /// (e.g. mid-iteration spill) could be re-selected immediately if the
    /// live probe currently looks healthy, defeating the spill and reaching
    /// a Pass verdict on the same exhausted bucket. Returns false when the
    /// router is unwired or the member is synthetic (no cache entry to
    /// consult); the audit-pool walk's
    /// <see cref="AgentClassRouter.OrderedFallbackCandidatesAsync"/> already
    /// applies the same gate for its candidates.
    /// </summary>
    private bool IsRouterCachedExhausted(WorkItemId itemId, AgentMembership? member)
    {
        if (_classRouter is null || member is null)
            return false;
        if (_classRouter.HasQuotaRetryAdmission(itemId, member, _opts.TimeProvider.GetUtcNow()))
            return false;
        return _classRouter.IsExhausted(member, _opts.TimeProvider.GetUtcNow());
    }

    private async Task<bool> IsAgentPausedAsync(AgentKind agent, CancellationToken ct)
    {
        if (_agentPauses is null)
            return false;
        try
        {
            var state = await _agentPauses.GetAgentStateAsync(agent, ct).ConfigureAwait(false);
            return state?.Paused == true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Pause-state check failed for agent {Agent}; treating as unpaused", agent.Value);
            return false;
        }
    }

    private static bool SameMemberBucket(AgentMembership left, AgentMembership right) =>
        string.Equals(left.RouteKey, right.RouteKey, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.ModelId ?? string.Empty, right.ModelId ?? string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// Resolves the configured class member to gate the preferred audit fast
    /// path against. Walks every member of <paramref name="preferredKind"/> in
    /// <paramref name="classId"/>, scoring instance-id and model-id matches
    /// (most-specific wins) and — when <paramref name="requireAuditCapability"/>
    /// is true — restricting to members with effective
    /// <see cref="WellKnownCapabilities.Audit"/> capability. Unlike
    /// <see cref="AgentClassRouter.FindMember"/>, this never demands an exact
    /// model-id equality, so a class configured with a ModelId-pinned member
    /// still resolves to the real member instead of falling through to a
    /// synthetic <see cref="AgentMembership"/> whose (kind, runner-default
    /// model) bucket no probe / cache entry tracks. Returns null only when
    /// no class member of <paramref name="preferredKind"/> qualifies — the
    /// caller then spills to the class-chain walk rather than dispatching
    /// against an ungated raw runner.
    /// </summary>
    private AgentMembership? FindPreferredAuditMember(
        string classId,
        AgentKind preferredKind,
        string? preferredModelId,
        string? instanceId,
        bool requireAuditCapability)
    {
        if (_classRouter is null)
            return null;
        var members = _classRouter.GetClassMembers(classId);
        AgentMembership? best = null;
        var bestScore = -1;
        foreach (var member in members)
        {
            if (member.Agent != preferredKind)
                continue;
            if (requireAuditCapability
                && !MemberHasClassCapability(classId, member, WellKnownCapabilities.Audit))
                continue;

            var score = 0;
            if (!string.IsNullOrWhiteSpace(instanceId)
                && AgentInstanceIds.Matches(member, instanceId))
                score += 2;
            if (preferredModelId is not null
                && string.Equals(member.ModelId ?? string.Empty, preferredModelId, StringComparison.Ordinal))
                score += 1;
            if (score > bestScore)
            {
                best = member;
                bestScore = score;
            }
        }
        return best;
    }

    private async Task<(AgentKind Agent, string Reason)?> TryGetPausedAuditPoolMemberAsync(
        WorkItem item,
        Project project,
        string classId,
        bool requireAuditCapability,
        CancellationToken ct)
    {
        var members = _classRouter?.GetClassMembers(classId);
        if (members is null || members.Count == 0)
            return null;

        foreach (var member in members)
        {
            if (_classRouter is not null
                && !_classRouter.IsEligibleClassMemberWithCapability(
                    item,
                    project,
                    member,
                    requireAuditCapability ? WellKnownCapabilities.Audit : null))
                continue;

            if (requireAuditCapability
                && !MemberHasClassCapability(classId, member, WellKnownCapabilities.Audit))
                continue;

            if (!_agents.TryGet(member.Agent, out _))
                continue;

            var cred = await ResolveAgentCredentialAsync(member, project, ct);
            if (cred is null)
                continue;

            var reason = GetAgentPausedReason(member);
            if (reason is null)
                continue;

            return (member.Agent, reason);
        }

        return null;
    }

    /// <summary>
    /// Reads the operator's local spend budget for (<paramref name="kind"/>,
    /// <paramref name="modelId"/>) and classifies it for the mid-iteration fallback
    /// gates. Returns the budget snapshot when configured and a <c>FailedClosed</c>
    /// flag set when the provider itself threw — that means the operator's spend
    /// cap cannot be verified, so callers must gate dispatch rather than silently
    /// drop the constraint. Shared by the audit-candidate gate and the work-phase
    /// fallback so both honour MIN(probe, local budget).
    /// <see cref="OperationCanceledException"/> propagates (shutdown/abort is not an
    /// accounting outage).
    /// </summary>
    private async Task<(AgentQuotaSnapshot? Budget, bool FailedClosed)> ReadCandidateBudgetAsync(
        AgentKind kind, string? modelId, CancellationToken ct)
    {
        if (_budgetProvider is null) return (null, false);
        try
        {
            var budget = await _budgetProvider.GetBudgetSnapshotAsync(kind, modelId, ct);
            return (budget, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Provider failure (not a configured-but-degraded budget, which is
            // already reported as 0%) means we cannot verify the spend cap.
            _log.LogWarning(ex,
                "Budget gate for {Agent}/{Model} threw; failing closed",
                kind.Value, modelId ?? "(default)");
            return (null, true);
        }
    }

    /// <summary>
    /// Returns <c>(true, reason)</c> when the candidate passes both the
    /// observed-failure breaker and the live quota probe (reason is a short
    /// human-readable description like "available (80.0%)" or
    /// "quota unknown; fail-open"); otherwise returns <c>(false, reason)</c>
    /// describing which gate rejected the candidate. Mirrors the gating logic
    /// in <see cref="AgentClassRouter"/> so the work and audit phases agree
    /// on what counts as "available".
    /// </summary>
    private async Task<(bool Allowed, string Reason)> EvaluateAuditCandidateQuotaAsync(
        WorkItemId itemId,
        AgentKind kind,
        AgentMembership member,
        CancellationToken ct)
    {
        var nowUtc = _opts.TimeProvider.GetUtcNow();
        var hasQuotaRetryAdmission = _classRouter?.HasQuotaRetryAdmission(
            itemId,
            member,
            nowUtc) == true;
        if (_quotaFailures is not null
            && !hasQuotaRetryAdmission
            && await _quotaFailures.HasRecentAsync(
                kind, member.ModelId,
                _auditQuotaOptions.ObservedFailureWindow,
                nowUtc, ct))
        {
            _quotaAvailabilityPublisher?.RecordQuotaUsability(
                member,
                isUsable: false,
                publishRecoverySignal: true);
            return (false, "recent observed quota failure");
        }

        // Local operator-budget snapshot. Acceptance criterion: quota routing
        // takes MIN(real probe, local budget), so the audit fallthrough must not
        // dispatch an agent whose operator spend budget is exhausted just because
        // the subscription probe still has headroom. budgetPct < 0 means "no
        // budget configured" (the budget gate is then absent).
        var (budget, budgetFailedClosed) = await ReadCandidateBudgetAsync(kind, member.ModelId, ct);
        if (budgetFailedClosed)
            return (false, "budget provider error (fail-closed)");
        var budgetPct = budget?.AvailablePct ?? -1;

        var resolution = ResolveQuotaProbe(member);
        if (resolution is not { Probe: { } probe, Conflict: null })
        {
            if (resolution.Conflict is not null)
            {
                // Equally specific probes claim this member (already logged at
                // Error by the catalog): fail closed rather than reading from an
                // arbitrary winner or falling through to the probe-less path.
                return (false, "conflicting quota probes (fail-closed)");
            }

            // No real probe. A healthy configured budget supplies a concrete
            // available percentage; otherwise preserve the prior probe-less
            // "allow" semantics.
            if (budgetPct < 0)
                return (true, "no probe registered");

            var budgetQuota = new EffectiveQuota(budgetPct, null, null, budget?.Windows);
            return EvaluateAuditQuotaGate(member, budgetQuota, nowUtc, budgetOnly: true);
        }

        EffectiveQuota probeQuota;
        try
        {
            var snapshot = await probe.GetAvailabilityAsync(member, ct);
            probeQuota = QuotaGatePolicy.ResolveMemberQuota(snapshot, member);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Probe threw (transient API error). Treat it as unknown (-1) and fall
            // through to the MIN(real probe, local budget) logic below rather than
            // short-circuiting: a healthy configured budget must still gate, and an
            // exhausted one was already rejected above. Bypassing the budget here
            // would fail-open the operator spend cap on a probe blip.
            _log.LogDebug(ex, "Audit quota probe for {Agent} threw; treating as unknown", kind.Value);
            probeQuota = new EffectiveQuota(-1, null, null);
        }

        // MIN(real probe, local budget): the budget stands alone when the probe is
        // unknown (-1), and the probe stands alone when no budget is configured.
        var combinedPct = probeQuota.AvailablePct < 0
            ? budgetPct
            : budgetPct < 0
                ? probeQuota.AvailablePct
                : Math.Min(probeQuota.AvailablePct, budgetPct);

        var combinedQuota = probeQuota with
        {
            AvailablePct = combinedPct,
        };

        var decision = EvaluateAuditQuotaGate(member, combinedQuota, nowUtc, budgetOnly: false);
        var providerGate = _auditQuotaGatePolicy.Evaluate(member, probeQuota, nowUtc);
        var denialIsBudgetOnly = !decision.Allowed
            && providerGate.Allow
            && budgetPct >= 0
            && combinedPct >= 0
            && (probeQuota.AvailablePct < 0 || budgetPct <= probeQuota.AvailablePct);
        if (!denialIsBudgetOnly)
        {
            _quotaAvailabilityPublisher?.RecordQuotaUsability(
                member,
                isUsable: decision.Allowed,
                publishRecoverySignal: true);
        }

        return decision;
    }

    private (bool Allowed, string Reason) EvaluateAuditQuotaGate(
        AgentMembership member,
        EffectiveQuota quota,
        DateTimeOffset nowUtc,
        bool budgetOnly)
    {
        var combinedPct = quota.AvailablePct;
        var gate = _auditQuotaGatePolicy.Evaluate(member, quota, nowUtc);
        if (gate.Allow)
        {
            return budgetOnly
                ? (true, $"available (budget {combinedPct:F1}%)")
                : (true, $"available ({combinedPct:F1}%)");
        }

        if (combinedPct >= 0 && gate.FloorPct is { } floor && string.IsNullOrEmpty(gate.WindowName))
        {
            var label = budgetOnly ? "local budget exhausted" : "quota exhausted";
            return (false, $"{label} ({combinedPct:F1}% < {floor:F1}%)");
        }

        return (false, gate.Reason);
    }

    private static string FormatBudgetGateComparison(double budgetPct, QuotaGateDecision gate)
    {
        if (gate.FloorPct is { } floor && string.IsNullOrEmpty(gate.WindowName))
            return $"{budgetPct:F1}% < {floor:F1}%";
        return gate.Reason;
    }

    /// <summary>
    /// Returns whether <paramref name="kind"/> is currently routable per the
    /// availability registry, gating the FIRST trust of an apparently-available
    /// agent on a real in-sandbox CLI check (<see cref="IInVmSmokeGate"/>, cache
    /// hit = free) so the exit-127 / auth cascade is caught here rather than at
    /// dispatch. Mirrors <see cref="AgentClassRouter.ResolveAsync"/>'s gate so
    /// the audit phase and the work phase agree on what "available" means.
    /// Returns true when no availability registry is wired (legacy callers
    /// preserve their prior behaviour).
    /// </summary>
    private async Task<AgentAvailability> EnsureAgentSmokeAvailableAsync(
        AgentKind kind,
        InVmSmokeSandboxTarget target,
        CancellationToken ct)
    {
        // The in-VM gate (when wired) owns the read→probe→re-read and returns the
        // reconciled availability — including the exclusion Reason — so callers
        // get a verdict from this one call and never re-read the availability
        // registry alongside the gate (that dual binding is exactly what
        // IInVmSmokeGate was extracted to remove; re-reading would also degrade
        // the reason to a generic placeholder under gate-only wiring). Falls back
        // to a plain registry read, then to "available" when neither is wired
        // (legacy callers preserve their prior behaviour). target.BaselineRef
        // pins the probe to the image this work item will clone (B1), not just
        // the active baseline.
        return _dispatchAvailability is not null
            ? await _dispatchAvailability.EnsureAvailableAsync(kind, target, ct)
                ?? new AgentAvailability(true, null, null)
            : new AgentAvailability(true, null, null);
    }

    private async Task<AgentAvailability> EnsureAgentPauseAllowsTextOnlyAsync(
        AgentKind kind,
        string? agentInstanceId,
        CancellationToken ct)
    {
        if (_agentPauses is null)
            return new AgentAvailability(true, null, null);

        var pause = await _agentPauses.GetAgentStateAsync(kind, ct, agentInstanceId);
        if (pause is null)
            return new AgentAvailability(true, null, null);

        var reason = string.IsNullOrWhiteSpace(pause.PausedReason)
            ? AgentDispatchAvailability.PausedReasonPrefix
            : $"{AgentDispatchAvailability.PausedReasonPrefix}: {pause.PausedReason}";
        if (pause.ExpiresAt is { } expiresAt)
            reason = $"{reason} until {expiresAt:O}";

        return new AgentAvailability(false, reason, null, AgentAvailabilityCause.OperatorPaused);
    }

    private static bool IsOperatorPaused(AgentAvailability? availability) =>
        availability is { Available: false, Cause: AgentAvailabilityCause.OperatorPaused };

    private static InVmSmokeSandboxTarget ResolvePhaseSmokeTarget(
        Project project,
        string phase,
        string? baselineRef = null)
    {
        var sandboxTarget = phase switch
        {
            "rebase" => new SandboxTarget(
                project.NetworkProfiles.Work
                    ?? project.NetworkProfiles.AuditAgent
                    ?? project.NetworkProfiles.AuditTool,
                SandboxProfileFlavor.Headless),
            "planning" => SandboxTargetResolver.ResolveProjectPhase(project, project.NetworkProfiles.Work),
            // "delegation" explicitly pins the delegation turn to the
            // work-profile sandbox target — the phase runs in a sandbox with
            // the repository exactly as the work phase does.
            "delegation" => SandboxTargetResolver.ResolveProjectPhase(project, project.NetworkProfiles.Work),
            "check" => new SandboxTarget(project.NetworkProfiles.Work, SandboxProfileFlavor.Headless),
            "rework" => SandboxTargetResolver.ResolveProjectPhase(project, project.NetworkProfiles.Rework),
            "merge" => new SandboxTarget(project.NetworkProfiles.Merge, SandboxProfileFlavor.Headless),
            "audit" => SandboxTargetResolver.ResolveAudit(
                project.NetworkProfiles.AuditAgent,
                AuditCapabilities.AgentCredentials),
            _ => SandboxTargetResolver.ResolveProjectPhase(project, project.NetworkProfiles.Work),
        };

        return SandboxTargetResolver.ToInVmSmokeTarget(project, sandboxTarget, baselineRef);
    }

    private Task<AgentCredential?> ResolveAgentCredentialAsync(AgentKind kind, Project project, CancellationToken ct)
        => ResolveAgentCredentialAsync(kind, project, item: null, ct);

    private async Task<AgentCredential?> ResolveAgentCredentialAsync(
        AgentKind kind,
        Project project,
        WorkItem? item,
        CancellationToken ct)
    {
        if (item is not null && TryResolveSelectedMember(kind, project, item) is { } member
            && member.CredentialReference is not null)
        {
            var credential = await AgentInstanceCredentialResolver.ResolveCredentialAsync(member, ct).ConfigureAwait(false);
            if (credential is not null)
                return await GraftSandboxGlobalMountsAsync(credential, kind, project, ct).ConfigureAwait(false);
        }

        return _credentials is IProjectAwareCredentialProvider pac
            ? await pac.GetAsync(kind, project.CredentialProviderPriority, ct).ConfigureAwait(false)
            : await _credentials.GetAsync(kind, ct).ConfigureAwait(false);
    }

    private async Task<AgentCredential?> ResolveAgentCredentialAsync(
        AgentMembership member,
        Project project,
        CancellationToken ct)
    {
        if (member.CredentialReference is not null)
        {
            var credential = await AgentInstanceCredentialResolver.ResolveCredentialAsync(member, ct).ConfigureAwait(false);
            if (credential is not null)
                return await GraftSandboxGlobalMountsAsync(credential, member.Agent, project, ct).ConfigureAwait(false);
        }

        return await ResolveAgentCredentialAsync(member.Agent, project, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A member-scoped credential carries its own secret material but not the
    /// sandbox-global bind-mounts a kind-scoped provider adds (e.g. the Crock
    /// host-daemon socket, which only <c>CrockEnvironmentCredentialProvider</c>
    /// can build from <c>CrockSandboxOptions</c>). When the member credential
    /// defines no mounts, graft the kind provider's mounts onto it so execution
    /// gets the member's key AND the shared host adjunct. A no-op for agents
    /// whose kind provider has no mounts.
    /// </summary>
    private async Task<AgentCredential> GraftSandboxGlobalMountsAsync(
        AgentCredential memberCredential, AgentKind kind, Project project, CancellationToken ct)
    {
        if (memberCredential.Mounts.Count > 0)
            return memberCredential;

        var kindCredential = _credentials is IProjectAwareCredentialProvider pac
            ? await pac.GetAsync(kind, project.CredentialProviderPriority, ct).ConfigureAwait(false)
            : await _credentials.GetAsync(kind, ct).ConfigureAwait(false);
        if (kindCredential is null || kindCredential.Mounts.Count == 0)
            return memberCredential;

        return new AgentCredential(
            memberCredential.Agent,
            memberCredential.EnvironmentVariables,
            memberCredential.Files)
        {
            Mounts = kindCredential.Mounts,
            ExpiresAt = memberCredential.ExpiresAt,
        };
    }

    private async Task<AgentCredential?> ResolveAgentCredentialForInvocationAsync(
        IAgentRunner runner,
        Project project,
        WorkItem item,
        CancellationToken ct)
    {
        var selectedMember = TryResolveSelectedMember(runner.Kind, project, item);
        return selectedMember is not null
            ? await ResolveAgentCredentialAsync(selectedMember, project, ct).ConfigureAwait(false)
            : await ResolveAgentCredentialAsync(runner.Kind, project, item, ct).ConfigureAwait(false);
    }

    private AgentMembership? TryResolveSelectedMember(AgentKind kind, Project project, WorkItem item)
    {
        if (_classRouter is null)
            return null;
        var classId = item.AgentClassId ?? project.DefaultAgentClass;
        return classId is null
            ? null
            : _classRouter.FindMember(classId, kind, item.ModelId, item.AgentInstanceId);
    }

    private static string CanonicalAgentRouteKey(AgentKind kind, string? agentInstanceId)
    {
        if (string.IsNullOrWhiteSpace(agentInstanceId))
            return kind.Value;

        var id = agentInstanceId.Trim();
        if (id.Contains('/', StringComparison.Ordinal)
            || string.Equals(id, kind.Value, StringComparison.OrdinalIgnoreCase))
            return id;

        return AgentInstanceIds.RouteKey(kind, id);
    }

    /// <summary>
    /// Classifies one completed dispatch attempt into its per-agent circuit-breaker
    /// outcome, independent of quota classification. The router's dispatch gate only
    /// ever opens because this decision feeds <see cref="AgentClassRouter.RecordDispatchOutcome"/>:
    /// <list type="bullet">
    ///   <item><c>true</c> — the attempt succeeded; resets the breaker's failure window.</item>
    ///   <item><c>false</c> — a genuine agent-side dispatch failure of ANY kind (a real
    ///     per-attempt timeout, resume-exhaustion, or any agent/quota/infrastructure
    ///     error); feeds the windowed failure counter that opens the breaker.</item>
    ///   <item><c>null</c> — a host/operator/phase cancellation that is not the agent's
    ///     fault; it neither opens nor resets the breaker and must be skipped.</item>
    /// </list>
    /// Pure: a total function of the terminal exception (<c>null</c> on success) and
    /// whether it was a genuine per-attempt timeout (as opposed to a host/phase
    /// cancellation that merely surfaced as an <see cref="OperationCanceledException"/>).
    /// </summary>
    internal static bool? ClassifyDispatchOutcome(Exception? error, bool genuineAttemptTimeout)
    {
        if (error is null)
            return true;
        // A no-action-required determination is a successful dispatch: the
        // agent ran cleanly and delivered its verdict. It resets the failure
        // window like any other success rather than feeding it.
        if (error is NoActionRequiredException)
            return true;
        if (genuineAttemptTimeout)
            return false;
        // Any remaining OperationCanceledException is a host/operator/phase
        // cancellation — not the agent's fault, so it must not move the breaker.
        if (error is OperationCanceledException)
            return null;
        // Every other terminal exception is a real dispatch failure.
        return false;
    }

    /// <summary>
    /// Binds a runner to a class member's configuration (today: Copilot's
    /// per-member BYOK provider override). Runners without member-scoped
    /// configuration pass through untouched, as do members of a different
    /// kind — so every existing invocation behaves exactly as before.
    /// </summary>
    private static IAgentRunner BindMemberRunner(IAgentRunner runner, AgentMembership? member)
    {
        ArgumentNullException.ThrowIfNull(runner);
        if (member is null || member.Agent != runner.Kind || runner is not IMemberScopedAgentRunner scoped)
            return runner;
        return scoped.ForMember(member);
    }

}
