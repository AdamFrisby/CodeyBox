using CodeyBox.Agents.Kilo;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="KiloAgentRunner"/>. Uses the shared
/// <c>CapturingSandbox</c> to inspect the argv, stdin, and environment the
/// runner forwards. Argv pins here encode the transport decision verified
/// against @kilocode/cli 7.7.2: <c>run --auto</c> (<c>--auto</c> is
/// mandatory — without it a non-interactive run auto-rejects every
/// permission request and exits 1), <c>--format json</c> (the only transport
/// this runner speaks), the prompt on stdin with no positional
/// <c>[message..]</c> argument (MAX_ARG_STRLEN), and <c>-m</c> from the
/// member or the config-sourced default (never <c>--variant</c>: its
/// vocabulary is provider-specific and unverified on the
/// openai-compatible path).
/// </summary>
public sealed class KiloAgentRunnerTests
{
    private static KiloAgentRunner Runner(KiloOptions? options = null, AgentDefaultsSnapshot? defaults = null) =>
        new(defaults: defaults, options: options is null ? null : (() => options));

    private static AgentDefaultsSnapshot DefaultsWith(string model) =>
        new(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["kilo"] = model });

    private static AgentCredential Cred(string key = "test-key") =>
        new(AgentKind.Kilo,
            new Dictionary<string, string> { ["KILO_API_KEY"] = key },
            new Dictionary<string, string>());

    [Fact]
    public void Kind_IsKilo()
    {
        Assert.Equal(AgentKind.Kilo, new KiloAgentRunner().Kind);
    }

    [Fact]
    public void AgentKind_Kilo_RoundTrips()
    {
        Assert.Equal(AgentKind.Kilo, new AgentKind("kilo"));
    }

    [Fact]
    public async Task RunAsync_Argv_UsesRunAutoFormatJsonTransport()
    {
        var sandbox = new CapturingSandbox();
        var runner = Runner();

        await runner.RunAsync(sandbox, "/work", "do the thing", Cred());

        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.Equal("kilo", argv[0]);
        Assert.Equal("run", argv[1]);
        Assert.Contains("--auto", argv);
        var formatIdx = argv.IndexOf("--format");
        Assert.True(formatIdx >= 0, "expected --format flag");
        Assert.Equal("json", argv[formatIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_Prompt_TravelsViaStdin_NotArgv()
    {
        // Linux MAX_ARG_STRLEN is 128 KiB per argv element; rework prompts can
        // exceed it. Verified: kilo run --auto --format json accepts a
        // stdin-only prompt (no positional [message..]).
        var sandbox = new CapturingSandbox();
        var runner = Runner();
        const string prompt = "implement the widget with extra care";

        await runner.RunAsync(sandbox, "/work", prompt, Cred());

        Assert.Equal(prompt, sandbox.CapturedExec!.Stdin);
        Assert.DoesNotContain(sandbox.CapturedExec.Argv, a => a.Contains("widget", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_Argv_NeverMapsReasoningModeToVariant()
    {
        // --variant is provider-specific and unverified on the
        // openai-compatible path; emitting one could fail dispatches.
        var sandbox = new CapturingSandbox();
        var runner = Runner();

        await runner.RunAsync(sandbox, "/work", "x", Cred(), reasoningMode: "high");

        Assert.DoesNotContain("--variant", sandbox.CapturedExec!.Argv);
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
        const string model = "openai-compatible/nvidia/nemotron-3.5-lightning:free";

        await runner.RunAsync(sandbox, "/work", "x", Cred(), modelId: model);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("-m");
        Assert.True(modelIdx >= 0, "expected -m flag");
        Assert.Equal(model, argv[modelIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_DefaultModelId_UsedWhenNoExplicitModel()
    {
        var sandbox = new CapturingSandbox();
        var runner = Runner(defaults: DefaultsWith("openai-compatible/nvidia/nemotron-3.5-lightning:free"));

        await runner.RunAsync(sandbox, "/work", "x", Cred());

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("-m");
        Assert.True(modelIdx >= 0, "expected -m flag");
        Assert.Equal("openai-compatible/nvidia/nemotron-3.5-lightning:free", argv[modelIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_MissingCredential_FailsFastWithoutDispatch()
    {
        // No KILO_API_KEY in the bundle: the runner must fail here rather
        // than dispatch into the CLI's interactive first-run connect flow.
        var sandbox = new CapturingSandbox();
        var runner = Runner();
        var empty = new AgentCredential(AgentKind.Kilo, new Dictionary<string, string>(), new Dictionary<string, string>());

        var result = await runner.RunAsync(sandbox, "/work", "x", empty);

        Assert.False(result.Success);
        Assert.Contains("CODEYBOX_KILO_API_KEY", result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(sandbox.Execs, e => e.Argv.Count > 0 && e.Argv[0] == "kilo");
    }

    [Fact]
    public async Task RunAsync_ExitOneErrorFrame_LiftsTerminalDiagnostic()
    {
        // Recorded real shape (@kilocode/cli 7.7.2, missing API key):
        // exit 1 with the cause in a type:error stream frame.
        const string stdout =
            "{\"type\":\"error\",\"timestamp\":1789585744519,\"sessionID\":\"ses_f5460ae95ffeLYpfQ1svbD5utB\"," +
            "\"error\":{\"name\":\"APIError\",\"data\":{\"message\":\"No cookie auth credentials found\",\"statusCode\":401}}}";
        var sandbox = new CapturingSandbox(exitCode: 1, stdout: stdout, stderr: "Error: No cookie auth credentials found");
        var runner = Runner();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("No cookie auth credentials found", result.TerminalDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_HealthyRun_LeavesTerminalDiagnosticNull()
    {
        // Recorded real frames (@kilocode/cli 7.7.2, text-only reply that
        // used no tools): step_start / text / step_finish with usage.
        const string stdout =
            "{\"type\":\"step_start\",\"timestamp\":1789585720043,\"sessionID\":\"ses_abc\",\"part\":{\"id\":\"p1\",\"sessionID\":\"ses_abc\",\"messageID\":\"m1\",\"type\":\"step-start\"}}\n" +
            "{\"type\":\"text\",\"timestamp\":1789585720933,\"sessionID\":\"ses_abc\",\"part\":{\"id\":\"p2\",\"sessionID\":\"ses_abc\",\"messageID\":\"m1\",\"type\":\"text\",\"text\":\"KILO_JSON_OK\"}}\n" +
            "{\"type\":\"step_finish\",\"timestamp\":1789585720968,\"sessionID\":\"ses_abc\",\"part\":{\"id\":\"p3\",\"sessionID\":\"ses_abc\",\"messageID\":\"m1\",\"type\":\"step-finish\",\"reason\":\"stop\",\"cost\":0,\"tokens\":{\"total\":12655,\"input\":1724,\"output\":2,\"reasoning\":49,\"cache\":{\"read\":10880,\"write\":0}}}}";
        var sandbox = new CapturingSandbox(exitCode: 0, stdout: stdout, stderr: string.Empty);
        var runner = Runner();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.Null(result.TerminalDiagnostic);
    }

    [Fact]
    public async Task RunAsync_MissingCliInGuest_SurfacesAsInfrastructureFailureNamingKilo()
    {
        // A guest without the CLI must surface as an infrastructure failure
        // naming the cause — never as "no changes". The shell reports the
        // missing binary as exit 127 + command-not-found.
        var sandbox = new CapturingSandbox(
            exitCode: 127,
            stdout: string.Empty,
            stderr: "bash: line 1: kilo: command not found");
        var runner = Runner();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.False(result.Success);
        Assert.NotNull(result.Stderr);
        Assert.Contains("kilo", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("command not found", result.Stderr, StringComparison.OrdinalIgnoreCase);
        var classification = ((IAgentRunner)runner).ClassifyFailure(result);
        Assert.Equal(AgentFailureKind.Infrastructure, classification.Kind);
    }

    [Fact]
    public async Task SupportsStructuredStreamAsync_ReturnsTrueWhenHelpAdvertisesFormatJson()
    {
        var sandbox = new CapturingSandbox { HelpOutput = "--format       format: default (formatted) or json (raw JSON events)" };
        var runner = Runner();

        Assert.True(await runner.SupportsStructuredStreamAsync(sandbox));

        var helpExec = Assert.Single(
            sandbox.Execs, e => e.Argv.Contains("--help"));
        Assert.Equal(KiloAgentRunner.DefaultBinary, helpExec.Argv[0]);
        Assert.Equal("run", helpExec.Argv[1]);
    }

    [Fact]
    public async Task SupportsStructuredStreamAsync_ReturnsFalseWhenFlagMissing()
    {
        var sandbox = new CapturingSandbox { HelpOutput = "kilo run [message..]\n  run kilo with a message" };
        var runner = Runner();

        Assert.False(await runner.SupportsStructuredStreamAsync(sandbox));
    }

    [Fact]
    public void DirectCredentialEnvironmentVariables_ExposesGateKey()
    {
        // The gate key is seeded into the guest kilo.jsonc provider block;
        // the CLI reads the key only from that file on the
        // openai-compatible path.
        IAgentCredentialEnvironmentPolicy policy = new KiloAgentRunner();

        Assert.Contains("KILO_API_KEY", policy.DirectCredentialEnvironmentVariables);
    }

    [Fact]
    public void DefaultModelId_ReadsAgentDefaultsSnapshot()
    {
        var runner = Runner(defaults: DefaultsWith("openai-compatible/nvidia/nemotron-3.5-lightning:free"));

        Assert.Equal("openai-compatible/nvidia/nemotron-3.5-lightning:free", runner.DefaultModelId);
    }

    [Fact]
    public void ConfiguredBaseUrl_FallsBackToOpenRouterDefault()
    {
        Assert.Equal(
            "https://openrouter.ai/api/v1",
            Runner(options: new KiloOptions()).ConfiguredBaseUrl);
    }

    [Fact]
    public void ConfiguredBaseUrl_HonoursOperatorOverride()
    {
        Assert.Equal(
            "https://proxy.internal/v1",
            Runner(options: new KiloOptions { BaseUrl = "https://proxy.internal/v1" }).ConfiguredBaseUrl);
    }
}
