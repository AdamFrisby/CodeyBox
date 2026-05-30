using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Tests.Integration.AgentSuspendResilience;

public sealed class AgentSuspendSmokeHarnessTests
{
    [Fact]
    public void Classify_Success_ReturnsCompleted()
    {
        var outcome = AgentSuspendSmokeHarness.Classify(
            AgentKind.Claude,
            new AgentResult(true, "ok", "OK", ""));
        Assert.Equal(AgentSuspendSmokeOutcome.Completed, outcome);
    }

    [Fact]
    public void Classify_TransientNetworkStderr_ReturnsRecoverableFailure()
    {
        var outcome = AgentSuspendSmokeHarness.Classify(
            AgentKind.Claude,
            new AgentResult(false, "agent exited 1", "", "ECONNRESET: connection reset"));
        Assert.Equal(AgentSuspendSmokeOutcome.RecoverableFailure, outcome);
    }

    [Theory]
    [InlineData(52)]
    [InlineData(56)]
    [InlineData(92)]
    public void Classify_UnknownWithSuspendExitCode_ReturnsRecoverableFailure(int exitCode)
    {
        var outcome = AgentSuspendSmokeHarness.Classify(
            AgentKind.Claude,
            new AgentResult(false, $"agent exited {exitCode}", "", ""));
        Assert.Equal(AgentSuspendSmokeOutcome.RecoverableFailure, outcome);
    }

    [Fact]
    public void Classify_NonRecoverableFailure_ReturnsFailed()
    {
        var outcome = AgentSuspendSmokeHarness.Classify(
            AgentKind.Claude,
            new AgentResult(false, "agent exited 2", "", "tests failed"));
        Assert.Equal(AgentSuspendSmokeOutcome.Failed, outcome);
    }
}
