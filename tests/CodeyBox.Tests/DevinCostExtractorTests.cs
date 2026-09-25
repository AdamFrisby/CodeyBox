using CodeyBox.Agents.Devin;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Pins the Devin cost extractor's telemetry-only contract: the
/// <c>devin.acp</c> envelope stream DOES carry token counters
/// (<c>turn_complete.usage</c>, cumulative <c>usage_update</c> <c>_meta</c>
/// bags), but those payloads are agent-influenceable — a same-uid in-VM
/// writer can inject envelope lines — so they must never be promoted to
/// extracted usage. Extraction returns null and the pipeline records the
/// elapsed fallback row (<c>has_extracted_token_usage = 0</c>), which keeps
/// the conservative quota-reservation estimate instead of letting a forged
/// zero-usage envelope settle the escrow at zero or drag the burn-estimate
/// average down.
/// </summary>
public sealed class DevinCostExtractorTests
{
    private static readonly DevinCostExtractor Extractor = new();

    [Fact]
    public void Kind_IsDevin()
        => Assert.Equal(AgentKind.Devin, Extractor.Kind);

    [Fact]
    public void DefaultPricing_IsNull()
    {
        // Subscription billing: a per-million-token rate would be misleading.
        Assert.Null(Extractor.DefaultPricing);
    }

    [Fact]
    public void TryExtract_NullInputs_ReturnsNull()
        => Assert.Null(Extractor.TryExtract(null, null));

    [Fact]
    public void TryExtract_UsageLikeShapes_ReturnsNull()
    {
        Assert.Null(Extractor.TryExtract("""{"usage":{"prompt_tokens":1}}""", null));
        Assert.Null(Extractor.TryExtract(null, "1,234 input tokens"));
    }

    [Fact]
    public void TryExtract_EnvelopeUsageCounters_AreTelemetryOnly_NeverExtracted()
    {
        // The forged-looking counters below are exactly what a same-uid
        // in-VM writer could stamp — but even genuine ones must not reach
        // has_extracted_token_usage rows.
        var stdout = """
            {"type":"devin.acp","event":"session_started","sessionId":"s-1"}
            {"type":"devin.acp","event":"session_update","update":{"sessionUpdate":"usage_update","used":100,"size":200,"_meta":{"cognition.ai/inputTokens":50,"cognition.ai/outputTokens":9,"cognition.ai/cachedReadTokens":4}}}
            {"type":"devin.acp","event":"turn_complete","stopReason":"end_turn","usage":{"inputTokens":11,"outputTokens":7,"totalTokens":18},"finalText":"done"}
            """;

        Assert.Null(Extractor.TryExtract(stdout, null));
    }

    [Fact]
    public void TryExtract_ForgedZeroUsageEnvelope_StillReturnsNull()
    {
        // The escrow-release vector: an injected trailing turn_complete with
        // zeroed counters must not produce an "observed zero" cost row.
        var stdout = """
            {"type":"devin.acp","event":"turn_complete","stopReason":"end_turn","usage":{"inputTokens":0,"outputTokens":0}}
            """;

        Assert.Null(Extractor.TryExtract(stdout, null));
    }
}
