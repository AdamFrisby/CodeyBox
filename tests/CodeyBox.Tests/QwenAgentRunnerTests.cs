using CodeyBox.Agents.Qwen;
using CodeyBox.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="QwenAgentRunner"/>. Uses the shared
/// <c>CapturingSandbox</c> to inspect the argv, stdin, and environment the
/// runner forwards. Argv pins here encode the transport decision verified
/// against qwen 0.24.0: <c>--approval-mode yolo --auth-type openai
/// --output-format stream-json</c> (never deprecated <c>-p</c>, never a
/// positional prompt), prompt via stdin, and no trust override.
/// </summary>
public sealed class QwenAgentRunnerTests
{
    [Fact]
    public void Kind_IsQwen()
    {
        Assert.Equal(AgentKind.Qwen, new QwenAgentRunner().Kind);
    }

    [Fact]
    public void AgentKind_Qwen_RoundTrips()
    {
        Assert.Equal(AgentKind.Qwen, new AgentKind("qwen"));
    }

    [Fact]
    public async Task RunAsync_Argv_UsesStructuredTransport()
    {
        var sandbox = new CapturingSandbox();
        var runner = new QwenAgentRunner();

        await runner.RunAsync(sandbox, "/work", "do the thing", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.Equal("qwen", argv[0]);
        Assert.Contains("--approval-mode", argv);
        Assert.Equal("yolo", argv[argv.IndexOf("--approval-mode") + 1]);
        Assert.Contains("--auth-type", argv);
        Assert.Equal("openai", argv[argv.IndexOf("--auth-type") + 1]);
        var formatIdx = argv.IndexOf("--output-format");
        Assert.True(formatIdx >= 0, "expected --output-format flag");
        Assert.Equal("stream-json", argv[formatIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_Argv_NeverUsesDeprecatedPrintOrPositionalPrompt()
    {
        // -p/--prompt is deprecated upstream; a positional prompt would hit
        // the 128 KiB MAX_ARG_STRLEN ceiling on rework prompts.
        var sandbox = new CapturingSandbox();
        var runner = new QwenAgentRunner();

        await runner.RunAsync(sandbox, "/work", "implement the widget", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.DoesNotContain(argv, a => a == "-p" || a == "--prompt" || a == "-i" || a == "--input");
        Assert.DoesNotContain(argv, a => a.Contains("widget", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_Prompt_TravelsViaStdin()
    {
        var sandbox = new CapturingSandbox();
        var runner = new QwenAgentRunner();
        const string prompt = "implement the widget with extra care";

        await runner.RunAsync(sandbox, "/work", prompt, credential: null);

        Assert.Equal(prompt, sandbox.CapturedExec!.Stdin);
    }

    [Fact]
    public async Task RunAsync_Argv_NeverOverridesTrust()
    {
        // Trusted Folders are disabled by default upstream; the sandbox
        // tree is untrusted, so no trust flag is passed either way.
        var sandbox = new CapturingSandbox();
        var runner = new QwenAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.DoesNotContain(argv, a => a.StartsWith("--trust", StringComparison.Ordinal));
        Assert.DoesNotContain(argv, a => a == "--skip-trust");
    }

    [Fact]
    public async Task RunAsync_ExtraEnvironment_SetsUnattendedPosture()
    {
        var sandbox = new CapturingSandbox();
        var runner = new QwenAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        var env = sandbox.CapturedExec!.ExtraEnvironment;
        Assert.NotNull(env);
        Assert.Equal("1", env![QwenAgentRunner.UnattendedRetryEnvironmentVariable]);
        Assert.Equal("1", env[QwenAgentRunner.SuppressYoloWarningEnvironmentVariable]);
    }

    [Fact]
    public async Task RunAsync_NoModelAnywhere_OmitsModelFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = new QwenAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.DoesNotContain("-m", sandbox.CapturedExec!.Argv);
        Assert.DoesNotContain("--model", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_ExplicitModelId_PassedVerbatim()
    {
        var sandbox = new CapturingSandbox();
        var runner = new QwenAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null,
            modelId: "nvidia/nemotron-3.5-lightning:free");

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("-m");
        Assert.True(modelIdx >= 0);
        Assert.Equal("nvidia/nemotron-3.5-lightning:free", argv[modelIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_ConfigDefaultModel_UsedWhenNoExplicitModel()
    {
        var sandbox = new CapturingSandbox();
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["qwen"] = "nvidia/nemotron-3.5-lightning:free",
            });
        var runner = new QwenAgentRunner(defaults);

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("-m");
        Assert.True(modelIdx >= 0);
        Assert.Equal("nvidia/nemotron-3.5-lightning:free", argv[modelIdx + 1]);
    }

    [Fact]
    public async Task DefaultModelId_IsNullWithoutConfig()
    {
        Assert.Null(new QwenAgentRunner().DefaultModelId);
    }

    [Fact]
    public async Task RunAsync_ReasoningMode_Ignored()
    {
        // Qwen has no CLI effort flag; the mode must not leak into argv.
        var sandbox = new CapturingSandbox();
        var runner = new QwenAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null, reasoningMode: "high");

        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.DoesNotContain(argv, a => a.Contains("reason", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(argv, a => a == "high");
    }

    [Fact]
    public async Task RunAsync_ExitOneWithResultError_LiftsTerminalDiagnostic()
    {
        // Verified qwen shape: exit 1 with error_during_execution in the
        // event stream plus AlreadyReportedError on stderr. Without the
        // lift this would terminal-fail as "produced no changes" instead
        // of parking on the quota/auth signal.
        const string stdout =
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s\",\"qwen_code_version\":\"0.24.0\"}\n" +
            "{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"session_id\":\"s\",\"is_error\":true," +
            "\"error\":{\"message\":\"[API Error: 401 Missing Authentication header]\"}}\n";
        const string stderr =
            "{\"error\":{\"type\":\"AlreadyReportedError\",\"message\":\"[API Error: 401 Missing Authentication header]\",\"code\":1}}";
        var sandbox = new CapturingSandbox(exitCode: 1, stdout: stdout, stderr: stderr);
        var runner = new QwenAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.False(result.Success);
        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("Missing Authentication header", result.TerminalDiagnostic);
    }

    [Fact]
    public async Task RunAsync_HealthyRun_LeavesTerminalDiagnosticEmpty()
    {
        var sandbox = new CapturingSandbox(
            exitCode: 0,
            stdout: "{\"type\":\"system\",\"subtype\":\"init\"}\n{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"ok\"}\n",
            stderr: string.Empty);
        var runner = new QwenAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.True(result.Success);
        Assert.Null(result.TerminalDiagnostic);
    }

    [Fact]
    public async Task RunAsync_BudgetAbortExit55_NamesTheCause()
    {
        // Exit 55 carries no provider error frame; the runner must name the
        // budget abort explicitly rather than leaving a generic non-zero.
        var sandbox = new CapturingSandbox(exitCode: 55, stdout: "", stderr: "");
        var runner = new QwenAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.False(result.Success);
        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("55", result.TerminalDiagnostic);
    }

    [Fact]
    public void ClassifyFailure_BudgetExit55_IsNormalWithBudgetReason()
    {
        var runner = new QwenAgentRunner();
        var result = new AgentResult(false, "agent exited 55", null, null);

        var classification = runner.ClassifyFailure(result);

        Assert.Equal(AgentFailureKind.Normal, classification.Kind);
        Assert.Contains("55", classification.Reason);
    }

    [Fact]
    public void ClassifyFailure_TurnCapExit53_IsNormalWithTurnCapReason()
    {
        var runner = new QwenAgentRunner();
        var result = new AgentResult(false, "agent exited 53", null, null);

        var classification = runner.ClassifyFailure(result);

        Assert.Equal(AgentFailureKind.Normal, classification.Kind);
        Assert.Contains("53", classification.Reason);
    }

    [Fact]
    public void ClassifyFailure_SigintExit130_IsInfrastructure()
    {
        var runner = new QwenAgentRunner();
        var result = new AgentResult(false, "agent exited 130", null, null);

        var classification = runner.ClassifyFailure(result);

        Assert.Equal(AgentFailureKind.Infrastructure, classification.Kind);
    }

    [Fact]
    public void ClassifyFailure_OtherExit_DefersToSharedClassifier()
    {
        // Exit 1 with no recognised shape must behave exactly like the
        // shared classifier — the override must not steal generic signals.
        var runner = new QwenAgentRunner();
        var result = new AgentResult(false, "agent exited 1", "some stderr", null);

        var classification = runner.ClassifyFailure(result);

        var expected = AgentFailureClassifier.Classify(AgentKind.Qwen, "some stderr", null, "agent exited 1");
        Assert.Equal(expected.Kind, classification.Kind);
    }

    [Fact]
    public async Task RunAsync_MissingCliInGuest_SurfacesAsInfrastructureNamingTheCause()
    {
        // The antigravity lesson: a missing baseline binary (exit 127 +
        // command-not-found) must surface as an infrastructure failure
        // naming the cause — never as "no changes".
        var sandbox = new CapturingSandbox(
            exitCode: 127,
            stdout: string.Empty,
            stderr: "bash: line 1: qwen: command not found\n");
        var runner = new QwenAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "x", credential: null);
        var classification = runner.ClassifyFailure(result);

        Assert.False(result.Success);
        Assert.Equal(AgentFailureKind.Infrastructure, classification.Kind);
        Assert.NotNull(classification.Reason);
        Assert.Contains("not found", classification.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SupportsStructuredStreamAsync_HelpAdvertisesOutputFormat_ReturnsTrue()
    {
        var sandbox = new CapturingSandbox { HelpOutput = "--output-format <format>  Output format: text (default), json, or stream-json\n" };
        var runner = new QwenAgentRunner();

        Assert.True(await runner.SupportsStructuredStreamAsync(sandbox));
    }

    [Fact]
    public async Task SupportsStructuredStreamAsync_HelpWithoutOutputFormat_ReturnsFalse()
    {
        var sandbox = new CapturingSandbox { HelpOutput = "usage: qwen [options]\n" };
        var runner = new QwenAgentRunner();

        Assert.False(await runner.SupportsStructuredStreamAsync(sandbox));
    }

    [Fact]
    public void InVmSmokeProbe_BuildsVersionAndOutputFormatSteps()
    {
        var steps = new QwenInVmSmokeProbe().BuildSteps(credential: null);

        Assert.Equal(2, steps.Count);
        Assert.Equal(["qwen", "--version"], steps[0].Argv);
        // The second step asserts --output-format/stream-json support
        // through grep's exit code (InVmSmokeStep is exit-code-only by
        // contract). Both pin to the runner's binary constant.
        Assert.Equal(QwenAgentRunner.DefaultBinary, steps[0].Argv[0]);
        Assert.Contains("--output-format", string.Join(" ", steps[1].Argv));
        Assert.Contains("stream-json", string.Join(" ", steps[1].Argv));
    }

    [Fact]
    public async Task ModelListProbe_ReturnsKnownSeed()
    {
        var probe = new QwenModelListProbe();

        Assert.Equal(AgentKind.Qwen, probe.Kind);
        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.Null(result.FailureReason);
        Assert.Contains("nvidia/nemotron-3.5-lightning:free", result.ModelIds);
    }

    [Fact]
    public void KnownModels_ValidateKnownId_NoWarning()
    {
        var message = QwenKnownModels.ValidateModelIdAgainstProviderList("cls", "nvidia/nemotron-3.5-lightning:free", NullLogger.Instance);

        Assert.Null(message);
    }

    [Fact]
    public void KnownModels_ValidateUnknownId_WarnsButAllows()
    {
        var message = QwenKnownModels.ValidateModelIdAgainstProviderList("cls", "some/unknown-model", NullLogger.Instance);

        Assert.NotNull(message);
        Assert.Contains("unknown-model", message);
    }
}
