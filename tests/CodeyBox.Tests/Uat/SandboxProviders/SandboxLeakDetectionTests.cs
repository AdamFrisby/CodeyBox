using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests.Uat.SandboxProviders;

/// <summary>
/// UAT coverage for <c>Sandbox leak detection and disposal - Finds stale managed sandboxes and exposes operator cleanup</c>.
/// Plan anchor: docs/uat/00-plan.md#sandbox-leak-detection-and-disposal---finds-stale-managed-sandboxes-and-exposes-operator-cleanup
/// </summary>
[Collection("GlobalSerilog")]
public sealed class SandboxLeakDetectionTests
{
    [Fact]
    public async Task Sweep_IgnoresActiveFreshUnknownAndPreservedPreemptSandboxes()
    {
        var threshold = TimeSpan.FromMinutes(30);
        var provider = new UatSandboxProvider();
        provider.Add(new ManagedSandboxInfo("codeybox-active", OldEnough(threshold), null, IsTrackedActive: true));
        provider.Add(new ManagedSandboxInfo("codeybox-fresh", DateTimeOffset.UtcNow.AddMinutes(-5), null, IsTrackedActive: false));
        provider.Add(new ManagedSandboxInfo("codeybox-unknown", null, null, IsTrackedActive: false));
        provider.Add(new ManagedSandboxInfo(
            "codeybox-preempt",
            DateTimeOffset.UtcNow.AddHours(-2),
            null,
            IsTrackedActive: false,
            HasPreemptMarker: true));
        provider.Add(new ManagedSandboxInfo("codeybox-leaked", OldEnough(threshold), 5 * 1024 * 1024, IsTrackedActive: false));
        var webhooks = new CapturingWebhookDispatcher();
        var reaper = BuildReaper(provider, webhooks, leakAgeThreshold: threshold, preemptRetention: TimeSpan.FromHours(24));

        await reaper.RunSweepAsync(CancellationToken.None);

        var leak = Assert.Single(reaper.GetLatestLeaks());
        Assert.Equal("codeybox-leaked", leak.Name);
        Assert.Equal(5 * 1024 * 1024, leak.DiskBytes);
        var evt = Assert.Single(webhooks.Events, e => e.Event == "sandbox.leak_detected");
        Assert.NotNull(evt.Details);
    }

    [Fact]
    public async Task AutoDispose_DisposesEligibleLeaksAndContinuesAfterFailures()
    {
        var threshold = TimeSpan.FromMinutes(30);
        var provider = new UatSandboxProvider();
        provider.Add(new ManagedSandboxInfo("codeybox-dispose-ok", OldEnough(threshold), null, false));
        provider.Add(new ManagedSandboxInfo("codeybox-dispose-fails", OldEnough(threshold), null, false));
        provider.ThrowOnDispose("codeybox-dispose-fails");
        var webhooks = new CapturingWebhookDispatcher();
        var reaper = BuildReaper(provider, webhooks, autoDispose: true, leakAgeThreshold: threshold);

        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Contains("codeybox-dispose-ok", provider.DisposedNames);
        Assert.DoesNotContain("codeybox-dispose-fails", provider.DisposedNames);
        Assert.Contains(webhooks.Events, e => e.Event == "sandbox.leak_disposed");
        Assert.Contains(webhooks.Events, e => e.Event == "sandbox.leak_dispose_failed");
    }

    [Fact]
    public async Task Sweep_WhenProviderListFails_LeavesLatestLeaksUnchanged()
    {
        var provider = new UatSandboxProvider();
        var reaper = BuildReaper(provider, new CapturingWebhookDispatcher());
        await reaper.RunSweepAsync(CancellationToken.None);
        var initial = reaper.GetLatestLeaks();
        provider.ThrowOnList();

        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Same(initial, reaper.GetLatestLeaks());
    }

    [Fact]
    public async Task LeakedSandboxEndpoints_ListDisposeAndHideDisposedSandbox()
    {
        var threshold = TimeSpan.FromMinutes(30);
        var provider = new UatSandboxProvider();
        provider.Add(new ManagedSandboxInfo("codeybox-endpoint", OldEnough(threshold), 2 * 1024 * 1024, false));
        var webhooks = new CapturingWebhookDispatcher();
        var reaper = BuildReaper(provider, webhooks, leakAgeThreshold: threshold);
        await reaper.RunSweepAsync(CancellationToken.None);
        using var factory = new SandboxProviderApiFactory(
            sandboxProvider: provider,
            reaper: reaper,
            webhooks: webhooks);
        using var client = factory.CreateClient();

        var list = await client.GetAsync("/sandboxes/leaked");
        var dispose = await client.PostAsync("/sandboxes/leaked/codeybox-endpoint/dispose", content: null);
        var repeat = await client.PostAsync("/sandboxes/leaked/codeybox-endpoint/dispose", content: null);

        list.EnsureSuccessStatusCode();
        var body = await list.Content.ReadFromJsonAsync<JsonElement>();
        var leak = Assert.Single(body.EnumerateArray());
        Assert.Equal("codeybox-endpoint", leak.GetProperty("name").GetString());
        Assert.Equal(2, leak.GetProperty("diskMb").GetInt64());
        dispose.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, repeat.StatusCode);
        Assert.Contains("codeybox-endpoint", provider.DisposedNames);
    }

    [Fact]
    public async Task LeakedSandboxEndpoint_RejectsNamesOutsideCodeyboxPrefix()
    {
        using var factory = new SandboxProviderApiFactory(
            sandboxProvider: new UatSandboxProvider(),
            reaper: BuildReaper(new UatSandboxProvider(), new CapturingWebhookDispatcher()));
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/sandboxes/leaked/primary/dispose", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static SandboxLeakReaper BuildReaper(
        UatSandboxProvider provider,
        CapturingWebhookDispatcher webhooks,
        bool autoDispose = false,
        TimeSpan? leakAgeThreshold = null,
        TimeSpan? preemptRetention = null)
    {
        return new SandboxLeakReaper(
            provider,
            webhooks,
            new SandboxLeakOptions
            {
                Enabled = true,
                CheckInterval = TimeSpan.FromHours(1),
                LeakAgeThreshold = leakAgeThreshold ?? TimeSpan.FromMinutes(30),
                PreemptRetention = preemptRetention ?? TimeSpan.FromHours(24),
                AutoDispose = autoDispose,
            },
            NullLogger<SandboxLeakReaper>.Instance);
    }

    private static DateTimeOffset OldEnough(TimeSpan threshold) =>
        DateTimeOffset.UtcNow - threshold - TimeSpan.FromMinutes(1);
}
