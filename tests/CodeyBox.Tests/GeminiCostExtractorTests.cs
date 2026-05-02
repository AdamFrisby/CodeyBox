using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class GeminiCostExtractorTests
{
    private static readonly GeminiCostExtractor Extractor = new();

    [Fact]
    public void NdJson_ParsesPromptAndCandidatesTokenCounts()
    {
        var stdout = """{"promptTokenCount":12345,"candidatesTokenCount":678,"model":"gemini-3.0-pro"}""";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(12345, result.InputTokens);
        Assert.Equal(678, result.OutputTokens);
        Assert.Equal("gemini-3.0-pro", result.ModelId);
    }

    [Fact]
    public void HumanReadable_ParsesPromptAndCandidatesLines()
    {
        var stdout = "Prompt tokens: 12,345\nCandidates tokens: 678";

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
}
