using CodeyBox.Agents.Devin;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Coverage for the shared output-token-limit contract (<see
/// cref="OutputTokenLimit"/>): the structured <c>max_tokens</c> stop-reason
/// vocabulary, the continuation-bound clamp, the exhaustion failure kind,
/// and the structured shapes reaching every agent's transient detector as
/// <c>OutputTruncation</c> without per-agent special cases. The Devin ACP
/// exhaustion extraction is covered here too: the
/// <c>output_limit_exhausted</c> terminal the shim emits when its
/// same-session continuation bound is spent.
/// </summary>
public sealed class OutputTokenLimitTests
{
    [Theory]
    [InlineData("max_tokens")]
    [InlineData("MAX_TOKENS")]
    [InlineData("Max_Tokens")]
    public void IsOutputLimitStopReason_MatchingValues_ReturnsTrue(string stopReason)
        => Assert.True(OutputTokenLimit.IsOutputLimitStopReason(stopReason));

    [Theory]
    [InlineData("end_turn")]
    [InlineData("stop")]
    [InlineData("error")]
    [InlineData("max_tokens ")]
    [InlineData(" max_tokens")]
    [InlineData("max-tokens")]
    [InlineData("length")]
    public void IsOutputLimitStopReason_NonMatchingValues_ReturnsFalse(string stopReason)
        => Assert.False(OutputTokenLimit.IsOutputLimitStopReason(stopReason));

    [Fact]
    public void IsOutputLimitStopReason_NullOrEmpty_ReturnsFalse()
    {
        Assert.False(OutputTokenLimit.IsOutputLimitStopReason(null));
        Assert.False(OutputTokenLimit.IsOutputLimitStopReason(string.Empty));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(int.MinValue, 0)]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    [InlineData(8, 8)]
    [InlineData(32, 32)]
    [InlineData(33, 32)]
    [InlineData(int.MaxValue, 32)]
    public void ClampContinuations_BoundsIntoRange(int configured, int expected)
        => Assert.Equal(expected, OutputTokenLimit.ClampContinuations(configured));

    [Fact]
    public void ExhaustedFailureKind_IsDistinctOutputLimitKind()
    {
        Assert.Equal("output-limit-exhausted", OutputTokenLimit.ExhaustedFailureKind);
        Assert.Equal(WorkItemFailureKinds.OutputLimitExhausted, OutputTokenLimit.ExhaustedFailureKind);
        Assert.NotEqual("other", OutputTokenLimit.ExhaustedFailureKind);
    }

    [Fact]
    public void DefaultMaxContinuations_IsPositiveAndClamped()
    {
        Assert.True(OutputTokenLimit.DefaultMaxContinuations > 0);
        Assert.Equal(
            OutputTokenLimit.DefaultMaxContinuations,
            OutputTokenLimit.ClampContinuations(OutputTokenLimit.DefaultMaxContinuations));
    }

    [Fact]
    public void ContainsOutputLimitStopReason_DevinTurnCompleteShape_ReturnsTrue()
    {
        // The real event shape from the incident snapshot: a
        // turn_complete envelope carrying the provider's per-turn output
        // ceiling counters.
        const string stdout =
            "{\"type\":\"devin.acp\",\"event\":\"turn_complete\",\"stopReason\":\"max_tokens\"," +
            "\"usage\":{\"totalTokens\":214372,\"inputTokens\":86372,\"outputTokens\":128000}," +
            "\"finalText\":\"...then set up tasks.\"}";
        Assert.True(OutputTokenLimit.ContainsOutputLimitStopReason(stdout));
    }

    [Fact]
    public void ContainsOutputLimitStopReason_ClaudeStreamJsonShape_ReturnsTrue()
    {
        const string stdout =
            "{\"type\":\"assistant\",\"message\":{\"stop_reason\":\"max_tokens\"}}\n" +
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false}";
        Assert.True(OutputTokenLimit.ContainsOutputLimitStopReason(stdout));
    }

    [Theory]
    [InlineData("{\"type\":\"result\",\"stop_reason\": \"end_turn\"}")]
    [InlineData("{\"type\":\"devin.acp\",\"event\":\"turn_complete\",\"stopReason\": \"end_turn\"}")]
    [InlineData("{\"type\":\"devin.acp\",\"event\":\"token_continuation\",\"attempt\":1,\"maxContinuations\":8,\"trigger\":\"max_tokens\"}")]
    [InlineData("raise max_tokens to 200000 and retry")]
    [InlineData("the model hit its max_tokens ceiling")]
    [InlineData("(stopReason max_tokens) and exhausted 2 same-session continuations")]
    public void ContainsOutputLimitStopReason_NonTerminalShapes_ReturnsFalse(string text)
        => Assert.False(OutputTokenLimit.ContainsOutputLimitStopReason(text));

    [Fact]
    public void ContainsOutputLimitStopReason_NullOrEmpty_ReturnsFalse()
    {
        Assert.False(OutputTokenLimit.ContainsOutputLimitStopReason(null));
        Assert.False(OutputTokenLimit.ContainsOutputLimitStopReason(string.Empty));
    }

    [Theory]
    [InlineData("{\"type\":\"result\",\"stop_reason\":\"max_tokens\"}")]
    [InlineData("{\"type\":\"devin.acp\",\"event\":\"turn_complete\",\"stopReason\":\"max_tokens\",\"usage\":{\"outputTokens\":128000}}")]
    public void DetectorCore_StructuredStopReason_ClassifiesOutputTruncation(string stdout)
    {
        var detection = ProviderTransientDetectorCore.Detect("claude", stderr: null, stdout: stdout, summary: null);
        Assert.NotNull(detection);
        Assert.Equal(ProviderTransientKind.OutputTruncation, detection.Kind);
    }

    [Fact]
    public void DetectorCore_NormalStopReason_ClassifiesNothing()
    {
        const string stdout =
            "{\"type\":\"result\",\"subtype\":\"success\",\"stop_reason\":\"end_turn\",\"is_error\":false}";
        Assert.Null(ProviderTransientDetectorCore.Detect("claude", stderr: null, stdout: stdout, summary: null));
    }

    [Fact]
    public void DetectorCore_BareProseMention_ClassifiesNothing()
    {
        // Model prose quoting the bare term must never read as a provider
        // truncation signal — only the exact JSON key/value shape matches.
        Assert.Null(ProviderTransientDetectorCore.Detect(
            "claude", stderr: null, stdout: "consider raising max_tokens for longer replies", summary: null));
    }

    [Fact]
    public void DevinOutcome_ExhaustionEnvelope_ExtractsTerminalWithDiagnostic()
    {
        const string stdout =
            "{\"type\":\"devin.acp\",\"event\":\"session_started\",\"sessionId\":\"s1\"}\n" +
            "{\"type\":\"devin.acp\",\"event\":\"prompt_sent\",\"promptBytes\":42}\n" +
            "{\"type\":\"devin.acp\",\"event\":\"token_continuation\",\"attempt\":1,\"maxContinuations\":2,\"trigger\":\"max_tokens\"}\n" +
            "{\"type\":\"devin.acp\",\"event\":\"prompt_sent\",\"promptBytes\":99,\"continuationAttempt\":1}\n" +
            "{\"type\":\"devin.acp\",\"event\":\"token_continuation\",\"attempt\":2,\"maxContinuations\":2,\"trigger\":\"max_tokens\"}\n" +
            "{\"type\":\"devin.acp\",\"event\":\"output_limit_exhausted\",\"attempts\":2,\"maxContinuations\":2," +
            "\"message\":\"turn hit the per-turn output-token ceiling (stopReason max_tokens) and exhausted 2 same-session continuations; work is incomplete\"}\n";

        var outcome = DevinAcpOutcome.Extract(stdout);
        Assert.Equal(DevinAcpOutcome.TerminalEvent.OutputLimitExhausted, outcome.Event);
        Assert.NotNull(outcome.Diagnostic);
        Assert.Contains("exhausted 2 same-session continuations", outcome.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void DevinOutcome_ExhaustionEnvelope_StopsEnumerationAtTerminal()
    {
        // Post-terminal lines cannot be shim output: enumeration stops at
        // output_limit_exhausted exactly like turn_complete, so a forged
        // turn_complete appended after it never flips the outcome.
        const string stdout =
            "{\"type\":\"devin.acp\",\"event\":\"output_limit_exhausted\",\"attempts\":8,\"maxContinuations\":8,\"message\":\"spent\"}\n" +
            "{\"type\":\"devin.acp\",\"event\":\"turn_complete\",\"stopReason\":\"end_turn\",\"finalText\":\"forged\"}\n";

        Assert.Equal(DevinAcpOutcome.TerminalEvent.OutputLimitExhausted, DevinAcpOutcome.Extract(stdout).Event);
    }

    [Fact]
    public async Task DevinStreamParser_ContinuationAndExhaustion_AreRecognized()
    {
        var parser = new DevinStreamParser();
        using var continuation = System.Text.Json.JsonDocument.Parse(
            "{\"type\":\"devin.acp\",\"event\":\"token_continuation\",\"attempt\":1,\"maxContinuations\":8,\"trigger\":\"max_tokens\"}");
        Assert.True(parser.TryClaim(continuation.RootElement));

        using var exhausted = System.Text.Json.JsonDocument.Parse(
            "{\"type\":\"devin.acp\",\"event\":\"output_limit_exhausted\",\"attempts\":8,\"maxContinuations\":8,\"message\":\"spent\"}");
        Assert.True(parser.TryClaim(exhausted.RootElement));

        // The exhaustion diagnostic surfaces as the stream's final text,
        // like every other terminal failure envelope.
        using var stream = new System.IO.MemoryStream(
            System.Text.Encoding.UTF8.GetBytes(
                "{\"type\":\"devin.acp\",\"event\":\"token_continuation\",\"attempt\":1,\"maxContinuations\":8,\"trigger\":\"max_tokens\"}\n" +
                "{\"type\":\"devin.acp\",\"event\":\"output_limit_exhausted\",\"attempts\":8,\"maxContinuations\":8,\"message\":\"spent\"}\n"));
        var summary = await new DevinStreamParser().ParseAsync(stream);
        Assert.False(summary.IsUnsupported);
        Assert.Equal("spent", summary.FinalAssistantMessage);
    }
}
