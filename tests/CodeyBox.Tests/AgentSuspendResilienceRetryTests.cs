using CodeyBox.Agents;
using CodeyBox.Agents.Claude;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for the R8-resilience single-retry shim in <see cref="CliAgentRunnerBase"/>.
/// </summary>
public sealed class AgentSuspendResilienceRetryTests
{
    [Fact]
    public async Task RunAsync_TransientNetworkError_DoesNotRetryInProcess()
    {
        var calls = 0;
        var sandbox = new RetryRecordingSandbox(() =>
        {
            calls++;
            return calls == 1
                ? new SandboxExecResult(1, "", "ECONNRESET: connection reset by peer")
                : new SandboxExecResult(0, "ok", "");
        });

        var result = await new ClaudeAgentRunner().RunAsync(
            sandbox,
            "/work",
            "hi",
            credential: null);

        Assert.False(result.Success);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RunAsync_TransientNetworkError_DoesNotUseLegacyRetryBudget()
    {
        var calls = 0;
        var sandbox = new RetryRecordingSandbox(() =>
        {
            calls++;
            return new SandboxExecResult(1, "", "ECONNRESET: connection reset by peer");
        });

        var result = await new ClaudeAgentRunner().RunAsync(
            sandbox,
            "/work",
            "hi",
            credential: null);

        Assert.False(result.Success);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RunAsync_NormalFailure_DoesNotRetry()
    {
        var calls = 0;
        var sandbox = new RetryRecordingSandbox(() =>
        {
            calls++;
            return new SandboxExecResult(1, "", "tests failed");
        });

        var result = await new ClaudeAgentRunner().RunAsync(
            sandbox,
            "/work",
            "hi",
            credential: null);

        Assert.False(result.Success);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("gemini")]
    [InlineData("cursor")]
    [InlineData("opencode")]
    [InlineData("copilot")]
    public void ShouldRetry_TransientNetwork_ReturnsFalse_ForAllAgents(string agent)
    {
        var classification = new AgentFailureClassification(AgentFailureKind.TransientNetwork);
        Assert.False(AgentSuspendResilience.ShouldRetry(new AgentKind(agent), classification, exitCode: 1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(52)]
    [InlineData(56)]
    [InlineData(92)]
    [InlineData(137)]
    public void ShouldRetry_UnknownWithSuspendExitCodes_ReturnsTrueForSupportedAgents(int exitCode)
    {
        var classification = new AgentFailureClassification(AgentFailureKind.Unknown);
        Assert.True(AgentSuspendResilience.ShouldRetry(AgentKind.Claude, classification, exitCode));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(52, true)]
    [InlineData(56, true)]
    [InlineData(92, true)]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(51, false)]
    [InlineData(57, false)]
    [InlineData(91, false)]
    [InlineData(93, false)]
    [InlineData(137, true)]
    public void IsSuspendRelatedExitCode_ReturnsExpectedBoundaryValues(int exitCode, bool expected)
    {
        Assert.Equal(expected, AgentSuspendResilience.IsSuspendRelatedExitCode(exitCode));
    }

    [Fact]
    public void ShouldRetry_UnknownWithUnrelatedExitCode_ReturnsFalse()
    {
        var classification = new AgentFailureClassification(AgentFailureKind.Unknown);
        Assert.False(AgentSuspendResilience.ShouldRetry(AgentKind.Claude, classification, exitCode: 2));
    }

    [Fact]
    public async Task RunAsync_UnknownExitCodeOnly_RetriesOnceAndSucceeds()
    {
        var calls = 0;
        var sandbox = new RetryRecordingSandbox(() =>
        {
            calls++;
            return calls == 1
                ? new SandboxExecResult(52, "", "")
                : new SandboxExecResult(0, "ok", "");
        });

        var result = await new ClaudeAgentRunner().RunAsync(
            sandbox,
            "/work",
            "hi",
            credential: null);

        Assert.True(result.Success);
        Assert.Equal(2, calls);
    }

    private sealed class RetryRecordingSandbox(Func<SandboxExecResult> onExec) : ISandbox
    {
        public string Id => "codeybox-retry-test";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (exec.Argv.Count > 0
                && exec.Argv[0] == "claude"
                && exec.Argv.Contains("--help"))
            {
                return Task.FromResult(new SandboxExecResult(0, "--output-format stream-json --verbose", string.Empty));
            }

            return Task.FromResult(onExec());
        }

        public Task<byte[]> GetScreenshotAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task SynthesizeInputAsync(IReadOnlyList<SandboxInputEvent> events, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
