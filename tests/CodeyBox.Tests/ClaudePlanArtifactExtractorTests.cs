using CodeyBox.Agents.Claude;

namespace CodeyBox.Tests;

public sealed class ClaudePlanArtifactExtractorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Extract_ReturnsNull_ForEmptyOrWhitespaceInput(string? input)
    {
        Assert.Null(ClaudePlanArtifactExtractor.Extract(input));
    }

    [Fact]
    public void Extract_ReturnsNull_WhenNoStreamJsonEventsObserved()
    {
        // Pure-text stdout has no NDJSON envelope; caller must fall back to raw.
        var rawText = "Plain output with {curly braces} but never a json object on a line.\n";
        Assert.Null(ClaudePlanArtifactExtractor.Extract(rawText));
    }

    [Fact]
    public void Extract_SkipsMalformedAndNonJsonLines()
    {
        // Mix of unparsable lines, lines without 'type', and one valid event.
        var stdout = string.Join('\n', new[]
        {
            "not json at all",
            "{ invalid json",
            "{\"missing_type\": true}",
            "{\"type\": 42}", // type must be a string
            "{\"type\": \"assistant\", \"message\": {\"content\": [{\"type\": \"text\", \"text\": \"good\"}]}}",
        });

        Assert.Equal("good", ClaudePlanArtifactExtractor.Extract(stdout));
    }

    [Fact]
    public void Extract_ResultEvent_RequiresStringResultField()
    {
        // Non-string 'result' is ignored; a stream event still observed,
        // so we don't fall back to null — we return empty.
        var stdout = "{\"type\": \"result\", \"result\": 42}";
        Assert.Equal(string.Empty, ClaudePlanArtifactExtractor.Extract(stdout));
    }

    [Fact]
    public void Extract_AssistantContent_IgnoresNonTextParts()
    {
        // tool_use parts must be skipped; only text parts are concatenated.
        var stdout = "{\"type\": \"assistant\", \"message\": {\"content\": ["
            + "{\"type\": \"tool_use\", \"id\": \"tu_1\", \"name\": \"Edit\"},"
            + "{\"type\": \"text\", \"text\": \"approved plan body\"}"
            + "]}}";

        Assert.Equal("approved plan body", ClaudePlanArtifactExtractor.Extract(stdout));
    }

    [Fact]
    public void Extract_AssistantTextTakesPrecedence_OverResultText()
    {
        // Both stream events present; assistant text wins, result is unused.
        var stdout = string.Join('\n', new[]
        {
            "{\"type\": \"assistant\", \"message\": {\"content\": [{\"type\": \"text\", \"text\": \"assistant-said\"}]}}",
            "{\"type\": \"result\", \"result\": \"result-said\"}",
        });

        Assert.Equal("assistant-said", ClaudePlanArtifactExtractor.Extract(stdout));
    }

    [Fact]
    public void Extract_ResultText_UsedWhenNoAssistantTextEmitted()
    {
        var stdout = "{\"type\": \"result\", \"result\": \"plan body from result\"}";
        Assert.Equal("plan body from result", ClaudePlanArtifactExtractor.Extract(stdout));
    }

    [Fact]
    public void Extract_ConcatenatesMultipleAssistantTextParts()
    {
        var stdout = "{\"type\": \"assistant\", \"message\": {\"content\": ["
            + "{\"type\": \"text\", \"text\": \"first\"},"
            + "{\"type\": \"text\", \"text\": \"second\"}"
            + "]}}";

        var extracted = ClaudePlanArtifactExtractor.Extract(stdout);
        Assert.NotNull(extracted);
        Assert.Contains("first", extracted, StringComparison.Ordinal);
        Assert.Contains("second", extracted, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_AssistantWithoutMessageWrapper_StillReadsContent()
    {
        // Some envelopes nest content directly under root rather than under message.
        var stdout = "{\"type\": \"assistant\", \"content\": [{\"type\": \"text\", \"text\": \"top-level\"}]}";

        Assert.Equal("top-level", ClaudePlanArtifactExtractor.Extract(stdout));
    }

    [Fact]
    public void Extract_AssistantEventWithoutTextParts_ReturnsEmpty()
    {
        // A stream event was observed but yielded no text. The caller should
        // distinguish "fall back to raw" (null) from "stream said nothing" (empty).
        var stdout = "{\"type\": \"assistant\", \"message\": {\"content\": [{\"type\": \"tool_use\", \"name\": \"Edit\"}]}}";

        Assert.Equal(string.Empty, ClaudePlanArtifactExtractor.Extract(stdout));
    }
}
