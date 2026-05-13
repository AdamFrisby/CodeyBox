using CodeyBox.Agents.Codex;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class CodexAgentRunnerTests
{
    [Fact]
    public async Task RunResumedAsync_MaterialisesAuthBeforeInvokingCodex()
    {
        var sandbox = new RecordingSandbox();
        var runner = new CodexAgentRunner();

        var result = await runner.RunResumedAsync(
            sandbox,
            "/work",
            "resume prompt",
            credential: null,
            new AgentResumeContext("refs/heads/codeybox/preempt/wi"));

        Assert.True(result.Success);
        // The codex CLI invocation must be preceded by the in-sandbox auth
        // materialisation bash command that references CODEX_AUTH_JSON.
        var authIdx = sandbox.Execs.FindIndex(e =>
            e.Argv.Count >= 3
            && e.Argv[0] == "bash"
            && e.Argv[2].Contains("CODEX_AUTH_JSON", StringComparison.Ordinal)
            && e.Argv[2].Contains("$HOME/.codex/auth.json", StringComparison.Ordinal));
        var codexIdx = sandbox.Execs.FindIndex(e => e.Argv.Count > 0 && e.Argv[0] == "codex");
        Assert.True(authIdx >= 0, "auth materialisation bash command was not invoked");
        Assert.True(codexIdx >= 0, "codex CLI was not invoked");
        Assert.True(authIdx < codexIdx, "auth materialisation must run before codex CLI");
        Assert.Equal("exec", sandbox.Execs[codexIdx].Argv[1]);
    }

    [Fact]
    public async Task RunResumedAsync_WhenAuthMaterialisationFails_DoesNotInvokeCodex()
    {
        var sandbox = new RecordingSandbox(authWriteExitCode: 7);
        var runner = new CodexAgentRunner();

        var result = await runner.RunResumedAsync(
            sandbox,
            "/work",
            "resume prompt",
            credential: null,
            new AgentResumeContext("refs/heads/codeybox/preempt/wi"));

        Assert.False(result.Success);
        Assert.Equal("failed to materialise codex auth: exit 7", result.Summary);
        Assert.DoesNotContain(sandbox.Execs, exec => exec.Argv.Count > 0 && exec.Argv[0] == "codex");
    }

    [Fact]
    public async Task PrepareSandboxScript_GuardsAgainstClobberingMountedAuthJson()
    {
        // The materialisation script must short-circuit when auth.json is
        // already non-empty inside the sandbox (i.e. provided by a bind-mount
        // from the host). Writing the env-var snapshot on top of a mounted
        // host file would clobber any refresh-token rotation the host has
        // performed since the credential was read, re-introducing the
        // refresh-token-reuse cascade the mount is supposed to prevent.
        var sandbox = new RecordingSandbox();
        var runner = new CodexAgentRunner();

        // RunResumedAsync drives PrepareSandboxAsync as a side effect.
        _ = await runner.RunResumedAsync(
            sandbox,
            "/work",
            "p",
            credential: null,
            new AgentResumeContext("refs/heads/codeybox/preempt/wi"));

        var prepExec = sandbox.Execs.FirstOrDefault(e =>
            e.Argv.Count >= 3
            && e.Argv[0] == "bash"
            && e.Argv[2].Contains("CODEX_AUTH_JSON", StringComparison.Ordinal));
        Assert.NotNull(prepExec);
        var script = prepExec!.Argv[2];

        // Guard must check existence (or non-empty) of auth.json BEFORE the
        // env-var write block, and exit early when present.
        var stillExistsIdx = script.IndexOf("$HOME/.codex/auth.json", StringComparison.Ordinal);
        var earlyExitIdx = script.IndexOf("exit 0", StringComparison.Ordinal);
        var writeIdx = script.IndexOf("printf", StringComparison.Ordinal);
        Assert.True(stillExistsIdx >= 0, "script must reference $HOME/.codex/auth.json");
        Assert.True(earlyExitIdx >= 0, "script must short-circuit when file is present (exit 0)");
        Assert.True(writeIdx >= 0, "script must still have a printf-from-env fallback");
        Assert.True(earlyExitIdx < writeIdx,
            "early-exit guard must come before the env-var write so a mounted auth.json is preserved");
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
            // The auth materialisation script is a single-arg bash -c invocation
            // referencing CODEX_AUTH_JSON; surface the configured exit code so we
            // can simulate a failed write.
            if (exec.Argv.Count >= 3
                && exec.Argv[0] == "bash"
                && exec.Argv[2].Contains("CODEX_AUTH_JSON", StringComparison.Ordinal)
                && exec.Argv[2].Contains(".codex/auth.json", StringComparison.Ordinal))
            {
                return Task.FromResult(new SandboxExecResult(_authWriteExitCode, "", "auth stderr"));
            }

            return Task.FromResult(new SandboxExecResult(0, "ok", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
