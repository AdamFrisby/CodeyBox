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

    // ValidateWebhookUrl — valid inputs
    [Theory]
    [InlineData("https://hooks.example.com/webhook")]
    [InlineData("http://hooks.example.com/webhook")]
    [InlineData("https://example.com:8443/path?q=1")]
    [InlineData("HTTPS://EXAMPLE.COM/HOOK")]  // scheme case-insensitive
    public void ValidWebhookUrls(string url) => Validation.ValidateWebhookUrl(url, "url");

    // ValidateWebhookUrl — rejected inputs
    [Theory]
    // option-like / control
    [InlineData("-https://example.com")]
    [InlineData("https://example.com\nevil")]
    // non-http/https schemes
    [InlineData("ftp://example.com/hook")]
    [InlineData("git://example.com/hook")]
    [InlineData("file:///etc/passwd")]
    // empty / blank
    [InlineData("")]
    [InlineData("   ")]
    // loopback
    [InlineData("http://127.0.0.1/hook")]
    [InlineData("http://[::1]/hook")]
    // RFC-1918
    [InlineData("http://10.0.0.1/hook")]
    [InlineData("http://10.255.255.255/hook")]
    [InlineData("http://172.16.0.1/hook")]
    [InlineData("http://172.31.255.255/hook")]
    [InlineData("http://192.168.1.1/hook")]
    // link-local / cloud metadata
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://169.254.0.1/hook")]
    // ULA IPv6
    [InlineData("http://[fc00::1]/hook")]
    [InlineData("http://[fd00:ec2::254]/hook")]
    // link-local IPv6
    [InlineData("http://[fe80::1]/hook")]
    // IPv4-mapped IPv6 bypasses (the key SSRF vectors)
    [InlineData("http://[::ffff:10.0.0.1]/hook")]
    [InlineData("http://[::ffff:192.168.1.1]/hook")]
    [InlineData("http://[::ffff:172.16.0.1]/hook")]
    [InlineData("http://[::ffff:169.254.169.254]/latest/meta-data/")]
    [InlineData("http://[::ffff:127.0.0.1]/hook")]
    // reserved internal hostnames
    [InlineData("https://localhost/hook")]
    [InlineData("https://LOCALHOST/hook")]
    [InlineData("https://metadata.google.internal/hook")]
    [InlineData("https://foo.internal/hook")]
    [InlineData("https://bar.local/hook")]
    public void RejectedWebhookUrls(string url)
        => Assert.Throws<ArgumentException>(() => Validation.ValidateWebhookUrl(url, "url"));
}
