using CodeyBox.Agents.Cursor;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Load-bearing regression suite for the workspace-trust constraint on the
/// Cursor runner — stage 3 of the 2026-05-28 cursor failure cascade.
///
/// <para><b>Background.</b> Cursor's CLI refuses to operate on a workspace
/// non-interactively unless invoked with <c>--trust</c> ("Workspace Trust
/// Required", exit 1). The multipass VM boundary is the security perimeter, so
/// per-workspace consent inside the sandbox is noise — the runner must ALWAYS
/// pass <c>--trust</c>. The in-VM smoke probe DOES exercise workspace trust at
/// smoke time: <c>CursorInVmSmokeProbe</c> runs a stage-3 trust-bearing turn via
/// <c>CursorAgentRunner.WorkspaceTrustInvocationPrefix</c> (covered by
/// <c>InVmSmokeProberTests.ThreeStageCascade_EachStageCaughtAtSmokeTime</c>), so
/// a dropped <c>--trust</c> is caught both at smoke time and here. This argv-level
/// suite remains the fast, sandbox-free pin on the exact dispatch argv — it pairs
/// with the smoke coverage rather than being the sole stage-3 guard.</para>
/// </summary>
public sealed class CursorAgentRunnerTrustRegressionTests
{
    [Fact]
    public async Task RunAsync_DefaultArgs_EmitsTrustFlag()
    {
        var sandbox = new RecordingSandbox();
        var runner = new CursorAgentRunner();

        await runner.RunAsync(sandbox, "/work", "prompt", credential: null);

        AssertTrustTokenInAgentArgv(sandbox);
    }

    [Theory]
    [InlineData("composer-2.5", null)]
    [InlineData("composer-3-preview", "high")]
    [InlineData(null, "low")]
    [InlineData(null, "max")]
    [InlineData("some-other-model", "xhigh")]
    public async Task RunAsync_AllParameterCombinations_EmitTrustFlag(string? modelId, string? reasoning)
    {
        var sandbox = new RecordingSandbox();
        var runner = new CursorAgentRunner();

        await runner.RunAsync(sandbox, "/work", "p", credential: null,
            modelId: modelId, reasoningMode: reasoning, captureStructuredStream: false);

        AssertTrustTokenInAgentArgv(sandbox);
    }

    [Fact]
    public async Task RunAsync_WithStructuredStreamRequested_EmitsTrustFlag()
    {
        var sandbox = new RecordingSandbox();
        var runner = new CursorAgentRunner();

        await runner.RunAsync(sandbox, "/work", "p", credential: null, captureStructuredStream: true);

        AssertTrustTokenInAgentArgv(sandbox);
    }

    [Fact]
    public async Task RunResumedAsync_EmitsTrustFlag()
    {
        var sandbox = new RecordingSandbox();
        var runner = new CursorAgentRunner();

        await runner.RunResumedAsync(sandbox, "/work", "p", credential: null,
            new AgentResumeContext("refs/heads/codeybox/preempt/wi"));

        AssertTrustTokenInAgentArgv(sandbox);
    }

    private static void AssertTrustTokenInAgentArgv(RecordingSandbox sandbox)
    {
        var agentExecs = sandbox.Execs
            .Where(e => e.Argv.Count > 0 && e.Argv[0] == CursorAgentRunner.DefaultBinary)
            .ToList();
        Assert.NotEmpty(agentExecs);
        foreach (var exec in agentExecs)
        {
            Assert.True(exec.Argv.Contains(CursorAgentRunner.WorkspaceTrustFlag),
                $"Cursor runner omitted {CursorAgentRunner.WorkspaceTrustFlag} from argv [{string.Join(' ', exec.Argv)}]. " +
                $"The CLI requires --trust to run non-interactively on a workspace " +
                $"(2026-05-28 cascade stage 3). The in-VM smoke probe now also exercises " +
                $"workspace trust (CursorInVmSmokeProbe stage 3, covered by " +
                $"InVmSmokeProberTests.ThreeStageCascade_EachStageCaughtAtSmokeTime); this " +
                $"argv pin is the fast dispatch-path guard that pairs with it. See CursorAgentRunner.cs.");
        }
    }

    private sealed class RecordingSandbox : ISandbox
    {
        public string Id => "recording-cursor-trust";
        public List<SandboxExec> Execs { get; } = [];

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Execs.Add(exec);
            return Task.FromResult(new SandboxExecResult(0, "ok", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
