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
    public async Task RunInjectionTurnAsync_SessionRunner_UsesOneShotRunAsync()
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
        Assert.Single(inner.RunCalls);
        Assert.Equal("session-prompt", inner.RunCalls[0]);
        Assert.Empty(sessionRunner.SessionCalls);
        Assert.Empty(sessionRunner.OpenedSessionIds);
        Assert.Empty(sessionRunner.SendTurnSessionIds);
        Assert.False(sandbox.Disposed);
        Assert.False(sessionRunner.SessionsClosed);
    }

    [Fact]
    public async Task RunAutonomousAndQueuedInjectionsAsync_SessionRunner_ReusesOneHandle()
    {
        var service = new AgentSupervisionService(
            () => new AgentSupervisionOptions { Enabled = true, InjectionQueueCapacity = 4 });
        await using var scope = await service.TryStartSessionAsync(Start())
            ?? throw new InvalidOperationException("expected supervision scope");
        var receipt = await service.EnqueueInjectionAsync(
            scope.SessionId,
            new AgentSupervisionInjectionRequest("stay in this conversation", "operator"));
        Assert.True(receipt.Accepted);

        var inner = new RecordingRunner();
        var sessionRunner = new RecordingSessionRunner(inner);
        var sandbox = new StubSandbox();

        var result = await AgentSupervisionTurnRunner.RunAutonomousAndQueuedInjectionsAsync(
            sessionRunner,
            sandbox,
            "/work",
            "autonomous-prompt",
            credential: null,
            modelId: "m",
            reasoningMode: "r",
            scope,
            stdoutCallback: null,
            captureStructuredStream: false,
            promptPreprocessor: null,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(sessionRunner.OpenedSessionIds);
        Assert.Equal(2, sessionRunner.SendTurnSessionIds.Count);
        Assert.All(sessionRunner.SendTurnSessionIds, id => Assert.Equal(sessionRunner.OpenedSessionIds[0], id));
        Assert.Equal(["autonomous-prompt", "## Live operator instruction"], [
            sessionRunner.SessionCalls[0],
            sessionRunner.SessionCalls[1][..Math.Min(sessionRunner.SessionCalls[1].Length, "## Live operator instruction".Length)],
        ]);
        Assert.True(sessionRunner.SessionsClosed);
        Assert.False(sandbox.Disposed);
    }

    [Fact]
    public async Task RunAutonomousAndQueuedInjectionsAsync_SessionRunnerPreservesSandboxTransportKind()
    {
        var service = new AgentSupervisionService(() => new AgentSupervisionOptions { Enabled = true });
        await using var scope = await service.TryStartSessionAsync(Start())
            ?? throw new InvalidOperationException("expected supervision scope");
        var inner = new RecordingRunner();
        var sessionRunner = new RecordingSessionRunner(inner);
        var sandbox = new StubSandbox(SandboxAgentOutputTransportKind.HttpIngest);

        var result = await AgentSupervisionTurnRunner.RunAutonomousAndQueuedInjectionsAsync(
            sessionRunner,
            sandbox,
            "/work",
            "autonomous-prompt",
            credential: null,
            modelId: "m",
            reasoningMode: "r",
            scope,
            stdoutCallback: null,
            captureStructuredStream: false,
            promptPreprocessor: null,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(SandboxAgentOutputTransportKind.HttpIngest, sessionRunner.OpenedSandboxTransportKinds.Single());
        Assert.False(sandbox.Disposed);
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
        public List<string> OpenedSessionIds { get; } = [];
        public List<SandboxAgentOutputTransportKind> OpenedSandboxTransportKinds { get; } = [];
        public List<string> SendTurnSessionIds { get; } = [];
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
        {
            var sessionId = "session-" + Guid.NewGuid().ToString("N");
            OpenedSessionIds.Add(sessionId);
            OpenedSandboxTransportKinds.Add(sandbox.AgentOutputTransportKind);
            return Task.FromResult(new AgentSessionHandle(
                Kind, sessionId,
                new AgentSessionSandboxRef(sandbox.Id), workingDirectory, modelId, reasoningMode));
        }

        public Task<AgentResult> SendTurnAsync(AgentSessionHandle sessionHandle, string prompt,
            CancellationToken ct = default, Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false)
        {
            SendTurnSessionIds.Add(sessionHandle.SessionId);
            _inner.SessionCalls.Add(prompt);
            return Task.FromResult(new AgentResult(true, "send-turn-ok", $"sturn:{prompt}", null));
        }

        public Task SuspendSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task ResumeSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default) =>
            Task.CompletedTask;

        public async Task CloseSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default)
        {
            // The supervision turn helper hands us a NonDisposingSandbox so
            // the caller-owned sandbox survives session close. We still record
            // that CloseSessionAsync was called so the test can assert the
            // session lifecycle ran.
            SessionsClosed = true;
            await Task.CompletedTask;
        }
    }

    private sealed class StubSandbox(
        SandboxAgentOutputTransportKind transportKind = SandboxAgentOutputTransportKind.ExecPipe) : ISandbox
    {
        public string Id => "stub-sandbox";
        public SandboxAgentOutputTransportKind AgentOutputTransportKind { get; } = transportKind;
        public bool Disposed { get; private set; }

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static AgentSupervisionSessionStart Start() =>
        new(
            WorkItemId.New(),
            "project",
            "work",
            1,
            AgentKind.Claude,
            AgentInstanceId: null,
            ModelId: null,
            ReasoningMode: null,
            SandboxId: "sandbox",
            WorkingDirectory: "/work",
            Source: "test");
}
