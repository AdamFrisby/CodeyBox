using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="PeriodicSmokeProbeService"/>. Verifies that the
/// background sweep feeds the registry, and that per-agent probes invoked via
/// <see cref="PeriodicSmokeProbeService.ProbeAsync"/> are exposed for the
/// <c>/admin/agent/{name}/smoke</c> endpoint.
/// </summary>
public sealed class PeriodicSmokeProbeServiceTests
{
    private static PeriodicSmokeProbeService Build(
        IEnumerable<IAgentSmokeProbe> probes,
        AgentAvailabilityRegistry registry,
        CapturingWebhookDispatcher webhooks,
        TimeSpan? interval = null,
        SmokeOptionsSnapshot? smokeOptions = null)
    {
        var cred = new AgentCredential(
            AgentKind.Claude,
            new Dictionary<string, string> { ["k"] = "v" },
            new Dictionary<string, string>());
        return new PeriodicSmokeProbeService(
            new ConstantCredentialProvider(cred),
            probes,
            webhooks,
            smokeOptions ?? new SmokeOptionsSnapshot(new SmokeOptions { Enabled = true, StartupTimeoutSeconds = 5 }),
            new AvailabilityOptions { PeriodicSweepInterval = interval ?? TimeSpan.FromSeconds(30) },
            registry,
            NullLogger<PeriodicSmokeProbeService>.Instance);
    }

    [Fact]
    public async Task ProbeAsync_PassingProbe_RecoversExcludedAgent()
    {
        var registry = new AgentAvailabilityRegistry(
            new AvailabilityOptions(), TimeProvider.System, NullLogger<AgentAvailabilityRegistry>.Instance);
        registry.MarkSmokeResult(AgentKind.Claude, new AgentSmokeResult(false, "auth", TimeSpan.Zero));
        Assert.False(registry.GetAvailability(AgentKind.Claude).Available);

        var probe = new FakeSmokeProbe(AgentKind.Claude, shouldPass: true);
        var webhooks = new CapturingWebhookDispatcher();
        var svc = Build([probe], registry, webhooks);

        var result = await svc.ProbeAsync(AgentKind.Claude, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.Ok);
        Assert.True(registry.GetAvailability(AgentKind.Claude).Available);
    }

    [Fact]
    public async Task ProbeAsync_RecoverTransition_EmitsRecoveredWebhook()
    {
        var registry = new AgentAvailabilityRegistry(
            new AvailabilityOptions(), TimeProvider.System, NullLogger<AgentAvailabilityRegistry>.Instance);
        registry.MarkSmokeResult(AgentKind.Claude, new AgentSmokeResult(false, "auth", TimeSpan.Zero));

        var webhooks = new CapturingWebhookDispatcher();
        var svc = Build([new FakeSmokeProbe(AgentKind.Claude, shouldPass: true)], registry, webhooks);

        await svc.ProbeAsync(AgentKind.Claude, CancellationToken.None);

        Assert.Contains(webhooks.Events, e => e.Event == "agent.smoke_recovered");
    }

    [Fact]
    public async Task ProbeAsync_FailureTransition_EmitsFailedWebhook()
    {
        var registry = new AgentAvailabilityRegistry(
            new AvailabilityOptions(), TimeProvider.System, NullLogger<AgentAvailabilityRegistry>.Instance);

        var webhooks = new CapturingWebhookDispatcher();
        var svc = Build([new FakeSmokeProbe(AgentKind.Claude, shouldPass: false)], registry, webhooks);

        await svc.ProbeAsync(AgentKind.Claude, CancellationToken.None);

        Assert.Contains(webhooks.Events, e => e.Event == "agent.smoke_failed");
        Assert.False(registry.GetAvailability(AgentKind.Claude).Available);
    }

    [Fact]
    public async Task ProbeAsync_SteadyStateFailure_DoesNotReEmitWebhook()
    {
        var registry = new AgentAvailabilityRegistry(
            new AvailabilityOptions(), TimeProvider.System, NullLogger<AgentAvailabilityRegistry>.Instance);
        registry.MarkSmokeResult(AgentKind.Claude, new AgentSmokeResult(false, "auth", TimeSpan.Zero));

        var webhooks = new CapturingWebhookDispatcher();
        var svc = Build([new FakeSmokeProbe(AgentKind.Claude, shouldPass: false)], registry, webhooks);

        await svc.ProbeAsync(AgentKind.Claude, CancellationToken.None);

        // Already excluded coming in — no transition, no webhook fan-out.
        Assert.DoesNotContain(webhooks.Events, e => e.Event == "agent.smoke_failed");
        Assert.DoesNotContain(webhooks.Events, e => e.Event == "agent.smoke_recovered");
    }

    [Fact]
    public async Task ProbeAsync_UnknownAgent_ReturnsNull()
    {
        var registry = new AgentAvailabilityRegistry(
            new AvailabilityOptions(), TimeProvider.System, NullLogger<AgentAvailabilityRegistry>.Instance);
        var svc = Build([new FakeSmokeProbe(AgentKind.Claude, shouldPass: true)], registry, new CapturingWebhookDispatcher());

        var result = await svc.ProbeAsync(AgentKind.Codex, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task SmokeDisabled_SweepOnceAndProbeAsync_DoNotProbeOrMutateAvailability()
    {
        var registry = new AgentAvailabilityRegistry(
            new AvailabilityOptions(), TimeProvider.System, NullLogger<AgentAvailabilityRegistry>.Instance);
        var webhooks = new CapturingWebhookDispatcher();
        var probe = new FakeSmokeProbe(AgentKind.Claude, shouldPass: false);
        var smokeOptions = new SmokeOptionsSnapshot(new SmokeOptions { Enabled = false, StartupTimeoutSeconds = 5 });
        var svc = Build([probe], registry, webhooks, smokeOptions: smokeOptions);

        await svc.SweepOnceAsync(CancellationToken.None);
        var result = await svc.ProbeAsync(AgentKind.Claude, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, probe.CallCount);
        Assert.True(registry.GetAvailability(AgentKind.Claude).Available);
        Assert.Empty(webhooks.Events);
    }

    [Fact]
    public async Task BackgroundSweep_DisabledThenEnabled_ResumesAfterHotReload()
    {
        var registry = new AgentAvailabilityRegistry(
            new AvailabilityOptions(), TimeProvider.System, NullLogger<AgentAvailabilityRegistry>.Instance);
        var webhooks = new CapturingWebhookDispatcher();
        var probe = new FakeSmokeProbe(AgentKind.Claude, shouldPass: false);
        var smokeOptions = new SmokeOptionsSnapshot(new SmokeOptions { Enabled = false, StartupTimeoutSeconds = 5 });
        var svc = Build(
            [probe],
            registry,
            webhooks,
            interval: TimeSpan.FromMilliseconds(20),
            smokeOptions: smokeOptions);

        await svc.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(80);
            Assert.Equal(0, probe.CallCount);
            Assert.True(registry.GetAvailability(AgentKind.Claude).Available);

            smokeOptions.Replace(new SmokeOptions { Enabled = true, StartupTimeoutSeconds = 5 });

            await WaitUntilAsync(() => probe.CallCount > 0, TimeSpan.FromSeconds(2));
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }

        Assert.False(registry.GetAvailability(AgentKind.Claude).Available);
        Assert.Contains(webhooks.Events, e => e.Event == "agent.smoke_failed");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
                return;
            await Task.Delay(10);
        }

        Assert.True(predicate(), "Timed out waiting for condition.");
    }
}
