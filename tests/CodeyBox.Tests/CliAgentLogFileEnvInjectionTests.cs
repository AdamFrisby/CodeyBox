using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>
/// R8-core: <see cref="CliAgentRunnerBase"/> must forward
/// <see cref="AgentInvocationLogContext.CurrentLogPath"/> to the in-VM
/// codeybox-exec wrapper as
/// <see cref="SandboxConventions.AgentLogFileEnv"/>. Without the env var the
/// wrapper does not tee output and the suspend/resume cycle has nothing to
/// re-tail.
///
/// <para>The runner reads the AsyncLocal at <c>RunAsync</c> time, so we drive
/// the test through a real CliAgentRunnerBase subclass and a capturing
/// in-process sandbox to observe the <see cref="SandboxExec.ExtraEnvironment"/>
/// the runner ultimately produces.</para>
/// </summary>
public sealed class CliAgentLogFileEnvInjectionTests
{
    [Fact]
    public async Task RunAsync_WithLogPathInScope_InjectsAgentLogFileEnv()
    {
        var sandbox = new CapturingSandbox();
        var runner = new TestRunner();

        using (AgentInvocationLogContext.BeginScope("/work/.codeybox/agent-logs/wi-work-i0.log"))
        {
            await runner.RunAsync(sandbox, "/work", "echo ok", credential: null);
        }

        var env = sandbox.LastExec?.ExtraEnvironment;
        Assert.NotNull(env);
        Assert.Equal(SandboxAgentOutputTransportPreference.PreferHttpIngest, sandbox.LastExec?.AgentOutputTransport);
        Assert.Equal(SandboxExecLaunchMode.DetachedBatch, sandbox.LastExec?.LaunchMode);
        Assert.True(env!.TryGetValue(SandboxConventions.AgentLogFileEnv, out var path));
        Assert.Equal("/work/.codeybox/agent-logs/wi-work-i0.log", path);
    }

    [Fact]
    public async Task RunAsync_WithNoLogPath_OmitsAgentLogFileEnv()
    {
        // When the ambient AsyncLocal is unset (test/non-pipeline callers),
        // the wrapper falls back to its exec-without-tee path. A regression
        // that always injected the var would force every test to wire a log
        // directory inside the sandbox.
        var sandbox = new CapturingSandbox();
        var runner = new TestRunner();

        Assert.Null(AgentInvocationLogContext.CurrentLogPath);
        await runner.RunAsync(sandbox, "/work", "echo ok", credential: null);

        var env = sandbox.LastExec?.ExtraEnvironment;
        Assert.NotNull(env);
        Assert.Equal(SandboxAgentOutputTransportPreference.PreferHttpIngest, sandbox.LastExec?.AgentOutputTransport);
        Assert.Equal(SandboxExecLaunchMode.DetachedBatch, sandbox.LastExec?.LaunchMode);
        Assert.False(env!.ContainsKey(SandboxConventions.AgentLogFileEnv));
    }

    [Fact]
    public async Task RunAsync_WithExecPipeSandbox_DoesNotRequestDetachedHttpIngest()
    {
        var sandbox = new CapturingSandbox(SandboxAgentOutputTransportKind.ExecPipe);
        var runner = new TestRunner();

        await runner.RunAsync(sandbox, "/work", "echo ok", credential: null);

        Assert.Equal(SandboxAgentOutputTransportPreference.ExecPipe, sandbox.LastExec?.AgentOutputTransport);
        Assert.Equal(SandboxExecLaunchMode.Attached, sandbox.LastExec?.LaunchMode);
    }

    [Fact]
    public async Task RunAsync_WithEmptyLogPath_OmitsAgentLogFileEnv()
    {
        // BeginScope(null) and BeginScope("") both mean "do not capture";
        // the runner's empty-string check has to honour that or the wrapper
        // would receive an empty path and crash on the unquoted dirname.
        var sandbox = new CapturingSandbox();
        var runner = new TestRunner();

        using (AgentInvocationLogContext.BeginScope(""))
        {
            await runner.RunAsync(sandbox, "/work", "echo ok", credential: null);
        }

        var env = sandbox.LastExec?.ExtraEnvironment;
        Assert.NotNull(env);
        Assert.Equal(SandboxAgentOutputTransportPreference.PreferHttpIngest, sandbox.LastExec?.AgentOutputTransport);
        Assert.Equal(SandboxExecLaunchMode.DetachedBatch, sandbox.LastExec?.LaunchMode);
        Assert.False(env!.ContainsKey(SandboxConventions.AgentLogFileEnv));
    }

    [Fact]
    public async Task RunResumedAsync_WithLogPathInScope_InjectsAgentLogFileEnv()
    {
        // Resume path goes through the same WithAgentRunId helper. Cover it
        // explicitly so a refactor that diverged the two code paths is caught.
        // RunResumedAsync first calls RestoreScratchpadAsync (one ExecAsync)
        // then the agent invocation (a second ExecAsync). The sandbox records
        // both, and we assert on the LAST exec which is the agent invocation.
        var sandbox = new CapturingSandbox();
        var runner = new TestRunner();

        using (AgentInvocationLogContext.BeginScope("/work/.codeybox/agent-logs/wi-resume.log"))
        {
            await runner.RunResumedAsync(
                sandbox, "/work", "echo ok", credential: null,
                resume: new AgentResumeContext(CheckpointRef: "refs/codeybox/test/checkpoint"));
        }

        var env = sandbox.LastExec?.ExtraEnvironment;
        Assert.NotNull(env);
        Assert.Equal(SandboxAgentOutputTransportPreference.PreferHttpIngest, sandbox.LastExec?.AgentOutputTransport);
        Assert.Equal(SandboxExecLaunchMode.DetachedBatch, sandbox.LastExec?.LaunchMode);
        Assert.True(env!.TryGetValue(SandboxConventions.AgentLogFileEnv, out var path));
        Assert.Equal("/work/.codeybox/agent-logs/wi-resume.log", path);
    }

    /// <summary>
    /// Captures the most recent ExecAsync call so the test can assert on the
    /// env dictionary the runner produced. Tar-unpack ExecAsync calls from the
    /// resume path are short-circuited by returning success.
    /// </summary>
    private sealed class CapturingSandbox : ISandbox
    {
        private readonly SandboxAgentOutputTransportKind _transportKind;

        public CapturingSandbox(
            SandboxAgentOutputTransportKind transportKind = SandboxAgentOutputTransportKind.HttpIngest)
        {
            _transportKind = transportKind;
        }

        public string Id => "capturing-test-sandbox";
        public SandboxAgentOutputTransportKind AgentOutputTransportKind => _transportKind;
        public SandboxBatchLaunchMode BatchLaunchMode => _transportKind == SandboxAgentOutputTransportKind.HttpIngest
            ? SandboxBatchLaunchMode.Detached
            : SandboxBatchLaunchMode.Attached;
        public SandboxExec? LastExec { get; private set; }

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            LastExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestRunner : CliAgentRunnerBase
    {
        public override AgentKind Kind => new("test-log-env");

        protected override AgentInvocation BuildInvocation(
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            bool captureStructuredStream = false)
            => new(["sh", "-c", prompt]);
    }
}
