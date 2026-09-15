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

// PipelineRunner.StuckProbe.cs — Stuck-agent probe: RunWithStuckProbeAsync, stuck handling, and threshold validation.
public sealed partial class PipelineRunner
{
    /// <summary>
    /// Wraps a returning agent phase action with a background liveness probe.
    /// If the probe detects a hang it cancels the phase's underlying CTS and
    /// throws <see cref="AgentStuckException"/>, which the caller's
    /// <c>catch</c> handles. The non-generic overload delegates here.
    ///
    /// <para>
    /// The <paramref name="phaseCancellation"/> parameter is also responsible
    /// for cancellation-source attribution: when the probe cancels the CTS,
    /// the post-fact catch filter records the source as
    /// <see cref="CancellationSources.StuckProbe"/> so the outer pipeline
    /// catch (if it ever sees an unwrapped OCE) can still tell apart a
    /// stuck-kill from a transient host cancellation.
    /// </para>
    /// </summary>
    private async Task<T> RunWithStuckProbeAsync<T>(
        WorkItem item,
        Project project,
        AgentKind agentKind,
        string phase,
        PhaseCancellation phaseCancellation,
        CancellationToken ct,
        Func<CancellationToken, Task<T>> work,
        CancellationToken? workToken = null)
    {
        var effectiveWorkToken = workToken ?? phaseCancellation.Token;
        var thresholdMinutes = ResolveEffectiveStuckThresholdMinutes(project);
        if (thresholdMinutes <= 0)
            return await work(effectiveWorkToken);

        ValidateStuckThreshold(thresholdMinutes, phase);

        var thresholdSamples = (int)Math.Ceiling(
            thresholdMinutes * 60.0 / StuckProbe.DefaultPollInterval.TotalSeconds);

        var ctx = new StuckContext { Phase = phase, AgentKind = agentKind };
        var source = ActivitySourceFactory();
        var probe = new StuckProbe(source, thresholdSamples, ctx, phaseCancellation.Cts, _log, StuckProbePollInterval);

        using var probeCts = new CancellationTokenSource();
        _ = probe.RunAsync(probeCts.Token); // fire-and-forget; self-terminating

        try
        {
            return await work(effectiveWorkToken);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && ctx.Detected)
        {
            // The probe cancelled directly via the CTS (no hook ran), so the
            // attribution slot may still be empty. Claim it now — before the
            // AgentStuckException propagates — so any observer that reads
            // PhaseCancellation.Source sees "stuck-probe".
            phaseCancellation.RecordStuckProbe();
            throw new AgentStuckException(ctx);
        }
        finally
        {
            probeCts.Cancel();
        }
    }

    private Task RunWithStuckProbeAsync(
        WorkItem item,
        Project project,
        AgentKind agentKind,
        string phase,
        PhaseCancellation phaseCancellation,
        CancellationToken ct,
        Func<CancellationToken, Task> work,
        CancellationToken? workToken = null)
        => RunWithStuckProbeAsync<bool>(item, project, agentKind, phase, phaseCancellation, ct,
            async pct => { await work(pct); return true; },
            workToken);

