using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies the stderr-tee behaviour in
/// <see cref="CliAgentRunnerBase.ExecuteInvocationOnceAsync"/>: stderr is
/// teed verbatim into the stdout chunk callback for plaintext-fallback
/// runs (<c>captureStructuredStream=false</c>); for structured runs
/// (<c>captureStructuredStream=true</c>) each complete stderr line is
/// wrapped in a single-line JSON envelope before being forwarded, so the
/// captured .jsonl carries auth/usage diagnostics that fired before any
/// structured event was emitted without corrupting per-line framing.
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
    public async Task ExecuteInvocation_StructuredRun_WrapsStderrLinesInJsonEnvelopes()
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

        // The structured stream-json line is forwarded verbatim.
        Assert.Contains("{\"type\":\"result\"}\n", received);
        // Raw stderr lines must NOT be interleaved — they would break JSONL
        // framing when chunks arrive split at non-newline boundaries.
        Assert.DoesNotContain("WARN: ignored\n", received);
        Assert.DoesNotContain("DEBUG: noise\n", received);
        // Instead, each complete stderr line is wrapped in a single-line
        // JSON envelope so it lands in the .jsonl as a recoverable diagnostic
        // (auth/usage failures that fire before any structured event would
        // otherwise be invisible to post-mortem inspection).
        var envelopes = received
            .Where(chunk => chunk.Contains("codeybox.stderr", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, envelopes.Count);
        foreach (var envelope in envelopes)
        {
            Assert.EndsWith("\n", envelope);
            using var doc = JsonDocument.Parse(envelope.TrimEnd('\n'));
            Assert.Equal("codeybox.stderr", doc.RootElement.GetProperty("type").GetString());
            var text = doc.RootElement.GetProperty("text").GetString();
            Assert.False(string.IsNullOrEmpty(text));
        }
        Assert.Contains(envelopes, env => env.Contains("WARN: ignored", StringComparison.Ordinal));
        Assert.Contains(envelopes, env => env.Contains("DEBUG: noise", StringComparison.Ordinal));
        // Both callbacks must be registered — without them the sandbox would
        // have no channel to deliver stderr in the first place.
        Assert.NotNull(sandbox.LastExec?.StdoutChunkCallback);
        Assert.NotNull(sandbox.LastExec?.StderrChunkCallback);
    }

    [Fact]
    public async Task ExecuteInvocation_NoStdoutCallback_StructuredStreamPropagatesNullStderrCallback()
    {
        // Defensive: when the caller did not pass a stdout callback at all,
        // structured-stream mode must still not synthesise a stderr callback —
        // there is nowhere to forward the envelope-wrapped lines.
        var sandbox = new CapturingTeeSandbox(stdoutChunks: [], stderrChunks: []);
        var runner = new TestRunner();

        await runner.RunAsync(
            sandbox, "/work", "go", credential: null,
            stdoutChunkCallback: null,
            captureStructuredStream: true);

        Assert.Null(sandbox.LastExec?.StdoutChunkCallback);
        Assert.Null(sandbox.LastExec?.StderrChunkCallback);
    }

    [Fact]
    public async Task ExecuteInvocation_StructuredRun_StderrSplitAcrossNonNewlineChunks_BuffersIntoOneEnvelope()
    {
        // Sandbox docs explicitly permit chunks that split at non-newline
        // boundaries; this test pins the line-buffering behaviour so each
        // forwarded envelope still contains exactly one complete stderr line.
        var sandbox = new CapturingTeeSandbox(
            stdoutChunks: [],
            stderrChunks: ["agy: token ", "refresh ", "failed\nnext line\n"]);
        var runner = new TestRunner();
        var received = new List<string>();

        await runner.RunAsync(
            sandbox, "/work", "go", credential: null,
            stdoutChunkCallback: chunk => received.Add(chunk),
            captureStructuredStream: true);

        Assert.Equal(2, received.Count);
        using var firstDoc = JsonDocument.Parse(received[0].TrimEnd('\n'));
        Assert.Equal("agy: token refresh failed", firstDoc.RootElement.GetProperty("text").GetString());
        using var secondDoc = JsonDocument.Parse(received[1].TrimEnd('\n'));
        Assert.Equal("next line", secondDoc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task ExecuteInvocation_StructuredRun_FlushesTrailingStderrWithoutNewline()
    {
        // If the agent dies mid-write the final stderr line may lack a trailing
        // newline. Without flush on completion that line would be lost — which
        // is exactly the auth/usage failure mode the wrapping is meant to fix.
        var sandbox = new CapturingTeeSandbox(
            stdoutChunks: [],
            stderrChunks: ["fatal: token expired"]);
        var runner = new TestRunner();
        var received = new List<string>();

        await runner.RunAsync(
            sandbox, "/work", "go", credential: null,
            stdoutChunkCallback: chunk => received.Add(chunk),
            captureStructuredStream: true);

        Assert.Single(received);
        using var doc = JsonDocument.Parse(received[0].TrimEnd('\n'));
        Assert.Equal("fatal: token expired", doc.RootElement.GetProperty("text").GetString());
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
