using System.Collections.Concurrent;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="ClaudeSessionLifecycle"/>. Drives the full
/// open → turn → suspend → resume → turn → close arc against a fake session
/// runner and asserts:
/// <list type="bullet">
///   <item>ONE session id is captured on the first turn and reused for every
///   subsequent turn (the cross-rework "one session" acceptance criterion).</item>
///   <item>The worker VM is suspended between turns (the "stop during audit"
///   acceptance criterion).</item>
///   <item>The VM is resumed before each subsequent turn (the
///   "resume for next rework" acceptance criterion).</item>
///   <item>Disposal closes the session and tears down the VM (the
///   "terminal/cancel disposes VM" acceptance criterion).</item>
/// </list>
/// </summary>
public sealed class ClaudeSessionLifecycleTests
{
    [Fact]
    public async Task FullArc_OneSessionAcrossWorkAndRework_SuspendsBetweenTurns()
    {
        // Models the brief's full arc:
        //   open → work-turn → suspend (audit) → resume → rework-turn → suspend
        //   (audit) → resume → rework-turn → close
        var worker = new FakeSessionRunner();
        var sandbox = new RecordingSandbox("worker-vm-1");

        await using var lifecycle = await ClaudeSessionLifecycle.OpenAsync(
            worker,
            handleSnapshot: null,
            sandbox,
            workingDirectory: "/work",
            credential: null,
            modelId: null,
            reasoningMode: null,
            openedAgentRouteKey: AgentKind.Claude.Value,
            projectId: null,
            agentClassMember: null,
            ct: CancellationToken.None);

        // --- Work turn (turn 1) ---
        var workSandbox = await lifecycle.GetSandboxAsync(CancellationToken.None);
        Assert.Same(sandbox, workSandbox);
        var workResult = await lifecycle.SendTurnAsync("do the work", CancellationToken.None, stdoutChunkCallback: null);
        Assert.True(workResult.Success);
        await lifecycle.SuspendAsync(CancellationToken.None);

        // --- Rework turn 1 (turn 2) ---
        var reworkSandbox1 = await lifecycle.GetSandboxAsync(CancellationToken.None);
        Assert.Same(sandbox, reworkSandbox1);
        var reworkResult1 = await lifecycle.SendTurnAsync("address findings #1", CancellationToken.None, stdoutChunkCallback: null);
        Assert.True(reworkResult1.Success);
        await lifecycle.SuspendAsync(CancellationToken.None);

        // --- Rework turn 2 (turn 3) ---
        var reworkSandbox2 = await lifecycle.GetSandboxAsync(CancellationToken.None);
        Assert.Same(sandbox, reworkSandbox2);
        var reworkResult2 = await lifecycle.SendTurnAsync("address findings #2", CancellationToken.None, stdoutChunkCallback: null);
        Assert.True(reworkResult2.Success);
        await lifecycle.SuspendAsync(CancellationToken.None);

        // Lifecycle counted 3 turns.
        Assert.Equal(3, lifecycle.TurnsCompleted);

        // ONE session was used for all three turns. The fake runner records
        // the session id on open and asserts the same one is reused on every
        // SendTurn call.
        Assert.Equal(1, worker.OpenedSessions);
        Assert.Equal(3, worker.SendTurns);
        var observedHandleIds = worker.HandleIdsObserved.ToArray();
        Assert.Equal(3, observedHandleIds.Length);
        Assert.All(observedHandleIds, id => Assert.Equal(worker.OpenedHandleId, id));

        // The VM was suspended between each turn (3 turns, 3 suspends).
        Assert.Equal(3, worker.SuspendCalls);
        // The VM was resumed before each subsequent turn (turn 2 + turn 3 =
        // 2 resumes; the first turn doesn't need a resume because the VM is
        // already running after open).
        Assert.Equal(2, worker.ResumeCalls);
        // The session is still open until lifecycle DisposeAsync runs.
        Assert.Equal(0, worker.CloseCalls);
        Assert.False(lifecycle.IsClosed);
    }

