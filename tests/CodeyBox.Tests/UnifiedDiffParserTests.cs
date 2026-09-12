using CodeyBox.Audit;

namespace CodeyBox.Tests;

public sealed class UnifiedDiffParserTests
{
    [Fact]
    public void ParseAddedLines_TracksFileAndNewLineNumbers()
    {
        var diff =
            "+++ b/src/Foo.cs\n" +
            "@@ -9,0 +10,2 @@\n" +
            "+var a = 1;\n" +
            "+var b = 2;\n";

        var added = UnifiedDiffParser.ParseAddedLines(diff);

        Assert.Collection(added,
            l => { Assert.Equal("src/Foo.cs", l.File); Assert.Equal(10, l.NewLine); Assert.Equal("var a = 1;", l.Content); },
            l => { Assert.Equal("src/Foo.cs", l.File); Assert.Equal(11, l.NewLine); Assert.Equal("var b = 2;", l.Content); });
    }

    [Fact]
    public void ParseAddedLines_DeletionsConsumeNoNewLineNumber()
    {
        // A modified line shows as -old then +new; the +new keeps the new-file
        // coordinate. A pure deletion advances nothing.
        var diff =
            "+++ b/x.py\n" +
            "@@ -5,2 +5,1 @@\n" +
            "-import old\n" +
            "-import gone\n" +
            "+import new\n";

        var added = UnifiedDiffParser.ParseAddedLines(diff);

        var line = Assert.Single(added);
        Assert.Equal("x.py", line.File);
        Assert.Equal(5, line.NewLine);
        Assert.Equal("import new", line.Content);
    }

    [Fact]
    public void ParseAddedLines_MultipleFilesAndHunks()
    {
        var diff =
            "+++ b/a.cs\n" +
            "@@ -1,0 +1 @@\n" +
            "+one\n" +
            "+++ b/b.cs\n" +
            "@@ -3,0 +4 @@\n" +
            "+four\n" +
            "@@ -9,0 +10 @@\n" +
            "+ten\n";

        var byFile = UnifiedDiffParser.ChangedLinesByFile(diff);

        Assert.Equal(new[] { 1 }, byFile["a.cs"]);
        Assert.Equal(new[] { 4, 10 }, byFile["b.cs"]);
    }

    [Fact]
    public void ChangedLinesByFile_EmptyDiffIsEmpty()
        => Assert.Empty(UnifiedDiffParser.ChangedLinesByFile(""));

    [Fact]
    public void ParseAddedLines_IgnoresDevNullNewFileHeaderForDeletion()
    {
        // Deleted file: "+++ /dev/null" is not "+++ b/…", so no added lines are
        // attributed to a file.
        var diff =
            "--- a/gone.cs\n" +
            "+++ /dev/null\n" +
            "@@ -1,2 +0,0 @@\n" +
            "-line one\n" +
            "-line two\n";

        Assert.Empty(UnifiedDiffParser.ParseAddedLines(diff));
    }
}
