using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class ClaudeCostExtractorTests
{
    private static readonly ClaudeCostExtractor Extractor = new();

    private const string NdJsonFixture = """
        {"type":"system","subtype":"init","session_id":"abc","tools":[]}
        {"type":"assistant","message":{"id":"msg_01","type":"message","role":"assistant","model":"claude-opus-4-7","content":[{"type":"text","text":"Working on it"}],"stop_reason":"end_turn","usage":{"input_tokens":1000,"output_tokens":200,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}}
        {"type":"result","subtype":"success","duration_ms":5000,"num_turns":1,"result":"Done","is_error":false,"session_id":"abc","total_cost_usd":0.05,"usage":{"input_tokens":12345,"output_tokens":678,"cache_read_input_tokens":5000,"cache_creation_input_tokens":0}}
        """;

    [Fact]
    public void NdJson_ParsesTokenCounts()
    {
        var result = Extractor.TryExtract(NdJsonFixture, null);

        Assert.NotNull(result);
        Assert.Equal(12345, result.InputTokens);
        Assert.Equal(678, result.OutputTokens);
        Assert.Equal(5000, result.CachedInputTokens);
    }

    [Fact]
    public void NdJson_ExtractsModelIdFromAssistantMessage()
    {
        var result = Extractor.TryExtract(NdJsonFixture, null);

        Assert.NotNull(result);
        Assert.Equal("claude-opus-4-7", result.ModelId);
    }

    [Fact]
    public void HumanReadable_ParsesCompactFooter()
    {
        var stdout = "12,345 input tokens, 678 output tokens, 5,000 cached tokens";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(12345, result.InputTokens);
        Assert.Equal(678, result.OutputTokens);
        Assert.Equal(5000, result.CachedInputTokens);
    }

    [Fact]
    public void NullAndEmpty_ReturnsNull()
    {
        Assert.Null(Extractor.TryExtract(null, null));
        Assert.Null(Extractor.TryExtract("", ""));
        Assert.Null(Extractor.TryExtract("   ", null));
    }

    [Fact]
    public void MalformedJson_ReturnsNull()
    {
        var stdout = "{\"type\":\"result\",\"usage\":{broken json";

        var result = Extractor.TryExtract(stdout, null);

        Assert.Null(result);
    }

    [Fact]
    public void NdJsonWithNoUsageField_ReturnsNull()
    {
        var stdout = """
            {"type":"system","subtype":"init","session_id":"abc","tools":[]}
            {"type":"result","subtype":"success","duration_ms":1000,"num_turns":1,"result":"Done","is_error":false}
            """;

        var result = Extractor.TryExtract(stdout, null);

        Assert.Null(result);
    }

    [Fact]
    public void StderrFallback_ParsesWhenStdoutEmpty()
    {
        var stderr = "12,345 input tokens, 678 output tokens, 5,000 cached tokens";

        var result = Extractor.TryExtract(null, stderr);

        Assert.NotNull(result);
        Assert.Equal(12345, result.InputTokens);
        Assert.Equal(678, result.OutputTokens);
    }
}
