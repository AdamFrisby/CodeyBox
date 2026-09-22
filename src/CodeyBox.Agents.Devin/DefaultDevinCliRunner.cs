using CodeyBox.HostProcess;

namespace CodeyBox.Agents.Devin;

/// <summary>
/// Host process runner for <c>devin models list</c> via shared
/// <see cref="IProcessRunner"/>.
/// </summary>
public sealed class DefaultDevinCliRunner : IDevinCliRunner
{
    private const int MaxOutputBytes = 512 * 1024;
    private readonly IProcessRunner _runner;
    private readonly IReadOnlyDictionary<string, string>? _environment;

    public DefaultDevinCliRunner(
        IProcessRunner? runner = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        _runner = runner ?? new DefaultProcessRunner();
        _environment = environment ?? MinimalHostProcessEnvironment.ForCliAuthDiscovery();
    }

    public async Task<DevinCliRunResult> RunModelsListAsync(string binary, CancellationToken ct)
    {
        var result = await _runner.RunAsync(
            [binary, "models", "list", "--format", "json"],
            stdin: null,
            ct,
            maxStdoutBytes: MaxOutputBytes,
            maxStderrBytes: MaxOutputBytes,
            environment: _environment).ConfigureAwait(false);

        if (result.StartFailed)
            return new DevinCliRunResult(1, "", "");

        return new DevinCliRunResult(result.ExitCode, result.Stdout, result.Stderr);
    }
}
