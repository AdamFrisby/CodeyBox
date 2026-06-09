using CodeyBox.Agents.Antigravity;

namespace CodeyBox.Tests;

public sealed class AntigravityModelListProbeParserTests
{
    [Fact]
    public void ExtractModelIds_SummaryShape_ReadsPerModel()
    {
        var json = """
        {
          "perModel": [
            {"modelId": "gemini-3.5-flash-high"},
            {"modelId": "claude-opus-4-6-thinking"}
          ]
        }
        """;

        var ids = AntigravityModelListProbe.ExtractModelIds(json);

        Assert.Contains("gemini-3.5-flash-high", ids);
        Assert.Contains("claude-opus-4-6-thinking", ids);
    }

    [Fact]
    public void ExtractModelIds_QuotaShape_ReadsBuckets()
    {
        var json = """
        {
          "buckets": [
            {"modelId": "gpt-oss-120b-medium"},
            {"modelId": "gemini-3.1-pro-high"}
          ]
        }
        """;

        var ids = AntigravityModelListProbe.ExtractModelIds(json);

        Assert.Contains("gpt-oss-120b-medium", ids);
        Assert.Contains("gemini-3.1-pro-high", ids);
    }

    [Fact]
    public void ExtractModelIds_DedupesAcrossArrays()
    {
        var json = """
        {
          "perModel": [{"modelId": "gemini-3.5-flash-high"}],
          "buckets":  [{"modelId": "gemini-3.5-flash-high"}, {"modelId": "claude-opus-4-6-thinking"}]
        }
        """;

        var ids = AntigravityModelListProbe.ExtractModelIds(json);

        Assert.Equal(2, ids.Count);
        Assert.Contains("gemini-3.5-flash-high", ids);
        Assert.Contains("claude-opus-4-6-thinking", ids);
    }

    [Fact]
    public void ExtractModelIds_Garbage_ReturnsEmpty()
    {
        Assert.Empty(AntigravityModelListProbe.ExtractModelIds("not json"));
        Assert.Empty(AntigravityModelListProbe.ExtractModelIds("{}"));
    }
}
