using CodeyBox.Agents.Unreal;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class UnrealCostExtractorTests
{
    private readonly UnrealCostExtractor _extractor = new();

    [Fact]
    public void Kind_IsUnreal()
    {
        Assert.Equal(AgentKind.Unreal, _extractor.Kind);
    }

    [Fact]
    public void TryExtract_FromModelResponseUsage_ReturnsTokens()
    {
        const string stdout =
            """
            {"Sequence":4,"Kind":"turn","Data":{}}
            {"Sequence":5,"Kind":"model_response","Data":{"Response":{"Usage":{"InputTokens":10,"CachedInputTokens":3,"OutputTokens":5}}}}
            """;

        var snapshot = _extractor.TryExtract(stdout, null);

        Assert.NotNull(snapshot);
        Assert.Equal(10, snapshot.InputTokens);
        Assert.Equal(3, snapshot.CachedInputTokens);
        Assert.Equal(5, snapshot.OutputTokens);
    }

    [Fact]
    public void TryExtract_MultipleResponses_UsesHighestOrLatest()
    {
        const string stdout =
            """
            {"Sequence":5,"Kind":"model_response","Data":{"Response":{"Usage":{"InputTokens":10,"CachedInputTokens":0,"OutputTokens":5}}}}
            {"Sequence":9,"Kind":"model_response","Data":{"Response":{"Usage":{"InputTokens":25,"CachedInputTokens":5,"OutputTokens":12}}}}
            """;

        var snapshot = _extractor.TryExtract(stdout, null);

        Assert.NotNull(snapshot);
        Assert.Equal(25, snapshot.InputTokens);
        Assert.Equal(5, snapshot.CachedInputTokens);
        Assert.Equal(12, snapshot.OutputTokens);
    }

    [Fact]
    public void TryExtract_NoUsage_ReturnsNull()
    {
        const string stdout = "{\"Sequence\":1,\"Kind\":\"input\",\"Data\":{}}";

        var snapshot = _extractor.TryExtract(stdout, null);

        Assert.Null(snapshot);
    }
}
