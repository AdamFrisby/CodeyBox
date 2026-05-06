using CodeyBox.Agents.Codex;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class CodexAgentRunnerTests
{
    [Fact]
    public async Task RunResumedAsync_WithCodexAuthJson_MaterialisesAuthBeforeInvokingCodex()
    {
        const string authJson = """{"tokens":{"access_token":"test"}}""";
        var sandbox = new RecordingSandbox();
        var runner = new CodexAgentRunner();
        var credential = new AgentCredential(
            AgentKind.Codex,
            new Dictionary<string, string> { ["CODEX_AUTH_JSON"] = authJson },
            new Dictionary<string, string>());

        var result = await runner.RunResumedAsync(
            sandbox,
            "/work",
            "resume prompt",
            credential,
            new AgentResumeContext("refs/heads/codeybox/preempt/wi"));

        Assert.True(result.Success);
        Assert.Equal(3, sandbox.Execs.Count);
        Assert.Equal(authJson, sandbox.Execs[1].Stdin);
        Assert.Contains("$HOME/.codex/auth.json", sandbox.Execs[1].Argv[2]);
        Assert.Equal("codex", sandbox.Execs[2].Argv[0]);
        Assert.Equal("exec", sandbox.Execs[2].Argv[1]);
    }

    [Fact]
    public async Task RunResumedAsync_WhenAuthMaterialisationFails_DoesNotInvokeCodex()
    {
        var sandbox = new RecordingSandbox(authWriteExitCode: 7);
        var runner = new CodexAgentRunner();
        var credential = new AgentCredential(
            AgentKind.Codex,
            new Dictionary<string, string> { ["CODEX_AUTH_JSON"] = "{}" },
            new Dictionary<string, string>());

        var result = await runner.RunResumedAsync(
            sandbox,
            "/work",
            "resume prompt",
            credential,
            new AgentResumeContext("refs/heads/codeybox/preempt/wi"));

        Assert.False(result.Success);
        Assert.Equal("failed to materialise codex auth: exit 7", result.Summary);
        Assert.DoesNotContain(sandbox.Execs, exec => exec.Argv.Count > 0 && exec.Argv[0] == "codex");
    }

    private sealed class RecordingSandbox : ISandbox
    {
        private readonly int _authWriteExitCode;

        public RecordingSandbox(int authWriteExitCode = 0)
        {
            _authWriteExitCode = authWriteExitCode;
        }

        public string Id => "recording";
        public List<SandboxExec> Execs { get; } = [];

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Execs.Add(exec);
            if (exec.Argv.Count > 0 && exec.Argv[0] == "bash")
                return Task.FromResult(new SandboxExecResult(_authWriteExitCode, "", "auth stderr"));

            return Task.FromResult(new SandboxExecResult(0, "ok", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
