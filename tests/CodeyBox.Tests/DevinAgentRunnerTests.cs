using CodeyBox.Agents.Devin;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="DevinAgentRunner"/>. Dispatch argv pins encode the
/// transport decision verified against devin 3000.11.1: <c>devin acp</c>
/// driven by the embedded Python shim (<c>initialize</c> →
/// <c>session/new</c> → <c>session/set_mode bypass</c> →
/// <c>session/prompt</c>) so <c>session/update</c> notifications keep the
/// agent stream advancing during long turns. The shim + prompt travel on
/// stdin as a framed payload (base64 shim, end marker, verbatim prompt) —
/// never in argv or the environment (<c>MAX_ARG_STRLEN</c> is 128 KiB per
/// element). <c>--model</c> only when a model is configured, and the
/// credentials.toml contents materialised in-guest from
/// <c>CODEYBOX_DEVIN_AUTH_TOML</c>.
/// </summary>
public sealed class DevinAgentRunnerTests
{
    private const string ConfiguredModel = "claude-sonnet-4";

    private static DevinAgentRunner RunnerWithDefault(string model = ConfiguredModel) =>
        new(new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["devin"] = model }));

    private static AgentCredential Cred(string toml = "api_key = \"sk-test\"") =>
        new(AgentKind.Devin,
            new Dictionary<string, string> { [DevinAgentRunner.AuthTomlEnvironmentVariable] = toml },
            new Dictionary<string, string>());

    private const string TurnCompleteStdout =
        "{\"type\":\"devin.acp\",\"event\":\"session_started\",\"sessionId\":\"s-1\"}\n"
        + "{\"type\":\"devin.acp\",\"event\":\"turn_complete\",\"stopReason\":\"end_turn\",\"usage\":{\"inputTokens\":11,\"outputTokens\":7},\"finalText\":\"done\"}\n";

    /// <summary>
    /// The devin dispatch exec. The CLI is wrapped in a bash script that
    /// collects the framed-stdin shim into a variable and execs it via
    /// <c>python3 -I -c</c> (see
    /// <c>DevinAgentRunner.BuildAcpDispatchScript</c>), so the exec to find
    /// is the one whose script declares that collector — not the sibling
    /// bash exec that materialises credentials.
    /// </summary>
    private static SandboxExec DevinExec(RecordingSandbox sandbox) =>
        Assert.Single(sandbox.Execs, e => e.Argv.Count == 3 && e.Argv[0] == "bash"
            && e.Argv[2].Contains("cb_shim_b64", StringComparison.Ordinal));

    /// <summary>The shim command line the wrapper script ends with.</summary>
    private static string DevinCommandLine(RecordingSandbox sandbox) =>
        DevinExec(sandbox).Argv[2].Split('\n')[^1];

    [Fact]
    public void Kind_IsDevin()
    {
        Assert.Equal(AgentKind.Devin, new DevinAgentRunner().Kind);
    }

    [Fact]
    public void AgentKind_Devin_RoundTrips()
    {
        Assert.Equal(AgentKind.Devin, new AgentKind("devin"));
    }

    [Fact]
    public async Task RunAsync_DispatchesAcpShim_WithBypassModeAndModel()
    {
        var sandbox = new RecordingSandbox();
        var runner = RunnerWithDefault();

        await runner.RunAsync(sandbox, "/work", "do the thing", Cred());

        Assert.Equal(
            $"python3 -I -c \"$cb_shim\" --prompt-file - '--binary' 'devin' "
            + $"'--model' '{ConfiguredModel}' '--mode' 'bypass'",
            DevinCommandLine(sandbox));
    }

    [Fact]
    public async Task RunAsync_Prompt_TravelsViaFramedStdin_NotArgv()
    {
        // Linux MAX_ARG_STRLEN is 128 KiB per element and applies to argv AND
        // the environment, so neither can carry a rework prompt. The prompt
        // is piped verbatim after the base64 shim + end-marker frame and the
        // shim reads it from the still-open descriptor 0 (--prompt-file -).
        var sandbox = new RecordingSandbox();
        var runner = RunnerWithDefault();
        const string prompt = "implement the widget with extra care";

        await runner.RunAsync(sandbox, "/work", prompt, Cred());

        var devin = DevinExec(sandbox);
        Assert.NotNull(devin.Stdin);
        Assert.EndsWith(prompt, devin.Stdin);
        Assert.Contains(DevinAcpShim.StdinEndMarker, devin.Stdin, StringComparison.Ordinal);
        Assert.DoesNotContain(devin.Argv, a => a.Contains("widget", StringComparison.Ordinal));
        Assert.All(devin.ExtraEnvironment ?? new Dictionary<string, string>(),
            kv => Assert.DoesNotContain("widget", kv.Value, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_StagesNoReOpenableArtifacts()
    {
        // The shim and the prompt must never sit at a path the dispatch
        // re-opens: a same-uid watcher in the sandbox could swap a staged
        // file between the write and the exec (and an earlier design that
        // re-opened /dev/stdin died EACCES — the exec wrapper's stdin pipe
        // is created before the sandbox-user drop). The shim rides argv via
        // `python3 -I -c`, the prompt stays on the inherited fd 0.
        var sandbox = new RecordingSandbox();
        var runner = RunnerWithDefault();

        await runner.RunAsync(sandbox, "/work", "do the thing", Cred());

        var script = DevinExec(sandbox).Argv[2];
        Assert.DoesNotContain("mktemp", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/dev/stdin", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/dev/fd/", script, StringComparison.Ordinal);
        Assert.DoesNotContain("cb_prompt", script, StringComparison.Ordinal);
        Assert.Contains("python3 -I -c \"$cb_shim\" --prompt-file -", script, StringComparison.Ordinal);
        // The framed-stdin block still feeds the shim through a variable
        // collected line-by-line — never a staged file.
        Assert.Contains("cb_shim_b64=\"${cb_shim_b64}$line\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ExplicitModelId_WinsOverDefault()
    {
        var sandbox = new RecordingSandbox();
        var runner = RunnerWithDefault();

        await runner.RunAsync(sandbox, "/work", "x", Cred(), modelId: "claude-opus-4.6");

        var command = DevinCommandLine(sandbox);
        Assert.Contains("'claude-opus-4.6'", command, StringComparison.Ordinal);
        Assert.DoesNotContain(ConfiguredModel, command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NoModelAnywhere_OmitsModelFlag()
    {
        // Unlike crush, devin has an account-side server default, so an
        // unset model is a legal dispatch — the flag is simply omitted.
        var sandbox = new RecordingSandbox();
        var runner = new DevinAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.DoesNotContain("--model", DevinCommandLine(sandbox), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_MaterialisesCredentialsBeforeDispatch()
    {
        var sandbox = new RecordingSandbox();
        var runner = RunnerWithDefault();
        const string toml = "api_key = \"sk-x\"\napi_server_url = \"https://u.example\"";

        await runner.RunAsync(sandbox, "/work", "x", Cred(toml));

        var materialise = Assert.Single(sandbox.Execs, e =>
            CredentialMaterialisationTestHelper.IsStdinMaterialisation(
                e, ".local/share/devin/credentials.toml"));
        Assert.True(sandbox.Execs.IndexOf(materialise) < sandbox.Execs.IndexOf(DevinExec(sandbox)));
    }

    [Fact]
    public async Task RunAsync_MissingCredential_DispatchesWithImageProvisionedAuth()
    {
        // MaterialiseFromSandboxEnvironmentWhenCredentialMissing: an absent
        // CODEYBOX_DEVIN_AUTH_TOML is NOT a failure — the sandbox image may
        // provision its own credentials.toml. The runner skips the
        // materialisation script and dispatches anyway.
        var sandbox = new RecordingSandbox();
        var runner = RunnerWithDefault();
        var empty = new AgentCredential(AgentKind.Devin,
            new Dictionary<string, string>(), new Dictionary<string, string>());

        var result = await runner.RunAsync(sandbox, "/work", "x", empty);

        Assert.True(result.Success);
        Assert.DoesNotContain(sandbox.Execs, e =>
            CredentialMaterialisationTestHelper.IsStdinMaterialisation(
                e, ".local/share/devin/credentials.toml"));
        DevinExec(sandbox);
    }

    [Fact]
    public async Task RunAsync_MaterialisationFailure_FailsClosedWithoutDispatch()
    {
        var sandbox = new RecordingSandbox { MaterialiseExitCode = 1 };
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.False(result.Success);
        Assert.DoesNotContain(sandbox.Execs, e => e.Argv.Count > 0 && e.Argv[0] == "devin");
    }

    [Fact]
    public async Task RunAsync_ErrorLine_LiftsTerminalDiagnostic()
    {
        // Recorded real shape (devin 3000.11.1, logged-out credentials): exit
        // nonzero with "Error: Not logged in" on stderr.
        var sandbox = new RecordingSandbox
        {
            DevinExitCode = 1,
            DevinStderr = "Error: Not logged in. Run `devin auth login` to authenticate.",
            DevinStdout = string.Empty,
        };
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("Not logged in", result.TerminalDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ErrorOnStdout_LiftsTerminalDiagnostic()
    {
        // Some CLI paths print the error to stdout; diagnoser scans both.
        var sandbox = new RecordingSandbox
        {
            DevinExitCode = 1,
            DevinStderr = string.Empty,
            DevinStdout = "Error: Quota exhausted. Wait for your quota to reset.",
        };
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("Quota exhausted", result.TerminalDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_AcpTurnErrorEnvelope_LiftsTypedTerminalDiagnostic()
    {
        // A session/prompt JSON-RPC error is the shim's typed failure shape;
        // it must surface verbatim in TerminalDiagnostic rather than as a
        // bare "agent exited 2".
        var sandbox = new RecordingSandbox
        {
            DevinExitCode = 2,
            DevinStderr = string.Empty,
            DevinStdout = "{\"type\":\"devin.acp\",\"event\":\"session_started\",\"sessionId\":\"s-1\"}\n"
                + "{\"type\":\"devin.acp\",\"event\":\"turn_error\",\"code\":-32001,\"message\":\"usage limit reached\"}\n",
        };
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.False(result.Success);
        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("turn error", result.TerminalDiagnostic, StringComparison.Ordinal);
        Assert.Contains("usage limit reached", result.TerminalDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_AcpFatalEnvelope_LiftsTypedTerminalDiagnostic()
    {
        var sandbox = new RecordingSandbox
        {
            DevinExitCode = 2,
            DevinStderr = string.Empty,
            DevinStdout = "{\"type\":\"devin.acp\",\"event\":\"fatal\",\"stage\":\"session/new\",\"message\":\"session refused\"}\n",
        };
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.False(result.Success);
        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("session/new", result.TerminalDiagnostic, StringComparison.Ordinal);
        Assert.Contains("session refused", result.TerminalDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ExitZeroWithoutTurnComplete_FailsHonestly()
    {
        // A zero exit with no terminal envelope means the run was cut short
        // before the turn outcome was written — never report silent success.
        var sandbox = new RecordingSandbox
        {
            DevinExitCode = 0,
            DevinStderr = string.Empty,
            DevinStdout = "{\"type\":\"devin.acp\",\"event\":\"session_started\",\"sessionId\":\"s-1\"}\n",
        };
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.False(result.Success);
        Assert.Contains("turn outcome", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_HealthyRun_LeavesTerminalDiagnosticNull()
    {
        var sandbox = new RecordingSandbox { DevinStdout = TurnCompleteStdout, DevinStderr = string.Empty };
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.True(result.Success);
        Assert.Null(result.TerminalDiagnostic);
    }

    [Fact]
    public async Task RunAsync_MissingCliInGuest_SurfacesAsInfrastructureFailure()
    {
        var sandbox = new RecordingSandbox
        {
            DevinExitCode = 127,
            DevinStderr = "bash: line 1: devin: command not found",
            DevinStdout = string.Empty,
        };
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.False(result.Success);
        Assert.Contains("command not found", result.Stderr, StringComparison.OrdinalIgnoreCase);
        var classification = ((IAgentRunner)runner).ClassifyFailure(result);
        Assert.Equal(AgentFailureKind.Infrastructure, classification.Kind);
    }

    [Fact]
    public void DefaultModelId_ReadsAgentDefaultsSnapshot()
    {
        var runner = RunnerWithDefault("claude-opus-4.6");

        Assert.Equal("claude-opus-4.6", runner.DefaultModelId);
    }

    [Fact]
    public async Task SupportsStructuredStream_ProbesAcpSubcommand()
    {
        var sandbox = new RecordingSandbox();
        var runner = new DevinAgentRunner();

        Assert.True(await runner.SupportsStructuredStreamAsync(sandbox, CancellationToken.None));
        Assert.Contains(sandbox.Execs, e =>
            e.Argv.Count == 3 && e.Argv[0] == "devin" && e.Argv[1] == "acp" && e.Argv[2] == "--help");
    }

    [Fact]
    public async Task SupportsStructuredStream_ReturnsFalse_WhenAcpUnavailable()
    {
        var sandbox = new RecordingSandbox { AcpHelpExitCode = 1 };
        var runner = new DevinAgentRunner();

        Assert.False(await runner.SupportsStructuredStreamAsync(sandbox, CancellationToken.None));
    }

    [Fact]
    public async Task RunTextOnlyAsync_DispatchesInSandbox_WithoutDangerousMode()
    {
        // Text-only stays on print mode and omits --permission-mode (the CLI
        // default `auto` auto-approves read-only tools only) — the
        // conservative shape for answering questions on untrusted resolver
        // input.
        var sandbox = new RecordingSandbox();
        var runner = RunnerWithDefault();

        var result = await runner.RunTextOnlyAsync("summarise", Cred(), sandbox: sandbox, workingDirectory: "/work");

        Assert.True(result.Success);
        var textOnly = Assert.Single(sandbox.Execs, e => e.Argv.Count == 3 && e.Argv[0] == "bash"
            && e.Argv[2].Contains("cb_prompt=", StringComparison.Ordinal)
            && !e.Argv[2].Contains("cb_shim_b64", StringComparison.Ordinal));
        var command = textOnly.Argv[2].Split('\n')[^1];
        Assert.Equal(
            $"'devin' '-p' '--respect-workspace-trust' 'false' "
            + $"'--model' '{ConfiguredModel}' --prompt-file \"$cb_prompt\"",
            command);
        Assert.DoesNotContain("--permission-mode", command, StringComparison.Ordinal);
        Assert.DoesNotContain("dangerous", command, StringComparison.Ordinal);
        Assert.DoesNotContain("bypass", command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTextOnlyAsync_WithoutSandbox_FailsNamingSandboxRequirement()
    {
        var runner = RunnerWithDefault();

        var result = await runner.RunTextOnlyAsync("summarise", Cred());

        Assert.False(result.Success);
        Assert.Contains("sandbox", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunTextOnlyAsync_MissingCredential_ReportsUnavailable()
    {
        var runner = RunnerWithDefault();
        var empty = new AgentCredential(AgentKind.Devin,
            new Dictionary<string, string>(), new Dictionary<string, string>());

        var reason = ((ITextOnlyAgentRunner)runner).GetTextOnlyUnavailabilityReason(empty);

        Assert.NotNull(reason);
        Assert.Contains(DevinAgentRunner.AuthTomlEnvironmentVariable, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractAgentVisibleText_JoinsMessageChunks_AndYieldsVerdictSentinels()
    {
        // Check-and-act feeds the aggregated stdout blob to the verdict
        // parser — for devin that blob is envelope NDJSON where the verdict
        // (including its <<<CODEYBOX_VERDICT>>> sentinels) arrives
        // JSON-escaped inside agent_message_chunk / finalText. The extractor
        // must project the stream back to the plain answer text the parser
        // was written against.
        var verdictJson = "{\"answer\": true, \"evidence\": \"src/Foo.cs L42\", \"confidence\": \"high\"}";
        var agentText =
            "analysis\n" + CheckAndActPipeline.StartSentinel + "\n" + verdictJson + "\n"
            + CheckAndActPipeline.EndSentinel + "\n";
        var stdout = string.Concat(
            "{\"type\":\"devin.acp\",\"event\":\"session_started\",\"sessionId\":\"s-1\"}\n",
            System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "devin.acp",
                @event = "session_update",
                sessionId = "s-1",
                update = new
                {
                    sessionUpdate = "agent_message_chunk",
                    content = new { type = "text", text = agentText },
                },
            }) + "\n",
            System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "devin.acp",
                @event = "turn_complete",
                stopReason = "end_turn",
                usage = new { inputTokens = 11, outputTokens = 7 },
                finalText = agentText,
            }) + "\n");

        var extracted = new DevinAgentRunner().ExtractAgentVisibleText(stdout);
        Assert.Equal(agentText, extracted);

        var ok = CheckAndActPipeline.TryParseVerdict(extracted, out var verdict, out var error);
        Assert.True(ok, error);
        Assert.NotNull(verdict);
        Assert.True(verdict!.Answer);
        Assert.Equal("src/Foo.cs L42", verdict.Evidence);
        Assert.Equal("high", verdict.Confidence);
    }

    [Fact]
    public void ExtractAgentVisibleText_NoChunks_FallsBackToTerminalFinalText()
    {
        // A truncated capture that dropped the session_update envelopes but
        // kept the terminal line still yields the joined text the shim
        // stamped at turn end.
        var stdout =
            "{\"type\":\"devin.acp\",\"event\":\"turn_complete\",\"stopReason\":\"end_turn\",\"finalText\":\"the final answer\"}\n";

        Assert.Equal("the final answer", new DevinAgentRunner().ExtractAgentVisibleText(stdout));
    }

    [Fact]
    public void ExtractAgentVisibleText_NoEnvelopes_ReturnsNull()
    {
        // Non-devin output (or an exec that never ran the shim) must fall
        // back to the caller's raw stdout unchanged.
        Assert.Null(new DevinAgentRunner().ExtractAgentVisibleText("plain text stdout\n"));
    }

    [Fact]
    public void DirectCredentialEnvironmentVariables_IsEmpty()
    {
        // The auth TOML is consumed by the materialisation script, not read
        // by the devin binary directly — nothing is exposed as a direct
        // credential env var (the CLI has no DEVIN_API_KEY equivalent).
        IAgentCredentialEnvironmentPolicy policy = new DevinAgentRunner();

        Assert.Empty(policy.DirectCredentialEnvironmentVariables);
    }

    /// <summary>
    /// Scripted <see cref="ISandbox"/> for the Devin runner tests: the
    /// credential-materialisation bash script and the <c>devin</c> dispatch
    /// get independently canned results so tests can fail either stage.
    /// </summary>
    private sealed class RecordingSandbox : ISandbox
    {
        public int MaterialiseExitCode { get; set; }
        public int DevinExitCode { get; set; }
        public string DevinStdout { get; set; } = TurnCompleteStdout;
        public string DevinStderr { get; set; } = "stderr";
        public int AcpHelpExitCode { get; set; }

        public string Id => "fake-devin";
        public List<SandboxExec> Execs { get; } = [];

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Execs.Add(exec);
            if (CredentialMaterialisationTestHelper.IsStdinMaterialisation(
                    exec, ".local/share/devin/credentials.toml"))
            {
                return Task.FromResult(new SandboxExecResult(MaterialiseExitCode, "", "auth stderr"));
            }
            if (exec.Argv.Count == 3 && exec.Argv[0] == "devin" && exec.Argv[1] == "acp")
            {
                return Task.FromResult(new SandboxExecResult(AcpHelpExitCode, "acp help", ""));
            }
            // The dispatch exec is the framed-stdin wrapper (bash -c <script>),
            // not a bare `devin` argv — the script collects the shim into
            // $cb_shim_b64.
            if (exec.Argv.Count == 3 && exec.Argv[0] == "bash"
                && exec.Argv[2].Contains("cb_shim_b64", StringComparison.Ordinal))
            {
                return Task.FromResult(new SandboxExecResult(DevinExitCode, DevinStdout, DevinStderr));
            }
            return Task.FromResult(new SandboxExecResult(0, "ok", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
