using CodeyBox.Agents.Pi;
using CodeyBox.Core;
using CodeyBox.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="PiAgentRunner"/>. Uses the shared
/// <c>CapturingSandbox</c> to inspect the argv and stdin the runner forwards.
/// Argv pins here encode the transport decision verified against pi 0.85.1:
/// <c>--mode json</c> (never raw <c>-p</c>, never <c>--mode rpc</c>), prompt
/// via stdin, <c>--offline</c>, and no approve/trust override.
/// </summary>
public sealed class PiAgentRunnerTests
{
    [Fact]
    public void Kind_IsPi()
    {
        Assert.Equal(AgentKind.Pi, new PiAgentRunner().Kind);
    }

    [Fact]
    public void AgentKind_Pi_RoundTrips()
    {
        Assert.Equal(AgentKind.Pi, new AgentKind("pi"));
    }

    [Fact]
    public async Task RunAsync_Argv_UsesModeJsonTransport()
    {
        var sandbox = new CapturingSandbox();
        var runner = new PiAgentRunner();

        await runner.RunAsync(sandbox, "/work", "do the thing", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.Equal("pi", argv[0]);
        var modeIdx = argv.IndexOf("--mode");
        Assert.True(modeIdx >= 0, "expected --mode flag");
        Assert.Equal("json", argv[modeIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_Argv_NeverUsesPrintOrRpc()
    {
        // Raw -p would drop usage/model/error signal; --mode rpc needs a
        // bidirectional driver loop this runner does not implement.
        var sandbox = new CapturingSandbox();
        var runner = new PiAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.DoesNotContain(argv, a => a == "-p" || a == "--print");
        Assert.DoesNotContain("rpc", argv);
    }

    [Fact]
    public async Task RunAsync_Argv_HasNoSessionAndOffline()
    {
        var sandbox = new CapturingSandbox();
        var runner = new PiAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.Contains("--no-session", argv);
        Assert.Contains("--offline", argv);
    }

    [Fact]
    public async Task RunAsync_Prompt_TravelsViaStdin_NotArgv()
    {
        // Linux MAX_ARG_STRLEN is 128 KiB per argv element; rework prompts can
        // exceed it. Verified: pi --mode json accepts a stdin-only prompt.
        var sandbox = new CapturingSandbox();
        var runner = new PiAgentRunner();
        const string prompt = "implement the widget with extra care";

        await runner.RunAsync(sandbox, "/work", prompt, credential: null);

        Assert.Equal(prompt, sandbox.CapturedExec!.Stdin);
        Assert.DoesNotContain(sandbox.CapturedExec.Argv, a => a.Contains("widget", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_Argv_NeverOverridesProjectTrust()
    {
        // Neither --approve nor --no-approve: the sandbox tree is untrusted,
        // so pi's ask default (ignore project resources) is the safe posture.
        var sandbox = new CapturingSandbox();
        var runner = new PiAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.DoesNotContain(argv, a => a.StartsWith("--approve", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_NoModelAnywhere_OmitsModelFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = new PiAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.DoesNotContain("--model", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_ExplicitModelId_PassedVerbatim()
    {
        var sandbox = new CapturingSandbox();
        var runner = new PiAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null,
            modelId: "anthropic/claude-haiku-4-5");

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("--model");
        Assert.True(modelIdx >= 0);
        Assert.Equal("anthropic/claude-haiku-4-5", argv[modelIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_ConfigDefaultModel_UsedWhenNoExplicitModel()
    {
        var sandbox = new CapturingSandbox();
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["pi"] = "anthropic/claude-haiku-4-5",
            });
        var runner = new PiAgentRunner(defaults);

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("--model");
        Assert.True(modelIdx >= 0);
        Assert.Equal("anthropic/claude-haiku-4-5", argv[modelIdx + 1]);
    }

    [Fact]
    public async Task DefaultModelId_IsNullWithoutConfig()
    {
        Assert.Null(new PiAgentRunner().DefaultModelId);
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
        var runner = new PiAgentRunner();

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
        var runner = new PiAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null, reasoningMode: level);

        Assert.DoesNotContain("--thinking", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_ExitZeroWithTerminalJsonError_LiftsTerminalDiagnostic()
    {
        // Verified pi shape: exit 0 with stopReason=error in the event
        // stream. Without the lift this would terminal-fail as "produced no
        // changes" instead of parking on the quota/auth signal.
        const string errorFrame =
            """{"type":"message_end","message":{"role":"assistant","provider":"anthropic","model":"claude-haiku-4-5","usage":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"totalTokens":0},"stopReason":"error","errorMessage":"401 {\"type\":\"error\",\"error\":{\"type\":\"authentication_error\"}}"}}""";
        var sandbox = new CapturingSandbox(
            exitCode: 0,
            stdout: "{\"type\":\"session\",\"version\":3}\n" + errorFrame + "\n",
            stderr: string.Empty);
        var runner = new PiAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.True(result.Success);
        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("authentication_error", result.TerminalDiagnostic);
    }

    [Fact]
    public async Task RunAsync_HealthyRun_LeavesTerminalDiagnosticEmpty()
    {
        var sandbox = new CapturingSandbox(
            exitCode: 0,
            stdout: "{\"type\":\"session\",\"version\":3}\n{\"type\":\"agent_end\",\"messages\":[],\"willRetry\":false}\n",
            stderr: string.Empty);
        var runner = new PiAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.True(result.Success);
        Assert.Null(result.TerminalDiagnostic);
    }

    [Fact]
    public async Task SupportsStructuredStreamAsync_HelpAdvertisesModeJson_ReturnsTrue()
    {
        var sandbox = new CapturingSandbox { HelpOutput = "--mode <mode>  text (default), json, or rpc\n" };
        var runner = new PiAgentRunner();

        Assert.True(await runner.SupportsStructuredStreamAsync(sandbox));
    }

    [Fact]
    public async Task SupportsStructuredStreamAsync_HelpWithoutMode_ReturnsFalse()
    {
        var sandbox = new CapturingSandbox { HelpOutput = "usage: pi [options]\n" };
        var runner = new PiAgentRunner();

        Assert.False(await runner.SupportsStructuredStreamAsync(sandbox));
    }

    [Fact]
    public void InVmSmokeProbe_BuildsVersionAndHelpSteps()
    {
        var steps = new PiInVmSmokeProbe().BuildSteps(credential: null);

        Assert.Equal(2, steps.Count);
        Assert.Equal(["pi", "--version"], steps[0].Argv);
        // The second step asserts --mode support through grep's exit code
        // (InVmSmokeStep is exit-code-only by contract).
        Assert.Contains("--mode", string.Join(" ", steps[1].Argv));
    }

    [Fact]
    public async Task ModelListProbe_ReturnsKnownSeed()
    {
        var probe = new PiModelListProbe();

        Assert.Equal(AgentKind.Pi, probe.Kind);
        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.Null(result.FailureReason);
        Assert.Contains("anthropic/claude-haiku-4-5", result.ModelIds);
    }

    [Fact]
    public void KnownModels_ValidateKnownId_NoWarning()
    {
        var message = PiKnownModels.ValidateModelIdAgainstProviderList("cls", "anthropic/claude-haiku-4-5", NullLogger.Instance);

        Assert.Null(message);
    }

    [Fact]
    public void KnownModels_ValidateUnknownId_WarnsButAllows()
    {
        var message = PiKnownModels.ValidateModelIdAgainstProviderList("cls", "anthropic/claude-zephyr-9", NullLogger.Instance);

        Assert.NotNull(message);
        Assert.Contains("claude-zephyr-9", message);
    }
}
