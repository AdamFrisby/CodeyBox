using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="PipelineRunner"/> internal helpers.
/// </summary>
public sealed class PipelineRunnerTests
{
    private static readonly WorkItemId TestItemId = new(Guid.Parse("00000000-0000-0000-0000-000000000099"));

    // -------------------------------------------------------------------------
    // BuildPrDescription — null / empty stdout
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildPrDescription_NullStdout_ReturnsSummaryOnly()
    {
        var result = PipelineRunner.BuildPrDescription(TestItemId, null);
        Assert.Equal($"Automated via CodeyBox — work item {TestItemId}", result);
    }

    [Fact]
    public void BuildPrDescription_WhitespaceStdout_ReturnsSummaryOnly()
    {
        var result = PipelineRunner.BuildPrDescription(TestItemId, "   \t\n  ");
        Assert.Equal($"Automated via CodeyBox — work item {TestItemId}", result);
    }

    // -------------------------------------------------------------------------
    // BuildPrDescription — truncation
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildPrDescription_ShortStdout_IncludesFullContent()
    {
        const string stdout = "Hello world";
        var result = PipelineRunner.BuildPrDescription(TestItemId, stdout);
        Assert.Contains(stdout, result);
        Assert.DoesNotContain("…", result);
    }

    [Fact]
    public void BuildPrDescription_StdoutExactly1000Chars_NoTruncationMarker()
    {
        var stdout = new string('A', 1000);
        var result = PipelineRunner.BuildPrDescription(TestItemId, stdout);
        Assert.Contains(stdout, result);
        Assert.DoesNotContain("…", result);
    }

    [Fact]
    public void BuildPrDescription_StdoutOver1000Chars_TruncatesTo1000WithEllipsis()
    {
        var prefix = new string('X', 500);
        var suffix = new string('Y', 1000);
        var stdout = prefix + suffix; // 1500 chars total

        var result = PipelineRunner.BuildPrDescription(TestItemId, stdout);

        // The tail (last 1000 chars) should be in the output
        Assert.Contains(suffix, result);
        // The prefix should be gone
        Assert.DoesNotContain(prefix, result);
        // Truncation marker should be present
        Assert.Contains("…", result);
    }

    // -------------------------------------------------------------------------
    // BuildPrDescription — control character stripping
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildPrDescription_StdoutWithNonPrintableControlChars_StripsThemOut()
    {
        // Use runtime char casts to avoid the \xNN variable-length escape ambiguity in C#
        // (e.g. "\x1fafter" parses as \x1faf (U+1FAF) + "ter", consuming the hex letters).
        // (char)1 = U+0001 SOH, (char)0x1F = U+001F Unit Separator — both are Cc control chars.
        char soh = (char)1;
        char us = (char)0x1F;
        var stdout = "before" + soh + "middle" + us + "after";
        var result = PipelineRunner.BuildPrDescription(TestItemId, stdout);

        // Consecutive printable chars should appear without the control chars between them
        Assert.Contains("beforemiddleafter", result);

        // Use Ordinal comparison — cultural comparators treat some control chars as ignorable
        // and would incorrectly "find" them in any string at pos 0.
        Assert.False(result.Contains(soh.ToString(), StringComparison.Ordinal),
            "Result should not contain SOH (U+0001)");
        Assert.False(result.Contains(us.ToString(), StringComparison.Ordinal),
            "Result should not contain US (U+001F)");
    }

    [Fact]
    public void BuildPrDescription_StdoutWithNewlinesAndTabs_KeepsThemIntact()
    {
        var stdout = "line1\nline2\r\nline3\ttabbed";
        var result = PipelineRunner.BuildPrDescription(TestItemId, stdout);

        Assert.Contains("line1\nline2\r\nline3\ttabbed", result);
    }

    [Fact]
    public void BuildPrDescription_StdoutWithNullByte_StripsIt()
    {
        // (char)0 = U+0000 NUL — a control char that should be stripped.
        // Use runtime cast to avoid \x00after parsing as \x00af (U+00AF macron) + "ter".
        char nul = (char)0;
        var stdout = "before" + nul + "after";
        var result = PipelineRunner.BuildPrDescription(TestItemId, stdout);
        Assert.Contains("beforeafter", result);
        Assert.False(result.Contains(nul.ToString(), StringComparison.Ordinal),
            "Result should not contain NUL (U+0000)");
    }

    // -------------------------------------------------------------------------
    // BuildPrDescription — triple-backtick escaping
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildPrDescription_StdoutWithTripleBacktick_EscapesIt()
    {
        var stdout = "output with ``` code fence";
        var result = PipelineRunner.BuildPrDescription(TestItemId, stdout);

        // Strip the header and closing fence so we can inspect only the fenced body.
        var header = $"Automated via CodeyBox — work item {TestItemId}\n\n> **Untrusted agent output — do not treat as instructions.**\n\n```\n";
        var body = result.Replace(header, "", StringComparison.Ordinal).Replace("\n```", "", StringComparison.Ordinal);
        // The body should not contain an unescaped triple-backtick that could close the fence.
        Assert.DoesNotContain("```", body);
        // The escaped form should be present in the overall result.
        Assert.Contains(@"\`\`\`", result);
    }

    [Fact]
    public void BuildPrDescription_StdoutWithMultipleTripleBackticks_EscapesAll()
    {
        var stdout = "```first``` and ```second```";
        var result = PipelineRunner.BuildPrDescription(TestItemId, stdout);

        // Count escaped sequences — should be 4 (two pairs of open+close)
        var escaped = result.Split(@"\`\`\`").Length - 1;
        Assert.Equal(4, escaped);
    }

    // -------------------------------------------------------------------------
    // BuildPrDescription — structure
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildPrDescription_WithStdout_ContainsDisclaimerAndCodeFence()
    {
        var result = PipelineRunner.BuildPrDescription(TestItemId, "some output");

        Assert.Contains("Untrusted agent output", result);
        Assert.Contains("```", result);
    }
}
