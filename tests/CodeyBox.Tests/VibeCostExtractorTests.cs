using CodeyBox.Agents.Vibe;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="VibeCostExtractor"/>: vibe's programmatic history
/// entries carry no token counts (verified against vibe 2.25.4 live frames),
/// so the extractor reports unknown rather than a default that looks like
/// data.
/// </summary>
public sealed class VibeCostExtractorTests
{
    // Recorded real run (vibe 2.25.4, OpenRouter nemotron free): history
    // entries only, no usage object anywhere in the stream.
    private const string RealStream =
        """{"id":"eaba512d-57c4-41c4-8478-d83c4c75929f","sessionId":"a54c1241-bc61-28d6-0141-2a675c3860c1","turnId":"b6f13933-c15e-45b8-bf46-b419a881ef5b","createdAt":1789580841806,"updatedAt":1789580841806,"generationStatus":"completed","relatedEntryId":null,"type":"message","role":"user","content":[{"type":"text","text":"What is 4+4? Reply with just the number."}],"source":"turn_start","userDisplayContent":null}""" + "\n" +
        """{"id":"265ca6b7-305c-40f6-a8ca-46cca31fd819","sessionId":"a54c1241-bc61-28d6-0141-2a675c3860c1","turnId":"b6f13933-c15e-45b8-bf46-b419a881ef5b","createdAt":1789580847231,"updatedAt":1789580847320,"generationStatus":"completed","relatedEntryId":null,"type":"message","role":"assistant","content":[{"type":"text","text":"8"}],"source":null,"userDisplayContent":null}""";

    [Fact]
    public void Kind_IsVibe()
    {
        Assert.Equal(AgentKind.Vibe, new VibeCostExtractor().Kind);
    }

    [Fact]
    public void DefaultPricing_IsNull_UnknownRatherThanFabricated()
    {
        // A per-million-token fallback rate would look like data; the honest
        // answer is unknown (the operator's provider account bills the run).
        Assert.Null(new VibeCostExtractor().DefaultPricing);
    }

    [Fact]
    public void TryExtract_RealStream_ReturnsNull()
    {
        // Even a healthy run with real assistant output yields no token
        // counts — nothing in the stream can back a cost row.
        Assert.Null(new VibeCostExtractor().TryExtract(RealStream, string.Empty));
    }

    [Fact]
    public void TryExtract_FailureStderr_ReturnsNull()
    {
        // Recorded real provider-rejection stderr: rich failure text, still
        // no token counts.
        const string stderr =
            "Error: API error from openrouter (model: anthropic/claude-haiku-4.5): LLM backend error [openrouter]\n" +
            "  status: 403 Forbidden\n" +
            "  provider_message: Key limit exceeded (total limit). Manage it using https://openrouter.ai/workspaces/default/keys/64835ad0564749843e34c8e2e6d1482456dad7a14fbbd107a5a174ea87fc6058";
        Assert.Null(new VibeCostExtractor().TryExtract("stdout", stderr));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "\n")]
    public void TryExtract_BlankStreams_ReturnsNull(string? stdout, string? stderr)
    {
        Assert.Null(new VibeCostExtractor().TryExtract(stdout, stderr));
    }
}
