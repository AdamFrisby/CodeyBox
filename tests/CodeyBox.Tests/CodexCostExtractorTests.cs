using CodeyBox.Core;
using CodeyBox.Orchestrator;

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
        Assert.Equal(678, result.OutputTokens);
        Assert.Equal("codex-5.5", result.ModelId);
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
