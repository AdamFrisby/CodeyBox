using CodeyBox.Agents.Antigravity;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Copilot;
using CodeyBox.Agents.Cursor;
using CodeyBox.Agents.Gemini;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class AgentRunnerStructuredStreamFlagTests
{
    [Fact]
    public async Task Claude_WhenCaptureEnabled_UsesStreamJsonVerbose()
    {
        var sandbox = new CapturingSandbox(stdout: "--output-format stream-json --verbose");
        await new ClaudeAgentRunner().RunAsync(sandbox, "/work", "prompt", null, captureStructuredStream: true);

        var argv = sandbox.CapturedExec!.Argv;
        Assert.Contains("--output-format", argv);
        Assert.Contains("stream-json", argv);
        Assert.Contains("--verbose", argv);
    }

    [Fact]
    public async Task Claude_WhenHelpAdvertisesStreamJson_ReportsSupport()
    {
        var sandbox = new CapturingSandbox(stdout: "--output-format stream-json --verbose");

        var supported = await new ClaudeAgentRunner().SupportsStructuredStreamAsync(sandbox);

        Assert.True(supported);
        Assert.Equal(["claude", "--help"], sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task Claude_WhenCaptureDisabled_DoesNotUseStreamJson()
    {
        var sandbox = new CapturingSandbox();
        await new ClaudeAgentRunner().RunAsync(sandbox, "/work", "prompt", null);

        Assert.DoesNotContain("--output-format", sandbox.CapturedExec!.Argv);
        Assert.DoesNotContain("stream-json", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task Claude_WhenProbeFailsWithUnsupportedFormat_FallsBackWithoutStreamJson()
    {
        var sandbox = new CapturingSandbox(exitCode: 1, stderr: "error: unsupported output format stream-json");
        var result = await new ClaudeAgentRunner().RunAsync(sandbox, "/work", "prompt", null, captureStructuredStream: true);

        Assert.DoesNotContain("--output-format", sandbox.CapturedExec!.Argv);
        Assert.DoesNotContain("stream-json", sandbox.CapturedExec!.Argv);
        Assert.Contains("structured stream capture was disabled", result.Stderr);
    }

    [Fact]
    public async Task Codex_WhenCaptureEnabled_UsesJsonEvents()
    {
        var sandbox = new CapturingSandbox(stdout: "--json");
        await new CodexAgentRunner().RunAsync(sandbox, "/work", "prompt", null, captureStructuredStream: true);

        Assert.Contains("--json", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task Codex_WhenJsonStreamAdvertised_UsesJsonStream()
    {
        var sandbox = new CapturingSandbox(stdout: "--json-stream");
        await new CodexAgentRunner().RunAsync(sandbox, "/work", "prompt", null, captureStructuredStream: true);

        Assert.Contains("--json-stream", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task Codex_WhenJsonFlagUnavailable_FallsBackWithoutJsonFlag()
    {
        var sandbox = new CapturingSandbox(stdout: "Usage: codex exec");
        var result = await new CodexAgentRunner().RunAsync(sandbox, "/work", "prompt", null, captureStructuredStream: true);

        Assert.DoesNotContain("--json", sandbox.CapturedExec!.Argv);
        Assert.DoesNotContain("--json-stream", sandbox.CapturedExec!.Argv);
        Assert.Contains("structured stream capture was disabled", result.Stderr);
    }

    [Fact]
    public async Task Gemini_WhenCaptureEnabled_UsesOutputFormatStreamJsonFlag()
    {
        // gemini-cli's structured stream flag is `--output-format stream-json`,
        // not `--json` (which doesn't exist in gemini-cli ≥ 0.40).
        var sandbox = new CapturingSandbox(stdout: "--output-format stream-json");
        await new GeminiAgentRunner().RunAsync(sandbox, "/work", "prompt", null, captureStructuredStream: true);

        Assert.DoesNotContain("--json", sandbox.CapturedExec!.Argv);
        var argv = sandbox.CapturedExec!.Argv.ToList();
        var ofIdx = argv.IndexOf("--output-format");
        Assert.True(ofIdx >= 0, "argv must contain --output-format flag");
        Assert.Equal("stream-json", argv[ofIdx + 1]);
    }

    [Fact]
    public async Task Gemini_WhenStreamJsonUnavailable_FallsBackWithoutFlag()
    {
        var sandbox = new CapturingSandbox(stdout: "Usage: gemini");
        var result = await new GeminiAgentRunner().RunAsync(sandbox, "/work", "prompt", null, captureStructuredStream: true);

        Assert.DoesNotContain("--output-format", sandbox.CapturedExec!.Argv);
        Assert.DoesNotContain("stream-json", sandbox.CapturedExec!.Argv);
        Assert.DoesNotContain("--json", sandbox.CapturedExec!.Argv);
        Assert.Contains("structured stream capture was disabled", result.Stderr);
    }

    [Fact]
    public async Task Gemini_WhenCaptureDisabled_StripsAnsiBeforeStdoutCallback()
    {
        var chunks = new List<string>();
        var sandbox = new CapturingSandbox(stdoutChunk: "\x1b[32mlive stdout\x1b[0m");

        await new GeminiAgentRunner().RunAsync(
            sandbox,
            "/work",
            "prompt",
            null,
            stdoutChunkCallback: chunks.Add);

        Assert.Equal(["live stdout"], chunks);
    }

    [Fact]
    public async Task Gemini_WhenCaptureEnabledAndSupported_ForwardsRawStdoutCallback()
    {
        var chunks = new List<string>();
        var sandbox = new CapturingSandbox(stdout: "--output-format stream-json", stdoutChunk: "\x1b[32m{\"type\":\"event\"}\x1b[0m");

        await new GeminiAgentRunner().RunAsync(
            sandbox,
            "/work",
            "prompt",
            null,
            stdoutChunkCallback: chunks.Add,
            captureStructuredStream: true);

        Assert.Equal(["\x1b[32m{\"type\":\"event\"}\x1b[0m"], chunks);
    }

    [Fact]
    public void Copilot_DoesNotAdvertiseStructuredStreamSupport()
    {
        IAgentRunner runner = new CopilotAgentRunner();
        Assert.False(runner is IStructuredStreamAgentRunner);
    }

    [Fact]
    public async Task Cursor_WhenHelpAdvertisesStreamJson_ReportsSupport()
    {
        var sandbox = new CapturingSandbox(stderr: "Usage: agent --output-format stream-json");
        var runner = new CursorAgentRunner { Binary = "/opt/cursor/agent" };

        var supported = await runner.SupportsStructuredStreamAsync(sandbox);

        Assert.True(supported);
        Assert.Equal(["/opt/cursor/agent", "--help"], sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task Cursor_WhenHelpCommandFails_ReturnsFalse()
    {
        var sandbox = new CapturingSandbox(
            exitCode: 1,
            stdout: "--output-format stream-json",
            stderr: "agent: auth failed");

        var supported = await new CursorAgentRunner().SupportsStructuredStreamAsync(sandbox);

        Assert.False(supported);
        Assert.Equal(["agent", "--help"], sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task Cursor_WhenHelpOnlyAdvertisesOutputFormatWithoutStreamJson_ReturnsFalse()
    {
        // Plausible bug guard: if the marker check became `--output-format` OR
        // `stream-json` (instead of AND), an older Cursor CLI that exposes
        // `--output-format text` but not `stream-json` would falsely report
        // support and the dispatch would crash with "unknown value" on the
        // stream-json flag. Pin: BOTH markers must be present.
        var sandbox = new CapturingSandbox(
            stdout: "  --output-format <text|json>   Output format");
        var supported = await new CursorAgentRunner().SupportsStructuredStreamAsync(sandbox);

        Assert.False(supported);
    }

    [Fact]
    public async Task Cursor_WhenHelpOnlyMentionsStreamJsonWithoutFlag_ReturnsFalse()
    {
        // Symmetric partial-marker case: help text that mentions "stream-json"
        // in prose without exposing the `--output-format` flag must NOT be
        // treated as supporting structured stream capture.
        var sandbox = new CapturingSandbox(
            stdout: "Cursor CLI supports stream-json experimentally; not yet exposed.");
        var supported = await new CursorAgentRunner().SupportsStructuredStreamAsync(sandbox);

        Assert.False(supported);
    }

    [Fact]
    public async Task Antigravity_WhenFunctionalProbeEmitsStructuredNdjson_ReportsSupport()
    {
        var sandbox = new CapturingSandbox
        {
            VersionOutput = "agy version structured-supported",
            HelpOutput = "Usage: agy --input-format stream-json --output-format stream-json",
            StructuredProbeOutput = "{\"type\":\"result\",\"result\":\"ok\"}\n",
        };
        var runner = new AntigravityAgentRunner { Binary = "/opt/agy" };

        var supported = await runner.SupportsStructuredStreamAsync(sandbox);

        Assert.True(supported);
        Assert.Equal(["/opt/agy", "--version"], sandbox.Execs[0].Argv);
        Assert.Equal(["/opt/agy", "--help"], sandbox.Execs[1].Argv);
        Assert.Equal("bash", sandbox.Execs[2].Argv[0]);
        Assert.Contains("--output-format", sandbox.Execs[3].Argv);
        Assert.Contains("stream-json", sandbox.Execs[3].Argv);
        Assert.Equal("/tmp", sandbox.Execs[3].WorkingDirectory);
        AssertStructuredProbeCaps(sandbox.Execs[0]);
        AssertStructuredProbeCaps(sandbox.Execs[1]);
        AssertStructuredProbeCaps(sandbox.Execs[2]);
        AssertStructuredProbeCaps(sandbox.Execs[3]);
    }

    [Fact]
    public async Task Antigravity_WhenHelpMentionsStreamJsonButPrintModeBreaks_ReturnsFalse()
    {
        var sandbox = new CapturingSandbox
        {
            VersionOutput = "agy version structured-broken",
            HelpOutput = "Usage: agy --input-format stream-json --output-format stream-json",
            StructuredProbeOutput = """
                Available subcommands:
                  install   Configure environment paths and shell settings
                  models    List available models
                """,
        };
        var runner = new AntigravityAgentRunner();

        var supported = await runner.SupportsStructuredStreamAsync(sandbox);

        Assert.False(supported);
        Assert.Contains("--output-format", sandbox.Execs[3].Argv);
        Assert.Contains("stream-json", sandbox.Execs[3].Argv);
    }

    [Fact]
    public async Task Antigravity_SupportProbe_CachesFunctionalOutcomeByVersion()
    {
        var runner = new AntigravityAgentRunner();
        var firstSandbox = new CapturingSandbox
        {
            VersionOutput = "agy version cached-supported",
            HelpOutput = "Usage: agy --input-format stream-json --output-format stream-json",
            StructuredProbeOutput = "{\"type\":\"result\",\"result\":\"ok\"}\n",
        };

        Assert.True(await runner.SupportsStructuredStreamAsync(firstSandbox));

        var secondSandbox = new CapturingSandbox
        {
            VersionOutput = "agy version cached-supported",
            HelpOutput = "Usage: agy",
        };

        Assert.True(await runner.SupportsStructuredStreamAsync(secondSandbox));
        Assert.Single(secondSandbox.Execs);
        Assert.Equal(["agy", "--version"], secondSandbox.Execs[0].Argv);

        var thirdSandbox = new CapturingSandbox
        {
            VersionOutput = "agy version cached-unsupported",
            HelpOutput = "Usage: agy --input-format stream-json --output-format stream-json",
            StructuredProbeOutput = """
                Available subcommands:
                  install   Configure environment paths and shell settings
                """,
        };

        Assert.False(await runner.SupportsStructuredStreamAsync(thirdSandbox));
        Assert.Contains(thirdSandbox.Execs, e => e.Argv.Contains("--help"));
        Assert.Contains(thirdSandbox.Execs, e => e.Argv.Contains("--output-format") && e.Argv.Contains("stream-json"));
    }

    [Fact]
    public async Task Antigravity_WhenHelpCommandFails_ReturnsFalse()
    {
        var sandbox = new CapturingSandbox(
            exitCode: 1,
            stdout: "--output-format stream-json",
            stderr: "agy: auth failed")
        {
            VersionOutput = "agy version help-fails",
        };

        var supported = await new AntigravityAgentRunner().SupportsStructuredStreamAsync(sandbox);

        Assert.False(supported);
        Assert.Equal(["agy", "--help"], sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task Antigravity_WhenHelpOnlyAdvertisesOutputFormatWithoutStreamJson_ReturnsFalse()
    {
        var sandbox = new CapturingSandbox
        {
            VersionOutput = "agy version output-format-only",
            HelpOutput = "  --output-format <text|json>   Output format",
        };
        var supported = await new AntigravityAgentRunner().SupportsStructuredStreamAsync(sandbox);

        Assert.False(supported);
        Assert.Equal(["agy", "--help"], sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task Antigravity_WhenHelpOnlyMentionsStreamJsonWithoutFlag_ReturnsFalse()
    {
        var sandbox = new CapturingSandbox
        {
            VersionOutput = "agy version stream-json-only",
            HelpOutput = "agy ships stream-json in a future preview channel.",
        };
        var supported = await new AntigravityAgentRunner().SupportsStructuredStreamAsync(sandbox);

        Assert.False(supported);
        Assert.Equal(["agy", "--help"], sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task Antigravity_WhenProbeOutputLimitExceeded_ReturnsFalseAndCachesUnsupported()
    {
        var runner = new AntigravityAgentRunner();
        var firstSandbox = new CapturingSandbox
        {
            VersionOutput = "agy version output-limit",
            HelpOutput = "Usage: agy --input-format stream-json --output-format stream-json",
            StructuredProbeOutput = "{\"type\":\"result\",\"result\":\"ok\"}\n",
            StructuredProbeStdoutLimitExceeded = true,
        };

        Assert.False(await runner.SupportsStructuredStreamAsync(firstSandbox));

        var secondSandbox = new CapturingSandbox
        {
            VersionOutput = "agy version output-limit",
            HelpOutput = "Usage: agy --input-format stream-json --output-format stream-json",
            StructuredProbeOutput = "{\"type\":\"result\",\"result\":\"ok\"}\n",
        };

        Assert.False(await runner.SupportsStructuredStreamAsync(secondSandbox));
        Assert.Single(secondSandbox.Execs);
        Assert.Equal(["agy", "--version"], secondSandbox.Execs[0].Argv);
    }

    [Fact]
    public async Task Antigravity_WhenVersionOutputTooLong_ReturnsFalseWithoutCachingKey()
    {
        var sandbox = new CapturingSandbox
        {
            VersionOutput = "agy version " + new string('x', 300),
            HelpOutput = "Usage: agy --input-format stream-json --output-format stream-json",
            StructuredProbeOutput = "{\"type\":\"result\",\"result\":\"ok\"}\n",
        };

        var supported = await new AntigravityAgentRunner().SupportsStructuredStreamAsync(sandbox);

        Assert.False(supported);
        Assert.Single(sandbox.Execs);
        Assert.Equal(["agy", "--version"], sandbox.Execs[0].Argv);
        AssertStructuredProbeCaps(sandbox.Execs[0]);
    }

    private static void AssertStructuredProbeCaps(SandboxExec exec)
    {
        Assert.Equal(AntigravityAgentRunner.StructuredStreamProbeMaxStdoutBytes, exec.MaxStdoutBytes);
        Assert.Equal(AntigravityAgentRunner.StructuredStreamProbeMaxStderrBytes, exec.MaxStderrBytes);
    }
}
