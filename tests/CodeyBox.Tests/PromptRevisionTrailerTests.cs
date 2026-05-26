using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the new <c>CodeyBox-Prompt-Revision: N</c> trailer line on the
/// commit-message attribution block. Only present when the orchestrator passes
/// an explicit dispatch revision; legacy callers that don't supply one still
/// get the original 3-line block.
/// </summary>
public sealed class PromptRevisionTrailerTests
{
    private static readonly WorkItemId TestItemId =
        new(Guid.Parse("11111111-2222-3333-4444-555555555555"));

    [Fact]
    public void Compose_IncludesPromptRevisionTrailer_WhenDispatchedRevisionProvided()
    {
        var trailer = CodeyBoxTrailers.Compose(
            TestItemId,
            AgentKind.Claude,
            "claude-opus-4-7",
            fallbackHistory: null,
            promptRevisionAtDispatch: 7);

        Assert.Contains($"{CodeyBoxTrailers.PromptRevisionTrailerKey}: 7\n", trailer);
        Assert.EndsWith(CodeyBoxTrailers.CoAuthoredBy, trailer);
    }

    [Fact]
    public void Compose_OmitsPromptRevisionTrailer_WhenRevisionIsNull()
    {
        var trailer = CodeyBoxTrailers.Compose(
            TestItemId,
            AgentKind.Claude,
            "claude-opus-4-7",
            fallbackHistory: null,
            promptRevisionAtDispatch: null);

        Assert.DoesNotContain(CodeyBoxTrailers.PromptRevisionTrailerKey, trailer);
    }

    [Fact]
    public void Compose_PromptRevisionTrailerOrderedBetweenAgentAndCoAuthoredBy()
    {
        // git interprets trailers from the bottom up; keep them grouped so
        // existing parsers (Co-Authored-By scrapers, our own auditor) all find
        // the keys without depending on absolute line numbers.
        var trailer = CodeyBoxTrailers.Compose(
            TestItemId,
            AgentKind.Gemini,
            finalModel: "gemini-3-pro",
            promptRevisionAtDispatch: 2);

        var agentIdx = trailer.IndexOf(CodeyBoxTrailers.AgentTrailerKey, StringComparison.Ordinal);
        var revIdx = trailer.IndexOf(CodeyBoxTrailers.PromptRevisionTrailerKey, StringComparison.Ordinal);
        var coIdx = trailer.IndexOf("Co-Authored-By", StringComparison.Ordinal);
        Assert.True(agentIdx < revIdx, "agent trailer must precede prompt-revision trailer");
        Assert.True(revIdx < coIdx, "prompt-revision trailer must precede Co-Authored-By");
    }
}
