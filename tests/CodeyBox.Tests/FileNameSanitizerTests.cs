using CodeyBox.Api;

namespace CodeyBox.Tests;

public sealed class FileNameSanitizerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData(" ")]
    public void Sanitize_ReturnsNull_ForEmptyOrReserved(string? input) =>
        Assert.Null(FileNameSanitizer.Sanitize(input));

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\etc\\passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\file.txt")]
    [InlineData("C:\\foo\\bar.txt")]
    [InlineData("a/b/c.png")]
    [InlineData("a\\b\\c.png")]
    public void Sanitize_StripsPathComponents(string input)
    {
        var result = FileNameSanitizer.Sanitize(input);
        Assert.NotNull(result);
        Assert.DoesNotContain('/', result!);
        Assert.DoesNotContain('\\', result!);
    }

    [Fact]
    public void Sanitize_ReplacesControlCharacters()
    {
        var result = FileNameSanitizer.Sanitize("file\u0000name\nwith\rctrl");
        Assert.NotNull(result);
        Assert.Equal("file_name_with_ctrl", result);
    }

    [Theory]
    [InlineData("file.", "file")]
    [InlineData("file..", "file")]
    [InlineData(".file", "file")]
    [InlineData("..file", "file")]
    [InlineData("... ...", null)] // collapses to a single space after trim-dot → whitespace-only → null
    public void Sanitize_TrimsTrailingAndLeadingDots(string input, string? expected) =>
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));

    [Theory]
    [InlineData("--allow-write", "_--allow-write")]
    [InlineData("-rf", "_-rf")]
    [InlineData("-a", "_-a")]
    [InlineData("normal.txt", "normal.txt")]
    public void Sanitize_PrefixesLeadingDash(string input, string expected) =>
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));

    [Theory]
    [InlineData("con")]
    [InlineData("file:name.txt")]
    [InlineData("file*name.txt")]
    [InlineData("file?name.txt")]
    [InlineData("file\"name.txt")]
    [InlineData("file<name>.txt")]
    [InlineData("file|name.txt")]
    public void Sanitize_ReplacesSpecialCharacters(string input)
    {
        var result = FileNameSanitizer.Sanitize(input);
        Assert.NotNull(result);
        // Every shell/header-special character must be neutralised to '_'.
        Assert.DoesNotContain(':', result!);
        Assert.DoesNotContain('*', result!);
        Assert.DoesNotContain('?', result!);
        Assert.DoesNotContain('"', result!);
        Assert.DoesNotContain('<', result!);
        Assert.DoesNotContain('>', result!);
        Assert.DoesNotContain('|', result!);
    }

    [Fact]
    public void Sanitize_PreservesUnicodeGlyphs()
    {
        var result = FileNameSanitizer.Sanitize("résumé-курсор-Δ.txt");
        Assert.Equal("résumé-курсор-Δ.txt", result);
    }
}
