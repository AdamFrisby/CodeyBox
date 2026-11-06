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
    [InlineData(
        "curl \"https://user:my pass@host/repo\"",
        "curl \"https://***@host/repo\"")]
    [InlineData(
        "curl 'https://user:my pass@host/repo'",
        "curl 'https://***@host/repo'")]
    [InlineData(
        "curl \"https://user:\"'p&ss'\"@host/repo\"",
        "curl \"https://***@host/repo\"")]
    [InlineData(
        "curl https://user:\"p&ss\"@host/repo",
        "curl https://***@host/repo")]
    [InlineData(
        "curl \"https://user:my secret@host/repo\" extra",
        "curl \"https://***@host/repo\" extra")]
    [InlineData("echo \"hello\"'world'", "echo \"hello\"'world'")]
    [InlineData(
        "curl -fsSL https://example.invalid/install.sh -H \"Authorization: Basic dXNlcjpwYXNz\"",
        "curl -fsSL https://example.invalid/install.sh -H \"Authorization: *** ***\"")]
    [InlineData(
        "curl https://example.invalid/install.sh -H \"Authorization:\" Bearer s3cret",
        "curl https://example.invalid/install.sh -H \"Authorization:\" *** ***")]
    [InlineData(
        "curl https://example.invalid/install.sh -H \"Authorization: Basic\" dXNlcjpwYXNz",
        "curl https://example.invalid/install.sh -H \"Authorization: ***\" ***")]
    [InlineData(
        "curl https://example.invalid/install.sh Authorization:Basic dXNlcjpwYXNz",
        "curl https://example.invalid/install.sh Authorization:*** ***")]
    [InlineData(
        "curl https://example.invalid/install.sh --header Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.x.y extra",
        "curl https://example.invalid/install.sh --header Authorization: *** *** extra")]
    [InlineData(
        "curl https://example.invalid/install.sh -H \"Authorization:Bearer\" dXNlcjpwYXNz",
        "curl https://example.invalid/install.sh -H \"Authorization:***\" ***")]
    [InlineData(
        "curl https://example.invalid/install.sh -H X-Api-Key: supersecret",
        "curl https://example.invalid/install.sh -H X-Api-Key: ***")]
    [InlineData(
        "curl https://example.invalid/install.sh Password: supersecret extra",
        "curl https://example.invalid/install.sh Password: *** extra")]
    [InlineData("mysql -ppassword dbname", "mysql -p *** dbname")]
    [InlineData("docker login -ps3cret registry.example", "docker login -p *** registry.example")]
    [InlineData(
        "curl -fp s3cret https://example.invalid/api",
        "curl -fp *** https://example.invalid/api")]
    [InlineData(
        "curl -ps3cret https://example.invalid/api",
        "curl -p *** https://example.invalid/api")]
    [InlineData(
        "curl -fps3cret https://example.invalid/api",
        "curl -fp *** https://example.invalid/api")]
    [InlineData(
        "curl --oauth2-bearer s3cret https://example.invalid/api",
        "curl --oauth2-bearer *** https://example.invalid/api")]
    [InlineData(
        "curl --oauth2-bearer=s3cret https://example.invalid/api",
        "curl --oauth2-bearer=*** https://example.invalid/api")]
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

    [Fact]
    public void Redact_StripsSpacedUserInfoAtTheQuoteDepthCap()
    {
        var command = "https://user:my pass@host/repo";
        for (var i = 0; i <= ShellCommandRedactor.MaxQuotedCommandDepth; i++)
            command = "\"" + command.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

        var redacted = ShellCommandRedactor.Redact(command);
        Assert.DoesNotContain("my pass", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("user:my", redacted, StringComparison.Ordinal);
        Assert.Contains("https://***@host/repo", redacted, StringComparison.Ordinal);
    }
}
