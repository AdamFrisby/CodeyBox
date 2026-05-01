using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class FindingIdStabilityTests
{
    [Fact]
    public void SameInputs_ProduceSameId()
    {
        var id1 = FindingIdComputer.Compute("Lint", "Missing null check", ["src/Foo.cs"]);
        var id2 = FindingIdComputer.Compute("Lint", "Missing null check", ["src/Foo.cs"]);
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void Id_HasExpectedPrefix()
    {
        var id = FindingIdComputer.Compute("Lint", "Some title", []);
        Assert.StartsWith("f-", id);
        Assert.Equal(10, id.Length); // "f-" + 8 hex chars
    }

    [Fact]
    public void Id_IsLowercaseHex()
    {
        var id = FindingIdComputer.Compute("A", "B", []);
        Assert.Matches("^f-[0-9a-f]{8}$", id);
    }

    [Fact]
    public void DifferentAuditor_ProducesDifferentId()
    {
        var id1 = FindingIdComputer.Compute("Lint", "Missing null check", []);
        var id2 = FindingIdComputer.Compute("Security", "Missing null check", []);
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void DifferentTitle_ProducesDifferentId()
    {
        var id1 = FindingIdComputer.Compute("Lint", "Missing null check", []);
        var id2 = FindingIdComputer.Compute("Lint", "Unused variable", []);
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void DifferentFiles_ProducesDifferentId()
    {
        var id1 = FindingIdComputer.Compute("Lint", "Missing null check", ["src/A.cs"]);
        var id2 = FindingIdComputer.Compute("Lint", "Missing null check", ["src/B.cs"]);
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void FileOrder_DoesNotAffectId()
    {
        var id1 = FindingIdComputer.Compute("Lint", "Title", ["src/A.cs", "src/B.cs"]);
        var id2 = FindingIdComputer.Compute("Lint", "Title", ["src/B.cs", "src/A.cs"]);
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void EmptyFiles_Vs_OneFile_DifferentIds()
    {
        var id1 = FindingIdComputer.Compute("Lint", "Title", []);
        var id2 = FindingIdComputer.Compute("Lint", "Title", ["src/A.cs"]);
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void TitleNormalization_StripsFileReference_SameId()
    {
        // "Missing return in src/Foo.cs" and "Missing return" should normalise to the same title
        var id1 = FindingIdComputer.Compute("Lint", "Missing return in src/Foo.cs", []);
        var id2 = FindingIdComputer.Compute("Lint", "Missing return", []);
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void TitleNormalization_StripsLineRef_SameId()
    {
        // "line 42" matches the \bline\s+\d+\b pattern and is stripped entirely
        var id1 = FindingIdComputer.Compute("Lint", "Hardcoded secret line 42", []);
        var id2 = FindingIdComputer.Compute("Lint", "Hardcoded secret", []);
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void TitleNormalization_CaseInsensitive_SameId()
    {
        var id1 = FindingIdComputer.Compute("Lint", "Missing Null Check", []);
        var id2 = FindingIdComputer.Compute("Lint", "missing null check", []);
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void TitleNormalization_CollapseWhitespace_SameId()
    {
        var id1 = FindingIdComputer.Compute("Lint", "Missing  null   check", []);
        var id2 = FindingIdComputer.Compute("Lint", "Missing null check", []);
        Assert.Equal(id1, id2);
    }
}
