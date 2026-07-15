namespace CodeyBox.Sandbox.Incus;

/// <summary>
/// A liveness deadline tripped on an Incus CLI invocation or a guest-agent
/// readiness wait. Under concurrent VM boot load these are transient
/// host/incusd contention failures — not deterministic ones — so the provider
/// escalates them to
/// <see cref="CodeyBox.Core.SandboxProvisioningDeferredException"/> during
/// sandbox creation and the recovery stack auto-retries instead of parking the
/// work item as an unclassified failure.
/// <para>
/// Derives from <see cref="TimeoutException"/> so callers that only distinguish
/// timeouts keep working; <see cref="Operation"/> names the incus subcommand or
/// lifecycle step that hung so operators can see which call stalled.
/// </para>
/// </summary>
internal sealed class IncusTransientTimeoutException : TimeoutException
{
    public IncusTransientTimeoutException(string operation, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        Operation = operation;
    }

    /// <summary>
    /// The incus subcommand (e.g. <c>exec</c>, <c>start</c>, <c>file push</c>)
    /// or lifecycle step (e.g. <c>guest-agent readiness</c>) whose liveness
    /// deadline elapsed. Never contains untrusted argument values.
    /// </summary>
    public string Operation { get; }
}
