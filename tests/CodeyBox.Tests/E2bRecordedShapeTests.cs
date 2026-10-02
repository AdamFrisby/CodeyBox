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
}
