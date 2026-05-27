namespace CodeyBox.Agents;

/// <summary>
/// Ambient context the orchestrator uses to ask the CLI agent runners to
/// tee'd-capture an invocation's stdout/stderr into an in-VM log file. The
/// path is set BEFORE the runner is invoked, so the runner can pass it to
/// the codeybox-exec wrapper via the
/// <see cref="CodeyBox.Sandbox.SandboxConventions.AgentLogFileEnv"/>
/// environment variable. The orchestrator persists the path on the work item
/// before the agent runs so a multipass suspend/start cycle can re-tail the
/// log on the resumed VM.
///
/// <para>This is a side-channel (AsyncLocal-backed) rather than a new
/// parameter on <see cref="CodeyBox.Core.IAgentRunner"/> so the 30+ existing
/// implementations of the interface — most of them fakes/test doubles — don't
/// need to take a parameter they ignore. Production runners (everything that
/// derives from <see cref="CliAgentRunnerBase"/>) read this on every
/// invocation.</para>
/// </summary>
public static class AgentInvocationLogContext
{
    private static readonly AsyncLocal<string?> _logPath = new();

    /// <summary>
    /// The agent log path the orchestrator has assigned for the active
    /// invocation, or null when capture is not requested (test/non-pipeline
    /// callers). Path is interpreted INSIDE the sandbox; the orchestrator
    /// picks a path under <see cref="CodeyBox.Sandbox.SandboxConventions.AgentLogDir"/>.
    /// </summary>
    public static string? CurrentLogPath => _logPath.Value;

    /// <summary>
    /// Sets the active log path for the duration of the returned scope.
    /// Disposing restores the previous value, so nested invocations
    /// (work-phase nested under an audit retry, for example) are isolated.
    /// </summary>
    public static IDisposable BeginScope(string? logPath)
    {
        var previous = _logPath.Value;
        _logPath.Value = logPath;
        return new RestoreScope(previous);
    }

    private sealed class RestoreScope : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;
        public RestoreScope(string? previous) { _previous = previous; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _logPath.Value = _previous;
        }
    }
}
