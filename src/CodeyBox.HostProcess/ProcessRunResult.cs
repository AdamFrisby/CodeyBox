namespace CodeyBox.HostProcess;

/// <summary>Outcome of a <see cref="IProcessRunner"/> invocation.</summary>
public readonly record struct ProcessRunResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool StdoutLimitExceeded = false,
    bool StderrLimitExceeded = false,
    bool StartFailed = false)
{
    public bool Success => ExitCode == 0 && !StartFailed;
}
