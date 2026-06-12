using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Dispatches a single supervised agent turn — applying optional prompt
/// preprocessing and routing through <see cref="ISessionAgentRunner"/> when
/// the supplied runner is session-capable, so human injections preserve the
/// resumable-session/sanitisation invariants of session-aware runners
/// (e.g. <c>ClaudeSessionWorker</c>'s ACP transport and thinking-block
/// sanitiser). Falls back to <see cref="IAgentRunner.RunAsync"/> for
/// stateless runners.
/// </summary>
/// <remarks>
/// The caller owns the sandbox lifecycle, so when a session-capable runner is
/// used we wrap the sandbox in <see cref="NonDisposingSandbox"/> before
/// handing it to <see cref="ISessionAgentRunner.OpenSessionAsync"/>. The
/// session runner's <c>CloseSessionAsync</c> would otherwise dispose our
/// caller's sandbox.
/// </remarks>
public sealed class SupervisedTurnDispatcher
{
    private readonly IAgentRunner _runner;
    private readonly ISandbox _sandbox;
    private readonly string _workingDirectory;
    private readonly AgentCredential? _credential;
    private readonly string? _modelId;
    private readonly string? _reasoningMode;
    private readonly Action<string>? _stdoutCallback;
    private readonly bool _captureStructuredStream;
    private readonly Func<string, CancellationToken, Task<string>>? _promptPreprocessor;

    public SupervisedTurnDispatcher(
        IAgentRunner runner,
        ISandbox sandbox,
        string workingDirectory,
        AgentCredential? credential,
        string? modelId,
        string? reasoningMode,
        Action<string>? stdoutCallback,
        bool captureStructuredStream,
        Func<string, CancellationToken, Task<string>>? promptPreprocessor = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
        _workingDirectory = workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory));
        _credential = credential;
        _modelId = modelId;
        _reasoningMode = reasoningMode;
        _stdoutCallback = stdoutCallback;
        _captureStructuredStream = captureStructuredStream;
        _promptPreprocessor = promptPreprocessor;
    }

    /// <summary>
    /// Runs one injection turn. The supplied prompt is preprocessed (if a
    /// preprocessor was wired in) and dispatched through the runner. For
    /// <see cref="ISessionAgentRunner"/> implementations the dispatch path is
    /// <c>OpenSessionAsync</c> → <c>SendTurnAsync</c> → <c>CloseSessionAsync</c>
    /// over a non-disposing sandbox wrapper so the caller retains ownership
    /// of the underlying sandbox.
    /// </summary>
    public async Task<AgentResult> RunInjectionTurnAsync(
        AgentSupervisionInjectionTurn turn,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var prompt = _promptPreprocessor is null
            ? turn.Prompt
            : await _promptPreprocessor(turn.Prompt, ct).ConfigureAwait(false);

        if (_runner is ISessionAgentRunner sessionRunner)
        {
            var shielded = new NonDisposingSandbox(_sandbox);
            var handle = await sessionRunner.OpenSessionAsync(
                shielded, _workingDirectory, _credential, _modelId, _reasoningMode, ct)
                .ConfigureAwait(false);
            try
            {
                return await sessionRunner.SendTurnAsync(
                    handle, prompt, ct, _stdoutCallback, _captureStructuredStream)
                    .ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await sessionRunner.CloseSessionAsync(handle, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort cleanup. The session adapter may try to dispose
                    // the sandbox here; NonDisposingSandbox neutralises that and
                    // any other transport teardown failures are non-fatal to the
                    // supervision injection result.
                }
            }
        }

        return await _runner.RunAsync(
            _sandbox,
            _workingDirectory,
            prompt,
            _credential,
            _modelId,
            _reasoningMode,
            ct,
            stdoutChunkCallback: _stdoutCallback,
            captureStructuredStream: _captureStructuredStream).ConfigureAwait(false);
    }
}

/// <summary>
/// Forwards <see cref="ISandbox"/> calls to an inner instance but suppresses
/// <see cref="IAsyncDisposable.DisposeAsync"/>. Used when a caller wants to
/// hand a sandbox to a session-capable runner without losing ownership of
/// the lifecycle.
/// </summary>
internal sealed class NonDisposingSandbox : ISandbox
{
    private readonly ISandbox _inner;

    public NonDisposingSandbox(ISandbox inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public string Id => _inner.Id;

    public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        => _inner.ExecAsync(exec, ct);

    public Task<byte[]> GetScreenshotAsync(CancellationToken ct = default)
        => _inner.GetScreenshotAsync(ct);

    public Task SynthesizeInputAsync(IReadOnlyList<SandboxInputEvent> events, CancellationToken ct = default)
        => _inner.SynthesizeInputAsync(events, ct);

    public Task<SandboxAccessibilitySnapshot?> GetAccessibilityAtPointAsync(int x, int y, CancellationToken ct = default)
        => _inner.GetAccessibilityAtPointAsync(x, y, ct);

    public Task<string?> GetAccessibilityTreeJsonAsync(CancellationToken ct = default)
        => _inner.GetAccessibilityTreeJsonAsync(ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
