using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="StartupSmokeProbeService"/>. Uses fake probes and an
/// in-memory webhook dispatcher to verify event emission and non-fatal startup.
/// </summary>
public sealed class StartupSmokeProbeTests
{
    private static readonly AgentCredential AnyClaudeCred = new(
        AgentKind.Claude,
        new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "key" },
        new Dictionary<string, string>());

    private static StartupSmokeProbeService Build(
        IEnumerable<IAgentSmokeProbe> probes,
        CapturingWebhookDispatcher webhooks,
        ICredentialProvider? credentials = null,
        bool enabled = true,
        int timeoutSeconds = 5,
        AgentAvailabilityRegistry? availability = null,
        ILogger<StartupSmokeProbeService>? logger = null,
        InVmSmokeOptions? inVmSmokeOptions = null)
    {
        return new StartupSmokeProbeService(
            credentials ?? new ConstantCredentialProvider(AnyClaudeCred),
            probes,
            webhooks,
            new SmokeOptions { Enabled = enabled, StartupTimeoutSeconds = timeoutSeconds },
            logger ?? NullLogger<StartupSmokeProbeService>.Instance,
            availability,
            inVmSmokeOptions);
    }

    // ── Failure events ────────────────────────────────────────────────────────

    [Fact]
    public async Task FailingProbe_EmitsSmokeFailedWebhookEvent()
    {
        var webhooks = new CapturingWebhookDispatcher();
        var svc = Build([new FakeSmokeProbe(AgentKind.Claude, shouldPass: false)], webhooks);

        await svc.StartAsync(CancellationToken.None);
        await svc.StartupTask;

        Assert.Contains(webhooks.Events, e => e.Event == "agent.smoke_failed");
    }

    [Fact]
    public async Task TwoFailingProbes_EmitsTwoWebhookEvents()
    {
        var webhooks = new CapturingWebhookDispatcher();
        var probes = new IAgentSmokeProbe[]
        {
            new FakeSmokeProbe(AgentKind.Claude, shouldPass: false),
            new FakeSmokeProbe(AgentKind.Codex, shouldPass: false),
        };
        var cred = new AgentCredential(AgentKind.Claude,
            new Dictionary<string, string> { ["X"] = "v" }, new Dictionary<string, string>());
        var svc = Build(probes, webhooks, credentials: new ConstantCredentialProvider(cred));

        await svc.StartAsync(CancellationToken.None);
        await svc.StartupTask;

        Assert.Equal(2, webhooks.Events.Count(e => e.Event == "agent.smoke_failed"));
    }

    [Fact]
    public async Task FailureWebhookEvent_HasCorrectShape()
    {
        var webhooks = new CapturingWebhookDispatcher();
        var svc = Build([new FakeSmokeProbe(AgentKind.Claude, shouldPass: false)], webhooks);

        await svc.StartAsync(CancellationToken.None);
        await svc.StartupTask;

        var evt = webhooks.Events.Single(e => e.Event == "agent.smoke_failed");
        Assert.Null(evt.WorkItem);
        Assert.Null(evt.Project);
        var details = Assert.IsType<AgentSmokeFailedDetails>(evt.Details);
        Assert.Equal("claude", details.AgentKind);
        Assert.Equal("auth", details.Reason);
    }

    // ── Success events ────────────────────────────────────────────────────────

    [Fact]
    public async Task PassingProbe_EmitsNoFailureWebhook()
    {
        var webhooks = new CapturingWebhookDispatcher();
        var svc = Build([new FakeSmokeProbe(AgentKind.Claude, shouldPass: true)], webhooks);

        await svc.StartAsync(CancellationToken.None);
        await svc.StartupTask;

        Assert.DoesNotContain(webhooks.Events, e => e.Event == "agent.smoke_failed");
    }

    // ── StartAsync does not block ─────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_ReturnsImmediately_BeforeProbesFinish()
    {
        var webhooks = new CapturingWebhookDispatcher();
        var slowProbe = new FakeSmokeProbe(AgentKind.Claude, shouldPass: true);
        var svc = Build([slowProbe], webhooks);

        // StartAsync must complete synchronously (fire-and-forget).
        var startTask = svc.StartAsync(CancellationToken.None);
        Assert.True(startTask.IsCompleted);

        await svc.StartupTask; // clean up
    }

    // ── Disabled ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SmokeDisabled_NeverCallsProbes()
    {
        var webhooks = new CapturingWebhookDispatcher();
        var probe = new FakeSmokeProbe(AgentKind.Claude, shouldPass: false);
        var svc = Build([probe], webhooks, enabled: false);

        await svc.StartAsync(CancellationToken.None);
        // StartupTask remains Task.CompletedTask when disabled.

        Assert.Equal(0, probe.CallCount);
        Assert.Empty(webhooks.Events);
    }

    [Fact]
    public async Task ProbeQueuedWhileEnabled_NoOps_WhenSmokeDisabledBeforeProbeRuns()
    {
        var webhooks = new CapturingWebhookDispatcher();
        var probe = new FakeSmokeProbe(AgentKind.Claude, shouldPass: false);
        var snapshot = new SmokeOptionsSnapshot(new SmokeOptions
        {
            Enabled = true,
            StartupTimeoutSeconds = 5,
        });
        var svc = new StartupSmokeProbeService(
            new ConstantCredentialProvider(AnyClaudeCred),
            [probe],
            webhooks,
            snapshot,
            NullLogger<StartupSmokeProbeService>.Instance);

        snapshot.Replace(new SmokeOptions { Enabled = false, StartupTimeoutSeconds = 5 });
        await InvokeProbeOneAsync(svc, probe);

        Assert.Equal(0, probe.CallCount);
        Assert.Empty(webhooks.Events);
    }

    [Fact]
    public async Task StartAsync_LogsEffectivePosture_AndPartialInVmDisableWarning()
    {
        var webhooks = new CapturingWebhookDispatcher();
        var log = new CapturingLogger<StartupSmokeProbeService>();
        var svc = Build(
            [new FakeSmokeProbe(AgentKind.Claude, shouldPass: true)],
            webhooks,
            logger: log,
            inVmSmokeOptions: new InVmSmokeOptions { Enabled = false });

        await svc.StartAsync(CancellationToken.None);
        await svc.StartupTask;

        Assert.Contains(log.Entries, e =>
            e.Level == LogLevel.Information
            && e.Message.Contains("Smoke posture:", StringComparison.Ordinal)
            && e.Properties.TryGetValue("SmokeEnabled", out var smokeEnabled)
            && smokeEnabled is true
            && e.Properties.TryGetValue("InVmSmokeGate", out var inVmGate)
            && inVmGate is false);
        Assert.Contains(log.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("CodeyBox:Smoke:InVm:Enabled=false disables only in-VM smoke", StringComparison.Ordinal));
    }

    // ── No credential ─────────────────────────────────────────────────────────

    [Fact]
    public async Task NoCredential_ProbeNotCalled_NoWebhookEmitted()
    {
        var webhooks = new CapturingWebhookDispatcher();
        var probe = new FakeSmokeProbe(AgentKind.Claude, shouldPass: false);
        var svc = Build([probe], webhooks, credentials: new StaticCredentialProvider());

        await svc.StartAsync(CancellationToken.None);
        await svc.StartupTask;

        Assert.Equal(0, probe.CallCount);
        Assert.Empty(webhooks.Events);
    }

    // ── Availability registry integration ─────────────────────────────────────

    [Fact]
    public async Task FailingProbe_MarksAgentExcludedInRegistry()
    {
        var webhooks = new CapturingWebhookDispatcher();
        var registry = new AgentAvailabilityRegistry(
            new AvailabilityOptions(), TimeProvider.System, NullLogger<AgentAvailabilityRegistry>.Instance);
        var svc = Build(
            [new FakeSmokeProbe(AgentKind.Claude, shouldPass: false)],
            webhooks,
            availability: registry);

        await svc.StartAsync(CancellationToken.None);
        await svc.StartupTask;

        var av = registry.GetAvailability(AgentKind.Claude);
        Assert.False(av.Available);
        Assert.Contains("auth", av.Reason);
    }

    [Fact]
    public async Task PassingProbe_LeavesAgentAvailableInRegistry()
    {
        var webhooks = new CapturingWebhookDispatcher();
        var registry = new AgentAvailabilityRegistry(
            new AvailabilityOptions(), TimeProvider.System, NullLogger<AgentAvailabilityRegistry>.Instance);
        var svc = Build(
            [new FakeSmokeProbe(AgentKind.Claude, shouldPass: true)],
            webhooks,
            availability: registry);

        await svc.StartAsync(CancellationToken.None);
        await svc.StartupTask;

        Assert.True(registry.GetAvailability(AgentKind.Claude).Available);
    }

    private static Task InvokeProbeOneAsync(StartupSmokeProbeService service, IAgentSmokeProbe probe)
    {
        var method = typeof(StartupSmokeProbeService).GetMethod(
            "ProbeOneAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return (Task)method.Invoke(service, [probe, CancellationToken.None])!;
    }
}
