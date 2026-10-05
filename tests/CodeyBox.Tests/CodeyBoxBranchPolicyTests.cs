using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Ownership guard for lease-guarded rewrites: only <c>codeybox/*</c>
/// branches may be force-pushed, matched by exact ordinal prefix.
/// </summary>
public sealed class CodeyBoxBranchPolicyTests
{
    [Theory]
    [InlineData("codeybox/abc123")]
    [InlineData("codeybox/a")]
    [InlineData("codeybox/feature/with-slashes")]
    public void IsOwnedWorkBranch_OwnedNames_ReturnsTrue(string branch)
        => Assert.True(CodeyBoxBranchPolicy.IsOwnedWorkBranch(branch));

    [Theory]
    [InlineData("main")]
    [InlineData("codeybox/")]
    [InlineData("codeybox")]
    [InlineData("Codeybox/abc")]
    [InlineData("CODEYBOX/abc")]
    [InlineData("xcodeybox/abc")]
    [InlineData("feature/codeybox/abc")]
    [InlineData("")]
    public void IsOwnedWorkBranch_UnownedNames_ReturnsFalse(string branch)
        => Assert.False(CodeyBoxBranchPolicy.IsOwnedWorkBranch(branch));

    [Fact]
    public void IsOwnedWorkBranch_Null_ReturnsFalse()
        => Assert.False(CodeyBoxBranchPolicy.IsOwnedWorkBranch(null));

    [Fact]
    public void RequireOwnedWorkBranch_OwnedBranch_DoesNotThrow()
        => CodeyBoxBranchPolicy.RequireOwnedWorkBranch("codeybox/e30ba075");

    [Theory]
    [InlineData("main")]
    [InlineData("release/1.0")]
    [InlineData("codeybox/")]
    [InlineData("Codeybox/abc")]
    public void RequireOwnedWorkBranch_UnownedBranch_ThrowsArgumentException(string branch)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => CodeyBoxBranchPolicy.RequireOwnedWorkBranch(branch));
        Assert.Contains("codeybox/", ex.Message);
    }
}
