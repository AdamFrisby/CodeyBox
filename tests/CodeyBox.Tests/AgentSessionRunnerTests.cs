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

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.SendTurnAsync(handle, "after close"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.CloseSessionAsync(handle));
        Assert.Equal(1, sandbox.DisposeCount);
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
            });

        var json = JsonSerializer.Serialize(handle, JsonOptions);
        var copy = JsonSerializer.Deserialize<AgentSessionHandle>(json, JsonOptions);

        var handleProperties = typeof(AgentSessionHandle).GetProperties();
        Assert.DoesNotContain(handleProperties, property => property.PropertyType == typeof(ISandbox));
        Assert.DoesNotContain(handleProperties, property => property.PropertyType == typeof(AgentCredential));
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
        Assert.DoesNotContain("RuntimeSandbox", json, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeCredential", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatelessAdapter_OpenSession_RejectsInvalidInputs()
    {
        var runner = new StatelessSessionAgentRunner(new RecordingAgentRunner());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => runner.OpenSessionAsync(null!, "/work", credential: null));
        await Assert.ThrowsAsync<ArgumentException>(
            () => runner.OpenSessionAsync(new RecordingSandbox("vm-open-blank"), " ", credential: null));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => runner.OpenSessionAsync(new RecordingSandbox("vm-open-canceled"), "/work", credential: null, ct: cts.Token));
    }

    [Fact]
    public async Task StatelessAdapter_SendTurn_RejectsInvalidInputs()
    {
        var inner = new RecordingAgentRunner();
        var runner = new StatelessSessionAgentRunner(inner);
        var handle = await runner.OpenSessionAsync(new RecordingSandbox("vm-send-validation"), "/work", credential: null);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => runner.SendTurnAsync(null!, "prompt"));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => runner.SendTurnAsync(handle, null!));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => runner.SendTurnAsync(handle, "prompt", ct: cts.Token));

        Assert.Empty(inner.Calls);
    }

    [Fact]
    public async Task StatelessAdapter_SendTurnWhileSuspended_RejectsTurn()
    {
        var inner = new RecordingAgentRunner();
        var runner = new StatelessSessionAgentRunner(inner);
        var handle = await runner.OpenSessionAsync(new RecordingSandbox("vm-session-2"), "/work", credential: null);

        await runner.SuspendSessionAsync(handle);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.SendTurnAsync(handle, "blocked turn"));
        Assert.Contains("suspended", ex.Message, StringComparison.Ordinal);
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public async Task StatelessAdapter_DeserializedHandle_ReattachesSandboxAndCredential()
    {
        var original = new StatelessSessionAgentRunner(
            new RecordingAgentRunner(),
            sandboxRefFactory: static sandbox => new AgentSessionSandboxRef(
                sandbox.Id,
                Provider: "test-provider",
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["region"] = "test-region",
                }));
        var opened = await original.OpenSessionAsync(
            new RecordingSandbox("vm-restart"),
            "/work",
            MakeCredential("initial"));
        var json = JsonSerializer.Serialize(opened, JsonOptions);
        var persisted = JsonSerializer.Deserialize<AgentSessionHandle>(json, JsonOptions)!;

        var inner = new RecordingAgentRunner();
        var reattachedSandbox = new RecordingSandbox("vm-restart");
        var resolvedCredential = MakeCredential("reattached");
        var reattachedRefs = new List<AgentSessionSandboxRef>();
        var credentialKinds = new List<AgentKind>();
        var restarted = new StatelessSessionAgentRunner(
            inner,
            sandboxReattacher: (sandboxRef, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                reattachedRefs.Add(sandboxRef);
                return Task.FromResult<ISandbox>(reattachedSandbox);
            },
            credentialResolver: (kind, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                credentialKinds.Add(kind);
                return Task.FromResult<AgentCredential?>(resolvedCredential);
            });

        await restarted.ResumeSessionAsync(persisted);
        var result = await restarted.SendTurnAsync(persisted, "after restart");

        Assert.True(result.Success);
        Assert.Single(reattachedRefs);
        Assert.Equal("vm-restart", reattachedRefs[0].Id);
        Assert.Equal("test-provider", reattachedRefs[0].Provider);
        Assert.Equal("test-region", reattachedRefs[0].Metadata!["region"]);
        Assert.Equal([AgentKind.Claude], credentialKinds);
        var call = Assert.Single(inner.Calls);
        Assert.Same(reattachedSandbox, call.Sandbox);
        Assert.Same(resolvedCredential, call.Credential);
        Assert.Equal("after restart", call.Prompt);
    }

    [Fact]
    public async Task StatelessAdapter_PersistedHandleWithoutReattacher_RejectsOperations()
    {
        var runner = new StatelessSessionAgentRunner(new RecordingAgentRunner());
        var handle = new AgentSessionHandle(
            AgentKind.Claude,
            "stateless-claude-persisted",
            new AgentSessionSandboxRef("vm-missing-runtime"),
            "/work");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.ResumeSessionAsync(handle));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.SendTurnAsync(handle, "prompt"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.CloseSessionAsync(handle));
    }

    [Fact]
    public async Task StatelessAdapter_WrongRunnerHandle_RejectsAllSessionOperations()
    {
        var runner = new StatelessSessionAgentRunner(new RecordingAgentRunner());
        var handle = new AgentSessionHandle(
            AgentKind.Codex,
            "stateless-codex-wrong-runner",
            new AgentSessionSandboxRef("vm-wrong-runner"),
            "/work");

        await Assert.ThrowsAsync<ArgumentException>(
            () => runner.SendTurnAsync(handle, "prompt"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => runner.SuspendSessionAsync(handle));
        await Assert.ThrowsAsync<ArgumentException>(
            () => runner.ResumeSessionAsync(handle));
        await Assert.ThrowsAsync<ArgumentException>(
            () => runner.CloseSessionAsync(handle));
    }

    [Fact]
    public async Task StatelessAdapter_CloseInactivePersistedHandle_ReattachesDisposesAndMarksClosed()
    {
        var sandbox = new RecordingSandbox("vm-close-persisted");
        var refs = new List<AgentSessionSandboxRef>();
        var runner = new StatelessSessionAgentRunner(
            new RecordingAgentRunner(),
            sandboxReattacher: (sandboxRef, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                refs.Add(sandboxRef);
                return Task.FromResult<ISandbox>(sandbox);
            });
        var handle = new AgentSessionHandle(
            AgentKind.Claude,
            "stateless-claude-close-persisted",
            new AgentSessionSandboxRef("vm-close-persisted"),
            "/work");

        await runner.CloseSessionAsync(handle);

        Assert.Single(refs);
        Assert.Equal(1, sandbox.DisposeCount);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.SendTurnAsync(handle, "after close"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.CloseSessionAsync(handle));
        Assert.Equal(1, sandbox.DisposeCount);
    }

    [Fact]
    public async Task StatelessAdapter_RunAsyncAndClassifyFailure_PassThroughToInnerRunner()
    {
        var inner = new RecordingAgentRunner
        {
            Classification = new AgentFailureClassification(AgentFailureKind.QuotaExhausted, Reason: "from-inner"),
        };
        var runner = new StatelessSessionAgentRunner(inner);
        var sandbox = new RecordingSandbox("vm-passthrough");

        var run = await runner.RunAsync(
            sandbox,
            "/work",
            "one shot",
            credential: null,
            modelId: "model",
            reasoningMode: "low",
            captureStructuredStream: true);
        var failure = new AgentResult(false, "failed", Stdout: null, Stderr: "stderr");
        var classification = runner.ClassifyFailure(failure);

        Assert.True(run.Success);
        var call = Assert.Single(inner.Calls);
        Assert.Same(sandbox, call.Sandbox);
        Assert.Equal("one shot", call.Prompt);
        Assert.Equal("model", call.ModelId);
        Assert.Equal("low", call.ReasoningMode);
        Assert.True(call.CaptureStructuredStream);
        Assert.Equal(AgentFailureKind.QuotaExhausted, classification.Kind);
        Assert.Equal("from-inner", classification.Reason);
        Assert.Same(failure, inner.ClassifiedResult);
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

    [Fact]
    public void AsSessionRunner_NullRunner_Throws()
    {
        IAgentRunner runner = null!;

        Assert.Throws<ArgumentNullException>(() => runner.AsSessionRunner());
    }

    private static AgentCredential MakeCredential(string value) =>
        new(
            AgentKind.Claude,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TOKEN"] = value,
            },
            new Dictionary<string, string>(StringComparer.Ordinal));

    private sealed class RecordingAgentRunner : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Claude;
        public List<AgentCall> Calls { get; } = [];
        public AgentFailureClassification Classification { get; set; } = new(AgentFailureKind.Normal);
        public AgentResult? ClassifiedResult { get; private set; }

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

        public AgentFailureClassification ClassifyFailure(AgentResult result)
        {
            ClassifiedResult = result;
            return Classification;
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
