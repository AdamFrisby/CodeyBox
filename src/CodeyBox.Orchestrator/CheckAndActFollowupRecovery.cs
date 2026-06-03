using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

internal static class CheckAndActFollowupRecovery
{
    public static async Task<WorkItem?> FindExistingFollowupAsync(
        IWorkItemStore store,
        WorkItemId originCheckId,
        CancellationToken ct)
    {
        await foreach (var item in store.ListAsync(ct))
        {
            if (item.OriginCheckWorkItemId == originCheckId)
                return item;
        }

        return null;
    }

    public static async Task<WorkItem?> TryBuildCompletedFromPersistedVerdictAsync(
        IWorkItemStore store,
        WorkItem item,
        CancellationToken ct)
    {
        if (item.JobType != JobType.CheckAndAct || item.Verdict is null || item.Check is null)
            return null;

        if (item.Verdict.Answer == item.Check.ActionableAnswer)
        {
            var followup = await FindExistingFollowupAsync(store, item.Id, ct);
            if (followup is null)
                return null;
        }

        return item with
        {
            State = WorkItemState.Done,
            LastError = null,
            StartedAt = null,
            PreemptedAt = null,
            PreemptCheckpoint = null,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    public static async Task EnqueueIfReadyAsync(
        IWorkItemStore store,
        ITaskQueue? queue,
        WorkItem followup,
        CancellationToken ct)
    {
        if (queue is null || followup.State != WorkItemState.Queued)
            return;

        var depStates = new Dictionary<WorkItemId, WorkItemState>();
        foreach (var depId in followup.DependsOn)
        {
            var dep = await store.GetAsync(depId, ct);
            if (dep is not null)
                depStates[depId] = dep.State;
        }

        if (WorkItemDependencies.AreSatisfied(followup.DependsOn, depStates))
            await queue.EnqueueAsync(followup.Id, ct);
    }

    public static async Task EnqueueExistingFollowupIfActionableAsync(
        IWorkItemStore store,
        ITaskQueue? queue,
        WorkItem checkItem,
        CancellationToken ct)
    {
        if (checkItem.Verdict is null
            || checkItem.Check is null
            || checkItem.Verdict.Answer != checkItem.Check.ActionableAnswer)
        {
            return;
        }

        var followup = await FindExistingFollowupAsync(store, checkItem.Id, ct);
        if (followup is not null)
            await EnqueueIfReadyAsync(store, queue, followup, ct);
    }
}
