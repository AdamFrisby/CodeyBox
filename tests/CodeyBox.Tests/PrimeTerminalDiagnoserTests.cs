using CodeyBox.Agents.Prime;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="PrimeTerminalDiagnoser"/> using the terminal shapes
/// recorded against prime-agent 0.9.5: exit-0 runs whose only failure signal
/// is the structured <c>stopReason:"error"</c> frame on stdout or the
/// plaintext <c>No API key found</c> line on stderr.
/// </summary>
public sealed class PrimeTerminalDiagnoserTests
{
    [Fact]
    public void NullAndEmpty_ReturnsNull()
    {
        Assert.Null(PrimeTerminalDiagnoser.TryExtractTerminalError(null, null));
        Assert.Null(PrimeTerminalDiagnoser.TryExtractTerminalError("", ""));
        Assert.Null(PrimeTerminalDiagnoser.TryExtractTerminalError("   ", "  "));
    }

    [Fact]
    public void HealthyRun_ReturnsNull()
    {
        // Recorded healthy frames: stopReason "stop" with usage, no
        // errorMessage — on stdout — and empty stderr.
        const string stdout =
            "{\"type\":\"session\",\"version\":3,\"cwd\":\"/work\"}\n" +
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"model\":\"m\"," +
            "\"usage\":{\"input\":1185,\"output\":104,\"cacheRead\":4352},\"stopReason\":\"stop\"}}\n" +
            "{\"type\":\"agent_end\",\"messages\":[]}\n";

        Assert.Null(PrimeTerminalDiagnoser.TryExtractTerminalError(stdout, ""));
    }

    [Fact]
    public void RecordedBadKeyError_ExtractsErrorMessage()
    {
        // Live shape from the prime-agent 0.9.5 bad-key probe.
        const string stdout =
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"provider\":\"openrouter\"," +
            "\"model\":\"nvidia/nemotron-3.5-lightning:free\"," +
            "\"usage\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":0}," +
            "\"stopReason\":\"error\",\"errorMessage\":\"401 User not found.\\n\\nRun /login to update credentials.\"}}\n";

        var result = PrimeTerminalDiagnoser.TryExtractTerminalError(stdout, null);

        Assert.NotNull(result);
        Assert.Contains("401 User not found", result);
    }

    [Fact]
    public void RecordedQuotaError_ExtractsErrorMessage()
    {
        // Live shape from the $0-limit probe.
        const string stdout =
            "{\"type\":\"message_end\",\"message\":{\"stopReason\":\"error\"," +
            "\"errorMessage\":\"403 Key limit exceeded (total limit). Manage it using https://openrouter.ai/\"}}\n";

        var result = PrimeTerminalDiagnoser.TryExtractTerminalError(stdout, null);

        Assert.NotNull(result);
        Assert.Contains("Key limit exceeded", result);
    }

    [Fact]
    public void RecordedMissingKeyOnStderr_Extracted()
    {
        // Live stderr from the no-key probe: no JSON error event follows.
        const string stderr =
            "No API key found for the selected model.\n" +
            "\n" +
            "Use /login to log into a provider via OAuth or API key. See:\n";

        var result = PrimeTerminalDiagnoser.TryExtractTerminalError(
            "{\"type\":\"session\",\"version\":3}\n", stderr);

        Assert.NotNull(result);
        Assert.Contains("No API key found", result);
    }

    [Fact]
    public void ModelOutputDiscussingErrors_NotExtracted()
    {
        // Reviewed code mentioning errors must not read as a terminal
        // failure: only stopReason=error frames and the No-API-key sentence
        // qualify.
        const string stdout =
            "{\"type\":\"message_end\",\"message\":{\"content\":[{\"type\":\"text\"," +
            "\"text\":\"Fixed the 401 handling in client.py\"}],\"stopReason\":\"stop\"}}\n";

        Assert.Null(PrimeTerminalDiagnoser.TryExtractTerminalError(stdout, ""));
    }

    [Fact]
    public void ErrorWithoutMessage_YieldsFallbackDiagnostic()
    {
        const string stdout =
            "{\"type\":\"message_end\",\"message\":{\"stopReason\":\"error\"}}\n";

        var result = PrimeTerminalDiagnoser.TryExtractTerminalError(stdout, null);

        Assert.NotNull(result);
        Assert.Contains("stopReason=error", result);
    }

    [Fact]
    public void LongErrorMessage_TruncatedToCap()
    {
        var stdout =
            "{\"type\":\"message_end\",\"message\":{\"stopReason\":\"error\"," +
            "\"errorMessage\":\"" + new string('x', 1000) + "\"}}\n";

        var result = PrimeTerminalDiagnoser.TryExtractTerminalError(stdout, null);

        Assert.NotNull(result);
        Assert.True(result!.Length <= PrimeTerminalDiagnoser.MaxDiagnosticChars + 1);
    }
}
