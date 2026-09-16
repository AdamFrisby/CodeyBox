using System.Text.Json;
using CodeyBox.Agents.Autohand;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="AutohandAgentRunner"/>. Uses the shared
/// <c>CapturingSandbox</c> to inspect the argv, stdin, and environment the
/// runner forwards. Argv pins here encode the transport decision verified
/// against autohand-cli 0.9.7: <c>-p</c> with the prompt on stdin (never
/// <c>-p &lt;text&gt;</c> argv), <c>--output-format stream-json</c> (the only
/// structured transport this CLI accepts — whole-doc <c>json</c> is rejected),
/// <c>--bare</c> (mandatory: without it every headless run blocks on an
/// interactive vendor device login), <c>--yes --unrestricted</c> (no human in
/// the sandbox), <c>--offline</c> (no startup catalog/update network), and
/// <c>--model</c> from the member or the config-sourced default.
/// </summary>
public sealed class AutohandAgentRunnerTests
{
    private static AutohandAgentRunner Runner(AutohandOptions? options = null) =>
        new(defaults: null, options: options is null ? null : (() => options));

    private static AgentDefaultsSnapshot DefaultsWith(string model) =>
        new(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["autohand"] = model });

    private static AgentCredential Cred(string key = "test-key") =>
        new(AgentKind.Autohand,
            new Dictionary<string, string> { ["AUTOHAND_API_KEY"] = key },
            new Dictionary<string, string>());

    [Fact]
    public void Kind_IsAutohand()
    {
        Assert.Equal(AgentKind.Autohand, new AutohandAgentRunner().Kind);
    }

    [Fact]
    public void AgentKind_Autohand_RoundTrips()
    {
        Assert.Equal(AgentKind.Autohand, new AgentKind("autohand"));
    }

    [Fact]
    public async Task RunAsync_Argv_UsesOneShotHeadlessTransport()
    {
        var sandbox = new CapturingSandbox();
        var runner = Runner();

        await runner.RunAsync(sandbox, "/work", "do the thing", Cred());

        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.Equal("autohand", argv[0]);
        Assert.Equal("-p", argv[1]);
        // Prompt on stdin with a bare -p; never -p <text> argv
        // (MAX_ARG_STRLEN) and never --prompt-text style positionals.
        Assert.DoesNotContain(argv, a => a.Contains("do the thing", StringComparison.Ordinal));
        // Only structured transport this CLI accepts; whole-doc json is
        // rejected by the binary.
        var formatIdx = argv.IndexOf("--output-format");
        Assert.True(formatIdx >= 0, "expected --output-format flag");
        Assert.Equal("stream-json", argv[formatIdx + 1]);
        // Bare mode gates out the blocking vendor device login.
        Assert.Contains("--bare", argv);
        // Non-interactive autonomy: auto-confirm and no approval prompts.
        Assert.Contains("--yes", argv);
        Assert.Contains("--unrestricted", argv);
        // No startup network beyond inference.
        Assert.Contains("--offline", argv);
        // Never the read-only planning mode or the interactive login gate.
        Assert.DoesNotContain("--plan", argv);
        Assert.DoesNotContain("--login", argv);
    }

    [Fact]
    public async Task RunAsync_Prompt_TravelsViaStdin_NotArgv()
    {
        // Linux MAX_ARG_STRLEN is 128 KiB per argv element; rework prompts can
        // exceed it. Verified: `autohand -p` with piped stdin runs normally.
        var sandbox = new CapturingSandbox();
        var runner = Runner();
        const string prompt = "implement the widget with extra care";

        await runner.RunAsync(sandbox, "/work", prompt, Cred());

        Assert.Equal(prompt, sandbox.CapturedExec!.Stdin);
        Assert.DoesNotContain(sandbox.CapturedExec.Argv, a => a.Contains("widget", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_ExplicitModelId_PassedVerbatim()
    {
        var sandbox = new CapturingSandbox();
        var runner = new AutohandAgentRunner(DefaultsWith("anthropic/claude-sonnet-4"));

        await runner.RunAsync(sandbox, "/work", "x", Cred(),
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
        var runner = new AutohandAgentRunner(DefaultsWith("nvidia/nemotron-3.5-lightning:free"));

        await runner.RunAsync(sandbox, "/work", "x", Cred());

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("--model");
        Assert.True(modelIdx >= 0, "expected --model flag");
        Assert.Equal("nvidia/nemotron-3.5-lightning:free", argv[modelIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_NoModelAnywhere_OmitsModelFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = new AutohandAgentRunner(new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)));

        await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.DoesNotContain("--model", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_SeedsGuestConfigWithProviderModelAndKey()
    {
        // The CLI reads its provider credential exclusively from
        // ~/.autohand/config.json; env vars never backfill the file key
        // (verified live). The seeding payload travels on the writer exec's
        // stdin — never argv — via SandboxCredentialFileWriter.
        var sandbox = new CapturingSandbox();
        var runner = Runner(new AutohandOptions { Provider = "openrouter" });

        await runner.RunAsync(sandbox, "/work", "x", Cred("test-autohand-key-material"));

        var seed = Assert.Single(sandbox.Execs, e =>
            e.Argv.Count > 0 && e.Argv[0] == "bash");
        Assert.NotNull(seed.Stdin);
        Assert.Contains("\"provider\": \"openrouter\"", seed.Stdin, StringComparison.Ordinal);
        Assert.Contains("test-autohand-key-material", seed.Stdin, StringComparison.Ordinal);
        Assert.DoesNotContain(
            sandbox.Execs.SelectMany(e => e.Argv),
            a => a.Contains("test-autohand-key-material", StringComparison.Ordinal));
        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.Equal("autohand", argv[0]);
    }

    [Fact]
    public async Task RunAsync_ConfiguredProvider_SeededVerbatim()
    {
        var sandbox = new CapturingSandbox();
        var runner = Runner(new AutohandOptions { Provider = "  openrouter  " });

        await runner.RunAsync(sandbox, "/work", "x", Cred());

        var seed = Assert.Single(sandbox.Execs, e =>
            e.Argv.Count > 0 && e.Argv[0] == "bash");
        Assert.NotNull(seed.Stdin);
        Assert.Contains("\"provider\": \"openrouter\"", seed.Stdin, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_MissingCredential_FailsFastWithoutDispatch()
    {
        // Without a key every dispatch would land in the blocking first-run
        // wizard; the runner fails fast instead.
        foreach (var credential in new AgentCredential?[] { null, Cred(string.Empty) })
        {
            var sandbox = new CapturingSandbox();
            var runner = Runner();

            var result = await runner.RunAsync(sandbox, "/work", "x", credential);

            Assert.False(result.Success);
            Assert.Contains("CODEYBOX_AUTOHAND_API_KEY", result.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain(sandbox.Execs, e => e.Argv.Count > 0 && e.Argv[0] == "autohand");
        }
    }

    [Fact]
    public async Task RunAsync_ExitZeroErrorFrame_LiftsTerminalDiagnostic()
    {
        // Recorded real shape (autohand-cli 0.9.7, non-completing command):
        // exit 0 with the cause only in a type:error stream frame.
        const string stdout =
            "{\"type\":\"error\",\"message\":\"Command did not complete successfully.\"}";
        var sandbox = new CapturingSandbox(exitCode: 0, stdout: stdout, stderr: string.Empty);
        var runner = Runner();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("did not complete successfully", result.TerminalDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_HealthyRun_LeavesTerminalDiagnosticNull()
    {
        // Recorded real frames (autohand-cli 0.9.7, bare headless run that
        // edited a file): tool events plus a terminal result with content.
        const string stdout =
            "{\"type\":\"tool_start\",\"toolId\":\"call-1\",\"toolName\":\"read_file\",\"toolArgs\":{\"path\":\"/tmp/ahedit/notes.txt\"}}\n" +
            "{\"type\":\"tool_end\",\"toolId\":\"call-1\",\"toolName\":\"read_file\",\"toolSuccess\":true,\"toolOutput\":\"placeholder\"}\n" +
            "{\"type\":\"file_modified\",\"filePath\":\"/tmp/ahedit/notes.txt\",\"changeType\":\"modify\",\"toolId\":\"call-2\"}\n" +
            "{\"type\":\"result\",\"content\":\"The task is complete.\"}";
        var sandbox = new CapturingSandbox(exitCode: 0, stdout: stdout, stderr: string.Empty);
        var runner = Runner();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.Null(result.TerminalDiagnostic);
    }

    [Fact]
    public async Task RunAsync_MissingCliInGuest_SurfacesAsInfrastructureFailureNamingAutohand()
    {
        // A guest without the CLI must surface as an infrastructure failure
        // naming the cause — never as "no changes". The shell reports the
        // missing binary as exit 127 + command-not-found.
        var sandbox = new CapturingSandbox(
            exitCode: 127,
            stdout: string.Empty,
            stderr: "bash: line 1: autohand: command not found");
        var runner = Runner();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.False(result.Success);
        Assert.NotNull(result.Stderr);
        Assert.Contains("autohand", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("command not found", result.Stderr, StringComparison.OrdinalIgnoreCase);
        var classification = ((IAgentRunner)runner).ClassifyFailure(result);
        Assert.Equal(AgentFailureKind.Infrastructure, classification.Kind);
    }

    [Fact]
    public async Task SupportsStructuredStreamAsync_ReturnsTrueWhenHelpAdvertisesStreamJson()
    {
        var sandbox = new CapturingSandbox { HelpOutput = "--output-format <format>              Command output format: stream-json" };
        var runner = Runner();

        Assert.True(await runner.SupportsStructuredStreamAsync(sandbox));

        var helpExec = Assert.Single(
            sandbox.Execs, e => e.Argv.Contains("--help"));
        Assert.Equal(AutohandAgentRunner.DefaultBinary, helpExec.Argv[0]);
    }

    [Fact]
    public async Task SupportsStructuredStreamAsync_ReturnsFalseWhenFlagMissing()
    {
        var sandbox = new CapturingSandbox { HelpOutput = "Usage: autohand [options]\n  -p, --prompt [text]" };
        var runner = Runner();

        Assert.False(await runner.SupportsStructuredStreamAsync(sandbox));
    }

    [Fact]
    public void DirectCredentialEnvironmentVariables_ExposesBareGateKey()
    {
        // The bare-mode gate requires AUTOHAND_API_KEY in the exec
        // environment; the runner also seeds the guest config from it.
        IAgentCredentialEnvironmentPolicy policy = new AutohandAgentRunner();

        Assert.Contains("AUTOHAND_API_KEY", policy.DirectCredentialEnvironmentVariables);
    }

    [Fact]
    public void DefaultModelId_ReadsAgentDefaultsSnapshot()
    {
        var runner = new AutohandAgentRunner(DefaultsWith("nvidia/nemotron-3.5-lightning:free"));

        Assert.Equal("nvidia/nemotron-3.5-lightning:free", runner.DefaultModelId);
    }
}
