using CodeyBox.HostProcess;

namespace CodeyBox.Sandbox.Incus;

/// <summary>
/// Incus-only host process boundary. Every invocation must supply explicit
/// output limits, and Linux commands run in a dedicated process group so a
/// timeout or a terminating output-limit breach also cleans up CLI
/// descendants.
/// </summary>
internal sealed class IncusCliProcessRunner : IProcessRunner
{
    private readonly DefaultProcessRunner _inner = new(
        new DefaultProcessRunnerOptions { IsolateLinuxProcessGroup = true });

    public Task<ProcessRunResult> RunAsync(
        IReadOnlyList<string> argv,
        string? stdin,
        CancellationToken ct,
        Action<string>? stdoutChunkCallback = null,
        Action<string>? stderrChunkCallback = null,
        int? maxStdoutBytes = null,
        int? maxStderrBytes = null,
        IReadOnlyDictionary<string, string>? environment = null,
        bool killOnOutputLimit = true)
    {
        if (!maxStdoutBytes.HasValue || !maxStderrBytes.HasValue)
        {
            throw new ArgumentException(
                "Incus CLI process execution requires explicit stdout and stderr limits.");
        }

        return _inner.RunAsync(
            argv,
            stdin,
            ct,
            stdoutChunkCallback,
            stderrChunkCallback,
            maxStdoutBytes,
            maxStderrBytes,
            environment,
            killOnOutputLimit);
    }
}