    private async Task HandleAgentStuckAsync(WorkItem item, Project project, AgentStuckException stuckEx)
    {
        var ctx = stuckEx.Context;
        _log.LogWarning("Work item {Id} agent stuck in phase '{Phase}' for {Seconds}s",
            item.Id, ctx.Phase, (int)ctx.StuckDuration.TotalSeconds);

        AuditLog.AgentStuckDetected(ctx.AgentKind, ctx.Phase, ctx.StuckDuration);
        AuditLog.AgentKilledByStuckProbe(ctx.AgentKind, ctx.Phase);

        var current = await _store.GetAsync(item.Id, CancellationToken.None) ?? item;

        await _webhooks.PublishAsync(new WebhookEvent
        {
            Event = "work_item.agent_stuck",
            WorkItem = current,
            Project = project,
            Details = new AgentStuckDetails
            {
                Phase = ctx.Phase,
                AgentKind = ctx.AgentKind.Value,
                StuckSeconds = (int)ctx.StuckDuration.TotalSeconds,
                Killed = true,
            },
        }, CancellationToken.None);

        if (project.Audit.AutoRetryOnStuck && current.StuckRetries < project.Audit.MaxStuckRetries)
        {
            // Re-queue from the same phase entry point.
            var retryFromState = ctx.Phase switch
            {
                "rework" => WorkItemState.WorkComplete,
                "merge" => WorkItemState.AuditPassed,
                _ => WorkItemState.Queued,
            };
            var retried = current with
            {
                State = retryFromState,
                StuckRetries = current.StuckRetries + 1,
                LastError = null,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await _store.UpdateAsync(retried, CancellationToken.None);
            _log.LogWarning(
                "Work item {Id} auto-retrying from phase '{Phase}' after stuck detection (retry {N}/{Max})",
                item.Id, ctx.Phase, retried.StuckRetries, project.Audit.MaxStuckRetries);
            AuditLog.WorkItemRetried(item.Id, ctx.Phase);
        }
        else
        {
            await TransitionFailed(item, stuckEx.Message, CancellationToken.None, project, failureKind: "agent");
        }
    }

    private int ResolveEffectiveStuckThresholdMinutes(Project project)
    {
        // ProjectAudit.StuckThresholdMinutes: -1 = inherit global, 0 = disabled, >0 = use it
        var projectVal = project.Audit.StuckThresholdMinutes;
        return projectVal < 0 ? _opts.StuckThresholdMinutes : projectVal;
    }

    private void ValidateStuckThreshold(int thresholdMinutes, string phase)
    {
        if (thresholdMinutes < 1)
            _log.LogWarning("Stuck probe: threshold {Min}min is below minimum 1 min for phase '{Phase}'",
                thresholdMinutes, phase);
    }

    // ── Intermediate webhook events (delegated) ─────────────────────────────
    //
    // Owned by PipelineWebhookPublisher; the one-line forwarders below keep
    // the existing intra-pipeline call-sites unchanged. See the collaborator
    // for the best-effort/cancellation contract.

    private Task TryPublishEventAsync(WorkItem item, Project project, string eventName, object details, CancellationToken ct)
        => _webhookPublisher.TryPublishEventAsync(item, project, eventName, details, ct);

    private Task PublishIterationStartedAsync(
        WorkItem item, Project project, string phase, int iteration, CancellationToken ct)
        => _webhookPublisher.PublishIterationStartedAsync(item, project, phase, iteration, ct);

    private Task PublishIterationCompletedAsync(
        WorkItem item, Project project, string phase, int iteration,
        string repoId, string workBranch, DateTimeOffset startedAt, CancellationToken ct)
        => _webhookPublisher.PublishIterationCompletedAsync(item, project, phase, iteration, repoId, workBranch, startedAt, ct);

    private Task PublishAuditStartedAsync(
        WorkItem item, Project project, int iteration, IReadOnlyList<IAuditor> auditors, CancellationToken ct)
        => _webhookPublisher.PublishAuditStartedAsync(item, project, iteration, auditors, ct);

    private Task PublishAuditFindingsEmittedAsync(
        WorkItem item, Project project, int iteration,
        IReadOnlyList<AuditFinding> findings, int blocking, int nonBlocking, CancellationToken ct)
        => _webhookPublisher.PublishAuditFindingsEmittedAsync(item, project, iteration, findings, blocking, nonBlocking, ct);

    private Task PublishAuditCompletedAsync(
        WorkItem item, Project project, int iteration, string verdict, DateTimeOffset startedAt, CancellationToken ct)
        => _webhookPublisher.PublishAuditCompletedAsync(item, project, iteration, verdict, startedAt, ct);

    private Task PublishMergeStartedAsync(
        WorkItem item, Project project, string baseBranch, string workBranch, CancellationToken ct)
        => _webhookPublisher.PublishMergeStartedAsync(item, project, baseBranch, workBranch, ct);

    private Task PublishMergeCompletedAsync(
        WorkItem item, Project project, string baseBranch, string workBranch,
        string? mergeSha, CancellationToken ct)
        => _webhookPublisher.PublishMergeCompletedAsync(item, project, baseBranch, workBranch, mergeSha, ct);

}
