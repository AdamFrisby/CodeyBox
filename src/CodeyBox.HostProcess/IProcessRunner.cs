namespace CodeyBox.HostProcess;

/// <summary>
/// Runs a host process with redirected streams. Shared by sandbox providers and
/// startup probes that need consistent cancellation, limits, and teardown.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="argv"/> (an argv array, never a shell string).
    /// When <paramref name="environment"/> is supplied it REPLACES the child
    /// environment (ambient variables are not inherited); callers wanting an
    /// overlay merge it themselves. <paramref name="workingDirectory"/> sets
    /// the child working directory; null inherits the runner's current
    /// directory.
    /// </summary>
    Task<ProcessRunResult> RunAsync(
        IReadOnlyList<string> argv,
        string? stdin,
        CancellationToken ct,
        Action<string>? stdoutChunkCallback = null,
        Action<string>? stderrChunkCallback = null,
        int? maxStdoutBytes = null,
        int? maxStderrBytes = null,
        IReadOnlyDictionary<string, string>? environment = null,
        bool killOnOutputLimit = true,
        string? workingDirectory = null);
}
