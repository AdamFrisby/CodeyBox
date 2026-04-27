using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class ValidationTests
{
    [Theory]
    [InlineData("main")]
    [InlineData("feature/x")]
    [InlineData("codeybox/abc123")]
    [InlineData("v1.2.3")]
    public void ValidBranchNames(string name) => Validation.ValidateBranchName(name, "branch");

    [Theory]
    [InlineData("-evil")]              // leading dash → would be option to git
    [InlineData("../escape")]
    [InlineData("foo..bar")]
    [InlineData("name with space")]
    [InlineData("a.lock")]
    [InlineData(".hidden")]            // we forbid leading dot too
    [InlineData("")]
    public void RejectedBranchNames(string name)
        => Assert.Throws<ArgumentException>(() => Validation.ValidateBranchName(name, "branch"));

    [Theory]
    [InlineData("https://github.com/x/y.git")]
    [InlineData("git@github.com:x/y.git")]
    [InlineData("/var/lib/codeybox/repos/abc.git")]
    [InlineData("ssh://git@example.com/x/y.git")]
    public void ValidRepoUrls(string url) => Validation.ValidateRepositoryUrl(url, "url");

    [Theory]
    [InlineData("--upload-pack=evil")] // git's classic option-injection vector
    [InlineData("-uX")]
    [InlineData("https://x\nmalicious")]
    [InlineData("not-a-url")]
    public void RejectedRepoUrls(string url)
        => Assert.Throws<ArgumentException>(() => Validation.ValidateRepositoryUrl(url, "url"));
}