    [Fact]
    public async Task DisposeAsync_ClosesUnderlyingSession_TearingDownTheWorkerVm()
    {
        // The lifecycle delegates VM teardown to the worker's
        // CloseSessionAsync — the real ClaudeSessionWorker.CloseSessionAsync
        // disposes the sandbox there, so the lifecycle exposes the close
        // observation; we don't double-dispose at the lifecycle layer.
        var worker = new FakeSessionRunner(disposeSandboxOnClose: true);
        var sandbox = new RecordingSandbox("worker-vm-2");
        worker.SandboxToDisposeOnClose = sandbox;

        var lifecycle = await ClaudeSessionLifecycle.OpenAsync(
            worker, handleSnapshot: null, sandbox, "/work", credential: null, modelId: null, reasoningMode: null,
            openedAgentRouteKey: AgentKind.Claude.Value, projectId: null, agentClassMember: null, ct: CancellationToken.None);

        Assert.False(sandbox.Disposed);

        await lifecycle.DisposeAsync();

        Assert.True(lifecycle.IsClosed);
        Assert.Equal(1, worker.CloseCalls);
        Assert.True(sandbox.Disposed);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent_AndDoesNotThrowSecondTime()
    {
        var worker = new FakeSessionRunner();
        var sandbox = new RecordingSandbox("worker-vm-3");

        var lifecycle = await ClaudeSessionLifecycle.OpenAsync(
            worker, handleSnapshot: null, sandbox, "/work", credential: null, modelId: null, reasoningMode: null,
            openedAgentRouteKey: AgentKind.Claude.Value, projectId: null, agentClassMember: null, ct: CancellationToken.None);

        await lifecycle.DisposeAsync();
        await lifecycle.DisposeAsync(); // second call is a no-op

        // Underlying CloseSessionAsync was only invoked once.
        Assert.Equal(1, worker.CloseCalls);
    }

    [Fact]
    public async Task SendTurnAsync_WhileSuspended_Throws()
    {
        var worker = new FakeSessionRunner();
        var sandbox = new RecordingSandbox("worker-vm-4");

        await using var lifecycle = await ClaudeSessionLifecycle.OpenAsync(
            worker, handleSnapshot: null, sandbox, "/work", credential: null, modelId: null, reasoningMode: null,
            openedAgentRouteKey: AgentKind.Claude.Value, projectId: null, agentClassMember: null, ct: CancellationToken.None);

        await lifecycle.SendTurnAsync("first", CancellationToken.None, stdoutChunkCallback: null);
        await lifecycle.SuspendAsync(CancellationToken.None);

        // Calling SendTurnAsync without first calling GetSandboxAsync (which
        // performs the resume) must throw — protects against accidentally
        // running a turn against a stopped VM.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lifecycle.SendTurnAsync("second", CancellationToken.None, stdoutChunkCallback: null));
    }

    [Fact]
    public async Task SuspendAsync_AfterClose_IsNoOp()
    {
        var worker = new FakeSessionRunner();
        var sandbox = new RecordingSandbox("worker-vm-5");

        var lifecycle = await ClaudeSessionLifecycle.OpenAsync(
            worker, handleSnapshot: null, sandbox, "/work", credential: null, modelId: null, reasoningMode: null,
            openedAgentRouteKey: AgentKind.Claude.Value, projectId: null, agentClassMember: null, ct: CancellationToken.None);

        await lifecycle.DisposeAsync();

        // Suspending an already-closed lifecycle is a no-op (don't crash a
        // RunAsync finally block that calls Suspend after the pipeline
        // already disposed the lifecycle).
        await lifecycle.SuspendAsync(CancellationToken.None);
        Assert.Equal(0, worker.SuspendCalls);
    }

    [Fact]
    public async Task FirstTurnComplete_FlipsAfterFirstSuccessfulSendTurn()
    {
        var worker = new FakeSessionRunner();
        var sandbox = new RecordingSandbox("worker-vm-6");

        await using var lifecycle = await ClaudeSessionLifecycle.OpenAsync(
            worker, handleSnapshot: null, sandbox, "/work", credential: null, modelId: null, reasoningMode: null,
            openedAgentRouteKey: AgentKind.Claude.Value, projectId: null, agentClassMember: null, ct: CancellationToken.None);

        Assert.False(lifecycle.FirstTurnComplete);
        await lifecycle.SendTurnAsync("first", CancellationToken.None, stdoutChunkCallback: null);
        Assert.True(lifecycle.FirstTurnComplete);
    }

    [Fact]
    public async Task HandleSnapshot_RefreshesHandleMetadataAfterEachTurn()
    {
        // Mirror the production wiring: after each turn the lifecycle
        // captures a refreshed handle from the runner. The captured handle
        // is the one a persistent store would write to durable state for
        // restart recovery — when the runner adds a CLI session id under
        // the metadata key on the first turn, the lifecycle exposes it.
        var worker = new FakeSessionRunner();
        var sandbox = new RecordingSandbox("worker-vm-7");

        // Hook simulates ClaudeSessionWorker.SnapshotPersistedHandle:
        // after the first turn it stamps the CLI session id under the
        // known metadata key (this is what production does).
        AgentSessionHandle Snapshot(AgentSessionHandle h)
        {
            var meta = h.Metadata is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(h.Metadata, StringComparer.Ordinal);
            meta[CodeyBox.Agents.Claude.ClaudeSessionWorker.CliSessionIdMetadataKey] = "cli-sess-fake-1";
            return h with { Metadata = meta };
        }

        await using var lifecycle = await ClaudeSessionLifecycle.OpenAsync(
            worker, handleSnapshot: Snapshot, sandbox, "/work", credential: null, modelId: null, reasoningMode: null,
            openedAgentRouteKey: AgentKind.Claude.Value, projectId: null, agentClassMember: null, ct: CancellationToken.None);

        // The lifecycle no longer hardcodes the Claude-specific metadata
        // key — callers pass the key their runner stamps under. Provider
        // coupling stays at the call site (here, the Claude test), not on
        // the orchestration boundary.
        var claudeKey = CodeyBox.Agents.Claude.ClaudeSessionWorker.CliSessionIdMetadataKey;
        Assert.Null(lifecycle.GetSessionIdFromMetadata(claudeKey));
        await lifecycle.SendTurnAsync("first", CancellationToken.None, stdoutChunkCallback: null);
        Assert.Equal("cli-sess-fake-1", lifecycle.GetSessionIdFromMetadata(claudeKey));
    }

    [Fact]
    public async Task OpenAsync_WhenRunnerOpenFails_DisposesProvisionedSandbox()
    {
        var worker = new FakeSessionRunner(failOpen: true);
        var sandbox = new RecordingSandbox("worker-vm-open-fail");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ClaudeSessionLifecycle.OpenAsync(
                worker,
                handleSnapshot: null,
                sandbox,
                "/work",
                credential: null,
                modelId: null,
                reasoningMode: null,
                openedAgentRouteKey: AgentKind.Claude.Value,
                projectId: null,
                agentClassMember: null,
                ct: CancellationToken.None));

        Assert.Equal("open failed", ex.Message);
        Assert.True(sandbox.Disposed);
    }

