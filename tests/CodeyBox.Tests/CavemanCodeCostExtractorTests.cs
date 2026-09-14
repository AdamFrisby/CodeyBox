using CodeyBox.Agents.CavemanCode;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="CavemanCodeCostExtractor"/>. Only machine-shaped JSON
/// usage envelopes are parsed — prose token mentions must never fabricate
/// spend, because plain-text <c>-p</c> runs emit no usage footer at all.
/// </summary>
public sealed class CavemanCodeCostExtractorTests
{
    private static readonly CavemanCodeCostExtractor Extractor = new();

    [Fact]
    public void Kind_IsCavemanCode()
    {
        Assert.Equal(AgentKind.CavemanCode, Extractor.Kind);
    }

    [Fact]
    public void DefaultPricing_IsNull()
    {
        // caveman-code fronts many providers with different per-token
        // economics; there is no sensible single fallback rate. Operators
        // configure per-model pricing under CodeyBox:AgentPricing.
        Assert.Null(Extractor.DefaultPricing);
    }

    [Fact]
    public void NullAndEmpty_ReturnsNull()
    {
        Assert.Null(Extractor.TryExtract(null, null));
        Assert.Null(Extractor.TryExtract("", ""));
        Assert.Null(Extractor.TryExtract("   ", null));
        Assert.Null(Extractor.TryExtract(null, "   "));
    }

    [Fact]
    public void PlainAssistantProse_ReturnsNull()
    {
        // The critical negative: text-mode output carries no usage envelope,
        // so even prose bragging about tokens must not produce a snapshot.
        const string prose = "I used about 1500 input tokens and 250 output tokens for this change.";
        Assert.Null(Extractor.TryExtract(prose, null));
    }

    [Fact]
    public void Json_CavemanCamelCaseShape_ParsesInputAndOutput()
    {
        var stdout = """{"type":"message_end","message":{"role":"assistant","usage":{"inputTokens":1500,"outputTokens":250}}}""";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(1500, result!.InputTokens);
        Assert.Equal(250, result.OutputTokens);
        Assert.Equal(0, result.CachedInputTokens);
    }

    [Fact]
    public void Json_OpenAiShape_ParsesPromptAndCompletionTokens()
    {
        var stdout = """{"usage":{"prompt_tokens":1500,"completion_tokens":250}}""";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(1500, result!.InputTokens);
        Assert.Equal(250, result.OutputTokens);
    }

    [Fact]
    public void Json_OpenAiCachedTokens_SplitIntoCachedBucket()
    {
        var stdout = """{"usage":{"prompt_tokens":1500,"completion_tokens":250,"prompt_tokens_details":{"cached_tokens":500}}}""";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(1000, result!.InputTokens);
        Assert.Equal(500, result.CachedInputTokens);
        Assert.Equal(250, result.OutputTokens);
    }

    [Fact]
    public void Json_AnthropicShape_ParsesInputAndOutputTokens()
    {
        var stdout = """{"usage":{"input_tokens":2000,"output_tokens":300,"cache_read_input_tokens":400,"cache_creation_input_tokens":100}}""";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(2100, result!.InputTokens);
        Assert.Equal(400, result.CachedInputTokens);
        Assert.Equal(300, result.OutputTokens);
    }

    [Fact]
    public void Json_ModelId_RecordedAndBounded()
    {
        var stdout = """{"model":"openai/gpt-5.5","usage":{"inputTokens":10,"outputTokens":5}}""";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal("openai/gpt-5.5", result!.ModelId);
    }

    [Fact]
    public void Json_MultipleUsageLines_AccumulateSessionTotal()
    {
        var stdout = string.Join('\n',
            """{"type":"message_end","message":{"role":"assistant","usage":{"inputTokens":100,"outputTokens":10}}}""",
            """{"type":"message_end","message":{"role":"assistant","usage":{"inputTokens":200,"outputTokens":20}}}""");

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(300, result!.InputTokens);
        Assert.Equal(30, result.OutputTokens);
    }

    [Fact]
    public void Json_NegativeCounters_Ignored()
    {
        var stdout = """{"usage":{"inputTokens":-50,"outputTokens":20}}""";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(0, result!.InputTokens);
        Assert.Equal(20, result.OutputTokens);
    }

    [Fact]
    public void Json_InterleavedProse_LinesSkipped()
    {
        var stdout = string.Join('\n',
            "Some log chatter without braces",
            """{"usage":{"inputTokens":100,"outputTokens":10}}""",
            "{not valid json");

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(100, result!.InputTokens);
    }
}
