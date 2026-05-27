using CodeyBox.Agents.Gemini;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="GeminiAgentRunner"/>. Uses a capturing fake
/// sandbox to inspect the argv and environment that RunAsync forwards to
/// the sandbox — the same pattern as the ClaudeQuotaProbe / router tests.
/// </summary>
public sealed class GeminiAgentRunnerTests
{
    // ── Kind ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Kind_IsGemini()
    {
        var runner = new GeminiAgentRunner();
        Assert.Equal(AgentKind.Gemini, runner.Kind);
    }

    [Fact]
    public void AgentKind_Gemini_RoundTrips()
    {
        var parsed = new AgentKind("gemini");
        Assert.Equal(AgentKind.Gemini, parsed);
    }

    // ── Argv construction ─────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_Argv_StartsWithBinary()
    {
        var sandbox = new CapturingSandbox();
        var runner = new GeminiAgentRunner();

        await runner.RunAsync(sandbox, "/work", "do the thing", credential: null);

        Assert.Equal("gemini", sandbox.CapturedExec!.Argv[0]);
    }

    [Fact]
    public async Task RunAsync_Argv_ContainsYoloFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = new GeminiAgentRunner();

        await runner.RunAsync(sandbox, "/work", "do the thing", credential: null);

        Assert.Contains("--yolo", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_Argv_ContainsSkipTrustFlag()
    {
        // Without --skip-trust the gemini CLI silently demotes --yolo to
        // "default" approval-mode for untrusted workspaces, which deadlocks
        // the non-interactive run because no operator can answer prompts.
        var sandbox = new CapturingSandbox();
        var runner = new GeminiAgentRunner();

        await runner.RunAsync(sandbox, "/work", "do the thing", credential: null);

        Assert.Contains("--skip-trust", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_PromptIsPassedViaStdin()
    {
        // Argv-passed prompts that exceed Linux's MAX_ARG_STRLEN (128 KiB per
        // single arg) surface as exit 126 from the sandbox wrapper's `exec "$@"`.
        // gemini-cli's -p flag docstring already documents "Appended to input on
        // stdin (if any)", so feeding the prompt as stdin works without -p.
        const string prompt = "write a fizzbuzz in Go";
        var sandbox = new CapturingSandbox();
        var runner = new GeminiAgentRunner();

        await runner.RunAsync(sandbox, "/work", prompt, credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.DoesNotContain(prompt, argv);
        Assert.DoesNotContain("-p", argv);
        Assert.Equal(prompt, sandbox.CapturedExec!.Stdin);
    }

    [Fact]
    public async Task RunAsync_WithModelId_InjectsModelFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = new GeminiAgentRunner();

        await runner.RunAsync(sandbox, "/work", "prompt", credential: null, modelId: "gemini-2.5-pro");

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("--model");
        Assert.True(modelIdx >= 0, "argv must contain --model flag");
        Assert.Equal("gemini-2.5-pro", argv[modelIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_WithoutModelId_NoModelFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = new GeminiAgentRunner();

        await runner.RunAsync(sandbox, "/work", "prompt", credential: null, modelId: null);

        Assert.DoesNotContain("--model", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_EmptyModelId_NoModelFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = new GeminiAgentRunner();

        await runner.RunAsync(sandbox, "/work", "prompt", credential: null, modelId: "");

        Assert.DoesNotContain("--model", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_WithReasoningModeHigh_DoesNotAddThinkingFlag()
    {
        // Gemini CLI 0.40+ has no --thinking/--reasoning flag. Reasoning level
        // is encoded in the model preset (gemini-3-* extends chat-base-3 which
        // sets thinkingLevel: HIGH). ReasoningMode is informational only on
        // this runner — passing "high" must not add a fictitious flag.
        var sandbox = new CapturingSandbox();
        var runner = new GeminiAgentRunner();

        await runner.RunAsync(sandbox, "/work", "prompt", credential: null, reasoningMode: "high");

        Assert.DoesNotContain("--thinking", sandbox.CapturedExec!.Argv);
        Assert.DoesNotContain("--reasoning", sandbox.CapturedExec!.Argv);
        Assert.DoesNotContain("--effort", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_WithoutReasoningMode_NoThinkingFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = new GeminiAgentRunner();

        await runner.RunAsync(sandbox, "/work", "prompt", credential: null, reasoningMode: null);

        Assert.DoesNotContain("--thinking", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_StructuredStreamCapture_UsesOutputFormatStreamJson()
    {
        // gemini-cli's structured stream flag is `--output-format stream-json`,
        // not `--json` (which doesn't exist).
        var sandbox = new CapturingSandbox { HelpOutput = "--output-format choices: text, json, stream-json" };
        var runner = new GeminiAgentRunner();

        await runner.RunAsync(sandbox, "/work", "prompt", credential: null, captureStructuredStream: true);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.DoesNotContain("--json", argv);
        var ofIdx = argv.IndexOf("--output-format");
        Assert.True(ofIdx >= 0, "argv must contain --output-format flag");
        Assert.Equal("stream-json", argv[ofIdx + 1]);
    }

    // ── Binary override ───────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_CustomBinary_UsesOverride()
    {
        var sandbox = new CapturingSandbox();
        var runner = new GeminiAgentRunner { Binary = "/opt/gemini/bin/gemini" };

        await runner.RunAsync(sandbox, "/work", "prompt", credential: null);

        Assert.Equal("/opt/gemini/bin/gemini", sandbox.CapturedExec!.Argv[0]);
    }

    // The prompt-via-stdin contract is covered by RunAsync_PromptIsPassedViaStdin
    // above; no separate "last argument" test is meaningful now that the prompt
    // is no longer on argv at all.

    // ── Success / failure propagation ─────────────────────────────────────────

    [Fact]
    public async Task RunAsync_SandboxExitZero_ReturnsSuccess()
    {
        var runner = new GeminiAgentRunner();
        var result = await runner.RunAsync(new CapturingSandbox(exitCode: 0), "/work", "p", null);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task RunAsync_SandboxExitNonZero_ReturnsFailure()
    {
        var runner = new GeminiAgentRunner();
        var result = await runner.RunAsync(new CapturingSandbox(exitCode: 1), "/work", "p", null);

        Assert.False(result.Success);
    }

    // ── Failure summary enrichment ────────────────────────────────────────────
    //
    // Without these, every Gemini exit-1 surfaces as "agent exited 1" in
    // TerminalQuotaError / WorkItem.LastError, so operators cannot tell quota
    // from auth from transport from a stale model id by inspecting the work
    // item alone. The full stderr is preserved on AgentResult.Stderr; only the
    // Summary is enriched (capped at FailureSummaryTailMaxChars).

    [Fact]
    public async Task RunAsync_Failure_AppendsStderrTailToSummary()
    {
        var sandbox = new CapturingSandbox(
            exitCode: 1,
            stderr: "RESOURCE_EXHAUSTED quota exceeded for gemini-3-flash-preview",
            stdout: "");
        var runner = new GeminiAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "p", null);

        Assert.False(result.Success);
        Assert.StartsWith("agent exited 1", result.Summary);
        Assert.Contains("RESOURCE_EXHAUSTED quota exceeded for gemini-3-flash-preview", result.Summary);
    }

    [Fact]
    public async Task RunAsync_Failure_StripsAnsiBeforeAppendingTail()
    {
        var sandbox = new CapturingSandbox(
            exitCode: 1,
            stderr: "\x1b[31merror: invalid_grant: refresh token expired\x1b[0m",
            stdout: "");
        var runner = new GeminiAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "p", null);

        Assert.Contains("invalid_grant: refresh token expired", result.Summary);
        Assert.False(result.Summary.Contains('\x1B'), $"summary contains ESC: {string.Join(",", result.Summary.Select(c => ((int)c).ToString("X2")))}");
    }

    [Fact]
    public async Task RunAsync_Failure_EmptyStderr_FallsBackToStdout()
    {
        // gemini-cli sometimes funnels structured errors only via stdout
        // (stream-json) — the summary should still carry context.
        var sandbox = new CapturingSandbox(
            exitCode: 1,
            stderr: "",
            stdout: """{"type":"result","status":"error","error":{"message":"API Error: 401 Unauthorized"}}""");
        var runner = new GeminiAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "p", null);

        Assert.Contains("API Error: 401", result.Summary);
    }

    [Fact]
    public async Task RunAsync_Failure_NoOutput_LeavesBaseSummary()
    {
        // Neither stream produced text — keep the unchanged "agent exited N"
        // so the orchestrator can still distinguish ok from failure without
        // crashing on a null tail.
        var sandbox = new CapturingSandbox(exitCode: 1, stderr: "", stdout: "");
        var runner = new GeminiAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "p", null);

        Assert.Equal("agent exited 1", result.Summary);
    }

    [Fact]
    public async Task RunAsync_Failure_CollapsesNewlinesInSummary()
    {
        // The summary is consumed by single-line audit log sinks and webhook
        // payloads; embedded newlines would break grep/CSV parsing.
        var sandbox = new CapturingSandbox(
            exitCode: 1,
            stderr: "line one\nline two\r\nline three",
            stdout: "");
        var runner = new GeminiAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "p", null);

        var summary = result.Summary;
        Assert.DoesNotContain('\n', summary);
        Assert.DoesNotContain('\r', summary);
        Assert.Contains("line one line two line three", summary);
    }

    [Fact]
    public async Task RunAsync_Failure_TailIsCapped()
    {
        var longStderr = "head " + new string('x', GeminiAgentRunner.FailureSummaryTailMaxChars * 2) + " tail-marker";
        var sandbox = new CapturingSandbox(exitCode: 1, stderr: longStderr, stdout: "");
        var runner = new GeminiAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "p", null);

        // The cap applies to the appended tail only; verify the tail-marker
        // (which sits at the end of stderr) survived while the leading "head"
        // was dropped.
        Assert.Contains("tail-marker", result.Summary);
        Assert.DoesNotContain("head ", result.Summary);
        Assert.StartsWith("agent exited 1: …", result.Summary);
    }

    [Fact]
    public async Task RunAsync_Success_LeavesSummaryAsOk()
    {
        var sandbox = new CapturingSandbox(exitCode: 0, stderr: "warning: deprecated flag", stdout: "result");
        var runner = new GeminiAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "p", null);

        Assert.True(result.Success);
        Assert.Equal("ok", result.Summary);
    }

    [Fact]
    public async Task RunAsync_Failure_FullStderrStillOnAgentResult()
    {
        // The Summary is capped, but the orchestrator's quota classifier and
        // detail builder still need the full stderr — assert it survives.
        var stderr = "RESOURCE_EXHAUSTED " + new string('y', GeminiAgentRunner.FailureSummaryTailMaxChars * 3);
        var sandbox = new CapturingSandbox(exitCode: 1, stderr: stderr, stdout: "");
        var runner = new GeminiAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "p", null);

        Assert.Equal(stderr.Length, result.Stderr!.Length);
        Assert.StartsWith("RESOURCE_EXHAUSTED", result.Stderr);
    }

    [Fact]
    public async Task RunResumedAsync_Failure_AppendsStderrTailToSummary()
    {
        // Differentiate bash setup calls (scratchpad restore, auth materialise)
        // from the agent invocation itself: the former must succeed (exit 0) or
        // base.RunResumedAsync throws before the agent runs; only the gemini
        // binary call should surface as exit 1.
        var sandbox = new ResumeFailingSandbox(
            agentStderr: "RESOURCE_EXHAUSTED gemini-3-pro-preview");
        var runner = new GeminiAgentRunner();

        var result = await runner.RunResumedAsync(
            sandbox,
            "/work",
            "p",
            null,
            new AgentResumeContext("refs/heads/codeybox/preempt/wi"));

        Assert.False(result.Success);
        Assert.Contains("RESOURCE_EXHAUSTED gemini-3-pro-preview", result.Summary);
    }

    // ── ANSI stripping ────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_StripsAnsiFromStdout()
    {
        var sandbox = new CapturingSandbox(stdout: "\x1b[32msome output\x1b[0m");
        var runner = new GeminiAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "p", null);

        Assert.Equal("some output", result.Stdout);
    }

    [Fact]
    public async Task RunAsync_StripsAnsiFromStderr()
    {
        var sandbox = new CapturingSandbox(stderr: "\x1b[1mProgress:\x1b[0m done");
        var runner = new GeminiAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "p", null);

        Assert.Equal("Progress: done", result.Stderr);
    }

    [Fact]
    public async Task RunAsync_PlainOutput_PassesThroughUnchanged()
    {
        var sandbox = new CapturingSandbox(stdout: "plain text");
        var runner = new GeminiAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "p", null);

        Assert.Equal("plain text", result.Stdout);
    }

    [Fact]
    public async Task RunResumedAsync_StripsAnsiFromStdoutAndStderr()
    {
        var sandbox = new CapturingSandbox(stdout: "\x1b[32mresumed\x1b[0m", stderr: "\x1b[1mProgress:\x1b[0m done");
        var runner = new GeminiAgentRunner();

        var result = await runner.RunResumedAsync(
            sandbox,
            "/work",
            "p",
            null,
            new AgentResumeContext("refs/heads/codeybox/preempt/wi"));

        Assert.Equal("resumed", result.Stdout);
        Assert.Equal("Progress: done", result.Stderr);
    }

    // ── GetTextOnlyUnavailabilityReason ───────────────────────────────────────
    //
    // The rebase-resolver router consults the probe before RunTextOnlyAsync,
    // so they must agree on what 'viable' means. A typo in the env-var name
    // (e.g. "GEMINI_KEY" instead of "GEMINI_API_KEY") would silently
    // misclassify every OAuth-only operator setup — which is exactly the
    // configuration the routing fix exists to handle.

    [Fact]
    public void GetTextOnlyUnavailabilityReason_NullCredential_ReturnsReason()
    {
        var runner = new GeminiAgentRunner();
        Assert.Equal("GEMINI_API_KEY is required",
            runner.GetTextOnlyUnavailabilityReason(credential: null));
    }

    [Fact]
    public void GetTextOnlyUnavailabilityReason_EmptyEnvironment_ReturnsReason()
    {
        var runner = new GeminiAgentRunner();
        var cred = new AgentCredential(AgentKind.Gemini,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

        Assert.Equal("GEMINI_API_KEY is required",
            runner.GetTextOnlyUnavailabilityReason(cred));
    }

    [Fact]
    public void GetTextOnlyUnavailabilityReason_OAuthOnlyCredentials_ReturnsReason()
    {
        // The bug shape this fix exists to fix: operator has OAuth-only
        // (CODEYBOX_GEMINI_OAUTH_FILE → mounted into sandbox) but no API key.
        // The probe must mark Gemini as unavailable so the router walks past
        // it to Claude/Codex.
        var runner = new GeminiAgentRunner();
        var cred = new AgentCredential(AgentKind.Gemini,
            new Dictionary<string, string>
            {
                ["GEMINI_OAUTH_TOKEN"] = "ya29.placeholder",
            },
            new Dictionary<string, string>());

        Assert.Equal("GEMINI_API_KEY is required",
            runner.GetTextOnlyUnavailabilityReason(cred));
    }

    [Fact]
    public void GetTextOnlyUnavailabilityReason_ApiKeyPresent_ReturnsNull()
    {
        var runner = new GeminiAgentRunner();
        var cred = new AgentCredential(AgentKind.Gemini,
            new Dictionary<string, string> { ["GEMINI_API_KEY"] = "AIzaSyTest" },
            new Dictionary<string, string>());

        Assert.Null(runner.GetTextOnlyUnavailabilityReason(cred));
    }

    [Fact]
    public void GetTextOnlyUnavailabilityReason_EmptyApiKey_ReturnsReason()
    {
        // Empty-string export (`GEMINI_API_KEY=`) must be treated as absent,
        // matching RunTextOnlyAsync's IsNullOrEmpty check.
        var runner = new GeminiAgentRunner();
        var cred = new AgentCredential(AgentKind.Gemini,
            new Dictionary<string, string> { ["GEMINI_API_KEY"] = "" },
            new Dictionary<string, string>());

        Assert.Equal("GEMINI_API_KEY is required",
            runner.GetTextOnlyUnavailabilityReason(cred));
    }

    [Fact]
    public async Task GetTextOnlyUnavailabilityReason_AgreesWithRunTextOnlyAsync_OnMissingCredentials()
    {
        var runner = new GeminiAgentRunner();
        var cred = new AgentCredential(AgentKind.Gemini,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

        Assert.NotNull(runner.GetTextOnlyUnavailabilityReason(cred));
        var result = await runner.RunTextOnlyAsync("hello", cred);
        Assert.False(result.Success);
        Assert.Contains("GEMINI_API_KEY", result.Error);
    }

    // ── Credential provider ───────────────────────────────────────────────────

    [Fact]
    public async Task EnvironmentCredentialProvider_Gemini_ReturnsCredentialWhenEnvVarSet()
    {
        Environment.SetEnvironmentVariable("CODEYBOX_GEMINI_API_KEY", "test-gemini-key");
        try
        {
            var provider = new EnvironmentCredentialProvider(new[]
            {
                new AgentCredentialMapping(AgentKind.Gemini, "CODEYBOX_GEMINI_API_KEY", "GEMINI_API_KEY"),
            });

            var cred = await provider.GetAsync(AgentKind.Gemini);

            Assert.NotNull(cred);
            Assert.Equal("test-gemini-key", cred!.EnvironmentVariables["GEMINI_API_KEY"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_GEMINI_API_KEY", null);
        }
    }

    [Fact]
    public async Task EnvironmentCredentialProvider_Gemini_ReturnsNullWhenEnvVarAbsent()
    {
        Environment.SetEnvironmentVariable("CODEYBOX_GEMINI_API_KEY", null);
        var provider = new EnvironmentCredentialProvider(new[]
        {
            new AgentCredentialMapping(AgentKind.Gemini, "CODEYBOX_GEMINI_API_KEY", "GEMINI_API_KEY"),
        });

        var cred = await provider.GetAsync(AgentKind.Gemini);

        Assert.Null(cred);
    }

    [Fact]
    public async Task EnvironmentCredentialProvider_Gemini_ReturnsNullForOtherAgents()
    {
        Environment.SetEnvironmentVariable("CODEYBOX_GEMINI_API_KEY", "test-gemini-key");
        try
        {
            var provider = new EnvironmentCredentialProvider(new[]
            {
                new AgentCredentialMapping(AgentKind.Gemini, "CODEYBOX_GEMINI_API_KEY", "GEMINI_API_KEY"),
            });

            Assert.Null(await provider.GetAsync(AgentKind.Claude));
            Assert.Null(await provider.GetAsync(AgentKind.Codex));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_GEMINI_API_KEY", null);
        }
    }
}

/// <summary>
/// Fake sandbox that records the most recent <see cref="SandboxExec"/> it
/// received and returns configurable exit code, stdout, and stderr.
/// </summary>
internal sealed class CapturingSandbox : ISandbox
{
    private readonly int _exitCode;
    private readonly string _stdout;
    private readonly string _stderr;
    private readonly string? _stdoutChunk;

    public CapturingSandbox(int exitCode = 0, string stdout = "stdout", string stderr = "stderr", string? stdoutChunk = null)
    {
        _exitCode = exitCode;
        _stdout = stdout;
        _stderr = stderr;
        _stdoutChunk = stdoutChunk;
    }

    public string Id => "fake";
    public SandboxExec? CapturedExec { get; private set; }

    /// <summary>
    /// Optional canned response for `--help` invocations (used by structured-
    /// stream detection). When null, falls back to the regular stdout/stderr.
    /// </summary>
    public string? HelpOutput { get; init; }

    public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        CapturedExec = exec;
        if (HelpOutput is not null && exec.Argv.Contains("--help"))
            return Task.FromResult(new SandboxExecResult(0, HelpOutput, string.Empty));
        if (_stdoutChunk is not null)
            exec.StdoutChunkCallback?.Invoke(_stdoutChunk);
        return Task.FromResult(new SandboxExecResult(_exitCode, _stdout, _stderr));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Sandbox that succeeds for the orchestrator-internal bash setup phases
/// (scratchpad restore, credential materialisation) but fails for the agent
/// binary invocation itself. Used to assert the runner's failure-summary
/// enrichment on the resume path without short-circuiting at scratchpad
/// restore.
/// </summary>
internal sealed class ResumeFailingSandbox : ISandbox
{
    private readonly string _agentStderr;
    public ResumeFailingSandbox(string agentStderr) { _agentStderr = agentStderr; }

    public string Id => "fake-resume";

    public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        // bash-driven prep (restore + auth materialisation) must succeed so
        // base.RunResumedAsync reaches the actual agent invocation.
        if (exec.Argv.Count > 0 && exec.Argv[0] == "bash")
            return Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));
        return Task.FromResult(new SandboxExecResult(1, string.Empty, _agentStderr));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
