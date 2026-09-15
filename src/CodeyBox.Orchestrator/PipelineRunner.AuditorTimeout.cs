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

// PipelineRunner.AuditorTimeout.cs — Auditor sandbox/idle-timeout handling: sandbox creation with idle guard, teardown, and post-run processing.
public sealed partial class PipelineRunner
{
    /// <summary>
    /// Surrenders the ambient work-phase reusable sandbox (if any) back to the
    /// sandbox admission gate. Phases that provision their own sandboxes
    /// directly from the provider (audit fan-out, merge, conflict rework) call
    /// this before acquiring, so a worker never blocks on a new permit while
    /// holding its work-phase permit.
    /// </summary>
    private static Task ReleaseAmbientWorkSandboxAsync() =>
        WorkSandboxContext.Current?.ReleaseActiveSandboxAsync() ?? Task.CompletedTask;

    private async Task<ISandbox> CreateAuditSandboxWithIdleTimeoutAsync(
        SandboxSpec spec,
        string auditorName,
        AgentKind agent,
        WorkItem item,
        Project project,
        int iteration,
        CancellationToken ct)
    {
        // Release-then-acquire: the audit phase provisions its own sandboxes
        // straight from the provider while the work-phase reusable sandbox is
        // still admitted. Surrender that permit first so this worker never
        // blocks on an audit permit while holding its work permit (pool-wide
        // deadlock when every worker does the same). Rework recreates the
        // reusable sandbox on demand afterwards.
        await ReleaseAmbientWorkSandboxAsync().ConfigureAwait(false);
        using var waitScope = SandboxPermitWaitScope.Begin(item.Id.ToString(), "audit");
        var timeout = _pipelineTuning.Current.AuditorIdleTimeout;
        if (timeout <= TimeSpan.Zero)
            return await _sandboxes.CreateAsync(spec, ct).ConfigureAwait(false);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var clock = _opts.TimeProvider;
        var lastActivityTicks = clock.GetTimestamp();
        void Touch() => Volatile.Write(ref lastActivityTicks, clock.GetTimestamp());

        var createTask = _sandboxes.CreateAsync(spec, linkedCts.Token);
        var timeoutTask = WaitForAuditorIdleTimeoutAsync(
            linkedCts.Token,
            () => Volatile.Read(ref lastActivityTicks));

        try
        {
            var completed = await Task.WhenAny(createTask, timeoutTask).ConfigureAwait(false);
            if (completed == timeoutTask)
            {
                var timedOutAfter = await timeoutTask.ConfigureAwait(false);
                if (timedOutAfter is not null)
                {
                    const string sandboxId = "(launch-timeout)";
                    LogAuditorTimedOut(item.Id, auditorName, agent, iteration, sandboxId);

                    await CancelAndObserveSandboxCreateAfterIdleTimeoutAsync(
                        linkedCts,
                        createTask,
                        "sandbox launch",
                        auditorName,
                        agent).ConfigureAwait(false);
                    await PublishAuditorTimedOutEventAsync(
                        item,
                        project,
                        auditorName,
                        agent,
                        iteration,
                        sandboxId).ConfigureAwait(false);
                    throw new AuditorIdleTimeoutException(auditorName, agent, timedOutAfter.Value);
                }

                ct.ThrowIfCancellationRequested();
            }

            var sandbox = await createTask.ConfigureAwait(false);
            Touch();
            ct.ThrowIfCancellationRequested();
            return sandbox;
        }
        finally
        {
            try { await linkedCts.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }

            try { await timeoutTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task RunAuditSandboxSetupWithIdleTimeoutAsync(
        ISandbox sandbox,
        string auditorName,
        AgentKind agent,
        WorkItem item,
        Project project,
        int iteration,
        Func<ISandbox, CancellationToken, Task> setup,
        CancellationToken ct)
    {
        var timeout = _pipelineTuning.Current.AuditorIdleTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            await setup(sandbox, ct).ConfigureAwait(false);
            return;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var clock = _opts.TimeProvider;
        var lastActivityTicks = clock.GetTimestamp();
        void Touch() => Volatile.Write(ref lastActivityTicks, clock.GetTimestamp());

        var watchedSandbox = new ActivityTrackingSandbox(sandbox, Touch);
        var setupTask = setup(watchedSandbox, linkedCts.Token);
        var timeoutTask = WaitForAuditorIdleTimeoutAsync(
            linkedCts.Token,
            () => Volatile.Read(ref lastActivityTicks));

        try
        {
            var completed = await Task.WhenAny(setupTask, timeoutTask).ConfigureAwait(false);
            if (completed == timeoutTask)
            {
                var timedOutAfter = await timeoutTask.ConfigureAwait(false);
                if (timedOutAfter is not null)
                {
                    await CancelAndTearDownAfterIdleTimeoutAsync(
                        linkedCts,
                        setupTask,
                        sandbox,
                        "audit setup",
                        auditorName,
                        agent,
                        item,
                        project,
                        iteration).ConfigureAwait(false);
                    throw new AuditorIdleTimeoutException(auditorName, agent, timedOutAfter.Value);
                }

                ct.ThrowIfCancellationRequested();
            }

            await setupTask.ConfigureAwait(false);
            Touch();
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            try { await linkedCts.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }

            try { await timeoutTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task<AuditResult> RunAuditorWithIdleTimeoutAsync(
        IAuditor auditor,
        AgentKind agent,
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        WorkItem item,
        Project project,
        CancellationToken ct)
    {
        var idleTimeout = EffectiveAuditorIdleTimeout(auditor);
        var absoluteTimeout = _pipelineTuning.Current.AuditorAbsoluteTimeout;
        if (idleTimeout <= TimeSpan.Zero && absoluteTimeout <= TimeSpan.Zero)
            return await auditor.RunAsync(sandbox, workingDirectory, context, ct).ConfigureAwait(false);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var clock = _opts.TimeProvider;
        var startTicks = clock.GetTimestamp();
        var lastActivityTicks = startTicks;
        void Touch() => Volatile.Write(ref lastActivityTicks, clock.GetTimestamp());

        var originalCallback = context.StdoutChunkCallback;
        var watchedContext = context with
        {
            StdoutChunkCallback = chunk =>
            {
                Touch();
                originalCallback?.Invoke(chunk);
            },
        };

        var watchedSandbox = new ActivityTrackingSandbox(sandbox, Touch);
        var auditorTask = auditor.RunAsync(watchedSandbox, workingDirectory, watchedContext, linkedCts.Token);

        try
        {
            var result = await AuditorIdleGuard.WaitAsync(
                auditorTask,
                auditor.Name,
                agent,
                () => watchedSandbox.HasActiveExecs,
                () => (EffectiveAuditorIdleTimeout(auditor), _pipelineTuning.Current.AuditorAbsoluteTimeout),
                () => Volatile.Read(ref lastActivityTicks),
                startTicks,
                Touch,
                // Poll well below test-scale idle windows so a genuinely hung
                // run is detected promptly; matches the legacy adaptive-delay
                // floor (100 ms) this guard replaced.
                TimeSpan.FromMilliseconds(100),
                linkedCts.Token,
                clock).ConfigureAwait(false);
            Touch();
            ct.ThrowIfCancellationRequested();
            return result;
        }
        catch (AuditorIdleTimeoutException)
        {
            await CancelAndTearDownAfterIdleTimeoutAsync(
                linkedCts,
                auditorTask,
                sandbox,
                "auditor",
                auditor.Name,
                agent,
                item,
                project,
                context.Iteration).ConfigureAwait(false);
            throw;
        }
        finally
        {
            try { await linkedCts.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Resolves the idle-timeout to apply to a single auditor run. A
    /// test-runner auditor (surfaced directly or through a wrapping provider)
    /// may declare a longer test-specific window via its
    /// <see cref="ITestRunnerAuditor.CurrentRunOptions"/>; every other auditor
    /// uses the global <see cref="PipelineTuningOptions.AuditorIdleTimeout"/>.
    /// </summary>
    private TimeSpan EffectiveAuditorIdleTimeout(IAuditor auditor)
        => ResolveEffectiveAuditorIdleTimeout(auditor, _pipelineTuning.Current.AuditorIdleTimeout);

    /// <summary>
    /// Pure branch logic behind <see cref="EffectiveAuditorIdleTimeout"/>, split
    /// out so the resolution — direct test-runner, wrapped test-runner via
    /// <see cref="ITestRunnerAuditorProvider"/>, the <c>&gt; TimeSpan.Zero</c>
    /// guard, and the global fallback — is unit-testable without standing up a
    /// full pipeline.
    /// </summary>
    internal static TimeSpan ResolveEffectiveAuditorIdleTimeout(IAuditor auditor, TimeSpan globalIdleTimeout)
    {
        var testRunner = auditor as ITestRunnerAuditor
            ?? (auditor as ITestRunnerAuditorProvider)?.TestRunner;
        if (testRunner?.CurrentRunOptions.IdleTimeout is { } idle && idle > TimeSpan.Zero)
            return idle;
        return globalIdleTimeout;
    }

    private async Task<TimeSpan?> WaitForAuditorIdleTimeoutAsync(
        CancellationToken ct,
        Func<long> getLastActivityTicks,
        Func<TimeSpan>? timeoutSelector = null)
    {
        var clock = _opts.TimeProvider;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var currentTimeout = timeoutSelector?.Invoke() ?? _pipelineTuning.Current.AuditorIdleTimeout;
                if (currentTimeout <= TimeSpan.Zero)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), clock, ct).ConfigureAwait(false);
                    continue;
                }

                var elapsed = clock.GetElapsedTime(getLastActivityTicks());
                if (elapsed >= currentTimeout)
                    return currentTimeout;

                var remaining = currentTimeout - elapsed;
                var delay = remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1);
                if (delay <= TimeSpan.Zero)
                    delay = TimeSpan.FromMilliseconds(100);
                await Task.Delay(delay, clock, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }

        return null;
    }

    private async Task CancelAndTearDownAfterIdleTimeoutAsync(
        CancellationTokenSource cts,
        Task task,
        ISandbox sandbox,
        string operation,
        string auditorName,
        AgentKind agent,
        WorkItem item,
        Project project,
        int iteration)
    {
        var sandboxId = sandbox.Id;
        try { await cts.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }

        LogAuditorTimedOut(item.Id, auditorName, agent, iteration, sandboxId);

        try
        {
            using var killCts = new CancellationTokenSource(AuditorTimeoutTeardownGrace);
            await sandbox.KillActiveExecsAsync(killCts.Token)
                .WaitAsync(AuditorTimeoutTeardownGrace)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.LogWarning(
                "Timed-out {Operation} for auditor {Auditor} (agent: {Agent}) did not kill active execs in sandbox {SandboxId} within the teardown grace period",
                operation,
                auditorName,
                agent.Value,
                sandboxId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Timed-out {Operation} for auditor {Auditor} (agent: {Agent}) failed while killing active execs in sandbox {SandboxId}",
                operation,
                auditorName,
                agent.Value,
                sandboxId);
        }

        try
        {
            await sandbox.DisposeAsync().AsTask()
                .WaitAsync(AuditorTimeoutTeardownGrace)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.LogWarning(
                "Timed-out {Operation} for auditor {Auditor} (agent: {Agent}) did not dispose sandbox {SandboxId} within the teardown grace period",
                operation,
                auditorName,
                agent.Value,
                sandboxId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Timed-out {Operation} for auditor {Auditor} (agent: {Agent}) failed while disposing sandbox {SandboxId}",
                operation,
                auditorName,
                agent.Value,
                sandboxId);
        }

        async Task PublishTimeoutEventAsync()
        {
            await PublishAuditorTimedOutEventAsync(
                item,
                project,
                auditorName,
                agent,
                iteration,
                sandboxId).ConfigureAwait(false);
        }

        if (task.IsCompleted)
        {
            ObserveTimedOutTask(task, operation, auditorName, agent);
            await PublishTimeoutEventAsync().ConfigureAwait(false);
            return;
        }

        var completed = await Task.WhenAny(task, Task.Delay(AuditorTimeoutTeardownGrace)).ConfigureAwait(false);
        if (completed == task)
        {
            ObserveTimedOutTask(task, operation, auditorName, agent);
            await PublishTimeoutEventAsync().ConfigureAwait(false);
            return;
        }

        _log.LogWarning(
            "Timed-out {Operation} for auditor {Auditor} (agent: {Agent}) did not stop within the teardown grace period after cancellation, active-exec kill, and sandbox disposal",
            operation,
            auditorName,
            agent.Value);
        _ = task.ContinueWith(
            completedTask =>
            {
                _ = completedTask.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        await PublishTimeoutEventAsync().ConfigureAwait(false);
    }

    private void LogAuditorTimedOut(
        WorkItemId workItemId,
        string auditorName,
        AgentKind agent,
        int iteration,
        string sandboxId)
    {
        try
        {
            AuditLog.AuditorTimedOut(workItemId, auditorName, agent, iteration, sandboxId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to write auditor timeout audit log for {Auditor}", auditorName);
        }
    }

    private async Task PublishAuditorTimedOutEventAsync(
        WorkItem item,
        Project project,
        string auditorName,
        AgentKind agent,
        int iteration,
        string sandboxId)
    {
        try
        {
            await TryPublishEventAsync(item, project, "audit.auditor_timed_out", new AuditAuditorTimedOutDetails
            {
                WorkItemId = item.Id.ToString(),
                Auditor = auditorName,
                Agent = agent.Value,
                Iteration = iteration,
                SandboxId = sandboxId
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to publish auditor timeout event for {Auditor}", auditorName);
        }
    }

    private async Task CancelAndObserveSandboxCreateAfterIdleTimeoutAsync(
        CancellationTokenSource cts,
        Task<ISandbox> createTask,
        string operation,
        string auditorName,
        AgentKind agent)
    {
        try { await cts.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }

        if (createTask.IsCompleted)
        {
            await ObserveOrDisposeCreatedSandboxAsync(createTask, operation, auditorName, agent)
                .ConfigureAwait(false);
            return;
        }

        var completed = await Task.WhenAny(createTask, Task.Delay(AuditorTimeoutTeardownGrace))
            .ConfigureAwait(false);
        if (completed == createTask)
        {
            await ObserveOrDisposeCreatedSandboxAsync(createTask, operation, auditorName, agent)
                .ConfigureAwait(false);
            return;
        }

        _log.LogWarning(
            "Timed-out {Operation} for auditor {Auditor} (agent: {Agent}) did not stop within the teardown grace period after cancellation",
            operation,
            auditorName,
            agent.Value);
        _ = createTask.ContinueWith(
            completedTask => ObserveOrDisposeCreatedSandboxAsync(completedTask, operation, auditorName, agent),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default).Unwrap();
    }

    private async Task ObserveOrDisposeCreatedSandboxAsync(
        Task<ISandbox> createTask,
        string operation,
        string auditorName,
        AgentKind agent)
    {
        ISandbox sandbox;
        try
        {
            sandbox = await createTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _log.LogDebug(
                ex,
                "Timed-out {Operation} for auditor {Auditor} (agent: {Agent}) stopped with an exception after launch cancellation",
                operation,
                auditorName,
                agent.Value);
            return;
        }

        try
        {
            await sandbox.DisposeAsync().AsTask()
                .WaitAsync(AuditorTimeoutTeardownGrace)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.LogWarning(
                "Timed-out {Operation} for auditor {Auditor} (agent: {Agent}) produced sandbox {SandboxId} after cancellation but did not dispose it within the teardown grace period",
                operation,
                auditorName,
                agent.Value,
                sandbox.Id);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Timed-out {Operation} for auditor {Auditor} (agent: {Agent}) produced sandbox {SandboxId} after cancellation but failed while disposing it",
                operation,
                auditorName,
                agent.Value,
                sandbox.Id);
        }
    }

    private void ObserveTimedOutTask(Task task, string operation, string auditorName, AgentKind agent)
    {
        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogDebug(
                ex,
                "Timed-out {Operation} for auditor {Auditor} (agent: {Agent}) stopped with an exception after teardown",
                operation,
                auditorName,
                agent.Value);
        }
    }

    /// <summary>
    /// Handles all post-run bookkeeping for a completed auditor: cost capture,
    /// sub-step emission, structured logging, and audit-report persistence.
    /// Always called sequentially (never from parallel tasks) to keep writes
    /// to external stores ordered and safe.
    /// </summary>
    private async Task PostProcessAuditorRunAsync(
        AuditorRunRecord run,
        IAgentRunner workRunner,
        bool needsCreds,
        WorkItem item,
        Project project,
        AuditContext ctx,
        CancellationToken ct)
    {
        // Auth/login-prompt check runs alongside the quota check: an exit-0
        // login prompt from an LLM auditor's agent that suppressed
        // audit/result.json was previously surfaced as a normal "agent did not
        // write audit/result.json" finding, leaving the unauthenticated agent
        // routable for the next iteration. Inspects AgentStdout / AgentStderr
        // (now populated unconditionally by LlmReviewAuditor) plus RawOutput
        // as a fallback for any auditor that did not propagate the stream.
        //
        // Auth has precedence over quota because OAuth/login prompts can include
        // 401 diagnostics that are also quota-detector inputs; the operator
        // action is to re-authenticate, not to park the item for quota reset.
        await ThrowIfAuditorRunAuthRequiredAsync(run, needsCreds, item, project, ct);
        await ThrowIfAuditorRunQuotaAsync(run, needsCreds, item, project, ct);

        if (needsCreds)
        {
            // Record usage under the exact model the auditor dispatched on, so
            // spend lands in the same bucket EvaluateAuditCandidateQuotaAsync gates
            // on (see BuildUsageEvent). ExecAuditorAsync resolved and pinned that
            // model on the run record (ResolvedModelId): same-kind keeps the work
            // model, cross-kind uses the auditor's configured class-member model.
            // Reading it back here keeps recording and dispatch on a single value
            // instead of re-deriving and risking drift.
            await TryRecordCostAsync(run.Result.RawOutput, null,
                run.Runner.Kind, run.AgentInstanceId, ctx.WorkItemId, "audit", ctx.Iteration,
                run.StartedAt, run.StartedAt + run.Elapsed,
                run.ResolvedModelId);
        }
        await _auditorTelemetry.EmitAuditorSubStepsAsync(run.Auditor.Name, run.Result.RawOutput,
            ctx.WorkItemId, ctx.Iteration, run.StartedAt);
        if (!run.CapturedStructuredStream)
        {
            await _auditorTelemetry.EmitToolCallCountsAsync(run.Runner.Kind, run.Result.RawOutput, ctx.WorkItemId, "audit",
                run.ScopeElapsedMs, ct, iteration: ctx.Iteration);
        }
        var worstSeverity = run.Result.Findings.Count > 0
            ? ((AuditSeverity)run.Result.Findings.Max(f => (int)f.Severity)).ToString()
            : "none";
        AuditLog.AuditorRun(run.Auditor.Name, worstSeverity, run.Elapsed, run.Runner.Kind);
        await PersistAuditReportAsync(ctx, run.Auditor, run.Result, run.StartedAt, run.Elapsed, ct);
    }

    private async Task ThrowIfAuditorRunAuthRequiredAsync(
        AuditorRunRecord run,
        bool needsCreds,
        WorkItem item,
        Project project,
        CancellationToken ct)
    {
        if (!needsCreds)
            return;

        // AgentStdout / AgentStderr are the structured agent-output fields
        // (set by LlmReviewAuditor on every return path now). RawOutput is the
        // belt-and-braces fallback — some auditors fold the agent's last reply
        // into RawOutput without splitting into stdout/stderr, and an exit-0
        // login prompt that landed only there would otherwise escape the
        // detector and surface as an "agent did not write audit/result.json"
        // finding.
        var stdout = !string.IsNullOrEmpty(run.Result.AgentStdout)
            ? run.Result.AgentStdout
            : run.Result.RawOutput;
        var stderr = run.Result.AgentStderr;
        if (string.IsNullOrEmpty(stdout) && string.IsNullOrEmpty(stderr))
            return;

        var phase = $"audit:{run.Auditor.Name}";
        var detection = _authFailureClassifier.DetectDetailed(run.Runner.Kind, stderr, stdout);
        if (detection is { Classification.Kind: AgentFailureKind.AuthRequired })
        {
            await HandleAuthRequiredDetectionAsync(
                item,
                project,
                run.Runner.Kind,
                phase,
                detection.Classification,
                throwOnMatch: true,
                stdoutOnlyEvidence: detection.IsStdoutOnly,
                requireStdoutOnlyCorroboration: true,
                matchedConfiguredPattern: detection.MatchedConfiguredStderrPattern
                    || detection.MatchedConfiguredStdoutPattern,
                ct: ct);
        }

        if (IsLlmAgentExecutionFailure(run.Result))
        {
            var classification = _authFailureClassifier.ClassifyFailure(
                run.Runner,
                ToAgentResultForAuditFailureClassification(run.Result));
            if (classification.Kind == AgentFailureKind.AuthError)
                await ThrowAuthErrorAgentFailureAsync(item, project, run.Runner.Kind, phase, classification, ct);
        }

        // LLM audit-agent execution failures report CLI diagnostics through
        // AgentStdout/AgentStderr, not source-code review prose. Accept guarded
        // stdout login fragments here so auth wins over a companion quota
        // diagnostic for the item outcome. Forced smoke corroboration is attempted
        // for stdout-only evidence, but an unavailable probe cannot leave a matched
        // login prompt routable. Routed through the injected classifier so
        // operator-configured stdout patterns participate alongside defaults.
        if (IsLlmAgentExecutionFailure(run.Result)
            && _authFailureClassifier.ContainsAuthRequiredFragmentInStdout(run.Runner.Kind, stdout))
        {
            await HandleAuthRequiredDetectionAsync(
                item,
                project,
                run.Runner.Kind,
                phase,
                new AgentFailureClassification(
                    AgentFailureKind.AuthRequired,
                    Reason: "auth/login prompt pattern matched in audit agent stdout"),
                throwOnMatch: true,
                stdoutOnlyEvidence: true,
                requireStdoutOnlyCorroboration: true,
                ct: ct);
        }
    }

    private async Task ThrowIfAuditorRunQuotaAsync(
        AuditorRunRecord run,
        bool needsCreds,
        WorkItem item,
        Project project,
        CancellationToken ct)
    {
        if (!needsCreds)
            return;

        // A completed audit that produced a VALID verdict did not hit a quota wall:
        // it ran to completion and wrote audit/result.json. Scanning a successful
        // audit's stdout/stderr for quota phrases false-positives when the code under
        // review is itself quota / rate-limit code — the reviewer legitimately quotes
        // "429" / "usage limit reached" / "quota exhausted" from the diff, and the
        // classifier reads the agent's own review text as an exhaustion signal. With a
        // single-member audit class that parks the whole item despite the agent having
        // ample quota (and starves the very fix for this bug, whose diff is quota code).
        // Only classify quota when the audit FAILED to produce a verdict (agent CLI
        // died / no result.json / invalid JSON) — what a genuine mid-audit exhaustion
        // looks like. Auth detection is handled separately and keeps its own precedence.
        if (AuditProducedValidVerdict(run.Result))
            return;

        if (run.Result.AgentStderr is not null || run.Result.AgentStdout is not null)
        {
            _quotaAuditEmitter.EmitAdvisoryAuditEvents(
                run.Runner.Kind, run.Result.AgentStderr, run.Result.AgentStdout, "audit", sandboxName: null);
            var auditQuotaClassification = _quotaClassifier.Classify(
                run.Runner.Kind, run.Result.AgentStderr, run.Result.AgentStdout);
            var quotaDetection = auditQuotaClassification.Detection;

            if (quotaDetection is not null)
            {
                await _quotaClassifier.RecordIfQuotaFailureAsync(
                    _quotaFailures,
                    run.Runner.Kind,
                    ResolveObservedModelId(run.Runner, modelId: null),
                    run.Result.AgentSummary,
                    run.Result.AgentStderr,
                    DateTimeOffset.UtcNow,
                    _auditQuotaOptions.ObservedFailureRetention,
                    ct,
                    projectId: project.Id,
                    stdout: run.Result.AgentStdout);

                throw new TerminalQuotaError(
                    quotaDetection.Kind,
                    QuotaFailureMessage(
                        quotaDetection.Kind,
                        $"Audit agent {run.Runner.Kind} reported quota failure while running {run.Auditor.Name}",
                        SanitizedAgentDetail.FromRaw(run.Result.AgentSummary ?? "agent failed")),
                    quotaDetection.ResetAt,
                    providerSurfaceMatch: auditQuotaClassification.ProviderSurfaceMatch);
            }
        }

        // Exit-0 give-up on the audit path: some CLIs (notably agy) exit 0 and
        // write no audit/result.json when a consumer-tier RESOURCE_EXHAUSTED (429)
        // stops them, surfacing the 429 only in an internal log the runner lifts
        // into TerminalDiagnostic (a side-channel distinct from AgentStderr, which
        // the block above reads). Without this, an exit-0 audit 429 is treated as
        // an audit that produced zero findings — the item could proceed/merge on an
        // audit that never actually ran, violating the "auditor never rubber-stamps"
        // invariant. Classify the terminal region and park a genuine quota block.
        // Restricted to real quota kinds (a lifted "API Error: 401" classifies as
        // Unauthorized and must NOT masquerade as a reset-and-retry park — an
        // expired token never clears on a quota window, so it would pin the item in
        // a retry loop; letting it fall through leaves the audit failing on its
        // "agent did not write result.json" finding instead).
        if (!string.IsNullOrEmpty(run.Result.AgentTerminalDiagnostic))
        {
            var terminalQuota = _quotaClassifier.Detect(
                run.Runner.Kind, run.Result.AgentTerminalDiagnostic, run.Result.AgentStdout);
            if (IsParkableQuotaKind(terminalQuota))
            {
                // AgentTerminalDiagnostic is the runner-lifted terminal error
                // region (CLI-owned, same trust as the no-diff CliOwned path):
                // it is provider-emitted by construction, never agent prose.
                // The trust flag travels on the exception so the park
                // transition keeps this park even against a healthy probe.
                _quotaAuditEmitter.EmitAdvisoryAuditEvents(
                    run.Runner.Kind, run.Result.AgentTerminalDiagnostic, run.Result.AgentStdout, "audit", sandboxName: null);
                await _quotaClassifier.RecordIfQuotaFailureAsync(
                    _quotaFailures,
                    run.Runner.Kind,
                    ResolveObservedModelId(run.Runner, modelId: null),
                    run.Result.AgentSummary,
                    run.Result.AgentTerminalDiagnostic,
                    DateTimeOffset.UtcNow,
                    _auditQuotaOptions.ObservedFailureRetention,
                    ct,
                    projectId: project.Id,
                    stdout: run.Result.AgentStdout,
                    bypassExitedSummaryGuard: true);
                throw new TerminalQuotaError(
                    terminalQuota!.Kind,
                    QuotaFailureMessage(
                        terminalQuota.Kind,
                        $"Audit agent {run.Runner.Kind} reported quota failure on clean exit while running {run.Auditor.Name}",
                        SanitizedAgentDetail.FromRaw(run.Result.AgentTerminalDiagnostic)),
                    terminalQuota.ResetAt,
                    providerSurfaceMatch: true);
            }
        }

        if (IsLlmAgentExecutionFailure(run.Result))
        {
            ThrowIfTransientAgentFailure(
                run.Runner,
                ToAgentResultForAuditFailureClassification(run.Result),
                "audit");
        }
    }

    /// <summary>
    /// True when a quota detection is a genuine rate-limit / cap block that should
    /// park the item as <see cref="WorkItemState.WaitingForQuotaReset"/> and retry
    /// after the reset. An <see cref="QuotaFailureKind.Unauthorized"/> detection is
    /// NOT parkable: a 401/403 never clears on a quota window, so parking it would
    /// loop forever and skip the auth-required handling. Callers that classify a
    /// terminal-diagnostic side-channel (where the auth path did not run first) use
    /// this to keep an auth marker from masquerading as a reset-and-retry park.
    /// </summary>
    private static bool IsParkableQuotaKind(QuotaDetection? detection) =>
        detection is { Kind: QuotaFailureKind.RateLimitExceeded or QuotaFailureKind.LimitReached };

    private static bool IsLlmAgentExecutionFailure(AuditResult result) =>
        !result.Passed
        && result.AgentSummary is not null
        && HasLlmAgentExecutionFailureSentinel(result.Findings, f => f.Title);

    private static bool HasLlmAgentExecutionFailureSentinel<T>(
        IEnumerable<T> findings,
        Func<T, string> titleSelector) =>
        findings.Any(f =>
            string.Equals(titleSelector(f), "review agent failed to run", StringComparison.OrdinalIgnoreCase));

    // A "valid verdict" means the auditor parsed audit/result.json into a real
    // pass/fail decision — its findings are the review's own findings, not one of
    // LlmReviewAuditor's synthetic run-failure sentinels ("review agent failed to
    // run", "agent did not write audit/result.json", "review agent produced invalid
    // JSON"). Used to suppress false quota classification on a successful audit whose
    // review text merely quotes quota/rate-limit code under review.
    private static bool AuditProducedValidVerdict(AuditResult result) =>
        !result.Findings.Any(f =>
            string.Equals(f.Title, "review agent failed to run", StringComparison.OrdinalIgnoreCase)
            || f.Title.StartsWith("agent did not write ", StringComparison.OrdinalIgnoreCase)
            || string.Equals(f.Title, "review agent produced invalid JSON", StringComparison.OrdinalIgnoreCase));

    private sealed record AuditorRunRecord(
        IAuditor Auditor,
        IAgentRunner Runner,
        string? AgentInstanceId,
        string? ResolvedModelId,
        AuditResult Result,
        DateTimeOffset StartedAt,
        TimeSpan Elapsed,
        long ScopeElapsedMs,
        bool CapturedStructuredStream);

    private sealed record AuditorBatchResult(
        IReadOnlyList<AuditFinding> Findings,
        AgentKind? ActiveAuditAgentKind,
        bool DeclaredShortCircuitBlocking,
        bool IncompleteVerdict = false,
        IReadOnlyList<string>? CompletedAuditors = null,
        IReadOnlyList<string>? IncompleteAuditors = null,
        BuildTestGateEvidence PassedBuildTestGateEvidence = BuildTestGateEvidence.None,
        bool BuildTestGateFailed = false,
        IReadOnlyList<TestFailureAttributionResult>? TestFailureAttributions = null);

    private enum AuditProgressUpdateOperation
    {
        Accumulate,
        Replace,
    }

    private sealed record AuditProgressUpdate(
        IReadOnlyList<AuditFinding> Findings,
        IReadOnlyList<string> CompletedAuditors,
        AuditProgressUpdateOperation Operation = AuditProgressUpdateOperation.Accumulate);

    private async Task PersistAuditReportAsync(
        AuditContext ctx,
        IAuditor auditor,
        AuditResult result,
        DateTimeOffset startedAt,
        TimeSpan elapsed,
        CancellationToken ct)
    {
        if (_auditReports is null) return;
        try
        {
            const int MaxRawBytes = 256 * 1024;
            string? rawOutput = null;
            if (result.RawOutput is not null)
            {
                var redacted = RawOutputRedactor.Redact(result.RawOutput);
                rawOutput = RawOutputRedactor.TruncateToBytes(redacted, MaxRawBytes);
            }

            var worstSeverity = result.Findings.Count > 0
                ? ((AuditSeverity)result.Findings.Max(f => (int)f.Severity)).ToString()
                : "none";

            var reportFindings = result.Findings.Select(f =>
            {
                var (files, lineHints) = FindingIdComputer.ParseLocation(f.Location);
                return new AuditReportFinding(
                    Id: FindingIdComputer.Compute(auditor.Name, f.Title, files),
                    Severity: f.Severity.ToString(),
                    Title: f.Title,
                    Message: f.Description,
                    Files: files,
                    LineHints: lineHints);
            }).ToList();

            var report = new AuditReport
            {
                Id = Guid.NewGuid().ToString(),
                WorkItemId = ctx.WorkItemId.ToString(),
                Iteration = ctx.Iteration,
                Target = ctx.EffectiveTarget,
                AuditorName = auditor.Name,
                AuditorKind = auditor.Kind,
                WorstSeverity = worstSeverity,
                StartedAt = startedAt,
                EndedAt = startedAt + elapsed,
                DurationMs = (long)elapsed.TotalMilliseconds,
                Findings = reportFindings,
                RawOutput = rawOutput,
                TestSelection = result.TestSelection,
            };
            await _auditReports.CreateAsync(report, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex,
                "Failed to persist diagnostic audit report for auditor {AuditorName} target {Target} iteration {Iteration} on work item {WorkItemId}",
                auditor.Name,
                ctx.EffectiveTarget.Value,
                ctx.Iteration,
                ctx.WorkItemId);
        }
    }

    /// <summary>
    /// Picks the agent runner for an LLM-driven auditor invocation. Always
    /// returns a non-null <see cref="AuditAgentSelection"/> or throws — a
    /// silent skip would let a Pass verdict emerge with one fewer review
    /// than configured, which violates the per-auditor independent-gate
    /// contract.
    ///
    /// <para>Resolution order:</para>
    /// <list type="number">
    ///   <item>Use the explicitly-configured per-auditor / default audit agent
    ///         when registered, credentialed, audit-capable, smoke-available,
    ///         and quota-available.</item>
    ///   <item>If the preferred agent is quota-exhausted AND the work item has
    ///         an agent class configured, walk the class chain (same order the
    ///         work-phase router would use) and pick the first member that is
    ///         registered + credentialed + audit-capable + quota-available.</item>
    ///   <item>Otherwise fall through to the work agent — preserves the
    ///         legacy "audit reuses the work agent on misconfiguration" path
    ///         (unregistered audit agent, missing credentials, or quota-exhausted
    ///         agent with no class chain to walk).</item>
    /// </list>
    ///
    /// <para>Failure modes (mutually exclusive, in order of precedence):</para>
    /// <list type="bullet">
    ///   <item><see cref="AgentPausedException"/> — operator-paused agent and
    ///         no usable substitute. Routed to WaitingForAgentResume.</item>
    ///   <item><see cref="AgentClassExhaustedException"/> — at least one
    ///         candidate was quota-rejected and no usable substitute remained.
    ///         Routed to WaitingForQuotaReset; QuotaRetryScheduler resumes the
    ///         same iteration when quota returns.</item>
    ///   <item><see cref="AuditUnavailableException"/> — configuration-shaped
    ///         absence: no candidate was ever dispatchable (smoke-rejected,
    ///         missing runner, or missing credentials). Routed to
    ///         <c>failureKind="infrastructure"</c> — distinct from quota
    ///         because quota returning will not make a smoke-benched CLI or
    ///         a missing credential usable.</item>
    /// </list>
    ///
    /// <para>
    /// Capability gate (<see cref="WellKnownCapabilities.Audit"/>): when AT
    /// LEAST ONE member of the routed class declares the <c>audit</c> tag, the
    /// audit phase is restricted to the router's effective audit-capable
    /// members, including same-kind siblings that inherit the capability. This
    /// is what fixes the audit-throughput collapse: with both Claude AND
    /// Codex audit-capable, an exhausted Codex spills to Claude (and vice-versa)
    /// while Gemini stays out of the audit pool entirely. When NO member
    /// carries the tag, audit routing falls back to the legacy
    /// "any class member is eligible" behaviour for backward compatibility.
    /// </para>
    /// </summary>
    private sealed record AuditAgentSelection(IAgentRunner Runner, AgentMembership? Member);

}