    [Fact]
    public async Task GetSandboxAsync_WhenResumeMarksFallback_ClosesAndRequiresFreshSandbox()
    {
        var worker = new FakeSessionRunner(disposeSandboxOnClose: true)
        {
            MarkFallbackOnResume = true,
        };
        var sandbox = new RecordingSandbox("worker-vm-resume-degrade");
        worker.SandboxToDisposeOnClose = sandbox;

        AgentSessionHandle Snapshot(AgentSessionHandle h)
        {
            if (!worker.FallbackMarked)
                return h;

            var metadata = h.Metadata is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(h.Metadata, StringComparer.Ordinal);
            metadata[AgentSessionMetadataKeys.FallbackToOneShot] = "true";
            return h with { Metadata = metadata };
        }

        var lifecycle = await ClaudeSessionLifecycle.OpenAsync(
            worker,
            handleSnapshot: Snapshot,
            sandbox,
            "/work",
            credential: null,
            modelId: null,
            reasoningMode: null,
            openedAgentRouteKey: AgentKind.Claude.Value,
            projectId: null,
            agentClassMember: null,
            ct: CancellationToken.None);

        await lifecycle.SendTurnAsync("first", CancellationToken.None, stdoutChunkCallback: null);
        await lifecycle.SuspendAsync(CancellationToken.None);

        await Assert.ThrowsAsync<AgentSessionDegradedException>(() =>
            lifecycle.GetSandboxAsync(CancellationToken.None));

        Assert.True(lifecycle.IsClosed);
        Assert.True(sandbox.Disposed);
        Assert.Equal(1, worker.CloseCalls);
    }

