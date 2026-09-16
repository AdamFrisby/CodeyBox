using CodeyBox.Agents.Aider;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="AiderAgentRunner"/>. Uses the shared
/// <c>CapturingSandbox</c> to inspect the argv and stdin the runner forwards.
/// Argv pins here encode the transport decision verified against aider 0.86.2:
/// the one-shot message form via <c>--message-file /dev/stdin</c> (prompt on
/// stdin, never a bare argv prompt), plus the non-interactive hardening flags
/// (<c>--yes-always</c>, <c>--no-auto-commits</c>, <c>--no-gitignore</c>,
/// telemetry/update checks off, <c>--no-pretty</c>).
/// </summary>
public sealed class AiderAgentRunnerTests
{
    [Fact]
    public void Kind_IsAider()
    {
        Assert.Equal(AgentKind.Aider, new AiderAgentRunner().Kind);
    }

    [Fact]
    public void AgentKind_Aider_RoundTrips()
    {
        Assert.Equal(AgentKind.Aider, new AgentKind("aider"));
    }

    [Fact]
    public async Task RunAsync_Argv_UsesMessageFileStdinTransport()
    {
        var sandbox = new CapturingSandbox();
        var runner = new AiderAgentRunner();

        await runner.RunAsync(sandbox, "/work", "do the thing", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.Equal("aider", argv[0]);
        var messageIdx = argv.IndexOf("--message-file");
        Assert.True(messageIdx >= 0, "expected --message-file flag");
        Assert.Equal("/dev/stdin", argv[messageIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_Prompt_TravelsViaStdin_NotArgv()
    {
        // Linux MAX_ARG_STRLEN is 128 KiB per argv element; rework prompts can
        // exceed it. Verified live: aider --message-file /dev/stdin with a
        // piped-stdin prompt runs one-shot and applies edits normally.
        var sandbox = new CapturingSandbox();
        var runner = new AiderAgentRunner();
        const string prompt = "implement the widget with extra care";

        await runner.RunAsync(sandbox, "/work", prompt, credential: null);

        Assert.Equal(prompt, sandbox.CapturedExec!.Stdin);
        Assert.DoesNotContain(sandbox.CapturedExec.Argv, a => a.Contains("widget", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_Argv_NeverPassesBareMessagePrompt()
    {
        // --message would put the whole prompt in one argv element; the
        // /dev/stdin variant is the only prompt channel this runner speaks.
        var sandbox = new CapturingSandbox();
        var runner = new AiderAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.DoesNotContain(argv, a => a == "--message" || a == "-m" || a == "--msg");
    }

    [Fact]
    public async Task RunAsync_Argv_HasNonInteractiveHardening()
    {
        var sandbox = new CapturingSandbox();
        var runner = new AiderAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.Contains("--yes-always", argv);
        Assert.Contains("--no-auto-commits", argv);
        Assert.Contains("--no-gitignore", argv);
        Assert.Contains("--analytics-disable", argv);
        Assert.Contains("--no-check-update", argv);
        Assert.Contains("--no-show-release-notes", argv);
        Assert.Contains("--no-pretty", argv);
        Assert.Equal("/dev/null", argv[argv.IndexOf("--chat-history-file") + 1]);
        Assert.Equal("/dev/null", argv[argv.IndexOf("--input-history-file") + 1]);
    }

    [Fact]
    public async Task RunAsync_Argv_LeavesCommitsAndRepoMapToDefaults()
    {
        // --no-auto-commits keeps edits in the worktree for the pipeline to
        // collect; the runner must never re-enable committing or disable git
        // (aider tracks edits through the repo).
        var sandbox = new CapturingSandbox();
        var runner = new AiderAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.DoesNotContain(argv, a => a == "--auto-commits");
        Assert.DoesNotContain(argv, a => a == "--no-git");
        Assert.DoesNotContain(argv, a => a == "--gui" || a == "--browser");
    }

    [Fact]
    public async Task RunAsync_ExplicitModel_Emitted()
    {
        var sandbox = new CapturingSandbox();
        var runner = new AiderAgentRunner();

        await runner.RunAsync(
            sandbox, "/work", "x", credential: null,
            modelId: "openrouter/anthropic/claude-haiku-4.5");

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("--model");
        Assert.True(modelIdx >= 0);
        Assert.Equal("openrouter/anthropic/claude-haiku-4.5", argv[modelIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_ConfigDefaultModel_UsedWhenNoExplicitModel()
    {
        var sandbox = new CapturingSandbox();
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["aider"] = "openrouter/anthropic/claude-haiku-4.5",
            });
        var runner = new AiderAgentRunner(defaults);

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("--model");
        Assert.True(modelIdx >= 0);
        Assert.Equal("openrouter/anthropic/claude-haiku-4.5", argv[modelIdx + 1]);
    }

    [Fact]
    public void DefaultModelId_IsNullWithoutConfig()
    {
        Assert.Null(new AiderAgentRunner().DefaultModelId);
    }

    [Fact]
    public async Task RunAsync_ExitZeroAuthFailure_LiftsTerminalDiagnostic()
    {
        // Verified live: a bad OpenRouter key exits 0 with only the litellm
        // relay on stdout. Without the lift the pipeline would terminal-fail
        // this as "produced no changes".
        const string stdout =
            "Aider v0.86.2\n" +
            "Model: openrouter/nvidia/nemotron-3.5-lightning:free with whole edit format\n" +
            "litellm.AuthenticationError: AuthenticationError: OpenrouterException - \n" +
            "{\"error\":{\"message\":\"Missing Authentication header\",\"code\":401}}\n" +
            "The API provider is not able to authenticate you. Check your API key.\n";
        var sandbox = new CapturingSandbox(exitCode: 0, stdout: stdout, stderr: "");
        var runner = new AiderAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("litellm.AuthenticationError", result.TerminalDiagnostic);
    }

    [Fact]
    public async Task RunAsync_HealthyRun_LeavesTerminalDiagnosticNull()
    {
        // A healthy run's Tokens/Applied-edit tail must not produce a
        // diagnostic — otherwise every successful dispatch would park.
        const string stdout =
            "Aider v0.86.2\n" +
            "Model: openrouter/nvidia/nemotron-3.5-lightning:free with whole edit format\n" +
            "Tokens: 766 sent, 905 received.\n" +
            "\n" +
            "pong2.txt\n" +
            "Applied edit to pong2.txt\n";
        var sandbox = new CapturingSandbox(exitCode: 0, stdout: stdout, stderr: "");
        var runner = new AiderAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.Null(result.TerminalDiagnostic);
    }

    [Fact]
    public void MissingCli_ClassifiesAsInfrastructure_NamingCause_NeverNoChanges()
    {
        // Regression guard for the antigravity confusion: a guest without the
        // aider binary (exit 127 / "command not found") must surface as an
        // infrastructure failure naming the cause — never as Normal (which the
        // pipeline would dead-letter as "produced no changes"). The summary
        // below is the verbatim production shape from CliAgentRunnerBase
        // ("agent exited {exitCode}").
        var classification = AgentFailureClassifier.Classify(
            AgentKind.Aider,
            stderr: "bash: aider: command not found",
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
            AgentKind.Aider,
            stderr: "/bin/sh: 1: aider: not found",
            stdout: "",
            summary: "agent exited 127");

        Assert.Equal(AgentFailureKind.Infrastructure, classification.Kind);
    }
}
