namespace CodeyBox.Agents.Devin;

/// <summary>
/// Runs the <c>devin</c> CLI on the host for probes that need local output.
/// Abstracted so unit tests and the API composition root can substitute process
/// execution without leaking <see cref="CodeyBox.HostProcess.IProcessRunner"/>.
/// </summary>
public interface IDevinCliRunner
{
    /// <summary>
    /// Runs <c>{binary} models list --format json</c> and returns exit code
    /// plus captured streams.
    /// </summary>
    /// <exception cref="FileNotFoundException">When <paramref name="binary"/> cannot be executed.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">On Linux/macOS when the binary is absent from PATH (ENOENT).</exception>
    Task<DevinCliRunResult> RunModelsListAsync(string binary, CancellationToken ct);
}

public readonly record struct DevinCliRunResult(int ExitCode, string Stdout, string Stderr);
