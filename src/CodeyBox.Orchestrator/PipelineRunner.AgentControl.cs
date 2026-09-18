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

// PipelineRunner.AgentControl.cs — AgentControl job dispatch (JobType.AgentControl): validation, top-level dispatch from RunAsync, and best-effort webhooks.
public sealed partial class PipelineRunner
{
    /// <summary>
    /// Whether the resumable Claude session worker should drive the work +
    /// every rework iteration for this item. All three conditions must hold:
    /// <list type="bullet">
    ///   <item>The worker is registered in DI (<see cref="_claudeSessionWorker"/> non-null).</item>
    ///   <item>The global flag <c>CodeyBox:ClaudeSession:Enabled</c> is true.</item>
    ///   <item>The per-project flag <c>Project.ClaudeSession.Enabled</c> is true.</item>
    ///   <item>The work item's effective agent is Claude (the worker is Claude-only).</item>
    ///   <item>For class-routed items, the selected class/member opts in to Claude sessions.</item>
    /// </list>
    /// <para>Items that fail any one of these conditions take the legacy
    /// independent-phase pipeline (fresh sandbox per work / rework call,
    /// no <c>--resume</c>, no shared VM across phases) unchanged. The brief
    /// is non-negotiable here: a session-shared auditor would self-review.</para>
    /// </summary>
    internal bool ShouldEnterClaudeSessionMode(WorkItem item, Project project, IAgentRunner runner)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(runner);
        if (_claudeSessionWorker is null) return false;
        if (!_claudeSessionOptions.Enabled) return false;
        if (!project.ClaudeSession.Enabled) return false;
        if (runner.Kind != AgentKind.Claude) return false;
        // CheckAndAct is a read-only single-shot probe; it doesn't have a
        // rework loop, so the session-share benefit doesn't apply.
        if (item.JobType == JobType.CheckAndAct) return false;
        if (item.JobType == JobType.AgentControl) return false;
        if (item.HasAgentTurnRecoveryBoundary) return false;
        var classId = item.AgentClassId ?? project.DefaultAgentClass;
        if (!string.IsNullOrWhiteSpace(classId))
        {
            if (_classRouter is null)
                return false;
            var selectedMember = _classRouter.FindMember(classId, runner.Kind, item.ModelId, item.AgentInstanceId);
            if (selectedMember is null || !_classRouter.IsClaudeSessionEnabled(classId, selectedMember))
                return false;
        }
        // The session worker opens ONE VM with the work-phase sandbox target
        // and reuses it across every rework turn. When the operator
        // configured Work and Rework with different network profiles (e.g.
        // broader egress during initial work, restricted rework after
        // auditor-controlled findings are fed back), keeping the work-phase
        // policy on the rework turns silently weakens the operator's
        // containment boundary. Refuse session mode in that configuration —
        // the legacy fresh-sandbox path applies the correct per-phase
        // policy and is the safe default.
        if (!string.Equals(
                project.NetworkProfiles.Work ?? string.Empty,
                project.NetworkProfiles.Rework ?? string.Empty,
                StringComparison.Ordinal))
        {
            _log.LogInformation(
                "Claude session-mode disabled for work item {WorkItemId}: project {ProjectId} configures distinct Work ({WorkProfile}) and Rework ({ReworkProfile}) network profiles; using the legacy per-phase sandbox path to preserve the rework containment boundary.",
                item.Id, project.Id, project.NetworkProfiles.Work ?? "(default)", project.NetworkProfiles.Rework ?? "(default)");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Opens a fresh <see cref="ClaudeSessionLifecycle"/> against a newly
    /// provisioned worker sandbox. Returns the lifecycle so the caller
    /// (<see cref="RunAsync"/>) can publish it on
    /// <see cref="_ambientSessionLifecycle"/> in its own ExecutionContext and
    /// keep a handle for the outer-finally disposal. The caller must assign
    /// the AsyncLocal — assigning here would be invisible to the parent
    /// because AsyncLocal values set inside an awaited child method do NOT
    /// flow back to the caller's ExecutionContext.
    /// </summary>
    private async Task<ClaudeSessionLifecycle?> TryOpenClaudeSessionLifecycleAsync(
        WorkItem item,
        Project project,
        IAgentRunner runner,
        string repoId,
        CancellationToken ct)
    {
        if (!ShouldEnterClaudeSessionMode(item, project, runner))
            return null;
        if (_claudeSessionWorker is null)
            return null;

        // Use the work-phase sandbox target for the worker VM. Subsequent
        // rework turns reuse the same VM via session resume, so the network
        // profile / flavor / baseline pin established here applies to every
        // worker turn for this item.
        var access = _gitHost.GetSandboxAccess(repoId);
        var sandboxTarget = SandboxTargetResolver.ResolveProjectPhase(project, project.NetworkProfiles.Work);
        var selectedMember = TryResolveSelectedMember(runner.Kind, project, item);
        var openedRouteKey = selectedMember?.RouteKey ?? CanonicalAgentRouteKey(runner.Kind, item.AgentInstanceId);
        var openedModelId = selectedMember?.ModelId ?? item.ModelId;
        var openedReasoningMode = selectedMember?.ReasoningMode ?? item.ReasoningMode;
        var credential = selectedMember is not null
            ? await ResolveAgentCredentialAsync(selectedMember, project, ct).ConfigureAwait(false)
            : await ResolveAgentCredentialAsync(runner.Kind, project, item, ct).ConfigureAwait(false);
        var spec = BuildSandboxSpec(
            access,
            includeAgentCredential: credential,
            allowAgentNetwork: true,
            hostNetworkProfile: sandboxTarget.NetworkProfile,
            timingWorkItemId: item.Id,
            timingPhase: "work",
            flavor: sandboxTarget.Flavor,
            extraEnvironment: null,
            baselineImageRef: SandboxTargetResolver.BaselineRefForTarget(
                project,
                new SandboxTarget(sandboxTarget.NetworkProfile, sandboxTarget.Flavor),
                item.BaselineImageRef),
            includeAgentTurnScratchpadTmpfs: true,
            credentialRunner: runner);

        var sandbox = await AcquireWorkPhaseSandboxAsync(
            item,
            "work",
            credential?.Agent.Value,
            sandboxTarget.NetworkProfile,
            spec,
            ct).ConfigureAwait(false);
        try
        {
            if (credential is not null && credential.Files.Count > 0)
                await MaterialiseCredentialFilesAsync(sandbox, credential, ct).ConfigureAwait(false);

            var lifecycle = await ClaudeSessionLifecycle.OpenAsync(
                _claudeSessionWorker,
                _claudeHandleSnapshot,
                sandbox,
                SandboxConventions.WorkDir,
                credential,
                openedModelId,
                openedReasoningMode,
                openedRouteKey,
                project.Id.Value,
                selectedMember?.RouteKey,
                ct).ConfigureAwait(false);
            // sandbox ownership transferred to the lifecycle. The AsyncLocal
            // is published by the caller (RunAsync) on the returned value, in
            // its own ExecutionContext — assigning here would be a no-op for
            // the parent frame.
            sandbox = null!;
            return lifecycle;
        }
        catch
        {
            // OpenAsync didn't adopt the sandbox; ensure we don't leak the VM.
            if (sandbox is not null)
                await sandbox.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }


    private Task<string> ProcessAgentPromptAsync(
        WorkItemId itemId,
        AgentKind agentKind,
        AgentPromptPhase phase,
        int iteration,
        Project project,
        ISandbox sandbox,
        string prompt,
        CancellationToken ct,
        AuditTarget? auditTarget = null)
    {
        if (!_promptPreprocessors.HasPreprocessors)
            return Task.FromResult(prompt);

        // Every pipeline-phase agent in this file runs against the
        // SandboxConventions.WorkDir clone (work, rework, check-and-act,
        // post-act-recheck, merge, conflict-rework, merge-security-review),
        // so we pass that as the preprocessor's working directory. The
        // deep-audit path uses /work/repo and goes through the
        // wrapper-based plumbing in PromptPreprocessingAgentRunner.RunAsync,
        // which forwards the runner's actual workingDirectory.
        var ctx = new PromptContext(itemId, agentKind, phase, iteration, project, sandbox, SandboxConventions.WorkDir, auditTarget);
        return _promptPreprocessors.ProcessAsync(ctx, prompt, ct);
    }

    private IAgentRunner WrapPromptPreprocessedRunner(
        IAgentRunner runner,
        WorkItemId itemId,
        AgentPromptPhase phase,
        int iteration,
        Project project,
        AuditTarget? auditTarget = null)
    {
        if (!_promptPreprocessors.HasPreprocessors)
            return runner;

        return PromptPreprocessingAgentRunner.Wrap(
            runner,
            _promptPreprocessors,
            itemId,
            phase,
            iteration,
            project,
            auditTarget);
    }

    private IReadOnlyList<AgenticConflictResolverCandidate> WrapPromptPreprocessedCandidates(
        IReadOnlyList<AgenticConflictResolverCandidate> candidates,
        WorkItemId itemId,
        AgentPromptPhase phase,
        int iteration,
        Project project)
    {
        if (!_promptPreprocessors.HasPreprocessors)
            return candidates;

        return candidates
            .Select(candidate => candidate with
            {
                Runner = WrapPromptPreprocessedRunner(
                    candidate.Runner,
                    itemId,
                    phase,
                    iteration,
                    project),
            })
            .ToList();
    }

    private async Task RunAgentControlAsync(WorkItem item, Project project, CancellationToken ct)
    {
        if (_agentPauses is null)
        {
            await TransitionFailed(
                item,
                "agent pause controller is not configured",
                CancellationToken.None,
                project,
                failureKind: "configuration");
            return;
        }

        var spec = item.AgentControl;
        if (spec is null)
        {
            await TransitionFailed(
                item,
                "agentControl spec is missing",
                CancellationToken.None,
                project,
                failureKind: "configuration");
            return;
        }

        var validationError = ValidateAgentControlSpec(spec);
        if (validationError is not null)
        {
            await TransitionFailed(item, validationError, CancellationToken.None, project, failureKind: "configuration");
            return;
        }

        await Transition(item, WorkItemState.Working, ct, project);
        var agent = new AgentKind(spec.Agent.Trim().ToLowerInvariant());
        var actor = $"work-item:{item.Id}";
        AgentPauseState? pausedState = null;
        bool resumed = false;

        try
        {
            switch (spec.Action)
            {
                case AgentControlAction.Pause:
                    {
                        var expiresAt = spec.ExpiresAt
                            ?? (spec.DurationSeconds is { } seconds
                                ? DateTimeOffset.UtcNow.AddSeconds(seconds)
                                : null);
                        pausedState = await _agentPauses.PauseAsync(agent, spec.Reason!.Trim(), actor, expiresAt, ct);
                        break;
                    }
                case AgentControlAction.Resume:
                    {
                        resumed = await _agentPauses.ResumeAsync(agent, actor, spec.Reason, ct);
                        break;
                    }
                default:
                    throw new UnreachableException($"validated unsupported agentControl action '{spec.Action}'");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await TransitionFailed(item, ex.Message, CancellationToken.None, project, failureKind: "infrastructure");
            return;
        }

        await PublishAgentControlWebhookBestEffortAsync(agent, spec, actor, pausedState, resumed);
        await Transition(item, WorkItemState.Done, ct, project);
    }

    private static string? ValidateAgentControlSpec(AgentControlSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Agent))
            return "agentControl.agent is required";

        switch (spec.Action)
        {
            case AgentControlAction.Pause:
                if (string.IsNullOrWhiteSpace(spec.Reason))
                    return "agentControl.reason is required for pause";
                if (AgentPauseValidation.ValidateOptionalReason(spec.Reason, "agentControl.reason") is { } pauseReasonError)
                    return pauseReasonError;
                break;
            case AgentControlAction.Resume:
                if (AgentPauseValidation.ValidateOptionalReason(spec.Reason, "agentControl.reason") is { } resumeReasonError)
                    return resumeReasonError;
                break;
            default:
                return $"unsupported agentControl action '{spec.Action}'";
        }

        if (spec.DurationSeconds is { } seconds && seconds <= 0)
            return "agentControl.durationSeconds must be positive";
        if (spec.DurationSeconds is not null && spec.ExpiresAt is not null)
            return "agentControl: provide either durationSeconds or expiresAt, not both";
        if (spec.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
            return "agentControl.expiresAt must be in the future";

        return null;
    }

    private async Task PublishAgentControlWebhookBestEffortAsync(
        AgentKind agent,
        AgentControlSpec spec,
        string actor,
        AgentPauseState? pausedState,
        bool resumed)
    {
        try
        {
            if (pausedState is not null)
            {
                await _webhooks.PublishAsync(new WebhookEvent
                {
                    Event = "agent.paused",
                    Details = new
                    {
                        agent = pausedState.Agent.Value,
                        reason = pausedState.PausedReason,
                        pausedAt = pausedState.PausedAt,
                        pausedBy = pausedState.PausedBy,
                        expiresAt = pausedState.ExpiresAt,
                    },
                }, CancellationToken.None);
                return;
            }

            if (resumed)
            {
                await _webhooks.PublishAsync(new WebhookEvent
                {
                    Event = "agent.resumed",
                    Details = new
                    {
                        agent = agent.Value,
                        resumedAt = DateTimeOffset.UtcNow,
                        resumedBy = actor,
                        reason = spec.Reason,
                    },
                }, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Agent control mutation for {Agent} succeeded, but webhook delivery failed",
                agent.Value);
        }
    }

    private int _missingKnobRegistryWarned;

}
