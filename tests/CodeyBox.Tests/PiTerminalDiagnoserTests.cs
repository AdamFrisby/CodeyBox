using CodeyBox.Agents.Pi;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="PiTerminalDiagnoser"/> using the terminal shapes
/// verified against pi 0.85.1: exit-0 runs whose only failure signal is the
/// JSON event stream (<c>stopReason: "error"</c> + <c>errorMessage</c>) or the
/// plaintext <c>No API key found</c> pre-session line.
/// </summary>
public sealed class PiTerminalDiagnoserTests
{
    [Fact]
    public void NullAndEmpty_ReturnsNull()
    {
        Assert.Null(PiTerminalDiagnoser.TryExtractTerminalError(null));
        Assert.Null(PiTerminalDiagnoser.TryExtractTerminalError(""));
        Assert.Null(PiTerminalDiagnoser.TryExtractTerminalError("   "));
    }

    [Fact]
    public void HealthyRun_ReturnsNull()
    {
        const string stdout =
            "{\"type\":\"session\",\"version\":3,\"id\":\"s\",\"cwd\":\"/work\"}\n" +
            "{\"type\":\"agent_end\",\"messages\":[],\"willRetry\":false}\n";

        Assert.Null(PiTerminalDiagnoser.TryExtractTerminalError(stdout));
    }

    [Fact]
    public void StopReasonError_ExtractsErrorMessage()
    {
        // Live shape from the pi 0.85.1 auth probe (bogus ANTHROPIC_API_KEY).
        const string stdout =
            "{\"type\":\"session\",\"version\":3}\n" +
            "{\"type\":\"message_end\",\"timestamp\":0,\"message\":{\"role\":\"assistant\",\"provider\":\"anthropic\",\"model\":\"claude-haiku-4-5\"," +
            "\"usage\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":0,\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"total\":0}}," +
            "\"stopReason\":\"error\",\"errorMessage\":\"401 {\\\"type\\\":\\\"error\\\",\\\"error\\\":{\\\"type\\\":\\\"authentication_error\\\"}}\",\"cost\":0}}\n";

        var result = PiTerminalDiagnoser.TryExtractTerminalError(stdout);

        Assert.NotNull(result);
        Assert.Contains("authentication_error", result);
    }

    [Fact]
    public void NoApiKeyPlaintextLine_Extracted()
    {
        const string stdout =
            "{\"type\":\"session\",\"version\":3}\n" +
            "No API key found for the selected model.\n";

        var result = PiTerminalDiagnoser.TryExtractTerminalError(stdout);

        Assert.NotNull(result);
        Assert.Contains("No API key found", result);
    }

    [Fact]
    public void NonErrorStopReason_Ignored()
    {
        const string stdout =
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"stopReason\":\"stop\"}}\n";

        Assert.Null(PiTerminalDiagnoser.TryExtractTerminalError(stdout));
    }

    [Fact]
    public void MalformedLines_Skipped_ErrorStillFound()
    {
        const string stdout =
            "not json at all\n" +
            "{\"type\":\"message_end\",\"message\":{\"stopReason\":\"error\",\"errorMessage\":\"boom\"}}\n" +
            "{\"truncated\": true\n";

        Assert.Equal("boom", PiTerminalDiagnoser.TryExtractTerminalError(stdout));
    }

    [Fact]
    public void LongErrorMessage_Truncated()
    {
        var longError = new string('e', PiTerminalDiagnoser.MaxDiagnosticChars + 100);
        var stdout = "{\"type\":\"message_end\",\"message\":{\"stopReason\":\"error\",\"errorMessage\":\"" + longError + "\"}}\n";

        var result = PiTerminalDiagnoser.TryExtractTerminalError(stdout);

        Assert.NotNull(result);
        Assert.True(result!.Length <= PiTerminalDiagnoser.MaxDiagnosticChars + 1);
    }
}
