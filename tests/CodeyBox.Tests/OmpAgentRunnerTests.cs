using CodeyBox.Agents.Omp;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="OmpAgentRunner"/>. Uses the shared
/// <c>CapturingSandbox</c> to inspect the argv, stdin, and environment the
/// runner forwards. Argv pins here encode the transport decision verified
/// against omp 18.2.2: <c>-p --mode json --no-session</c> (a bare
/// <c>omp "prompt"</c> is interactive), <c>--model</c> from the member or
/// the config-sourced default, the prompt on stdin with no positional
/// <c>MESSAGES</c> argument (MAX_ARG_STRLEN), and NO <c>--offline</c> (absent
/// from <c>omp --help</c> — pi's flag did not survive the fork).
/// </summary>
public sealed class OmpAgentRunnerTests
{
    private static OmpAgentRunner Runner(AgentDefaultsSnapshot? defaults = null) =>
        new(defaults: defaults);

    private static AgentDefaultsSnapshot DefaultsWith(string model) =>
        new(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["omp"] = model });

    private static AgentCredential Cred(string key = "test-key") =>
        new(AgentKind.Omp,
            new Dictionary<string, string> { ["OPENROUTER_API_KEY"] = key },
            new Dictionary<string, string>());

    [Fact]
    public void Kind_IsOmp()
    {
        Assert.Equal(AgentKind.Omp, new OmpAgentRunner().Kind);
    }

    [Fact]
    public void AgentKind_Omp_RoundTrips()
    {
        Assert.Equal(AgentKind.Omp, new AgentKind("omp"));
    }

    [Fact]
    public async Task RunAsync_Argv_UsesPrintJsonNoSessionTransport()
    {
        var sandbox = new CapturingSandbox();
        var runner = Runner();

        await runner.RunAsync(sandbox, "/work", "do the thing", Cred());

        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.Equal("omp", argv[0]);
        Assert.Contains("-p", argv);
        var modeIdx = argv.IndexOf("--mode");
        Assert.True(modeIdx >= 0, "expected --mode flag");
        Assert.Equal("json", argv[modeIdx + 1]);
        Assert.Contains("--no-session", argv);
    }

    [Fact]
    public async Task RunAsync_Argv_NeverPassesOfflineFlag()
    {
        // --offline is absent from `omp --help` (verified against omp
        // 18.2.2): pi's flag did not survive the fork, so emitting it would
        // fail the dispatch at the CLI layer.
        var sandbox = new CapturingSandbox();
        var runner = Runner();

        await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.DoesNotContain("--offline", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_Prompt_TravelsViaStdin_NotArgv()
    {
        // Linux MAX_ARG_STRLEN is 128 KiB per argv element; rework prompts can
        // exceed it. Verified: `omp -p --mode json` accepts a stdin-only
        // prompt (no positional MESSAGES arg).
        var sandbox = new CapturingSandbox();
        var runner = Runner();
        const string prompt = "implement the widget with extra care";

        await runner.RunAsync(sandbox, "/work", prompt, Cred());

        Assert.Equal(prompt, sandbox.CapturedExec!.Stdin);
        Assert.DoesNotContain(sandbox.CapturedExec.Argv, a => a.Contains("widget", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_NoModelAnywhere_OmitsModelFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = Runner();

        await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.DoesNotContain("--model", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_ExplicitModelId_PassedVerbatim()
    {
        var sandbox = new CapturingSandbox();
        var runner = Runner();
        const string model = "nvidia/nemotron-3.5-lightning:free";

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
        var runner = Runner(defaults: DefaultsWith("nvidia/nemotron-3.5-lightning:free"));

        await runner.RunAsync(sandbox, "/work", "x", Cred());

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("--model");
        Assert.True(modelIdx >= 0, "expected --model flag");
        Assert.Equal("nvidia/nemotron-3.5-lightning:free", argv[modelIdx + 1]);
    }

    [Theory]
    [InlineData("high", true)]
    [InlineData("auto", true)]
    [InlineData("off", true)]
    [InlineData("turbo", false)]
    [InlineData("", false)]
    public async Task RunAsync_ThinkingFlag_OnlyForAllowlistedLevels(string level, bool expectFlag)
    {
        // --thinking vocabulary verified against omp 18.2.2 (pi's levels
        // plus auto); anything else is ignored so a typo cannot fail a
        // dispatch at the CLI layer.
        var sandbox = new CapturingSandbox();
        var runner = Runner();

        await runner.RunAsync(sandbox, "/work", "x", Cred(), reasoningMode: level);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var thinkingIdx = argv.IndexOf("--thinking");
        if (expectFlag)
        {
            Assert.True(thinkingIdx >= 0, "expected --thinking flag");
            Assert.Equal(level, argv[thinkingIdx + 1]);
        }
        else
        {
            Assert.Equal(-1, thinkingIdx);
        }
    }

    [Fact]
    public async Task RunAsync_ExitOneQuotaFrame_LiftsTerminalDiagnostic()
    {
        // Recorded real shape (omp 18.2.2, paid model on a $0-spend-limit
        // OpenRouter key): exit 1 with the provider refusal verbatim in
        // errorMessage on message_end / turn_end (key-id tail redacted —
        // assertions only touch the stable refusal prefix).
        const string stdout =
            "{\"type\":\"session\",\"version\":3,\"id\":\"s\",\"timestamp\":\"2026-09-16T23:49:46.523Z\",\"cwd\":\"/tmp/omptest\"}\n" +
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[],\"api\":\"openrouter\",\"provider\":\"openrouter\"," +
            "\"model\":\"anthropic/claude-haiku-4-5\",\"usage\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":0," +
            "\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"total\":0}},\"stopReason\":\"error\"," +
            "\"errorMessage\":\"403 Key limit exceeded (total limit).\"}}\n" +
            "{\"type\":\"turn_end\",\"message\":{\"role\":\"assistant\",\"content\":[],\"stopReason\":\"error\"," +
            "\"errorMessage\":\"403 Key limit exceeded (total limit).\"},\"toolResults\":[]}";
        var sandbox = new CapturingSandbox(exitCode: 1, stdout: stdout, stderr: string.Empty);
        var runner = Runner();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("Key limit exceeded", result.TerminalDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_MissingKeyStderrCrash_LiftsTerminalDiagnostic()
    {
        // Recorded real shape (omp 18.2.2, no usable key): exit 1 with only
        // the session header on stdout and the bun crash
        // `No API key found for anthropic.` on stderr.
        const string stdout =
            "{\"type\":\"session\",\"version\":3,\"id\":\"s\",\"timestamp\":\"2026-09-16T23:49:32.601Z\",\"cwd\":\"/tmp/omptest\"}";
        const string stderr = "error: No API key found for anthropic.";
        var sandbox = new CapturingSandbox(exitCode: 1, stdout: stdout, stderr: stderr);
        var runner = Runner();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("No API key found", result.TerminalDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_HealthyRun_LeavesTerminalDiagnosticNull()
    {
        // Recorded real frames (omp 18.2.2, text-only "PING" reply):
        // session header + assistant message_end with usage and
        // stopReason stop (thinking/text content elided — usage and the
        // stop reason are what this assertion pins).
        const string stdout =
            "{\"type\":\"session\",\"version\":3,\"id\":\"s\",\"timestamp\":\"2026-09-16T23:49:36.704Z\",\"cwd\":\"/tmp/omptest\"}\n" +
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[],\"api\":\"openrouter\",\"provider\":\"openrouter\"," +
            "\"model\":\"nvidia/nemotron-3.5-lightning:free\",\"usage\":{\"input\":19200,\"output\":45,\"cacheRead\":0,\"cacheWrite\":0," +
            "\"totalTokens\":19245,\"reasoningTokens\":45,\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"total\":0}}," +
            "\"stopReason\":\"stop\"}}\n" +
            "{\"type\":\"turn_end\",\"message\":{\"role\":\"assistant\",\"stopReason\":\"stop\"},\"toolResults\":[]}";
        var sandbox = new CapturingSandbox(exitCode: 0, stdout: stdout, stderr: string.Empty);
        var runner = Runner();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.Null(result.TerminalDiagnostic);
    }

    [Fact]
    public async Task RunAsync_MissingCliInGuest_SurfacesAsInfrastructureFailureNamingOmp()
    {
        // A guest without the CLI must surface as an infrastructure failure
        // naming the cause — never as "no changes". The shell reports the
        // missing binary as exit 127 + command-not-found.
        var sandbox = new CapturingSandbox(
            exitCode: 127,
            stdout: string.Empty,
            stderr: "bash: line 1: omp: command not found");
        var runner = Runner();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.False(result.Success);
        Assert.NotNull(result.Stderr);
        Assert.Contains("omp", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("command not found", result.Stderr, StringComparison.OrdinalIgnoreCase);
        var classification = ((IAgentRunner)runner).ClassifyFailure(result);
        Assert.Equal(AgentFailureKind.Infrastructure, classification.Kind);
    }

    [Fact]
    public async Task SupportsStructuredStreamAsync_ReturnsTrueWhenHelpAdvertisesModeJson()
    {
        var sandbox = new CapturingSandbox { HelpOutput = "--mode=<value>  Output mode: text (default), json, rpc, or rpc-ui" };
        var runner = Runner();

        Assert.True(await runner.SupportsStructuredStreamAsync(sandbox));

        var helpExec = Assert.Single(
            sandbox.Execs, e => e.Argv.Contains("--help"));
        Assert.Equal(OmpAgentRunner.DefaultBinary, helpExec.Argv[0]);
    }

    [Fact]
    public async Task SupportsStructuredStreamAsync_ReturnsFalseWhenFlagMissing()
    {
        var sandbox = new CapturingSandbox { HelpOutput = "omp [COMMAND]\n  run omp with a message" };
        var runner = Runner();

        Assert.False(await runner.SupportsStructuredStreamAsync(sandbox));
    }

    [Fact]
    public void DirectCredentialEnvironmentVariables_ExposesOpenRouterKey()
    {
        // The shipped mapping (CODEYBOX_OMP_API_KEY -> OPENROUTER_API_KEY)
        // is read by the CLI directly from the environment — no config-file
        // seeding, no --api-key argv.
        IAgentCredentialEnvironmentPolicy policy = new OmpAgentRunner();

        Assert.Contains("OPENROUTER_API_KEY", policy.DirectCredentialEnvironmentVariables);
    }

    [Fact]
    public void DefaultModelId_ReadsAgentDefaultsSnapshot()
    {
        var runner = Runner(defaults: DefaultsWith("nvidia/nemotron-3.5-lightning:free"));

        Assert.Equal("nvidia/nemotron-3.5-lightning:free", runner.DefaultModelId);
    }
}
