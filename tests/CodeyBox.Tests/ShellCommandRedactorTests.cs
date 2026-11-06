using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class ShellCommandRedactorTests
{
    [Theory]
    [InlineData("https://user:p@ss@host/repo", "https://***@host/repo")]
    [InlineData("https://user:p'ass@host/repo", "https://***@host/repo")]
    [InlineData("https://user:p&ss@host/repo", "https://***@host/repo")]
    [InlineData("https://user:p;ass@host/repo", "https://***@host/repo")]
    [InlineData("https://user:p(ass)@host/repo", "https://***@host/repo")]
    [InlineData("https://user:pass@127.0.0.1:1/repo.git", "https://***@127.0.0.1:1/repo.git")]
    [InlineData("ssh://git@github.com/org/repo.git", "ssh://***@github.com/org/repo.git")]
    [InlineData("git@github.com:org/repo.git", "git@github.com:org/repo.git")]
    [InlineData(
        "https://user:pass@host failed for git@github.com:org/repo",
        "https://***@host failed for git@github.com:org/repo")]
    [InlineData(
        "see \"https://user:p'ass@host/repo\" now",
        "see \"https://***@host/repo\" now")]
    public void Strip_UsesTheLastAtAndKeepsSubDelimitersInsideUserInfo(string text, string expected)
    {
        Assert.Equal(expected, EmbeddedUrlUserInfo.Strip(text));
    }

    [Fact]
    public void StripToken_KeepsSpacesThatWereInsideThePassword()
    {
        Assert.Equal(
            "https://***@host/repo",
            EmbeddedUrlUserInfo.StripToken("https://user:my pass@host/repo"));
    }

    [Theory]
    [InlineData(
        "curl -fsSLu builder:pass https://example.invalid/install.sh",
        "curl -fsSLu *** https://example.invalid/install.sh")]
    [InlineData(
        "curl -fsSLubuilder:pass https://example.invalid/install.sh",
        "curl -fsSLu *** https://example.invalid/install.sh")]
    [InlineData(
        "curl -u builder:pass https://example.invalid/install.sh",
        "curl -u *** https://example.invalid/install.sh")]
    [InlineData(
        "sh -c \"curl -u builder:pass https://example.invalid/install.sh\"",
        "sh -c \"curl -u *** https://example.invalid/install.sh\"")]
    [InlineData(
        "sh -c \"npm config set //registry.npmjs.org/:_authToken npm_token\"",
        "sh -c \"npm config set //registry.npmjs.org/:_authToken ***\"")]
    [InlineData(
        "docker login -u builder -p pass",
        "docker login -u *** -p ***")]
    [InlineData(
        "npm config set //registry.npmjs.org/:_auth authvalue",
        "npm config set //registry.npmjs.org/:_auth ***")]
    [InlineData(
        "npm config set //registry.npmjs.org/:_auth=authvalue",
        "npm config set //registry.npmjs.org/:_auth=***")]
    [InlineData("\"--user\"builder:pass", "\"--user\"***")]
    [InlineData(
        "curl \"https://user:p'ass@registry.example/install.sh\"",
        "curl \"https://***@registry.example/install.sh\"")]
    [InlineData(
        "curl \"https://user:p&ss@registry.example/install.sh\"",
        "curl \"https://***@registry.example/install.sh\"")]
    [InlineData(
        "curl https://user:my\\ pass@registry.example/install.sh",
        "curl https://***@registry.example/install.sh")]
    [InlineData(
        "curl https://user:p\\&ss@registry.example/install.sh",
        "curl https://***@registry.example/install.sh")]
    [InlineData(
        "curl https://example.invalid/install.sh && export NPM_TOKEN=npm_token",
        "curl https://example.invalid/install.sh && export NPM_TOKEN=***")]
    [InlineData(
        "curl --user-agent CodeyBox https://example.invalid/install.sh",
        "curl --user-agent CodeyBox https://example.invalid/install.sh")]
    [InlineData("curl -update archive", "curl -update archive")]
    [InlineData("git clone git@github.com:org/repo.git", "git clone git@github.com:org/repo.git")]
    public void Redact_RemovesShellCredentials(string command, string expected)
    {
        var redacted = ShellCommandRedactor.Redact(command);
        Assert.Equal(expected, redacted);
        Assert.DoesNotContain("builder:pass", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("npm_token", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("authvalue", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_EmptyAndNullStayEmpty()
    {
        Assert.Equal("", ShellCommandRedactor.Redact(null));
        Assert.Equal("", ShellCommandRedactor.Redact(""));
        Assert.Equal("", EmbeddedUrlUserInfo.Strip(null));
        Assert.Equal("", EmbeddedUrlUserInfo.StripToken(null));
    }
}
