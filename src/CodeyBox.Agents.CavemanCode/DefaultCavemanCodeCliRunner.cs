using CodeyBox.HostProcess;

namespace CodeyBox.Agents.CavemanCode;

/// <summary>
/// Host process runner for <c>caveman-code --list-models</c> via shared
/// <see cref="IProcessRunner"/>.
/// </summary>
public sealed class DefaultCavemanCodeCliRunner : ICavemanCodeCliRunner
{
    private const int MaxOutputBytes = 512 * 1024;
    private readonly IProcessRunner _runner;
    private readonly IReadOnlyDictionary<string, string>? _environment;

    public DefaultCavemanCodeCliRunner(
        IProcessRunner? runner = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        _runner = runner ?? new DefaultProcessRunner();
        _environment = environment ?? MinimalHostProcessEnvironment.ForCliAuthDiscovery();
    }

    public async Task<CavemanCodeCliRunResult> RunListModelsAsync(string binary, CancellationToken ct)
    {
        var result = await _runner.RunAsync(
            [binary, "--list-models"],
            stdin: null,
            ct,
            maxStdoutBytes: MaxOutputBytes,
            maxStderrBytes: MaxOutputBytes,
            environment: _environment).ConfigureAwait(false);

        if (result.StartFailed)
            return new CavemanCodeCliRunResult(1, "", "");

        return new CavemanCodeCliRunResult(result.ExitCode, result.Stdout, result.Stderr);
    }
}
