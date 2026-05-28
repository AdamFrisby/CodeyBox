namespace CodeyBox.Agents.Opencode;

/// <summary>
/// Runs the <c>opencode</c> CLI on the host for probes that need local output.
/// Abstracted so unit tests can stub exit codes and stdout without shelling out.
/// </summary>
internal interface IOpencodeCliRunner
{
    /// <summary>
    /// Runs <c>{binary} models</c> and returns exit code plus captured streams.
    /// </summary>
    /// <exception cref="FileNotFoundException">When <paramref name="binary"/> cannot be executed.</exception>
    Task<OpencodeCliRunResult> RunModelsAsync(string binary, CancellationToken ct);
}

internal readonly record struct OpencodeCliRunResult(int ExitCode, string Stdout, string Stderr);
