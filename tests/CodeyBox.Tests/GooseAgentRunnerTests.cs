using CodeyBox.Agents.Goose;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="GooseAgentRunner"/>. Uses the shared
/// <c>CapturingSandbox</c> to inspect the argv, stdin, and environment the
/// runner forwards. Argv pins here encode the transport decision verified
/// against goose 1.50.1: <c>run -i -</c> (prompt on stdin, never <c>-t</c>
/// argv), <c>--output-format stream-json</c> (never text/json),
/// <c>--no-session</c>, never <c>-s/--interactive</c>, and
/// <c>GOOSE_MODE=auto</c> in the exec environment.
/// </summary>
public sealed class GooseAgentRunnerTests
{
    private static GooseAgentRunner Runner(GooseOptions? options = null) =>
        new(defaults: null, options: options is null ? null : (() => options));

    private static AgentDefaultsSnapshot DefaultsWith(string model) =>
        new(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["goose"] = model });

    [Fact]
    public void Kind_IsGoose()
    {
        Assert.Equal(AgentKind.Goose, new GooseAgentRunner().Kind);
    }

    [Fact]
    public void AgentKind_Goose_RoundTrips()
    {
        Assert.Equal(AgentKind.Goose, new AgentKind("goose"));
    }

    [Fact]
    public async Task RunAsync_Argv_UsesOneShotRunTransport()
    {
        var sandbox = new CapturingSandbox();
        var runner = Runner();

        await runner.RunAsync(sandbox, "/work", "do the thing", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.Equal("goose", argv[0]);
        Assert.Equal("run", argv[1]);
        // Prompt on stdin via -i -; never -t argv (MAX_ARG_STRLEN).
        Assert.Contains("-i", argv);
        Assert.Equal("-", argv[argv.IndexOf("-i") + 1]);
        Assert.DoesNotContain("-t", argv);
        Assert.DoesNotContain("--text", argv);
        // One-shot: never the interactive keep-alive flag.
        Assert.DoesNotContain("-s", argv);
        Assert.DoesNotContain("--interactive", argv);
        // Ephemeral VM: never persist sessions.
        Assert.Contains("--no-session", argv);
    }

    [Fact]
    public async Task RunAsync_Argv_UsesStreamJsonTransport()
    {
        var sandbox = new CapturingSandbox();
        var runner = Runner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var formatIdx = argv.IndexOf("--output-format");
        Assert.True(formatIdx >= 0, "expected --output-format flag");
        Assert.Equal("stream-json", argv[formatIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_Prompt_TravelsViaStdin_NotArgv()
    {
        // Linux MAX_ARG_STRLEN is 128 KiB per argv element; rework prompts can
        // exceed it. Verified: `goose run -i -` accepts a stdin-only prompt.
        var sandbox = new CapturingSandbox();
        var runner = Runner();
        const string prompt = "implement the widget with extra care";

        await runner.RunAsync(sandbox, "/work", prompt, credential: null);

        Assert.Equal(prompt, sandbox.CapturedExec!.Stdin);
        Assert.DoesNotContain(sandbox.CapturedExec.Argv, a => a.Contains("widget", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_ExtraEnvironment_ForcesGooseModeAuto()
    {
        // Goose honors no CLI flag for approvals; the mode travels as a
        // process env var. Forced auto: a headless run with any approval
        // mode would stall on a human that does not exist.
        var sandbox = new CapturingSandbox();
        var runner = Runner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        var env = sandbox.CapturedExec!.ExtraEnvironment;
        Assert.NotNull(env);
        Assert.Equal("auto", env["GOOSE_MODE"]);
    }

    [Fact]
    public async Task RunAsync_NoModelAnywhere_OmitsModelFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = new GooseAgentRunner(defaults: new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)));

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.DoesNotContain("--model", sandbox.CapturedExec!.Argv);
        Assert.DoesNotContain("--provider", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_ConfiguredProvider_PassedVerbatim()
    {
        var sandbox = new CapturingSandbox();
        var runner = Runner(new GooseOptions { Provider = "openrouter" });

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var providerIdx = argv.IndexOf("--provider");
        Assert.True(providerIdx >= 0, "expected --provider flag");
        Assert.Equal("openrouter", argv[providerIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_BlankProvider_OmitsProviderFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = Runner(new GooseOptions { Provider = "  " });

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.DoesNotContain("--provider", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_ExplicitModelId_PassedVerbatim()
    {
        var sandbox = new CapturingSandbox();
        var runner = new GooseAgentRunner(DefaultsWith("anthropic/claude-opus-4-7"));

        await runner.RunAsync(sandbox, "/work", "x", credential: null,
            modelId: "nvidia/nemotron-3.5-lightning:free");

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("--model");
        Assert.True(modelIdx >= 0, "expected --model flag");
        Assert.Equal("nvidia/nemotron-3.5-lightning:free", argv[modelIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_DefaultModelId_UsedWhenNoExplicitModel()
    {
        var sandbox = new CapturingSandbox();
        var runner = new GooseAgentRunner(DefaultsWith("nvidia/nemotron-3.5-lightning:free"));

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("--model");
        Assert.True(modelIdx >= 0, "expected --model flag");
        Assert.Equal("nvidia/nemotron-3.5-lightning:free", argv[modelIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_DefaultMaxTurns_EmitsMaxTurnsFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = Runner(new GooseOptions { MaxTurns = 100 });

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var turnsIdx = argv.IndexOf("--max-turns");
        Assert.True(turnsIdx >= 0, "expected --max-turns flag");
        Assert.Equal("100", argv[turnsIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_NonPositiveMaxTurns_OmitsFlag()
    {
        foreach (int? maxTurns in new int?[] { null, 0, -5 })
        {
            var sandbox = new CapturingSandbox();
            var runner = Runner(new GooseOptions { MaxTurns = maxTurns });

            await runner.RunAsync(sandbox, "/work", "x", credential: null);

            Assert.DoesNotContain("--max-turns", sandbox.CapturedExec!.Argv);
        }
    }

    [Fact]
    public async Task RunAsync_ExitZeroAuthError_LiftsTerminalDiagnostic()
    {
        // Recorded real shape (goose 1.50.1, bad OpenRouter key): exit 0
        // with the cause only in a content error block.
        const string stdout = """
            __( O)>  ● new session · openrouter nvidia/nemotron-3.5-lightning:free
               \____)    20260916_6 · /tmp/goosetest
                 L L     goose is ready
            {"type":"message","message":{"id":"msg_1","role":"assistant","created":1789551852,"content":[{"type":"error","kind":"authentication","message":"Ran into this error: Authentication error: Authentication failed for https://openrouter.ai/api/v1/chat/completions. Status: 401 Unauthorized. Response: Missing Authentication header."}],"metadata":{"userVisible":true,"agentVisible":false}}}
            {"type":"complete","total_tokens":0,"input_tokens":0,"output_tokens":0,"cache_read_input_tokens":0,"cache_write_input_tokens":0,"cost_usd":0.0}
            """;
        var sandbox = new CapturingSandbox(exitCode: 0, stdout: stdout, stderr: string.Empty);
        var runner = Runner();

        var result = await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("401 Unauthorized", result.TerminalDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_HealthyRun_LeavesTerminalDiagnosticNull()
    {
        const string stdout = """
            {"type":"message","message":{"id":"gen-1","role":"assistant","created":1789551816,"content":[{"type":"text","text":"GOOSE_OK"}],"metadata":{"userVisible":true}}}
            {"type":"complete","total_tokens":5329,"input_tokens":5257,"output_tokens":72,"cache_read_input_tokens":4352,"cache_write_input_tokens":0,"cost_usd":0.0}
            """;
        var sandbox = new CapturingSandbox(exitCode: 0, stdout: stdout, stderr: string.Empty);
        var runner = Runner();

        var result = await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.Null(result.TerminalDiagnostic);
    }

    [Fact]
    public async Task RunAsync_MissingCliInGuest_SurfacesAsInfrastructureFailureNamingGoose()
    {
        // The antigravity regression: a guest without the CLI must surface as
        // an infrastructure failure naming the cause — never as "no changes".
        // The shell reports the missing binary as exit 127 + command-not-found.
        var sandbox = new CapturingSandbox(
            exitCode: 127,
            stdout: string.Empty,
            stderr: "bash: line 1: goose: command not found");
        var runner = Runner();

        var result = await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.False(result.Success);
        Assert.NotNull(result.Stderr);
        Assert.Contains("goose", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("command not found", result.Stderr, StringComparison.OrdinalIgnoreCase);
        var classification = ((IAgentRunner)runner).ClassifyFailure(result);
        Assert.Equal(AgentFailureKind.Infrastructure, classification.Kind);
    }

    [Fact]
    public async Task SupportsStructuredStreamAsync_ReturnsTrueWhenHelpAdvertisesStreamJson()
    {
        var sandbox = new CapturingSandbox { HelpOutput = "--output-format <FORMAT>\n  [possible values: text, json, stream-json]" };
        var runner = Runner();

        Assert.True(await runner.SupportsStructuredStreamAsync(sandbox));

        var helpExec = Assert.Single(
            sandbox.Execs, e => e.Argv.Contains("--help"));
        Assert.Equal(GooseAgentRunner.DefaultBinary, helpExec.Argv[0]);
    }

    [Fact]
    public async Task SupportsStructuredStreamAsync_ReturnsFalseWhenFlagMissing()
    {
        var sandbox = new CapturingSandbox { HelpOutput = "Usage: goose run [OPTIONS]\n  -t, --text <TEXT>" };
        var runner = Runner();

        Assert.False(await runner.SupportsStructuredStreamAsync(sandbox));
    }

    [Fact]
    public void DirectCredentialEnvironmentVariables_ExposesOpenRouterKey()
    {
        IAgentCredentialEnvironmentPolicy policy = new GooseAgentRunner();

        Assert.Contains("OPENROUTER_API_KEY", policy.DirectCredentialEnvironmentVariables);
    }

    [Fact]
    public void DefaultModelId_ReadsAgentDefaultsSnapshot()
    {
        var runner = new GooseAgentRunner(DefaultsWith("nvidia/nemotron-3.5-lightning:free"));

        Assert.Equal("nvidia/nemotron-3.5-lightning:free", runner.DefaultModelId);
    }
}
