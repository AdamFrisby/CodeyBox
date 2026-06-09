using CodeyBox.Agents;
using CodeyBox.Agents.Codex;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class CodexCostExtractorTests
{
    private static readonly CodexCostExtractor Extractor = new();

    [Fact]
    public void Json_ParsesPromptAndCompletionTokens()
    {
        var stdout = """{"usage":{"prompt_tokens":12345,"completion_tokens":678},"model":"codex-5.5"}""";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(12345, result.InputTokens);
        Assert.Equal(0, result.CachedInputTokens);
        Assert.Equal(678, result.OutputTokens);
        Assert.Equal("codex-5.5", result.ModelId);
    }

    [Fact]
    public void Json_OpenAiPromptTokenDetails_RecordsCachedAndFreshInput()
    {
        var stdout = """{"usage":{"prompt_tokens":82750,"completion_tokens":290,"prompt_tokens_details":{"cached_tokens":82000}},"model":"gpt-5"}""";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(750, result.InputTokens);
        Assert.Equal(82000, result.CachedInputTokens);
        Assert.Equal(290, result.OutputTokens);
        Assert.Equal("gpt-5", result.ModelId);
    }

    [Fact]
    public void Json_CodexExecJsonTurnCompleted_RecordsCachedAndFreshInput()
    {
        // Verified against local `codex exec --json --ephemeral` output:
        // turn.completed carries usage.input_tokens and usage.cached_input_tokens.
        var stdout = """{"type":"turn.completed","usage":{"input_tokens":10546,"cached_input_tokens":2432,"output_tokens":5,"reasoning_output_tokens":0}}""";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(8114, result.InputTokens);
        Assert.Equal(2432, result.CachedInputTokens);
        Assert.Equal(5, result.OutputTokens);
    }

    [Fact]
    public void Json_WrappedCodexPayloadUsage_RecordsCachedAndFreshInput()
    {
        var stdout = """{"type":"event_msg","payload":{"type":"turn_complete","usage":{"input_tokens":10546,"cached_input_tokens":2432,"output_tokens":5}}}""";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(8114, result.InputTokens);
        Assert.Equal(2432, result.CachedInputTokens);
        Assert.Equal(5, result.OutputTokens);
    }

    [Theory]
    [InlineData("""{"usage":{"input_tokens":1000,"output_tokens":7,"input_tokens_details":{"cached_tokens":400}}}""")]
    [InlineData("""{"usage":{"input_tokens":1000,"output_tokens":7,"cached_tokens":400}}""")]
    public void Json_CachedTokenAliases_RecordCachedTokens(string stdout)
    {
        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(600, result.InputTokens);
        Assert.Equal(400, result.CachedInputTokens);
        Assert.Equal(7, result.OutputTokens);
    }

    [Theory]
    [InlineData("""{"token_usage":{"input_tokens":1000,"cached_input_tokens":400,"output_tokens":7}}""")]
    [InlineData("""{"total_token_usage":{"input_tokens":1000,"cached_input_tokens":400,"output_tokens":7}}""")]
    [InlineData("""{"last_token_usage":{"input_tokens":1000,"cached_input_tokens":400,"output_tokens":7}}""")]
    [InlineData("""{"item":{"usage":{"input_tokens":1000,"cached_input_tokens":400,"output_tokens":7}}}""")]
    [InlineData("""{"info":{"usage":{"input_tokens":1000,"cached_input_tokens":400,"output_tokens":7}}}""")]
    public void Json_UsageWrapperAliases_RecordUsage(string stdout)
    {
        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(600, result.InputTokens);
        Assert.Equal(400, result.CachedInputTokens);
        Assert.Equal(7, result.OutputTokens);
    }

    [Fact]
    public void Json_NdJsonStream_KeepsFinalUsageSnapshot()
    {
        var stdout = """
            {"type":"token_count","total_token_usage":{"input_tokens":100,"cached_input_tokens":10,"output_tokens":1}}
            {"type":"turn.completed","usage":{"input_tokens":1000,"cached_input_tokens":400,"output_tokens":7}}
            """;

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(600, result.InputTokens);
        Assert.Equal(400, result.CachedInputTokens);
        Assert.Equal(7, result.OutputTokens);
    }

    [Fact]
    public void Json_StringifiedUsagePayload_RecordsCachedTokens()
    {
        var stdout = """{"token_usage_json":"{\"input_tokens\":1000,\"cached_input_tokens\":400,\"output_tokens\":7}"}""";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(600, result.InputTokens);
        Assert.Equal(400, result.CachedInputTokens);
        Assert.Equal(7, result.OutputTokens);
    }

    [Fact]
    public void Json_ArrayRootUsage_RecordsCachedTokens()
    {
        var stdout = """[{"usage":{"input_tokens":1000,"cached_input_tokens":400,"output_tokens":7}}]""";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(600, result.InputTokens);
        Assert.Equal(400, result.CachedInputTokens);
        Assert.Equal(7, result.OutputTokens);
    }

    [Fact]
    public void Json_MalformedUsageDetailObjects_TreatsDetailsAsAbsent()
    {
        var stdout = """{"usage":{"prompt_tokens":1,"completion_tokens":1,"prompt_tokens_details":0,"input_tokens_details":null}}""";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(1, result.InputTokens);
        Assert.Equal(0, result.CachedInputTokens);
        Assert.Equal(1, result.OutputTokens);
    }

    [Fact]
    public void Json_NegativeCachedTokens_IgnoresCachedValue()
    {
        var stdout = """{"usage":{"prompt_tokens":12345,"completion_tokens":678,"prompt_tokens_details":{"cached_tokens":-5000}}}""";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(12345, result.InputTokens);
        Assert.Equal(0, result.CachedInputTokens);
        Assert.Equal(678, result.OutputTokens);
    }

    [Fact]
    public void Json_CachedTokensEqualInputTotal_RecordsCachedOnlyInput()
    {
        var stdout = """{"usage":{"prompt_tokens":1000,"completion_tokens":0,"prompt_tokens_details":{"cached_tokens":1000}}}""";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(0, result.InputTokens);
        Assert.Equal(1000, result.CachedInputTokens);
        Assert.Equal(0, result.OutputTokens);
    }

    [Fact]
    public void HumanReadable_ParsesPromptAndCompletionLines()
    {
        var stdout = "Prompt tokens: 12,345 / Completion tokens: 678";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(12345, result.InputTokens);
        Assert.Equal(678, result.OutputTokens);
    }

    [Fact]
    public void HumanReadable_ParsesCachedTokensAndStoresFreshInput()
    {
        var stdout = "Prompt tokens: 12,345 / Cached input tokens: 2,000 / Completion tokens: 678";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(10345, result.InputTokens);
        Assert.Equal(2000, result.CachedInputTokens);
        Assert.Equal(678, result.OutputTokens);
    }

    [Fact]
    public void HumanReadable_CompactInputOutputBranch_ParsesCachedTokens()
    {
        var stdout = "12,345 input tokens, 678 output tokens, 2,000 cached tokens";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(10345, result.InputTokens);
        Assert.Equal(2000, result.CachedInputTokens);
        Assert.Equal(678, result.OutputTokens);
    }

    [Fact]
    public void NullAndEmpty_ReturnsNull()
    {
        Assert.Null(Extractor.TryExtract(null, null));
        Assert.Null(Extractor.TryExtract("", ""));
    }

    [Fact]
    public void MalformedJson_ReturnsNull()
    {
        var stdout = "{not valid json at all";

        var result = Extractor.TryExtract(stdout, null);

        Assert.Null(result);
    }
}
