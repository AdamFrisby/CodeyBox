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

// PipelineRunner.QuotaAuth.cs — Quota/auth probing for no-diff outcomes: quota-candidate probes, auth corroboration, stream capture, and supervision startup.
public sealed partial class PipelineRunner
{
    private async Task ThrowIfNoDiffReworkQuotaFailureAsync(
        WorkItem item,
        Project project,
        AgentKind agent,
        string? observedModelId,
        AgentResult agentResult,
        string phase,
        string sandboxId,
        DateTimeOffset agentEndedAt,
        CancellationToken ct)
    {
        // Rework-only clean-exit/no-diff infra classification. Captured
        // stdout/stderr are the rework run's output and must be disambiguated
        // before genuine empty-rework handling. Runner terminal diagnostics are
        // a separate side channel, so they still need quota-probe corroboration.
        await ThrowIfNoDiffQuotaFailureFromTextAsync(
            item: item,
            project: project,
            agent: agent,
            observedModelId: observedModelId,
            summary: agentResult.Summary,
            stderr: agentResult.Stderr,
            stdout: null,
            phase: phase,
            sandboxId: sandboxId,
            agentEndedAt: agentEndedAt,
            evidenceSource: "captured stderr",
            evidenceTrust: NoDiffQuotaEvidenceTrust.RequiresQuotaProbe,
            ct: ct);

        await ThrowIfNoDiffQuotaFailureFromTextAsync(
            item: item,
            project: project,
            agent: agent,
            observedModelId: observedModelId,
            summary: agentResult.Summary,
            stderr: null,
            stdout: agentResult.Stdout,
            phase: phase,
            sandboxId: sandboxId,
            agentEndedAt: agentEndedAt,
            evidenceSource: "captured stdout",
            evidenceTrust: NoDiffQuotaEvidenceTrust.RequiresQuotaProbe,
            ct: ct);

        if (!string.IsNullOrWhiteSpace(agentResult.TerminalDiagnostic))
        {
            await ThrowIfNoDiffQuotaFailureFromTextAsync(
                item: item,
                project: project,
                agent: agent,
                observedModelId: observedModelId,
                summary: agentResult.Summary,
                stderr: agentResult.TerminalDiagnostic,
                stdout: null,
                phase: phase,
                sandboxId: sandboxId,
                agentEndedAt: agentEndedAt,
                evidenceSource: "terminal diagnostic",
                evidenceTrust: NoDiffQuotaEvidenceTrust.RequiresQuotaProbe,
                ct: ct);
        }
    }

    private async Task ThrowIfNoDiffTerminalDiagnosticQuotaFailureAsync(
        WorkItem item,
        AgentKind agent,
        string? observedModelId,
        AgentResult agentResult,
        string phase,
        string sandboxId,
        DateTimeOffset agentEndedAt,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(agentResult.TerminalDiagnostic))
            return;

