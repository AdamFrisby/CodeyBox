using CodeyBox.Agents.Continue;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="ContinueAgentRunner"/>. Uses the shared
/// <c>CapturingSandbox</c> to inspect the argv, stdin, and seeded guest
/// config the runner forwards. Argv pins here encode the transport decision
/// verified against @continuedev/cli 1.5.47: <c>--print --auto</c> (NO
/// <c>--format json</c> — verified to coerce the model's answer rather than
/// frame the transport, which would corrupt work output), the prompt on
/// stdin with no positional argument (MAX_ARG_STRLEN), and the dispatch
/// model in the seeded single-entry guest <c>config.yaml</c>
/// (first-entry-wins selection — a per-member <c>ModelId</c> can never
/// silently dispatch the wrong model).
/// </summary>
public sealed class ContinueAgentRunnerTests
{
    private const string FreeModel = "nvidia/nemotron-3.5-lightning:free";

    private static ContinueAgentRunner Runner(ContinueOptions? options = null, AgentDefaultsSnapshot? defaults = null) =>
        new(defaults: defaults, options: options is null ? null : (() => options));

    private static ContinueAgentRunner RunnerWithDefault(string model = FreeModel) =>
        Runner(defaults: new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["continue"] = model }));

    private static AgentCredential Cred(string key = "test-key") =>
        new(AgentKind.Continue,
            new Dictionary<string, string> { ["OPENROUTER_API_KEY"] = key },
            new Dictionary<string, string>());

    private static SandboxExec CnExec(CapturingSandbox sandbox) =>
        Assert.Single(sandbox.Execs, e => e.Argv.Count > 0 && e.Argv[0] == "cn");

    private static string SeedStdin(CapturingSandbox sandbox) =>
        Assert.Single(sandbox.Execs, e => e.Argv.Count > 0 && e.Argv[0] == "bash").Stdin
            ?? throw new Xunit.Sdk.XunitException("expected the config-seed exec to carry stdin");

    [Fact]
    public void Kind_IsContinue()
    {
        Assert.Equal(AgentKind.Continue, new ContinueAgentRunner().Kind);
    }

    [Fact]
    public void AgentKind_Continue_RoundTrips()
    {
        Assert.Equal(AgentKind.Continue, new AgentKind("continue"));
    }

    [Fact]
    public async Task RunAsync_Argv_UsesPrintAutoTransportWithoutFormatJson()
    {
        var sandbox = new CapturingSandbox();
        var runner = RunnerWithDefault();

        await runner.RunAsync(sandbox, "/work", "do the thing", Cred());

        var argv = CnExec(sandbox).Argv.ToList();
        Assert.Equal(["cn", "--print", "--auto"], argv);
        Assert.DoesNotContain("--format", argv);
        Assert.DoesNotContain("--model", argv);
        Assert.DoesNotContain("-m", argv);
    }

    [Fact]
    public async Task RunAsync_Prompt_TravelsViaStdin_NotArgv()
    {
        // Linux MAX_ARG_STRLEN is 128 KiB per argv element; rework prompts can
        // exceed it. Verified: cn --print --auto accepts a stdin-only prompt
        // (no positional argument).
        var sandbox = new CapturingSandbox();
        var runner = RunnerWithDefault();
        const string prompt = "implement the widget with extra care";

        await runner.RunAsync(sandbox, "/work", prompt, Cred());

        var cn = CnExec(sandbox);
        Assert.Equal(prompt, cn.Stdin);
        Assert.DoesNotContain(cn.Argv, a => a.Contains("widget", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_SeedsSingleModelGuestConfig_BeforeDispatch()
    {
        var sandbox = new CapturingSandbox();
        var runner = RunnerWithDefault();

        await runner.RunAsync(sandbox, "/work", "x", Cred("live-key"));

        var seed = SeedStdin(sandbox);
        Assert.Contains("provider: openrouter", seed, StringComparison.Ordinal);
        Assert.Contains(FreeModel, seed, StringComparison.Ordinal);
        Assert.Contains("https://openrouter.ai/api/v1", seed, StringComparison.Ordinal);
        Assert.Contains("live-key", seed, StringComparison.Ordinal);
        // Exactly one model entry: first-entry-wins selection leaves no room
        // for a union.
        Assert.Equal(1, seed.Split("- name:", StringSplitOptions.None).Length - 1);
        // The seed write precedes the dispatch.
        Assert.True(
            sandbox.Execs.FindIndex(e => e.Argv.Count > 0 && e.Argv[0] == "bash")
            < sandbox.Execs.FindIndex(e => e.Argv.Count > 0 && e.Argv[0] == "cn"));
    }

    [Fact]
    public async Task RunAsync_ExplicitModelId_WinsInSeededConfig()
    {
        var sandbox = new CapturingSandbox();
        var runner = RunnerWithDefault();
        const string explicitModel = "z-ai/glm-5.2:free";

        await runner.RunAsync(sandbox, "/work", "x", Cred(), modelId: explicitModel);

        Assert.Contains(explicitModel, SeedStdin(sandbox), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NoModelAnywhere_FailsFastWithoutDispatch()
    {
        // Neither member nor default supplies a model: writing a modelless
        // file the CLI rejects would only fail later with a vaguer cause.
        var sandbox = new CapturingSandbox();
        var runner = Runner();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.False(result.Success);
        Assert.Contains("CodeyBox:AgentDefaults[continue]", result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(sandbox.Execs, e => e.Argv.Count > 0 && e.Argv[0] == "cn");
    }

    [Fact]
    public async Task RunAsync_Argv_NeverMapsReasoningMode()
    {
        // cn exposes no reasoning flag on the headless path; emitting one
        // could fail dispatches.
        var sandbox = new CapturingSandbox();
        var runner = RunnerWithDefault();

        await runner.RunAsync(sandbox, "/work", "x", Cred(), reasoningMode: "high");

        var argv = CnExec(sandbox).Argv.ToList();
        Assert.DoesNotContain("--reasoning", argv);
        Assert.DoesNotContain("--effort", argv);
        Assert.DoesNotContain("--thinking", argv);
    }

    [Fact]
    public async Task RunAsync_MissingCredential_FailsFastWithoutDispatch()
    {
        // No OPENROUTER_API_KEY in the bundle: the runner must fail here
        // rather than dispatch into a provider call that can only fail at
        // request time.
        var sandbox = new CapturingSandbox();
        var runner = RunnerWithDefault();
        var empty = new AgentCredential(AgentKind.Continue, new Dictionary<string, string>(), new Dictionary<string, string>());

        var result = await runner.RunAsync(sandbox, "/work", "x", empty);

        Assert.False(result.Success);
        Assert.Contains("CODEYBOX_CONTINUE_API_KEY", result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(sandbox.Execs, e => e.Argv.Count > 0 && e.Argv[0] == "cn");
    }

    [Fact]
    public async Task RunAsync_ExitZeroErrorEnvelope_LiftsTerminalDiagnostic()
    {
        // Recorded real shape (@continuedev/cli 1.5.47, $0-spend-limit key
        // against a paid model): exit 0 with the cause in the status:error
        // envelope on stdout.
        const string stdout =
            "{\"status\":\"error\",\"message\":\"403 Key limit exceeded (total limit). Manage it using https://openrouter.ai/workspaces/default/keys/64835ad0564749843e34c8e2e6d1482456dad7a14fbbd107a5a174ea87fc6058\"}";
        var sandbox = new CapturingSandbox(exitCode: 0, stdout: stdout, stderr: string.Empty);
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("Key limit exceeded", result.TerminalDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_OnboardingGateEnvelope_LiftsTerminalDiagnostic()
    {
        // The known first-run onboarding-gate failure shares the envelope
        // shape with a different message; it must classify the same way.
        const string stdout =
            "{\"status\":\"error\",\"message\":\"The request failed and the interceptors did not return an alternative response\"}";
        var sandbox = new CapturingSandbox(exitCode: 0, stdout: stdout, stderr: string.Empty);
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("interceptors did not return an alternative response", result.TerminalDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_HealthyRun_LeavesTerminalDiagnosticNull()
    {
        // Recorded real output (@continuedev/cli 1.5.47, plain-text reply):
        // free text is not an error envelope.
        var sandbox = new CapturingSandbox(exitCode: 0, stdout: "HELLO-CN-OK\n", stderr: string.Empty);
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.Null(result.TerminalDiagnostic);
    }

    [Fact]
    public async Task RunAsync_EmptyReply_LeavesTerminalDiagnosticNull()
    {
        // Recorded real behaviour: a file-edit run replied with empty stdout
        // (exit 0, change merged). Empty output is success, not failure.
        var sandbox = new CapturingSandbox(exitCode: 0, stdout: string.Empty, stderr: string.Empty);
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.True(result.Success);
        Assert.Null(result.TerminalDiagnostic);
    }

    [Fact]
    public async Task RunAsync_MissingCliInGuest_SurfacesAsInfrastructureFailureNamingCause()
    {
        // A guest without the CLI must surface as an infrastructure failure
        // naming the cause — never as "no changes". The shell reports the
        // missing binary as exit 127 + command-not-found.
        var sandbox = new CapturingSandbox(
            exitCode: 127,
            stdout: string.Empty,
            stderr: "bash: line 1: cn: command not found");
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.False(result.Success);
        Assert.NotNull(result.Stderr);
        Assert.Contains("cn", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("command not found", result.Stderr, StringComparison.OrdinalIgnoreCase);
        var classification = ((IAgentRunner)runner).ClassifyFailure(result);
        Assert.Equal(AgentFailureKind.Infrastructure, classification.Kind);
    }

    [Fact]
    public void DirectCredentialEnvironmentVariables_ExposesGateKey()
    {
        // The gate key is seeded into the guest config.yaml model entry; the
        // CLI reads the key only from that file on the config path.
        IAgentCredentialEnvironmentPolicy policy = new ContinueAgentRunner();

        Assert.Contains("OPENROUTER_API_KEY", policy.DirectCredentialEnvironmentVariables);
    }

    [Fact]
    public void DefaultModelId_ReadsAgentDefaultsSnapshot()
    {
        var runner = RunnerWithDefault("z-ai/glm-5.2:free");

        Assert.Equal("z-ai/glm-5.2:free", runner.DefaultModelId);
    }

    [Fact]
    public void ConfiguredBaseUrl_FallsBackToOpenRouterDefault()
    {
        Assert.Equal(
            "https://openrouter.ai/api/v1",
            Runner(options: new ContinueOptions()).ConfiguredBaseUrl);
    }

    [Fact]
    public void ConfiguredBaseUrl_HonoursOperatorOverride()
    {
        Assert.Equal(
            "https://proxy.internal/v1",
            Runner(options: new ContinueOptions { BaseUrl = "https://proxy.internal/v1 " }).ConfiguredBaseUrl);
    }

    [Fact]
    public async Task RunTextOnlyAsync_SeedsConfigAndDispatchesInSandbox()
    {
        var sandbox = new CapturingSandbox(exitCode: 0, stdout: "text answer", stderr: string.Empty);
        var runner = RunnerWithDefault();

        var result = await runner.RunTextOnlyAsync("summarise", Cred(), sandbox: sandbox, workingDirectory: "/work");

        Assert.True(result.Success);
        Assert.Equal("text answer", result.Output);
        Assert.Contains(FreeModel, SeedStdin(sandbox), StringComparison.Ordinal);
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
    public async Task RunTextOnlyAsync_MissingCredential_FailsFastWithoutDispatch()
    {
        var sandbox = new CapturingSandbox();
        var runner = RunnerWithDefault();
        var empty = new AgentCredential(AgentKind.Continue, new Dictionary<string, string>(), new Dictionary<string, string>());

        var result = await runner.RunTextOnlyAsync("summarise", empty, sandbox: sandbox, workingDirectory: "/work");

        Assert.False(result.Success);
        Assert.Contains("CODEYBOX_CONTINUE_API_KEY", result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(sandbox.Execs, e => e.Argv.Count > 0 && e.Argv[0] == "cn");
    }
}
