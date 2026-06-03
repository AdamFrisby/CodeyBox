namespace CodeyBox.Core;

/// <summary>
/// Thrown when a work item write fails because another follow-up item already
/// references the same originating CheckAndAct work item.
/// </summary>
public sealed class WorkItemOriginCheckConflictException : Exception
{
    public WorkItemId OriginCheckWorkItemId { get; }

    public WorkItemOriginCheckConflictException(WorkItemId originCheckWorkItemId)
        : base($"a follow-up already exists for origin check work item {originCheckWorkItemId}")
    {
        OriginCheckWorkItemId = originCheckWorkItemId;
    }
}
