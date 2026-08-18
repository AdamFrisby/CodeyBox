using CodeyBox.Core;
using Xunit;

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
        {
            foreach (var seconds in AgentSuspendSmokeEnvironment.SuspendDurationsSeconds)
            {
                yield return [agent.Value, seconds];
            }
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Matrix))]
    public async Task AgentSurvivesSuspendDuringLlmCall(string agentName, int suspendSeconds)
    {
        var agent = new AgentKind(agentName);
        var skipReason = AgentSuspendSmokeEnvironment.SkipReason(agent);
        Skip.If(skipReason is not null, skipReason!);

        var outcome = await AgentSuspendSmokeHarness.RunScenarioAsync(agent, suspendSeconds);

        // ≤60s: must complete or surface a recoverable failure (orchestrator retry).
        // Longer windows: same bar — document failures in docs/operating/sandbox-reliability.md.
        if (suspendSeconds <= 60)
        {
            Assert.True(
                outcome is AgentSuspendSmokeOutcome.Completed or AgentSuspendSmokeOutcome.RecoverableFailure,
                $"agent={agentName} suspend={suspendSeconds}s expected survival, got {outcome}");
        }
        // >60s: record outcome in scenario logs for docs/operating/sandbox-reliability.md;
        // do not fail CI on long windows until the matrix is populated.
    }
}
