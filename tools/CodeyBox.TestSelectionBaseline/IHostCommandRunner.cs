namespace CodeyBox.TestSelectionProducer;

/// <summary>Outcome of one host process invocation. Argv is never a shell string.</summary>
public sealed record HostCommandResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool StdoutLimitExceeded = false,
    bool StderrLimitExceeded = false)
{
    public bool Success => ExitCode == 0 && !StdoutLimitExceeded && !StderrLimitExceeded;
}

/// <summary>
/// Runs a host process with an argv array, a working directory, and output
/// caps. Implementations must never concatenate a shell command string.
/// </summary>
public interface IHostCommandRunner
{
    Task<HostCommandResult> RunAsync(
        IReadOnlyList<string> argv,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? extraEnvironment,
        int maxStdoutChars,
        int maxStderrChars,
        TimeSpan timeout,
        CancellationToken ct);
}

internal static class HostCommandRun
{
    public static async Task<HostCommandResult> CappedAsync(
        IHostCommandRunner runner,
        IReadOnlyList<string> argv,
        string workingDirectory,
        int maxStdoutChars,
        int maxStderrChars,
        TimeSpan timeout,
        string label,
        CancellationToken ct)
    {
        try
        {
            return await runner.RunAsync(
                argv,
                workingDirectory,
                extraEnvironment: null,
                maxStdoutChars,
                maxStderrChars,
                timeout,
                ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new TestSelectionBaselineProduceException($"{label} timed out.", ex);
        }
    }
}
