using CodeyBox.Agents.Autohand;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="AutohandCostExtractor"/>. Bare headless streams carry
/// no machine-readable usage frame (verified against autohand-cli 0.9.7 —
/// the failure and healthy fixtures below are recorded real output), so the
/// extractor reports unknown (null) rather than a zero that looks like data.
/// Only the documented <c>messageType: "usage"</c> frame yields a snapshot.
/// </summary>
public sealed class AutohandCostExtractorTests
{
    private static AutohandCostExtractor Extractor() => new();

    [Fact]
    public void Kind_IsAutohand()
    {
        Assert.Equal(AgentKind.Autohand, Extractor().Kind);
    }

    [Fact]
    public void DefaultPricing_IsNull()
    {
        // No fallback rate: without a measured token snapshot there is
        // nothing to price, and a fallback would book unmeasured cost.
        Assert.Null(Extractor().DefaultPricing);
    }

    [Fact]
    public void TryExtract_RecordedHealthyRun_ReturnsNull()
    {
        // Recorded real output (autohand-cli 0.9.7, bare run that edited a
        // file): tool/file/result events, no usage frame, plus the human
        // footer on stderr ("12.0k tokens used") which is a rounded total
        // with no input/output split — parsing it would fabricate data.
        const string stdout =
            "{\"type\":\"tool_start\",\"toolId\":\"call-1\",\"toolName\":\"read_file\",\"toolArgs\":{\"path\":\"notes.txt\"}}\n" +
            "{\"type\":\"tool_end\",\"toolId\":\"call-1\",\"toolName\":\"read_file\",\"toolSuccess\":true,\"toolOutput\":\"1 line\"}\n" +
            "{\"type\":\"file_modified\",\"filePath\":\"notes.txt\",\"changeType\":\"modify\",\"toolId\":\"call-2\"}\n" +
            "{\"type\":\"result\",\"content\":\"The task is complete.\"}";
        const string stderr = "Completed in 0m 07s · 12.0k tokens used";

        Assert.Null(Extractor().TryExtract(stdout, stderr));
    }

    [Fact]
    public void TryExtract_RecordedAuthFailure_ReturnsNull()
    {
        const string stdout =
            "{\"type\":\"error\",\"message\":\"Authentication failed. Please verify your OpenRouter API key in ~/.autohand/config.json.\\nUser not found.\"}";

        Assert.Null(Extractor().TryExtract(stdout, "Authentication failed. Please verify your OpenRouter API key."));
    }

    [Fact]
    public void TryExtract_RecordedQuotaFailure_ReturnsNull()
    {
        const string stdout =
            "{\"type\":\"error\",\"message\":\"Access denied. Your OpenRouter API key may not have permission for this model.\\nKey limit exceeded (total limit).\"}";

        Assert.Null(Extractor().TryExtract(stdout, "Key limit exceeded (total limit)."));
    }

    [Fact]
    public void TryExtract_EmptyResponse_ReturnsNull()
    {
        Assert.Null(Extractor().TryExtract(string.Empty, string.Empty));
        Assert.Null(Extractor().TryExtract(null, null));
        Assert.Null(Extractor().TryExtract("not json at all\nmore chatter", "stderr tail"));
    }

    [Fact]
    public void TryExtract_DocumentedUsageFrame_ReturnsSnapshot()
    {
        // Shape from the headless-mode docs (messageType:usage with
        // promptTokens/completionTokens); honoured when present even though
        // observed bare streams omit it.
        const string stdout =
            "{\"type\":\"message\",\"messageType\":\"usage\",\"promptTokens\":294,\"completionTokens\":97}";

        var snapshot = Extractor().TryExtract(stdout, null);

        Assert.NotNull(snapshot);
        Assert.Equal(294, snapshot.InputTokens);
        Assert.Equal(97, snapshot.OutputTokens);
        Assert.Equal(0, snapshot.CachedInputTokens);
    }

    [Fact]
    public void TryExtract_HostileInputs_ReturnNull()
    {
        // Deeply nested JSON, hostile numbers, and non-object lines must
        // never throw and never yield a snapshot.
        Assert.Null(Extractor().TryExtract("{\"type\":\"message\",\"messageType\":\"usage\"}", null));
        Assert.Null(Extractor().TryExtract("{\"type\":\"message\",\"messageType\":\"usage\",\"promptTokens\":-5}", null));
        Assert.Null(Extractor().TryExtract("{\"type\":\"message\",\"messageType\":\"usage\",\"promptTokens\":\"lots\"}", null));
        Assert.Null(Extractor().TryExtract("[1,2,3]", null));
        Assert.Null(Extractor().TryExtract(new string('x', 100_000), null));
    }
}
