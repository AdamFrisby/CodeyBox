using System.Net;
using System.Security.Cryptography;
using System.Text;
using CodeyBox.Upstream.GitHub;

namespace CodeyBox.Tests;

public sealed class GitHubAppTokenProviderTests : IDisposable
{
    private readonly string _keyPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-github-app-{Guid.NewGuid():N}.pem");

    [Fact]
    public async Task GetTokenAsync_UsesAppJwtAndCachesInstallationToken()
    {
        using (var rsa = RSA.Create(2048))
        {
            await File.WriteAllTextAsync(_keyPath, rsa.ExportRSAPrivateKeyPem());
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(
                """{"token":"installation-token","expires_at":"2026-07-29T12:00:00Z"}""",
                Encoding.UTF8,
                "application/json"),
        });
        var factory = new FakeHttpClientFactory(handler, userAgent: "CodeyBox");
        using var provider = new GitHubAppTokenProvider(
            factory,
            new GitHubAppTokenOptions(123, 456, _keyPath),
            new FixedTimeProvider(DateTimeOffset.Parse(
                "2026-07-29T11:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture)));

        var first = await provider.GetTokenAsync();
        var second = await provider.GetTokenAsync();

        Assert.Equal("installation-token", first);
        Assert.Equal(first, second);
        Assert.Single(handler.Requests);
        Assert.Equal(
            "https://api.github.com/app/installations/456/access_tokens",
            handler.Requests[0].RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization!.Scheme);
        Assert.Equal(3, handler.Requests[0].Headers.Authorization!.Parameter!.Split('.').Length);
    }

    public void Dispose()
    {
        try { File.Delete(_keyPath); } catch { }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
