using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Events;

namespace CodeyBox.Tests;

[Collection("GlobalSerilog")]
public sealed class SandboxLeakReaperAuditTests : IDisposable
{
    private readonly TestSink _sink = new();

    public SandboxLeakReaperAuditTests()
    {
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    public void Dispose() => Log.CloseAndFlush();

    [Fact]
    public async Task RunSweepAsync_AutoDispose_EmitsDisposedAuditWithReason()
    {
        var threshold = TimeSpan.FromMinutes(30);
        var provider = new FakeSandboxProvider();
        provider.AddSandbox(new ManagedSandboxInfo(
            "codeybox-audit-dispose",
            DateTimeOffset.UtcNow - TimeSpan.FromMinutes(91),
            DiskBytes: 512L * 1024 * 1024,
            IsTrackedActive: false));
        var reaper = new SandboxLeakReaper(
            provider,
            new NullWebhookDispatcher(),
            new SandboxLeakOptions
            {
                Enabled = true,
                CheckInterval = TimeSpan.FromHours(1),
                LeakAgeThreshold = threshold,
                AutoDispose = true,
            },
            NullLogger<SandboxLeakReaper>.Instance);

        await reaper.RunSweepAsync(CancellationToken.None);

        var evt = Assert.Single(_sink.Events, e =>
            GetScalar<string>(e, "EventName") == "sandbox.leak_disposed"
            && GetScalar<string>(e, "SandboxName") == "codeybox-audit-dispose");
        Assert.Equal(SandboxLeakReasons.UntrackedSandbox, GetScalar<string>(evt, "Reason"));
        Assert.InRange(GetScalar<double>(evt, "AgeMinutes"), 90.9, 91.2);
    }

    [Fact]
    public async Task RunSweepAsync_AutoDisposeFailure_EmitsFailedAuditWithReason()
    {
        var threshold = TimeSpan.FromMinutes(30);
        var provider = new FakeSandboxProvider();
        provider.AddSandbox(new ManagedSandboxInfo(
            "codeybox-audit-failed",
            DateTimeOffset.UtcNow - TimeSpan.FromMinutes(65),
            DiskBytes: null,
            IsTrackedActive: false));
        provider.SetDisposeThrows("codeybox-audit-failed");
        var reaper = new SandboxLeakReaper(
            provider,
            new NullWebhookDispatcher(),
            new SandboxLeakOptions
            {
                Enabled = true,
                CheckInterval = TimeSpan.FromHours(1),
                LeakAgeThreshold = threshold,
                AutoDispose = true,
            },
            NullLogger<SandboxLeakReaper>.Instance);

        await reaper.RunSweepAsync(CancellationToken.None);

        var evt = Assert.Single(_sink.Events, e =>
            GetScalar<string>(e, "EventName") == "sandbox.leak_dispose_failed"
            && GetScalar<string>(e, "SandboxName") == "codeybox-audit-failed");
        Assert.Equal(SandboxLeakReasons.UntrackedSandbox, GetScalar<string>(evt, "Reason"));
    }

    private static T? GetScalar<T>(LogEvent evt, string key)
    {
        if (!evt.Properties.TryGetValue(key, out var prop) || prop is not ScalarValue sv)
            return default;
        return sv.Value is T t ? t : default;
    }
}
