using CodeyBox.Agents.Devin;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Pins the Devin cost extractor's <c>devin.acp</c> envelope contract:
/// ACP-mode dispatch output carries the shim's envelopes, whose
/// <c>turn_complete</c> usage object and <c>usage_update</c> _meta counters
/// hold the turn's token totals. Print-mode and foreign streams carry no
/// machine-readable usage, so they still extract null. Pricing stays null
/// either way — consumption bills as ACUs on the operator's subscription,
/// and the plan windows are surfaced by <see cref="DevinQuotaProbe"/>.
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
    public void TryExtract_EnvelopesWithoutUsage_ReturnsNull()
    {
        var stdout = """
            {"type":"devin.acp","event":"session_started","sessionId":"s-1"}
            {"type":"devin.acp","event":"session_update","update":{"sessionUpdate":"tool_call","toolCallId":"t-1"}}
            {"type":"devin.acp","event":"turn_complete","stopReason":"end_turn"}
            """;

        Assert.Null(Extractor.TryExtract(stdout, null));
    }

    [Fact]
    public void TryExtract_TurnCompleteUsage_ExtractsTotals()
    {
        var stdout = """
            {"type":"devin.acp","event":"session_started","sessionId":"s-1"}
            {"type":"devin.acp","event":"turn_complete","stopReason":"end_turn","usage":{"inputTokens":11,"outputTokens":7,"totalTokens":18}}
            """;

        var snapshot = Extractor.TryExtract(stdout, null);

        Assert.NotNull(snapshot);
        Assert.Equal(11, snapshot.InputTokens);
        Assert.Equal(7, snapshot.OutputTokens);
        Assert.Equal(0, snapshot.CachedInputTokens);
        Assert.Null(snapshot.ModelId);
    }

    [Fact]
    public void TryExtract_TurnCompleteSnakeCaseUsage_ExtractsTotals()
    {
        // The stream parser accepts snake_case usage keys; the cost row must
        // read the same spellings or a snake_case payload would feed the
        // summary but record a zero-token row.
        var stdout = """
            {"type":"devin.acp","event":"turn_complete","stopReason":"end_turn","usage":{"input_tokens":5,"output_tokens":3,"cached_input_tokens":2}}
            """;

        var snapshot = Extractor.TryExtract(stdout, null);

        Assert.NotNull(snapshot);
        Assert.Equal(5, snapshot.InputTokens);
        Assert.Equal(3, snapshot.OutputTokens);
        Assert.Equal(2, snapshot.CachedInputTokens);
    }

    [Fact]
    public void TryExtract_UsageUpdateMeta_ExtractsCachedTokens()
    {
        var stdout = """
            {"type":"devin.acp","event":"session_update","update":{"sessionUpdate":"usage_update","used":100,"size":200,"_meta":{"cognition.ai/inputTokens":50,"cognition.ai/outputTokens":9,"cognition.ai/cachedReadTokens":4}}}
            {"type":"devin.acp","event":"turn_complete","stopReason":"end_turn"}
            """;

        var snapshot = Extractor.TryExtract(stdout, null);

        Assert.NotNull(snapshot);
        Assert.Equal(50, snapshot.InputTokens);
        Assert.Equal(9, snapshot.OutputTokens);
        Assert.Equal(4, snapshot.CachedInputTokens);
    }

    [Fact]
    public void TryExtract_TurnCompleteUsage_WinsOverUsageUpdateTicks()
    {
        // The terminal envelope's totals are authoritative; intermediate
        // usage_update _meta counters only fill keys the turn result lacks.
        var stdout = """
            {"type":"devin.acp","event":"session_update","update":{"sessionUpdate":"usage_update","_meta":{"cognition.ai/inputTokens":50,"cognition.ai/outputTokens":9,"cognition.ai/cachedReadTokens":4}}}
            {"type":"devin.acp","event":"turn_complete","stopReason":"end_turn","usage":{"inputTokens":11,"outputTokens":7}}
            """;

        var snapshot = Extractor.TryExtract(stdout, null);

        Assert.NotNull(snapshot);
        Assert.Equal(11, snapshot.InputTokens);
        Assert.Equal(7, snapshot.OutputTokens);
        Assert.Equal(4, snapshot.CachedInputTokens);
    }
}
