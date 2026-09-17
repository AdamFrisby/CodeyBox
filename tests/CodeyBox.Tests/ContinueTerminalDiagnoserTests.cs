using CodeyBox.Agents.Continue;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="ContinueTerminalDiagnoser"/> over recorded real
/// @continuedev/cli 1.5.47 output: the $0-spend-limit paid-model refusal,
/// the onboarding-gate interceptor failure (same envelope, different
/// message), healthy runs (plain text and empty), and the output cap.
/// </summary>
public sealed class ContinueTerminalDiagnoserTests
{
    // Recorded real shape: $0-spend-limit key against a paid model (exit 0,
    // envelope on stdout). The workspace key id is redacted — secret-derived.
    private const string SpendLimitEnvelope =
        "{\"status\":\"error\",\"message\":\"403 Key limit exceeded (total limit). " +
        "Manage it using https://openrouter.ai/workspaces/default/keys/REDACTED\"}";

    // The known first-run onboarding-gate failure shares the envelope shape.
    private const string OnboardingGateEnvelope =
        "{\"status\":\"error\",\"message\":\"The request failed and the interceptors did not return an alternative response\"}";

    [Fact]
    public void TryExtractTerminalError_SpendLimitEnvelope_ReturnsProviderMessage()
    {
        Assert.Equal(
            "403 Key limit exceeded (total limit). Manage it using https://openrouter.ai/workspaces/default/keys/REDACTED",
            ContinueTerminalDiagnoser.TryExtractTerminalError(SpendLimitEnvelope));
    }

    [Fact]
    public void TryExtractTerminalError_OnboardingGateEnvelope_ReturnsMessage()
    {
        Assert.Equal(
            "The request failed and the interceptors did not return an alternative response",
            ContinueTerminalDiagnoser.TryExtractTerminalError(OnboardingGateEnvelope));
    }

    [Fact]
    public void TryExtractTerminalError_LastEnvelopeWins()
    {
        var combined = SpendLimitEnvelope + "\n" + OnboardingGateEnvelope;

        Assert.Equal(
            "The request failed and the interceptors did not return an alternative response",
            ContinueTerminalDiagnoser.TryExtractTerminalError(combined));
    }

    [Fact]
    public void TryExtractTerminalError_HealthyReply_ReturnsNull()
    {
        Assert.Null(ContinueTerminalDiagnoser.TryExtractTerminalError("HELLO-CN-OK\n"));
    }

    [Fact]
    public void TryExtractTerminalError_EmptyOutput_ReturnsNull()
    {
        Assert.Null(ContinueTerminalDiagnoser.TryExtractTerminalError(string.Empty));
        Assert.Null(ContinueTerminalDiagnoser.TryExtractTerminalError(null));
    }

    [Fact]
    public void TryExtractTerminalError_NonEnvelopeJson_ReturnsNull()
    {
        // A --format json success reply ({"message": "…"}) is model output,
        // not a terminal error — status must equal "error".
        Assert.Null(ContinueTerminalDiagnoser.TryExtractTerminalError("{\"message\": \"HELLO-CN-JSON\"}\n"));
        // status:error without a message names no cause — surface nothing.
        Assert.Null(ContinueTerminalDiagnoser.TryExtractTerminalError("{\"status\":\"error\"}\n"));
    }

    [Fact]
    public void TryExtractTerminalError_LongMessage_IsCapped()
    {
        var longMessage = new string('x', ContinueTerminalDiagnoser.MaxDiagnosticChars + 100);
        var extracted = ContinueTerminalDiagnoser.TryExtractTerminalError(
            "{\"status\":\"error\",\"message\":\"" + longMessage + "\"}");

        Assert.NotNull(extracted);
        Assert.True(extracted.Length <= ContinueTerminalDiagnoser.MaxDiagnosticChars + 1);
    }
}
