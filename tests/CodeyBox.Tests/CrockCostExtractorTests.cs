using CodeyBox.Agents;
using CodeyBox.Agents.Crock;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="CrockCostExtractor"/>. The crock spend rows are
/// computed from this extractor's output; a regression that swapped cache_read
/// and cache_creation, dropped one of the input buckets, or let a stray
/// bare-usage line clobber the terminal result envelope would silently corrupt
/// every crock cost row — so every code path lives behind a pinned test here.
/// </summary>
public sealed class CrockCostExtractorTests
{
    private static readonly CrockCostExtractor Extractor = new();

    [Fact]
    public void Kind_IsCrock()
    {
        Assert.Equal(AgentKind.Crock, Extractor.Kind);
    }

    // ── NDJSON: typed `result` envelope ──────────────────────────────────────

    [Fact]
    public void NdJson_TypedResult_ReturnsTotalsAndModelId()
    {
        // Anthropic Message Batches API mirrors /v1/messages — the same usage
        // envelope shape as ClaudeCostExtractor consumes. The terminal
        // `type:"result"` envelope carries the billable totals.
        const string fixture = """
            {"type":"assistant","message":{"id":"m1","model":"claude-opus-4-7","content":[],"usage":{"input_tokens":10,"output_tokens":5,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}}
            {"type":"result","subtype":"success","model":"claude-opus-4-7","usage":{"input_tokens":12345,"output_tokens":678,"cache_read_input_tokens":5000,"cache_creation_input_tokens":0}}
            """;

        var result = Extractor.TryExtract(fixture, null);

        Assert.NotNull(result);
        Assert.Equal(12345, result.InputTokens);
        Assert.Equal(678, result.OutputTokens);
        Assert.Equal(5000, result.CachedInputTokens);
        Assert.Equal("claude-opus-4-7", result.ModelId);
    }

    [Fact]
    public void NdJson_IncludesCacheCreationInInputTokens()
    {
        // Regression-pin: cache_creation_input_tokens must be ADDED to
        // input_tokens (the cache-write bucket is billed at the elevated
        // cache-creation rate, separately from cache_read). Mirrors
        // ClaudeCostExtractor's contract.
        const string fixture = """
            {"type":"result","subtype":"success","model":"claude-opus-4-7","usage":{"input_tokens":43,"output_tokens":120,"cache_read_input_tokens":900000,"cache_creation_input_tokens":10000}}
            """;

        var result = Extractor.TryExtract(fixture, null);

        Assert.NotNull(result);
        Assert.Equal(43 + 10000, result.InputTokens);
        Assert.Equal(900000, result.CachedInputTokens);
        Assert.Equal(120, result.OutputTokens);
    }

    [Fact]
    public void NdJson_BareUsageObject_IsAccepted()
    {
        // Preview CrockCode builds have surfaced the usage envelope without a
        // wrapper `type` field — the extractor must accept either shape so a
        // CLI re-shape across CrockCode releases does not silently zero out
        // the cost row.
        const string fixture = """
            {"usage":{"input_tokens":100,"output_tokens":50,"cache_read_input_tokens":200,"cache_creation_input_tokens":0}}
            """;

        var result = Extractor.TryExtract(fixture, null);

        Assert.NotNull(result);
        Assert.Equal(100, result.InputTokens);
        Assert.Equal(50, result.OutputTokens);
        Assert.Equal(200, result.CachedInputTokens);
    }

    [Fact]
    public void NdJson_ResultEnvelope_BeatsLaterBareUsage()
    {
        // Regression-pin: if a bare `usage` line follows the terminal
        // `type:"result"` envelope, the partial counts must NOT clobber the
        // billable totals (ClaudeCostExtractor prevents this by only accepting
        // type=="result"; CrockCostExtractor uses the same precedence ordering).
        const string fixture = """
            {"type":"result","subtype":"success","usage":{"input_tokens":12000,"output_tokens":600,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}
            {"usage":{"input_tokens":1,"output_tokens":0,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}
            """;

        var result = Extractor.TryExtract(fixture, null);

        Assert.NotNull(result);
        Assert.Equal(12000, result.InputTokens);
        Assert.Equal(600, result.OutputTokens);
    }

