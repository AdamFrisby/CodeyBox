using CodeyBox.Agents.Vibe;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="VibeAgentRunner"/>. Uses the shared
/// <c>CapturingSandbox</c> to inspect the argv, stdin, and environment the
/// runner forwards. Argv pins here encode the transport decision verified
/// against vibe 2.25.4: bare <c>-p</c> (programmatic mode; prompt on stdin,
/// never <c>-p</c> argv or the positional <c>PROMPT</c>), <c>--trust</c>,
/// <c>--output streaming</c> (never text/json), <c>--auto-approve</c> (the
/// programmatic default agent is accept-edits, so a headless run must opt
/// into auto-approval explicitly), and the model alias via
/// <c>VIBE_ACTIVE_MODEL</c> (there is no <c>--model</c> flag).
/// </summary>
public sealed class VibeAgentRunnerTests
{
    private static VibeAgentRunner Runner(VibeOptions? options = null) =>
        new(defaults: null, options: options is null ? null : (() => options));

    private static AgentDefaultsSnapshot DefaultsWith(string model) =>
        new(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["vibe"] = model });

    [Fact]
    public void Kind_IsVibe()
    {
        Assert.Equal(AgentKind.Vibe, new VibeAgentRunner().Kind);
    }

    [Fact]
    public void AgentKind_Vibe_RoundTrips()
    {
        Assert.Equal(AgentKind.Vibe, new AgentKind("vibe"));
    }

    [Fact]
    public async Task RunAsync_Argv_UsesProgrammaticOneShotTransport()
    {
        var sandbox = new CapturingSandbox();
        var runner = Runner();

        await runner.RunAsync(sandbox, "/work", "do the thing", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.Equal("vibe", argv[0]);
        // Bare -p enters programmatic mode (prompt arrives on stdin); never
        // the interactive chat interface.
        Assert.Contains("-p", argv);
        Assert.DoesNotContain("--prompt=do the thing", argv);
        // Temporary trust for this invocation only — never persisted.
        Assert.Contains("--trust", argv);
        // Auto-approve: programmatic mode's default agent is accept-edits,
        // and no human exists in the sandbox to approve anything.
        Assert.Contains("--auto-approve", argv);
        Assert.DoesNotContain("--agent", argv);
    }

    [Fact]
    public async Task RunAsync_Argv_UsesStreamingTransport()
    {
        var sandbox = new CapturingSandbox();
        var runner = Runner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var outputIdx = argv.IndexOf("--output");
        Assert.True(outputIdx >= 0, "expected --output flag");
        Assert.Equal("streaming", argv[outputIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_Prompt_TravelsViaStdin_NotArgv()
    {
        // Linux MAX_ARG_STRLEN is 128 KiB per argv element; rework prompts can
        // exceed it. Verified: bare `vibe -p` with a piped-stdin prompt runs
        // programmatic mode and exits.
        var sandbox = new CapturingSandbox();
        var runner = Runner();
        const string prompt = "implement the widget with extra care";

        await runner.RunAsync(sandbox, "/work", prompt, credential: null);

        Assert.Equal(prompt, sandbox.CapturedExec!.Stdin);
        Assert.DoesNotContain(sandbox.CapturedExec.Argv, a => a.Contains("widget", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_NoModelAnywhere_OmitsActiveModelVariable()
    {
        var sandbox = new CapturingSandbox();
        var runner = new VibeAgentRunner(defaults: new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)));

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        var env = sandbox.CapturedExec!.ExtraEnvironment;
        Assert.True(env is null || !env.ContainsKey(VibeAgentRunner.ActiveModelVariable),
            "expected no VIBE_ACTIVE_MODEL when no model is configured");
        Assert.DoesNotContain("--model", sandbox.CapturedExec.Argv);
    }

    [Fact]
    public async Task RunAsync_ExplicitModelId_PassedViaActiveModelVariable()
    {
        // Vibe has no --model flag: per-dispatch model control travels as
        // VIBE_ACTIVE_MODEL, and the value must be a guest-config model alias.
        var sandbox = new CapturingSandbox();
        var runner = new VibeAgentRunner(DefaultsWith("other-alias"));

        await runner.RunAsync(sandbox, "/work", "x", credential: null,
            modelId: "nemotron-free");

        var env = sandbox.CapturedExec!.ExtraEnvironment;
        Assert.NotNull(env);
        Assert.Equal("nemotron-free", env[VibeAgentRunner.ActiveModelVariable]);
        Assert.DoesNotContain("--model", sandbox.CapturedExec.Argv);
    }

    [Fact]
    public async Task RunAsync_DefaultModelId_UsedWhenNoExplicitModel()
    {
        var sandbox = new CapturingSandbox();
        var runner = new VibeAgentRunner(DefaultsWith("nemotron-free"));

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        var env = sandbox.CapturedExec!.ExtraEnvironment;
        Assert.NotNull(env);
        Assert.Equal("nemotron-free", env[VibeAgentRunner.ActiveModelVariable]);
    }

    [Fact]
    public async Task RunAsync_DefaultMaxTurns_EmitsMaxTurnsFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = Runner(new VibeOptions { MaxTurns = 100 });

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
            var runner = Runner(new VibeOptions { MaxTurns = maxTurns });

            await runner.RunAsync(sandbox, "/work", "x", credential: null);

            Assert.DoesNotContain("--max-turns", sandbox.CapturedExec!.Argv);
        }
    }

    [Fact]
    public async Task RunAsync_MissingKeyError_LiftsTerminalDiagnostic()
    {
        // Recorded real shape (vibe 2.25.4, no provider key in the
        // environment): exit 1 with the cause only on stderr.
        const string stderr =
            "Error: Missing OPENROUTER_API_KEY environment variable for openrouter provider. " +
            "Set the environment variable (e.g. in ~/.vibe/.env or your shell), " +
            "or run `vibe --setup` once interactively.";
        var sandbox = new CapturingSandbox(exitCode: 1, stdout: string.Empty, stderr: stderr);
        var runner = Runner();

        var result = await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("Missing OPENROUTER_API_KEY", result.TerminalDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ProviderRejection_LiftsTerminalDiagnostic()
    {
        // Recorded real shape (vibe 2.25.4, $0-limit OpenRouter key against a
        // paid model): exit 1; stdout carries only the user-echo history
        // entry, the cause is the stderr API-error body.
        const string stdout =
            """{"id":"0447d122-1175-477c-96c9-6145edfed5e3","sessionId":"fe267585-22f6-653b-95c5-1ba5c441fdf8","turnId":"7e381af2-b392-46ee-a18b-130d7d0d1718","createdAt":1789580940416,"updatedAt":1789580940416,"generationStatus":"completed","relatedEntryId":null,"type":"message","role":"user","content":[{"type":"text","text":"Say OK."}],"source":"turn_start","userDisplayContent":null}""";
        const string stderr =
            "Error: API error from openrouter (model: anthropic/claude-haiku-4.5): LLM backend error [openrouter]\n" +
            "  status: 403 Forbidden\n" +
            "  provider_message: Key limit exceeded (total limit). Manage it using https://openrouter.ai/workspaces/default/keys/64835ad0564749843e34c8e2e6d1482456dad7a14fbbd107a5a174ea87fc6058";
        var sandbox = new CapturingSandbox(exitCode: 1, stdout: stdout, stderr: stderr);
        var runner = Runner();

        var result = await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("API error from openrouter", result.TerminalDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_HealthyRun_LeavesTerminalDiagnosticNull()
    {
        // Recorded real shape (vibe 2.25.4, OpenRouter nemotron free):
        // history entries only, no Error: lines anywhere.
        const string stdout =
            """{"id":"eaba512d-57c4-41c4-8478-d83c4c75929f","sessionId":"a54c1241-bc61-28d6-0141-2a675c3860c1","turnId":"b6f13933-c15e-45b8-bf46-b419a881ef5b","createdAt":1789580841806,"updatedAt":1789580841806,"generationStatus":"completed","relatedEntryId":null,"type":"message","role":"user","content":[{"type":"text","text":"What is 4+4? Reply with just the number."}],"source":"turn_start","userDisplayContent":null}""" + "\n" +
            """{"id":"265ca6b7-305c-40f6-a8ca-46cca31fd819","sessionId":"a54c1241-bc61-28d6-0141-2a675c3860c1","turnId":"b6f13933-c15e-45b8-bf46-b419a881ef5b","createdAt":1789580847231,"updatedAt":1789580847320,"generationStatus":"completed","relatedEntryId":null,"type":"message","role":"assistant","content":[{"type":"text","text":"8"}],"source":null,"userDisplayContent":null}""";
        var sandbox = new CapturingSandbox(exitCode: 0, stdout: stdout, stderr: string.Empty);
        var runner = Runner();

        var result = await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.Null(result.TerminalDiagnostic);
    }

    [Fact]
    public async Task RunAsync_MissingCliInGuest_SurfacesAsInfrastructureFailureNamingVibe()
    {
        // The antigravity regression: a guest without the CLI must surface as
        // an infrastructure failure naming the cause — never as "no changes".
        // The shell reports the missing binary as exit 127 + command-not-found.
        var sandbox = new CapturingSandbox(
            exitCode: 127,
            stdout: string.Empty,
            stderr: "bash: line 1: vibe: command not found");
        var runner = Runner();

        var result = await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.False(result.Success);
        Assert.NotNull(result.Stderr);
        Assert.Contains("vibe", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("command not found", result.Stderr, StringComparison.OrdinalIgnoreCase);
        var classification = ((IAgentRunner)runner).ClassifyFailure(result);
        Assert.Equal(AgentFailureKind.Infrastructure, classification.Kind);
    }

    [Fact]
    public async Task SupportsStructuredStreamAsync_ReturnsTrueWhenHelpAdvertisesOutputStreaming()
    {
        var sandbox = new CapturingSandbox { HelpOutput = "--output {text,json,streaming}\n  Output format for programmatic mode (-p)" };
        var runner = Runner();

        Assert.True(await runner.SupportsStructuredStreamAsync(sandbox));

        var helpExec = Assert.Single(
            sandbox.Execs, e => e.Argv.Contains("--help"));
        Assert.Equal(VibeAgentRunner.DefaultBinary, helpExec.Argv[0]);
    }

    [Fact]
    public async Task SupportsStructuredStreamAsync_ReturnsFalseWhenFlagMissing()
    {
        var sandbox = new CapturingSandbox { HelpOutput = "usage: vibe [-h] [-v] [--workdir DIR]" };
        var runner = Runner();

        Assert.False(await runner.SupportsStructuredStreamAsync(sandbox));
    }

    [Fact]
    public void DirectCredentialEnvironmentVariables_ExposesOpenRouterKey()
    {
        IAgentCredentialEnvironmentPolicy policy = new VibeAgentRunner();

        Assert.Contains("OPENROUTER_API_KEY", policy.DirectCredentialEnvironmentVariables);
    }

    [Fact]
    public void DefaultModelId_ReadsAgentDefaultsSnapshot()
    {
        var runner = new VibeAgentRunner(DefaultsWith("nemotron-free"));

        Assert.Equal("nemotron-free", runner.DefaultModelId);
    }
}
