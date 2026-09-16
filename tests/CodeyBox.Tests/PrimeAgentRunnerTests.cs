using CodeyBox.Agents.Prime;
using CodeyBox.Core;
using CodeyBox.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="PrimeAgentRunner"/>. Uses the shared
/// <c>CapturingSandbox</c> to inspect the argv and stdin the runner forwards.
/// Argv pins here encode the transport decision verified against prime-agent
/// 0.9.5 with real OpenRouter runs: <c>-p --mode json</c> (the one-shot
/// contract with the JSON event stream — never bare <c>-p</c>, never
/// <c>--mode rpc</c>), prompt via stdin, <c>--no-session</c>,
/// <c>--offline</c>, <c>--provider</c> from hot-reloadable config, and no
/// <c>--autonomous</c> (budget exhaustion exits non-zero, which would fail
/// the item before diffs are staged).
/// </summary>
public sealed class PrimeAgentRunnerTests
{
    [Fact]
    public void Kind_IsPrime()
    {
        Assert.Equal(AgentKind.Prime, new PrimeAgentRunner().Kind);
    }

    [Fact]
    public void AgentKind_Prime_RoundTrips()
    {
        Assert.Equal(AgentKind.Prime, new AgentKind("prime"));
    }

    [Fact]
    public async Task RunAsync_Argv_UsesPrintPlusModeJsonTransport()
    {
        var sandbox = new CapturingSandbox();
        var runner = new PrimeAgentRunner();

        await runner.RunAsync(sandbox, "/work", "do the thing", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.Equal("prime-agent", argv[0]);
        Assert.Contains("-p", argv);
        var modeIdx = argv.IndexOf("--mode");
        Assert.True(modeIdx >= 0, "expected --mode flag");
        Assert.Equal("json", argv[modeIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_Argv_NeverUsesRpc()
    {
        // --mode rpc needs a bidirectional driver loop this runner does not
        // implement; bare -p (no --mode json) would drop usage/model/error
        // signal. The only transport is the pair asserted above.
        var sandbox = new CapturingSandbox();
        var runner = new PrimeAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.DoesNotContain("rpc", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_Argv_HasNoSessionAndOffline()
    {
        var sandbox = new CapturingSandbox();
        var runner = new PrimeAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.Contains("--no-session", argv);
        Assert.Contains("--offline", argv);
    }

    [Fact]
    public async Task RunAsync_Argv_NeverPassesAutonomous()
    {
        // Verified: a plain -p run already works multi-turn until the model
        // stops (a file edit completed across 3 turns, exit 0), while
        // --autonomous budget exhaustion exits 1 ("Autonomous run stopped
        // before terminal evidence") — which the pipeline treats as a
        // reported failure BEFORE staging diffs, losing real work.
        var sandbox = new CapturingSandbox();
        var runner = new PrimeAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.DoesNotContain(
            sandbox.CapturedExec!.Argv,
            a => a.StartsWith("--autonomous", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_Prompt_TravelsViaStdin_NotArgv()
    {
        // Linux MAX_ARG_STRLEN is 128 KiB per argv element; rework prompts can
        // exceed it. Verified live: -p --mode json with a piped-stdin prompt
        // and no positional prompt arg answered on stdin alone.
        var sandbox = new CapturingSandbox();
        var runner = new PrimeAgentRunner();
        const string prompt = "implement the widget with extra care";

        await runner.RunAsync(sandbox, "/work", prompt, credential: null);

        Assert.Equal(prompt, sandbox.CapturedExec!.Stdin);
        Assert.DoesNotContain(sandbox.CapturedExec.Argv, a => a.Contains("widget", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_Argv_DefaultProviderIsOpenRouter()
    {
        // Matches the shipped credential mapping
        // (CODEYBOX_PRIME_API_KEY -> OPENROUTER_API_KEY).
        var sandbox = new CapturingSandbox();
        var runner = new PrimeAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var providerIdx = argv.IndexOf("--provider");
        Assert.True(providerIdx >= 0, "expected --provider flag");
        Assert.Equal("openrouter", argv[providerIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_ConfiguredProvider_Emitted()
    {
        var sandbox = new CapturingSandbox();
        var runner = new PrimeAgentRunner()
        {
            PrimeOptions = static () => new PrimeSectionOptions { Provider = "anthropic" },
        };

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var providerIdx = argv.IndexOf("--provider");
        Assert.True(providerIdx >= 0);
        Assert.Equal("anthropic", argv[providerIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_BlankProvider_OmitsProviderFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = new PrimeAgentRunner()
        {
            PrimeOptions = static () => new PrimeSectionOptions { Provider = "  " },
        };

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.DoesNotContain("--provider", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_NoModelAnywhere_OmitsModelFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = new PrimeAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.DoesNotContain("--model", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_ExplicitModelId_PassedVerbatim()
    {
        var sandbox = new CapturingSandbox();
        var runner = new PrimeAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null,
            modelId: "anthropic/claude-haiku-4.5");

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("--model");
        Assert.True(modelIdx >= 0);
        Assert.Equal("anthropic/claude-haiku-4.5", argv[modelIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_ConfigDefaultModel_UsedWhenNoExplicitModel()
    {
        var sandbox = new CapturingSandbox();
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["prime"] = "anthropic/claude-haiku-4.5",
            });
        var runner = new PrimeAgentRunner(defaults);

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("--model");
        Assert.True(modelIdx >= 0);
        Assert.Equal("anthropic/claude-haiku-4.5", argv[modelIdx + 1]);
    }

    [Fact]
    public async Task DefaultModelId_IsNullWithoutConfig()
    {
        Assert.Null(new PrimeAgentRunner().DefaultModelId);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("minimal")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("max")]
    public async Task RunAsync_KnownThinkingLevel_Emitted(string level)
    {
        var sandbox = new CapturingSandbox();
        var runner = new PrimeAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null, reasoningMode: level);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var idx = argv.IndexOf("--thinking");
        Assert.True(idx >= 0);
        Assert.Equal(level, argv[idx + 1]);
    }

    [Theory]
    [InlineData("ultra")]
    [InlineData("high ")]
    [InlineData("--thinking")]
    public async Task RunAsync_UnknownThinkingLevel_Dropped(string level)
    {
        // A typo must not fail the CLI invocation; the flag is omitted.
        var sandbox = new CapturingSandbox();
        var runner = new PrimeAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null, reasoningMode: level);

        Assert.DoesNotContain("--thinking", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_ExitZeroWithTerminalJsonError_LiftsTerminalDiagnostic()
    {
        // Recorded prime-agent 0.9.5 shape: exit 0 with stopReason=error in
        // the event stream (bad OpenRouter key). Without the lift this would
        // terminal-fail as "produced no changes" instead of parking on the
        // auth signal.
        const string errorFrame =
            """{"type":"message_end","message":{"role":"assistant","provider":"openrouter","model":"nvidia/nemotron-3.5-lightning:free","usage":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"totalTokens":0},"stopReason":"error","errorMessage":"401 User not found.\n\nRun /login to update credentials."}}""";
        var sandbox = new CapturingSandbox(
            exitCode: 0,
            stdout: "{\"type\":\"session\",\"version\":3,\"cwd\":\"/work\"}\n" + errorFrame + "\n",
            stderr: string.Empty);
        var runner = new PrimeAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.True(result.Success);
        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("401 User not found", result.TerminalDiagnostic);
    }

    [Fact]
    public async Task RunAsync_ExitZeroWithStderrNoKeyError_LiftsTerminalDiagnostic()
    {
        // Recorded prime-agent 0.9.5 shape: missing key exits 0 with the
        // plaintext failure on STDERR (unlike pi, which prints it on
        // stdout). The diagnoser must scan both streams.
        var sandbox = new CapturingSandbox(
            exitCode: 0,
            stdout: "{\"type\":\"session\",\"version\":3,\"cwd\":\"/work\"}\n",
            stderr: "No API key found for the selected model.\n");
        var runner = new PrimeAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.True(result.Success);
        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("No API key found", result.TerminalDiagnostic);
    }

    [Fact]
    public async Task RunAsync_HealthyRun_LeavesTerminalDiagnosticEmpty()
    {
        // A healthy run's assistant frames carry stopReason "stop" with
        // usage and no errorMessage — not a diagnostic.
        const string stdout =
            "{\"type\":\"session\",\"version\":3,\"cwd\":\"/work\"}\n" +
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"model\":\"m\"," +
            "\"usage\":{\"input\":1185,\"output\":104,\"cacheRead\":4352,\"cacheWrite\":0,\"totalTokens\":5641},\"stopReason\":\"stop\"}}\n" +
            "{\"type\":\"agent_end\",\"messages\":[]}\n";
        var sandbox = new CapturingSandbox(exitCode: 0, stdout: stdout, stderr: string.Empty);
        var runner = new PrimeAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.True(result.Success);
        Assert.Null(result.TerminalDiagnostic);
    }

    [Fact]
    public async Task SupportsStructuredStreamAsync_HelpAdvertisesModeJson_ReturnsTrue()
    {
        var sandbox = new CapturingSandbox { HelpOutput = "--mode <text|json|rpc|acp|daemon>  Output mode\n" };
        var runner = new PrimeAgentRunner();

        Assert.True(await runner.SupportsStructuredStreamAsync(sandbox));
    }

    [Fact]
    public async Task SupportsStructuredStreamAsync_HelpWithoutMode_ReturnsFalse()
    {
        var sandbox = new CapturingSandbox { HelpOutput = "usage: prime-agent [options]\n" };
        var runner = new PrimeAgentRunner();

        Assert.False(await runner.SupportsStructuredStreamAsync(sandbox));
    }

    [Fact]
    public void MissingCli_ClassifiesAsInfrastructure_NamingCause_NeverNoChanges()
    {
        // Regression guard for the antigravity confusion: a guest without
        // the prime-agent binary (exit 127 / "command not found") must
        // surface as an infrastructure failure naming the cause — never as
        // Normal (which the pipeline would dead-letter as "produced no
        // changes"). The summary below is the verbatim production shape from
        // CliAgentRunnerBase ("agent exited {exitCode}").
        var classification = AgentFailureClassifier.Classify(
            AgentKind.Prime,
            stderr: "bash: prime-agent: command not found",
            stdout: "",
            summary: "agent exited 127");

        Assert.Equal(AgentFailureKind.Infrastructure, classification.Kind);
        Assert.NotEqual(AgentFailureKind.Normal, classification.Kind);
        Assert.Contains("not found", classification.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingCli_PosixNotFoundShape_ClassifiesAsInfrastructure()
    {
        var classification = AgentFailureClassifier.Classify(
            AgentKind.Prime,
            stderr: "/bin/sh: 1: prime-agent: not found",
            stdout: "",
            summary: "agent exited 127");

        Assert.Equal(AgentFailureKind.Infrastructure, classification.Kind);
    }

    [Fact]
    public void InVmSmokeProbe_BuildsVersionAndTransportSteps()
    {
        var steps = new PrimeInVmSmokeProbe().BuildSteps(credential: null);

        Assert.Equal(2, steps.Count);
        Assert.Equal([PrimeAgentRunner.DefaultBinary, "--version"], steps[0].Argv);
        // The second step asserts BOTH halves of the -p --mode json
        // transport through grep's exit code (InVmSmokeStep is
        // exit-code-only by contract).
        var probe = string.Join(" ", steps[1].Argv);
        Assert.Contains(PrimeAgentRunner.DefaultBinary, probe);
        Assert.Contains("--print", probe);
        Assert.Contains("--mode", probe);
    }

    [Fact]
    public async Task SmokeProbe_NoCredential_FailsPersistent()
    {
        var credential = new AgentCredential(
            AgentKind.Prime,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

        var result = await new PrimeSmokeProbe().SmokeTestAsync(credential, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(SmokeFailureCategory.Persistent, result.Category);
        Assert.Contains("CODEYBOX_PRIME_API_KEY", result.FailureReason);
    }

    [Fact]
    public async Task SmokeProbe_WithOpenRouterKey_Passes()
    {
        var credential = new AgentCredential(
            AgentKind.Prime,
            new Dictionary<string, string> { ["OPENROUTER_API_KEY"] = "sk-or-v1-test" },
            new Dictionary<string, string>());

        var result = await new PrimeSmokeProbe().SmokeTestAsync(credential, CancellationToken.None);

        Assert.True(result.Ok);
    }

    [Fact]
    public async Task ModelListProbe_ReturnsKnownSeed()
    {
        var probe = new PrimeModelListProbe();

        Assert.Equal(AgentKind.Prime, probe.Kind);
        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.Null(result.FailureReason);
        Assert.Contains("nvidia/nemotron-3.5-lightning:free", result.ModelIds);
    }

    [Fact]
    public void KnownModels_ValidateKnownId_NoWarning()
    {
        var message = PrimeKnownModels.ValidateModelIdAgainstProviderList(
            "cls", "anthropic/claude-haiku-4.5", NullLogger.Instance);

        Assert.Null(message);
    }

    [Fact]
    public void KnownModels_ValidateUnknownId_WarnsButAllows()
    {
        var message = PrimeKnownModels.ValidateModelIdAgainstProviderList(
            "cls", "anthropic/claude-zephyr-9", NullLogger.Instance);

        Assert.NotNull(message);
        Assert.Contains("claude-zephyr-9", message);
    }
}
