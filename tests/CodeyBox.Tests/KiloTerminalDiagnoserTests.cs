using CodeyBox.Agents.Kilo;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="KiloTerminalDiagnoser"/> over recorded real
/// @kilocode/cli 7.7.2 output: the missing-key 401 frame, the generic +
/// specific model-resolution pair (the specific cause must win), healthy
/// runs, and the output cap.
/// </summary>
public sealed class KiloTerminalDiagnoserTests
{
    private const string AuthErrorLine =
        "{\"type\":\"error\",\"timestamp\":1789585744519,\"sessionID\":\"ses_def\"," +
        "\"error\":{\"name\":\"APIError\",\"data\":{\"message\":\"No cookie auth credentials found\"," +
        "\"statusCode\":401,\"isRetryable\":false}}}";

    // Recorded real pair (unseeded models map, exit 1): a content-free
    // generic frame followed by the specific cause.
    private const string GenericThenSpecific =
        "{\"type\":\"error\",\"timestamp\":1789585925135,\"sessionID\":\"ses_xyz\"," +
        "\"error\":{\"name\":\"UnknownError\",\"data\":{\"message\":\"Unexpected server error. Check server logs for details.\",\"ref\":\"err_5f4a3950\"}}}\n" +
        "{\"type\":\"error\",\"timestamp\":1789585925140,\"sessionID\":\"ses_xyz\"," +
        "\"error\":{\"name\":\"UnknownError\",\"data\":{\"message\":\"Model not found: openai-compatible/nvidia/nemotron-3.5-lightning:free.\"}}}";

    [Fact]
    public void TryExtractTerminalError_AuthFailure_ReturnsProviderMessage()
    {
        Assert.Equal(
            "No cookie auth credentials found",
            KiloTerminalDiagnoser.TryExtractTerminalError(AuthErrorLine));
    }

    [Fact]
    public void TryExtractTerminalError_GenericPlusSpecific_ReturnsSpecificCause()
    {
        // The generic frame names no cause (and the sandbox has no server
        // logs to check); the specific companion frame must win.
        Assert.Equal(
            "Model not found: openai-compatible/nvidia/nemotron-3.5-lightning:free.",
            KiloTerminalDiagnoser.TryExtractTerminalError(GenericThenSpecific));
    }

    [Fact]
    public void TryExtractTerminalError_OnlyGeneric_ReturnsGeneric()
    {
        // A lone generic frame is still the only terminal signal — surface
        // it rather than nothing.
        const string lone =
            "{\"type\":\"error\",\"error\":{\"name\":\"UnknownError\",\"data\":{\"message\":\"Unexpected server error. Check server logs for details.\"}}}";

        Assert.Equal(
            "Unexpected server error. Check server logs for details.",
            KiloTerminalDiagnoser.TryExtractTerminalError(lone));
    }

    [Fact]
    public void TryExtractTerminalError_HealthyRun_ReturnsNull()
    {
        const string healthy =
            "{\"type\":\"step_start\",\"sessionID\":\"ses_abc\",\"part\":{\"sessionID\":\"ses_abc\",\"messageID\":\"m1\",\"type\":\"step-start\"}}\n" +
            "{\"type\":\"text\",\"sessionID\":\"ses_abc\",\"part\":{\"sessionID\":\"ses_abc\",\"messageID\":\"m1\",\"type\":\"text\",\"text\":\"KILO_JSON_OK\"}}\n" +
            "{\"type\":\"step_finish\",\"sessionID\":\"ses_abc\",\"part\":{\"sessionID\":\"ses_abc\",\"messageID\":\"m1\",\"type\":\"step-finish\",\"reason\":\"stop\"}}";

        Assert.Null(KiloTerminalDiagnoser.TryExtractTerminalError(healthy));
    }

    [Fact]
    public void TryExtractTerminalError_EmptyOrMalformed_ReturnsNull()
    {
        Assert.Null(KiloTerminalDiagnoser.TryExtractTerminalError(null));
        Assert.Null(KiloTerminalDiagnoser.TryExtractTerminalError("  "));
        Assert.Null(KiloTerminalDiagnoser.TryExtractTerminalError("not json\n{\"type\":\"step_finish\"}"));
    }

    [Fact]
    public void TryExtractTerminalError_LongMessage_IsCapped()
    {
        var longMessage = new string('x', KiloTerminalDiagnoser.MaxDiagnosticChars + 100);
        var line = "{\"type\":\"error\",\"error\":{\"data\":{\"message\":\"" + longMessage + "\"}}}";

        var extracted = KiloTerminalDiagnoser.TryExtractTerminalError(line);

        Assert.NotNull(extracted);
        Assert.True(extracted.Length <= KiloTerminalDiagnoser.MaxDiagnosticChars + 1);
        Assert.StartsWith(new string('x', 10), extracted, StringComparison.Ordinal);
    }
}