        await ThrowIfNoDiffQuotaFailureFromTextAsync(
            item: item,
            project: null,
            agent: agent,
            observedModelId: observedModelId,
            summary: agentResult.Summary,
            stderr: agentResult.TerminalDiagnostic,
            stdout: null,
            phase: phase,
            sandboxId: sandboxId,
            agentEndedAt: agentEndedAt,
            evidenceSource: "terminal diagnostic",
            evidenceTrust: NoDiffQuotaEvidenceTrust.CliOwned,
            ct: ct);
    }

    private enum NoDiffQuotaEvidenceTrust
    {
        CliOwned,
        RequiresQuotaProbe,
    }

    private async Task ThrowIfNoDiffQuotaFailureFromTextAsync(
        WorkItem item,
        Project? project,
        AgentKind agent,
        string? observedModelId,
        string summary,
        string? stderr,
        string? stdout,
        string phase,
        string sandboxId,
        DateTimeOffset agentEndedAt,
        string evidenceSource,
        NoDiffQuotaEvidenceTrust evidenceTrust,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(stderr) && string.IsNullOrWhiteSpace(stdout))
            return;

        var noChangeQuota = _quotaClassifier.Detect(agent, stderr, stdout);
        if (noChangeQuota is null)
            return;

        if (noChangeQuota.Kind == QuotaFailureKind.Unauthorized && project is not null)
        {
            var authRequired = ToAuthRequiredClassification(new AgentFailureClassification(
                AgentFailureKind.AuthError,
                Reason: "auth pattern matched"));
            await HandleAuthRequiredDetectionAsync(
                item,
                project,
                agent,
                phase,
                authRequired,
                throwOnMatch: true,
                stdoutOnlyEvidence: string.IsNullOrWhiteSpace(stderr) && !string.IsNullOrWhiteSpace(stdout),
                requireStdoutOnlyCorroboration: evidenceTrust == NoDiffQuotaEvidenceTrust.RequiresQuotaProbe,
                requireAuthCorroboration: evidenceTrust == NoDiffQuotaEvidenceTrust.RequiresQuotaProbe,
                ct: ct);
        }

        if (!IsParkableQuotaKind(noChangeQuota))
            return;

        if (evidenceTrust == NoDiffQuotaEvidenceTrust.RequiresQuotaProbe
            && !await TryCorroborateNoDiffQuotaFailureAsync(item, project, agent, observedModelId, phase, ct))
        {
            _log.LogWarning(
                "Ignoring uncorroborated clean-exit/no-diff quota evidence from {Source} for agent {Agent} during {Phase}; treating run as genuine no-diff",
                evidenceSource,
                agent.Value,
                phase);
            return;
        }

        _quotaAuditEmitter.EmitAdvisoryAuditEvents(agent, stderr, stdout, phase, sandboxId);
        // Feed the observed-failure store so the router proactively gates this
        // member during its quota window. A clean-exit give-up summary is often
        // "ok", so bypass the exit-1 summary guard after a positive no-diff quota
        // match.
        await _quotaClassifier.RecordIfQuotaFailureAsync(
            _quotaFailures,
            agent,
            observedModelId,
            summary,
            stderr,
            agentEndedAt,
            _auditQuotaOptions.ObservedFailureRetention,
            ct,
            projectId: item.ProjectId,
            stdout: stdout,
            bypassExitedSummaryGuard: true);

        throw new TerminalQuotaError(noChangeQuota.Kind,
            QuotaFailureMessage(
                noChangeQuota.Kind,
                $"Agent {agent} reported quota failure on clean-exit/no-diff rework from {evidenceSource}",
                SanitizedAgentDetail.FromRaw(stderr ?? stdout)),
            noChangeQuota.ResetAt,
            providerSurfaceMatch: evidenceTrust != NoDiffQuotaEvidenceTrust.RequiresQuotaProbe);
    }

    private async Task<bool> TryCorroborateNoDiffQuotaFailureAsync(
        WorkItem item,
        Project? project,
        AgentKind agent,
        string? observedModelId,
        string phase,
        CancellationToken ct)
    {
        if (project is null
            || _quotaProbes is null)
        {
            return false;
        }

        var member = BuildNoDiffQuotaProbeMember(item, project, agent, observedModelId);
        var probe = ResolveQuotaProbe(member).Probe;
        if (probe is null)
        {
            return false;
        }

        try
        {
            var snapshot = await probe.GetAvailabilityAsync(member, ct).ConfigureAwait(false);
            var quota = QuotaGatePolicy.ResolveMemberQuota(snapshot, member);
            if (!quota.IsKnown)
                return false;

            var gate = _auditQuotaGatePolicy.Evaluate(member, quota, _opts.TimeProvider.GetUtcNow());
            return !gate.Allow;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Quota probe corroboration failed for no-diff quota evidence from agent {Agent} during {Phase}; ignoring untrusted quota text",
                agent.Value,
                phase);
            return false;
        }
    }

    /// <summary>
    /// Checks whether the agent's quota probe — which authenticates with the
    /// same credential the failed run used — currently reads healthy. A known
    /// healthy reading contradicts an auth-failure classification: the
    /// credential just authenticated successfully against the provider, so the
    /// captured text is the agent's narration (e.g. credential-handling work
    /// quoting auth shapes), not the harness refusing to run. Callers
    /// downgrade contradicted evidence to an item-scoped failure instead of a
    /// fleet-wide exclusion. Unknown or missing probes never contradict:
    /// absence of evidence is not evidence of health, so paths without probe
    /// coverage keep their existing benching behaviour.
    /// </summary>
    private async Task<bool> IsAuthContradictedByHealthyQuotaProbeAsync(
        WorkItem? item,
        Project? project,
        AgentKind agent,
        string? observedModelId,
        CancellationToken ct)
    {
        if (item is null || project is null || _quotaProbes is null)
            return false;

        var member = BuildQuotaProbeMember(item, project, agent, observedModelId);
        var probe = ResolveQuotaProbe(member).Probe;
        if (probe is null)
            return false;

        try
        {
            var snapshot = await probe.GetAvailabilityAsync(member, ct).ConfigureAwait(false);
            var quota = QuotaGatePolicy.ResolveMemberQuota(snapshot, member);
            if (!quota.IsKnown)
                return false;

            var gate = _auditQuotaGatePolicy.Evaluate(member, quota, _opts.TimeProvider.GetUtcNow());
            if (gate.Allow)
            {
                _log.LogWarning(
                    "Auth evidence from agent {Agent} during failure handling contradicted by healthy quota probe; downgrading to item-level failure without fleet-wide bench",
                    agent.Value);
                return true;
            }

            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(
                ex,
                "Quota probe contradiction check failed for auth evidence from agent {Agent}; keeping existing fleet-bench behaviour",
                agent.Value);
            return false;
        }
    }

    private AgentMembership BuildQuotaProbeMember(
        WorkItem item,
        Project? project,
        AgentKind agent,
        string? observedModelId)
    {
        var effectiveProject = project ?? new Project
        {
            Id = item.ProjectId,
            DisplayName = item.ProjectId.Value,
            RepositoryUrl = string.Empty,
        };
        var selected = TryResolveSelectedMember(agent, effectiveProject, item);
        if (selected is not null)
        {
            return observedModelId is null
                ? selected
                : selected with { ModelId = observedModelId };
        }

        return new AgentMembership
        {
            Agent = agent,
            InstanceId = item.AgentInstanceId,
            ModelId = observedModelId ?? item.ModelId,
            ReasoningMode = item.ReasoningMode,
            Billing = AgentBilling.Subscription,
            QualityScore = SyntheticQuotaProbeQualityScore,
        };
    }

    private AgentMembership BuildNoDiffQuotaProbeMember(
        WorkItem item,
        Project project,
        AgentKind agent,
        string? observedModelId) =>
        BuildQuotaProbeMember(item, project, agent, observedModelId);

    private async Task<bool> HasAnyQuotaCandidateHealthyProbeAsync(
        WorkItem item,
        Project? project,
        string classId,
        CancellationToken ct,
        AgentMembership? preferredMember = null,
        string? requireCapability = null)
    {
        if (_quotaProbes is null || _classRouter is null)
            return false;

        var effectiveProject = project ?? new Project
        {
            Id = item.ProjectId,
            DisplayName = item.ProjectId.Value,
            RepositoryUrl = string.Empty,
        };
        var nowUtc = _opts.TimeProvider.GetUtcNow();

        if (preferredMember is not null
            && !IsRouterCachedExhausted(item.Id, preferredMember)
            && !await IsAgentPausedAsync(preferredMember.Agent, ct).ConfigureAwait(false)
            && ResolveQuotaProbe(preferredMember).Probe is { } preferredProbe)
        {
            try
            {
                var snapshot = await preferredProbe.GetAvailabilityAsync(preferredMember, ct).ConfigureAwait(false);
                var quota = QuotaGatePolicy.ResolveMemberQuota(snapshot, preferredMember);
                if (quota.IsKnown)
                {
                    var gate = _auditQuotaGatePolicy.Evaluate(preferredMember, quota, nowUtc);
                    if (gate.Allow)
                        return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug(ex, "Probe check failed for preferred member {Agent} in class '{ClassId}'", preferredMember.Agent.Value, classId);
            }
        }

        IReadOnlyList<AgentMembership> candidates;
        try
        {
            candidates = await _classRouter.OrderedFallbackCandidatesAsync(
                item, effectiveProject, ct, smokeTarget: null, requireQuota: false).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Failed to resolve fallback candidates for class '{ClassId}' during probe corroboration", classId);
            return false;
        }

        foreach (var candidate in candidates)
        {
            // A healthy probe on a member outside the capability pool the
            // park is about (e.g. a non-audit-capable member when the audit
            // pool is exhausted) must not veto the park — that member was
            // never eligible to relieve it.
            if (requireCapability is not null
                && !MemberHasClassCapability(classId, candidate, requireCapability))
                continue;
            if (ResolveQuotaProbe(candidate).Probe is not { } probe)
                continue;

            // A member the router already marked exhausted from a real
            // rejection in this episode is not "healthy" just because a
            // lagging probe snapshot still reads headroom. The repo pins
            // this cache-wins semantic (a live healthy probe must not
            // resurrect a cached-out bucket); the veto below only applies
            // to members with no cache verdict.
            if (IsRouterCachedExhausted(item.Id, candidate))
                continue;
            // A member the operator paused cannot relieve the park no matter
            // what its probe reads — counting its healthy snapshot as a veto
            // would park-then-idle behind the wrong scheduler (e.g. quota
            // retry for an agent-resume blocker).
            if (await IsAgentPausedAsync(candidate.Agent, ct).ConfigureAwait(false))
                continue;

            try
            {
                var snapshot = await probe.GetAvailabilityAsync(candidate, ct).ConfigureAwait(false);
                var quota = QuotaGatePolicy.ResolveMemberQuota(snapshot, candidate);
                if (!quota.IsKnown)
                    continue;

                var gate = _auditQuotaGatePolicy.Evaluate(candidate, quota, nowUtc);
                if (gate.Allow)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug(ex, "Probe check failed for candidate {Agent} in class '{ClassId}'", candidate.Agent.Value, classId);
            }
        }

        return false;
    }

    private async Task<bool> IsQuotaContradictedByProbeForWorkItemAsync(
        WorkItem item,
        Project? project,
        string phase,
        CancellationToken ct)
    {
        if (_quotaProbes is null)
            return false;

        var agent = item.Agent;
        // The work agent's own probe only speaks for work-lane parks. An
        // audit-pool park (phase "audit") must be corroborated by audit
        // candidates alone — a healthy work runner must not veto it, and a
        // spent one must not force it; the candidate walk below decides.
        var probeSpeaksForPhase = string.Equals(phase, "work", StringComparison.Ordinal)
            || string.Equals(phase, "rework", StringComparison.Ordinal);
        if (probeSpeaksForPhase
            && agent is { } agentKind)
        {
            var member = BuildQuotaProbeMember(item, project, agentKind, item.ModelId);
            if (ResolveQuotaProbe(member).Probe is { } probe
                && !await IsAgentPausedAsync(agentKind, ct).ConfigureAwait(false))
            {
                // Same staleness rule as the candidate walk below: a member with
                // a live router-cache exhaustion entry was rejected for real in
                // this episode — its lagging healthy snapshot must not veto the
                // park.
                if (!IsRouterCachedExhausted(item.Id, member))
                {
                    try
                    {
                        var snapshot = await probe.GetAvailabilityAsync(member, ct).ConfigureAwait(false);
                        var quota = QuotaGatePolicy.ResolveMemberQuota(snapshot, member);
                        if (quota.IsKnown)
                        {
                            var nowUtc = _opts.TimeProvider.GetUtcNow();
                            var gate = _auditQuotaGatePolicy.Evaluate(member, quota, nowUtc);
                            if (gate.Allow)
                            {
                                return true;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogDebug(ex, "Probe check in TransitionWaitingForQuotaResetAsync failed for agent {Agent}", agentKind.Value);
                    }
                }
            }
        }

        var classId = item.AgentClassId ?? project?.DefaultAgentClass;
        if (_classRouter is not null && classId is not null)
        {
            // Audit-phase parks are corroborated by audit-pool members only;
            // a healthy non-audit-capable member must not veto them. When no
            // audit pool is configured every member is audit-eligible, so no
            // filter applies.
            string? requireCapability = null;
            if (string.Equals(phase, "audit", StringComparison.Ordinal)
                && _classRouter.GetCapabilityPool(classId, WellKnownCapabilities.Audit) is not null)
            {
                requireCapability = WellKnownCapabilities.Audit;
            }
            return await HasAnyQuotaCandidateHealthyProbeAsync(item, project, classId, ct, requireCapability: requireCapability).ConfigureAwait(false);
        }

        return false;
    }

    private async Task ThrowIfNoDiffReworkCapturedAuthErrorAsync(
        WorkItem item,
        Project project,
        AgentKind agent,
        string phase,
        AgentResult agentResult,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(agentResult.Stderr)
            && string.IsNullOrWhiteSpace(agentResult.Stdout))
        {
            return;
        }

        var classification = _authFailureClassifier.ClassifyFailure(
            agent,
            new AgentResult(
                Success: true,
                Summary: "clean-exit/no-diff rework",
                Stdout: agentResult.Stdout,
                Stderr: agentResult.Stderr));
        if (classification.Kind is not AgentFailureKind.AuthError)
            return;

        var authRequired = ToAuthRequiredClassification(classification);
        await HandleAuthRequiredDetectionAsync(
            item,
            project,
            agent,
            phase,
            authRequired,
            throwOnMatch: true,
            stdoutOnlyEvidence: string.IsNullOrWhiteSpace(agentResult.Stderr)
                && !string.IsNullOrWhiteSpace(agentResult.Stdout),
            requireStdoutOnlyCorroboration: true,
            requireAuthCorroboration: true,
            ct: ct);
    }

    private async Task ThrowIfNoDiffTerminalAuthDiagnosticAsync(
        WorkItem item,
        Project project,
        AgentKind agent,
        string phase,
        string? terminalDiagnostic,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(terminalDiagnostic))
            return;

        var classification = _authFailureClassifier.ClassifyFailure(
            agent,
            new AgentResult(
                Success: true,
                Summary: "agent terminal diagnostic",
                Stdout: null,
                Stderr: terminalDiagnostic));
        if (classification.Kind is not (AgentFailureKind.AuthError or AgentFailureKind.AuthRequired))
            return;

        var authRequired = ToAuthRequiredClassification(classification);
        await HandleAuthRequiredDetectionAsync(
            item,
            project,
            agent,
            phase,
            authRequired,
            throwOnMatch: true,
            stdoutOnlyEvidence: false,
            requireStdoutOnlyCorroboration: false,
            requireAuthCorroboration: true,
            ct: ct);
    }

    private static AgentFailureClassification ToAuthRequiredClassification(
        AgentFailureClassification classification)
        => classification.Kind == AgentFailureKind.AuthRequired
            ? classification
            : classification with
            {
                Kind = AgentFailureKind.AuthRequired,
                Reason = classification.Reason ?? "auth pattern matched",
            };

    private enum AuthRequiredCorroboration
    {
        Unavailable,
        NotCorroborated,
        Corroborated,
    }

    private async Task<AuthRequiredCorroboration> TryCorroborateAuthRequiredAsync(
        WorkItem? item,
        Project project,
        AgentKind agent,
        string phase,
        CancellationToken ct)
    {
        if (_inVmSmokeGate is not { Enabled: true })
            return AuthRequiredCorroboration.Unavailable;

        try
        {
            var target = ResolveAuthCorroborationSmokeTarget(project, phase, item?.BaselineImageRef);
            var availability = await _inVmSmokeGate.ForceProbeAsync(agent, target, ct);
            if (availability is null)
                return AuthRequiredCorroboration.Unavailable;
            return IsAuthCorroboratingSmokeFailure(agent, availability)
                ? AuthRequiredCorroboration.Corroborated
                : AuthRequiredCorroboration.NotCorroborated;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Forced in-VM smoke corroboration failed for auth evidence from agent {Agent} during {Phase}; continuing item-level auth failure without global bench",
                agent.Value,
                phase);
            return AuthRequiredCorroboration.Unavailable;
        }
    }

    private static InVmSmokeSandboxTarget ResolveAuthCorroborationSmokeTarget(
        Project project,
        string phase,
        string? baselineRef)
    {
        var normalizedPhase =
            phase.StartsWith("audit:", StringComparison.OrdinalIgnoreCase) ? "audit"
            : phase.Contains("check", StringComparison.OrdinalIgnoreCase) ? "check"
            : phase.Contains("rework", StringComparison.OrdinalIgnoreCase) ? "rework"
            : phase.Contains("merge", StringComparison.OrdinalIgnoreCase) ? "merge"
            : phase.Contains("rebase", StringComparison.OrdinalIgnoreCase) ? "rebase"
            : phase;

        return ResolvePhaseSmokeTarget(project, normalizedPhase, baselineRef);
    }

    private bool IsAuthCorroboratingSmokeFailure(AgentKind agent, AgentAvailability? availability)
    {
        // The forced in-VM probe ran just before this check. If it observed an
        // auth/login prompt, InVmSmokeProber escalates via MarkAuthRequired
        // (not MarkSmokeResult), so the structured AuthRequired channel is now
        // populated. Read that channel directly instead of substring-sniffing
        // AgentAvailability.Reason — the freeform text is operator-facing and
        // any future reword would silently break corroboration without a test
        // signal.
        if (_authRequiredReader is not null
            && _authRequiredReader.GetAuthRequiredAvailability(agent).AuthRequired)
        {
            return true;
        }

        // Backstop for legacy/embedded paths where the auth registry isn't
        // wired or a test double cannot publish the structured channel. The
        // forced in-VM smoke reason is orchestrator-owned evidence, not model
        // output, but keep the fallback narrow so generic smoke failures
        // ("transient: try later", missing binary, policy mismatch) do not
        // become fleet-wide auth benches.
        return availability is { Available: false }
            && IsLegacyAuthSmokeReason(availability.Reason);
    }

    private static bool IsLegacyAuthSmokeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return false;

        return reason.Contains("credential login required", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("auth required", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("authentication required", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("login required", StringComparison.OrdinalIgnoreCase);
    }

    private Task ThrowIfAuthRequiredOutputAsync(
        WorkItem item,
        Project project,
        AgentKind agent,
        string phase,
        string? stdout,
        string? stderr,
        bool requireStdoutOnlyCorroboration = false,
        CancellationToken ct = default)
    {
        return HandleAuthRequiredOutputAsync(
            item, project, agent, phase, stdout, stderr,
            throwOnMatch: true,
            requireStdoutOnlyCorroboration: requireStdoutOnlyCorroboration,
            ct: ct);
    }

    // The AgentResult-form wrapper mirrors the explicit-stream overload's
    // requireStdoutOnlyCorroboration knob so callers don't silently fall back
    // to the policy default by passing an AgentResult instead of (stdout,
    // stderr). Every retrofit call site that runs on model-controlled stdout
    // (audit / merge / rebase-resolver / session-resume / conflict-rework /
    // check / post-act-recheck / work-phase failure) opts into corroboration;
    // leaving the AgentResult overload at the false default reintroduces the
    // single-crafted-prompt fleet-wide bench the corroboration path exists
    // to prevent. Keep the parameter explicit at every call site rather than
    // flipping the default so the security-relevant choice is visible in diff.
    private Task ThrowIfAuthRequiredOutputAsync(
        WorkItem item,
        Project project,
        AgentKind agent,
        string phase,
        AgentResult result,
        bool requireStdoutOnlyCorroboration,
        CancellationToken ct = default)
        => ThrowIfAuthRequiredOutputAsync(
            item, project, agent, phase, result.Stdout, result.Stderr,
            requireStdoutOnlyCorroboration: requireStdoutOnlyCorroboration,
            ct: ct);

    private async Task HandleAgenticResolverAuthRequiredOutputAsync(
        WorkItem item,
        Project project,
        string phase,
        AgenticConflictResolverResult result,
        CancellationToken ct = default)
    {
        var authFailures = result.AuthFailures ?? [];
        if (authFailures.Count == 0)
        {
            var emittingAgent = result.LastAttemptedRunner?.Kind ?? result.ChosenRunner?.Kind ?? item.Agent ?? project.DefaultAgent;
            await ThrowIfAuthRequiredOutputAsync(
                item, project, emittingAgent, phase, result.Stdout, result.Stderr,
                requireStdoutOnlyCorroboration: true,
                ct: ct);
            return;
        }

        // Publish side effects for EVERY auth-failed candidate before throwing.
        // Previously this loop passed throwOnMatch=!result.Success directly to
        // HandleAuthRequiredDetectionAsync, which threw on the first iteration
        // and skipped the remaining failures — meaning a multi-agent outage
        // (e.g. the whole class unauthenticated) only benched and alerted on
        // the first candidate while leaving the rest routable. Coalesce into
        // one publish-all-then-throw sequence so the breaker reflects every
        // affected agent.
        AgentAuthRequiredException? firstThrow = null;
        foreach (var failure in authFailures)
        {
            var handling = await HandleAuthRequiredDetectionAsync(
                item,
                project,
                failure.Runner.Kind,
                phase,
                failure.Classification,
                throwOnMatch: false,
                failure.StdoutOnlyEvidence,
                requireStdoutOnlyCorroboration: true,
                matchedConfiguredPattern: failure.MatchedConfiguredPattern,
                ct: ct);

            // If the resolver ultimately succeeded, a failed earlier candidate's
            // login prompt should bench that candidate and alert the operator,
            // but it should not discard the fallback's valid resolution.
            if (!result.Success && firstThrow is null)
            {
                var reason = _authRequiredHandler.BuildReason(
                    phase,
                    failure.Classification,
                    failure.StdoutOnlyEvidence);
                firstThrow = new AgentAuthRequiredException(
                    failure.Runner.Kind,
                    phase,
                    handling.Reason ?? reason,
                    handling.Scope ?? WorkItemAuthFailureScope.Fleet);
            }
        }

        if (firstThrow is not null)
            throw firstThrow;
    }

    private async Task<AgentStreamCapture?> BeginAgentStreamCaptureAsync(
        WorkItemId workItemId,
        string phase,
        int iteration,
        CancellationToken ct)
    {
        if (_agentStreams is null)
            return null;
        return await _agentStreams.BeginCaptureAsync(workItemId, phase, iteration, ct);
    }

    private async Task<bool> CanCaptureStructuredStreamAsync(
        IAgentRunner runner,
        ISandbox sandbox,
        string phase,
        CancellationToken ct)
    {
        if (_agentStreams is null || !_agentStreams.Options.Enabled)
            return false;

        if (runner is not IStructuredStreamAgentRunner structuredRunner)
        {
            _log.LogInformation(
                "Agent {AgentKind} does not support structured stream capture; using plaintext fallback for phase {Phase}",
                runner.Kind.Value,
                phase);
            return false;
        }

        try
        {
            if (await structuredRunner.SupportsStructuredStreamAsync(sandbox, ct).ConfigureAwait(false))
                return true;

            _log.LogInformation(
                "Agent {AgentKind} structured stream flag is unavailable; using plaintext fallback for phase {Phase}",
                runner.Kind.Value,
                phase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogInformation(
                ex,
                "Failed to verify structured stream support for agent {AgentKind}; using plaintext fallback for phase {Phase}",
                runner.Kind.Value,
                phase);
        }

        return false;
    }

    private async Task<bool> CanCaptureAuditStructuredStreamAsync(
        IAgentRunner runner,
        ISandbox sandbox,
        string phase,
        string auditorName,
        WorkItem item,
        Project project,
        int iteration,
        CancellationToken ct)
    {
        var timeout = _pipelineTuning.Current.AuditorIdleTimeout;
        if (timeout <= TimeSpan.Zero)
            return await CanCaptureStructuredStreamAsync(runner, sandbox, phase, ct).ConfigureAwait(false);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var lastActivityTicks = Stopwatch.GetTimestamp();
        void Touch() => Volatile.Write(ref lastActivityTicks, Stopwatch.GetTimestamp());

        var watchedSandbox = new ActivityTrackingSandbox(sandbox, Touch);
        var probeTask = CanCaptureStructuredStreamAsync(runner, watchedSandbox, phase, linkedCts.Token);
        var timeoutTask = WaitForAuditorIdleTimeoutAsync(linkedCts.Token, () => Volatile.Read(ref lastActivityTicks));

        try
        {
            var completed = await Task.WhenAny(probeTask, timeoutTask).ConfigureAwait(false);
            if (completed == timeoutTask)
            {
                var timedOutAfter = await timeoutTask.ConfigureAwait(false);
                if (timedOutAfter is not null)
                {
                    await CancelAndTearDownAfterIdleTimeoutAsync(
                        linkedCts,
                        probeTask,
                        sandbox,
                        "structured-stream probe",
                        auditorName,
                        runner.Kind,
                        item,
                        project,
                        iteration).ConfigureAwait(false);
                    throw new AuditorIdleTimeoutException(auditorName, runner.Kind, timedOutAfter.Value);
                }

                ct.ThrowIfCancellationRequested();
            }

            var result = await probeTask.ConfigureAwait(false);
            Touch();
            ct.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            try { await linkedCts.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }

            try { await timeoutTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private Action<string>? BuildStdoutCallback(
        WorkItemId workItemId,
        string phase,
        AgentStreamCapture? streamCapture)
    {
        if (_stdoutBroadcaster is null && streamCapture is null)
            return null;

        return chunk =>
        {
            _stdoutBroadcaster?.BroadcastChunk(workItemId, phase, chunk);
            streamCapture?.WriteChunk(chunk);
        };
    }

    private Task<IAgentSupervisionSession?> StartAgentSupervisionSessionAsync(
        WorkItemId workItemId,
        Project project,
        string phase,
        int iteration,
        IAgentRunner runner,
        string? agentInstanceId,
        string? modelId,
        string? reasoningMode,
        ISandbox sandbox,
        string workingDirectory,
        string source,
        CancellationToken ct)
    {
        if (_agentSupervision is null || !_agentSupervision.Enabled)
            return Task.FromResult<IAgentSupervisionSession?>(null);

        return _agentSupervision.TryStartSessionAsync(
            new AgentSupervisionSessionStart(
                workItemId,
                project.Id.Value,
                phase,
                iteration,
                runner.Kind,
                agentInstanceId,
                modelId,
                reasoningMode,
                sandbox.Id,
                workingDirectory,
                source),
            ct);
    }

    private static Action<string>? WrapSupervisionStdout(
        IAgentSupervisionSession? supervision,
        Action<string>? stdoutCallback) =>
        supervision is null ? stdoutCallback : supervision.WrapStdoutCallback(stdoutCallback);

    private sealed class SupervisedAgentRunner : IAgentRunner
    {
        private readonly IAgentRunner _inner;
        private readonly IAgentSupervisionSession _supervision;

        public SupervisedAgentRunner(IAgentRunner inner, IAgentSupervisionSession supervision)
        {
            _inner = inner;
            _supervision = supervision;
        }

        public AgentKind Kind => _inner.Kind;

        public async Task<AgentResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false)
        {
            return await AgentSupervisionTurnRunner.RunAutonomousAndQueuedInjectionsAsync(
                    _inner,
                    sandbox,
                    workingDirectory,
                    prompt,
                    credential,
                    modelId,
                    reasoningMode,
                    _supervision,
                    stdoutChunkCallback,
                    captureStructuredStream,
                    promptPreprocessor: null,
                    ct)
                .ConfigureAwait(false);
        }

        public AgentFailureClassification ClassifyFailure(AgentResult result) =>
            _inner.ClassifyFailure(result);
    }

}
