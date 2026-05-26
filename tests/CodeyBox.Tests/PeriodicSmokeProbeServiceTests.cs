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
        TimeSpan? interval = null)
    {
        var cred = new AgentCredential(
            AgentKind.Claude,
            new Dictionary<string, string> { ["k"] = "v" },
            new Dictionary<string, string>());
        return new PeriodicSmokeProbeService(
            new ConstantCredentialProvider(cred),
            probes,
            webhooks,
            new SmokeOptions { Enabled = true, StartupTimeoutSeconds = 5 },
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
}
