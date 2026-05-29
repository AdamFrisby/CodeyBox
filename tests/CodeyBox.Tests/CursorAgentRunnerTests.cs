using CodeyBox.Agents.Cursor;
using CodeyBox.Core;
using CodeyBox.HostProcess;

namespace CodeyBox.Tests;

public sealed class CursorAgentRunnerTests
{
    // ── BuildInvocation contract ──────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_InvokesAgentBinaryWithPrintAndDefaultModel()
    {
        var sandbox = new RecordingSandbox();
        var runner = new CursorAgentRunner();

        var result = await runner.RunAsync(
            sandbox,
            "/work",
            "do the thing",
            credential: null);

        Assert.True(result.Success);
        var agentExec = Assert.Single(sandbox.Execs, e => e.Argv.Count > 0 && e.Argv[0] == "agent");
        Assert.Contains("--print", agentExec.Argv);
        Assert.Contains("--model", agentExec.Argv);
        Assert.Contains("composer-2.5", agentExec.Argv);
        Assert.Equal("do the thing", agentExec.Stdin);
    }

    [Fact]
    public async Task RunAsync_OverrideModelId_FlowsToArgv()
    {
        var sandbox = new RecordingSandbox();
        var runner = new CursorAgentRunner();

        await runner.RunAsync(sandbox, "/work", "p", credential: null, modelId: "composer-3-preview");

        var agentExec = Assert.Single(sandbox.Execs, e => e.Argv.Count > 0 && e.Argv[0] == "agent");
        Assert.Contains("composer-3-preview", agentExec.Argv);
        Assert.DoesNotContain("composer-2.5", agentExec.Argv);
    }

    [Fact]
    public async Task RunAsync_PromptDeliveredViaStdin_NotPositionalArgv()
    {
        // 200 KiB prompt exceeds Linux's 128 KiB MAX_ARG_STRLEN; argv delivery
        // would surface as exit 126. Lock in stdin delivery.
        var bigPrompt = new string('x', 200_000);
        var sandbox = new RecordingSandbox();
        var runner = new CursorAgentRunner();

        await runner.RunAsync(sandbox, "/work", bigPrompt, credential: null);

        var agentExec = Assert.Single(sandbox.Execs, e => e.Argv.Count > 0 && e.Argv[0] == "agent");
        Assert.Equal(bigPrompt, agentExec.Stdin);
        Assert.DoesNotContain(bigPrompt, agentExec.Argv);
    }

    [Fact]
    public async Task RunAsync_ReasoningModeIgnored_NoSurfaceInArgv()
    {
        // Cursor CLI has no reasoning-effort flag; the value is informational.
        var sandbox = new RecordingSandbox();
        var runner = new CursorAgentRunner();

        await runner.RunAsync(sandbox, "/work", "p", credential: null, reasoningMode: "high");

        var agentExec = Assert.Single(sandbox.Execs, e => e.Argv.Count > 0 && e.Argv[0] == "agent");
        Assert.DoesNotContain("--effort", agentExec.Argv);
        Assert.DoesNotContain("--reasoning", agentExec.Argv);
        Assert.DoesNotContain("--thinking", agentExec.Argv);
        Assert.DoesNotContain("high", agentExec.Argv);
    }

    // ── Auth materialisation ──────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_RunsAuthMaterialisationBeforeAgentCli()
    {
        var sandbox = new RecordingSandbox();
        var runner = new CursorAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "p", credential: null);

