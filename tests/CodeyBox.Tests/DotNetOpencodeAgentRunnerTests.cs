using CodeyBox.Agents.DotNetOpencode;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="DotNetOpencodeAgentRunner"/>. Uses the shared
/// <c>CapturingSandbox</c> to inspect the argv and stdin the runner forwards.
/// Argv pins here encode the transport decision verified live against
/// 0.1.0-ci.20260905083303.33955573552.1: <c>run --format json
/// --standalone --auto</c> (never bare <c>run</c>, never the managed-service
/// default), prompt via stdin, config-sourced <c>--model</c>.
/// </summary>
public sealed class DotNetOpencodeAgentRunnerTests
{
    [Fact]
    public void Kind_IsDotNetOpencode()
    {
        Assert.Equal(AgentKind.DotNetOpencode, new DotNetOpencodeAgentRunner().Kind);
    }

    [Fact]
    public void AgentKind_DotNetOpencode_RoundTrips()
    {
        Assert.Equal(AgentKind.DotNetOpencode, new AgentKind("dotnet-opencode"));
    }

    [Fact]
    public async Task RunAsync_Argv_UsesRunFormatJsonTransport()
    {
        var sandbox = new CapturingSandbox();
        var runner = new DotNetOpencodeAgentRunner();

        await runner.RunAsync(sandbox, "/work", "do the thing", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.Equal("dotnet-opencode", argv[0]);
        Assert.Equal("run", argv[1]);
        var formatIdx = argv.IndexOf("--format");
        Assert.True(formatIdx >= 0, "expected --format flag");
        Assert.Equal("json", argv[formatIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_Argv_UsesStandaloneAndAuto()
    {
        // --standalone avoids the managed-service port/election overhead (a
        // second dispatch in the same VM fails with "listener address is
        // already in use"); --auto lets permission-gated tool calls proceed
        // headless instead of returning an empty run.
        var sandbox = new CapturingSandbox();
        var runner = new DotNetOpencodeAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.Contains("--standalone", argv);
        Assert.Contains("--auto", argv);
    }

    [Fact]
    public async Task RunAsync_Prompt_TravelsViaStdin_NotArgv()
    {
        // Linux MAX_ARG_STRLEN is 128 KiB per argv element; rework prompts can
        // exceed it. Verified live: redirected stdin is appended to the
        // message by the CLI.
        var sandbox = new CapturingSandbox();
        var runner = new DotNetOpencodeAgentRunner();
        const string prompt = "implement the widget with extra care";

        await runner.RunAsync(sandbox, "/work", prompt, credential: null);

        Assert.Equal(prompt, sandbox.CapturedExec!.Stdin);
        Assert.DoesNotContain(sandbox.CapturedExec.Argv, a => a.Contains("widget", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_NoModelAnywhere_OmitsModelFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = new DotNetOpencodeAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.DoesNotContain("--model", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_ExplicitModelId_PassedVerbatim()
    {
        var sandbox = new CapturingSandbox();
        var runner = new DotNetOpencodeAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null,
            modelId: "anthropic/claude-haiku-4-5");

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("--model");
        Assert.True(modelIdx >= 0, "expected --model flag");
        Assert.Equal("anthropic/claude-haiku-4-5", argv[modelIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_DefaultModelId_UsedWhenNoExplicitModel()
    {
        var sandbox = new CapturingSandbox();
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["dotnet-opencode"] = "anthropic/claude-haiku-4-5",
            });
        var runner = new DotNetOpencodeAgentRunner(defaults);

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("--model");
        Assert.True(modelIdx >= 0, "expected --model flag");
        Assert.Equal("anthropic/claude-haiku-4-5", argv[modelIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_ExitZeroErrorFrame_LiftedIntoTerminalDiagnostic()
    {
        // Upstream permission/form rejections can exit 0 with no model
        // answer; the error frame must surface as TerminalDiagnostic so the
        // pipeline parks it instead of recording "produced no changes".
        const string stdout =
            "{\"type\":\"step_start\",\"timestamp\":1789512734124,\"sessionID\":\"ses_abc\",\"part\":{\"id\":\"prt_1\",\"sessionID\":\"ses_abc\",\"messageID\":\"msg_1\",\"type\":\"step-start\"}}\n" +
            "{\"type\":\"error\",\"timestamp\":1789512734146,\"sessionID\":\"ses_abc\",\"error\":{\"type\":\"provider.auth\",\"message\":\"Provider request failed with HTTP 401.\",\"status\":401}}\n";
        var sandbox = new CapturingSandbox(exitCode: 0, stdout: stdout, stderr: string.Empty);
        var runner = new DotNetOpencodeAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("provider.auth", result.TerminalDiagnostic!, StringComparison.Ordinal);
        Assert.Contains("401", result.TerminalDiagnostic!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_CleanOutput_LeavesTerminalDiagnosticEmpty()
    {
        var sandbox = new CapturingSandbox(exitCode: 0, stdout: "done\n", stderr: string.Empty);
        var runner = new DotNetOpencodeAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.True(string.IsNullOrEmpty(result.TerminalDiagnostic));
    }

    [Fact]
    public async Task SupportsStructuredStreamAsync_AdvertisedFormat_ReturnsTrue()
    {
        var sandbox = new CapturingSandbox { HelpOutput = "Usage:\n  dotnet opencode run [<message>...] [options]\n  --format <format>  Output format: default or json [default: default]\n" };
        var runner = new DotNetOpencodeAgentRunner();

        Assert.True(await runner.SupportsStructuredStreamAsync(sandbox));

        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.Equal(["dotnet-opencode", "run", "--help"], argv);
    }

    [Fact]
    public async Task SupportsStructuredStreamAsync_MissingFormat_ReturnsFalse()
    {
        var sandbox = new CapturingSandbox { HelpOutput = "Usage:\n  dotnet opencode run [<message>...]\n" };
        var runner = new DotNetOpencodeAgentRunner();

        Assert.False(await runner.SupportsStructuredStreamAsync(sandbox));
    }
}
