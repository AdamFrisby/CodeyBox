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

// PipelineRunner.WorkPhase.cs — Work phase: agent-turn execution inside the work sandbox.
public sealed partial class PipelineRunner
{
    /// <summary>
    /// Runs the agent in a sandbox against <paramref name="branch"/>. On the
    /// first call (work phase), <paramref name="isInitial"/> is true and the
    /// branch is created from <paramref name="baseBranch"/>. On rework calls
    /// the branch is checked out as-is (with the work-phase commits already
    /// on it) and the agent stacks new commits on top.
    /// Returns the agent's stdout for post-phase processing (e.g. question parsing).
    /// </summary>
    private async Task<string?> RunAgentPhaseAsync(
        WorkItem item,
        IAgentRunner runner,
        string repoId,
        string baseBranch,
        string branch,
        string prompt,
        bool isInitial,
        string? networkProfile,
        SandboxProfileFlavor sandboxFlavor,
        Project project,
        CancellationToken ct,
        CancellationToken hostShutdownToken,
        RequiredBuildPolicy buildFailurePolicy,
        int? iteration = null,
        IReadOnlyList<IAuditor>? auditorsForPreemptiveSelfReview = null,
        ReworkNoDiffHandling reworkNoDiffHandling = ReworkNoDiffHandling.TerminalError,
        string? resumePreTurnCommitSha = null,
        bool suppressNoChangesBreaker = false,
        // Delegation turns reuse this method with isInitial: false (existing
        // work branch checked out as-is) but must surface as "delegation" in
        // timings, streams, supervision, and prompt preprocessing instead of
        // "rework". Null keeps the legacy isInitial-derived labels.
        string? phaseLabelOverride = null,
        AgentPromptPhase? promptPhaseOverride = null)
    {
        item = await RefreshAgentTurnResumeCheckpointAsync(item, isInitial, iteration, ct);
        var resumingGitCheckpoint = !string.IsNullOrWhiteSpace(item.PreemptCheckpoint);
        var resumingRetainedSandbox = item.AgentTurnRecoveryLease is not null;
        var resumingPreempt = resumingGitCheckpoint || resumingRetainedSandbox;
        if (resumingGitCheckpoint && string.IsNullOrWhiteSpace(resumePreTurnCommitSha))
        {
            // A checkpoint may be created by the first member of an agent class
            // and consumed by a fallback member during this same pickup. That
            // route did not enter RunAsync with resume metadata, so resolve the
            // still-unmoved work-branch tip here.
            resumePreTurnCommitSha = await ResolveAgentTurnPreTurnCommitAsync(
                repoId,
                branch,
                baseBranch,
                ct);
        }
        var credential = await ResolveAgentCredentialAsync(runner.Kind, project, item, ct);
        var selectedMemberForSession = TryResolveSelectedMember(runner.Kind, project, item);
        var sessionTurnItem = selectedMemberForSession is null
            ? item
            : item with
            {
                AgentInstanceId = selectedMemberForSession.RouteKey,
                ModelId = selectedMemberForSession.ModelId,
                ReasoningMode = selectedMemberForSession.ReasoningMode,
            };
        var access = _gitHost.GetSandboxAccess(repoId);
        var agentPhase = phaseLabelOverride ?? (isInitial ? "work" : "rework");
        var promptPhase = promptPhaseOverride ?? (isInitial ? AgentPromptPhase.Work : AgentPromptPhase.Rework);

        // Look up the prompt revision snapshotted at iteration-dispatch time.
        // The orchestrator records this row before transitioning the item to
        // Working/Reworking; a concurrent PUT /workitems/{id}/prompt cannot
        // change what we read here.
        var dispatchIteration = isInitial ? AuditProgressIterationNumbers.WorkPhase : (iteration ?? AuditProgressIterationNumbers.WorkPhase);
        var promptRevisionAtDispatch = await ResolveIterationRevisionAsync(item, dispatchIteration, ct);

        var extraEnv = new Dictionary<string, string>
        {
            [PromptRevisionEnvVar] = promptRevisionAtDispatch.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var leasedSecrets = await ResolveLeasedProjectSecretsAsync(project, item, agentPhase, ct).ConfigureAwait(false);
        var spec = BuildSandboxSpec(WithLeaseBrokerHosts(access, leasedSecrets), includeAgentCredential: credential, allowAgentNetwork: true,
            hostNetworkProfile: networkProfile, timingWorkItemId: item.Id, timingPhase: agentPhase,
            flavor: sandboxFlavor, extraEnvironment: extraEnv,
            baselineImageRef: SandboxTargetResolver.BaselineRefForTarget(
                project,
                new SandboxTarget(networkProfile, sandboxFlavor),
                item.BaselineImageRef),
            includeAgentTurnScratchpadTmpfs: true,
            credentialRunner: runner,
            projectSecretEnvironment: leasedSecrets.Environment) with
        {
            RecoveryLease = item.AgentTurnRecoveryLease,
        };

        var durableTurnResume = item.AgentTurnResumeCheckpoint;
        var retainedPreparationClaimed = false;
        if (resumingRetainedSandbox)
        {
            if (durableTurnResume is null || !IsExactCheckpointRoute(item, runner, durableTurnResume))
            {
                throw new AgentInfrastructureFailureException(
                    runner.Kind,
                    agentPhase,
                    "The retained sandbox is bound to a different agent route; refusing cross-route adoption.");
            }

            item = await ClaimAgentTurnResumePreparationAsync(item, ct);
            durableTurnResume = item.AgentTurnResumeCheckpoint
                ?? throw new AgentTurnResumeClaimConflictException(
                    "Retained-sandbox preparation claim lost its agent-turn metadata.");
            retainedPreparationClaimed = true;
        }

        // Session-mode (Claude resumable worker) reuses ONE sandbox across the
        // work phase + every rework iteration: the VM is stopped during each
        // (long) audit and resumed for the next worker turn. The lifecycle
        // owns disposal (it disposes the VM via CloseSessionAsync at the end
        // of RunAsync), so this method must NOT dispose the sandbox in the
        // session branch — suspending it after a successful turn is what
        // preserves the prompt cache + transcript across the upcoming audit.
        var sessionLifecycle = _ambientSessionLifecycle.Value;
        var useClaudeSession = sessionLifecycle is not null
            && !sessionLifecycle.IsClosed
            && runner.Kind == AgentKind.Claude
            && sessionLifecycle.CanRunTurn(runner, sessionTurnItem)
            && !resumingPreempt;

        ISandbox sandbox;
        bool sandboxOwnedByPhase;
        bool skipClone;
        try
        {
            if (useClaudeSession)
            {
                // GetSandboxAsync resumes the VM via the worker's resume hook
                // (multipass start) when the lifecycle is currently suspended;
                // on the very first call it is already running.
                try
                {
                    sandbox = await sessionLifecycle!.GetSandboxAsync(ct);
                    sandboxOwnedByPhase = false;
                    // On subsequent worker turns (rework) the previous turn already
                    // cloned into /work; re-cloning would fail and would also throw
                    // away the agent's mid-tree scratch state. We refresh against
                    // origin via fetch + checkout below instead of cloning.
                    skipClone = sessionLifecycle.FirstTurnComplete;
                }
                catch (AgentSessionDegradedException ex)
                {
                    _log.LogWarning(ex,
                        "Claude session lifecycle degraded before phase '{Phase}' for work item {Id}; using the legacy fresh-sandbox path for this turn",
                        agentPhase, item.Id);
                    _ambientSessionLifecycle.Value = null;
                    sessionLifecycle = null;
                    useClaudeSession = false;
                    var sandboxStartSw = Stopwatch.StartNew();
                    sandbox = await AcquireWorkPhaseSandboxAsync(
                        item, agentPhase, credential?.Agent.Value, networkProfile, spec, ct);
                    sandboxStartSw.Stop();
                    CodeyBoxMeters.SandboxLifecycle.Record(sandboxStartSw.ElapsedMilliseconds, new KeyValuePair<string, object?>("step", "start"));
                    sandboxOwnedByPhase = true;
                    skipClone = false;
                }
            }
            else
            {
                // Legacy independent-phase path. WorkSandboxContext, when present
                // on the ambient AsyncLocal, lets the orchestrator reuse a warm
                // sandbox across the work + audit phases of the same item; the
                // wrapper it returns has a cheap DisposeAsync so the
                // sandboxOwnedByPhase finally below stays safe.
                if (sessionLifecycle is not null
                    && !sessionLifecycle.IsClosed
                    && runner.Kind == AgentKind.Claude
                    && !resumingPreempt
                    && !sessionLifecycle.CanRunTurn(runner, sessionTurnItem))
                {
                    await CloseAmbientClaudeSessionAsync(
                        sessionLifecycle,
                        item,
                        project,
                        "selected Claude fallback member does not match the opened session");
                    _ambientSessionLifecycle.Value = null;
                    sessionLifecycle = null;
                }
                var sandboxStartSw = Stopwatch.StartNew();
                // Name this wait for the sandbox-permit diagnostic (work item +
                // phase) so a slow admission gate is attributable instead of
                // silent. No release here: the reusable context never holds a
                // permit across this acquire (it disposes before recreating).
                using var phaseWaitScope = SandboxPermitWaitScope.Begin(item.Id.ToString(), agentPhase);
                sandbox = WorkSandboxContext.Current != null
                    ? await WorkSandboxContext.Current.GetOrCreateSandboxAsync(
                        spec,
                        ct,
                        acquireAsync: (s, token) => AcquireWorkPhaseSandboxAsync(
                            item, agentPhase, credential?.Agent.Value, networkProfile, s, token))
                    : await AcquireWorkPhaseSandboxAsync(
                        item, agentPhase, credential?.Agent.Value, networkProfile, spec, ct);
                sandboxStartSw.Stop();
                CodeyBoxMeters.SandboxLifecycle.Record(sandboxStartSw.ElapsedMilliseconds, new KeyValuePair<string, object?>("step", "start"));
                sandboxOwnedByPhase = true;
                skipClone = false;
            }
        }
        catch (Exception ex) when (
            resumingRetainedSandbox
            && ex is not OperationCanceledException
            && ex is not AgentTurnResumeClaimConflictException
            && ex is not AgentInfrastructureFailureException)
        {
            if (retainedPreparationClaimed)
                await TryReleaseAgentTurnPreparationClaimAsync(item, CancellationToken.None);
            throw new AgentInfrastructureFailureException(
                runner.Kind,
                agentPhase,
                "Retained-sandbox adoption could not be completed safely; the exact recovery lease remains preserved.",
                ex);
        }
        catch
        {
            if (retainedPreparationClaimed)
                await TryReleaseAgentTurnPreparationClaimAsync(item, CancellationToken.None);
            throw;
        }

        var useExactCheckpointResume = durableTurnResume is not null
            && IsExactCheckpointRoute(item, runner, durableTurnResume);
        var useCrossAgentFileOnlyResume = durableTurnResume is not null
            && !useExactCheckpointResume;
        var phaseSucceeded = false;
        AgentResult? successfulAgentResult = null;
        try
        {
            if (resumingRetainedSandbox)
            {
                var preserve = SandboxCapability.Find<IPreserveOnDisposeSandbox>(sandbox)
                    ?? throw new InvalidOperationException(
                        "A retained sandbox was adopted without preserve-on-dispose control.");

                // A retained VM is mutable recovery evidence, not a safe place
                // to launch another agent turn. Convert it to the ordinary
                // immutable Git/private-state checkpoint first. Preservation
                // stays armed across every preparation command so a host crash
                // can re-adopt the exact VM and repeat this conversion.
                await ConvertRetainedSandboxToCheckpointAsync(
                    item,
                    runner,
                    sandbox,
                    branch,
                    access.CloneUrlInsideSandbox,
                    isInitial,
                    iteration,
                    promptRevisionAtDispatch,
                    ct);

                // The database publication above atomically replaced the lease
                // with an immutable checkpoint. The retained VM is now only a
                // cleanup duplicate and may be deleted by phase disposal.
                preserve.DisablePreserveOnDispose();
                retainedPreparationClaimed = false;
                throw new AgentTurnCheckpointConvertedException(agentPhase);
            }

            if (credential is not null && credential.Files.Count > 0)
                await MaterialiseCredentialFilesAsync(sandbox, credential, ct);
            if (useClaudeSession && !resumingPreempt)
                await sessionLifecycle!.RefreshCredentialAsync(credential, ct);

            if (!skipClone)
            {
                TimingScope cloneScope = await TimingScope.BeginAsync(_timings, item.Id, agentPhase, "git.clone_into_sandbox",
                    activitySource: CodeyBoxActivities.Sandbox, log: _log);
                await using (cloneScope)
                {
                    await Run(sandbox, "git", "clone", access.CloneUrlInsideSandbox, SandboxConventions.WorkDir);
                }
                CodeyBoxMeters.SandboxLifecycle.Record(cloneScope.ElapsedMs, new KeyValuePair<string, object?>("step", "clone"));
            }
            else
            {
                // Session-mode: the prior clone is still
                // on disk. Refresh origin without cleaning its dirty work tree.
                await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "fetch", "origin");
                if (useClaudeSession && !resumingPreempt)
                    await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "remote", "set-url", "--push", "origin", access.CloneUrlInsideSandbox);
            }
            var checkedOutExistingBranch = false;
            if (resumingGitCheckpoint)
            {
                var preemptCheckpoint = item.PreemptCheckpoint!;
                var checkpointBranch = ValidatePreemptCheckpoint(item, preemptCheckpoint);
                await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "fetch", "origin", preemptCheckpoint);
                await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "checkout", "-B", branch, $"origin/{checkpointBranch}");
                if (durableTurnResume is not null)
                {
                    var typedRef = AgentTurnCheckpointRef.Parse(preemptCheckpoint);
                    var restoredHead = await ReadSandboxHeadShaAsync(sandbox, ct);
                    if (!string.Equals(restoredHead, typedRef.SourceCommitSha, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "Durable agent-turn checkpoint ref did not resolve to its content-bound source commit.");
                    }
                    if (useExactCheckpointResume && runner is IResumableAgentRunner)
                        await RestoreAgentTurnScratchpadArchiveAsync(item.Id, typedRef, sandbox, ct);
                    await MigrateAndRemoveLegacyScratchpadArchiveAsync(
                        sandbox,
                        migrateArchive: false,
                        ct);
                }
                else
                {
                    // Backward-read path for checkpoints created before private
                    // agent-turn archives were stored outside Git. Validate and
                    // atomically rematerialise the exact legacy regular file in
                    // tmpfs, then remove both legacy artifacts before the agent
                    // can inspect repository-controlled provider state.
                    await MigrateAndRemoveLegacyScratchpadArchiveAsync(
                        sandbox,
                        migrateArchive: true,
                        ct);
                }
                checkedOutExistingBranch = true;
                prompt = _promptComposer.BuildResumePrompt(prompt, preemptCheckpoint);
                if (useCrossAgentFileOnlyResume)
                {
                    // A fallback member may inherit the partial source tree but
                    // must not have another provider/account's CLI state
                    // restored into its home directory or left in its working
                    // tree for accidental consumption.
                    await RemovePreemptScratchpadFilesAsync(sandbox, ct);
                }
            }
            else if (isInitial)
            {
                if (await OriginBranchExistsAsync(sandbox, branch, ct))
                {
                    await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "checkout", "-B", branch, $"origin/{branch}");
                    checkedOutExistingBranch = true;
                }
                else
                    await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "checkout", "-B", branch, $"origin/{baseBranch}");
            }
            else
            {
                await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "checkout", "-B", branch, $"origin/{branch}");
                checkedOutExistingBranch = true;
            }
            var (gitName, gitEmail) = ResolveGitIdentity(project, _opts.HostGitIdentity, item.Initiator);
            await RunMasked(sandbox, "git", "-C", SandboxConventions.WorkDir, "config", "user.email", gitEmail);
            await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "config", "user.name", gitName);

            // Capture HEAD before the agent runs. The rework prompt explicitly
            // asks the agent to make new commits, so the agent may move HEAD
            // itself. We compare before/after to distinguish "agent committed"
            // from "agent did nothing" — both end with a clean working tree
            // but only the former is success.
            var beforeHead = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", SandboxConventions.WorkDir, "rev-parse", "HEAD"],
            }, ct);
            ThrowIfExecutionUnavailable(beforeHead);
            if (!beforeHead.Success)
                throw new InvalidOperationException($"Failed to read HEAD before agent: {beforeHead.Stderr}");
            var shaBefore = beforeHead.Stdout.Trim();
            var checkpointContainedMeaningfulChanges = false;
            if (resumingGitCheckpoint)
            {
                if (string.IsNullOrWhiteSpace(resumePreTurnCommitSha))
                    throw new InvalidOperationException("Durable agent-turn resume has no pre-turn source commit.");
                checkpointContainedMeaningfulChanges = await CheckpointContainsMeaningfulAgentChangesAsync(
                    sandbox,
                    resumePreTurnCommitSha,
                    shaBefore,
                    ct);
            }

            var canCaptureStructuredStream = await CanCaptureStructuredStreamAsync(runner, sandbox, agentPhase, ct);
            prompt = await ProcessAgentPromptAsync(
                item.Id,
                runner.Kind,
                promptPhase,
                iteration ?? 1,
                project,
                sandbox,
                prompt,
                ct);
            // The runner's CLI-native session resume capability is independent of
            // optional stream persistence: a transient agent crash should still be
            // recoverable in the same sandbox even when AgentStreams is disabled.
            // Force-enable the id-bearing output mode only when the runner's public
            // resume contract says its session-id extractor needs structured output.
            var needsStreamForResume = NeedsStructuredStreamForSessionResume(runner);
            var captureStructuredStream = canCaptureStructuredStream || needsStreamForResume;
            var useCheckpointResumeHook = resumingPreempt
                && runner is IResumableAgentRunner
                && (durableTurnResume is null || useExactCheckpointResume);

            // Session-mode worker VMs are opened once and reused across the
            // work + every rework iteration; the per-iteration extraEnv we
            // build above is applied to the legacy fresh-sandbox spec only,
            // so the session VM's environment does not carry the current
            // iteration's CODEYBOX_PROMPT_REVISION. The work/rework prompts
            // instruct the agent to copy the env var verbatim into the
            // CodeyBox-Prompt-Revision commit trailer — without the var,
            // the agent has an impossible instruction and the orchestrator
            // stamp would be papering over a noisy commit. Inline the
            // resolved literal so the session-mode agent writes the right
            // trailer regardless of env-var visibility inside the VM.
            if (useClaudeSession && !resumingPreempt)
                prompt = AppendSessionPromptRevisionDirective(prompt, promptRevisionAtDispatch);

            var streamCapture = (_agentStreams is not null && _agentStreams.Options.Enabled)
                ? await BeginAgentStreamCaptureAsync(item.Id, agentPhase, iteration ?? 1, ct)
                : null;
            var stdoutCallback = BuildStdoutCallback(item.Id, agentPhase, streamCapture);
            var supervision = await StartAgentSupervisionSessionAsync(
                item.Id,
                project,
                agentPhase,
                iteration ?? 1,
                runner,
                item.AgentInstanceId,
                item.ModelId,
                item.ReasoningMode,
                sandbox,
                SandboxConventions.WorkDir,
                source: "pipeline",
                ct);

            // Claim only after routing, availability/smoke gates, sandbox
            // preparation, capability checks, prompt preprocessing, stream
            // setup, and supervision setup have succeeded. The CAS is adjacent
            // to the resumed CLI dispatch so unavailable pickups do not consume
            // the bounded resume budget, and a second host cannot run the same
            // checkpoint concurrently.
            try
            {
                if (durableTurnResume is not null)
                {
                    item = await ClaimAgentTurnResumeDispatchAsync(item, ct);
                    durableTurnResume = item.AgentTurnResumeCheckpoint
                        ?? throw new AgentTurnResumeClaimConflictException(
                            "Durable agent-turn resume checkpoint disappeared after its dispatch claim.");
                }
            }
            catch
            {
                if (streamCapture is not null)
                    await streamCapture.DisposeAsync();
                if (supervision is not null)
                    await supervision.DisposeAsync();
                throw;
            }

            AuditLog.AgentStarted(runner.Kind, sandbox.Id, agentPhase);
            var agentSw = Stopwatch.StartNew();
            var agentExecScope = await TimingScope.BeginAsync(
                _timings, item.Id, agentPhase, "agent.exec",
                metadata: new Dictionary<string, object>
                {
                    ["agent"] = runner.Kind.Value,
                    ["resuming_preempt"] = resumingPreempt,
                },
                log: _log,
                activitySource: CodeyBoxActivities.Pipeline);

            AgentResult agentResult;
            using var runnerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var preemptRequested = false;
            var preemptCaptureQuiesced = true;
            var supervisionHandledRun = supervision is not null && !resumingPreempt;
            try
            {
                await using (agentExecScope)
                {
                    if (supervisionHandledRun)
                    {
                        var phaseForPreprocessor = promptPhase;
                        var iterationForPreprocessor = iteration ?? 1;
                        Func<string, CancellationToken, Task<string>> promptPreprocessor = (raw, pct) => ProcessAgentPromptAsync(
                            item.Id,
                            runner.Kind,
                            phaseForPreprocessor,
                            iterationForPreprocessor,
                            project,
                            sandbox,
                            raw,
                            pct);
                        var runTask = useClaudeSession
                            ? RunClaudeSessionSupervisedTurnsAsync(
                                sessionLifecycle!,
                                supervision!,
                                prompt,
                                stdoutCallback,
                                promptPreprocessor,
                                runnerCts.Token)
                            : AgentSupervisionTurnRunner.RunAutonomousAndQueuedInjectionsAsync(
                                runner,
                                sandbox,
                                SandboxConventions.WorkDir,
                                prompt,
                                credential,
                                item.ModelId,
                                item.ReasoningMode,
                                supervision!,
                                stdoutCallback,
                                captureStructuredStream,
                                promptPreprocessor,
                                runnerCts.Token);
                        var completed = await Task.WhenAny(runTask, WaitForCancellationAsync(hostShutdownToken));
                        if (completed != runTask)
                        {
                            preemptRequested = true;
                            preemptCaptureQuiesced = await RequestAgentPreemptWithDeadlineAsync(
                                runner, sandbox, SandboxConventions.WorkDir, ct);
                            completed = await Task.WhenAny(runTask, Task.Delay(_opts.AgentPreemptDrain, ct));
                            if (completed != runTask)
                                await runnerCts.CancelAsync();
                        }

                        agentResult = await runTask;
                        if (preemptRequested)
                            throw new OperationCanceledException(hostShutdownToken);
                    }
                    else
                    {
                        stdoutCallback = WrapSupervisionStdout(supervision, stdoutCallback);
                        if (supervision is not null)
                            await supervision.PublishCodeyBoxCommandAsync("autonomous", prompt, injectionId: null, runnerCts.Token);

                        // Session-mode work / rework turn: route the agent invocation
                        // through the lifecycle so the captured CLI session id flows
                        // across turns (--resume on turn 2+) and the per-turn cache_read
                        // metrics get emitted. The lifecycle forces stream-json on so
                        // the worker can observe the session id; the captureStructuredStream
                        // value we pass into RunAsync is irrelevant when useClaudeSession.
                        var runTask = useClaudeSession && !resumingPreempt
                            ? sessionLifecycle!.SendTurnAsync(prompt, runnerCts.Token, stdoutCallback)
                            : (useCheckpointResumeHook && runner is IResumableAgentRunner resumable
                                ? resumable.RunResumedAsync(
                                    sandbox, SandboxConventions.WorkDir, prompt, credential,
                                    new AgentResumeContext(
                                        item.PreemptCheckpoint
                                            ?? throw new InvalidOperationException(
                                                "Resumed agent dispatch requires its immutable checkpoint ref."),
                                        ScratchpadArchivePath: SandboxConventions.AgentTurnScratchpadArchivePath,
                                        NativeSessionId: useExactCheckpointResume
                                            ? durableTurnResume!.NativeSessionId
                                            : null),
                                    item.ModelId, item.ReasoningMode, runnerCts.Token,
                                    stdoutChunkCallback: stdoutCallback)
                                : runner.RunAsync(sandbox, SandboxConventions.WorkDir, prompt, credential, item.ModelId, item.ReasoningMode, runnerCts.Token,
                                    stdoutChunkCallback: stdoutCallback,
                                    captureStructuredStream: captureStructuredStream));
                        var completed = await Task.WhenAny(runTask, WaitForCancellationAsync(hostShutdownToken));
                        if (completed != runTask)
                        {
                            preemptRequested = true;
                            preemptCaptureQuiesced = await RequestAgentPreemptWithDeadlineAsync(
                                runner, sandbox, SandboxConventions.WorkDir, ct);
                            completed = await Task.WhenAny(runTask, Task.Delay(_opts.AgentPreemptDrain, ct));
                            if (completed != runTask)
                                await runnerCts.CancelAsync();
                        }

                        agentResult = await runTask;
                        if (preemptRequested)
                            throw new OperationCanceledException(hostShutdownToken);

                        if (supervision is not null)
                        {
                            var dispatcher = new SupervisedTurnDispatcher(
                                runner, sandbox, SandboxConventions.WorkDir, credential,
                                item.ModelId, item.ReasoningMode, stdoutCallback,
                                captureStructuredStream: captureStructuredStream,
                                promptPreprocessor: (raw, pct) => ProcessAgentPromptAsync(
                                    item.Id, runner.Kind,
                                    promptPhase,
                                    iteration ?? 1, project, sandbox, raw, pct));
                            agentResult = await supervision.RunPendingInjectionsAsync(
                                agentResult, dispatcher.RunInjectionTurnAsync, runnerCts.Token);
                        }
                    }
                }
            }
            catch (AgentSessionResumeExhaustedException ex)
            {
                await TryCheckpointRecoverableAgentTurnAsync(
                    item,
                    runner,
                    sandbox,
                    branch,
                    ex.LastResult,
                    isInitial,
                    iteration,
                    promptRevisionAtDispatch);
                throw;
            }
            catch (OperationCanceledException) when (hostShutdownToken.IsCancellationRequested)
            {
                if (streamCapture is not null)
                    await streamCapture.DisposeAsync();

                // R8-core: if SandboxShutdownTeardownService already took ownership
                // of this VM during IHostedLifecycleService.StoppingAsync (which runs
                // and completes BEFORE BackgroundService cancellation flows down as
                // hostShutdownToken), either Suspend is preserving the frozen VM for
                // SandboxResumeOnStartupService or Dispose is destroying the VM. The
                // preempt-checkpoint flow would block on a frozen VM or fault against
                // a deleted VM. Skip both the checkpoint and StopAndPreserveAsync in
                // those lifecycle-owned cases.
                //
                // The signal is "did the shutdown teardown handler take ownership
                // of this VM", NOT just ISuspendableSandbox.IsSuspended: the handler
                // persists SuspendedVmName BEFORE awaiting multipass suspend, and on
                // a per-VM suspend timeout it returns with the mapping still
                // persisted while IsSuspended is left false (multipassd is still
                // writing the RAM snapshot). Gating only on IsSuspended would let
                // the legacy git-checkpoint + multipass-stop path race that
                // in-flight suspend. Dispose mode sets the ownership flag before
                // destroying the VM because in-VM checkpoint commands would fault
                // after lifecycle teardown. Stop mode sets the flag only after a
                // successful stop/preserve, and only for items whose state can
                // recover without PipelineRunner creating a new preempt checkpoint.
                // We re-read the store under CancellationToken.None (ct is already
                // cancelled by host shutdown): on the per-VM suspend-timeout path the handler has
                // persisted SuspendedVmName and returned while multipassd is still
                // writing the snapshot and IsSuspended / IsOwnedByShutdownHandler may
                // still be false on the sandbox instance, so the persisted mapping is
                // the authoritative late signal.
                var lifecycleHandled = sandbox is IShutdownTeardownSandbox teardownSandbox
                    && teardownSandbox.IsOwnedByShutdownHandler;
                if (!lifecycleHandled)
                {
                    var persisted = await _store.GetAsync(item.Id, CancellationToken.None);
                    lifecycleHandled = !string.IsNullOrEmpty(persisted?.SuspendedVmName);
                }
                if (lifecycleHandled)
                {
                    _log.LogInformation(
                        "Work item {Id}: sandbox {SandboxId} was taken over by SandboxShutdownTeardownService; skipping preempt-checkpoint and preserve to avoid racing the frozen, stopped, or disposed VM",
                        item.Id, sandbox.Id);
                    throw;
                }

                Exception? checkpointFailure = null;
                if (!preemptCaptureQuiesced)
                {
                    checkpointFailure = new TimeoutException(
                        "The agent preempt capture did not terminate after cancellation; refusing to race it with checkpoint publication.");
                    _log.LogError(
                        "Preempt capture remained active for work item {Id}; preserving sandbox without publishing a checkpoint",
                        item.Id);
                }
                else
                {
                    try
                    {
                        using var checkpointCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        checkpointCts.CancelAfter(_opts.PreemptCheckpointDrain);
                        var current = await _store.GetAsync(item.Id, checkpointCts.Token) ?? item;
                        var previous = current.AgentTurnResumeCheckpoint;
                        var turnCheckpoint = CreateAgentTurnResumeCheckpoint(
                            item,
                            current,
                            runner,
                            previous is not null && IsExactCheckpointRoute(item, runner, previous)
                                ? previous.NativeSessionId
                                : null,
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
                            turnCheckpoint);
                    }
                    catch (Exception ex)
                    {
                        checkpointFailure = ex;
                        _log.LogError(ex, "Preempt checkpoint failed for work item {Id}; preserving sandbox for operator recovery", item.Id);
                    }
                }

                Exception? preserveFailure = null;
                if (sandbox is IPreemptibleSandbox preemptible)
                {
                    using var preserveCts = new CancellationTokenSource(_opts.SandboxPreserveDrain);
                    try
                    {
                        await preemptible.StopAndPreserveAsync(preserveCts.Token);
                    }
                    catch (OperationCanceledException ex)
                    {
                        preserveFailure = ex;
                        _log.LogWarning(
                            "Timed out preserving sandbox {SandboxId} for work item {Id} after {Timeout}",
                            sandbox.Id, item.Id, _opts.SandboxPreserveDrain);
                    }
                    catch (Exception ex)
                    {
                        preserveFailure = ex;
                        _log.LogWarning(ex,
                            "Failed preserving sandbox {SandboxId} for work item {Id} during host shutdown; leaving the checkpointed item recoverable and the VM for operator cleanup",
                            sandbox.Id, item.Id);
                    }
                }

                if (checkpointFailure is not null)
                    throw new OperationCanceledException("Host shutdown interrupted work, but the preempt checkpoint could not be created.", checkpointFailure, hostShutdownToken);
                if (preserveFailure is not null)
                    throw new OperationCanceledException("Host shutdown interrupted work and created a preempt checkpoint, but preserving the sandbox failed.", preserveFailure, hostShutdownToken);

                throw;
            }
            finally
            {
                if (streamCapture is not null && !preemptRequested)
                    await streamCapture.DisposeAsync();
                if (supervision is not null)
                    await supervision.DisposeAsync();
            }
            CodeyBoxMeters.AgentDuration.Record(agentExecScope.ElapsedMs,
                new KeyValuePair<string, object?>("agent.kind", runner.Kind.Value),
                new KeyValuePair<string, object?>("phase", agentPhase));

            var agentEndedAt = DateTimeOffset.UtcNow;
            var observedModelId = ResolveObservedModelId(runner, item.ModelId);
            var agentStartedAt = agentEndedAt.AddMilliseconds(-agentExecScope.ElapsedMs);
            if (!canCaptureStructuredStream)
                await _auditorTelemetry.EmitToolCallCountsAsync(runner.Kind, agentResult.Stdout, item.Id, agentPhase, agentExecScope.ElapsedMs, ct);
            await TryRecordCostAsync(agentResult.Stdout, agentResult.Stderr,
                runner.Kind, item.AgentInstanceId, item.Id, agentPhase, iteration, agentStartedAt, agentEndedAt, observedModelId);
            agentSw.Stop();
            AgentFailureClassification? availabilityFailureClassification = null;
            if (_availability is { } regOnFinish)
            {
                availabilityFailureClassification = await RecordAvailabilityOutcomeAsync(
                    regOnFinish,
                    runner,
                    agentResult,
                    agentSw.Elapsed,
                    item,
                    project,
                    sandbox.Id,
                    agentPhase);
            }
            AuditLog.AgentFinished(runner.Kind, sandbox.Id, agentResult.Success, null, agentSw.Elapsed,
                stdoutTail: Tail(agentResult.Stdout), stderrTail: Tail(agentResult.Stderr));
            // Always log a truncated tail of agent output, regardless of
            // success. This is critical when an agent finishes "successfully"
            // but produces no useful diff — without this log, we have no
            // visibility into what the agent reasoned.
            LogAgentOutput(_log, runner.Kind, agentResult);
            AgentAuthFailureDetection? deferredSuccessAuthDetection = null;
            if (agentResult.Success)
            {
                var authDetection = _authFailureClassifier.DetectDetailed(
                    runner.Kind,
                    agentResult.Stderr,
                    agentResult.Stdout);
                if (authDetection is { Classification.Kind: AgentFailureKind.AuthRequired })
                {
                    // Defer success-path auth handling until after the diff/HEAD
                    // check. Stdout-only login transcripts should not bench a run
                    // that actually changed files, and no-diff stderr/auth text
                    // needs the same in-VM corroboration guard before global
                    // auth-required side effects are published.
                    deferredSuccessAuthDetection = authDetection;
                }
            }
            if (!agentResult.Success)
            {
                // Same policy as the success branch: a nonzero CLI can also
                // print the login prompt on stdout before exiting. Require
                // forced in-VM probe corroboration before publishing the
                // global bench — the auth-failure detector matches a CLI-
                // login-shaped substring, and a nonzero work-phase exit
                // whose stderr is a generic CLI failure with stdout
                // containing one OAuth-callback URL line would otherwise
                // bench the agent fleet-wide on model-controllable evidence.
                await ThrowIfAuthRequiredOutputAsync(
                    item, project, runner.Kind, agentPhase, agentResult,
                    requireStdoutOnlyCorroboration: true,
                    ct: ct);

                await ThrowIfAuthErrorAgentFailureAsync(
                    item,
                    project,
                    runner,
                    agentResult,
                    agentPhase,
                    availabilityFailureClassification,
                    ct);

                // Per-provider detector (registered as IQuotaFailureClassifier) inspects
                // stderr/stdout and structured stream events. Per-CLI classification +
                // reset-window parsing now live in the per-provider library.
                var resolvedFailureClassification = availabilityFailureClassification
                    ?? _authFailureClassifier.ClassifyFailure(runner, agentResult);
                var quotaClassification = _quotaClassifier.Classify(runner.Kind, agentResult.Stderr, agentResult.Stdout);
                var detection = quotaClassification.Detection;
                var canDurablyResumeFailure = detection is not null
                    || resolvedFailureClassification.Kind == AgentFailureKind.TransientNetwork
                    || resolvedFailureClassification.Kind == AgentFailureKind.Infrastructure
                        && agentResult.ExecutionUnavailable
                    || AgentSuspendResilience.IsInfrastructureProcessExitCode(
                        AgentSuspendResilience.ParseAgentExitCode(agentResult.Summary));
                if (canDurablyResumeFailure)
                {
                    await TryCheckpointRecoverableAgentTurnAsync(
                        item,
                        runner,
                        sandbox,
                        branch,
                        agentResult,
                        isInitial,
                        iteration,
                        promptRevisionAtDispatch);
                }

                _quotaAuditEmitter.EmitAdvisoryAuditEvents(
                    runner.Kind, agentResult.Stderr, agentResult.Stdout, agentPhase, sandbox.Id);
                // Only genuine quota/rate-limit signals take the quota path. A
                // 401/403 (Unauthorized) never clears on a quota window: it must
                // fall through to the auth handling above/below, never bench the
                // member via MarkExhausted or park the item for a reset that
                // will never arrive. RecordIfQuotaFailureAsync applies the same
                // narrowing before touching the observed-failure store.
                if (detection is { Kind: var detectedKind } && detectedKind.IsExhaustionSignal())
                {
                    // The quota record, fallback routing, and park signals below
                    // must fire exactly as before for every detection: peers
                    // still need their fallback and the probe its write-back.
                    // Whether the evidence was provider-owned travels on the
                    // exception, so the park transition can veto parks that
                    // rest only on agent-quotable stdout evidence against a
                    // healthy probe — without disturbing fallback behavior.
                    await _quotaClassifier.RecordIfQuotaFailureAsync(
                        _quotaFailures,
                        runner.Kind,
                        observedModelId,
                        agentResult.Summary,
                        agentResult.Stderr,
                        agentEndedAt,
                        _auditQuotaOptions.ObservedFailureRetention,
                        ct,
                        projectId: item.ProjectId,
                        stdout: agentResult.Stdout);

                    var quotaKind = detection?.Kind ?? QuotaFailureKind.RateLimitExceeded;
                    throw new TerminalQuotaError(quotaKind,
                        QuotaFailureMessage(
                            quotaKind,
                            $"Agent {runner.Kind} reported quota failure",
                            SanitizedAgentDetail.FromRaw(agentResult.Summary)),
                        detection?.ResetAt,
                        providerSurfaceMatch: quotaClassification.ProviderSurfaceMatch);
                }

                ThrowIfTransientAgentFailure(runner, agentResult, agentPhase);
                var agentExitCode = AgentSuspendResilience.ParseAgentExitCode(agentResult.Summary);
                if (AgentSuspendResilience.IsInfrastructureProcessExitCode(agentExitCode))
                {
                    throw new AgentInfrastructureFailureException(
                        runner.Kind,
                        agentPhase,
                        BuildAgentFailureDetail(
                            $"Agent {runner.Kind} was terminated by infrastructure (exit {agentExitCode})",
                            agentResult,
                            _opts.MaxFailureDetailBytes));
                }
                ThrowIfInfrastructureAgentFailure(
                    runner,
                    agentResult,
                    agentPhase,
                    $"Agent {runner.Kind} reported failure",
                    availabilityFailureClassification);

                await _quotaClassifier.RecordIfQuotaFailureAsync(
                    _quotaFailures,
                    runner.Kind,
                    observedModelId,
                    agentResult.Summary,
                    agentResult.Stderr,
                    agentEndedAt,
                    _auditQuotaOptions.ObservedFailureRetention,
                    ct,
                    projectId: item.ProjectId,
                    stdout: agentResult.Stdout);

                // Redact and truncate agent-controlled output before it reaches
                // LastError, audit persistence, webhooks, or API responses via the
                // exception message chain.
                var detail = BuildAgentFailureDetail($"Agent {runner.Kind} reported failure", agentResult, _opts.MaxFailureDetailBytes);
                throw new InvalidOperationException(detail);
            }

            successfulAgentResult = agentResult;

            if (resumingPreempt)
                await RemovePreemptScratchpadFilesAsync(sandbox, ct);

            // Stage anything the agent left dirty in the working tree. If the
            // agent already committed (per the rework prompt's instruction
            // to make new commits), `git add -A` is a no-op.
            await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "add", "-A");

            // Read the suggestions file BEFORE stripping it from the staged tree
            // so we capture it even when the agent staged it alongside real changes.
            // Only the work phase (isInitial) emits suggestions; rework does not.
            string? suggestionsJson = null;
            if (isInitial)
                suggestionsJson = await TryReadSuggestionsFileAsync(sandbox, ct);

            // Read the no-action-required report BEFORE stripping it, for the
            // same reason. Only the initial work phase can resolve to
            // NoActionRequired; rework empty diffs keep the converge-aware
            // audit-loop handling.
            string? noActionRequiredJson = null;
            if (isInitial)
                noActionRequiredJson = await TryReadNoActionRequiredFileAsync(sandbox, ct);

            // Strip suggestions.json from the staged tree so it is never committed
            // to the work branch, regardless of whether the agent staged it.
            // Use separate argv so ProcessSandbox translates the -C path correctly.
            // Ignore the exit code: git rm --cached exits 128 when the file is not tracked.
            await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", SandboxConventions.WorkDir, "rm", "--cached", "--",
                ".codeybox/suggestions.json"],
            }, ct);

            // Strip the no-action-required protocol file the same way: it must
            // never land on the work branch, whether or not this run resolves
            // to NoActionRequired.
            await StripNoActionRequiredFileFromIndexAsync(sandbox, ct);

            // Strip CodeyBox's internal agent-log scratch dir from the staged tree
            // so it is never committed to the work branch and pushed in the PR.
            await StripAgentLogScratchFromIndexAsync(sandbox, ct);
            await StripReservedScratchpadPathsFromIndexAsync(sandbox, ct);

            var staged = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", SandboxConventions.WorkDir, "diff", "--cached", "--quiet"],
            }, ct);
            ThrowIfExecutionUnavailable(staged);
            // diff --cached --quiet exits 0 on no-diff, 1 on diff.
            var hasStagedDiff = staged.ExitCode != 0;

            if (hasStagedDiff)
            {
                var trailerBlock = await ComposeCommitTrailerBlockAsync(item.Id, runner.Kind, observedModelId, ct,
                    promptRevisionAtDispatch: promptRevisionAtDispatch);
                var commitMessage = isInitial
                    ? $"codeybox: {item.Title}\n\n{trailerBlock}"
                    : $"codeybox rework: address audit findings\n\n{trailerBlock}";
                await using (var commitScope = await TimingScope.BeginAsync(_timings, item.Id, agentPhase, "git.commit",
                    activitySource: CodeyBoxActivities.Sandbox, log: _log))
                {
                    await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "commit", "-m", commitMessage);
                }
            }
            await EnsureReservedScratchpadPathsAbsentFromTreeAsync(sandbox, ct);

            // Did HEAD advance — either via the agent committing itself or
            // via our just-now commit?
            var afterHead = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", SandboxConventions.WorkDir, "rev-parse", "HEAD"],
            }, ct);
            ThrowIfExecutionUnavailable(afterHead);
            if (!afterHead.Success)
                throw new InvalidOperationException($"Failed to read HEAD after agent: {afterHead.Stderr}");
            var shaAfter = afterHead.Stdout.Trim();
            var hasMeaningfulAgentChanges = await HasMeaningfulAgentChangesAsync(
                sandbox,
                shaBefore,
                shaAfter,
                ct);
            if (!hasMeaningfulAgentChanges)
            {
                if (deferredSuccessAuthDetection is not null)
                {
                    // Audit rework clean-exit/no-diff is the ambiguous empty
                    // result this policy exists to disambiguate. A matched
                    // captured auth signature is infra, so publish the
                    // availability exclusion before throwing and let class
                    // fallback reroute the item. Other phases retain the
                    // existing stdout / built-in stderr corroboration guard.
                    var isAuditEmptyRework = !isInitial
                        && reworkNoDiffHandling == ReworkNoDiffHandling.AuditEmptyRework;
                    var matchedConfiguredStderrPattern =
                        deferredSuccessAuthDetection.MatchedConfiguredStderrPattern;
                    // Captured rework output is agent-controlled even when it
                    // arrived on stderr. It can classify this attempt as auth
                    // required so the item reroutes, but global availability
                    // benching requires runner-owned in-VM corroboration.
                    await HandleAuthRequiredDetectionAsync(
                        item,
                        project,
                        runner.Kind,
                        agentPhase,
                        deferredSuccessAuthDetection.Classification,
                        throwOnMatch: true,
                        stdoutOnlyEvidence: deferredSuccessAuthDetection.IsStdoutOnly,
                        requireStdoutOnlyCorroboration: true,
                        requireAuthCorroboration: isAuditEmptyRework
                            || !deferredSuccessAuthDetection.IsStdoutOnly
                                && !matchedConfiguredStderrPattern,
                        matchedConfiguredPattern: deferredSuccessAuthDetection.MatchedConfiguredStderrPattern
                            || deferredSuccessAuthDetection.MatchedConfiguredStdoutPattern,
                        ct: ct);
                }

                try
                {
                    if (isInitial)
                    {
                        await ThrowIfNoDiffTerminalDiagnosticQuotaFailureAsync(
                            item,
                            runner.Kind,
                            observedModelId,
                            agentResult,
                            agentPhase,
                            sandbox.Id,
                            agentEndedAt,
                            ct);
                    }
                    else
                    {
                        await ThrowIfNoDiffReworkQuotaFailureAsync(
                            item,
                            project,
                            runner.Kind,
                            observedModelId,
                            agentResult,
                            agentPhase,
                            sandbox.Id,
                            agentEndedAt,
                            ct);

                        await ThrowIfNoDiffReworkCapturedAuthErrorAsync(
                            item,
                            project,
                            runner.Kind,
                            agentPhase,
                            agentResult,
                            ct);

                        await ThrowIfNoDiffTerminalAuthDiagnosticAsync(
                            item,
                            project,
                            runner.Kind,
                            agentPhase,
                            agentResult.TerminalDiagnostic,
                            ct);
                    }
                }
                catch (TerminalQuotaError)
                {
                    await TryCheckpointRecoverableAgentTurnAsync(
                        item,
                        runner,
                        sandbox,
                        branch,
                        agentResult with
                        {
                            Success = false,
                            Summary = "agent ended without changes after a recognized quota failure",
                        },
                        isInitial,
                        iteration,
                        promptRevisionAtDispatch,
                        failureAlreadyClassified: true);
                    throw;
                }

                if (resumingPreempt && checkpointContainedMeaningfulChanges)
                {
                    await using (var pushScope = await TimingScope.BeginAsync(_timings, item.Id, agentPhase, "git.push_resumed_checkpoint_to_bare_repo",
                        activitySource: CodeyBoxActivities.Sandbox, log: _log))
                    {
                        await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "push", "origin", $"HEAD:{branch}");
                    }
                    await sandbox.SyncStateToHostAsync(ct);
                    // HEAD is now durable on the work branch. A later build or
                    // probe infrastructure failure must retry from that branch,
                    // not replay the older source/session checkpoint.
                    await ClearPreemptAsync(item, CancellationToken.None);

                    if (isInitial && suggestionsJson is not null)
                        await PickUpSuggestionsAsync(item, project, suggestionsJson, ct);

                    await _requiredBuildGate.EnforceForWorkPhaseAsync(item, project, repoId, baseBranch, branch, agentPhase, buildFailurePolicy, ct);
                    phaseSucceeded = true;
                    return agentResult.Stdout;
                }

                var buildOutcome = RequiredBuildWorkPhaseOutcome.PassedOrSkipped;
                if (checkedOutExistingBranch)
                {
                    buildOutcome = await _requiredBuildGate.EnforceForWorkPhaseAsync(
                        item, project, repoId, baseBranch, branch, agentPhase, buildFailurePolicy, ct);
                }

                if (buildOutcome == RequiredBuildWorkPhaseOutcome.DeferredFailure)
                    return agentResult.Stdout;

                // Feed the no-changes circuit breaker: a clean-exit-but-no-diff
                // outcome is the silent-failure signature an agent exhibits when
                // it's broken in a way the fast-fail breaker (non-zero exit only)
                // cannot see — auth collapse, capability collapse, or a failure
                // mode whose signature isn't recognised yet. After N consecutive
                // DISTINCT work items the agent is excluded; the same item
                // retried doesn't advance the counter. Suppressed for rework
                // passes driven by zero blocking findings or by a verdict that
                // never completed: with nothing (finished) to fix, an empty
                // diff is the correct outcome, not a silent failure.
                //
                // An explicit no-action-required report short-circuits BEFORE
                // the breaker feed: the agent deliberately determined that no
                // action is warranted (conditional item, precondition unmet),
                // which is a terminal resolution — not a silent failure — so
                // it must neither bench the agent nor fail the item. Only the
                // initial work phase resolves this way; rework keeps the
                // converge-aware handling below.
                if (isInitial && noActionRequiredJson is not null)
                {
                    var noActionReport = NoActionRequiredFileParser.Parse(noActionRequiredJson, _log);
                    if (noActionReport is not null)
                        throw new NoActionRequiredException(
                            runner.Kind,
                            noActionReport.Reason,
                            noActionReport.Precondition);
                }

                if (!suppressNoChangesBreaker)
                    await RecordNoChangesOutcomeAsync(runner.Kind, item, project);

                if (isInitial)
                {
                    // Initial work phase stays fail-fast: there is no audit /
                    // rework loop sitting behind it to converge a "declined to
                    // work" outcome. Same shape as before this change. (A valid
                    // no-action-required report already threw above.)
                    throw new InvalidOperationException("Agent produced no changes to commit");
                }

                if (reworkNoDiffHandling == ReworkNoDiffHandling.AuditEmptyRework)
                {
                    // Audit rework: surface the empty-diff outcome via a typed
                    // exception the audit/rework loop catches. The loop applies
                    // converge-aware handling instead of terminal-failing the item
                    // on the first empty pass.
                    throw new ReworkProducedNoChangesException(
                        runner.Kind,
                        message: "Rework agent produced no changes");
                }

                // Non-audit rework callers keep the pre-existing terminal error
                // contract; only the audit loop owns the converge-aware policy.
                throw new InvalidOperationException("Rework agent produced no changes; cannot resolve audit findings");
            }

            if (deferredSuccessAuthDetection is { IsStdoutOnly: false })
            {
                await HandleAuthRequiredDetectionAsync(
                    item,
                    project,
                    runner.Kind,
                    agentPhase,
                    deferredSuccessAuthDetection.Classification,
                    throwOnMatch: true,
                    stdoutOnlyEvidence: false,
                    matchedConfiguredPattern: deferredSuccessAuthDetection.MatchedConfiguredStderrPattern
                        || deferredSuccessAuthDetection.MatchedConfiguredStdoutPattern,
                    ct: ct);
            }

            // HEAD advanced: this run produced real changes. Clear the
            // no-changes streak so an isolated empty-diff before this success
            // is forgotten — only CONSECUTIVE no-changes signal a broken agent.
            _availability?.RecordChangesProduced(runner.Kind);

            // Stamp the CodeyBox trailers on HEAD if the agent forgot to emit them.
            // The dispatch revision is orchestrator-owned state — delegating it to
            // the agent's commit-hygiene is unreliable in practice, and a missing
            // trailer would block the post-work audit on a purely mechanical
            // triviality. Skipped when the operator updated the prompt
            // mid-iteration so the auditor still surfaces the stale-prompt signal.
            await EnsureHeadCarriesPromptRevisionTrailerAsync(
                sandbox, item, runner.Kind, observedModelId,
                promptRevisionAtDispatch, agentPhase, ct);

            await using (var pushScope = await TimingScope.BeginAsync(_timings, item.Id, agentPhase, "git.push_back_to_bare_repo",
                activitySource: CodeyBoxActivities.Sandbox, log: _log))
            {
                await PushSandboxWorkBranchWithReconcileAsync(sandbox, branch, ct);
            }

            await sandbox.SyncStateToHostAsync(ct);
            if (resumingPreempt)
            {
                // The resumed agent completed and its resulting tree is durable.
                // Clear stale turn/session state before post-agent verification.
                await ClearPreemptAsync(item, CancellationToken.None);
            }

            // Pick up suggestions after the sandbox pushes; sandbox is still alive here.
            if (isInitial && suggestionsJson is not null)
                await PickUpSuggestionsAsync(item, project, suggestionsJson, ct);

            // Session-path enhancement (config-gated, default OFF): inject ONE
            // pre-emptive self-review turn in the SAME warm session right after
            // the initial work commit lands, BEFORE the formal audit fires in
            // its own fresh sandbox. The turn uses the runtime-composed
            // checklist from the project's active auditors. The auditor is
            // intentionally NOT informed of this turn — pass/fail still belongs
            // to the separate fresh-sandbox audit pipeline (item 3 of the
            // session brief is non-negotiable on auditor isolation).
            if (useClaudeSession
                && isInitial
                && !resumingPreempt
                && _claudeSessionOptions.PreemptiveSelfReviewEnabled
                && auditorsForPreemptiveSelfReview is not null
                && auditorsForPreemptiveSelfReview.Count > 0)
            {
                await TryRunPreemptiveSelfReviewTurnAsync(
                    sessionLifecycle!,
                    sandbox,
                    item,
                    project,
                    runner,
                    branch,
                    auditorsForPreemptiveSelfReview,
                    promptRevisionAtDispatch,
                    ct);
            }

            await _requiredBuildGate.EnforceForWorkPhaseAsync(item, project, repoId, baseBranch, branch, agentPhase, buildFailurePolicy, ct);

            phaseSucceeded = true;
            return agentResult.Stdout;
        }
        catch (AgentResumePreparationUnavailableException ex)
        {
            await TryRefundUndispatchedAgentTurnClaimAsync(item, CancellationToken.None);
            throw new AgentInfrastructureFailureException(
                runner.Kind,
                agentPhase,
                ex.ExitCode is { } exitCode
                    ? $"Sandbox execution became unavailable while preparing the checkpointed {agentPhase} turn (exit {exitCode})."
                    : $"Sandbox execution became unavailable while preparing the checkpointed {agentPhase} turn.");
        }
        catch (SandboxExecutionUnavailableException ex)
        {
            if (successfulAgentResult is not null)
            {
                var interruptedResult = successfulAgentResult with
                {
                    Success = false,
                    Summary = "sandbox execution became unavailable while preserving completed agent work",
                    ExecutionUnavailable = true,
                };
                await TryCheckpointRecoverableAgentTurnAsync(
                    item,
                    runner,
                    sandbox,
                    branch,
                    interruptedResult,
                    isInitial,
                    iteration,
                    promptRevisionAtDispatch);
            }
            throw new AgentInfrastructureFailureException(
                runner.Kind,
                agentPhase,
                successfulAgentResult is null
                    ? $"Sandbox execution became unavailable before the {agentPhase} agent could run (exit {ex.ExitCode})."
                    : $"Sandbox execution became unavailable after agent exit while preserving the {agentPhase} work tree (exit {ex.ExitCode}).");
        }
        catch (SandboxCredentialFileWriteException ex) when (ex.ExecutionUnavailable)
        {
            throw new AgentInfrastructureFailureException(
                runner.Kind,
                agentPhase,
                $"Sandbox execution became unavailable while materialising credentials for {agentPhase} (exit {ex.ExitCode}).");
        }
        catch (Exception ex) when (
            resumingRetainedSandbox
            && ex is not OperationCanceledException
            && ex is not AgentTurnCheckpointConvertedException
            && ex is not AgentTurnResumeClaimConflictException
            && ex is not AgentInfrastructureFailureException
            && ex is not SandboxProvisioningDeferredException)
        {
            throw new AgentInfrastructureFailureException(
                runner.Kind,
                agentPhase,
                "Retained-sandbox checkpoint conversion could not be completed safely; the exact recovery lease remains preserved.",
                ex);
        }
        finally
        {
            if (retainedPreparationClaimed)
                await TryReleaseAgentTurnPreparationClaimAsync(item, CancellationToken.None);

            if (sandboxOwnedByPhase)
            {
                // Legacy independent-phase pipeline: the sandbox is per-phase,
                // dispose it now (matches the original `await using var sandbox`
                // behaviour).
                try
                {
                    await sandbox.DisposeAsync();
                }
                catch (Exception ex) when (!phaseSucceeded)
                {
                    // Best-effort disposal — the outer exception (if any) is
                    // the meaningful failure.
                    _log.LogWarning(ex, "Sandbox disposal failed after unsuccessful phase {Phase} for work item {Id}", agentPhase, item.Id);
                }
                catch (Exception ex)
                {
                    // Teardown after completed work is an operational event,
                    // not the item's outcome: the agent's work is durable
                    // (pushed, synced, checkpointed above), so a cleanup
                    // failure must not fail the item. The failure stays
                    // surfaced — logged here with item context (and by the
                    // sandbox layers below) — while the sandbox remains
                    // discoverable through the provider's managed inventory
                    // instead of being silently forgotten, with its capacity
                    // permit retained until a restart, so shutdown teardown
                    // or operator reclamation can still reclaim it.
                    // Never rethrow here.
                    _log.LogWarning(ex, "Sandbox disposal failed after successful phase {Phase} for work item {Id}; the phase outcome stands and the sandbox remains in the managed inventory for reclamation", agentPhase, item.Id);
                }
            }
            else if (useClaudeSession)
            {
                if (phaseSucceeded)
                {
                    // Session-mode success path: suspend the worker VM so the
                    // (long) audit phase doesn't burn host resources holding an
                    // idle worker, while preserving the in-VM transcript and the
                    // server-side prompt cache (within its TTL) for the next
                    // rework turn. On failure we MUST NOT silently swallow: a
                    // failed multipass stop/resume boundary is exactly the
                    // session-mode acceptance criterion the brief lists as
                    // non-negotiable. Surface the failure to operators via the
                    // audit log and a webhook, then close the lifecycle so:
                    //   (a) the worker VM is torn down before the long audit
                    //       (no idle VM holding host resources), and
                    //   (b) the next rework turn falls back to the legacy
                    //       fresh-sandbox path (RunAgentPhaseAsync checks
                    //       IsClosed to opt out of the session branch).
                    try
                    {
                        await sessionLifecycle!.SuspendAsync(CancellationToken.None);
                    }
                    catch (Exception suspendEx)
                    {
                        var sessionIdForLog = sessionLifecycle!.Handle.SessionId;
                        AuditLog.ClaudeSessionSuspendFailed(item.Id, sessionIdForLog, suspendEx.Message);
                        _log.LogWarning(suspendEx,
                            "ClaudeSessionLifecycle.SuspendAsync failed for work item {Id} session {SessionId}; closing the session and degrading to legacy fresh-sandbox rework",
                            item.Id, sessionIdForLog);
                        try
                        {
                            await _webhooks.PublishAsync(new WebhookEvent
                            {
                                Event = "agent.claude_session_suspend_failed",
                                WorkItem = item,
                                Project = project,
                                Details = new
                                {
                                    workItemId = item.Id.ToString(),
                                    sessionId = sessionIdForLog,
                                    reason = suspendEx.Message,
                                },
                            }, CancellationToken.None);
                        }
                        catch
                        {
                            // Webhook delivery is best-effort; the audit log is
                            // the durable surface.
                        }
                        await CloseAmbientClaudeSessionAsync(
                            sessionLifecycle!,
                            item,
                            project,
                            "suspend failed before audit");
                        _ambientSessionLifecycle.Value = null;
                    }
                }
                else
                {
                    await CloseAmbientClaudeSessionAsync(
                        sessionLifecycle!,
                        item,
                        project,
                        "session-backed attempt failed before phase success");
                    _ambientSessionLifecycle.Value = null;
                }
            }
        }
    }

}
