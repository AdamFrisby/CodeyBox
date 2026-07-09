using CodeyBox.Agents.Cursor;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

public sealed class CursorAgentRunnerTests
{
    // ── BuildInvocation contract ──────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_InvokesAgentBinaryWithPrintAndDefaultModel()
    {
        var sandbox = new RecordingSandbox();
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["cursor"] = "composer-2.5",
            });
        var runner = new CursorAgentRunner(defaults);

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
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["cursor"] = "composer-2.5",
            });
        var runner = new CursorAgentRunner(defaults);

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

    // ── Structured-stream capture flag ────────────────────────────────────────

    [Fact]
    public async Task RunAsync_WhenCaptureStructuredStreamTrue_AppendsStreamJsonAndPartialOutput()
    {
        // Cursor CLI emits NDJSON in the same shape as Claude when
        // `--output-format stream-json --stream-partial-output` are set.
        // PipelineRunner only sets captureStructuredStream=true once
        // SupportsStructuredStreamAsync confirmed `agent --help` advertises
        // the flag. A regression dropping either flag silently downgrades
        // the capture file from real-time NDJSON to a single end-of-run
        // blob, defeating the live tail / structured-summary path — and
        // the parser tests (which hand-write NDJSON) would not catch it.
        var sandbox = new RecordingSandbox();
        var runner = new CursorAgentRunner();

        await runner.RunAsync(sandbox, "/work", "p", credential: null, captureStructuredStream: true);

        var agentExec = Assert.Single(sandbox.Execs, e => e.Argv.Count > 0 && e.Argv[0] == "agent");
        var argv = agentExec.Argv;
        var formatIdx = -1;
        for (var i = 0; i < argv.Count; i++)
            if (argv[i] == "--output-format") { formatIdx = i; break; }
        Assert.True(formatIdx >= 0, "expected --output-format when captureStructuredStream=true");
        Assert.Equal("stream-json", argv[formatIdx + 1]);
        Assert.Contains("--stream-partial-output", argv);
    }

    [Fact]
    public async Task RunAsync_WhenCaptureStructuredStreamFalse_OmitsStreamJsonAndPartialOutput()
    {
        // Plaintext-capture path: the help-text probe rejected --output-format
        // (older Cursor build). The runner must NOT pass it; an unknown-option
        // exit 1 here would mask itself as a stream-classified failure.
        var sandbox = new RecordingSandbox();
        var runner = new CursorAgentRunner();

        await runner.RunAsync(sandbox, "/work", "p", credential: null, captureStructuredStream: false);

        var agentExec = Assert.Single(sandbox.Execs, e => e.Argv.Count > 0 && e.Argv[0] == "agent");
        Assert.DoesNotContain("--output-format", agentExec.Argv);
        Assert.DoesNotContain("stream-json", agentExec.Argv);
        Assert.DoesNotContain("--stream-partial-output", agentExec.Argv);
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
            && e.Argv[2].Contains(".config/cursor/auth.json", StringComparison.Ordinal));
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
    public async Task RunAsync_WithCredentialMaterialisesAuthFromCredentialStdin()
    {
        const string authJson = """{"token":"cursor-fallback-token"}""";
        var sandbox = new RecordingSandbox();
        var runner = new CursorAgentRunner();
        var credential = new AgentCredential(
            AgentKind.Cursor,
            new Dictionary<string, string> { ["CODEYBOX_CURSOR_AUTH_JSON"] = authJson },
            new Dictionary<string, string>());

        var result = await runner.RunAsync(sandbox, "/work", "p", credential);

        Assert.True(result.Success);
        var authExec = Assert.Single(sandbox.Execs, e =>
            e.Argv.Count >= 5
            && e.Argv[0] == "bash"
            && e.Argv[4] == ".config/cursor/auth.json");
        Assert.Equal(authJson, authExec.Stdin);
        Assert.Null(authExec.ExtraEnvironment);
        Assert.DoesNotContain(authJson, authExec.Argv[2]);
        Assert.DoesNotContain(authJson, authExec.Argv);
        Assert.DoesNotContain("CODEYBOX_CURSOR_AUTH_JSON", authExec.Argv[2]);
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

        Assert.Contains(".config/cursor/auth.json", script, StringComparison.Ordinal);
        Assert.Contains("CODEYBOX_CURSOR_AUTH_JSON", script, StringComparison.Ordinal);
        Assert.Contains("credential destination file is a symlink", script, StringComparison.Ordinal);
        Assert.Contains("if [ -f \"$dest\" ] && [ -s \"$dest\" ]; then return 0; fi", script, StringComparison.Ordinal);
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
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["cursor"] = "composer-2.5",
            });
        var runner = new CursorAgentRunner(defaults);
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
    public void GetTextOnlyUnavailabilityReason_MissingAuth_ReturnsNull()
    {
        var runner = new CursorAgentRunner();
        Assert.Null(runner.GetTextOnlyUnavailabilityReason(credential: null));
    }

    [Fact]
    public void GetTextOnlyUnavailabilityReason_EmptyCredentialBundle_ReturnsReason()
    {
        var runner = new CursorAgentRunner();
        var cred = new AgentCredential(
            AgentKind.Cursor,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());
        Assert.Equal(
            "CODEYBOX_CURSOR_AUTH_JSON is required when a credential bundle is supplied",
            runner.GetTextOnlyUnavailabilityReason(cred));
    }

    [Fact]
    public void GetTextOnlyUnavailabilityReason_WithAuthJson_ReturnsNull()
    {
        var runner = new CursorAgentRunner();
        var cred = new AgentCredential(
            AgentKind.Cursor,
            new Dictionary<string, string> { ["CODEYBOX_CURSOR_AUTH_JSON"] = """{"token":"x"}""" },
            new Dictionary<string, string>());
        Assert.Null(runner.GetTextOnlyUnavailabilityReason(cred));
    }

    [Fact]
    public void TextOnlyRequiresSandbox_IsTrue()
    {
        ITextOnlyAgentRunner runner = new CursorAgentRunner();

        Assert.True(runner.TextOnlyRequiresSandbox);
    }

    [Fact]
    public async Task RunTextOnlyInSandboxAsync_InvokesAgentPrintWithModelAndStdin()
    {
        const string prompt = "resolve this conflict";
        var sandbox = new RecordingSandbox(agentStdout: "assistant text");
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["cursor"] = "composer-2.5",
            });
        var runner = new CursorAgentRunner(defaults);
        var cred = new AgentCredential(
            AgentKind.Cursor,
            new Dictionary<string, string> { ["CODEYBOX_CURSOR_AUTH_JSON"] = """{"token":"x"}""" },
            new Dictionary<string, string>());

        var result = await runner.RunTextOnlyAsync(prompt, cred, sandbox: sandbox, workingDirectory: "/work");

        Assert.True(result.Success, $"{result.Summary} | {result.Error}");
        Assert.Equal("assistant text", result.Output);
        var agentExec = sandbox.Execs.Last();
        Assert.Equal("agent", agentExec.Argv[0]);
        Assert.Contains("--print", agentExec.Argv);
        Assert.DoesNotContain("--trust", agentExec.Argv);
        Assert.DoesNotContain("--force", agentExec.Argv);
        Assert.Contains("--model", agentExec.Argv);
        Assert.Contains("composer-2.5", agentExec.Argv);
        Assert.Equal(prompt, agentExec.Stdin);
        Assert.Equal(SandboxAgentOutputTransportPreference.ExecPipe, agentExec.AgentOutputTransport);
        Assert.Null(agentExec.ExtraEnvironment);
    }

    [Fact]
    public async Task RunTextOnlyInSandboxAsync_HttpIngestSandboxPrefersDetachedBatchLaunch()
    {
        var sandbox = new RecordingSandbox(
            agentStdout: "assistant text",
            transportKind: SandboxAgentOutputTransportKind.HttpIngest);
        var runner = new CursorAgentRunner();
        var cred = new AgentCredential(
            AgentKind.Cursor,
            new Dictionary<string, string> { ["CODEYBOX_CURSOR_AUTH_JSON"] = """{"token":"x"}""" },
            new Dictionary<string, string>());

        var result = await runner.RunTextOnlyAsync("prompt", cred, sandbox: sandbox, workingDirectory: "/work");

        Assert.True(result.Success, $"{result.Summary} | {result.Error}");
        var agentExec = sandbox.Execs.Last(e => e.Argv.Count > 0 && e.Argv[0] == "agent");
        Assert.Equal(SandboxAgentOutputTransportPreference.PreferHttpIngest, agentExec.AgentOutputTransport);
        Assert.Equal(SandboxExecLaunchMode.DetachedBatch, agentExec.LaunchMode);
    }

    [Fact]
    public async Task RunTextOnlyAsync_RequiresSandbox()
    {
        var runner = new CursorAgentRunner();
        var cred = new AgentCredential(
            AgentKind.Cursor,
            new Dictionary<string, string> { ["CODEYBOX_CURSOR_AUTH_JSON"] = """{"token":"x"}""" },
            new Dictionary<string, string>());

        var result = await runner.RunTextOnlyAsync("prompt", cred);

        Assert.False(result.Success);
        Assert.Contains("sandbox", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunTextOnlyInSandboxAsync_NonZeroExit_UsesStderrDetail()
    {
        var sandbox = new RecordingSandbox(agentExitCode: 2, agentStdout: "", agentStderr: "model unavailable");
        var runner = new CursorAgentRunner();
        var cred = new AgentCredential(
            AgentKind.Cursor,
            new Dictionary<string, string> { ["CODEYBOX_CURSOR_AUTH_JSON"] = """{"token":"x"}""" },
            new Dictionary<string, string>());

        var result = await runner.RunTextOnlyAsync("prompt", cred, sandbox: sandbox, workingDirectory: "/work");

        Assert.False(result.Success);
        Assert.Equal("model unavailable", result.Error);
    }

    [Fact]
    public async Task RunTextOnlyInSandboxAsync_EmptyStdout_FallsBackToStderr()
    {
        var sandbox = new RecordingSandbox(agentStdout: "", agentStderr: "stderr-only");
        var runner = new CursorAgentRunner();
        var cred = new AgentCredential(
            AgentKind.Cursor,
            new Dictionary<string, string> { ["CODEYBOX_CURSOR_AUTH_JSON"] = """{"token":"x"}""" },
            new Dictionary<string, string>());

        var result = await runner.RunTextOnlyAsync("prompt", cred, sandbox: sandbox, workingDirectory: "/work");

        Assert.True(result.Success);
        Assert.Equal("stderr-only", result.Output);
    }

    private sealed class RecordingSandbox : ISandbox
    {
        private readonly int _authWriteExitCode;
        private readonly int _agentExitCode;
        private readonly string _agentStdout;
        private readonly string _agentStderr;

        public RecordingSandbox(
            int authWriteExitCode = 0,
            int agentExitCode = 0,
            string agentStdout = "ok",
            string agentStderr = "",
            SandboxAgentOutputTransportKind transportKind = SandboxAgentOutputTransportKind.ExecPipe)
        {
            _authWriteExitCode = authWriteExitCode;
            _agentExitCode = agentExitCode;
            _agentStdout = agentStdout;
            _agentStderr = agentStderr;
            AgentOutputTransportKind = transportKind;
        }

        public string Id => "recording-cursor";
        public SandboxAgentOutputTransportKind AgentOutputTransportKind { get; }
        public SandboxBatchLaunchMode BatchLaunchMode => AgentOutputTransportKind == SandboxAgentOutputTransportKind.HttpIngest
            ? SandboxBatchLaunchMode.Detached
            : SandboxBatchLaunchMode.Attached;
        public List<SandboxExec> Execs { get; } = [];

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Execs.Add(exec);
            if (exec.Argv.Count >= 3
                && exec.Argv[0] == "bash"
                && (exec.Argv[2].Contains(".config/cursor/auth.json", StringComparison.Ordinal)
                    || exec.Argv.Contains(".config/cursor/auth.json", StringComparer.Ordinal))
                && (exec.Argv[2].Contains("CODEYBOX_CURSOR_AUTH_JSON", StringComparison.Ordinal)
                    || exec.Stdin is not null))
            {
                return Task.FromResult(new SandboxExecResult(_authWriteExitCode, "", "auth stderr"));
            }

            if (exec.Argv.Count > 0 && exec.Argv[0] == "agent")
                return Task.FromResult(new SandboxExecResult(_agentExitCode, _agentStdout, _agentStderr));

            return Task.FromResult(new SandboxExecResult(0, "ok", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
