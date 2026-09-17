using CodeyBox.Agents.Qwen;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="QwenCostExtractor"/>. Qwen reports provider usage
/// as <c>usage {input_tokens, output_tokens, cache_read_input_tokens}</c>
/// on assistant frames (nested under <c>message</c>) and the terminal
/// <c>result</c> frame, with the dispatch model in <c>message.model</c> and
/// a per-model breakdown in <c>stats.models</c>. Usage grows across turns,
/// so the latest frame is the run total. Recorded live against qwen 0.24.0.
/// </summary>
public sealed class QwenCostExtractorTests
{
    private static readonly QwenCostExtractor Extractor = new();

    [Fact]
    public void Kind_IsQwen()
    {
        Assert.Equal(AgentKind.Qwen, Extractor.Kind);
    }

    [Fact]
    public void DefaultPricing_IsNull()
    {
        // Qwen fronts many providers with unrelated per-token economics; no
        // single fallback rate is honest. Operators configure per-model rates
        // under CodeyBox:AgentPricing (or rely on the bundled qwen bucket).
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
    public void LiveSuccessRun_ParsesLatestUsageAndModel()
    {
        // Recorded live (ids redacted): the assistant frame's usage is
        // smaller than the result frame's — the latest frame wins.
        const string stdout =
            "{\"type\":\"assistant\",\"session_id\":\"s\",\"parent_tool_use_id\":null,\"message\":{\"role\":\"assistant\",\"model\":\"nvidia/nemotron-3.5-lightning:free\",\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"usage\":{\"input_tokens\":24157,\"output_tokens\":58,\"cache_read_input_tokens\":0,\"total_tokens\":24215}}}\n" +
            "{\"type\":\"result\",\"subtype\":\"success\",\"session_id\":\"s\",\"is_error\":false,\"duration_ms\":115311,\"num_turns\":1,\"result\":\"ok\",\"usage\":{\"input_tokens\":33823,\"output_tokens\":795,\"cache_read_input_tokens\":0,\"total_tokens\":34618},\"permission_denials\":[],\"stats\":{\"models\":{\"nvidia/nemotron-3.5-lightning:free\":{\"tokens\":{\"prompt\":33823,\"candidates\":795,\"total\":34618,\"cached\":0}}}}}\n";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(33823, result!.InputTokens);
        Assert.Equal(795, result.OutputTokens);
        Assert.Equal(0, result.CachedInputTokens);
        Assert.Equal("nvidia/nemotron-3.5-lightning:free", result.ModelId);
    }

    [Fact]
    public void CachedInput_MappedToCachedBucket()
    {
        const string stdout =
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"usage\":{\"input_tokens\":100,\"output_tokens\":20,\"cache_read_input_tokens\":400,\"total_tokens\":520}}\n";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(100, result!.InputTokens);
        Assert.Equal(20, result.OutputTokens);
        Assert.Equal(400, result.CachedInputTokens);
    }

    [Fact]
    public void ModelFromStats_WhenNoMessageModel()
    {
        const string stdout =
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"usage\":{\"input_tokens\":50,\"output_tokens\":5,\"cache_read_input_tokens\":0,\"total_tokens\":55},\"stats\":{\"models\":{\"some/model:free\":{\"tokens\":{\"prompt\":50,\"candidates\":5,\"total\":55,\"cached\":0}}}}}\n";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal("some/model:free", result!.ModelId);
    }

    [Fact]
    public void LiveFailureRun_AllZeroUsage_ReturnsNull()
    {
        // Recorded live: bogus key, exit 1. Error runs report all-zero
        // usage — unknown, never a zero that looks like data.
        const string stdout =
            "{\"type\":\"assistant\",\"session_id\":\"s\",\"parent_tool_use_id\":null,\"message\":{\"role\":\"assistant\",\"model\":\"m\",\"content\":[{\"type\":\"text\",\"text\":\"[API Error: 401 Missing Authentication header]\"}],\"usage\":{\"input_tokens\":0,\"output_tokens\":0}}}\n" +
            "{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"session_id\":\"s\",\"is_error\":true,\"usage\":{\"input_tokens\":0,\"output_tokens\":0,\"cache_read_input_tokens\":0},\"error\":{\"message\":\"[API Error: 401 Missing Authentication header]\"}}\n";

        Assert.Null(Extractor.TryExtract(stdout, null));
    }

    [Fact]
    public void EmptyResponse_ReturnsNull()
    {
        // A run that emitted no usage frames at all (empty response)
        // reports unknown rather than a default that looks like data.
        Assert.Null(Extractor.TryExtract("{\"type\":\"system\",\"subtype\":\"init\"}\n", null));
        Assert.Null(Extractor.TryExtract("some plaintext banner\n", null));
    }

    [Fact]
    public void UsageOnStderr_Extracted()
    {
        const string stderr =
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"usage\":{\"input_tokens\":7,\"output_tokens\":3,\"cache_read_input_tokens\":0,\"total_tokens\":10}}\n";

        var result = Extractor.TryExtract(null, stderr);

        Assert.NotNull(result);
        Assert.Equal(7, result!.InputTokens);
    }

    [Fact]
    public void NonJsonChatter_Skipped()
    {
        const string stdout =
            "some plaintext banner\n" +
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"usage\":{\"input_tokens\":11,\"output_tokens\":2,\"cache_read_input_tokens\":0,\"total_tokens\":13}}\n" +
            "{\"broken\": \n";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(11, result!.InputTokens);
    }

    [Fact]
    public void NeverThrows_OnHostileInput()
    {
        const string stdout = "{\"usage\":[1,2,{\"input_tokens\":\"x\"}]}\n{\"usage\":{\"input_tokens\":-5,\"output_tokens\":null}}\n{\"message\":{\"model\":42}}\n";

        // Must return normally (null here — no positive counts), never throw.
        Assert.Null(Extractor.TryExtract(stdout, stdout));
    }
}
