using CodeyBox.HostProcess;

namespace CodeyBox.Agents.Opencode;

/// <summary>
/// Host process runner for <c>opencode models</c> via shared <see cref="IProcessRunner"/>.
/// </summary>
internal sealed class DefaultOpencodeCliRunner : IOpencodeCliRunner
{
    private const int MaxOutputBytes = 512 * 1024;
    private readonly IProcessRunner _runner;
    private readonly IReadOnlyDictionary<string, string>? _environment;

    public DefaultOpencodeCliRunner(
        IProcessRunner? runner = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        _runner = runner ?? new DefaultProcessRunner();
        _environment = environment ?? MinimalHostProcessEnvironment.ForCliAuthDiscovery();
    }

    public async Task<OpencodeCliRunResult> RunModelsAsync(string binary, CancellationToken ct)
    {
        var result = await _runner.RunAsync(
            [binary, "models"],
            stdin: null,
            ct,
            maxStdoutBytes: MaxOutputBytes,
            maxStderrBytes: MaxOutputBytes,
            environment: _environment).ConfigureAwait(false);

        if (result.StartFailed)
            return new OpencodeCliRunResult(1, "", "");

        return new OpencodeCliRunResult(result.ExitCode, result.Stdout, result.Stderr);
    }
}
