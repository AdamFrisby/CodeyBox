namespace CodeyBox.Agents.Opencode;

/// <summary>
/// Runs the <c>opencode</c> CLI on the host for probes that need local output.
/// Abstracted so unit tests and the API composition root can substitute process
/// execution without leaking <see cref="CodeyBox.HostProcess.IProcessRunner"/>.
/// </summary>
public interface IOpencodeCliRunner
{
    /// <summary>
    /// Runs <c>{binary} models</c> and returns exit code plus captured streams.
    /// </summary>
    /// <exception cref="FileNotFoundException">When <paramref name="binary"/> cannot be executed.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">On Linux/macOS when the binary is absent from PATH (ENOENT).</exception>
    Task<OpencodeCliRunResult> RunModelsAsync(string binary, CancellationToken ct);
}

public readonly record struct OpencodeCliRunResult(int ExitCode, string Stdout, string Stderr);
