using System.Text;
using CodeyBox.Core;

namespace CodeyBox.Agents.Claude;

/// <summary>
/// The default transport. Each turn is one <c>claude --print
/// --dangerously-skip-permissions [--resume &lt;id&gt;]</c> invocation inside
/// the sandbox — the behaviour that has been live on main. Session continuity
/// across turns is the captured Claude CLI session id passed back via
/// <c>--resume</c>, and the provider-side prompt cache TTL covers the gap
/// between turns.
///
/// <para>
/// This implementation owns NONE of the session state itself — it is a thin
/// per-turn adapter onto <see cref="ClaudeAgentRunner.RunSessionTurnAsync"/>
/// so the worker can swap transports without re-writing the cache-warm
/// resume mechanics.
/// </para>
/// </summary>
public sealed class PrintClaudeTransport : IClaudeTransport
{
    private readonly ClaudeAgentRunner _runner;

    public PrintClaudeTransport(ClaudeAgentRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public string Name => "print";
    public ClaudeSessionTransport Transport => ClaudeSessionTransport.Print;

    public Task<IClaudeTransportSession> OpenAsync(
        ClaudeTransportOpenRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IClaudeTransportSession>(new PrintSession(_runner, request));
    }

    private sealed class PrintSession : ICredentialRefreshableClaudeTransportSession
    {
        private readonly ClaudeAgentRunner _runner;
        private ClaudeTransportOpenRequest _open;

        public PrintSession(ClaudeAgentRunner runner, ClaudeTransportOpenRequest open)
        {
            _runner = runner;
            _open = open;
        }

        public async Task<ClaudeTransportTurnResult> SendTurnAsync(
            ClaudeTransportTurnRequest request,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);

            var stdoutCapture = new StringBuilder(1024);
            Action<string> aggregator = chunk =>
            {
                lock (stdoutCapture)
                {
                    stdoutCapture.Append(chunk);
                }
                request.StdoutChunkCallback?.Invoke(chunk);
            };

            var result = await _runner.RunSessionTurnAsync(
                _open.Sandbox,
                _open.WorkingDirectory,
                request.Prompt,
                _open.Credential,
                request.CliResumeSessionId,
                _open.ModelId,
                _open.ReasoningMode,
                captureStructuredStream: true,
                ct,
                aggregator).ConfigureAwait(false);

            var combinedStdout = stdoutCapture.Length > 0
                ? stdoutCapture.ToString()
                : result.Stdout ?? string.Empty;

            var captured = ClaudeSessionWorker.TryExtractCliSessionId(combinedStdout);

            return new ClaudeTransportTurnResult(result, combinedStdout, captured);
        }

        public void RefreshCredential(AgentCredential? credential) =>
            _open = _open with { Credential = credential };

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
