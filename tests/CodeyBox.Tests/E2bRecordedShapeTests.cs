using System.Net;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.E2bSandboxPlugin;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Recorded-shape pins for the E2B provider contract: exact control-plane
/// paths and methods, auth-header routing per plane, preview-URL gating and
/// host format, and option validation. A single production mutation in any
/// pinned behavior flips one of these red.
/// </summary>
public sealed class E2bRecordedShapeTests
{
    private static E2bSandboxOptions TestOptions() => new()
    {
        ApiKey = "test-key",
        ApiBaseUrl = "https://api.e2b.dev",
        WaitForRunningTimeout = TimeSpan.FromSeconds(10),
        StatusPollInterval = TimeSpan.FromMilliseconds(10),
        ApiTimeout = TimeSpan.FromSeconds(5),
    };

    private static E2bSandboxProvider NewProvider(FakeE2bHandler handler, E2bSandboxOptions? opts = null)
    {
        var options = opts ?? TestOptions();
        return new E2bSandboxProvider(
            () => options,
            new HttpClient(handler),
            TimeProvider.System,
            NullLogger.Instance);
    }

    private static SandboxSpec BasicSpec() => new()
    {
        ImageReference = "ignored",
        WorkingDirectory = "/work",
    };

    [Fact]
    public async Task CreateAsync_RecordedControlPlaneShape()
    {
        var handler = new FakeE2bHandler();
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);

        // Exact method + path contract against the control plane.
        var create = Assert.Single(handler.Requests, r => r.Path == "/v2/sandboxes");
        Assert.Equal(HttpMethod.Post, create.Method);
        var waits = handler.Requests.Where(r => r.Method == HttpMethod.Get && r.Path == $"/sandboxes/{sandbox.Id}").ToList();
        Assert.NotEmpty(waits);
        var timeout = Assert.Single(handler.Requests, r => r.Path == $"/sandboxes/{sandbox.Id}/timeout");
        Assert.Equal(HttpMethod.Post, timeout.Method);
        var timeoutBody = JsonDocument.Parse(timeout.Body).RootElement;
        Assert.True(timeoutBody.TryGetProperty("timeout", out _));

        // The API key rides control-plane traffic only.
        Assert.All(
            handler.Requests.Where(r => !r.Path.StartsWith("/files", StringComparison.Ordinal) && r.Path is not ("/commands" or "/health")),
            r => Assert.Equal("test-key", r.ApiKey));

