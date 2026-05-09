using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Copilot;
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
}
