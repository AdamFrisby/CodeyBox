using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

internal static class WorkItemAgentPauseParking
{
    public sealed record Result(bool Updated, WorkItem? Parked, string Reason, string RetryFrom);

    public static async Task<Result> ParkAsync(
        IWorkItemStore store,
        IWebhookDispatcher? webhooks,
        ILogger log,
        WorkItem item,
        string? reason,
        Project? project,
        AgentKind? pausedAgent,
        CancellationToken ct,
        string? retryFrom = null)
    {
        var current = await store.GetAsync(item.Id, ct).ConfigureAwait(false) ?? item;
        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? "agent paused by operator"
            : reason.Trim();
        var normalizedRetryFrom = retryFrom is null
            ? AgentPauseResumeMapper.RetryFromForState(current.State)
            : AgentPauseResumeMapper.NormalizeRetryFrom(retryFrom);
        var target = pausedAgent ?? current.AgentPauseTarget;
        // Stamp the resume entry-point on the dedicated agent_pause_retry_from
        // column rather than overloading quota_retry_from: agent-pause parking
        // is not quota recovery, and the WorkItemDto / scheduler boundary keeps
        // the two deferral mechanisms cleanly separated.
        var next = current.With(
            WorkItemState.WaitingForAgentResume,
            $"waiting: agent paused: {normalizedReason}") with
        {
            FailureKind = null,
            QuotaResetAt = null,
            NextQuotaRetryAt = null,
            QuotaRetryFrom = null,
            QuotaRetryPhase = null,
            NextTransientRetryAt = null,
            TransientRetryAttempts = 0,
            TransientRetryFirstFailedAt = null,
            AgentPauseRetryFrom = normalizedRetryFrom,
            StartedAt = null,
            AgentPauseTarget = target,
        };

        var updated = await store.TryUpdateIfStateAsync(next, current.State, ct).ConfigureAwait(false);
        if (!updated)
        {
            log.LogInformation(
                "Work item {Id} state changed concurrently; skipping WaitingForAgentResume transition",
                item.Id);
            return new Result(false, null, normalizedReason, normalizedRetryFrom);
        }

        AuditLog.AgentPauseDispatchDeferred(item.Id, normalizedReason, normalizedRetryFrom);
        if (webhooks is not null)
        {
            try
            {
                await webhooks.PublishAsync(new WebhookEvent
                {
                    Event = "work_item.waiting_for_agent_resume",
                    WorkItem = next,
                    Project = project,
                    Details = new
                    {
                        reason = normalizedReason,
                        retryFrom = normalizedRetryFrom,
                        pausedAgent = target?.Value,
                    },
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogWarning(
                    ex,
                    "WaitingForAgentResume transition for work item {Id} succeeded, but webhook delivery failed",
                    item.Id);
            }
        }

        return new Result(true, next, normalizedReason, normalizedRetryFrom);
    }
}