        // Data-plane traffic carries the sandbox-scoped token, never the API key.
        var dataPlane = handler.Requests.Where(r => r.Host.Contains(".e2b.app", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(dataPlane);
        Assert.All(dataPlane, r =>
        {
            Assert.Null(r.ApiKey);
            Assert.Equal($"envd_token_{sandbox.Id}", r.AccessToken);
        });
    }

    [Fact]
    public async Task PreviewUrls_DisabledByDefault_RefusePublishing()
    {
        var handler = new FakeE2bHandler();
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var publisher = Assert.IsAssignableFrom<ISandboxPortPublisher>(sandbox);

        Assert.False(publisher.CanPublishPort(3000));
        var ex = Assert.Throws<InvalidOperationException>(() => publisher.PublishPort(3000));
        Assert.Contains("EnablePreviewUrls", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewUrls_Enabled_PublishVerifiedHostFormat()
    {
        var handler = new FakeE2bHandler();
        var provider = NewProvider(handler, TestOptions() with
        {
            EnablePreviewUrls = true,
            AllowedPreviewPorts = [3000],
        });

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var publisher = Assert.IsAssignableFrom<ISandboxPortPublisher>(sandbox);

        Assert.True(publisher.CanPublishPort(3000));
        Assert.False(publisher.CanPublishPort(8080));

        var published = publisher.PublishPort(3000);
        Assert.Equal($"3000-{sandbox.Id}.e2b.app", published.Host);
        Assert.NotNull(published.Metadata);
        Assert.True(published.Metadata.TryGetValue("url", out var previewUrl));
        Assert.Equal($"https://3000-{sandbox.Id}.e2b.app/", previewUrl);

        var denied = Assert.Throws<InvalidOperationException>(() => publisher.PublishPort(8080));
        Assert.Contains("AllowedPreviewPorts", denied.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_UnknownDomain_Refused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            E2bSandboxProvider.ValidateOptions(TestOptions() with { SandboxDomain = "evil.example" }));
    }

    [Fact]
    public void Options_PreviewWithoutAllowlist_Refused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            E2bSandboxProvider.ValidateOptions(TestOptions() with { EnablePreviewUrls = true }));
    }

    [Fact]
    public void Options_CleartextBaseUrl_Refused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            E2bSandboxProvider.ValidateOptions(TestOptions() with { ApiBaseUrl = "http://api.e2b.dev" }));
    }

    [Fact]
    public void Options_LoopbackHttp_AllowedOnlyWithEscapeHatch()
    {
        E2bSandboxProvider.ValidateOptions(TestOptions() with
        {
            ApiBaseUrl = "http://127.0.0.1:9",
            AllowUnsafeHttp = true,
        });
        Assert.Throws<InvalidOperationException>(() =>
            E2bSandboxProvider.ValidateOptions(TestOptions() with { ApiBaseUrl = "http://127.0.0.1:9" }));
    }

    [Fact]
    public void Options_BadNamePrefix_Refused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            E2bSandboxProvider.ValidateOptions(TestOptions() with { NamePrefix = "9bad" }));
        Assert.Throws<InvalidOperationException>(() =>
            E2bSandboxProvider.ValidateOptions(TestOptions() with { NamePrefix = "bad prefix" }));
    }

    [Fact]
    public void EnvdBaseUrl_DerivesVerifiedHostFormat()
    {
        Assert.Equal(
            "https://49983-sb_abc123.e2b.app",
            E2bSandboxProvider.EnvdBaseUrl(TestOptions(), "sb_abc123"));
    }

    [Theory]
    [InlineData("x.evil.com#")]
    [InlineData("a/b")]
    [InlineData("a?b=c")]
    [InlineData("a b")]
    [InlineData("a:b")]
    [InlineData("a@b")]
    [InlineData("")]
    [InlineData("sb!abc")]
    public void EnvdBaseUrl_RejectsDnsBreakoutIds(string sandboxId)
    {
        Assert.Throws<ArgumentException>(() =>
            E2bSandboxProvider.EnvdBaseUrl(TestOptions(), sandboxId));
    }

    [Theory]
    [InlineData("sb_abc123")]
    [InlineData("ABC-123_xyz")]
    [InlineData("a")]
    public void SandboxId_AllowlistedIds_Accepted(string sandboxId)
    {
        Assert.True(E2bSandboxProvider.IsValidSandboxId(sandboxId));
        Assert.False(string.IsNullOrWhiteSpace(
            E2bSandboxProvider.EnvdBaseUrl(TestOptions(), sandboxId)));
    }

    [Fact]
    public void SandboxId_OverlongId_Rejected()
    {
        Assert.False(E2bSandboxProvider.IsValidSandboxId(new string('a', 129)));
        Assert.Throws<ArgumentException>(() =>
            E2bSandboxProvider.EnvdBaseUrl(TestOptions(), new string('a', 129)));
    }

    [Fact]
    public async Task ListSandboxes_OversizeBody_RejectedBeforeBuffering()
    {
        var handler = new OversizeControlPlaneHandler();
        var client = new E2bApiClient(new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<E2bApiException>(
            () => client.ListSandboxesAsync("https://api.e2b.dev", "test-key", TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal("oversize-response", ex.ErrorClass);
    }

    [Fact]
    public async Task CreateAsync_MissingAccessToken_DeletesAndDefers()
    {
        var handler = new FakeE2bHandler { OmitAccessToken = true };
        var provider = NewProvider(handler);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => provider.CreateAsync(BasicSpec(), CancellationToken.None));
        Assert.True(SandboxDeferralGuard.IsDeferral(ex));
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Delete);
    }

    /// <summary>
    /// Declares a huge Content-Length with a tiny body: an unbounded reader
    /// would trust the stream, a bounded reader rejects on the pre-check.
    /// </summary>
    private sealed class OversizeControlPlaneHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new ByteArrayContent("[]"u8.ToArray());
            content.Headers.ContentLength = 8L * 1024 * 1024;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
