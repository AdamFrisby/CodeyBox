namespace CodeyBox.HostProcess;

/// <summary>
/// Construction-time policy for <see cref="DefaultProcessRunner"/>. The
/// default preserves the historical shared-runner behavior; callers that own
/// descendant processes may explicitly request an isolated Linux process
/// group so cancellation can verify that the complete group exited.
/// </summary>
public sealed record DefaultProcessRunnerOptions
{
    /// <summary>
    /// Launch the command through <c>setsid</c> on Linux and terminate the
    /// resulting process group on cancellation or an output-bound breach.
    /// Requires util-linux <c>setsid</c> at <c>/usr/bin/setsid</c> or
    /// <c>/bin/setsid</c>. Ignored on non-Linux hosts.
    /// </summary>
    public bool IsolateLinuxProcessGroup { get; init; }
}
