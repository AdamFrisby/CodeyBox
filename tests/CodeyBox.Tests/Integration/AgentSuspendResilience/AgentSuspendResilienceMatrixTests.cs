using CodeyBox.Core;

namespace CodeyBox.Tests.Integration.AgentSuspendResilience;

/// <summary>
/// R8-resilience: per-agent-CLI × per-suspend-duration matrix against real
/// multipass VMs and live provider credentials.
///
/// <para>Skipped unless <c>CODEYBOX_RUN_AGENT_SUSPEND_SMOKE=1</c> and the
/// agent's credential env vars are set. Run in CI via the
/// <c>agent-suspend-resilience</c> workflow.</para>
/// </summary>
[Collection("Agent suspend resilience")]
[Trait("Category", "AgentSuspendResilience")]
[Trait("requires_multipass", "true")]
public sealed class AgentSuspendResilienceMatrixTests
{
    public static IEnumerable<object[]> Matrix()
    {
        foreach (var agent in AgentSuspendSmokeEnvironment.AllAgents)
        foreach (var seconds in AgentSuspendSmokeEnvironment.SuspendDurationsSeconds)
            yield return [agent.Value, seconds];
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task AgentSurvivesSuspendDuringLlmCall(string agentName, int suspendSeconds)
    {
        var agent = new AgentKind(agentName);
        var skip = AgentSuspendSmokeEnvironment.SkipReason(agent);
        if (skip is not null)
        {
            // xUnit skip via return when Skip.If not available in all versions
            return;
        }

        var outcome = await AgentSuspendSmokeHarness.RunScenarioAsync(agent, suspendSeconds);

        // ≤60s: must complete or surface a recoverable failure (orchestrator retry).
        // Longer windows: same bar — document failures in docs/agent-suspend-resilience.md.
        if (suspendSeconds <= 60)
        {
            Assert.True(
                outcome is AgentSuspendSmokeOutcome.Completed or AgentSuspendSmokeOutcome.RecoverableFailure,
                $"agent={agentName} suspend={suspendSeconds}s expected survival, got {outcome}");
        }
        else
        {
            // Ideal: survive through 300s; record outcome without failing CI on 120/300
            // until the matrix is populated — still assert for visibility in local runs.
            Assert.True(
                outcome is AgentSuspendSmokeOutcome.Completed or AgentSuspendSmokeOutcome.RecoverableFailure,
                $"agent={agentName} suspend={suspendSeconds}s expected survival (ideal ≤300s), got {outcome}");
        }
    }
}
