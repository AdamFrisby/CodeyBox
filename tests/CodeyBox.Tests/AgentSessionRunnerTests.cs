using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class AgentSessionRunnerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task StatelessAdapter_OpenTurnSuspendResumeTurnClose_DelegatesTurnsAndDisposesSandbox()
    {
        var inner = new RecordingAgentRunner();
        var runner = new StatelessSessionAgentRunner(inner);
        var sandbox = new RecordingSandbox("vm-session-1");
        var credential = new AgentCredential(
            AgentKind.Claude,
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));

        var handle = await runner.OpenSessionAsync(
            sandbox,
            "/work",
            credential,
            modelId: "claude-sonnet-test",
            reasoningMode: "high");
        var chunks = new List<string>();

        var first = await runner.SendTurnAsync(
            handle,
            "first turn",
            stdoutChunkCallback: chunks.Add,
            captureStructuredStream: true);
        await runner.SuspendSessionAsync(handle);
        await runner.ResumeSessionAsync(handle);
        var second = await runner.SendTurnAsync(handle, "second turn");
        await runner.CloseSessionAsync(handle);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(AgentKind.Claude, handle.RunnerKind);
        Assert.Equal("vm-session-1", handle.Sandbox.Id);
        Assert.StartsWith("stateless-claude-", handle.SessionId, StringComparison.Ordinal);
        Assert.Equal(["chunk:first turn"], chunks);
        Assert.Equal(1, sandbox.DisposeCount);

        Assert.Equal(2, inner.Calls.Count);
        Assert.Equal("first turn", inner.Calls[0].Prompt);
        Assert.Equal("second turn", inner.Calls[1].Prompt);
        Assert.All(inner.Calls, call =>
        {
            Assert.Same(sandbox, call.Sandbox);
            Assert.Same(credential, call.Credential);
            Assert.Equal("/work", call.WorkingDirectory);
            Assert.Equal("claude-sonnet-test", call.ModelId);
            Assert.Equal("high", call.ReasoningMode);
        });
        Assert.True(inner.Calls[0].CaptureStructuredStream);
        Assert.False(inner.Calls[1].CaptureStructuredStream);
    }

    [Fact]
    public void AgentSessionHandle_SerializeDeserialize_RoundTripsPersistedIdentityOnly()
    {
        var handle = new AgentSessionHandle(
            AgentKind.Claude,
            "claude-session-123",
            new AgentSessionSandboxRef(
                "codeybox-vm-123",
                Provider: "multipass",
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["profile"] = "default",
                }),
            "/work",
            ModelId: "claude-sonnet-test",
            ReasoningMode: "high",
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["cacheTtl"] = "5m",
            })
        {
            RuntimeSandbox = new RecordingSandbox("runtime-only"),
            RuntimeCredential = new AgentCredential(
                AgentKind.Claude,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["RUNTIME_ONLY_ENV"] = "runtime-only-value",
                },
                new Dictionary<string, string>(StringComparer.Ordinal)),
        };

        var json = JsonSerializer.Serialize(handle, JsonOptions);
        var copy = JsonSerializer.Deserialize<AgentSessionHandle>(json, JsonOptions);

        Assert.NotNull(copy);
        Assert.Equal(handle.RunnerKind, copy.RunnerKind);
        Assert.Equal(handle.SessionId, copy.SessionId);
        Assert.Equal(handle.Sandbox.Id, copy.Sandbox.Id);
        Assert.Equal(handle.Sandbox.Provider, copy.Sandbox.Provider);
        Assert.Equal("default", copy.Sandbox.Metadata!["profile"]);
        Assert.Equal(handle.WorkingDirectory, copy.WorkingDirectory);
        Assert.Equal(handle.ModelId, copy.ModelId);
        Assert.Equal(handle.ReasoningMode, copy.ReasoningMode);
        Assert.Equal("5m", copy.Metadata!["cacheTtl"]);
        Assert.Null(copy.RuntimeSandbox);
        Assert.Null(copy.RuntimeCredential);
        Assert.DoesNotContain("runtime-only-value", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatelessAdapter_SendTurnWhileSuspended_RejectsTurn()
    {
        var runner = new StatelessSessionAgentRunner(new RecordingAgentRunner());
        var handle = await runner.OpenSessionAsync(new RecordingSandbox("vm-session-2"), "/work", credential: null);

        await runner.SuspendSessionAsync(handle);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.SendTurnAsync(handle, "blocked turn"));
        Assert.Contains("suspended", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AsSessionRunner_WrapsStatelessRunnerAndPreservesNativeSessionRunner()
    {
        IAgentRunner stateless = new RecordingAgentRunner();
        var wrapped = stateless.AsSessionRunner();
        var native = new NativeSessionAgentRunner();

        Assert.IsType<StatelessSessionAgentRunner>(wrapped);
        Assert.Same(native, ((IAgentRunner)native).AsSessionRunner());
    }

    private sealed class RecordingAgentRunner : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Claude;
        public List<AgentCall> Calls { get; } = [];

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
            ct.ThrowIfCancellationRequested();
            Calls.Add(new AgentCall(
                sandbox,
                workingDirectory,
                prompt,
                credential,
                modelId,
                reasoningMode,
                captureStructuredStream));
            stdoutChunkCallback?.Invoke($"chunk:{prompt}");
            return Task.FromResult(new AgentResult(true, "ok", prompt, null));
        }
    }

    private sealed record AgentCall(
        ISandbox Sandbox,
        string WorkingDirectory,
        string Prompt,
        AgentCredential? Credential,
        string? ModelId,
        string? ReasoningMode,
        bool CaptureStructuredStream);

    private sealed class RecordingSandbox(string id) : ISandbox
    {
        public string Id { get; } = id;
        public int DisposeCount { get; private set; }

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            _ = exec;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NativeSessionAgentRunner : ISessionAgentRunner
    {
        private readonly StatelessSessionAgentRunner _inner = new(new RecordingAgentRunner());

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
            => _inner.RunAsync(
                sandbox,
                workingDirectory,
                prompt,
                credential,
                modelId,
                reasoningMode,
                ct,
                stdoutChunkCallback,
                captureStructuredStream);

        public Task<AgentSessionHandle> OpenSessionAsync(
            ISandbox sandbox,
            string workingDirectory,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default)
            => _inner.OpenSessionAsync(sandbox, workingDirectory, credential, modelId, reasoningMode, ct);

        public Task<AgentResult> SendTurnAsync(
            AgentSessionHandle sessionHandle,
            string prompt,
            CancellationToken ct = default,
            Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false)
            => _inner.SendTurnAsync(sessionHandle, prompt, ct, stdoutChunkCallback, captureStructuredStream);

        public Task SuspendSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default)
            => _inner.SuspendSessionAsync(sessionHandle, ct);

        public Task ResumeSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default)
            => _inner.ResumeSessionAsync(sessionHandle, ct);

        public Task CloseSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default)
            => _inner.CloseSessionAsync(sessionHandle, ct);
    }
}
