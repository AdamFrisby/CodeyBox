using CodeyBox.Agents.Cmd;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="CmdAgentRunner"/>. Uses the shared
/// <c>CapturingSandbox</c> to inspect the argv, stdin, and environment the
/// runner forwards. Argv pins here encode the transport decision verified
/// against command-code 1.54.2: <c>--local-only -p --output-format json
/// --no-session --skip-onboarding --yolo</c> (<c>-p</c> is the documented
/// headless contract with no positional query — the prompt travels on
/// stdin, dodging the 128 KiB MAX_ARG_STRLEN ceiling and the
/// must-quote-the-query argv hazard), <c>--model</c> from the member or the
/// config-sourced default, guest <c>~/.commandcode/</c> seeding before
/// dispatch, and never <c>--effort</c> (the shipped free-tier model rejects
/// it). Failure-path tests pin the two deliverables that keep this agent
/// honest: a missing CLI surfaces as infrastructure, and a permission-gated
/// (no-yolo-shaped) success lifts a diagnostic instead of reading as a
/// successful empty result.
/// </summary>
public sealed class CmdAgentRunnerTests
{
    private static CmdAgentRunner Runner(AgentDefaultsSnapshot? defaults = null) =>
        new(defaults: defaults);

    private static AgentDefaultsSnapshot DefaultsWith(string model) =>
        new(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["cmd"] = model });

    private static AgentCredential Cred(string key = "test-key") =>
        new(AgentKind.Cmd,
            new Dictionary<string, string> { ["OPENROUTER_API_KEY"] = key },
            new Dictionary<string, string>());

    [Fact]
    public void Kind_IsCmd()
    {
        Assert.Equal(AgentKind.Cmd, new CmdAgentRunner().Kind);
    }

    [Fact]
    public void AgentKind_Cmd_RoundTrips()
    {
        Assert.Equal(AgentKind.Cmd, new AgentKind("cmd"));
    }

    [Fact]
    public async Task RunAsync_Argv_UsesLocalOnlyPrintJsonTransport()
    {
        var sandbox = new CapturingSandbox();
        var runner = Runner();

        await runner.RunAsync(sandbox, "/work", "do the thing", Cred());

        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.Equal("cmd", argv[0]);
        Assert.Contains("--local-only", argv);
        Assert.Contains("-p", argv);
        var formatIdx = argv.IndexOf("--output-format");
        Assert.True(formatIdx >= 0, "expected --output-format flag");
        Assert.Equal("json", argv[formatIdx + 1]);
        Assert.Contains("--no-session", argv);
        Assert.Contains("--skip-onboarding", argv);
        Assert.Contains("--yolo", argv);
    }

    [Fact]
    public async Task RunAsync_Prompt_TravelsViaStdin_NotArgv()
    {
        // Linux MAX_ARG_STRLEN is 128 KiB per argv element; rework prompts can
        // exceed it. Verified: `-p` with no query argument reads the
        // piped-stdin prompt, so stdin is a complete prompt channel.
        var sandbox = new CapturingSandbox();
        var runner = Runner();
        const string prompt = "implement the widget with extra care";

        await runner.RunAsync(sandbox, "/work", prompt, Cred());

        Assert.Equal(prompt, sandbox.CapturedExec!.Stdin);
        Assert.DoesNotContain(sandbox.CapturedExec.Argv, a => a.Contains("widget", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_Argv_NeverMapsReasoningModeToEffort()
    {
        // --effort exists but the shipped free-tier model rejects it ("has
        // no adjustable reasoning effort", exit 1 — verified live), so the
        // runner ignores reasoningMode rather than passing it through.
        var sandbox = new CapturingSandbox();
        var runner = Runner();

        await runner.RunAsync(sandbox, "/work", "x", Cred(), reasoningMode: "high");

        Assert.DoesNotContain("--effort", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_NoModelAnywhere_OmitsModelFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = Runner();

        await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.DoesNotContain("-m", sandbox.CapturedExec!.Argv);
        Assert.DoesNotContain("--model", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_ExplicitModelId_PassedVerbatim()
    {
        var sandbox = new CapturingSandbox();
        var runner = Runner();
        const string model = "openrouter/nvidia/nemotron-3.5-lightning:free";

        await runner.RunAsync(sandbox, "/work", "x", Cred(), modelId: model);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("--model");
        Assert.True(modelIdx >= 0, "expected --model flag");
        Assert.Equal(model, argv[modelIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_DefaultModelId_UsedWhenNoExplicitModel()
    {
        var sandbox = new CapturingSandbox();
        var runner = Runner(DefaultsWith("openrouter/nvidia/nemotron-3.5-lightning:free"));

        await runner.RunAsync(sandbox, "/work", "x", Cred());

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("--model");
        Assert.True(modelIdx >= 0, "expected --model flag");
        Assert.Equal("openrouter/nvidia/nemotron-3.5-lightning:free", argv[modelIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_SeedsGuestCommandcodeFiles_BeforeDispatch()
    {
        // The interactive /connect flow cannot run headless, so the runner
        // pre-seeds ~/.commandcode/providers.json (openrouter entry with the
        // $OPENROUTER_API_KEY reference — never the raw key) and
        // ~/.commandcode/auth.json (non-credential presence placeholder)
        // before the agent invocation.
        var sandbox = new CapturingSandbox();
        var runner = Runner(DefaultsWith("openrouter/nvidia/nemotron-3.5-lightning:free"));

        await runner.RunAsync(sandbox, "/work", "x", Cred("real-secret"));

        var seeding = sandbox.Execs
            .Where(e => e.Argv.Count > 0 && e.Argv[0] == "bash")
            .Select(e => e.Stdin ?? string.Empty)
            .ToList();
        var providers = Assert.Single(seeding, s => s.Contains("\"openrouter\"", StringComparison.Ordinal));
        Assert.Contains("$OPENROUTER_API_KEY", providers, StringComparison.Ordinal);
        Assert.Contains("nvidia/nemotron-3.5-lightning:free", providers, StringComparison.Ordinal);
        Assert.DoesNotContain("real-secret", providers, StringComparison.Ordinal);
        var auth = Assert.Single(
            seeding,
            s => s.Contains("\"apiKey\"", StringComparison.Ordinal)
                && s.Contains(CmdAgentRunner.LocalOnlyAuthPlaceholder, StringComparison.Ordinal));
        Assert.DoesNotContain("real-secret", auth, StringComparison.Ordinal);
        // The agent invocation is the last exec — seeding never displaces it.
        Assert.Equal("cmd", sandbox.CapturedExec!.Argv[0]);
    }

    [Fact]
    public async Task RunAsync_ExitFourSpendRefusal_LiftsTerminalDiagnostic()
    {
        // Recorded real shape (command-code 1.54.2, paid model on a
        // $0-spend-limit OpenRouter key): exit 4 with the provider refusal
        // verbatim in the terminal result line's error field.
        const string stdout =
            "{\"type\":\"event\",\"event\":{\"type\":\"run_start\",\"sessionId\":\"9c8c9bd8-da4a-4211-b1b1-2a615a5c7ce2\"}}\n" +
            "{\"type\":\"event\",\"event\":{\"type\":\"run_error\",\"error\":{\"name\":\"Error\",\"message\":\"Error: 403\"}}}\n" +
            "{\"type\":\"result\",\"subtype\":\"error\",\"isError\":true,\"usage\":{\"inputTokens\":0,\"outputTokens\":0,\"cacheReadTokens\":0,\"cacheWriteTokens\":0}," +
            "\"durationMs\":2202,\"finalText\":\"\",\"error\":\"Error: 403 Key limit exceeded (total limit).\"}";
        var sandbox = new CapturingSandbox(exitCode: 4, stdout: stdout, stderr: string.Empty);
        var runner = Runner();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("Key limit exceeded", result.TerminalDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_PermissionGatedSuccess_LiftsGateDiagnostic_NeverEmptySuccess()
    {
        // The blank-pass trap: headless mode without the permission flag
        // blocks writes/edits/shell but still exits 0 with subtype success.
        // Recorded real no-yolo shape (command-code 1.54.2): the run must
        // NOT read as a successful empty result — the blocked-write count
        // is lifted into TerminalDiagnostic so the pipeline can tell it
        // apart from the model declining to act.
        const string stdout =
            "{\"type\":\"event\",\"event\":{\"type\":\"run_start\",\"sessionId\":\"s\"}}\n" +
            "{\"type\":\"event\",\"event\":{\"type\":\"tool_queued\",\"toolCallId\":\"call-1\",\"toolName\":\"write_file\",\"input\":{}}}\n" +
            "{\"type\":\"event\",\"event\":{\"type\":\"tool_hook_blocked\",\"toolCallId\":\"call-1\",\"toolName\":\"write_file\"," +
            "\"hookOutput\":\"Tool \\\"write_file\\\" requires permissions. Use --yolo to allow all tools in trusted environments.\"}}\n" +
            "{\"type\":\"event\",\"event\":{\"type\":\"tool_queued\",\"toolCallId\":\"call-2\",\"toolName\":\"shell\",\"input\":{}}}\n" +
            "{\"type\":\"event\",\"event\":{\"type\":\"tool_hook_blocked\",\"toolCallId\":\"call-2\",\"toolName\":\"shell\"," +
            "\"hookOutput\":\"Tool \\\"shell\\\" requires permissions. Use --yolo to allow all tools in trusted environments.\"}}\n" +
            "{\"type\":\"result\",\"subtype\":\"success\",\"isError\":false,\"usage\":{\"inputTokens\":100,\"outputTokens\":50,\"cacheReadTokens\":0,\"cacheWriteTokens\":0}," +
            "\"durationMs\":1000,\"finalText\":\"I reviewed the code.\"}";
        var sandbox = new CapturingSandbox(exitCode: 0, stdout: stdout, stderr: string.Empty);
        var runner = Runner();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.True(result.Success);
        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("permission gate", result.TerminalDiagnostic, StringComparison.Ordinal);
        Assert.Contains("--yolo", result.TerminalDiagnostic, StringComparison.Ordinal);
        Assert.Contains("2", result.TerminalDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_HealthyRun_LeavesTerminalDiagnosticNull()
    {
        // Recorded real success frames (command-code 1.54.2): run_start
        // plus a subtype-success result line with non-empty finalText and
        // real usage — no error, no permission gate.
        const string stdout =
            "{\"type\":\"event\",\"event\":{\"type\":\"run_start\",\"sessionId\":\"s\"}}\n" +
            "{\"type\":\"event\",\"event\":{\"type\":\"turn_end\",\"turnNumber\":1,\"hadToolCalls\":false," +
            "\"usage\":{\"inputTokens\":101,\"outputTokens\":10,\"cacheReadTokens\":0,\"cacheWriteTokens\":0}}}\n" +
            "{\"type\":\"result\",\"subtype\":\"success\",\"isError\":false,\"usage\":{\"inputTokens\":101,\"outputTokens\":10,\"cacheReadTokens\":0,\"cacheWriteTokens\":0}," +
            "\"durationMs\":1000,\"finalText\":\"CMD_SMOKE_OK\"}";
        var sandbox = new CapturingSandbox(exitCode: 0, stdout: stdout, stderr: string.Empty);
        var runner = Runner();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.Null(result.TerminalDiagnostic);
    }

    [Fact]
    public async Task RunAsync_MissingCliInGuest_SurfacesAsInfrastructureFailureNamingCmd()
    {
        // A guest without the CLI must surface as an infrastructure failure
        // naming the cause — never as "no changes". The shell reports the
        // missing binary as exit 127 + command-not-found.
        var sandbox = new CapturingSandbox(
            exitCode: 127,
            stdout: string.Empty,
            stderr: "bash: line 1: cmd: command not found");
        var runner = Runner();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.False(result.Success);
        var classification = ((IAgentRunner)runner).ClassifyFailure(result);
        Assert.Equal(AgentFailureKind.Infrastructure, classification.Kind);
        Assert.Contains("cmd", result.Stderr ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
