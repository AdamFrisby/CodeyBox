using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

/// <summary>
/// Single source of truth for recording an operator answer to a work item
/// question: persist via <see cref="IWorkItemQuestionStore.AnswerAsync"/>,
/// publish <c>work_item.question_answered</c>, record a human-review verdict
/// when the question backs a deployment review, and resume the work item when
/// all questions are resolved. Both <c>POST /workitems/{id}/answer</c> and
/// <c>POST /webhooks/interactions/{provider}</c> funnel through here so the
/// two ingresses can never diverge in what "answered" means.
/// </summary>
internal static class QuestionAnswerPipeline
{
    public static async Task AnswerAndResumeAsync(
        WorkItem item,
        string questionId,
        string redactedAnswer,
        string? answeredBy,
        string? decidedBy,
        IWorkItemQuestionStore questionStore,
        IWorkItemStore store,
        ITaskQueue queue,
        IWebhookDispatcher webhooks,
        IProjectRepository projects,
        IHumanDeploymentReviewStore? reviews,
        CancellationToken ct)
    {
        await questionStore.AnswerAsync(item.Id.ToString(), questionId, redactedAnswer, answeredBy, ct);

        var project = await projects.GetAsync(item.ProjectId, ct);
        await webhooks.PublishAsync(new WebhookEvent
        {
            Event = "work_item.question_answered",
            WorkItem = item,
            Project = project,
            Details = new QuestionAnsweredDetails(item.Id.ToString(), item.ProjectId.Value, questionId, redactedAnswer, AnsweredBy: answeredBy),
        }, ct);

        // A human-review backing question carries the operator's verdict:
        // exactly "approve" approves, any other answer rejects with the text
        // as notes.
        await TryRecordHumanReviewVerdictAsync(item.Id, questionId, redactedAnswer, decidedBy, reviews, ct);

        // Transition out of NeedsOperatorInput if all questions are now resolved.
        await MaybeResumeFromNeedsOperatorInputAsync(item, store, questionStore, queue, webhooks, project, ct);
    }

    /// <summary>
    /// Interprets an answer to a human-review backing question as a verdict.
    /// Best-effort: the answer itself is already persisted, so a missing
    /// review store, an already-decided review, or an expired review simply
    /// leaves the verdict unrecorded (the resume path still fails closed on
    /// expiry).
    /// </summary>
    internal static async Task TryRecordHumanReviewVerdictAsync(
        WorkItemId itemId,
        string questionId,
        string answer,
        string? decidedBy,
        IHumanDeploymentReviewStore? reviews,
        CancellationToken ct)
    {
        if (reviews is null || !HumanDeploymentReviewPolicy.IsReviewQuestion(questionId))
            return;
        var review = await reviews.GetActiveForWorkItemAsync(itemId.ToString(), ct);
        if (review is null
            || review.Status != HumanDeploymentReviewStatus.Pending
            || !string.Equals(review.QuestionId, questionId, StringComparison.Ordinal))
            return;
        var now = DateTimeOffset.UtcNow;
        if (now >= review.Deadline)
            return;
        var approved = HumanDeploymentReviewPolicy.IsApprovalAnswer(answer);
        await reviews.RecordVerdictAsync(
            review.WorkItemId,
            review.Iteration,
            approved,
            approved ? null : HumanDeploymentReviewPolicy.TruncateNotes(answer),
            decidedBy: decidedBy,
            now,
            ct);
    }

    /// <summary>
    /// When a work item is in NeedsOperatorInput state and all its questions are now
    /// resolved (answered or dismissed), transitions back to WorkComplete and re-enqueues.
    /// </summary>
    internal static async Task MaybeResumeFromNeedsOperatorInputAsync(
        WorkItem item,
        IWorkItemStore store,
        IWorkItemQuestionStore questionStore,
        ITaskQueue queue,
        IWebhookDispatcher webhooks,
        Project? project,
        CancellationToken ct)
    {
        var current = await store.GetAsync(item.Id, ct) ?? item;
        if (current.State != WorkItemState.NeedsOperatorInput) return;

        var allQuestions = await questionStore.ListByWorkItemAsync(item.Id.ToString(), ct);
        var hasOpen = allQuestions.Any(q => q.State == "open");
        if (hasOpen) return;

        var resumed = current.With(WorkItemState.WorkComplete);
        var transitioned = await store.TryUpdateIfStateAsync(resumed, WorkItemState.NeedsOperatorInput, ct);
        if (!transitioned) return;
        AuditLog.WorkItemTransitioned(item.Id, "WorkComplete (resumed from NeedsOperatorInput)");
        await queue.EnqueueAsync(item.Id, ct);

        if (project is not null)
        {
            await webhooks.PublishAsync(new WebhookEvent
            {
                Event = "work_item.work_complete",
                WorkItem = resumed,
                Project = project,
            }, ct);
        }
    }
}
