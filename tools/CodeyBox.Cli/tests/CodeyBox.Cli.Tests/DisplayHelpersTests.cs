using CodeyBox.Cli.Commands;

namespace CodeyBox.Cli.Tests;

public sealed class DisplayHelpersTests
{
    [Fact]
    public void Percent_NegativeValue_ReturnsUnknown()
    {
        Assert.Equal("unknown", DisplayHelpers.Percent(-1));
    }

    [Theory]
    [InlineData("abcdef", 3, "abc")]
    [InlineData("abcdef", 4, "a...")]
    [InlineData("abc", 4, "abc")]
    public void Truncate_UsesConfiguredWidth(string value, int maxLen, string expected)
    {
        Assert.Equal(expected, DisplayHelpers.Truncate(value, maxLen));
    }

    [Fact]
    public void Sanitize_StripsControlCharacters()
    {
        Assert.Equal("abc", DisplayHelpers.Sanitize("a\nb\u001bc"));
    }

    [Fact]
    public void Sanitize_LongString_UsesHeapBuffer()
    {
        var input = "a\n" + new string('x', 1500) + "\u001bb";

        var sanitized = DisplayHelpers.Sanitize(input);

        Assert.Equal("a" + new string('x', 1500) + "b", sanitized);
    }
}
