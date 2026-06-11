using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies the stderr-tee behaviour added to
/// <see cref="CliAgentRunnerBase.ExecuteInvocationOnceAsync"/>: stderr is
/// teed into the stdout chunk callback ONLY for plaintext-fallback runs
/// (<c>captureStructuredStream=false</c>), so the agent stream-store
/// capture file picks up agy/opencode diagnostic lines that land on
/// stderr. For structured runs (<c>captureStructuredStream=true</c>)
/// stderr is withheld — interleaving stderr with stream-json on the same
/// callback channel would break per-line JSON framing in the captured
/// .jsonl when chunks arrive split at non-newline boundaries.
/// </summary>
public sealed class CliAgentStderrTeeTests
{
    [Fact]
    public async Task ExecuteInvocation_PlaintextRun_TeesStderrIntoStdoutCallback()
    {
        var sandbox = new CapturingTeeSandbox(
            stdoutChunks: ["stdout chunk\n"],
            stderrChunks: ["agy: token refresh failed\n", "opencode: applying patch\n"]);
        var runner = new TestRunner();
        var received = new List<string>();

        await runner.RunAsync(
            sandbox, "/work", "go", credential: null,
            stdoutChunkCallback: chunk => received.Add(chunk),
            captureStructuredStream: false);

        // Both channels must reach the callback when capture-structured is OFF.
        Assert.Contains("stdout chunk\n", received);
        Assert.Contains("agy: token refresh failed\n", received);
        Assert.Contains("opencode: applying patch\n", received);
        // The runner must register both callbacks on the SandboxExec — without
        // the stderr callback the sandbox would have no channel to deliver
        // stderr chunks even if produced.
        Assert.NotNull(sandbox.LastExec?.StdoutChunkCallback);
        Assert.NotNull(sandbox.LastExec?.StderrChunkCallback);
    }

    [Fact]
    public async Task ExecuteInvocation_StructuredRun_WithholdsStderrFromCallback()
    {
        var sandbox = new CapturingTeeSandbox(
            stdoutChunks: ["{\"type\":\"result\"}\n"],
            stderrChunks: ["WARN: ignored\n", "DEBUG: noise\n"]);
        var runner = new TestRunner();
        var received = new List<string>();

        await runner.RunAsync(
            sandbox, "/work", "go", credential: null,
            stdoutChunkCallback: chunk => received.Add(chunk),
            captureStructuredStream: true);

        // stdout-only — structured-stream mode must NOT interleave stderr.
        Assert.Contains("{\"type\":\"result\"}\n", received);
        Assert.DoesNotContain("WARN: ignored\n", received);
        Assert.DoesNotContain("DEBUG: noise\n", received);
        // The runner must pass null for the stderr callback so the sandbox
        // wrapper has no place to deliver stderr (matching the comment in
        // CliAgentRunnerBase about non-JSON noise corrupting JSONL framing).
        Assert.NotNull(sandbox.LastExec?.StdoutChunkCallback);
        Assert.Null(sandbox.LastExec?.StderrChunkCallback);
    }

    [Fact]
    public async Task ExecuteInvocation_NoStdoutCallback_StructuredStreamPropagatesNullStderrCallback()
    {
        // Defensive: when the caller did not pass a stdout callback at all,
        // structured-stream mode must still not synthesise a stderr callback.
        // (The runner's expression is `captureStructuredStream ? null :
        // stdoutChunkCallback`, but if the conditional ever changed to use a
        // default sink, this test would catch it.)
        var sandbox = new CapturingTeeSandbox(stdoutChunks: [], stderrChunks: []);
        var runner = new TestRunner();

        await runner.RunAsync(
            sandbox, "/work", "go", credential: null,
            stdoutChunkCallback: null,
            captureStructuredStream: true);

        Assert.Null(sandbox.LastExec?.StdoutChunkCallback);
        Assert.Null(sandbox.LastExec?.StderrChunkCallback);
    }

    private sealed class CapturingTeeSandbox : ISandbox
    {
        private readonly IReadOnlyList<string> _stdoutChunks;
        private readonly IReadOnlyList<string> _stderrChunks;

        public CapturingTeeSandbox(IReadOnlyList<string> stdoutChunks, IReadOnlyList<string> stderrChunks)
        {
            _stdoutChunks = stdoutChunks;
            _stderrChunks = stderrChunks;
        }

        public string Id => "tee-test-sandbox";
        public SandboxExec? LastExec { get; private set; }

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            LastExec = exec;
            foreach (var chunk in _stdoutChunks)
                exec.StdoutChunkCallback?.Invoke(chunk);
            foreach (var chunk in _stderrChunks)
                exec.StderrChunkCallback?.Invoke(chunk);
            return Task.FromResult(new SandboxExecResult(0, string.Concat(_stdoutChunks), string.Concat(_stderrChunks)));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestRunner : CliAgentRunnerBase
    {
        public override AgentKind Kind => new("test-stderr-tee");

        protected override AgentInvocation BuildInvocation(
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            bool captureStructuredStream = false)
            => new(["sh", "-c", prompt]);
    }
}
