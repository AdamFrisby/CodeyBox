namespace CodeyBox.Core;

/// <summary>
/// Thrown by a sandbox provider when host-side provisioning exhausted a bounded
/// transient retry budget. The work item should be moved back to a durable
/// pre-phase state and re-enqueued after <see cref="RecheckIn"/> rather than
/// marked as an agent failure.
///
/// <para>Base type for every deferrable sandbox-provisioning failure, including
/// <see cref="SandboxDiskDeferredException"/>. Catch sites must filter on this
/// base type (not on a single leaf) so a new deferral kind cannot silently fall
/// into a terminal-failure arm.</para>
/// </summary>
public class SandboxProvisioningDeferredException : Exception
{
    public SandboxProvisioningDeferredException(
        string provider,
        string operation,
        string errorClass,
        string detail,
        TimeSpan recheckIn,
        string? retainedSandboxName = null,
        string? retainedSandboxLifecycleProviderId = null,
        string? retainedSandboxHostId = null,
        Exception? innerException = null)
        : this(
            BuildMessage(provider, operation, errorClass, detail),
            provider,
            operation,
            errorClass,
            detail,
            recheckIn,
            retainedSandboxName,
            retainedSandboxLifecycleProviderId,
            retainedSandboxHostId,
            innerException)
    {
    }

    protected SandboxProvisioningDeferredException(
        string message,
        string provider,
        string operation,
        string errorClass,
        string detail,
        TimeSpan recheckIn,
        string? retainedSandboxName = null,
        string? retainedSandboxLifecycleProviderId = null,
        string? retainedSandboxHostId = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Provider = provider;
        Operation = operation;
        ErrorClass = errorClass;
        Detail = detail;
        RecheckIn = recheckIn;
        RetainedSandboxName = retainedSandboxName;
        RetainedSandboxLifecycleProviderId = retainedSandboxLifecycleProviderId;
        RetainedSandboxHostId = retainedSandboxHostId;
    }

    public string Provider { get; }
    public string Operation { get; }
    public string ErrorClass { get; }
    public string Detail { get; }
    public TimeSpan RecheckIn { get; }
    public string? RetainedSandboxName { get; }
    public string? RetainedSandboxLifecycleProviderId { get; }
    public string? RetainedSandboxHostId { get; }

    private static string BuildMessage(string provider, string operation, string errorClass, string detail)
    {
        var suffix = string.IsNullOrWhiteSpace(detail) ? "" : $": {detail.Trim()}";
        return $"sandbox provisioning deferred: provider={provider} operation={operation} errorClass={errorClass}{suffix}";
    }
}
