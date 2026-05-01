using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class SuggestionsParserTests
{
    private static readonly IReadOnlyList<SuggestionEntry> Empty = [];

    private static string ValidJson(string title = "Add tests", string rationale = "Missing coverage",
        string category = "test-coverage", string severity = "minor", string effort = "small") => $$"""
        {
          "suggestions": [
            {
              "title": "{{title}}",
              "rationale": "{{rationale}}",
              "category": "{{category}}",
              "severity": "{{severity}}",
              "estimatedEffort": "{{effort}}"
            }
          ]
        }
        """;

    [Fact]
    public void Parse_ValidSchema_ReturnsEntries()
    {
        var entries = SuggestionsFileParser.Parse(ValidJson(), NullLogger.Instance);
        Assert.Single(entries);
        Assert.Equal("Add tests", entries[0].Title);
        Assert.Equal("Missing coverage", entries[0].Rationale);
        Assert.Equal("test-coverage", entries[0].Category);
        Assert.Equal("minor", entries[0].Severity);
        Assert.Equal("small", entries[0].EstimatedEffort);
    }

    [Fact]
    public void Parse_ValidSchema_WithFilesReferenced()
    {
        var json = """
            {
              "suggestions": [
                {
                  "title": "Fix test",
                  "rationale": "reason",
                  "category": "refactor",
                  "severity": "notable",
                  "estimatedEffort": "medium",
                  "filesReferenced": ["src/Foo.cs", "tests/FooTests.cs"]
                }
              ]
            }
            """;
        var entries = SuggestionsFileParser.Parse(json, NullLogger.Instance);
        Assert.Single(entries);
        Assert.Equal(2, entries[0].FilesReferenced.Count);
        Assert.Equal("src/Foo.cs", entries[0].FilesReferenced[0]);
    }

    [Fact]
    public void Parse_NullInput_ReturnsEmpty()
    {
        var entries = SuggestionsFileParser.Parse(null, NullLogger.Instance);
        Assert.Equal(Empty, entries);
    }

    [Fact]
    public void Parse_EmptyString_ReturnsEmpty()
    {
        var entries = SuggestionsFileParser.Parse("", NullLogger.Instance);
        Assert.Equal(Empty, entries);
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsEmpty()
    {
        var entries = SuggestionsFileParser.Parse("not json {{{ ", NullLogger.Instance);
        Assert.Equal(Empty, entries);
    }

    [Fact]
    public void Parse_NoSuggestionsArray_ReturnsEmpty()
    {
        var entries = SuggestionsFileParser.Parse("""{ "other": [] }""", NullLogger.Instance);
        Assert.Equal(Empty, entries);
    }

    [Fact]
    public void Parse_MissingTitle_SkipsEntry()
    {
        var json = """
            { "suggestions": [
                { "rationale": "r", "category": "other", "severity": "minor", "estimatedEffort": "tiny" }
            ]}
            """;
        var entries = SuggestionsFileParser.Parse(json, NullLogger.Instance);
        Assert.Equal(Empty, entries);
    }

    [Fact]
    public void Parse_MissingRationale_SkipsEntry()
    {
        var json = """
            { "suggestions": [
                { "title": "t", "category": "other", "severity": "minor", "estimatedEffort": "tiny" }
            ]}
            """;
        var entries = SuggestionsFileParser.Parse(json, NullLogger.Instance);
        Assert.Equal(Empty, entries);
    }

    [Fact]
    public void Parse_MissingCategory_SkipsEntry()
    {
        var json = """
            { "suggestions": [
                { "title": "t", "rationale": "r", "severity": "minor", "estimatedEffort": "tiny" }
            ]}
            """;
        var entries = SuggestionsFileParser.Parse(json, NullLogger.Instance);
        Assert.Equal(Empty, entries);
    }

    [Fact]
    public void Parse_MissingSeverity_SkipsEntry()
    {
        var json = """
            { "suggestions": [
                { "title": "t", "rationale": "r", "category": "other", "estimatedEffort": "tiny" }
            ]}
            """;
        var entries = SuggestionsFileParser.Parse(json, NullLogger.Instance);
        Assert.Equal(Empty, entries);
    }

    [Fact]
    public void Parse_MissingEstimatedEffort_SkipsEntry()
    {
        var json = """
            { "suggestions": [
                { "title": "t", "rationale": "r", "category": "other", "severity": "minor" }
            ]}
            """;
        var entries = SuggestionsFileParser.Parse(json, NullLogger.Instance);
        Assert.Equal(Empty, entries);
    }

    [Fact]
    public void Parse_InvalidCategory_SkipsEntry()
    {
        var json = """
            { "suggestions": [
                { "title": "t", "rationale": "r", "category": "unknown-cat", "severity": "minor", "estimatedEffort": "tiny" }
            ]}
            """;
        var entries = SuggestionsFileParser.Parse(json, NullLogger.Instance);
        Assert.Equal(Empty, entries);
    }

    [Fact]
    public void Parse_InvalidSeverity_SkipsEntry()
    {
        var json = """
            { "suggestions": [
                { "title": "t", "rationale": "r", "category": "other", "severity": "critical", "estimatedEffort": "tiny" }
            ]}
            """;
        var entries = SuggestionsFileParser.Parse(json, NullLogger.Instance);
        Assert.Equal(Empty, entries);
    }

    [Fact]
    public void Parse_InvalidEffort_SkipsEntry()
    {
        var json = """
            { "suggestions": [
                { "title": "t", "rationale": "r", "category": "other", "severity": "minor", "estimatedEffort": "huge" }
            ]}
            """;
        var entries = SuggestionsFileParser.Parse(json, NullLogger.Instance);
        Assert.Equal(Empty, entries);
    }

    [Fact]
    public void Parse_OversizedTitle_SkipsEntry()
    {
        var longTitle = new string('x', 121);
        var entries = SuggestionsFileParser.Parse(ValidJson(title: longTitle), NullLogger.Instance);
        Assert.Equal(Empty, entries);
    }

    [Fact]
    public void Parse_OversizedRationale_SkipsEntry()
    {
        var longRationale = new string('x', 2001);
        var entries = SuggestionsFileParser.Parse(ValidJson(rationale: longRationale), NullLogger.Instance);
        Assert.Equal(Empty, entries);
    }

    [Fact]
    public void Parse_MaxAllowedTitle_Accepted()
    {
        var title = new string('x', 120);
        var entries = SuggestionsFileParser.Parse(ValidJson(title: title), NullLogger.Instance);
        Assert.Single(entries);
    }

    [Fact]
    public void Parse_MaxAllowedRationale_Accepted()
    {
        var rationale = new string('x', 2000);
        var entries = SuggestionsFileParser.Parse(ValidJson(rationale: rationale), NullLogger.Instance);
        Assert.Single(entries);
    }

    [Fact]
    public void Parse_MixedValidAndInvalid_ReturnsOnlyValid()
    {
        var json = """
            { "suggestions": [
                { "title": "Good", "rationale": "r", "category": "docs", "severity": "notable", "estimatedEffort": "large" },
                { "rationale": "r", "category": "other", "severity": "minor", "estimatedEffort": "tiny" },
                { "title": "Also Good", "rationale": "r", "category": "security", "severity": "important", "estimatedEffort": "medium" }
            ]}
            """;
        var entries = SuggestionsFileParser.Parse(json, NullLogger.Instance);
        Assert.Equal(2, entries.Count);
        Assert.Equal("Good", entries[0].Title);
        Assert.Equal("Also Good", entries[1].Title);
    }

    [Fact]
    public void Parse_AllValidCategories_Accepted()
    {
        foreach (var cat in new[] { "test-coverage", "refactor", "dead-code", "security", "dependency", "docs", "other" })
        {
            var entries = SuggestionsFileParser.Parse(ValidJson(category: cat), NullLogger.Instance);
            Assert.Single(entries);
        }
    }

    [Fact]
    public void Parse_AllValidSeverities_Accepted()
    {
        foreach (var sev in new[] { "minor", "notable", "important" })
        {
            var entries = SuggestionsFileParser.Parse(ValidJson(severity: sev), NullLogger.Instance);
            Assert.Single(entries);
        }
    }

    [Fact]
    public void Parse_AllValidEfforts_Accepted()
    {
        foreach (var effort in new[] { "tiny", "small", "medium", "large" })
        {
            var entries = SuggestionsFileParser.Parse(ValidJson(effort: effort), NullLogger.Instance);
            Assert.Single(entries);
        }
    }
}
