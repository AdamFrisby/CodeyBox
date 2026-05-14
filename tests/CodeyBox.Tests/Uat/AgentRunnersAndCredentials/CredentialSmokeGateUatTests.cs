using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests.Uat.AgentRunnersAndCredentials;

/// <summary>
/// UAT coverage for <c>Credential smoke gate - Validates agent credentials before pickup</c>.
/// Plan anchor: docs/uat/00-plan.md#credential-smoke-gate---validates-agent-credentials-before-pickup
/// </summary>
public sealed class CredentialSmokeGateUatTests
{
    [Fact]
    public async Task GateCachesFailureForSameCredentialFingerprint()
    {
        var credential = Credential(AgentKind.Claude);
        var probe = new CountingSmokeProbe(AgentKind.Claude);
        probe.Enqueue(new AgentSmokeResult(false, "auth", TimeSpan.Zero));
        probe.Enqueue(new AgentSmokeResult(true, null, TimeSpan.Zero));
        var gate = BuildGate(credential, [probe], new AgentSmokeCache(TimeSpan.FromMinutes(15)));

        var first = await gate.CheckAsync(AgentKind.Claude, CancellationToken.None);
        var second = await gate.CheckAsync(AgentKind.Claude, CancellationToken.None);

        Assert.False(first!.Ok);
        Assert.False(second!.Ok);
        Assert.Equal("auth", second.FailureReason);
        Assert.Equal(1, probe.CallCount);
    }

    [Fact]
    public async Task ExpiredCacheEntry_RerunsProbeWithoutWallClockWait()
    {
        var credential = Credential(AgentKind.Codex);
        var probe = new CountingSmokeProbe(AgentKind.Codex);
        probe.Enqueue(new AgentSmokeResult(false, "expired", TimeSpan.Zero));
        probe.Enqueue(new AgentSmokeResult(true, null, TimeSpan.Zero));
        var gate = BuildGate(credential, [probe], new AgentSmokeCache(TimeSpan.FromTicks(-1)));

        var first = await gate.CheckAsync(AgentKind.Codex, CancellationToken.None);
        var second = await gate.CheckAsync(AgentKind.Codex, CancellationToken.None);

        Assert.False(first!.Ok);
        Assert.True(second!.Ok);
        Assert.Equal(2, probe.CallCount);
    }

    [Fact]
    public async Task MissingProbeOrMissingCredential_DegradesWithoutBlocking()
    {
        var missingProbeGate = BuildGate(Credential(AgentKind.Gemini), [], new AgentSmokeCache(TimeSpan.FromMinutes(15)));
        var noCredentialGate = BuildGate(
            credential: null,
            [new CountingSmokeProbe(AgentKind.Gemini)],
            new AgentSmokeCache(TimeSpan.FromMinutes(15)));

        Assert.Null(await missingProbeGate.CheckAsync(AgentKind.Gemini, CancellationToken.None));
        Assert.Null(await noCredentialGate.CheckAsync(AgentKind.Gemini, CancellationToken.None));
    }

    [Fact]
    public async Task StartupSmokeProbeService_EmitsFailureWebhookButDoesNotFailStartup()
    {
        var probe = new CountingSmokeProbe(AgentKind.Claude);
        probe.Enqueue(new AgentSmokeResult(false, "auth", TimeSpan.Zero));
        var webhooks = new CapturingWebhookDispatcher();
        var service = new StartupSmokeProbeService(
            new ConstantCredentialProvider(Credential(AgentKind.Claude)),
            [probe],
            webhooks,
            new SmokeOptions { Enabled = true, StartupTimeoutSeconds = 5 },
            NullLogger<StartupSmokeProbeService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.StartupTask;

        var evt = Assert.Single(webhooks.Events, e => e.Event == "agent.smoke_failed");
        var details = Assert.IsType<AgentSmokeFailedDetails>(evt.Details);
        Assert.Equal("claude", details.AgentKind);
        Assert.Equal("auth", details.Reason);
    }

    private static CredentialSmokeGate BuildGate(
        AgentCredential? credential,
        IEnumerable<IAgentSmokeProbe> probes,
        IAgentSmokeCache cache)
        => new(
            new OptionalCredentialProvider(credential),
            probes,
            cache,
            new SmokeOptions { Enabled = true },
            NullLogger<CredentialSmokeGate>.Instance);

    private static AgentCredential Credential(AgentKind kind)
        => new(kind, new Dictionary<string, string> { ["UAT_CREDENTIAL"] = kind.Value }, new Dictionary<string, string>());

    private sealed class OptionalCredentialProvider(AgentCredential? credential) : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
        {
            _ = agent;
            _ = ct;
            return Task.FromResult(credential);
        }
    }
}
