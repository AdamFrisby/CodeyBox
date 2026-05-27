namespace CodeyBox.Core;

/// <summary>
/// Thrown when a work item write fails because another item already holds the
/// same <c>(projectId, namespace, externalId)</c> triple. Signals a concurrent
/// duplicate that slipped past the application-level pre-check (or a PATCH
/// trying to add a namespaced ID that conflicts with an existing row).
/// </summary>
public sealed class WorkItemExternalIdConflictException : Exception
{
    public string? Namespace { get; }
    public string? ExternalId { get; }

    public WorkItemExternalIdConflictException()
        : base("externalId already exists in this project") { }

    public WorkItemExternalIdConflictException(string @namespace, string externalId)
        : base($"externalId '{externalId}' in namespace '{@namespace}' already exists in this project")
    {
        Namespace = @namespace;
        ExternalId = externalId;
    }
}
