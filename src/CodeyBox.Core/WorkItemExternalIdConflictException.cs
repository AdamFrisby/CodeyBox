namespace CodeyBox.Core;

/// <summary>
/// Thrown when a work item INSERT fails because another item already holds the same
/// (projectId, externalId) pair. Signals a concurrent duplicate that slipped past the
/// application-level pre-check.
/// </summary>
public sealed class WorkItemExternalIdConflictException : Exception
{
    public WorkItemExternalIdConflictException()
        : base("externalId already exists in this project") { }
}