    [Fact]
    public async Task DisposeAsync_WhenCloseFails_DoesNotMarkClosed_AndCanRetry()
    {
        var worker = new FakeSessionRunner(closeFailures: 1);
        var sandbox = new RecordingSandbox("worker-vm-close-retry");

        var lifecycle = await ClaudeSessionLifecycle.OpenAsync(
            worker,
            handleSnapshot: null,
            sandbox,
            "/work",
            credential: null,
            modelId: null,
            reasoningMode: null,
            openedAgentRouteKey: AgentKind.Claude.Value,
            projectId: null,
            agentClassMember: null,
            ct: CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await lifecycle.DisposeAsync());
        Assert.False(lifecycle.IsClosed);

        await lifecycle.DisposeAsync();
        Assert.True(lifecycle.IsClosed);
        Assert.Equal(2, worker.CloseCalls);
    }

    // ─── test doubles ─────────────────────────────────────────────────────

    /// <summary>
    /// Programmable <see cref="ISessionAgentRunner"/> that records every
    /// open / send-turn / suspend / resume / close call. Each Send returns
    /// the same handle id, so tests can assert one session id flowed across
    /// every turn.
    /// </summary>
    private sealed class FakeSessionRunner : ISessionAgentRunner
    {
        private readonly bool _disposeSandboxOnClose;
        private readonly bool _failOpen;
        private int _closeFailuresRemaining;
        public FakeSessionRunner(
            bool disposeSandboxOnClose = false,
            bool failOpen = false,
            int closeFailures = 0)
        {
            _disposeSandboxOnClose = disposeSandboxOnClose;
            _failOpen = failOpen;
            _closeFailuresRemaining = closeFailures;
        }
        public AgentKind Kind => AgentKind.Claude;
        public int OpenedSessions;
        public int SendTurns;
        public int SuspendCalls;
        public int ResumeCalls;
        public int CloseCalls;
        public string? OpenedHandleId;
        public string? LastHandleIdOnTurn;
        public bool MarkFallbackOnResume { get; set; }
        public bool FallbackMarked { get; private set; }
        public RecordingSandbox? SandboxToDisposeOnClose { get; set; }
        public ConcurrentQueue<string> PromptsSent { get; } = new();
        public ConcurrentQueue<string> HandleIdsObserved { get; } = new();

        public Task<AgentResult> RunAsync(
            ISandbox sandbox, string workingDirectory, string prompt, AgentCredential? credential,
            string? modelId = null, string? reasoningMode = null,
            CancellationToken ct = default, Action<string>? stdoutChunkCallback = null, bool captureStructuredStream = false)
            => Task.FromResult(new AgentResult(true, "ok", null, null));

        public AgentFailureClassification ClassifyFailure(AgentResult result)
            => new(AgentFailureKind.Normal);

        public Task<AgentSessionHandle> OpenSessionAsync(
            ISandbox sandbox, string workingDirectory, AgentCredential? credential,
            string? modelId = null, string? reasoningMode = null, CancellationToken ct = default)
        {
            if (_failOpen)
                throw new InvalidOperationException("open failed");
            OpenedSessions++;
            var handleId = $"claude-session-{OpenedSessions}";
            OpenedHandleId = handleId;
            return Task.FromResult(new AgentSessionHandle(
                Kind,
                handleId,
                new AgentSessionSandboxRef(sandbox.Id),
                workingDirectory,
                modelId,
                reasoningMode));
        }

        public Task<AgentResult> SendTurnAsync(
            AgentSessionHandle sessionHandle, string prompt,
            CancellationToken ct = default, Action<string>? stdoutChunkCallback = null, bool captureStructuredStream = false)
        {
            SendTurns++;
            LastHandleIdOnTurn = sessionHandle.SessionId;
            HandleIdsObserved.Enqueue(sessionHandle.SessionId);
            PromptsSent.Enqueue(prompt);
            return Task.FromResult(new AgentResult(true, "ok", null, null));
        }

        public Task SuspendSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default)
        {
            SuspendCalls++;
            return Task.CompletedTask;
        }

        public Task ResumeSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default)
        {
            ResumeCalls++;
            if (MarkFallbackOnResume)
                FallbackMarked = true;
            return Task.CompletedTask;
        }

        public async Task CloseSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default)
        {
            CloseCalls++;
            if (_closeFailuresRemaining > 0)
            {
                _closeFailuresRemaining--;
                throw new InvalidOperationException("close failed");
            }
            if (_disposeSandboxOnClose && SandboxToDisposeOnClose is not null)
                await SandboxToDisposeOnClose.DisposeAsync();
        }
    }

    private sealed class RecordingSandbox : ISandbox
    {
        public RecordingSandbox(string id) { Id = id; }
        public string Id { get; }
        public bool Disposed { get; private set; }

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));

        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
