using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class QuestionParserTests
{
    [Fact]
    public void Parse_SingleBlock_ReturnsQuestion()
    {
        var stdout = """
            Some agent output here.
            <codeybox-question id="q-001">Should I use approach A or B?</codeybox-question>
            More agent output.
            """;
        var result = QuestionParser.Parse(stdout, NullLogger.Instance);
        Assert.Single(result);
        Assert.Equal("q-001", result[0].QuestionId);
        Assert.Equal("Should I use approach A or B?", result[0].QuestionText);
    }

    [Fact]
    public void Parse_MultiLineBlock_ReturnsFullText()
    {
        var stdout = """
            <codeybox-question id="q-002">
            Line one of the question.
            Line two of the question.
            Default: use approach A.
            </codeybox-question>
            """;
        var result = QuestionParser.Parse(stdout, NullLogger.Instance);
        Assert.Single(result);
        Assert.Equal("q-002", result[0].QuestionId);
        Assert.Contains("Line one", result[0].QuestionText);
        Assert.Contains("Line two", result[0].QuestionText);
    }

    [Fact]
    public void Parse_MultipleBlocks_ReturnsAllQuestions()
    {
        var stdout = """
            <codeybox-question id="q-001">First question?</codeybox-question>
            Some text.
            <codeybox-question id="q-002">Second question?</codeybox-question>
            """;
        var result = QuestionParser.Parse(stdout, NullLogger.Instance);
        Assert.Equal(2, result.Count);
        Assert.Equal("q-001", result[0].QuestionId);
        Assert.Equal("q-002", result[1].QuestionId);
    }

    [Fact]
    public void Parse_NullStdout_ReturnsEmpty()
    {
        var result = QuestionParser.Parse(null, NullLogger.Instance);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_EmptyStdout_ReturnsEmpty()
    {
        var result = QuestionParser.Parse("", NullLogger.Instance);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_NoBlocks_ReturnsEmpty()
    {
        var result = QuestionParser.Parse("Normal agent output with no questions.", NullLogger.Instance);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_InvalidId_TooLong_Ignored()
    {
        var longId = new string('a', 65);
        var stdout = $"<codeybox-question id=\"{longId}\">Question?</codeybox-question>";
        // The regex id group is limited to {1,64} chars, so this won't match at all.
        var result = QuestionParser.Parse(stdout, NullLogger.Instance);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_InvalidId_WithSpaces_Ignored()
    {
        var stdout = "<codeybox-question id=\"bad id\">Question?</codeybox-question>";
        // Spaces aren't in [^"]{1,64} — actually they are, but IdPattern rejects them.
        var result = QuestionParser.Parse(stdout, NullLogger.Instance);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_ValidIdCharacters_Accepted()
    {
        var stdout = "<codeybox-question id=\"q_001-abc\">Valid id?</codeybox-question>";
        var result = QuestionParser.Parse(stdout, NullLogger.Instance);
        Assert.Single(result);
        Assert.Equal("q_001-abc", result[0].QuestionId);
    }

    [Fact]
    public void Parse_EmptyQuestionText_Ignored()
    {
        var stdout = "<codeybox-question id=\"q-001\">   </codeybox-question>";
        var result = QuestionParser.Parse(stdout, NullLogger.Instance);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_DuplicateIdInSameStdout_DeduplicatedToOne()
    {
        var stdout = """
            <codeybox-question id="q-001">First time.</codeybox-question>
            <codeybox-question id="q-001">Second time (duplicate).</codeybox-question>
            """;
        var result = QuestionParser.Parse(stdout, NullLogger.Instance);
        Assert.Single(result);
        Assert.Equal("First time.", result[0].QuestionText);
    }

    [Fact]
    public void Parse_MalformedTag_NotMatched()
    {
        // Missing id attribute
        var stdout = "<codeybox-question>No id attribute.</codeybox-question>";
        var result = QuestionParser.Parse(stdout, NullLogger.Instance);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_SecretInQuestionText_IsRedacted()
    {
        var stdout = "<codeybox-question id=\"q-001\">Use token sk-ant-api123abc for auth.</codeybox-question>";
        var result = QuestionParser.Parse(stdout, NullLogger.Instance);
        Assert.Single(result);
        Assert.DoesNotContain("sk-ant-api123abc", result[0].QuestionText);
        Assert.Contains("***", result[0].QuestionText);
    }

    [Fact]
    public void Parse_VeryLongText_TruncatedAt4000Chars()
    {
        var longText = new string('x', 5000);
        var stdout = $"<codeybox-question id=\"q-001\">{longText}</codeybox-question>";
        var result = QuestionParser.Parse(stdout, NullLogger.Instance);
        Assert.Single(result);
        Assert.True(result[0].QuestionText.Length <= 4020); // 4000 + "[truncated]" overhead
        Assert.Contains("[truncated]", result[0].QuestionText);
    }

    [Fact]
    public void Parse_MixedValidAndInvalid_ReturnsOnlyValid()
    {
        var stdout = """
            <codeybox-question id="q-001">Valid question.</codeybox-question>
            <codeybox-question id="bad id with spaces">Invalid id.</codeybox-question>
            <codeybox-question id="q-002">Another valid one.</codeybox-question>
            """;
        var result = QuestionParser.Parse(stdout, NullLogger.Instance);
        Assert.Equal(2, result.Count);
        Assert.Equal("q-001", result[0].QuestionId);
        Assert.Equal("q-002", result[1].QuestionId);
    }
}
