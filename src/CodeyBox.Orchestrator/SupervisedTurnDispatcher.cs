using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Dispatches a single supervised agent turn for one-shot runners, applying
/// optional prompt preprocessing before calling <see cref="IAgentRunner.RunAsync"/>.
/// </summary>
/// <remarks>
/// Session-capable runners must be handled by
/// <see cref="AgentSupervisionTurnRunner"/>, which owns the active
/// <see cref="AgentSessionHandle"/> across the autonomous turn and queued
/// injections. This dispatcher deliberately never opens a fresh native
/// session for an injection turn.
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
    /// preprocessor was wired in) and dispatched through the runner's one-shot
    /// <see cref="IAgentRunner.RunAsync"/> contract.
    /// </summary>
    public async Task<AgentResult> RunInjectionTurnAsync(
        AgentSupervisionInjectionTurn turn,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var prompt = _promptPreprocessor is null
            ? turn.Prompt
            : await _promptPreprocessor(turn.Prompt, ct).ConfigureAwait(false);

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

    public Task KillActiveExecsAsync(CancellationToken ct = default)
        => _inner.KillActiveExecsAsync(ct);

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
