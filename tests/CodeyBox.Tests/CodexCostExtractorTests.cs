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
    public void Json_OpenAiPromptTokenDetails_RecordsCachedAndFreshRemainder()
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
    public void Json_CodexExecJsonTurnCompleted_RecordsCachedAndFreshRemainder()
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
    public void Json_WrappedCodexPayloadUsage_RecordsCachedAndFreshRemainder()
    {
        var stdout = """{"type":"event_msg","payload":{"type":"turn_complete","usage":{"input_tokens":10546,"cached_input_tokens":2432,"output_tokens":5}}}""";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(8114, result.InputTokens);
        Assert.Equal(2432, result.CachedInputTokens);
        Assert.Equal(5, result.OutputTokens);
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
    public void HumanReadable_ParsesPromptAndCompletionLines()
    {
        var stdout = "Prompt tokens: 12,345 / Completion tokens: 678";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(12345, result.InputTokens);
        Assert.Equal(678, result.OutputTokens);
    }

    [Fact]
    public void HumanReadable_ParsesCachedTokensAndStoresFreshRemainder()
    {
        var stdout = "Prompt tokens: 12,345 / Cached input tokens: 2,000 / Completion tokens: 678";

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
