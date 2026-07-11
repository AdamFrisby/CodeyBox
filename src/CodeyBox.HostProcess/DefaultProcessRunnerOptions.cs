namespace CodeyBox.HostProcess;

/// <summary>
/// Construction-time policy for <see cref="DefaultProcessRunner"/>. The
/// default preserves the historical shared-runner behavior; callers that own
/// descendant processes may explicitly request an isolated Linux process
/// group so cancellation can verify that the complete group exited.
/// </summary>
public sealed record DefaultProcessRunnerOptions
{
    public static readonly TimeSpan DefaultCleanupTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultProcessGroupExitPollInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan MaximumCleanupTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumProcessGroupExitPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Launch the command through <c>setsid</c> on Linux and terminate the
    /// resulting process group on cancellation or an output-bound breach.
    /// Requires util-linux <c>setsid</c> at <c>/usr/bin/setsid</c> or
    /// <c>/bin/setsid</c>. Ignored on non-Linux hosts.
    /// </summary>
    public bool IsolateLinuxProcessGroup { get; init; }

    /// <summary>
    /// Independent deadline for terminating a failed or cancelled process and
    /// draining its redirected streams. Caller cancellation never shortens
    /// this safety cleanup window.
    /// </summary>
    public TimeSpan CleanupTimeout { get; init; } = DefaultCleanupTimeout;

    /// <summary>Delay between Linux process-group absence probes during cleanup.</summary>
    public TimeSpan ProcessGroupExitPollInterval { get; init; } = DefaultProcessGroupExitPollInterval;

    /// <summary>Throws when cleanup timing values fall outside bounded safety ranges.</summary>
    public static void Validate(DefaultProcessRunnerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.CleanupTimeout <= TimeSpan.Zero || options.CleanupTimeout > MaximumCleanupTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"CleanupTimeout must be positive and no more than {MaximumCleanupTimeout}.");
        }
        if (options.ProcessGroupExitPollInterval <= TimeSpan.Zero
            || options.ProcessGroupExitPollInterval > MaximumProcessGroupExitPollInterval
            || options.ProcessGroupExitPollInterval > options.CleanupTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"ProcessGroupExitPollInterval must be positive, no more than {MaximumProcessGroupExitPollInterval}, and no greater than CleanupTimeout.");
        }
    }
}
