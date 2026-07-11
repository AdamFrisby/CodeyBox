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
    private readonly Func<IncusSandboxOptions> _optionsAccessor;
    private readonly TimeProvider _timeProvider;

    internal IncusCliProcessRunner()
        : this(static () => new IncusSandboxOptions(), TimeProvider.System)
    {
    }

    internal IncusCliProcessRunner(
        Func<IncusSandboxOptions> optionsAccessor,
        TimeProvider? timeProvider = null)
    {
        _optionsAccessor = optionsAccessor ?? throw new ArgumentNullException(nameof(optionsAccessor));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

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

        var options = _optionsAccessor()
            ?? throw new InvalidOperationException("The Incus options accessor returned null.");
        var runner = new DefaultProcessRunner(
            new DefaultProcessRunnerOptions
            {
                IsolateLinuxProcessGroup = true,
                CleanupTimeout = options.CliProcessCleanupTimeout,
                ProcessGroupExitPollInterval = options.CliProcessGroupExitPollInterval,
            },
            _timeProvider);
        return runner.RunAsync(
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
