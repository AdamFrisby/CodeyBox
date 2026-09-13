using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Shared fail-closed expiry for parked human deployment reviews, used by
/// both the background sweeper and the verdict endpoints (a late verdict
/// races the same path). For an undecided review past its deadline: tears
/// the held deployment down first (bounding the VM even if later steps
/// fail), marks the review expired, dismisses the backing question as
/// "expired unreviewed", and re-queues the item so the audit loop resumes
/// and records the blocking expiry finding. A verdict that wins the
/// compare-and-set race keeps its authority — expiry never overwrites a
/// recorded decision.
/// </summary>
public static class HumanReviewExpiry
{
    /// <summary>
    /// Expires <paramref name="review"/> when it is still pending. Returns
    /// false when the review already carries a verdict (the caller honours
    /// the recorded decision instead).
    /// </summary>
    public static async Task<bool> ExpireAsync(
        IHumanDeploymentReviewStore reviews,
        IDeploymentManager? deployments,
        IWorkItemStore? store,
        IWorkItemQuestionStore? questions,
        ITaskQueue? queue,
        IWebhookDispatcher? webhooks,
        HumanDeploymentReview review,
        DateTimeOffset now,
        ILogger? log = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        ArgumentNullException.ThrowIfNull(review);
        var logger = log ?? NullLogger.Instance;

        // Bound the deployment first: even if every step below fails, the VM
        // is gone and silence cannot become an implicit pass.
        if (deployments is not null
            && deployments.TryGetActive(review.DeploymentId, out var handle)
            && handle is not null)
        {
            try
            {
                await handle.DisposeAsync().ConfigureAwait(false);
                logger.LogInformation(
                    "Human-review expiry tore down deployment {DeploymentId} for work item {WorkItemId}",
                    review.DeploymentId, review.WorkItemId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex,
                    "Human-review expiry failed to tear down deployment {DeploymentId}; continuing with expiry",
                    review.DeploymentId);
            }
        }

        // CAS: a verdict that raced the expiry wins; the resume path owns it.
        if (!await reviews.MarkExpiredAsync(review.WorkItemId, review.Iteration, now, ct).ConfigureAwait(false))
            return false;

        if (questions is not null)
        {
            try
            {
                await questions.DismissAsync(
                    review.WorkItemId, review.QuestionId, "expired unreviewed", ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Human-review expiry failed to dismiss question {QuestionId}", review.QuestionId);
            }
        }

        if (store is null || queue is null)
            return true;

        WorkItemId itemId;
        try
        {
            itemId = WorkItemId.Parse(review.WorkItemId);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            logger.LogWarning(ex, "Human-review expiry could not parse work item id {WorkItemId}", review.WorkItemId);
            return true;
        }

        var current = await store.GetAsync(itemId, ct).ConfigureAwait(false);
        if (current is null || current.State != WorkItemState.NeedsOperatorInput)
            return true;

        var open = questions is null
            ? []
            : await questions.ListByWorkItemAsync(review.WorkItemId, ct).ConfigureAwait(false);
        if (open.Any(q => q.State == "open"))
            return true;

        var resumed = current.With(
            WorkItemState.WorkComplete,
            $"Human deployment review expired unreviewed at {now:O}; resuming for fail-closed audit.");
        if (!await store.TryUpdateIfStateAsync(resumed, WorkItemState.NeedsOperatorInput, ct).ConfigureAwait(false))
            return true;

        AuditLog.WorkItemTransitioned(itemId, "WorkComplete (human review expired unreviewed)");
        await queue.EnqueueAsync(itemId, ct).ConfigureAwait(false);
        if (webhooks is not null)
        {
            await webhooks.PublishAsync(new WebhookEvent
            {
                Event = "work_item.work_complete",
                WorkItem = resumed,
                Project = null,
                Details = new HumanReviewExpiredDetails(
                    review.WorkItemId, review.Iteration, review.DeploymentId, now),
            }, CancellationToken.None).ConfigureAwait(false);
        }

        return true;
    }
}
