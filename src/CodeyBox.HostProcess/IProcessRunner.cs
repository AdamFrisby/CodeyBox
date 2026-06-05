namespace CodeyBox.HostProcess;

/// <summary>
/// Runs a host process with redirected streams. Shared by sandbox providers and
/// startup probes that need consistent cancellation, limits, and teardown.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(
        IReadOnlyList<string> argv,
        string? stdin,
        CancellationToken ct,
        Action<string>? stdoutChunkCallback = null,
        Action<string>? stderrChunkCallback = null,
        int? maxStdoutBytes = null,
        int? maxStderrBytes = null,
        IReadOnlyDictionary<string, string>? environment = null,
        bool killOnOutputLimit = true);
}
