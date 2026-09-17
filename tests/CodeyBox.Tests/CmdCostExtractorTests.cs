using CodeyBox.Agents.Cmd;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="CmdCostExtractor"/>: usage + model extraction over
/// recorded real command-code 1.54.2 output (the run-total usage on the
/// terminal frames, the full dispatch id on <c>model_request_start</c>),
/// the all-zero/empty unknown paths, and the no-fallback-rate posture (an
/// agent with no readable single rate must report unknown rather than a
/// default that looks like data).
/// </summary>
public sealed class CmdCostExtractorTests
{
    [Fact]
    public void Kind_IsCmd()
    {
        Assert.Equal(AgentKind.Cmd, new CmdCostExtractor().Kind);
    }

    [Fact]
    public void DefaultPricing_IsNull_UnknownRatherThanFabricated()
    {
        // Cmd fronts 150+ providers with unrelated per-token economics: no
        // single fallback rate is honest, so the extractor reports unknown
        // and unrated models cost $0 with a startup warning.
        Assert.Null(new CmdCostExtractor().DefaultPricing);
    }

    [Fact]
    public void TryExtract_SuccessRun_ReturnsRunTotalAndQualifiedModelId()
    {
        // Recorded real frames (command-code 1.54.2): per-turn
        // model_request_end usage plus the run_total on the terminal result
        // line — the terminal frame sorts last and wins. The stream echoes
        // the full dispatch id (unlike omp, which strips the qualifier).
        const string stdout =
            "{\"type\":\"event\",\"event\":{\"type\":\"model_request_start\",\"model\":\"openrouter/nvidia/nemotron-3.5-lightning:free\"}}\n" +
            "{\"type\":\"event\",\"event\":{\"type\":\"model_request_end\",\"model\":\"openrouter/nvidia/nemotron-3.5-lightning:free\"," +
            "\"usage\":{\"inputTokens\":15353,\"outputTokens\":186,\"cacheReadTokens\":0,\"cacheWriteTokens\":0},\"stopReason\":\"tool_calls\"}}\n" +
            "{\"type\":\"event\",\"event\":{\"type\":\"turn_end\",\"turnNumber\":1,\"hadToolCalls\":true," +
            "\"usage\":{\"inputTokens\":15353,\"outputTokens\":186,\"cacheReadTokens\":0,\"cacheWriteTokens\":0}}}\n" +
            "{\"type\":\"result\",\"subtype\":\"success\",\"isError\":false," +
            "\"usage\":{\"inputTokens\":46284,\"outputTokens\":282,\"cacheReadTokens\":21760,\"cacheWriteTokens\":0}," +
            "\"durationMs\":110866,\"finalText\":\"Done!\"}";

        var snapshot = new CmdCostExtractor().TryExtract(stdout, agentStderr: string.Empty);

        Assert.NotNull(snapshot);
        Assert.Equal(46284, snapshot.InputTokens);
        Assert.Equal(21760, snapshot.CachedInputTokens);
        Assert.Equal(282, snapshot.OutputTokens);
        Assert.Equal("openrouter/nvidia/nemotron-3.5-lightning:free", snapshot.ModelId);
    }

    [Fact]
    public void TryExtract_MaxTurnsRun_ReturnsUsageDespiteEmptyFinalText()
    {
        // Exit 8 (command-code 1.54.2): max_turns subtype, empty finalText,
        // but the turns burned real tokens — spend is spend.
        const string stdout =
            "{\"type\":\"event\",\"event\":{\"type\":\"turn_end\",\"turnNumber\":63,\"hadToolCalls\":false," +
            "\"usage\":{\"inputTokens\":220425,\"outputTokens\":8191,\"cacheReadTokens\":4096,\"cacheWriteTokens\":0}}}\n" +
            "{\"type\":\"result\",\"subtype\":\"max_turns\",\"isError\":true," +
            "\"usage\":{\"inputTokens\":222468,\"outputTokens\":8281,\"cacheReadTokens\":4096,\"cacheWriteTokens\":0}," +
            "\"durationMs\":1684026,\"finalText\":\"\",\"error\":\"Stopped: exceeded maximum turns (100).\"}";

        var snapshot = new CmdCostExtractor().TryExtract(stdout, agentStderr: null);

        Assert.NotNull(snapshot);
        Assert.Equal(222468, snapshot.InputTokens);
        Assert.Equal(4096, snapshot.CachedInputTokens);
        Assert.Equal(8281, snapshot.OutputTokens);
    }

    [Fact]
    public void TryExtract_QuotaFailureAllZeroUsage_ReturnsNull()
    {
        // Recorded real failure shape (paid model on $0 key, exit 4): usage
        // frames exist but every counter is zero — null is unknown, never a
        // zero that looks like data.
        const string stdout =
            "{\"type\":\"event\",\"event\":{\"type\":\"run_error\",\"error\":{\"name\":\"Error\",\"message\":\"Error: 403\"}}}\n" +
            "{\"type\":\"result\",\"subtype\":\"error\",\"isError\":true," +
            "\"usage\":{\"inputTokens\":0,\"outputTokens\":0,\"cacheReadTokens\":0,\"cacheWriteTokens\":0}," +
            "\"durationMs\":2202,\"finalText\":\"\",\"error\":\"Error: 403 Key limit exceeded (total limit).\"}";

        Assert.Null(new CmdCostExtractor().TryExtract(stdout, agentStderr: string.Empty));
    }

    [Fact]
    public void TryExtract_EmptyResponse_ReturnsNull()
    {
        Assert.Null(new CmdCostExtractor().TryExtract(agentStdout: string.Empty, agentStderr: string.Empty));
        Assert.Null(new CmdCostExtractor().TryExtract(agentStdout: null, agentStderr: null));
    }

    [Fact]
    public void TryExtract_MalformedLines_NeverThrows()
    {
        const string stdout = "not json\n{\"type\":\"result\", truncated\n";

        Assert.Null(new CmdCostExtractor().TryExtract(stdout, agentStderr: null));
    }

    [Fact]
    public void TryExtract_ProseMentioningUsageWord_DoesNotFabricate()
    {
        // The prescreen matches the `"usage"` token; only a JSON frame with
        // a real usage object may produce a snapshot. Assistant prose merely
        // mentioning usage must not.
        const string stdout =
            "{\"type\":\"event\",\"event\":{\"type\":\"message_end\",\"content\":[{\"type\":\"text\"," +
            "\"text\":\"Here is the usage summary of my changes.\"}]}}\n";

        Assert.Null(new CmdCostExtractor().TryExtract(stdout, agentStderr: null));
    }
}