        Assert.True(result.Success);
        var authIdx = sandbox.Execs.FindIndex(e =>
            e.Argv.Count >= 3
            && e.Argv[0] == "bash"
            && e.Argv[2].Contains("CODEYBOX_CURSOR_AUTH_JSON", StringComparison.Ordinal)
            && e.Argv[2].Contains("$HOME/.config/cursor/auth.json", StringComparison.Ordinal));
        var agentIdx = sandbox.Execs.FindIndex(e => e.Argv.Count > 0 && e.Argv[0] == "agent");
        Assert.True(authIdx >= 0, "auth materialisation bash command was not invoked");
        Assert.True(agentIdx >= 0, "agent CLI was not invoked");
        Assert.True(authIdx < agentIdx, "auth materialisation must run before agent CLI");
    }

    [Fact]
    public async Task RunAsync_WhenAuthMaterialisationFails_DoesNotInvokeAgent()
    {
        var sandbox = new RecordingSandbox(authWriteExitCode: 7);
        var runner = new CursorAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "p", credential: null);

        Assert.False(result.Success);
        Assert.Equal("failed to materialise cursor auth: exit 7", result.Summary);
        Assert.DoesNotContain(sandbox.Execs, exec => exec.Argv.Count > 0 && exec.Argv[0] == "agent");
    }

    [Fact]
    public async Task PrepareSandboxScript_PreservesExistingSandboxCredentialsJson()
    {
        // The materialisation script must short-circuit when credentials.json
        // is already non-empty inside the sandbox (restored from a checkpoint
        // scratchpad). Mirrors the Codex pattern.
        var sandbox = new RecordingSandbox();
        var runner = new CursorAgentRunner();

        _ = await runner.RunAsync(sandbox, "/work", "p", credential: null);

        var prepExec = sandbox.Execs.FirstOrDefault(e =>
            e.Argv.Count >= 3
            && e.Argv[0] == "bash"
            && e.Argv[2].Contains("CODEYBOX_CURSOR_AUTH_JSON", StringComparison.Ordinal));
        Assert.NotNull(prepExec);
        var script = prepExec!.Argv[2];

        var existenceCheckIdx = script.IndexOf("$HOME/.config/cursor/auth.json", StringComparison.Ordinal);
        var earlyExitIdx = script.IndexOf("exit 0", StringComparison.Ordinal);
        var writeIdx = script.IndexOf("printf", StringComparison.Ordinal);
        Assert.True(existenceCheckIdx >= 0, "script must reference $HOME/.config/cursor/auth.json");
        Assert.True(earlyExitIdx >= 0, "script must short-circuit when file is present (exit 0)");
        Assert.True(writeIdx >= 0, "script must still have a printf-from-env fallback");
        Assert.True(earlyExitIdx < writeIdx,
            "early-exit guard must come before the env-var write so an existing credentials.json is preserved");
    }

    // ── Resume path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task RunResumedAsync_InvokesAgentBinary()
    {
        var sandbox = new RecordingSandbox();
        var runner = new CursorAgentRunner();

        var result = await runner.RunResumedAsync(
            sandbox,
            "/work",
            "resume prompt",
            credential: null,
            new AgentResumeContext("refs/heads/codeybox/preempt/wi"));

        Assert.True(result.Success);
        Assert.Contains(sandbox.Execs, e => e.Argv.Count > 0 && e.Argv[0] == "agent");
    }

    // ── Default model surface ─────────────────────────────────────────────────

    [Fact]
    public void DefaultModelId_IsComposer25()
    {
        var runner = new CursorAgentRunner();
        Assert.Equal("composer-2.5", runner.DefaultModelId);
    }

    [Fact]
    public void Kind_IsCursor()
    {
        var runner = new CursorAgentRunner();
        Assert.Equal(AgentKind.Cursor, runner.Kind);
    }

    [Fact]
    public void Binary_IsAgentNotCursorAgent()
    {
        // The Cursor CLI installs as `agent`, NOT `cursor-agent`. Pinning the
        // binary name avoids a subtle install-path regression.
        var runner = new CursorAgentRunner();
        Assert.Equal("agent", runner.Binary);
    }

    [Fact]
    public void GetTextOnlyUnavailabilityReason_MissingAuth_ReturnsReason()
    {
        var runner = new CursorAgentRunner();
        Assert.Equal(
            "CODEYBOX_CURSOR_AUTH_JSON is required",
            runner.GetTextOnlyUnavailabilityReason(credential: null));
    }

    [Fact]
    public async Task RunTextOnlyAsync_InvokesAgentPrintWithModelAndStdin()
    {
        const string prompt = "resolve this conflict";
        var process = new RecordingProcessRunner();
        var runner = new CursorAgentRunner(process);
        var cred = new AgentCredential(
            AgentKind.Cursor,
            new Dictionary<string, string> { ["CODEYBOX_CURSOR_AUTH_JSON"] = """{"token":"x"}""" },
            new Dictionary<string, string>());

        var result = await runner.RunTextOnlyAsync(prompt, cred);

        Assert.True(result.Success, $"{result.Summary} | {result.Error}");
        Assert.Equal("assistant text", result.Output);
        var call = Assert.Single(process.Calls);
        Assert.Equal("agent", call[0]);
        Assert.Contains("--print", call);
        Assert.Contains("--model", call);
        Assert.Contains("composer-2.5", call);
        Assert.Equal(prompt, process.Stdins[0]);
        Assert.False(string.IsNullOrWhiteSpace(process.Environments[0]?["HOME"]));
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public List<string[]> Calls { get; } = [];
        public List<string?> Stdins { get; } = [];
        public List<IReadOnlyDictionary<string, string>?> Environments { get; } = [];

        public Task<ProcessRunResult> RunAsync(
            IReadOnlyList<string> argv,
            string? stdin,
            CancellationToken ct,
            Action<string>? stdoutChunkCallback = null,
            Action<string>? stderrChunkCallback = null,
            int? maxStdoutBytes = null,
            int? maxStderrBytes = null,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            Calls.Add(argv.ToArray());
            Stdins.Add(stdin);
            Environments.Add(environment);
            return Task.FromResult(new ProcessRunResult(0, "assistant text", ""));
        }
    }

    private sealed class RecordingSandbox : ISandbox
    {
        private readonly int _authWriteExitCode;

        public RecordingSandbox(int authWriteExitCode = 0)
        {
            _authWriteExitCode = authWriteExitCode;
        }

        public string Id => "recording-cursor";
        public List<SandboxExec> Execs { get; } = [];

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Execs.Add(exec);
            if (exec.Argv.Count >= 3
                && exec.Argv[0] == "bash"
                && exec.Argv[2].Contains("CODEYBOX_CURSOR_AUTH_JSON", StringComparison.Ordinal)
                && exec.Argv[2].Contains(".config/cursor/auth.json", StringComparison.Ordinal))
            {
                return Task.FromResult(new SandboxExecResult(_authWriteExitCode, "", "auth stderr"));
            }
            return Task.FromResult(new SandboxExecResult(0, "ok", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
