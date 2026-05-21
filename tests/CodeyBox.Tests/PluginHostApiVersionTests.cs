using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class PluginHostApiVersionTests
{
    [Fact]
    public void Current_IsNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(CodeyBoxApiVersion.Current));
    }

    [Fact]
    public void Satisfies_ExactMatch_ReturnsTrue()
    {
        Assert.True(CodeyBoxApiVersion.Satisfies(CodeyBoxApiVersion.Current));
    }

    [Fact]
    public void Satisfies_MinorLowerThanCurrent_ReturnsTrue()
    {
        // Host 1.1 satisfies plugin requiring 1.0 (same or lower minor)
        Assert.True(CodeyBoxApiVersion.Satisfies("1.0"));
    }

    [Theory]
    [InlineData("99.0")]
    [InlineData("2.0")]
    public void Satisfies_IncompatibleMajor_ReturnsFalse(string pluginMin)
    {
        Assert.False(CodeyBoxApiVersion.Satisfies(pluginMin));
    }

    [Fact]
    public void Satisfies_HigherMinor_ReturnsFalse()
    {
        // Plugin wants 1.99 but host is 1.1
        Assert.False(CodeyBoxApiVersion.Satisfies("1.99"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("notaversion")]
    [InlineData("1")]
    [InlineData(".1")]
    public void Satisfies_MalformedVersion_ReturnsFalse(string version)
    {
        Assert.False(CodeyBoxApiVersion.Satisfies(version));
    }
}
