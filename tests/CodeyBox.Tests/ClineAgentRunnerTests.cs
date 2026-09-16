using CodeyBox.Agents.Cline;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="ClineAgentRunner"/>. Uses the shared
/// <c>CapturingSandbox</c> to inspect the argv and environment the runner
/// forwards. Argv pins here encode the transport decision verified against
/// cline 3.0.62: a positional prompt (single turn, then exit — the only
/// prompt transport this version accepts; piped stdin was probed and
/// rejected), <c>--json</c> (the runner's only transport — NDJSON
/// hook_event / agent_event / run_result frames), <c>-P</c> always emitted
/// from <c>CodeyBox:Cline</c> (the CLI default provider is the cline vendor
/// account, so omitting it ignores the provider key), <c>-m</c> from the
/// member or the config-sourced default, and <c>--auto-approve true</c> (no
/// human in the sandbox). The key travels via the environment only — the
/// <c>-k</c> override is never emitted so the secret stays out of argv.
/// </summary>
public sealed class ClineAgentRunnerTests
{
    private static ClineAgentRunner Runner(ClineOptions? options = null) =>
        new(defaults: null, options: options is null ? null : (() => options));

    private static AgentDefaultsSnapshot DefaultsWith(string model) =>
        new(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["cline"] = model });

    private static AgentCredential Cred(string key = "test-key") =>
        new(AgentKind.Cline,
            new Dictionary<string, string> { ["OPENROUTER_API_KEY"] = key },
            new Dictionary<string, string>());

    [Fact]
    public void Kind_IsCline()
    {
        Assert.Equal(AgentKind.Cline, new ClineAgentRunner().Kind);
    }

    [Fact]
    public void AgentKind_Cline_RoundTrips()
    {
        Assert.Equal(AgentKind.Cline, new AgentKind("cline"));
    }

    [Fact]
    public async Task RunAsync_Argv_UsesOneShotHeadlessTransport()
    {
        var sandbox = new CapturingSandbox();
        var runner = Runner();

        await runner.RunAsync(sandbox, "/work", "do the thing", Cred());

        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.Equal("cline", argv[0]);
        // The runner's only transport is the NDJSON event stream.
        Assert.Contains("--json", argv);
        // Provider flag is always emitted: without it the CLI defaults to
        // the cline vendor account and ignores OPENROUTER_API_KEY.
        var providerIdx = argv.IndexOf("-P");
        Assert.True(providerIdx >= 0, "expected -P flag");
        Assert.Equal("openrouter", argv[providerIdx + 1]);
        // No human in the sandbox: tool auto-approval pinned on.
        var approveIdx = argv.IndexOf("--auto-approve");
        Assert.True(approveIdx >= 0, "expected --auto-approve flag");
        Assert.Equal("true", argv[approveIdx + 1]);
        // The prompt is positional and required; never the read-only
        // planning mode.
        Assert.Equal("do the thing", argv[^1]);
        Assert.DoesNotContain("--plan", argv);
        // The -k key override is never emitted: the secret travels via the
        // environment only.
        Assert.DoesNotContain("-k", argv);
        Assert.DoesNotContain("--key", argv);
    }

    [Fact]
    public async Task RunAsync_PromptFollowsOptionTerminator()
    {
        // A prompt beginning with `-` must travel as data after `--`, never
        // as CLI flags (verified against cline 3.0.62: `--` ends option
        // parsing; without it `--help` would print help instead of running).
        var sandbox = new CapturingSandbox();
        var runner = Runner();

        await runner.RunAsync(sandbox, "/work", "--help", Cred());

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var sepIdx = argv.IndexOf("--");
        Assert.True(sepIdx >= 0, "expected -- option terminator");
        Assert.Equal("--help", argv[sepIdx + 1]);
        Assert.Equal("--help", argv[^1]);
    }

    [Fact]
    public async Task RunAsync_ConfiguredProvider_PassedVerbatim()
    {
        var sandbox = new CapturingSandbox();
        var runner = Runner(new ClineOptions { Provider = "  openai-compatible  " });

        await runner.RunAsync(sandbox, "/work", "x", Cred());

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var providerIdx = argv.IndexOf("-P");
        Assert.True(providerIdx >= 0, "expected -P flag");
        Assert.Equal("openai-compatible", argv[providerIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_ExplicitModelId_PassedVerbatim()
    {
        var sandbox = new CapturingSandbox();
        var runner = new ClineAgentRunner(DefaultsWith("anthropic/claude-sonnet-4"));

        await runner.RunAsync(sandbox, "/work", "x", Cred(),
            modelId: "nvidia/nemotron-3.5-lightning:free");

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("-m");
        Assert.True(modelIdx >= 0, "expected -m flag");
        Assert.Equal("nvidia/nemotron-3.5-lightning:free", argv[modelIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_DefaultModelId_UsedWhenNoExplicitModel()
    {
        var sandbox = new CapturingSandbox();
        var runner = new ClineAgentRunner(DefaultsWith("nvidia/nemotron-3.5-lightning:free"));

        await runner.RunAsync(sandbox, "/work", "x", Cred());

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("-m");
        Assert.True(modelIdx >= 0, "expected -m flag");
        Assert.Equal("nvidia/nemotron-3.5-lightning:free", argv[modelIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_NoModelAnywhere_OmitsModelFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = new ClineAgentRunner(new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)));

        await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.DoesNotContain("-m", sandbox.CapturedExec!.Argv);
        Assert.DoesNotContain("--model", sandbox.CapturedExec.Argv);
    }

    [Fact]
    public async Task RunAsync_ApiKey_NeverAppearsInArgv()
    {
        // The -k override would place the secret in argv (visible via ps);
        // the environment is the only key channel.
        var sandbox = new CapturingSandbox();
        var runner = Runner();

        await runner.RunAsync(sandbox, "/work", "x", Cred("test-cline-key-material"));

        Assert.DoesNotContain(
            sandbox.CapturedExec!.Argv,
            a => a.Contains("test-cline-key-material", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_ExitOneErrorFrame_LiftsTerminalDiagnostic()
    {
        // Recorded real shape (cline 3.0.62, bad OpenRouter key in clean
        // state): exit 1 with the cause in an agent_event error frame, in
        // the error run_result text, and on stderr.
        const string stdout =
            "{\"ts\":\"2026-09-16T18:01:41.598Z\",\"type\":\"hook_event\",\"hookEventName\":\"agent_error\",\"agentId\":\"agent_x\",\"taskId\":\"conv_x\",\"parentAgentId\":null}\n" +
            "{\"ts\":\"2026-09-16T18:01:41.599Z\",\"type\":\"agent_event\",\"event\":{\"type\":\"error\",\"error\":{\"name\":\"Error\",\"message\":\"User not found.\",\"stack\":\"Error: User not found.\"},\"errorClass\":\"auth\",\"recoverable\":false,\"iteration\":1}}\n" +
            "{\"ts\":\"2026-09-16T18:01:41.673Z\",\"type\":\"run_result\",\"finishReason\":\"error\",\"iterations\":1,\"usage\":{\"inputTokens\":0,\"outputTokens\":0,\"cacheReadTokens\":0,\"cacheWriteTokens\":0,\"totalCost\":0},\"aggregateUsage\":{\"inputTokens\":0,\"outputTokens\":0,\"cacheReadTokens\":0,\"cacheWriteTokens\":0,\"totalCost\":0},\"durationMs\":407,\"text\":\"User not found.\",\"model\":{\"id\":\"nvidia/nemotron-3.5-lightning:free\",\"provider\":\"openrouter\"}}";
        const string stderr =
            "{\"ts\":\"2026-09-16T18:01:41.673Z\",\"type\":\"error\",\"message\":\"User not found.\"}";
        var sandbox = new CapturingSandbox(exitCode: 1, stdout: stdout, stderr: stderr);
        var runner = Runner();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.False(result.Success);
        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("User not found", result.TerminalDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_HealthyRun_LeavesTerminalDiagnosticNull()
    {
        // Recorded real frames (cline 3.0.62, --json run that created a file
        // via the editor tool): tool call/result plus a completed terminal
        // frame with final text.
        const string stdout =
            "{\"ts\":\"2026-09-16T18:02:02.535Z\",\"type\":\"agent_event\",\"event\":{\"type\":\"content_start\",\"contentType\":\"tool\",\"toolName\":\"editor\",\"toolCallId\":\"call-1\",\"input\":{\"new_text\":\"HELLO-FROM-CLINE\",\"path\":\"/tmp/cline-tools/hello.txt\"}}}\n" +
            "{\"ts\":\"2026-09-16T18:02:02.541Z\",\"type\":\"agent_event\",\"event\":{\"type\":\"content_end\",\"contentType\":\"tool\",\"toolName\":\"editor\",\"toolCallId\":\"call-1\",\"output\":{\"query\":\"edit:/tmp/cline-tools/hello.txt\",\"result\":\"File created successfully at: /tmp/cline-tools/hello.txt\",\"success\":true},\"durationMs\":6}}\n" +
            "{\"ts\":\"2026-09-16T18:02:09.691Z\",\"type\":\"run_result\",\"finishReason\":\"completed\",\"iterations\":2,\"usage\":{\"inputTokens\":19209,\"outputTokens\":294,\"cacheReadTokens\":8704,\"cacheWriteTokens\":0,\"totalCost\":0},\"aggregateUsage\":{\"inputTokens\":19209,\"outputTokens\":294,\"cacheReadTokens\":8704,\"cacheWriteTokens\":0,\"totalCost\":0},\"durationMs\":10075,\"text\":\"The task is complete.\",\"model\":{\"id\":\"nvidia/nemotron-3.5-lightning:free\",\"provider\":\"openrouter\"}}";
        var sandbox = new CapturingSandbox(exitCode: 0, stdout: stdout, stderr: string.Empty);
        var runner = Runner();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.True(result.Success);
        Assert.Null(result.TerminalDiagnostic);
    }

    [Fact]
    public async Task RunAsync_MissingCliInGuest_SurfacesAsInfrastructureFailureNamingCline()
    {
        // A guest without the CLI must surface as an infrastructure failure
        // naming the cause — never as "no changes". The shell reports the
        // missing binary as exit 127 + command-not-found.
        var sandbox = new CapturingSandbox(
            exitCode: 127,
            stdout: string.Empty,
            stderr: "bash: line 1: cline: command not found");
        var runner = Runner();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.False(result.Success);
        Assert.NotNull(result.Stderr);
        Assert.Contains("cline", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("command not found", result.Stderr, StringComparison.OrdinalIgnoreCase);
        var classification = ((IAgentRunner)runner).ClassifyFailure(result);
        Assert.Equal(AgentFailureKind.Infrastructure, classification.Kind);
    }

    [Fact]
    public async Task SupportsStructuredStreamAsync_ReturnsTrueWhenHelpAdvertisesJson()
    {
        var sandbox = new CapturingSandbox { HelpOutput = "Options:\n  --json  Output messages as JSON instead of styled text" };
        var runner = Runner();

        Assert.True(await runner.SupportsStructuredStreamAsync(sandbox));

        var helpExec = Assert.Single(
            sandbox.Execs, e => e.Argv.Contains("--help"));
        Assert.Equal(ClineAgentRunner.DefaultBinary, helpExec.Argv[0]);
    }

    [Fact]
    public async Task SupportsStructuredStreamAsync_ReturnsFalseWhenFlagMissing()
    {
        var sandbox = new CapturingSandbox { HelpOutput = "Usage: cline [options] [command] [prompt]\n  -p, --plan  Run in plan mode" };
        var runner = Runner();

        Assert.False(await runner.SupportsStructuredStreamAsync(sandbox));
    }

    [Fact]
    public void DirectCredentialEnvironmentVariables_ExposesProviderKey()
    {
        // The CLI reads OPENROUTER_API_KEY from the exec environment; the
        // runner seeds nothing else and never emits -k.
        IAgentCredentialEnvironmentPolicy policy = new ClineAgentRunner();

        Assert.Contains("OPENROUTER_API_KEY", policy.DirectCredentialEnvironmentVariables);
    }

    [Fact]
    public void DefaultModelId_ReadsAgentDefaultsSnapshot()
    {
        var runner = new ClineAgentRunner(DefaultsWith("nvidia/nemotron-3.5-lightning:free"));

        Assert.Equal("nvidia/nemotron-3.5-lightning:free", runner.DefaultModelId);
    }

    [Fact]
    public void ConfiguredProvider_FallsBackToOpenRouterWhenUnset()
    {
        Assert.Equal("openrouter", new ClineAgentRunner(defaults: null).ConfiguredProvider);
        Assert.Equal("openrouter", Runner(new ClineOptions { Provider = "  " }).ConfiguredProvider);
    }
}
