namespace CodeyBox.Core;

/// <summary>
/// Thrown by a sandbox provider when host-side provisioning exhausted a bounded
/// transient retry budget. The work item should be moved back to a durable
/// pre-phase state and re-enqueued after <see cref="RecheckIn"/> rather than
/// marked as an agent failure.
/// </summary>
public sealed class SandboxProvisioningDeferredException : Exception
{
    public SandboxProvisioningDeferredException(
        string provider,
        string operation,
        string errorClass,
        string detail,
        TimeSpan recheckIn)
        : base(BuildMessage(provider, operation, errorClass, detail))
    {
        Provider = provider;
        Operation = operation;
        ErrorClass = errorClass;
        Detail = detail;
        RecheckIn = recheckIn;
    }

    public string Provider { get; }
    public string Operation { get; }
    public string ErrorClass { get; }
    public string Detail { get; }
    public TimeSpan RecheckIn { get; }

    private static string BuildMessage(string provider, string operation, string errorClass, string detail)
    {
        var suffix = string.IsNullOrWhiteSpace(detail) ? "" : $": {detail.Trim()}";
        return $"sandbox provisioning deferred: provider={provider} operation={operation} errorClass={errorClass}{suffix}";
    }
}
