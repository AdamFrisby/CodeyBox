using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class SupervisedTurnDispatcherTests
{
    [Fact]
    public async Task RunInjectionTurnAsync_AppliesPreprocessorBeforeDispatch()
    {
        var runner = new RecordingRunner();
        var sandbox = new StubSandbox();
        var dispatcher = new SupervisedTurnDispatcher(
            runner, sandbox, "/work", credential: null,
            modelId: "model", reasoningMode: "high",
            stdoutCallback: null, captureStructuredStream: false,
            promptPreprocessor: (raw, _) => Task.FromResult($"PRE::{raw}"));

        var turn = new AgentSupervisionInjectionTurn(
            new AgentSupervisionInjection("agi-1", "alice", "msg", DateTimeOffset.UtcNow),
            "raw-prompt");
        var result = await dispatcher.RunInjectionTurnAsync(turn, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(runner.RunCalls);
        Assert.Equal("PRE::raw-prompt", runner.RunCalls[0]);
    }

    [Fact]
    public async Task RunInjectionTurnAsync_StatelessRunner_UsesRunAsync()
    {
        var runner = new RecordingRunner();
        var sandbox = new StubSandbox();
        var dispatcher = new SupervisedTurnDispatcher(
            runner, sandbox, "/work", credential: null,
            modelId: null, reasoningMode: null,
            stdoutCallback: null, captureStructuredStream: false);

        var turn = new AgentSupervisionInjectionTurn(
            new AgentSupervisionInjection("agi-1", "alice", "msg", DateTimeOffset.UtcNow),
            "the-prompt");
        var result = await dispatcher.RunInjectionTurnAsync(turn, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(runner.SessionCalls);
        Assert.Single(runner.RunCalls);
    }

    [Fact]
    public async Task RunInjectionTurnAsync_SessionRunner_RoutesThroughSendTurnAsync()
    {
        var inner = new RecordingRunner();
        var sessionRunner = new RecordingSessionRunner(inner);
        var sandbox = new StubSandbox();
        var dispatcher = new SupervisedTurnDispatcher(
            sessionRunner, sandbox, "/work", credential: null,
            modelId: "m", reasoningMode: "r",
            stdoutCallback: null, captureStructuredStream: false);

        var turn = new AgentSupervisionInjectionTurn(
            new AgentSupervisionInjection("agi-2", "alice", "msg", DateTimeOffset.UtcNow),
            "session-prompt");
        var result = await dispatcher.RunInjectionTurnAsync(turn, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(sessionRunner.SessionCalls);
        Assert.Equal("session-prompt", sessionRunner.SessionCalls[0]);
        // The injection MUST NOT have caused the caller's sandbox to be
        // disposed via CloseSessionAsync — non-disposing wrapper neutralises
        // the worker's lifecycle teardown.
        Assert.False(sandbox.Disposed);
        // CloseSessionAsync was still called on the wrapper so internal
        // session state was released.
        Assert.True(sessionRunner.SessionsClosed);
    }

    private sealed class RecordingRunner : IAgentRunner
    {
        public List<string> RunCalls { get; } = [];
        public List<string> SessionCalls { get; } = [];

        public AgentKind Kind => AgentKind.Claude;

        public Task<AgentResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false)
        {
            RunCalls.Add(prompt);
            return Task.FromResult(new AgentResult(true, "ok", $"out:{prompt}", null));
        }

        public AgentFailureClassification ClassifyFailure(AgentResult result)
            => new(AgentFailureKind.Unknown, Reason: "stub");
    }

    private sealed class RecordingSessionRunner : ISessionAgentRunner
    {
        private readonly RecordingRunner _inner;
        public List<string> SessionCalls => _inner.SessionCalls;
        public bool SessionsClosed { get; private set; }

        public RecordingSessionRunner(RecordingRunner inner) => _inner = inner;

        public AgentKind Kind => _inner.Kind;

        public Task<AgentResult> RunAsync(ISandbox sandbox, string workingDirectory, string prompt,
            AgentCredential? credential, string? modelId = null, string? reasoningMode = null,
            CancellationToken ct = default, Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false)
            => _inner.RunAsync(sandbox, workingDirectory, prompt, credential, modelId, reasoningMode,
                ct, stdoutChunkCallback, captureStructuredStream);

        public AgentFailureClassification ClassifyFailure(AgentResult result) => _inner.ClassifyFailure(result);

        public Task<AgentSessionHandle> OpenSessionAsync(ISandbox sandbox, string workingDirectory,
            AgentCredential? credential, string? modelId = null, string? reasoningMode = null,
            CancellationToken ct = default)
            => Task.FromResult(new AgentSessionHandle(
                Kind, "session-" + Guid.NewGuid().ToString("N"),
                new AgentSessionSandboxRef(sandbox.Id), workingDirectory, modelId, reasoningMode));

        public Task<AgentResult> SendTurnAsync(AgentSessionHandle sessionHandle, string prompt,
            CancellationToken ct = default, Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false)
        {
            _inner.SessionCalls.Add(prompt);
            return Task.FromResult(new AgentResult(true, "send-turn-ok", $"sturn:{prompt}", null));
        }

        public Task SuspendSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task ResumeSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default) =>
            Task.CompletedTask;

        public async Task CloseSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default)
        {
            // The dispatcher hands us a NonDisposingSandbox; calling
            // DisposeAsync on it would normally tear down the caller's
            // sandbox but the wrapper suppresses it. We still record that
            // CloseSessionAsync was called so the test can assert the
            // session lifecycle ran.
            SessionsClosed = true;
            await Task.CompletedTask;
        }
    }

    private sealed class StubSandbox : ISandbox
    {
        public string Id => "stub-sandbox";
        public bool Disposed { get; private set; }

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
