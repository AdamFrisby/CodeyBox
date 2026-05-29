using CodeyBox.Agents.Cursor;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

public sealed class PipelineRunnerInvokeTextOnlyTests
{
    [Fact]
    public async Task InvokeTextOnlyAsync_SandboxMissing_ReturnsFailureForSandboxOnlyRunner()
    {
        var runner = new CursorAgentRunner();
        var cred = new AgentCredential(
            AgentKind.Cursor,
            new Dictionary<string, string> { ["CODEYBOX_CURSOR_AUTH_JSON"] = """{"token":"x"}""" },
            new Dictionary<string, string>());

        var result = await PipelineRunner.InvokeTextOnlyAsync(
            runner,
            sandbox: null,
            workingDirectory: null,
            "prompt",
            cred,
            modelId: null,
            reasoningMode: null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("sandbox", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeTextOnlyAsync_UnsupportedRunner_ReturnsFailure()
    {
        var runner = new WorkOnlyFakeRunner { Kind = AgentKind.Gemini };

        var result = await PipelineRunner.InvokeTextOnlyAsync(
            runner,
            sandbox: null,
            workingDirectory: null,
            "prompt",
            credential: null,
            modelId: null,
            reasoningMode: null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("does not support text-only", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class WorkOnlyFakeRunner : IAgentRunner
    {
        public required AgentKind Kind { get; init; }

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
            => Task.FromResult(new AgentResult(true, "ok", null, null));
    }

    [Fact]
    public async Task InvokeTextOnlyAsync_SandboxPresent_UsesSandboxPathForCursor()
    {
        var runner = new CursorAgentRunner();
        var sandbox = new RecordingSandbox(agentStdout: "review output");
        var cred = new AgentCredential(
            AgentKind.Cursor,
            new Dictionary<string, string> { ["CODEYBOX_CURSOR_AUTH_JSON"] = """{"token":"x"}""" },
            new Dictionary<string, string>());

        var result = await PipelineRunner.InvokeTextOnlyAsync(
            runner,
            sandbox,
            "/work",
            "prompt",
            cred,
            modelId: null,
            reasoningMode: null,
            CancellationToken.None);

        Assert.True(result.Success, result.Summary);
        Assert.Equal("review output", result.Output);
        Assert.DoesNotContain(
            sandbox.Execs.SelectMany(e => e.ExtraEnvironment?.Keys ?? []),
            k => k == "CODEYBOX_CURSOR_AUTH_JSON");
    }

    private sealed class RecordingSandbox : ISandbox
    {
        private readonly string _agentStdout;

        public RecordingSandbox(string agentStdout = "ok")
        {
            _agentStdout = agentStdout;
        }

        public string Id => "recording-invoke-text-only";
        public List<SandboxExec> Execs { get; } = [];

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Execs.Add(exec);
            if (exec.Argv.Count >= 3
                && exec.Argv[0] == "bash"
                && exec.Argv[2].Contains("CODEYBOX_CURSOR_AUTH_JSON", StringComparison.Ordinal))
            {
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            }

            if (exec.Argv.Count > 0 && exec.Argv[0] == "agent")
                return Task.FromResult(new SandboxExecResult(0, _agentStdout, ""));

            return Task.FromResult(new SandboxExecResult(0, "ok", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