    [Fact]
    public void NdJson_NoUsageField_ReturnsNull()
    {
        const string fixture = """
            {"type":"system","subtype":"init"}
            {"type":"result","subtype":"success"}
            """;

        Assert.Null(Extractor.TryExtract(fixture, null));
    }

    [Fact]
    public void NdJson_ModelIdFromNestedMessage_IsCaptured()
    {
        // When a CrockCode build only surfaces the model id on the nested
        // assistant `message.model` field (no top-level `model`), the extractor
        // must still capture it.
        const string fixture = """
            {"type":"assistant","message":{"model":"claude-sonnet-4-6","content":[]}}
            {"type":"result","subtype":"success","usage":{"input_tokens":1,"output_tokens":1,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}
            """;

        var result = Extractor.TryExtract(fixture, null);

        Assert.NotNull(result);
        Assert.Equal("claude-sonnet-4-6", result.ModelId);
    }

    [Fact]
    public void NdJson_ModelIdLongerThan128Chars_IsTruncated()
    {
        var longId = new string('a', 200);
        var fixture =
            "{\"type\":\"result\",\"subtype\":\"success\",\"model\":\"" + longId + "\"," +
            "\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}}";

        var result = Extractor.TryExtract(fixture, null);

        Assert.NotNull(result);
        Assert.NotNull(result.ModelId);
        Assert.Equal(128, result.ModelId!.Length);
    }

    [Fact]
    public void NdJson_MalformedLineThenValidResult_StillExtracts()
    {
        // A garbled line must not poison the whole stream — the loop catches
        // JsonException per-line and keeps walking.
        const string fixture = """
            {broken-json-here
            {"type":"result","subtype":"success","usage":{"input_tokens":7,"output_tokens":3,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}
            """;

        var result = Extractor.TryExtract(fixture, null);

        Assert.NotNull(result);
        Assert.Equal(7, result.InputTokens);
        Assert.Equal(3, result.OutputTokens);
    }

    // ── Human-readable fallback ─────────────────────────────────────────────

    [Fact]
    public void HumanReadable_CompactFooter_ParsesAndDeductsCachedFromInput()
    {
        // Mirrors ClaudeCostExtractor's contract: the human-readable total
        // includes cached tokens, so InputTokens (fresh non-cached) is
        // total - cached.
        const string stdout = "12,345 input tokens, 678 output tokens, 5,000 cached tokens";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(7345, result.InputTokens);
        Assert.Equal(678, result.OutputTokens);
        Assert.Equal(5000, result.CachedInputTokens);
    }

    [Fact]
    public void HumanReadable_LabelledTotals_Parse()
    {
        const string stdout = """
            Total input tokens: 12,345
            Cache input tokens: 5,000
            Total output tokens: 678
            """;

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(7345, result.InputTokens);
        Assert.Equal(678, result.OutputTokens);
        Assert.Equal(5000, result.CachedInputTokens);
    }

    [Fact]
    public void StderrFallback_WhenStdoutHasNoUsage()
    {
        // If the CLI ever routes the cost footer to stderr (observed in some
        // CrockCode preview builds when the result envelope is interleaved
        // with diagnostic output on stdout), the extractor must fall back.
        const string stderr = "100 input tokens, 50 output tokens";

        var result = Extractor.TryExtract(null, stderr);

        Assert.NotNull(result);
        Assert.Equal(100, result.InputTokens);
        Assert.Equal(50, result.OutputTokens);
    }

    // ── Null / empty ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    public void NullOrEmpty_ReturnsNull(string? stdout, string? stderr)
    {
        Assert.Null(Extractor.TryExtract(stdout, stderr));
    }

    // ── DefaultPricing surface ──────────────────────────────────────────────

    [Fact]
    public void DefaultPricing_IsNull_RatesLiveInConfigOnly()
    {
        // The extractor holds NO compiled fallback rate: all crock rates,
        // including the unknown-model default, live only in
        // agent-pricing-defaults.json (the `crock` bucket plus DefaultRates.crock)
        // so the hot-reloadable pricing config is the single source of truth and
        // cannot drift from a stale in-source literal.
        Assert.Null(Extractor.DefaultPricing);
    }
}
