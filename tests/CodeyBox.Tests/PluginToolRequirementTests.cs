using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Validator tests for plugin tool declarations: the declaration is untrusted
/// input, so anything that is not a bare binary name / Debian package name
/// fails closed. Each rejected case below would otherwise be a shell- or
/// option-injection vector in a baseline bake.
/// </summary>
public sealed class PluginToolRequirementTests
{
    [Theory]
    [InlineData("dotnet")]
    [InlineData("pytest")]
    [InlineData("node")]
    [InlineData("a")]
    [InlineData("my-tool_2.0")]
    [InlineData("codeybox-sample-scan-tool")]
    [InlineData("X9.-_")]
    public void TryCreate_AcceptsBareBinaryNames(string binary)
    {
        Assert.True(PluginToolRequirement.TryCreate(
            "sample.plugin", binary, null, null, out var requirement, out var error));
        Assert.NotNull(requirement);
        Assert.Null(error);
        Assert.Equal(binary, requirement!.Binary);
    }

    [Theory]
    [InlineData("x; touch /tmp/codeybox-pwned")]   // command chaining
    [InlineData("x && touch /tmp/pwned")]          // command chaining
    [InlineData("x | tee /tmp/pwned")]             // pipe
    [InlineData("$(touch /tmp/pwned)")]            // command substitution
    [InlineData("`touch /tmp/pwned`")]             // backtick substitution
    [InlineData("a b")]                            // word splitting
    [InlineData("a\tb")]                          // word splitting (tab)
    [InlineData("../bin/x")]                       // path traversal
    [InlineData("/usr/bin/x")]                     // absolute path
    [InlineData("C:\\tools\\x")]                   // windows path
    [InlineData("-x")]                             // option injection
    [InlineData("--version")]                      // option injection
    [InlineData("")]                               // empty
    [InlineData(" leading")]                       // whitespace
    [InlineData("trailing ")]                      // whitespace
    [InlineData("x;y")]                            // semicolon
    [InlineData("x\ny")]                           // newline injection
    [InlineData("x${PATH}y")]                      // variable expansion
    [InlineData("x>y")]                            // redirection
    [InlineData("x<y")]                            // redirection
    [InlineData("x*y")]                            // glob
    [InlineData("x?y")]                            // glob
    [InlineData("~x")]                             // tilde expansion
    public void TryCreate_RejectsNonBareBinaryNames(string binary)
    {
        Assert.False(PluginToolRequirement.TryCreate(
            "sample.plugin", binary, null, null, out var requirement, out var error));
        Assert.Null(requirement);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryCreate_RejectsOverlongBinaryName()
    {
        var binary = new string('a', 65);
        Assert.False(PluginToolRequirement.TryCreate(
            "sample.plugin", binary, null, null, out var requirement, out _));
        Assert.Null(requirement);
    }

    [Theory]
    [InlineData("dotnet-sdk-8.0")]
    [InlineData("python3-pytest")]
    [InlineData("nodejs")]
    [InlineData("g++")]
    public void TryCreate_AcceptsValidAptPackages(string package)
    {
        Assert.True(PluginToolRequirement.TryCreate(
            "sample.plugin", "some-tool", package, null, out var requirement, out _));
        Assert.Equal(package, requirement!.AptPackage);
    }

    [Theory]
    [InlineData("pkg; rm -rf /")]   // command chaining
    [InlineData("-evil")]           // option injection (leading dash)
    [InlineData("--yes")]           // option injection
    [InlineData("UPPER")]           // debian names are lowercase
    [InlineData("a b")]             // word splitting
    [InlineData("../../x")]         // path characters
    [InlineData("pkg=x")]           // version pinning syntax is not a name
    [InlineData("pkg\x00name")]     // embedded NUL
    public void TryCreate_RejectsInvalidAptPackages(string package)
    {
        Assert.False(PluginToolRequirement.TryCreate(
            "sample.plugin", "some-tool", package, null, out var requirement, out _));
        Assert.Null(requirement);
    }

    [Fact]
    public void TryCreate_NullPackageMeansVerifyOnly()
    {
        Assert.True(PluginToolRequirement.TryCreate(
            "sample.plugin", "some-tool", null, null, out var requirement, out _));
        Assert.Null(requirement!.AptPackage);
    }

    [Fact]
    public void TryCreate_SanitizesInstallHint_ControlCharsStripped_LengthCapped()
    {
        Assert.True(PluginToolRequirement.TryCreate(
            "sample.plugin", "some-tool", null,
            "install it\x1b[2J\nfrom your feed",
            out var requirement, out _));
        Assert.Equal("install it[2Jfrom your feed", requirement!.InstallHint);

        var longHint = new string('h', PluginToolRequirement.MaxInstallHintLength + 100);
        Assert.True(PluginToolRequirement.TryCreate(
            "sample.plugin", "some-tool", null, longHint, out requirement, out _));
        Assert.Equal(PluginToolRequirement.MaxInstallHintLength, requirement!.InstallHint!.Length);
    }
}
